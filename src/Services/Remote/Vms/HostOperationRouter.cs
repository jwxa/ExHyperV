using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Tools;

namespace ExHyperV.Services.Remote.Vms;

public interface IHostOperationRouter
{
    Task<HostVmReadResult<T>> ReadAsync<T>(
        HostId hostId,
        Func<WmiContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    Task<HostVmReadResult<T>> ReadAsync<T>(
        HostId hostId,
        Func<WmiContext, CancellationToken, Task<T>> operation,
        HostOperationStamp expectedStamp,
        CancellationToken cancellationToken = default);

    Task<HostVmWriteResult> WriteAsync(
        HostId hostId,
        Func<WmiContext, CancellationToken, Task<HostVmBackendWriteResult>> operation,
        CancellationToken cancellationToken = default);

    Task<HostVmWriteResult> WriteAsync(
        HostId hostId,
        Func<WmiContext, CancellationToken, Task<HostVmBackendWriteResult>> operation,
        HostOperationStamp expectedStamp,
        CancellationToken cancellationToken = default);
}

public sealed class HostOperationRouter(
    IHostOperationSessionSource sessions,
    IHostWmiContextResolver contextResolver) : IHostOperationRouter
{
    private readonly IHostOperationSessionSource _sessions =
        sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IHostWmiContextResolver _contextResolver =
        contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));

    public Task<HostVmReadResult<T>> ReadAsync<T>(
        HostId hostId,
        Func<WmiContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) =>
        ReadCoreAsync(hostId, operation, expectedStamp: null, cancellationToken);

    public Task<HostVmReadResult<T>> ReadAsync<T>(
        HostId hostId,
        Func<WmiContext, CancellationToken, Task<T>> operation,
        HostOperationStamp expectedStamp,
        CancellationToken cancellationToken = default) =>
        ReadCoreAsync(
            hostId,
            operation,
            expectedStamp ?? throw new ArgumentNullException(nameof(expectedStamp)),
            cancellationToken);

    private async Task<HostVmReadResult<T>> ReadCoreAsync<T>(
        HostId hostId,
        Func<WmiContext, CancellationToken, Task<T>> operation,
        HostOperationStamp? expectedStamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (expectedStamp is not null && !CanUseExpectedStamp(hostId, expectedStamp))
            return new HostVmReadResult<T>(HostVmOperationStatus.Stale, default, "宿主会话已改变，未执行旧会话读取。", null);
        if (!_sessions.TryCaptureManagementOperation(hostId, out HostManagementOperationContext? context, out string reason))
            return new HostVmReadResult<T>(HostVmOperationStatus.Failed, default, reason, null);
        if (expectedStamp is not null && context!.Stamp != expectedStamp)
            return new HostVmReadResult<T>(HostVmOperationStatus.Stale, default, "宿主会话已改变，未执行旧会话读取。", context);

        try
        {
            WmiContext wmiContext = _contextResolver.Resolve(context!);
            T value = await operation(wmiContext, cancellationToken);
            return _sessions.CanApply(context!.Stamp)
                ? new HostVmReadResult<T>(HostVmOperationStatus.Succeeded, value, string.Empty, context)
                : new HostVmReadResult<T>(HostVmOperationStatus.Stale, default, "宿主会话已改变，已丢弃旧会话返回的数据。", context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppLog.Warning(
                "虚拟机操作",
                "虚拟机读取已取消。",
                CreateLogContext(hostId, context!, "读取", "Cancelled"));
            return new HostVmReadResult<T>(HostVmOperationStatus.Cancelled, default, "操作已取消。", context);
        }
        catch (Exception ex)
        {
            string message = SensitiveDataRedactor.Redact(ex.Message);
            if (!_sessions.CanApply(context!.Stamp))
                return new HostVmReadResult<T>(HostVmOperationStatus.Stale, default, "宿主会话已改变，已丢弃旧会话返回的错误。", context);
            bool connectionLost = !context.Target.IsLocal
                && HostConnectionFailureClassifier.IsConnectionLoss(ex);
            if (connectionLost)
                _sessions.ReportConnectionLoss(context.Stamp, message);
            AppLog.Error(
                "虚拟机操作",
                $"虚拟机读取失败：{message}",
                CreateLogContext(hostId, context, "读取", connectionLost ? "ConnectionLost" : "ReadFailed"),
                ex);
            return new HostVmReadResult<T>(HostVmOperationStatus.Failed, default, message, context);
        }
    }

    public Task<HostVmWriteResult> WriteAsync(
        HostId hostId,
        Func<WmiContext, CancellationToken, Task<HostVmBackendWriteResult>> operation,
        CancellationToken cancellationToken = default) =>
        WriteCoreAsync(hostId, operation, expectedStamp: null, cancellationToken);

    public Task<HostVmWriteResult> WriteAsync(
        HostId hostId,
        Func<WmiContext, CancellationToken, Task<HostVmBackendWriteResult>> operation,
        HostOperationStamp expectedStamp,
        CancellationToken cancellationToken = default) =>
        WriteCoreAsync(
            hostId,
            operation,
            expectedStamp ?? throw new ArgumentNullException(nameof(expectedStamp)),
            cancellationToken);

    private async Task<HostVmWriteResult> WriteCoreAsync(
        HostId hostId,
        Func<WmiContext, CancellationToken, Task<HostVmBackendWriteResult>> operation,
        HostOperationStamp? expectedStamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (expectedStamp is not null && !CanUseExpectedStamp(hostId, expectedStamp))
            return new HostVmWriteResult(HostVmOperationStatus.Stale, "宿主会话已改变，未执行旧会话写操作。", null);
        if (!_sessions.TryBeginWrite(hostId, out IHostWriteLease? lease, out string reason))
            return new HostVmWriteResult(HostVmOperationStatus.WriteBlocked, reason, null);

        using (lease)
        {
            if (!_sessions.TryCaptureManagementOperation(hostId, out HostManagementOperationContext? context, out reason))
                return new HostVmWriteResult(HostVmOperationStatus.Failed, reason, null);
            if (context!.Stamp != lease!.Stamp)
                return new HostVmWriteResult(HostVmOperationStatus.Stale, "宿主会话已改变，未执行写操作。", context);
            if (expectedStamp is not null && context.Stamp != expectedStamp)
                return new HostVmWriteResult(HostVmOperationStatus.Stale, "宿主会话已改变，未执行旧会话写操作。", context);

            try
            {
                AppLog.Information(
                    "虚拟机操作",
                    "开始执行虚拟机写操作。",
                    CreateLogContext(hostId, context, "写入"));
                WmiContext wmiContext = _contextResolver.Resolve(context);
                HostVmBackendWriteResult result = await operation(wmiContext, cancellationToken);
                if (!_sessions.CanApply(lease.Stamp))
                    return new HostVmWriteResult(HostVmOperationStatus.Stale, "宿主会话已改变，已忽略旧会话操作结果。", context);

                bool connectionLost = !result.Succeeded
                    && !context.Target.IsLocal
                    && result.FailureException is not null
                    && HostConnectionFailureClassifier.IsConnectionLoss(result.FailureException);
                if (connectionLost)
                    _sessions.ReportConnectionLoss(context.Stamp, SensitiveDataRedactor.Redact(result.Message));

                string resultMessage = string.IsNullOrWhiteSpace(result.Message)
                    ? result.Succeeded ? "虚拟机写操作已完成。" : "虚拟机写操作失败。"
                    : result.Message;
                if (result.Succeeded)
                {
                    AppLog.Information(
                        "虚拟机操作",
                        resultMessage,
                        CreateLogContext(hostId, context, "写入"));
                }
                else
                {
                    AppLog.Warning(
                        "虚拟机操作",
                        resultMessage,
                        CreateLogContext(
                            hostId,
                            context,
                            "写入",
                            connectionLost ? "ConnectionLost" : "WriteFailed"),
                        result.FailureException);
                }

                return result.Succeeded
                    ? new HostVmWriteResult(HostVmOperationStatus.Succeeded, result.Message, context)
                    : new HostVmWriteResult(
                        HostVmOperationStatus.Failed,
                        SensitiveDataRedactor.Redact(result.Message),
                        context);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AppLog.Warning(
                    "虚拟机操作",
                    "虚拟机写操作已取消。",
                    CreateLogContext(hostId, context, "写入", "Cancelled"));
                return new HostVmWriteResult(HostVmOperationStatus.Cancelled, "操作已取消。", context);
            }
            catch (Exception ex)
            {
                string message = SensitiveDataRedactor.Redact(ex.Message);
                bool connectionLost = !context.Target.IsLocal
                    && HostConnectionFailureClassifier.IsConnectionLoss(ex);
                if (connectionLost)
                    _sessions.ReportConnectionLoss(context.Stamp, message);
                AppLog.Error(
                    "虚拟机操作",
                    $"虚拟机写操作失败：{message}",
                    CreateLogContext(hostId, context, "写入", connectionLost ? "ConnectionLost" : "WriteFailed"),
                    ex);
                return new HostVmWriteResult(HostVmOperationStatus.Failed, message, context);
            }
        }
    }

    private static AppLogContext CreateLogContext(
        HostId hostId,
        HostManagementOperationContext context,
        string operation,
        string errorCategory = "None") => new(
            Host: context.Target.IsLocal ? Environment.MachineName : context.Target.Address,
            SessionGeneration: context.Stamp.Generation,
            Properties: new Dictionary<string, object?> { ["操作类型"] = operation },
            HostId: hostId,
            ErrorCategory: errorCategory);

    private bool CanUseExpectedStamp(HostId hostId, HostOperationStamp stamp) =>
        (hostId.IsLocal ? stamp.ProfileId is null : stamp.ProfileId == hostId.ProfileId)
        && _sessions.CanApply(stamp);
}
