using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Sessions;
using MediaBrush = System.Windows.Media.Brush;

namespace ExHyperV.ViewModels;

public sealed partial class HostLogViewModel : ObservableObject, IDisposable
{
    private readonly IHostLogFeed _feed;
    private readonly SynchronizationContext? _synchronizationContext;
    private IDisposable? _subscription;
    private long _selectionVersion;
    private bool _hasSelection;

    [ObservableProperty] private bool _isFollowingLatest = true;
    [ObservableProperty] private bool _hasEntries;

    public HostLogViewModel(IHostLogFeed feed)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _synchronizationContext = SynchronizationContext.Current;
    }

    public ObservableCollection<HostLogEntryViewModel> Entries { get; } = [];
    public HostId SelectedHostId { get; private set; } = HostId.Local;
    public event EventHandler? FollowLatestRequested;

    public void SelectHost(HostId hostId)
    {
        if (_hasSelection && SelectedHostId == hostId) return;

        _subscription?.Dispose();
        _subscription = null;
        SelectedHostId = hostId;
        _hasSelection = true;
        IsFollowingLatest = true;
        long version = ++_selectionVersion;
        var gate = new object();
        var pending = new List<AppLogEntry>();
        bool snapshotLoaded = false;

        IDisposable subscription = _feed.Subscribe(hostId, entry =>
        {
            lock (gate)
            {
                if (!snapshotLoaded)
                {
                    pending.Add(entry);
                    return;
                }
            }
            Dispatch(() => AppendIfCurrent(hostId, version, entry));
        });

        IReadOnlyList<AppLogEntry> snapshot = _feed.GetSnapshot(hostId);
        Entries.Clear();
        foreach (AppLogEntry entry in snapshot) Entries.Add(new HostLogEntryViewModel(entry));
        HasEntries = Entries.Count > 0;

        AppLogEntry[] queued;
        lock (gate)
        {
            snapshotLoaded = true;
            queued = pending.ToArray();
        }
        _subscription = subscription;
        foreach (AppLogEntry entry in queued) AppendIfCurrent(hostId, version, entry);
    }

    public void PauseFollowingLatest() => IsFollowingLatest = false;

    [RelayCommand]
    private void ReturnToLatest()
    {
        IsFollowingLatest = true;
        FollowLatestRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        _hasSelection = false;
        _selectionVersion++;
    }

    private void AppendIfCurrent(HostId hostId, long version, AppLogEntry entry)
    {
        if (!_hasSelection || SelectedHostId != hostId || _selectionVersion != version) return;
        if (Entries.Any(item => ReferenceEquals(item.Entry, entry))) return;

        Entries.Add(new HostLogEntryViewModel(entry));
        while (Entries.Count > _feed.MaxEntriesPerHost) Entries.RemoveAt(0);
        HasEntries = true;
    }

    private void Dispatch(Action action)
    {
        if (_synchronizationContext is null
            || ReferenceEquals(SynchronizationContext.Current, _synchronizationContext))
        {
            action();
            return;
        }
        _synchronizationContext.Post(_ => action(), null);
    }
}

public sealed class HostLogEntryViewModel
{
    public HostLogEntryViewModel(AppLogEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        TimestampText = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        LevelText = entry.Level switch
        {
            AppLogLevel.Debug => "调试",
            AppLogLevel.Information => "信息",
            AppLogLevel.Warning => "警告",
            AppLogLevel.Error => "错误",
            _ => "信息"
        };
        LevelBrush = entry.Level switch
        {
            AppLogLevel.Warning => UiStatusBrushes.Caution,
            AppLogLevel.Error => UiStatusBrushes.Critical,
            _ => UiStatusBrushes.Success
        };
        ErrorCategoryText = string.Equals(entry.ErrorCategory, "None", StringComparison.Ordinal)
            ? string.Empty
            : entry.ErrorCategory;
        string properties = string.Join(
            "  ",
            entry.Properties.Select(property => $"{property.Name}={property.Value}"));
        DetailText = string.Join(
            "  ",
            new[] { properties, entry.ExceptionText }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    internal AppLogEntry Entry { get; }
    public string TimestampText { get; }
    public string LevelText { get; }
    public MediaBrush LevelBrush { get; }
    public string Source => Entry.Source;
    public string Message => Entry.Message;
    public string ErrorCategoryText { get; }
    public bool HasErrorCategory => !string.IsNullOrEmpty(ErrorCategoryText);
    public string DetailText { get; }
    public bool HasDetail => !string.IsNullOrEmpty(DetailText);
}
