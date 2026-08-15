namespace ExHyperV.Services.Remote.Sessions;

public sealed record ActiveHostSession(
    long Generation,
    HostTarget Target,
    HostConnectionState ConnectionState,
    HostChannelState ManagementChannel,
    HostChannelState ConsoleChannel,
    bool HasStaleData)
{
    public static ActiveHostSession CreateLocal(long generation = 1)
    {
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        return new ActiveHostSession(
            generation,
            HostTarget.Local,
            HostConnectionState.LocalConnected,
            HostChannelState.Available,
            HostChannelState.Available,
            HasStaleData: false);
    }
}
