using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;

internal static class HostSessionRegistryTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("HostIdentity_UsesProfileIdInsteadOfPresentation", UsesProfileIdInsteadOfPresentation),
        ("VmIdentity_ScopesVmIdToOwningHost", ScopesVmIdToOwningHost),
        ("HostRegistry_StartsWithFixedLocalSession", StartsWithFixedLocalSession),
        ("HostRegistry_ConnectsTwoRemoteHostsWithoutReplacingLocal", ConnectsTwoRemoteHostsWithoutReplacingLocal),
        ("HostRegistry_FailedConnectDoesNotPublishPartialHost", FailedConnectDoesNotPublishPartialHost),
        ("HostRegistry_ReconnectAdvancesOnlyTargetHostGeneration", ReconnectAdvancesOnlyTargetHostGeneration),
        ("HostRegistry_BasicSnapshotUsesLocaleIndependentVmSummary", BasicSnapshotUsesLocaleIndependentVmSummary),
        ("HostConnectionPage_UsesSharedRegistryWithoutLocalSwitch", HostConnectionPageUsesSharedRegistryWithoutLocalSwitch),
        ("HostConnectionPage_RefreshesDiagnosticBeforeEveryConnect", RefreshesDiagnosticBeforeEveryConnect)
    ];

    private static void UsesProfileIdInsteadOfPresentation()
    {
        Guid profileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var original = new HostProfile(profileId, "实验室主机", "10.0.0.6");
        var edited = new HostProfile(profileId, "重命名后的主机", "10.0.0.7");

        HostId first = HostId.FromProfile(original);
        HostId second = HostId.FromProfile(edited);

        TestAssert.Equal(first, second);
        TestAssert.Equal(profileId, first.ProfileId);
        TestAssert.False(first.IsLocal, "A profile-backed host was classified as local.");
        TestAssert.True(HostId.Local.IsLocal, "HostId.Local was not classified as local.");
        TestAssert.False(HostId.Local == first, "A remote host identity matched HostId.Local.");
        AssertThrows<ArgumentException>(() => HostId.FromProfileId(Guid.Empty));
    }

    private static void ScopesVmIdToOwningHost()
    {
        Guid vmId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        HostId remoteHost = HostId.FromProfileId(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        var localVm = new VmKey(HostId.Local, vmId);
        var remoteVm = new VmKey(remoteHost, vmId);
        var sameRemoteVm = new VmKey(remoteHost, vmId);

        TestAssert.False(localVm == remoteVm, "The same VM ID on different hosts produced the same key.");
        TestAssert.Equal(remoteVm, sameRemoteVm);
        TestAssert.Equal(remoteHost, remoteVm.HostId);
        TestAssert.Equal(vmId, remoteVm.VmId);
        AssertThrows<ArgumentException>(() => new VmKey(remoteHost, Guid.Empty));
    }

    private static void StartsWithFixedLocalSession()
    {
        IHostSessionRegistry registry = new HostSessionRegistry();

        HostRegistrySnapshot snapshot = registry.Current;

        TestAssert.Equal(1, snapshot.Hosts.Count);
        HostSessionSnapshot local = snapshot.Hosts[0];
        TestAssert.Equal(HostId.Local, local.HostId);
        TestAssert.True(local.Target.IsLocal, "The fixed local registry entry did not target the local computer.");
        TestAssert.Equal(1L, local.Generation);
        TestAssert.Equal(HostConnectionState.LocalConnected, local.ConnectionState);
        TestAssert.Equal(HostChannelState.Available, local.ManagementChannel);
        TestAssert.Equal(HostChannelState.Available, local.ConsoleChannel);
        TestAssert.False(local.HasStaleData, "The fixed local registry entry started with stale data.");
    }

    private static void ConnectsTwoRemoteHostsWithoutReplacingLocal()
    {
        var connector = new RegistryConnector();
        var registry = new HostSessionRegistry(connector, new RegistrySnapshotLoader());
        HostRegistrySnapshot startup = registry.Current;
        HostProfile firstProfile = Profile("33333333-3333-3333-3333-333333333333", "宿主 A", "10.0.0.6");
        HostProfile secondProfile = Profile("44444444-4444-4444-4444-444444444444", "宿主 B", "10.0.0.7");

        HostConnectResult first = registry.ConnectAsync(Request(firstProfile)).GetAwaiter().GetResult();
        HostConnectResult second = registry.ConnectAsync(Request(secondProfile)).GetAwaiter().GetResult();

        TestAssert.Equal(HostConnectStatus.Succeeded, first.Status);
        TestAssert.Equal(HostConnectStatus.Succeeded, second.Status);
        TestAssert.Equal(1, startup.Hosts.Count);
        TestAssert.Equal(3, registry.Current.Hosts.Count);
        TestAssert.Equal(HostId.Local, registry.Current.Hosts[0].HostId);
        TestAssert.Equal(HostId.FromProfile(firstProfile), registry.Current.Hosts[1].HostId);
        TestAssert.Equal(HostId.FromProfile(secondProfile), registry.Current.Hosts[2].HostId);
        TestAssert.Equal(2, connector.CallCount);
    }

    private static void ReconnectAdvancesOnlyTargetHostGeneration()
    {
        var registry = new HostSessionRegistry(
            new RegistryConnector(),
            new RegistrySnapshotLoader(),
            new ImmediateReconnectScheduler());
        HostProfile firstProfile = Profile("55555555-5555-5555-5555-555555555555", "宿主 A", "10.0.0.8");
        HostProfile secondProfile = Profile("66666666-6666-6666-6666-666666666666", "宿主 B", "10.0.0.9");
        HostId firstHostId = HostId.FromProfile(firstProfile);
        HostId secondHostId = HostId.FromProfile(secondProfile);

        try
        {
            registry.ConnectAsync(Request(firstProfile)).GetAwaiter().GetResult();
            registry.ConnectAsync(Request(secondProfile)).GetAwaiter().GetResult();
            HostRegistrySnapshot beforeLoss = registry.Current;
            HostOperationStamp staleFirstStamp = registry.CaptureOperationStamp(firstHostId);
            HostOperationStamp secondStamp = registry.CaptureOperationStamp(secondHostId);

            TestAssert.True(
                registry.ReportConnectionLoss(staleFirstStamp, "模拟宿主 A 连接丢失。"),
                "The target host did not accept its current loss stamp.");
            TestAssert.True(
                SpinWait.SpinUntil(
                    () => Session(registry.Current, firstHostId).Generation > staleFirstStamp.Generation,
                    TimeSpan.FromSeconds(5)),
                "The target host did not publish a recovered generation.");

            TestAssert.Equal(staleFirstStamp.Generation, Session(beforeLoss, firstHostId).Generation);
            TestAssert.Equal(secondStamp.Generation, Session(registry.Current, secondHostId).Generation);
            TestAssert.False(registry.CanApply(staleFirstStamp), "A stale target-host stamp remained valid after reconnect.");
            TestAssert.True(registry.CanApply(secondStamp), "Reconnect on host A invalidated host B's current stamp.");
        }
        finally
        {
            registry.Shutdown();
        }
    }

    private static void FailedConnectDoesNotPublishPartialHost()
    {
        HostProfile profile = Profile(
            "77777777-7777-7777-7777-777777777777",
            "失败宿主",
            "10.0.0.10");
        var connectionFailure = new HostSessionRegistry(
            new FailingConnector(),
            new RegistrySnapshotLoader());

        HostConnectResult failedConnect = connectionFailure.ConnectAsync(Request(profile)).GetAwaiter().GetResult();

        TestAssert.Equal(HostConnectStatus.Failed, failedConnect.Status);
        TestAssert.Equal(1, connectionFailure.Current.Hosts.Count);
        TestAssert.Equal(HostId.Local, connectionFailure.Current.Hosts[0].HostId);
        connectionFailure.Shutdown();

        var candidate = new TrackingCandidate(profile);
        var snapshotFailure = new HostSessionRegistry(
            new SingleCandidateConnector(candidate),
            new FailingSnapshotLoader());

        HostConnectResult failedSnapshot = snapshotFailure.ConnectAsync(Request(profile)).GetAwaiter().GetResult();

        TestAssert.Equal(HostConnectStatus.Failed, failedSnapshot.Status);
        TestAssert.Equal(1, snapshotFailure.Current.Hosts.Count);
        TestAssert.Equal(1, candidate.DisposeCount);
        snapshotFailure.Shutdown();
    }

    private static void BasicSnapshotUsesLocaleIndependentVmSummary()
    {
        string loader = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Services", "Remote", "Windows", "WindowsHostBasicSnapshotLoader.cs"));

        TestAssert.Contains("SELECT Name FROM Msvm_SummaryInformation", loader);
        TestAssert.False(
            loader.Contains("Caption = 'Virtual Machine'", StringComparison.Ordinal),
            "The basic snapshot still depends on a localized Msvm_ComputerSystem caption.");
    }

    private static HostSessionSnapshot Session(HostRegistrySnapshot snapshot, HostId hostId) =>
        snapshot.Hosts.Single(host => host.HostId == hostId);

    private static void HostConnectionPageUsesSharedRegistryWithoutLocalSwitch()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(root, "src", "ViewModels", "HostConnectionPageViewModel.cs"));
        string page = File.ReadAllText(Path.Combine(root, "src", "Views", "Pages", "HostConnectionPage.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(root, "src", "Views", "Pages", "HostConnectionPage.xaml.cs"));
        string app = File.ReadAllText(Path.Combine(root, "src", "App.xaml.cs"));

        TestAssert.Contains("IHostSessionRegistry _sessionRegistry", viewModel);
        TestAssert.Contains("_sessionRegistry.ConnectAsync", viewModel);
        TestAssert.Contains("_disconnectCoordinator.DisconnectAsync", viewModel);
        TestAssert.Contains("GetDisconnectAvailability", viewModel);
        TestAssert.Contains("ConnectionActionText", viewModel);
        TestAssert.Contains("\"连接到此主机\"", viewModel);
        TestAssert.Contains("\"正在连接\"", viewModel);
        TestAssert.Contains("\"断开\"", viewModel);
        TestAssert.Contains("IsEnabled=\"{Binding CanExecuteConnectionAction}\"", page);
        TestAssert.Contains("ToolTip=\"{Binding ConnectionActionToolTip}\"", page);
        TestAssert.Contains("Text=\"{Binding ConnectionActionText}\"", page);
        TestAssert.False(viewModel.Contains("SwitchToSelectedAsync(", StringComparison.Ordinal),
            "The host connection page still switches a global active host.");
        TestAssert.False(viewModel.Contains("SwitchToLocalAsync(", StringComparison.Ordinal),
            "The host connection page still implements switch-to-local.");
        TestAssert.False(page.Contains("SwitchToLocalCommand", StringComparison.Ordinal),
            "The host connection page still renders a switch-to-local command.");
        TestAssert.False(viewModel.Contains("当前活动宿主", StringComparison.Ordinal),
            "The connection confirmation still describes a global active-host switch.");
        TestAssert.False(page.Contains("正在切换", StringComparison.Ordinal),
            "The connection button still describes connecting as a host switch.");
        TestAssert.Contains("HostSessions.Registry", codeBehind);
        TestAssert.Contains("HostSessions.Registry.Shutdown()", app);
        int selectionUpdateStart = viewModel.IndexOf(
            "private void OnSelectionPropertiesChanged()",
            StringComparison.Ordinal);
        int selectionUpdateEnd = viewModel.IndexOf(
            "private HostRepairDecision CurrentRepairDecision",
            selectionUpdateStart,
            StringComparison.Ordinal);
        TestAssert.True(
            selectionUpdateStart >= 0 && selectionUpdateEnd > selectionUpdateStart,
            "The selected-host property update method could not be located.");
        TestAssert.Contains(
            "UpdateSelectedHostProperties();",
            viewModel[selectionUpdateStart..selectionUpdateEnd]);
    }

    private static void RefreshesDiagnosticBeforeEveryConnect()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(root, "src", "ViewModels", "HostConnectionPageViewModel.cs"));
        string page = File.ReadAllText(Path.Combine(root, "src", "Views", "Pages", "HostConnectionPage.xaml"));

        string connectEligibility = Slice(
            viewModel,
            "public bool CanConnectToSelectedHost =>",
            "public bool CanExecuteConnectionAction");
        TestAssert.False(
            connectEligibility.Contains("GetCurrentReport", StringComparison.Ordinal),
            "A previous diagnostic still gates whether the connection action can run.");

        string connectMethod = Slice(
            viewModel,
            "private async Task ConnectToSelectedHostAsync()",
            "private async Task DisconnectSelectedHostAsync");
        string diagnoseMethod = Slice(
            viewModel,
            "private async Task DiagnoseSelectedHostAsync()",
            "private void CancelDiagnostics()");
        int reportInvalidationIndex = diagnoseMethod.IndexOf("_reports.Remove(profile.Id);", StringComparison.Ordinal);
        int missingCredentialIndex = diagnoseMethod.IndexOf("profile.CredentialTarget is null", StringComparison.Ordinal);
        int diagnosticIndex = connectMethod.IndexOf("await DiagnoseSelectedHostAsync();", StringComparison.Ordinal);
        int reportIndex = connectMethod.IndexOf("GetCurrentReport(profile)", StringComparison.Ordinal);
        int confirmationIndex = connectMethod.IndexOf("Dialogs.ShowConfirmAsync", StringComparison.Ordinal);
        int registryIndex = connectMethod.IndexOf("_sessionRegistry.ConnectAsync", StringComparison.Ordinal);
        TestAssert.True(diagnosticIndex >= 0, "The connection action does not run the diagnostic pipeline.");
        TestAssert.True(
            reportInvalidationIndex >= 0 && reportInvalidationIndex < missingCredentialIndex,
            "A missing explicit credential can return before invalidating the previous diagnostic report.");
        TestAssert.Contains("SelectedHost?.ResetReport();", diagnoseMethod);
        TestAssert.True(
            diagnosticIndex < reportIndex
            && reportIndex < confirmationIndex
            && confirmationIndex < registryIndex,
            "Connection must diagnose, validate the fresh report, confirm, and only then publish a host session.");
        TestAssert.Contains("IsEnabled=\"{Binding CanDiagnoseSelectedHost}\"", page);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        TestAssert.True(start >= 0 && end > start, $"Could not locate source slice {startMarker}.");
        return source[start..end];
    }

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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static HostConnectRequest Request(HostProfile profile) => new(
        profile,
        HostChannelState.Available,
        HostChannelState.Available);

    private static HostProfile Profile(string id, string name, string address) =>
        new(Guid.Parse(id), name, address);

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
    }

    private sealed class RegistryConnection : IHostManagementConnection;

    private sealed class RegistryCandidate(HostProfile profile) : IHostSessionCandidate
    {
        public HostTarget Target { get; } = HostTarget.FromProfile(profile);
        public IHostManagementConnection ManagementConnection { get; } = new RegistryConnection();
        public HostChannelState ManagementChannel => HostChannelState.Available;
        public HostChannelState ConsoleChannel => HostChannelState.Available;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RegistryConnector : IHostSessionConnector
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<IHostSessionCandidate> ConnectAsync(
            HostSwitchRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult<IHostSessionCandidate>(new RegistryCandidate(request.Profile));
        }
    }

    private sealed class RegistrySnapshotLoader : IHostBasicSnapshotLoader
    {
        public Task<HostBasicSnapshot> LoadAsync(
            IHostSessionCandidate candidate,
            CancellationToken cancellationToken) => Task.FromResult(new HostBasicSnapshot(
                candidate.Target.DisplayName,
                "Windows",
                "Running",
                1,
                new DateTimeOffset(2026, 8, 15, 18, 0, 0, TimeSpan.FromHours(8))));
    }

    private sealed class FailingConnector : IHostSessionConnector
    {
        public Task<IHostSessionCandidate> ConnectAsync(
            HostSwitchRequest request,
            CancellationToken cancellationToken) =>
            throw new HostSwitchException("模拟连接失败。");
    }

    private sealed class SingleCandidateConnector(TrackingCandidate candidate) : IHostSessionConnector
    {
        public Task<IHostSessionCandidate> ConnectAsync(
            HostSwitchRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<IHostSessionCandidate>(candidate);
    }

    private sealed class TrackingCandidate(HostProfile profile) : IHostSessionCandidate
    {
        public HostTarget Target { get; } = HostTarget.FromProfile(profile);
        public IHostManagementConnection ManagementConnection { get; } = new RegistryConnection();
        public HostChannelState ManagementChannel => HostChannelState.Available;
        public HostChannelState ConsoleChannel => HostChannelState.Available;
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingSnapshotLoader : IHostBasicSnapshotLoader
    {
        public Task<HostBasicSnapshot> LoadAsync(
            IHostSessionCandidate candidate,
            CancellationToken cancellationToken) =>
            throw new HostSwitchException("模拟快照失败。");
    }

    private sealed class ImmediateReconnectScheduler : IReconnectScheduler
    {
        public DateTimeOffset UtcNow => new(2026, 8, 15, 18, 0, 0, TimeSpan.FromHours(8));

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
