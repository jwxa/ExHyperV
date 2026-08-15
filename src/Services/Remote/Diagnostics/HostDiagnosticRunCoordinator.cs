namespace ExHyperV.Services.Remote.Diagnostics;

internal sealed class HostDiagnosticRunCoordinator : IDisposable
{
    private readonly object _sync = new();
    private HostDiagnosticRun? _current;

    public HostDiagnosticRun Begin(Guid profileId)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("诊断目标配置 ID 不能为空。", nameof(profileId));

        var next = new HostDiagnosticRun(profileId);
        HostDiagnosticRun? previous;
        lock (_sync)
        {
            previous = _current;
            _current = next;
        }
        previous?.Cancel();
        return next;
    }

    public bool IsCurrent(HostDiagnosticRun run, Guid? selectedProfileId)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_sync)
            return ReferenceEquals(_current, run) && selectedProfileId == run.ProfileId;
    }

    public bool Complete(HostDiagnosticRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_sync)
        {
            if (!ReferenceEquals(_current, run)) return false;
            _current = null;
            return true;
        }
    }

    public void CancelCurrent()
    {
        HostDiagnosticRun? current;
        lock (_sync) current = _current;
        current?.Cancel();
    }

    public bool Invalidate()
    {
        HostDiagnosticRun? current;
        lock (_sync)
        {
            current = _current;
            _current = null;
        }
        current?.Cancel();
        return current is not null;
    }

    public void Dispose() => Invalidate();
}

internal sealed class HostDiagnosticRun(Guid profileId) : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private int _disposed;

    public Guid ProfileId { get; } = profileId;
    public CancellationToken Token => _cancellation.Token;

    public void Cancel()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _cancellation.Dispose();
    }
}
