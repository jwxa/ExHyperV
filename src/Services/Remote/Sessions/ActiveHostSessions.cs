namespace ExHyperV.Services.Remote.Sessions;

public static class ActiveHostSessions
{
    private static readonly object Sync = new();
    private static IActiveHostSessionCoordinator _current = new ActiveHostSessionCoordinator();
    private static bool _configured;

    public static IActiveHostSessionCoordinator Current
    {
        get
        {
            lock (Sync) return _current;
        }
    }

    public static void Configure(
        IHostSessionConnector connector,
        IHostBasicSnapshotLoader snapshotLoader)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(snapshotLoader);
        lock (Sync)
        {
            if (_configured) throw new InvalidOperationException("活动宿主会话已配置。" );
            _current = new ActiveHostSessionCoordinator(connector, snapshotLoader);
            _configured = true;
        }
    }
}
