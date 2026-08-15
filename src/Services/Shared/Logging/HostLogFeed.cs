using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.Services.Logging;

public interface IHostLogFeed
{
    int MaxEntriesPerHost { get; }
    IReadOnlyList<AppLogEntry> GetSnapshot(HostId hostId);
    IDisposable Subscribe(HostId hostId, Action<AppLogEntry> onEntry);
}

public sealed class HostLogFeed : IHostLogFeed
{
    public const int DefaultMaxEntriesPerHost = 2_000;

    private readonly object _sync = new();
    private readonly int _maxEntriesPerHost;
    private readonly Dictionary<HostId, Queue<AppLogEntry>> _entries = [];
    private readonly Dictionary<long, SubscriptionRegistration> _subscriptions = [];
    private long _nextSubscriptionId;

    public HostLogFeed(int maxEntriesPerHost = DefaultMaxEntriesPerHost)
    {
        if (maxEntriesPerHost <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxEntriesPerHost),
                "每宿主日志条目上限必须大于 0。");
        _maxEntriesPerHost = maxEntriesPerHost;
    }

    public int MaxEntriesPerHost => _maxEntriesPerHost;

    public IReadOnlyList<AppLogEntry> GetSnapshot(HostId hostId)
    {
        lock (_sync)
            return _entries.TryGetValue(hostId, out Queue<AppLogEntry>? entries)
                ? entries.ToArray()
                : [];
    }

    public IDisposable Subscribe(HostId hostId, Action<AppLogEntry> onEntry)
    {
        ArgumentNullException.ThrowIfNull(onEntry);
        long id;
        lock (_sync)
        {
            id = ++_nextSubscriptionId;
            _subscriptions.Add(id, new SubscriptionRegistration(hostId, onEntry));
        }
        return new Subscription(this, id);
    }

    public void Publish(AppLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Action<AppLogEntry>[] subscribers;
        lock (_sync)
        {
            if (!_entries.TryGetValue(entry.HostId, out Queue<AppLogEntry>? entries))
            {
                entries = new Queue<AppLogEntry>(_maxEntriesPerHost);
                _entries.Add(entry.HostId, entries);
            }

            entries.Enqueue(entry);
            while (entries.Count > _maxEntriesPerHost) entries.Dequeue();
            subscribers = _subscriptions.Values
                .Where(subscription => subscription.HostId == entry.HostId)
                .Select(subscription => subscription.OnEntry)
                .ToArray();
        }

        foreach (Action<AppLogEntry> subscriber in subscribers)
        {
            try { subscriber(entry); }
            catch { }
        }
    }

    internal void Clear()
    {
        lock (_sync) _entries.Clear();
    }

    private void Unsubscribe(long id)
    {
        lock (_sync) _subscriptions.Remove(id);
    }

    private sealed record SubscriptionRegistration(HostId HostId, Action<AppLogEntry> OnEntry);

    private sealed class Subscription(HostLogFeed owner, long id) : IDisposable
    {
        private HostLogFeed? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(id);
    }
}
