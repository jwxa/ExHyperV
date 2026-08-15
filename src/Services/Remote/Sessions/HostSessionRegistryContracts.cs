using System.Collections.ObjectModel;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.Services.Remote.Sessions;

public interface IHostSessionRegistry
{
    HostRegistrySnapshot Current { get; }
    event EventHandler<HostRegistryChangedEventArgs>? Changed;

    Task<HostConnectResult> ConnectAsync(
        HostConnectRequest request,
        CancellationToken cancellationToken = default);

    HostOperationStamp CaptureOperationStamp(HostId hostId);
    bool CanApply(HostOperationStamp stamp);
    bool ReportConnectionLoss(HostOperationStamp stamp, string reason);
    void Shutdown();
}

public sealed class HostRegistryChangedEventArgs(
    HostId changedHostId,
    HostRegistrySnapshot previous,
    HostRegistrySnapshot current) : EventArgs
{
    public HostId ChangedHostId { get; } = changedHostId;
    public HostRegistrySnapshot Previous { get; } = previous;
    public HostRegistrySnapshot Current { get; } = current;
}

public sealed record HostConnectRequest(
    HostProfile Profile,
    HostChannelState ManagementChannel,
    HostChannelState ConsoleChannel,
    WindowsCredential? TransientCredential = null,
    bool RevalidateChannels = false)
{
    internal HostSwitchRequest ToSwitchRequest() => new(
        Profile,
        ManagementChannel,
        ConsoleChannel,
        TransientCredential,
        RevalidateChannels);
}

public enum HostConnectStatus
{
    Succeeded,
    AlreadyConnected,
    Cancelled,
    Shutdown,
    Failed
}

public sealed record HostConnectResult(
    HostConnectStatus Status,
    string Message,
    HostId HostId,
    HostRegistrySnapshot Snapshot)
{
    public bool Succeeded => Status == HostConnectStatus.Succeeded;
}

public sealed record HostSessionSnapshot(
    HostId HostId,
    long Generation,
    HostTarget Target,
    HostConnectionState ConnectionState,
    HostChannelState ManagementChannel,
    HostChannelState ConsoleChannel,
    bool HasStaleData)
{
    public static HostSessionSnapshot CreateLocal() => new(
        HostId.Local,
        1,
        HostTarget.Local,
        HostConnectionState.LocalConnected,
        HostChannelState.Available,
        HostChannelState.Available,
        HasStaleData: false);

    internal static HostSessionSnapshot FromActive(ActiveHostCoordinatorSnapshot snapshot)
    {
        ActiveHostSession session = snapshot.ActiveSession;
        return new HostSessionSnapshot(
            HostId.FromProfileId(session.Target.ProfileId
                ?? throw new ArgumentException("远程会话缺少宿主配置 ID。", nameof(snapshot))),
            session.Generation,
            session.Target,
            session.ConnectionState,
            session.ManagementChannel,
            session.ConsoleChannel,
            session.HasStaleData);
    }
}

public sealed class HostRegistrySnapshot
{
    private readonly ReadOnlyCollection<HostSessionSnapshot> _hosts;

    internal HostRegistrySnapshot(IEnumerable<HostSessionSnapshot> hosts)
    {
        HostSessionSnapshot[] copy = hosts?.ToArray()
            ?? throw new ArgumentNullException(nameof(hosts));
        if (copy.Length == 0 || copy[0].HostId != HostId.Local)
            throw new ArgumentException("注册表快照必须以本机会话开头。", nameof(hosts));
        if (copy.Count(host => host.HostId == HostId.Local) != 1)
            throw new ArgumentException("注册表快照必须且只能包含一个本机会话。", nameof(hosts));
        if (copy.Select(host => host.HostId).Distinct().Count() != copy.Length)
            throw new ArgumentException("注册表快照不能包含重复宿主。", nameof(hosts));

        _hosts = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<HostSessionSnapshot> Hosts => _hosts;
}
