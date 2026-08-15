using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Tools;

namespace ExHyperV.Services.Remote.Vms;

public sealed class ActiveHostVmOperations(
    IActiveHostSessionCoordinator coordinator,
    IHostWmiContextResolver contextResolver)
{
    private readonly IActiveHostSessionCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly IHostWmiContextResolver _contextResolver =
        contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));

    public async Task<HostVmReadResult<T>> ReadAsync<T>(
        Func<WmiContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default,
        HostOperationStamp? expectedStamp = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!_coordinator.TryCaptureManagementOperation(out HostManagementOperationContext? context, out string reason))
            return new HostVmReadResult<T>(HostVmOperationStatus.Failed, default, reason, null);
        if (expectedStamp is not null && context!.Stamp != expectedStamp)
            return new HostVmReadResult<T>(HostVmOperationStatus.Stale, default, "控制台所属的活动宿主已改变。", context);

        try
        {
            WmiContext wmiContext = _contextResolver.Resolve(context!);
            T value = await operation(wmiContext, cancellationToken);
            return _coordinator.CanApply(context!.Stamp)
                ? new HostVmReadResult<T>(HostVmOperationStatus.Succeeded, value, string.Empty, context)
                : new HostVmReadResult<T>(HostVmOperationStatus.Stale, default, "活动宿主已改变，已丢弃旧宿主返回的数据。", context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new HostVmReadResult<T>(HostVmOperationStatus.Cancelled, default, "操作已取消。", context);
        }
        catch (Exception ex)
        {
            string message = SensitiveDataRedactor.Redact(ex.Message);
            if (!_coordinator.CanApply(context!.Stamp))
                return new HostVmReadResult<T>(HostVmOperationStatus.Stale, default, "活动宿主已改变，已丢弃旧宿主返回的错误。", context);
            if (!context.Target.IsLocal && HostConnectionFailureClassifier.IsConnectionLoss(ex))
                _coordinator.ReportConnectionLoss(context.Stamp, message);
            return new HostVmReadResult<T>(HostVmOperationStatus.Failed, default, message, context);
        }
    }

    public async Task<HostVmWriteResult> WriteAsync(
        Func<WmiContext, CancellationToken, Task<HostVmBackendWriteResult>> operation,
        HostOperationStamp? expectedStamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!_coordinator.TryBeginWrite(out IHostWriteLease? lease, out string reason))
            return new HostVmWriteResult(HostVmOperationStatus.WriteBlocked, reason, null);

        using (lease)
        {
            if (!_coordinator.TryCaptureManagementOperation(out HostManagementOperationContext? context, out reason))
                return new HostVmWriteResult(HostVmOperationStatus.Failed, reason, null);
            if (context!.Stamp != lease!.Stamp)
                return new HostVmWriteResult(HostVmOperationStatus.Stale, "活动宿主已改变，未执行写操作。", context);
            if (expectedStamp is not null && context.Stamp != expectedStamp)
                return new HostVmWriteResult(HostVmOperationStatus.Stale, "确认后活动宿主已改变，未执行写操作。", context);

            try
            {
                WmiContext wmiContext = _contextResolver.Resolve(context);
                HostVmBackendWriteResult result = await operation(wmiContext, cancellationToken);
                if (!_coordinator.CanApply(lease.Stamp))
                    return new HostVmWriteResult(HostVmOperationStatus.Stale, "活动宿主已改变，已忽略旧宿主操作结果。", context);

                if (!result.Succeeded
                    && !context.Target.IsLocal
                    && result.FailureException is not null
                    && HostConnectionFailureClassifier.IsConnectionLoss(result.FailureException))
                    _coordinator.ReportConnectionLoss(context.Stamp, SensitiveDataRedactor.Redact(result.Message));

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
                    _coordinator.ReportConnectionLoss(context.Stamp, message);
                return new HostVmWriteResult(HostVmOperationStatus.Failed, message, context);
            }
        }
    }
}
