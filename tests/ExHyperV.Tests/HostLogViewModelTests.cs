using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.ViewModels;

internal static class HostLogViewModelTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("LoggingUi_SelectionReplacesHostScopeAndOldSubscription", SelectionReplacesHostScopeAndOldSubscription),
        ("LoggingUi_PausePersistsUntilReturnToLatest", PausePersistsUntilReturnToLatest),
        ("LoggingUi_XamlUsesVirtualizedListAndScrollPause", XamlUsesVirtualizedListAndScrollPause)
    ];

    private static void SelectionReplacesHostScopeAndOldSubscription()
    {
        var feed = new HostLogFeed(maxEntriesPerHost: 3);
        HostId hostA = HostId.FromProfileId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        HostId hostB = HostId.FromProfileId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        feed.Publish(Entry(hostA, "A-1"));
        feed.Publish(Entry(hostB, "B-1"));

        using var viewModel = new HostLogViewModel(feed);
        viewModel.SelectHost(hostA);
        TestAssert.SequenceEqual(new[] { "A-1" }, viewModel.Entries.Select(entry => entry.Message));

        feed.Publish(Entry(hostB, "B-2"));
        feed.Publish(Entry(hostA, "A-2"));
        TestAssert.SequenceEqual(new[] { "A-1", "A-2" }, viewModel.Entries.Select(entry => entry.Message));

        viewModel.SelectHost(hostB);
        TestAssert.SequenceEqual(new[] { "B-1", "B-2" }, viewModel.Entries.Select(entry => entry.Message));
        feed.Publish(Entry(hostA, "A-after-switch"));
        TestAssert.SequenceEqual(new[] { "B-1", "B-2" }, viewModel.Entries.Select(entry => entry.Message));
    }

    private static void PausePersistsUntilReturnToLatest()
    {
        var feed = new HostLogFeed();
        using var viewModel = new HostLogViewModel(feed);
        int followRequests = 0;
        viewModel.FollowLatestRequested += (_, _) => followRequests++;
        viewModel.SelectHost(HostId.Local);

        TestAssert.True(viewModel.IsFollowingLatest, "Log view did not follow latest by default.");
        viewModel.PauseFollowingLatest();
        feed.Publish(Entry(HostId.Local, "arrived-while-paused"));
        TestAssert.False(viewModel.IsFollowingLatest, "A new entry resumed following without user action.");
        TestAssert.Equal("arrived-while-paused", viewModel.Entries.Single().Message);

        viewModel.ReturnToLatestCommand.Execute(null);
        TestAssert.True(viewModel.IsFollowingLatest, "Return-to-latest did not resume following.");
        TestAssert.Equal(1, followRequests);
    }

    private static void XamlUsesVirtualizedListAndScrollPause()
    {
        string xaml = ReadSource("Views", "Pages", "HostConnectionPage.xaml");
        string codeBehind = ReadSource("Views", "Pages", "HostConnectionPage.xaml.cs");

        TestAssert.Contains("ItemsSource=\"{Binding Logs.Entries}\"", xaml);
        TestAssert.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", xaml);
        TestAssert.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", xaml);
        TestAssert.Contains("ScrollChanged=\"OnLiveLogScrollChanged\"", xaml);
        TestAssert.Contains("Command=\"{Binding Logs.ReturnToLatestCommand}\"", xaml);
        TestAssert.Contains("Symbol=\"ArrowDown24\"", xaml);
        TestAssert.Contains("PauseFollowingLatest", codeBehind);
        TestAssert.Contains("ScrollIntoView", codeBehind);
    }

    private static AppLogEntry Entry(HostId hostId, string message) => AppLogEntry.Create(
        DateTimeOffset.UnixEpoch,
        AppLogLevel.Information,
        "测试",
        message,
        new AppLogContext(Host: hostId.ToString(), HostId: hostId));

    private static string ReadSource(params string[] segments) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), "src", .. segments]));

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "ExHyperV.csproj")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the ExHyperV repository root.");
    }
}
