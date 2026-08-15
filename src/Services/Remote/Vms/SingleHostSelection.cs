using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.Services.Remote.Vms;

public sealed class HostScopedSelection<T>
{
    internal HostScopedSelection(HostId hostId, IReadOnlyList<T> items)
    {
        HostId = hostId;
        Items = items;
    }

    public HostId HostId { get; }
    public IReadOnlyList<T> Items { get; }
    public int Count => Items.Count;
}

public sealed class SingleHostSelection<T>
{
    private readonly Func<T, HostId> _hostIdOf;
    private HostScopedSelection<T>? _current;

    public SingleHostSelection(Func<T, HostId> hostIdOf) =>
        _hostIdOf = hostIdOf ?? throw new ArgumentNullException(nameof(hostIdOf));

    public IReadOnlyList<T> Items => _current?.Items ?? Array.Empty<T>();
    public int Count => _current?.Count ?? 0;

    public HostId? Replace(HostId sourceHostId, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        T[] copy = items.ToArray();
        if (copy.Any(item => _hostIdOf(item) != sourceHostId))
            throw new ArgumentException("虚拟机选择必须属于同一宿主。", nameof(items));

        if (copy.Length == 0)
        {
            if (_current?.HostId == sourceHostId) _current = null;
            return null;
        }

        HostId? previousHostId = _current?.HostId;
        _current = new HostScopedSelection<T>(sourceHostId, Array.AsReadOnly(copy));
        return previousHostId is { } previous && previous != sourceHostId
            ? previous
            : null;
    }

    public HostScopedSelection<T>? Capture() => _current;

    public void Clear() => _current = null;
}
