namespace ExHyperV.Services.Remote.Sessions;

public static class HostSessions
{
    private static readonly object Sync = new();
    private static IHostSessionRegistry _registry = new HostSessionRegistry();
    private static bool _configured;

    public static IHostSessionRegistry Registry
    {
        get
        {
            lock (Sync) return _registry;
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
            if (_configured) throw new InvalidOperationException("宿主会话注册表已配置。");
            _registry = new HostSessionRegistry(connector, snapshotLoader);
            _configured = true;
        }
    }
}
