using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;
using ExHyperV.Services;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Configuration;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Preflight;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.ViewModels;

namespace ExHyperV.Views;

public partial class HostConnectionPage
{
    private bool _isNarrow;
    private bool _isAutoScrollingLogs;
    private bool _logHandlersAttached;

    public HostConnectionPage() : this(CreateViewModel())
    {
    }

    public HostConnectionPage(HostConnectionPageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        SizeChanged += (_, _) => ApplyResponsiveLayout(ActualWidth < 780);
        Loaded += (_, _) => ApplyResponsiveLayout(ActualWidth < 780);
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private static HostConnectionPageViewModel CreateViewModel()
    {
        var credentialStore = new WindowsCredentialStore();
        var identityResolver = new HostIdentityResolver(credentialStore);
        var preflightReader = new WindowsHostPreflightReader();
        var preflightPipeline = new HostPreflightPipeline(identityResolver, preflightReader);
        var diagnosticPipeline = new HostDiagnosticPipeline(
            new WindowsIpv4ReachabilityProbe(),
            identityResolver,
            new WindowsExplicitCredentialValidator(),
            new WindowsWmiDcomProbe(),
            new WindowsTcpPortProbe());
        var configurationPipeline = new HostConfigurationPipeline(
            identityResolver,
            preflightPipeline,
            new WindowsHostConfigurationCommandRunner(),
            new HostRollbackScriptWriter(),
            diagnosticPipeline);
        return new HostConnectionPageViewModel(
            new HostProfileManager(new HostProfileStore(), credentialStore),
            ActiveHostSessions.Registry,
            diagnosticPipeline,
            preflightPipeline,
            configurationPipeline,
            new WindowsSupportArtifactLocator());
    }

    private HostLogViewModel Logs => ((HostConnectionPageViewModel)DataContext).Logs;

    private void OnPageLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_logHandlersAttached)
        {
            Logs.Entries.CollectionChanged += OnLiveLogEntriesChanged;
            Logs.FollowLatestRequested += OnFollowLatestRequested;
            _logHandlersAttached = true;
        }
        if (Logs.IsFollowingLatest) ScrollLogsToLatest();
    }

    private void OnPageUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_logHandlersAttached) return;
        Logs.Entries.CollectionChanged -= OnLiveLogEntriesChanged;
        Logs.FollowLatestRequested -= OnFollowLatestRequested;
        _logHandlersAttached = false;
    }

    private void OnLiveLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Logs.IsFollowingLatest) return;
        Dispatcher.BeginInvoke(ScrollLogsToLatest, DispatcherPriority.Loaded);
    }

    private void OnFollowLatestRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(ScrollLogsToLatest, DispatcherPriority.Loaded);

    private void OnLiveLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isAutoScrollingLogs || !Logs.IsFollowingLatest) return;
        bool movedUp = e.VerticalChange < -0.1;
        bool isAwayFromBottom = e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 1;
        if (movedUp && isAwayFromBottom) Logs.PauseFollowingLatest();
    }

    private void ScrollLogsToLatest()
    {
        if (LiveLogList.Items.Count == 0) return;
        _isAutoScrollingLogs = true;
        LiveLogList.ScrollIntoView(LiveLogList.Items[^1]);
        Dispatcher.BeginInvoke(
            () => _isAutoScrollingLogs = false,
            DispatcherPriority.ContextIdle);
    }

    private void ApplyResponsiveLayout(bool narrow)
    {
        if (_isNarrow == narrow && IsLoaded) return;
        _isNarrow = narrow;

        HostStripGrid.RowDefinitions.Clear();
        DetailHeaderGrid.RowDefinitions.Clear();
        ChannelGrid.RowDefinitions.Clear();
        IdentityPropertyGrid.RowDefinitions.Clear();
        DiagnosticContentGrid.RowDefinitions.Clear();
        ReconnectBannerGrid.RowDefinitions.Clear();
        PreflightContentGrid.RowDefinitions.Clear();

        if (narrow)
        {
            HostStripRow.Height = new System.Windows.GridLength(220);
            HostStripGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(116) });
            HostStripGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(82) });
            HostStripGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            HostStripGrid.ColumnDefinitions[1].Width = new System.Windows.GridLength(0);
            HostStripGrid.ColumnDefinitions[2].Width = System.Windows.GridLength.Auto;
            System.Windows.Controls.Grid.SetRow(ActiveHostPanel, 0);
            System.Windows.Controls.Grid.SetColumn(ActiveHostPanel, 0);
            System.Windows.Controls.Grid.SetColumnSpan(ActiveHostPanel, 2);
            System.Windows.Controls.Grid.SetRow(HostList, 1);
            System.Windows.Controls.Grid.SetColumn(HostList, 0);
            System.Windows.Controls.Grid.SetColumnSpan(HostList, 3);
            System.Windows.Controls.Grid.SetRow(AddHostTile, 0);
            System.Windows.Controls.Grid.SetColumn(AddHostTile, 2);
            AddHostTile.Height = 68;
            AddHostTile.Width = 108;

            DetailHeaderGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            DetailHeaderGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            DetailHeaderGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            DetailHeaderGrid.ColumnDefinitions[1].Width = new System.Windows.GridLength(0);
            System.Windows.Controls.Grid.SetRow(DetailActions, 1);
            System.Windows.Controls.Grid.SetColumn(DetailActions, 0);
            DetailActions.Margin = new System.Windows.Thickness(0, 12, 0, 0);

            ChannelGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            ChannelGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(12) });
            ChannelGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            ChannelGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            ChannelGrid.ColumnDefinitions[0].MinWidth = 0;
            ChannelGrid.ColumnDefinitions[1].Width = new System.Windows.GridLength(0);
            ChannelGrid.ColumnDefinitions[2].Width = new System.Windows.GridLength(0);
            ChannelGrid.ColumnDefinitions[2].MinWidth = 0;
            System.Windows.Controls.Grid.SetRow(ManagementPanel, 0);
            System.Windows.Controls.Grid.SetColumn(ManagementPanel, 0);
            System.Windows.Controls.Grid.SetRow(ConsolePanel, 2);
            System.Windows.Controls.Grid.SetColumn(ConsolePanel, 0);

            IdentityPropertyGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            IdentityPropertyGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(116);
            IdentityPropertyGrid.ColumnDefinitions[1].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            IdentityPropertyGrid.ColumnDefinitions[2].Width = new System.Windows.GridLength(116);
            IdentityPropertyGrid.ColumnDefinitions[3].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);

            DiagnosticContentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            DiagnosticContentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(10) });
            DiagnosticContentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            DiagnosticContentGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            DiagnosticContentGrid.ColumnDefinitions[0].MinWidth = 0;
            DiagnosticContentGrid.ColumnDefinitions[1].Width = new System.Windows.GridLength(0);
            DiagnosticContentGrid.ColumnDefinitions[2].Width = new System.Windows.GridLength(0);
            DiagnosticContentGrid.ColumnDefinitions[2].MinWidth = 0;
            System.Windows.Controls.Grid.SetRow(DiagnosticStepsList, 0);
            System.Windows.Controls.Grid.SetColumn(DiagnosticStepsList, 0);
            System.Windows.Controls.Grid.SetRow(DiagnosticLogPanel, 2);
            System.Windows.Controls.Grid.SetColumn(DiagnosticLogPanel, 0);

            ReconnectBannerGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            ReconnectBannerGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            System.Windows.Controls.Grid.SetRow(ReconnectActions, 1);
            System.Windows.Controls.Grid.SetColumn(ReconnectActions, 1);
            ReconnectActions.Margin = new System.Windows.Thickness(0, 10, 0, 0);
            ReconnectActions.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;

            PreflightContentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            PreflightContentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(10) });
            PreflightContentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            PreflightContentGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            PreflightContentGrid.ColumnDefinitions[1].Width = new System.Windows.GridLength(0);
            PreflightContentGrid.ColumnDefinitions[2].Width = new System.Windows.GridLength(0);
            PreflightStepSidebar.Visibility = System.Windows.Visibility.Collapsed;
            PreflightStepStrip.Visibility = System.Windows.Visibility.Visible;
            System.Windows.Controls.Grid.SetRow(PreflightStepBody, 2);
            System.Windows.Controls.Grid.SetColumn(PreflightStepBody, 0);
            System.Windows.Controls.Grid.SetColumnSpan(PreflightStepBody, 3);
            return;
        }

        HostStripRow.Height = new System.Windows.GridLength(126);
        HostStripGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        HostStripGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(172);
        HostStripGrid.ColumnDefinitions[1].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        HostStripGrid.ColumnDefinitions[2].Width = System.Windows.GridLength.Auto;
        System.Windows.Controls.Grid.SetRow(ActiveHostPanel, 0);
        System.Windows.Controls.Grid.SetColumn(ActiveHostPanel, 0);
        System.Windows.Controls.Grid.SetColumnSpan(ActiveHostPanel, 1);
        System.Windows.Controls.Grid.SetRow(HostList, 0);
        System.Windows.Controls.Grid.SetColumn(HostList, 1);
        System.Windows.Controls.Grid.SetColumnSpan(HostList, 1);
        System.Windows.Controls.Grid.SetRow(AddHostTile, 0);
        System.Windows.Controls.Grid.SetColumn(AddHostTile, 2);
        AddHostTile.Height = 82;

        DetailHeaderGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        DetailHeaderGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        DetailHeaderGrid.ColumnDefinitions[1].Width = System.Windows.GridLength.Auto;
        System.Windows.Controls.Grid.SetRow(DetailActions, 0);
        System.Windows.Controls.Grid.SetColumn(DetailActions, 1);
        DetailActions.Margin = new System.Windows.Thickness(0);

        ChannelGrid.ColumnDefinitions[0].MinWidth = 240;
        ChannelGrid.ColumnDefinitions[2].MinWidth = 240;
        RestoreTwoColumnGrid(ChannelGrid, ManagementPanel, ConsolePanel, new System.Windows.GridLength(12));
        IdentityPropertyGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        IdentityPropertyGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(140);
        IdentityPropertyGrid.ColumnDefinitions[1].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        IdentityPropertyGrid.ColumnDefinitions[2].Width = new System.Windows.GridLength(140);
        IdentityPropertyGrid.ColumnDefinitions[3].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        DiagnosticContentGrid.ColumnDefinitions[0].MinWidth = 300;
        DiagnosticContentGrid.ColumnDefinitions[2].MinWidth = 360;
        RestoreTwoColumnGrid(DiagnosticContentGrid, DiagnosticStepsList, DiagnosticLogPanel, new System.Windows.GridLength(10));
        ReconnectBannerGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        System.Windows.Controls.Grid.SetRow(ReconnectActions, 0);
        System.Windows.Controls.Grid.SetColumn(ReconnectActions, 2);
        ReconnectActions.Margin = new System.Windows.Thickness(0);
        ReconnectActions.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;

        PreflightContentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        PreflightContentGrid.ColumnDefinitions[0].Width = new System.Windows.GridLength(190);
        PreflightContentGrid.ColumnDefinitions[1].Width = new System.Windows.GridLength(12);
        PreflightContentGrid.ColumnDefinitions[2].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        PreflightStepSidebar.Visibility = System.Windows.Visibility.Visible;
        PreflightStepStrip.Visibility = System.Windows.Visibility.Collapsed;
        System.Windows.Controls.Grid.SetRow(PreflightStepBody, 0);
        System.Windows.Controls.Grid.SetColumn(PreflightStepBody, 2);
        System.Windows.Controls.Grid.SetColumnSpan(PreflightStepBody, 1);
    }

    private static void RestoreTwoColumnGrid(
        System.Windows.Controls.Grid grid,
        System.Windows.FrameworkElement left,
        System.Windows.FrameworkElement right,
        System.Windows.GridLength gap)
    {
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        grid.ColumnDefinitions[0].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = gap;
        grid.ColumnDefinitions[2].Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        System.Windows.Controls.Grid.SetRow(left, 0);
        System.Windows.Controls.Grid.SetColumn(left, 0);
        System.Windows.Controls.Grid.SetRow(right, 0);
        System.Windows.Controls.Grid.SetColumn(right, 2);
    }
}
