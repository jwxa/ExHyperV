using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Services.Remote.Vms;
using ExHyperV.ViewModels;

internal static class VirtualMachinesMultiHostWiringTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("VmGroups_ProjectLocalThenRemoteAThenRemoteB", ProjectLocalThenRemoteAThenRemoteB),
        ("VmGroups_ApplySessionUpdatesOnlyOwningGroup", ApplySessionUpdatesOnlyOwningGroup),
        ("VmPage_AssignsOwningHostIdentityToRows", AssignsOwningHostIdentityToRows),
        ("VmPage_RoutesReadsAndWritesByOwningHost", RoutesReadsAndWritesByOwningHost),
        ("VmPage_XamlGroupsRowsByHost", XamlGroupsRowsByHost),
        ("VmPage_RemoteGroupsRefreshIndependently", RemoteGroupsRefreshIndependently),
        ("VmSelection_SwitchingHostReplacesOriginalScope", SwitchingHostReplacesOriginalScope),
        ("VmSelection_MixedHostsRejectedAndCaptureHasOneHostId", MixedHostsRejectedAndCaptureHasOneHostId),
        ("VmSelection_EmptySelectionClearsCurrentScope", EmptySelectionClearsCurrentScope),
        ("VmPage_SelectionUsesSingleHostScope", SelectionUsesSingleHostScope),
        ("VmPage_DisconnectRemovesOnlyTargetRemoteGroup", DisconnectRemovesOnlyTargetRemoteGroup)
    ];

    private static void ProjectLocalThenRemoteAThenRemoteB()
    {
        IReadOnlyList<HostVmGroupViewModel> groups = HostVmGroupViewModel.CreateOrdered(Snapshot());
        try
        {
            TestAssert.Equal(3, groups.Count);
            TestAssert.Equal(HostId.Local, groups[0].HostId);
            TestAssert.Equal(HostA, groups[1].HostId);
            TestAssert.Equal(HostB, groups[2].HostId);
            TestAssert.Equal(0, groups[0].Order);
            TestAssert.Equal(1, groups[1].Order);
            TestAssert.Equal(2, groups[2].Order);
        }
        finally
        {
            foreach (HostVmGroupViewModel group in groups) group.Dispose();
        }
    }

    private static void ApplySessionUpdatesOnlyOwningGroup()
    {
        IReadOnlyList<HostVmGroupViewModel> groups = HostVmGroupViewModel.CreateOrdered(Snapshot());
        try
        {
            HostVmGroupViewModel groupA = groups[1];
            HostVmGroupViewModel groupB = groups[2];
            string originalBName = groupB.DisplayName;
            HostConnectionState originalBState = groupB.ConnectionState;

            groupA.ApplySession(RemoteSnapshot(ProfileA with { DisplayName = "宿主 A 已更新" }, HostConnectionState.Reconnecting, stale: true));

            TestAssert.Equal("宿主 A 已更新", groupA.DisplayName);
            TestAssert.True(groupA.HasStaleData, "Updating host A did not update its stale state.");
            TestAssert.Equal(originalBName, groupB.DisplayName);
            TestAssert.Equal(originalBState, groupB.ConnectionState);
            TestAssert.False(groupB.HasStaleData, "Updating host A changed host B's stale state.");
        }
        finally
        {
            foreach (HostVmGroupViewModel group in groups) group.Dispose();
        }
    }

    private static void AssignsOwningHostIdentityToRows()
    {
        string rowSource = ReadSource("ViewModels", "VmInstanceViewModel.cs");
        string pageSource = ReadSource("ViewModels", "VirtualMachinesPageViewModel.cs");

        TestAssert.Contains("public HostId HostId", rowSource);
        TestAssert.Contains("public VmKey VmKey", rowSource);
        TestAssert.Contains("new VmInstanceViewModel(group.HostId, snapshot)", pageSource);
    }

    private static void RoutesReadsAndWritesByOwningHost()
    {
        string source = ReadSource("ViewModels", "VirtualMachinesPageViewModel.cs");

        TestAssert.Contains("_hostOperationRouter.ReadAsync(", source);
        TestAssert.Contains("group.HostId", source);
        TestAssert.Contains("_hostOperationRouter.WriteAsync(", source);
        TestAssert.Contains("instance.HostId", source);
    }

    private static void XamlGroupsRowsByHost()
    {
        string xaml = ReadSource("Views", "Pages", "VirtualMachinesPage.xaml");

        TestAssert.Contains("ItemsSource=\"{Binding HostGroups}\"", xaml);
        TestAssert.Contains("ItemsSource=\"{Binding Vms}\"", xaml);
        TestAssert.Contains("DisplayName", xaml);
        TestAssert.Contains("DisplayAddress", xaml);
    }

    private static void RemoteGroupsRefreshIndependently()
    {
        string source = ReadSource("ViewModels", "VirtualMachinesPageViewModel.cs");

        TestAssert.Contains("Task.WhenAll(", source);
        TestAssert.Contains("MonitorRemoteStateLoop(HostVmGroupViewModel group,", source);
        TestAssert.Contains("StartRemoteMonitoring(group)", source);
        TestAssert.Contains("onlyWriteCountChanged", source);
        TestAssert.False(
            source.Contains("FirstOrDefault(candidate => !candidate.IsLocal)", StringComparison.Ordinal),
            "Remote monitoring still selects one shared remote group per iteration.");
    }

    private static void SwitchingHostReplacesOriginalScope()
    {
        var selection = new SingleHostSelection<FakeVm>(vm => vm.HostId);
        var a1 = new FakeVm(HostA, "A-1");
        var a2 = new FakeVm(HostA, "A-2");
        var b1 = new FakeVm(HostB, "B-1");

        TestAssert.Equal<HostId?>(null, selection.Replace(HostA, [a1, a2]));
        HostId? previous = selection.Replace(HostB, [b1]);

        TestAssert.Equal<HostId?>(HostA, previous);
        TestAssert.Equal(1, selection.Count);
        TestAssert.Equal(HostB, selection.Capture()!.HostId);
        TestAssert.True(ReferenceEquals(b1, selection.Items[0]), "Switching host kept an item from the original scope.");
    }

    private static void MixedHostsRejectedAndCaptureHasOneHostId()
    {
        var selection = new SingleHostSelection<FakeVm>(vm => vm.HostId);
        var a = new FakeVm(HostA, "A");
        var b = new FakeVm(HostB, "B");
        selection.Replace(HostA, [a]);

        Assert.Throws<ArgumentException>(() => selection.Replace(HostA, [a, b]));

        HostScopedSelection<FakeVm>? captured = selection.Capture();
        TestAssert.NotNull(captured, "A rejected mixed-host replacement cleared the valid selection.");
        TestAssert.Equal(HostA, captured!.HostId);
        TestAssert.Equal(1, captured.Count);
        TestAssert.True(captured.Items.All(vm => vm.HostId == captured.HostId), "Captured batch selection spans hosts.");
    }

    private static void EmptySelectionClearsCurrentScope()
    {
        var selection = new SingleHostSelection<FakeVm>(vm => vm.HostId);
        selection.Replace(HostA, [new FakeVm(HostA, "A")]);

        selection.Replace(HostA, Array.Empty<FakeVm>());

        TestAssert.Equal(0, selection.Count);
        TestAssert.True(selection.Capture() is null, "Clearing the final visual selection retained a stale batch scope.");
    }

    private static void SelectionUsesSingleHostScope()
    {
        string page = ReadSource("Views", "Pages", "VirtualMachinesPage.xaml.cs");
        string viewModel = ReadSource("ViewModels", "VirtualMachinesPageViewModel.cs");

        TestAssert.Contains("var previousHostId = vm.UpdateSelection(group.HostId, lv.SelectedItems)", page);
        TestAssert.Contains("otherGroup.HostId == previous", page);
        TestAssert.False(page.Contains("e.AddedItems.Count == 0", StringComparison.Ordinal),
            "Clearing the final row still leaves a stale ViewModel selection.");
        TestAssert.Contains("SingleHostSelection<VmInstanceViewModel>", viewModel);
        TestAssert.Contains("HostScopedSelection<VmInstanceViewModel>", viewModel);
        TestAssert.False(viewModel.Contains("private List<VmInstanceViewModel> _selectedVms", StringComparison.Ordinal),
            "The VM page still keeps a second unscoped selection state.");
    }

    private static void DisconnectRemovesOnlyTargetRemoteGroup()
    {
        string source = ReadSource("ViewModels", "VirtualMachinesPageViewModel.cs");

        TestAssert.Contains("RemoveDisconnectedHostGroup(change.ChangedHostId)", source);
        TestAssert.Contains("if (hostId.IsLocal) return;", source);
        TestAssert.Contains("group.Dispose();", source);
        TestAssert.Contains("HostGroups.Remove(group);", source);
        TestAssert.Contains("_remoteMonitorTasks.Remove(hostId);", source);
        TestAssert.Contains("RebuildVmList();", source);
        TestAssert.Contains("HostGroups.First(group => group.IsLocal)", source);
    }

    private static string ReadSource(params string[] segments) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), "src", .. segments]));

    private static readonly HostProfile ProfileA = new(
        Guid.Parse("17171717-1717-1717-1717-171717171717"),
        "宿主 A",
        "10.0.0.6");

    private static readonly HostProfile ProfileB = new(
        Guid.Parse("18181818-1818-1818-1818-181818181818"),
        "宿主 B",
        "10.0.0.7");

    private static HostId HostA => HostId.FromProfile(ProfileA);
    private static HostId HostB => HostId.FromProfile(ProfileB);

    private static HostRegistrySnapshot Snapshot() => new(
        [
            HostSessionSnapshot.CreateLocal(),
            RemoteSnapshot(ProfileA),
            RemoteSnapshot(ProfileB)
        ]);

    private static HostSessionSnapshot RemoteSnapshot(
        HostProfile profile,
        HostConnectionState state = HostConnectionState.Connected,
        bool stale = false) => new(
            HostId.FromProfile(profile),
            1,
            HostTarget.FromProfile(profile),
            state,
            HostChannelState.Available,
            HostChannelState.Available,
            stale)
        {
            Capabilities = HostCapabilityMatrix.Create(
                new ActiveHostSession(
                    1,
                    HostTarget.FromProfile(profile),
                    state,
                    HostChannelState.Available,
                    HostChannelState.Available,
                    stale),
                isSwitching: false)
        };

    private sealed record FakeVm(HostId HostId, string Name);

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
