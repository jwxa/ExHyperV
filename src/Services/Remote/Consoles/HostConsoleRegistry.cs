using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.Services.Remote.Consoles;

public interface IHostConsoleWindow
{
    void Activate();
    void Close();
}

public interface IHostConsoleRegistry
{
    int Count(HostId hostId);
    IReadOnlyList<HostConsoleWindowInfo> GetOpenWindows(HostId hostId);
    bool TryActivate(string windowKey);
    void Register(HostConsoleSession session, IHostConsoleWindow window);
    bool Unregister(string windowKey, IHostConsoleWindow window);
    HostConsoleCloseResult CloseAll(HostId hostId);
}

public sealed record HostConsoleWindowInfo(
    HostId HostId,
    VmKey VmKey,
    string VmName,
    string WindowKey);

public sealed record HostConsoleCloseResult(
    int RequestedCount,
    int ClosedCount,
    string Message)
{
    public bool Succeeded => RequestedCount == ClosedCount;
}

public sealed class HostConsoleRegistry : IHostConsoleRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Registration> _windows = new(StringComparer.Ordinal);

    public int Count(HostId hostId)
    {
        lock (_sync) return _windows.Values.Count(item => item.Info.HostId == hostId);
    }

    public IReadOnlyList<HostConsoleWindowInfo> GetOpenWindows(HostId hostId)
    {
        lock (_sync)
        {
            return _windows.Values
                .Where(item => item.Info.HostId == hostId)
                .Select(item => item.Info)
                .ToArray();
        }
    }

    public bool TryActivate(string windowKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowKey);
        Registration? registration;
        lock (_sync) _windows.TryGetValue(windowKey, out registration);
        if (registration is null) return false;

        try
        {
            registration.Window.Activate();
            return true;
        }
        catch
        {
            Unregister(windowKey, registration.Window);
            return false;
        }
    }

    public void Register(HostConsoleSession session, IHostConsoleWindow window)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(window);
        var info = new HostConsoleWindowInfo(
            session.HostId,
            session.VmKey,
            session.VmName,
            session.WindowKey);
        lock (_sync)
        {
            if (_windows.ContainsKey(session.WindowKey))
                throw new InvalidOperationException("该虚拟机控制台窗口已经打开。");
            _windows.Add(session.WindowKey, new Registration(info, window));
        }
    }

    public bool Unregister(string windowKey, IHostConsoleWindow window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowKey);
        ArgumentNullException.ThrowIfNull(window);
        lock (_sync)
        {
            if (!_windows.TryGetValue(windowKey, out Registration? registration)
                || !ReferenceEquals(registration.Window, window))
                return false;
            return _windows.Remove(windowKey);
        }
    }

    public HostConsoleCloseResult CloseAll(HostId hostId)
    {
        Registration[] targets;
        lock (_sync)
        {
            targets = _windows.Values
                .Where(item => item.Info.HostId == hostId)
                .ToArray();
        }

        int closedCount = 0;
        foreach (Registration target in targets)
        {
            try
            {
                target.Window.Close();
                Unregister(target.Info.WindowKey, target.Window);
                closedCount++;
            }
            catch
            {
                // Keep a failed window registered so a later disconnect attempt can retry it.
            }
        }

        string message = closedCount == targets.Length
            ? targets.Length == 0
                ? "目标宿主没有打开的控制台窗口。"
                : $"已关闭目标宿主的 {closedCount} 个控制台窗口。"
            : $"需要关闭 {targets.Length} 个控制台窗口，但仅关闭了 {closedCount} 个。";
        return new HostConsoleCloseResult(targets.Length, closedCount, message);
    }

    private sealed record Registration(
        HostConsoleWindowInfo Info,
        IHostConsoleWindow Window);
}

public static class HostConsoleWindows
{
    public static IHostConsoleRegistry Registry { get; } = new HostConsoleRegistry();
}
