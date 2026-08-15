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
            return new HostVmReadResult<T>(HostVmOperationStatus.Cancelled, default, "操作已取消。", context);
        }
        catch (Exception ex)
        {
            string message = SensitiveDataRedactor.Redact(ex.Message);
            if (!_sessions.CanApply(context!.Stamp))
                return new HostVmReadResult<T>(HostVmOperationStatus.Stale, default, "宿主会话已改变，已丢弃旧会话返回的错误。", context);
            if (!context.Target.IsLocal && HostConnectionFailureClassifier.IsConnectionLoss(ex))
                _sessions.ReportConnectionLoss(context.Stamp, message);
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
                WmiContext wmiContext = _contextResolver.Resolve(context);
                HostVmBackendWriteResult result = await operation(wmiContext, cancellationToken);
                if (!_sessions.CanApply(lease.Stamp))
                    return new HostVmWriteResult(HostVmOperationStatus.Stale, "宿主会话已改变，已忽略旧会话操作结果。", context);

                if (!result.Succeeded
                    && !context.Target.IsLocal
                    && result.FailureException is not null
                    && HostConnectionFailureClassifier.IsConnectionLoss(result.FailureException))
                    _sessions.ReportConnectionLoss(context.Stamp, SensitiveDataRedactor.Redact(result.Message));

                return result.Succeeded
                    ? new HostVmWriteResult(HostVmOperationStatus.Succeeded, result.Message, context)
                    : new HostVmWriteResult(
                        HostVmOperationStatus.Failed,
                        SensitiveDataRedactor.Redact(result.Message),
                        context);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new HostVmWriteResult(HostVmOperationStatus.Cancelled, "操作已取消。", context);
            }
            catch (Exception ex)
            {
                string message = SensitiveDataRedactor.Redact(ex.Message);
                if (!context.Target.IsLocal && HostConnectionFailureClassifier.IsConnectionLoss(ex))
                    _sessions.ReportConnectionLoss(context.Stamp, message);
                return new HostVmWriteResult(HostVmOperationStatus.Failed, message, context);
            }
        }
    }

    private bool CanUseExpectedStamp(HostId hostId, HostOperationStamp stamp) =>
        (hostId.IsLocal ? stamp.ProfileId is null : stamp.ProfileId == hostId.ProfileId)
        && _sessions.CanApply(stamp);
}
