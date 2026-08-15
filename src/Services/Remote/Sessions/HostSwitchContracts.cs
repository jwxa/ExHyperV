using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.Services.Remote.Sessions;

public interface IHostManagementConnection;

public sealed class LocalHostManagementConnection : IHostManagementConnection
{
    public static LocalHostManagementConnection Instance { get; } = new();

    private LocalHostManagementConnection()
    {
    }
}

public interface IHostSessionCandidate : IAsyncDisposable
{
    HostTarget Target { get; }
    IHostManagementConnection ManagementConnection { get; }
    HostChannelState ManagementChannel { get; }
    HostChannelState ConsoleChannel { get; }
}

public interface IHostSessionConnector
{
    Task<IHostSessionCandidate> ConnectAsync(
        HostSwitchRequest request,
        CancellationToken cancellationToken);
}

public interface IHostBasicSnapshotLoader
{
    Task<HostBasicSnapshot> LoadAsync(
        IHostSessionCandidate candidate,
        CancellationToken cancellationToken);
}

public interface IHostWriteLease : IDisposable
{
    HostOperationStamp Stamp { get; }
}

public sealed record HostSwitchRequest(
    HostProfile Profile,
    HostChannelState ManagementChannel,
    HostChannelState ConsoleChannel,
    WindowsCredential? TransientCredential = null,
    bool RevalidateChannels = false)
{
    public static HostSwitchRequest ForConfirmedDiagnostic(
        HostProfile profile,
        bool consoleAvailable,
        WindowsCredential? transientCredential = null) =>
        new(
            profile ?? throw new ArgumentNullException(nameof(profile)),
            HostChannelState.Available,
            consoleAvailable ? HostChannelState.Available : HostChannelState.Unavailable,
            transientCredential,
            RevalidateChannels: true);
}

public sealed record HostBasicSnapshot(
    string ComputerName,
    string OperatingSystem,
    string HyperVStatus,
    int VirtualMachineCount,
    DateTimeOffset RefreshedAt);

public sealed record HostOperationStamp(long Generation, Guid? ProfileId);

public sealed record HostManagementOperationContext(
    HostTarget Target,
    HostOperationStamp Stamp,
    IHostManagementConnection ManagementConnection);

public sealed record HostConsoleOperationContext(
    HostTarget Target,
    HostOperationStamp Stamp);

public enum HostSwitchStatus
{
    Succeeded,
    NoSelection,
    BlockedByActiveWrites,
    SwitchInProgress,
    StaleSelection,
    Cancelled,
    Shutdown,
    Failed
}

public sealed record HostSwitchResult(
    HostSwitchStatus Status,
    string Message,
    ActiveHostCoordinatorSnapshot Snapshot)
{
    public bool Succeeded => Status == HostSwitchStatus.Succeeded;
}

public sealed class HostSwitchException(string message, Exception? innerException = null)
    : Exception(message, innerException);
