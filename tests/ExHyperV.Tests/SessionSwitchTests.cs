using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Tools;

internal static class SessionSwitchTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Switch_SuccessPublishesOneCompleteNewGeneration", SuccessPublishesOneCompleteNewGeneration),
        ("Switch_ConnectionFailureKeepsOldSession", ConnectionFailureKeepsOldSession),
        ("Switch_SnapshotFailureKeepsOldSessionAndDisposesCandidate", SnapshotFailureKeepsOldSessionAndDisposesCandidate),
        ("Switch_ActiveWriteBlocksBeforeConnection", ActiveWriteBlocksBeforeConnection),
        ("Switch_FreezeRejectsNewWritesDuringPreparation", FreezeRejectsNewWritesDuringPreparation),
        ("Switch_SelectionChangeRejectsStaleCandidate", SelectionChangeRejectsStaleCandidate),
        ("Switch_ProfileEditRejectsStaleCandidate", ProfileEditRejectsStaleCandidate),
        ("Switch_CancellationKeepsOldSession", CancellationKeepsOldSession),
        ("Switch_OldGenerationStampCannotApplyAfterCommit", OldGenerationStampCannotApplyAfterCommit),
        ("Switch_UserCanExplicitlyReturnToLocal", UserCanExplicitlyReturnToLocal),
        ("Switch_WmiContextsIsolateIdentityCacheKeys", WmiContextsIsolateIdentityCacheKeys),
        ("Switch_HostSelectionIsDisabledDuringSwitch", HostSelectionIsDisabledDuringSwitch),
        ("Switch_BasicSnapshotUsesLocaleIndependentVmSummary", BasicSnapshotUsesLocaleIndependentVmSummary),
        ("Ui_RemoteHostUsesSharedSymbolsAndThemeResources", RemoteHostUsesSharedSymbolsAndThemeResources)
    ];

    private static void SuccessPublishesOneCompleteNewGeneration()
    {
        var profile = Profile("10.0.0.6");
        var candidate = new FakeCandidate(profile, HostChannelState.Available, HostChannelState.Unavailable);
        ActiveHostSessionCoordinator? coordinator = null;
        var connector = new FakeConnector((_, _) =>
        {
            TestAssert.True(coordinator!.IsWriteFrozen, "Writes must be frozen before connection starts.");
            TestAssert.True(coordinator.Current.ActiveSession.Target.IsLocal, "Candidate connection exposed a partial active host.");
            return Task.FromResult<IHostSessionCandidate>(candidate);
        });
        var expectedSnapshot = Snapshot(profile, 3);
        var loader = new FakeSnapshotLoader((_, _) =>
        {
            TestAssert.True(coordinator!.Current.ActiveSession.Target.IsLocal, "Snapshot loading exposed a partial active host.");
            return Task.FromResult(expectedSnapshot);
        });
        coordinator = new ActiveHostSessionCoordinator(connector, loader);
        coordinator.SelectProfile(profile);
        var changes = new List<ActiveHostStateChangedEventArgs>();
        coordinator.StateChanged += (_, change) => changes.Add(change);

        HostSwitchResult result = coordinator.SwitchToSelectedAsync(Request(profile)).GetAwaiter().GetResult();

        TestAssert.Equal(HostSwitchStatus.Succeeded, result.Status);
        TestAssert.Equal(2, changes.Count);
        TestAssert.Equal(
            HostCapabilityReasonCode.HostSwitchInProgress,
            changes[0].Current.Capabilities[HostCapabilityKind.VmWrite].ReasonCode);
        TestAssert.Equal(1L, changes[0].Current.ActiveSession.Generation);
        TestAssert.True(changes[1].Previous.ActiveSession.Target.IsLocal, "The switch event did not start from the old host.");
        TestAssert.Equal(profile.Id, changes[1].Current.ActiveSession.Target.ProfileId);
        TestAssert.Equal(2L, changes[1].Current.ActiveSession.Generation);
        TestAssert.Equal(HostConnectionState.PartiallyAvailable, changes[1].Current.ActiveSession.ConnectionState);
        TestAssert.Equal(expectedSnapshot, changes[1].Current.BasicSnapshot);
        TestAssert.Equal(
            HostCapabilityReasonCode.None,
            changes[1].Current.Capabilities[HostCapabilityKind.VmWrite].ReasonCode);
        TestAssert.Equal(0, candidate.DisposeCount);
        TestAssert.False(coordinator.IsWriteFrozen, "Write freeze was not released after success.");
    }

    private static void ConnectionFailureKeepsOldSession()
    {
        var profile = Profile("10.0.0.7");
        var connector = new FakeConnector((_, _) => throw new HostSwitchException("模拟连接失败。"));
        var loader = new FakeSnapshotLoader((_, _) => throw new InvalidOperationException("Must not load."));
        var coordinator = new ActiveHostSessionCoordinator(connector, loader);
        coordinator.SelectProfile(profile);
        ActiveHostCoordinatorSnapshot before = coordinator.Current;

        HostSwitchResult result = coordinator.SwitchToSelectedAsync(Request(profile)).GetAwaiter().GetResult();

        TestAssert.Equal(HostSwitchStatus.Failed, result.Status);
        TestAssert.Equal(before, coordinator.Current);
        TestAssert.Equal(0, loader.CallCount);
        TestAssert.False(coordinator.IsWriteFrozen, "Write freeze remained after connection failure.");
    }

    private static void SnapshotFailureKeepsOldSessionAndDisposesCandidate()
    {
        var profile = Profile("10.0.0.8");
        var candidate = new FakeCandidate(profile);
        var coordinator = new ActiveHostSessionCoordinator(
            new FakeConnector((_, _) => Task.FromResult<IHostSessionCandidate>(candidate)),
            new FakeSnapshotLoader((_, _) => throw new HostSwitchException("模拟快照失败。")));
        coordinator.SelectProfile(profile);
        ActiveHostCoordinatorSnapshot before = coordinator.Current;

        HostSwitchResult result = coordinator.SwitchToSelectedAsync(Request(profile)).GetAwaiter().GetResult();

        TestAssert.Equal(HostSwitchStatus.Failed, result.Status);
        TestAssert.Equal(before, coordinator.Current);
        TestAssert.Equal(1, candidate.DisposeCount);
    }

    private static void ActiveWriteBlocksBeforeConnection()
    {
        var profile = Profile("10.0.0.9");
        var connector = new FakeConnector((_, _) => throw new InvalidOperationException("Must not connect."));
        var coordinator = new ActiveHostSessionCoordinator(connector, new FakeSnapshotLoader(SucceedSnapshot));
        coordinator.SelectProfile(profile);
        TestAssert.True(coordinator.TryBeginWrite(out IHostWriteLease? write, out string reason), reason);
        using (write)
        {
            HostSwitchResult result = coordinator.SwitchToSelectedAsync(Request(profile)).GetAwaiter().GetResult();
            TestAssert.Equal(HostSwitchStatus.BlockedByActiveWrites, result.Status);
            TestAssert.Equal(0, connector.CallCount);
            TestAssert.True(coordinator.Current.ActiveSession.Target.IsLocal, "Blocked switch changed the active host.");
        }
    }

    private static void FreezeRejectsNewWritesDuringPreparation()
    {
        var profile = Profile("10.0.0.10");
        ActiveHostSessionCoordinator? coordinator = null;
        var candidate = new FakeCandidate(profile);
        var connector = new FakeConnector((_, _) =>
        {
            TestAssert.False(
                coordinator!.TryBeginWrite(out IHostWriteLease? rejected, out string reason),
                "A new write was accepted while switching was frozen.");
            TestAssert.Null(rejected, "Rejected write returned a lease.");
            TestAssert.Contains("切换", reason);
            return Task.FromResult<IHostSessionCandidate>(candidate);
        });
        coordinator = new ActiveHostSessionCoordinator(connector, new FakeSnapshotLoader(SucceedSnapshot));
        coordinator.SelectProfile(profile);

        HostSwitchResult result = coordinator.SwitchToSelectedAsync(Request(profile)).GetAwaiter().GetResult();

        TestAssert.Equal(HostSwitchStatus.Succeeded, result.Status);
        TestAssert.True(coordinator.TryBeginWrite(out IHostWriteLease? lease, out string afterReason), afterReason);
        lease!.Dispose();
    }

    private static void SelectionChangeRejectsStaleCandidate()
    {
        var original = Profile("10.0.0.11");
        var other = Profile("10.0.0.12");
        var candidate = new FakeCandidate(original);
        ActiveHostSessionCoordinator? coordinator = null;
        var loader = new FakeSnapshotLoader((_, _) =>
        {
            coordinator!.SelectProfile(other);
            return Task.FromResult(Snapshot(original, 2));
        });
        coordinator = new ActiveHostSessionCoordinator(
            new FakeConnector((_, _) => Task.FromResult<IHostSessionCandidate>(candidate)),
            loader);
        coordinator.SelectProfile(original);

        HostSwitchResult result = coordinator.SwitchToSelectedAsync(Request(original)).GetAwaiter().GetResult();

        TestAssert.Equal(HostSwitchStatus.StaleSelection, result.Status);
        TestAssert.True(coordinator.Current.ActiveSession.Target.IsLocal, "Stale candidate replaced the old host.");
        TestAssert.Equal(other.Id, coordinator.Current.SelectedProfile?.Id);
        TestAssert.Equal(1, candidate.DisposeCount);
    }

    private static void ProfileEditRejectsStaleCandidate()
    {
        var original = Profile("10.0.0.21");
        var edited = original with { Address = "10.0.0.22", DisplayName = "编辑后的远程宿主" };
        var candidate = new FakeCandidate(original);
        ActiveHostSessionCoordinator? coordinator = null;
        var loader = new FakeSnapshotLoader((_, _) =>
        {
            coordinator!.SelectProfile(edited);
            return Task.FromResult(Snapshot(original, 2));
        });
        coordinator = new ActiveHostSessionCoordinator(
            new FakeConnector((_, _) => Task.FromResult<IHostSessionCandidate>(candidate)),
            loader);
        coordinator.SelectProfile(original);

        HostSwitchResult result = coordinator.SwitchToSelectedAsync(Request(original)).GetAwaiter().GetResult();

        TestAssert.Equal(HostSwitchStatus.StaleSelection, result.Status);
        TestAssert.True(coordinator.Current.ActiveSession.Target.IsLocal, "A candidate from the old profile configuration became active.");
        TestAssert.Equal(edited, coordinator.Current.SelectedProfile);
        TestAssert.Equal(1, candidate.DisposeCount);
    }

    private static void CancellationKeepsOldSession()
    {
        var profile = Profile("10.0.0.13");
        var candidate = new FakeCandidate(profile);
        using var cancellation = new CancellationTokenSource();
        var loader = new FakeSnapshotLoader((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var coordinator = new ActiveHostSessionCoordinator(
            new FakeConnector((_, _) => Task.FromResult<IHostSessionCandidate>(candidate)),
            loader);
        coordinator.SelectProfile(profile);
        ActiveHostCoordinatorSnapshot before = coordinator.Current;

        HostSwitchResult result = coordinator.SwitchToSelectedAsync(Request(profile), cancellation.Token).GetAwaiter().GetResult();

        TestAssert.Equal(HostSwitchStatus.Cancelled, result.Status);
        TestAssert.Equal(before, coordinator.Current);
        TestAssert.Equal(1, candidate.DisposeCount);
        TestAssert.False(coordinator.IsWriteFrozen, "Write freeze remained after cancellation.");
    }

    private static void OldGenerationStampCannotApplyAfterCommit()
    {
        var profile = Profile("10.0.0.14");
        var coordinator = Coordinator(profile);
        HostOperationStamp oldStamp = coordinator.CaptureOperationStamp();
        coordinator.SelectProfile(profile);

        HostSwitchResult result = coordinator.SwitchToSelectedAsync(Request(profile)).GetAwaiter().GetResult();

        TestAssert.Equal(HostSwitchStatus.Succeeded, result.Status);
        TestAssert.False(coordinator.CanApply(oldStamp), "Old generation result could apply to the new active host.");
        TestAssert.True(coordinator.CanApply(coordinator.CaptureOperationStamp()), "Current generation result was rejected.");
    }

    private static void UserCanExplicitlyReturnToLocal()
    {
        var profile = Profile("10.0.0.15");
        var candidate = new FakeCandidate(profile);
        var coordinator = new ActiveHostSessionCoordinator(
            new FakeConnector((_, _) => Task.FromResult<IHostSessionCandidate>(candidate)),
            new FakeSnapshotLoader(SucceedSnapshot));
        coordinator.SelectProfile(profile);
        TestAssert.Equal(
            HostSwitchStatus.Succeeded,
            coordinator.SwitchToSelectedAsync(Request(profile)).GetAwaiter().GetResult().Status);

        HostSwitchResult result = coordinator.SwitchToLocalAsync().GetAwaiter().GetResult();

        TestAssert.Equal(HostSwitchStatus.Succeeded, result.Status);
        TestAssert.True(coordinator.Current.ActiveSession.Target.IsLocal, "Explicit local switch did not activate local host.");
        TestAssert.Equal(3L, coordinator.Current.ActiveSession.Generation);
        TestAssert.Null(coordinator.Current.SelectedProfile, "Local switch did not clear selected remote profile.");
        TestAssert.Equal(1, candidate.DisposeCount);
    }

    private static void WmiContextsIsolateIdentityCacheKeys()
    {
        WmiContext currentIdentity = WmiContext.RemoteCurrentWindowsIdentity("10.0.0.6");
        WmiContext firstCredential = WmiContext.Remote("10.0.0.6", "LAB\\Admin", "secret-one");
        WmiContext secondCredential = WmiContext.Remote("10.0.0.6", "LAB\\Admin", "secret-two");

        TestAssert.True(currentIdentity.UsesCurrentWindowsIdentity, "Remote current identity was not preserved.");
        TestAssert.Null(currentIdentity.Username, "Current identity unexpectedly set a WMI username.");
        TestAssert.Null(currentIdentity.Password, "Current identity unexpectedly set a WMI password.");
        TestAssert.False(firstCredential.UsesCurrentWindowsIdentity, "Explicit identity was classified as current identity.");
        TestAssert.False(
            firstCredential.IdentityContextId == secondCredential.IdentityContextId,
            "Two explicit sessions for the same host reused an identity context.");
        TestAssert.False(
            firstCredential.IdentityContextId.Contains(firstCredential.Password!, StringComparison.Ordinal),
            "Identity context ID contains a password.");
    }

    private static ActiveHostSessionCoordinator Coordinator(HostProfile profile) =>
        new(
            new FakeConnector((_, _) => Task.FromResult<IHostSessionCandidate>(new FakeCandidate(profile))),
            new FakeSnapshotLoader(SucceedSnapshot));

    private static Task<HostBasicSnapshot> SucceedSnapshot(IHostSessionCandidate candidate, CancellationToken _) =>
        Task.FromResult(Snapshot(candidate.Target, 2));

    private static HostSwitchRequest Request(HostProfile profile) =>
        new(profile, HostChannelState.Available, HostChannelState.Available);

    private static HostProfile Profile(string address) =>
        new(Guid.NewGuid(), "远程宿主", address);

    private static HostBasicSnapshot Snapshot(HostProfile profile, int vmCount) =>
        Snapshot(HostTarget.FromProfile(profile), vmCount);

    private static HostBasicSnapshot Snapshot(HostTarget target, int vmCount) =>
        new(target.DisplayName, "Windows", "Running", vmCount, new DateTimeOffset(2026, 8, 13, 21, 0, 0, TimeSpan.FromHours(8)));

    private static void HostSelectionIsDisabledDuringSwitch()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Views", "Pages", "HostConnectionPage.xaml"));
        int listStart = xaml.IndexOf("<ListBox x:Name=\"HostList\"", StringComparison.Ordinal);
        TestAssert.True(listStart >= 0, "Could not locate the remote host selector.");
        string declaration = xaml.Substring(listStart, Math.Min(800, xaml.Length - listStart));
        TestAssert.Contains(
            "IsEnabled=\"{Binding IsSwitching, Converter={StaticResource InverseBooleanConverter}}\"",
            declaration);
    }

    private static void RemoteHostUsesSharedSymbolsAndThemeResources()
    {
        string root = FindRepositoryRoot();
        string page = File.ReadAllText(Path.Combine(root, "src", "Views", "Pages", "HostConnectionPage.xaml"));
        string navigation = File.ReadAllText(Path.Combine(root, "src", "Views", "Windows", "MainWindow.xaml"));
        string brushes = File.ReadAllText(Path.Combine(root, "src", "ViewModels", "UiStatusBrushes.cs"));

        TestAssert.Contains("<ui:SymbolIcon Symbol=\"Server24\"", page);
        TestAssert.Contains("<ui:SymbolIcon Symbol=\"Server24\"", navigation);
        TestAssert.Contains("Foreground=\"{ui:ThemeResource TextFillColorPrimaryBrush}\"", page);
        TestAssert.False(
            page.Contains("Foreground=\"{DynamicResource TextFillColorPrimaryBrush}\"", StringComparison.Ordinal),
            "The remote page title still uses the non-theme-aware foreground resource.");
        int navigationItemStart = navigation.IndexOf("Content=\"主机连接\"", StringComparison.Ordinal);
        TestAssert.True(navigationItemStart >= 0, "Could not locate the remote host navigation item.");
        string navigationItem = navigation.Substring(
            navigationItemStart,
            Math.Min(500, navigation.Length - navigationItemStart));
        TestAssert.False(
            navigationItem.Contains("Foreground=", StringComparison.Ordinal),
            "The remote navigation item overrides the NavigationView selected-state foreground.");
        TestAssert.False(page.Contains("&#xE968;", StringComparison.Ordinal), "The remote page still uses the legacy glyph icon.");
        TestAssert.False(navigation.Contains("Glyph=\"&#xE968;\"", StringComparison.Ordinal), "The remote navigation item still uses the legacy glyph icon.");
        TestAssert.Contains("Glyph=\"{Binding StatusIconGlyph}\"", page);
        TestAssert.Contains("Visibility=\"{Binding StatusUsesDot, Converter={StaticResource BoolToVis}}\"", page);
        TestAssert.Contains("Visibility=\"{Binding ManagementStatusUsesDot, Converter={StaticResource BoolToVis}}\"", page);
        TestAssert.Contains("ToolTip=\"{Binding ConnectionActionToolTip}\"", page);
        TestAssert.False(page.Contains("Text=\"{Binding ConnectHint}\"", StringComparison.Ordinal), "The connection hint still changes the action-row height.");
        TestAssert.Contains("SystemFillColorSuccessBrush", brushes);
        TestAssert.Contains("SystemFillColorCautionBrush", brushes);
        TestAssert.Contains("SystemFillColorCriticalBrush", brushes);
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

    private sealed class FakeConnection : IHostManagementConnection;

    private sealed class FakeCandidate(
        HostProfile profile,
        HostChannelState management = HostChannelState.Available,
        HostChannelState console = HostChannelState.Available) : IHostSessionCandidate
    {
        public HostTarget Target { get; } = HostTarget.FromProfile(profile);
        public IHostManagementConnection ManagementConnection { get; } = new FakeConnection();
        public HostChannelState ManagementChannel { get; } = management;
        public HostChannelState ConsoleChannel { get; } = console;
        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeConnector(
        Func<HostSwitchRequest, CancellationToken, Task<IHostSessionCandidate>> connect) : IHostSessionConnector
    {
        public int CallCount { get; private set; }
        public Task<IHostSessionCandidate> ConnectAsync(HostSwitchRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return connect(request, cancellationToken);
        }
    }

    private sealed class FakeSnapshotLoader(
        Func<IHostSessionCandidate, CancellationToken, Task<HostBasicSnapshot>> load) : IHostBasicSnapshotLoader
    {
        public int CallCount { get; private set; }
        public Task<HostBasicSnapshot> LoadAsync(IHostSessionCandidate candidate, CancellationToken cancellationToken)
        {
            CallCount++;
            return load(candidate, cancellationToken);
        }
    }
}
