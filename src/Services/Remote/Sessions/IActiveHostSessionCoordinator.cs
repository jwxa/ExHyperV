using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.Services.Remote.Sessions;

public interface IActiveHostSessionCoordinator
{
    ActiveHostCoordinatorSnapshot Current { get; }
    bool IsWriteFrozen { get; }
    event EventHandler<ActiveHostStateChangedEventArgs>? StateChanged;
    void SelectProfile(HostProfile? profile);
    void ResetToLocal();
    Task<HostSwitchResult> SwitchToSelectedAsync(
        HostSwitchRequest request,
        CancellationToken cancellationToken = default);
    Task<HostSwitchResult> SwitchToLocalAsync(CancellationToken cancellationToken = default);
    bool TryBeginWrite(out IHostWriteLease? lease, out string reason);
    bool TryCaptureManagementOperation(
        out HostManagementOperationContext? context,
        out string reason);
    bool TryCaptureConsoleOperation(
        out HostConsoleOperationContext? context,
        out string reason);
    HostOperationStamp CaptureOperationStamp();
    bool CanApply(HostOperationStamp stamp);
    bool CanUseConsole(HostOperationStamp stamp);
    bool UpdateActiveChannels(
        Guid profileId,
        HostChannelState managementChannel,
        HostChannelState consoleChannel,
        string? managementFailureReason = null);
    bool ReportConnectionLoss(HostOperationStamp stamp, string reason);
    bool RetryReconnectNow();
    void StopReconnect();
    void Shutdown();
}
