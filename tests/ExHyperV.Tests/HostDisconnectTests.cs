using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Services.Remote.Consoles;

internal static class HostDisconnectTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Disconnect_ActiveWriteBlocksOnlyOwningHost", ActiveWriteBlocksOnlyOwningHost),
        ("Disconnect_PreparationFreezesNewWritesAndCancelRestores", PreparationFreezesNewWritesAndCancelRestores),
        ("Disconnect_CommitRemovesOnlyTargetAndReleasesSession", CommitRemovesOnlyTargetAndReleasesSession),
        ("DisconnectWorkflow_NoConsoleDisconnectsWithoutConfirmation", NoConsoleDisconnectsWithoutConfirmation),
        ("DisconnectWorkflow_CancelHasZeroSideEffects", CancelHasZeroSideEffects),
        ("DisconnectWorkflow_ConfirmationClosesOnlyTargetHost", ConfirmationClosesOnlyTargetHost),
        ("DisconnectWorkflow_WriteRaceStopsBeforeClosingConsoles", WriteRaceStopsBeforeClosingConsoles),
        ("DisconnectWorkflow_CloseFailureKeepsSessionAndUnfreezesWrites", CloseFailureKeepsSessionAndUnfreezesWrites)
    ];

    private static void ActiveWriteBlocksOnlyOwningHost()
    {
        using var fixture = new DisconnectFixture();
        fixture.ConnectBoth();
        int targetChanges = 0;
        fixture.Registry.Changed += (_, change) =>
        {
            if (change.ChangedHostId == fixture.HostA) targetChanges++;
        };

        TestAssert.True(
            fixture.Registry.TryBeginWrite(fixture.HostA, out IHostWriteLease? lease, out string writeReason),
            writeReason);
        HostDisconnectAvailability blocked = fixture.Registry.GetDisconnectAvailability(fixture.HostA);
        HostDisconnectAvailability other = fixture.Registry.GetDisconnectAvailability(fixture.HostB);

        TestAssert.False(blocked.CanDisconnect, "A host with an active write was disconnectable.");
        TestAssert.Equal(1, blocked.ActiveWriteCount);
        TestAssert.Contains("1 个写操作", blocked.Reason);
        TestAssert.Equal(1, Session(fixture.Registry, fixture.HostA).ActiveWriteCount);
        TestAssert.True(other.CanDisconnect, other.Reason);
        TestAssert.Equal(0, other.ActiveWriteCount);

        IHostWriteLease acquiredLease = lease
            ?? throw new InvalidOperationException("A successful write acquisition returned no lease.");
        Parallel.Invoke(acquiredLease.Dispose, acquiredLease.Dispose);

        HostDisconnectAvailability released = fixture.Registry.GetDisconnectAvailability(fixture.HostA);
        TestAssert.True(released.CanDisconnect, released.Reason);
        TestAssert.Equal(0, Session(fixture.Registry, fixture.HostA).ActiveWriteCount);
        TestAssert.True(targetChanges >= 2, "Write begin and end did not publish target-host snapshot changes.");
    }

    private static void PreparationFreezesNewWritesAndCancelRestores()
    {
        using var fixture = new DisconnectFixture();
        fixture.ConnectBoth();

        TestAssert.True(
            fixture.Registry.TryPrepareDisconnect(
                fixture.HostA,
                out IHostDisconnectPreparation? preparation,
                out string prepareReason),
            prepareReason);
        using (preparation)
        {
            TestAssert.False(
                fixture.Registry.TryBeginWrite(fixture.HostA, out IHostWriteLease? blockedLease, out string blockedReason),
                "A write started after the host entered disconnect preparation.");
            TestAssert.Null(blockedLease, "A blocked write returned a lease.");
            TestAssert.Contains("正在断开", blockedReason);
            TestAssert.False(
                fixture.Registry.TryCaptureConsoleOperation(
                    fixture.HostA,
                    out HostConsoleOperationContext? blockedConsole,
                    out string consoleReason),
                "A console opened after the host entered disconnect preparation.");
            TestAssert.Null(blockedConsole, "A blocked console capture returned an operation context.");
            TestAssert.Contains("正在断开", consoleReason);

            TestAssert.True(
                fixture.Registry.TryBeginWrite(fixture.HostB, out IHostWriteLease? otherLease, out string otherReason),
                otherReason);
            otherLease!.Dispose();
        }

        TestAssert.True(fixture.Registry.Current.TryGet(fixture.HostA, out _),
            "Cancelling disconnect preparation removed the host.");
        TestAssert.True(
            fixture.Registry.TryBeginWrite(fixture.HostA, out IHostWriteLease? restoredLease, out string restoredReason),
            restoredReason);
        restoredLease!.Dispose();
    }

    private static void CommitRemovesOnlyTargetAndReleasesSession()
    {
        using var fixture = new DisconnectFixture();
        fixture.ConnectBoth();

        TestAssert.True(
            fixture.Registry.TryPrepareDisconnect(
                fixture.HostA,
                out IHostDisconnectPreparation? preparation,
                out string prepareReason),
            prepareReason);
        using (preparation)
        {
            HostDisconnectResult result = preparation!.Commit();
            TestAssert.True(result.Succeeded, result.Message);
            TestAssert.Equal(fixture.HostA, result.HostId);
        }

        TestAssert.False(fixture.Registry.Current.TryGet(fixture.HostA, out _),
            "Committed disconnect left the target host in the registry.");
        TestAssert.True(fixture.Registry.Current.TryGet(HostId.Local, out _),
            "Committed remote disconnect removed the fixed local host.");
        TestAssert.True(fixture.Registry.Current.TryGet(fixture.HostB, out _),
            "Committed disconnect removed another remote host.");
        TestAssert.Equal(1, fixture.Connector.DisposeCount(fixture.ProfileA.Id));
        TestAssert.Equal(0, fixture.Connector.DisposeCount(fixture.ProfileB.Id));
        TestAssert.False(fixture.Registry.RetryReconnectNow(fixture.HostA),
            "A disconnected host still accepted reconnect commands.");
    }

    private static void NoConsoleDisconnectsWithoutConfirmation()
    {
        using var fixture = new DisconnectFixture();
        fixture.ConnectBoth();
        var workflow = new HostDisconnectCoordinator(fixture.Registry, new HostConsoleRegistry());

        HostDisconnectWorkflowResult result = workflow.DisconnectAsync(
            fixture.HostA,
            fixture.ProfileA.DisplayName,
            (_, _) => throw new InvalidOperationException("A no-console disconnect requested confirmation."))
            .GetAwaiter().GetResult();

        TestAssert.True(result.Succeeded, result.Message);
        TestAssert.False(fixture.Registry.Current.TryGet(fixture.HostA, out _),
            "A no-console disconnect left the host connected.");
        TestAssert.True(fixture.Registry.Current.TryGet(fixture.HostB, out _),
            "A no-console disconnect removed another host.");
    }

    private static void CancelHasZeroSideEffects()
    {
        using var fixture = new DisconnectFixture();
        fixture.ConnectBoth();
        var consoles = new HostConsoleRegistry();
        var window = new FakeConsoleWindow();
        ActiveHostConsoleSession session = ConsoleSession(fixture.ProfileA, VmA1);
        consoles.Register(session, window);
        var workflow = new HostDisconnectCoordinator(fixture.Registry, consoles);

        HostDisconnectWorkflowResult result = workflow.DisconnectAsync(
            fixture.HostA,
            fixture.ProfileA.DisplayName,
            (_, _) => Task.FromResult(false)).GetAwaiter().GetResult();

        TestAssert.True(result.Cancelled, "Cancelling the prompt did not return a cancelled result.");
        TestAssert.True(fixture.Registry.Current.TryGet(fixture.HostA, out _),
            "Cancelling disconnect removed the host.");
        TestAssert.Equal(1, consoles.Count(fixture.HostA));
        TestAssert.Equal(0, window.CloseCount);
        TestAssert.True(
            fixture.Registry.TryBeginWrite(fixture.HostA, out IHostWriteLease? lease, out string reason),
            reason);
        lease!.Dispose();
    }

    private static void ConfirmationClosesOnlyTargetHost()
    {
        using var fixture = new DisconnectFixture();
        fixture.ConnectBoth();
        var consoles = new HostConsoleRegistry();
        var windowA1 = new FakeConsoleWindow();
        var windowA2 = new FakeConsoleWindow();
        var windowB = new FakeConsoleWindow();
        consoles.Register(ConsoleSession(fixture.ProfileA, VmA1), windowA1);
        consoles.Register(ConsoleSession(fixture.ProfileA, VmA2), windowA2);
        consoles.Register(ConsoleSession(fixture.ProfileB, VmA1), windowB);
        var workflow = new HostDisconnectCoordinator(fixture.Registry, consoles);
        HostDisconnectPrompt? observedPrompt = null;

        HostDisconnectWorkflowResult result = workflow.DisconnectAsync(
            fixture.HostA,
            fixture.ProfileA.DisplayName,
            (prompt, _) =>
            {
                observedPrompt = prompt;
                return Task.FromResult(true);
            }).GetAwaiter().GetResult();

        TestAssert.True(result.Succeeded, result.Message);
        TestAssert.Equal(2, observedPrompt!.ConsoleCount);
        TestAssert.Contains(fixture.ProfileA.DisplayName, observedPrompt.Message);
        TestAssert.Contains("2 个控制台窗口", observedPrompt.Message);
        TestAssert.Equal(1, windowA1.CloseCount);
        TestAssert.Equal(1, windowA2.CloseCount);
        TestAssert.Equal(0, windowB.CloseCount);
        TestAssert.Equal(1, consoles.Count(fixture.HostB));
        TestAssert.True(fixture.Registry.Current.TryGet(fixture.HostB, out _),
            "Confirmed disconnect changed another host.");
    }

    private static void WriteRaceStopsBeforeClosingConsoles()
    {
        using var fixture = new DisconnectFixture();
        fixture.ConnectBoth();
        var consoles = new HostConsoleRegistry();
        var window = new FakeConsoleWindow();
        consoles.Register(ConsoleSession(fixture.ProfileA, VmA1), window);
        var workflow = new HostDisconnectCoordinator(fixture.Registry, consoles);
        IHostWriteLease? racingLease = null;

        HostDisconnectWorkflowResult result = workflow.DisconnectAsync(
            fixture.HostA,
            fixture.ProfileA.DisplayName,
            (_, _) =>
            {
                TestAssert.True(
                    fixture.Registry.TryBeginWrite(fixture.HostA, out racingLease, out string reason),
                    reason);
                return Task.FromResult(true);
            }).GetAwaiter().GetResult();
        try
        {
            TestAssert.False(result.Succeeded, "A disconnect committed after a write started during confirmation.");
            TestAssert.False(result.Cancelled, "A write race was reported as user cancellation.");
            TestAssert.Contains("写操作", result.Message);
            TestAssert.Equal(0, window.CloseCount);
            TestAssert.Equal(1, consoles.Count(fixture.HostA));
            TestAssert.True(fixture.Registry.Current.TryGet(fixture.HostA, out _),
                "A write race removed the target host.");
        }
        finally
        {
            racingLease?.Dispose();
        }
    }

    private static void CloseFailureKeepsSessionAndUnfreezesWrites()
    {
        using var fixture = new DisconnectFixture();
        fixture.ConnectBoth();
        var consoles = new HostConsoleRegistry();
        var window = new FakeConsoleWindow { ThrowOnClose = true };
        consoles.Register(ConsoleSession(fixture.ProfileA, VmA1), window);
        var workflow = new HostDisconnectCoordinator(fixture.Registry, consoles);

        HostDisconnectWorkflowResult result = workflow.DisconnectAsync(
            fixture.HostA,
            fixture.ProfileA.DisplayName,
            (_, _) => Task.FromResult(true)).GetAwaiter().GetResult();

        TestAssert.Equal(HostDisconnectWorkflowStatus.ConsoleCloseFailed, result.Status);
        TestAssert.True(fixture.Registry.Current.TryGet(fixture.HostA, out _),
            "A console close failure removed the host.");
        TestAssert.Equal(1, consoles.Count(fixture.HostA));
        TestAssert.True(
            fixture.Registry.TryBeginWrite(fixture.HostA, out IHostWriteLease? lease, out string reason),
            reason);
        lease!.Dispose();
    }

    private static ActiveHostConsoleSession ConsoleSession(HostProfile profile, Guid vmId) => new(
        HostTarget.FromProfile(profile),
        new HostOperationStamp(2, profile.Id),
        vmId,
        $"VM {vmId:N}",
        profile.Address,
        ActiveHostConsoleSessions.ConsolePort,
        $"2:{profile.Id:N}:{vmId:N}");

    private static readonly Guid VmA1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid VmA2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class FakeConsoleWindow : IHostConsoleWindow
    {
        public int CloseCount { get; private set; }
        public bool ThrowOnClose { get; init; }
        public void Activate() { }
        public void Close()
        {
            if (ThrowOnClose) throw new InvalidOperationException("Simulated console close failure.");
            CloseCount++;
        }
    }

    private static HostSessionSnapshot Session(HostSessionRegistry registry, HostId hostId) =>
        registry.Current.Hosts.Single(host => host.HostId == hostId);

    private sealed class DisconnectFixture : IDisposable
    {
        public DisconnectFixture()
        {
            Registry = new HostSessionRegistry(Connector, new SnapshotLoader());
        }

        public TrackingConnector Connector { get; } = new();
        public HostSessionRegistry Registry { get; }
        public HostProfile ProfileA { get; } = new(
            Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111"),
            "宿主 A",
            "10.0.0.6");
        public HostProfile ProfileB { get; } = new(
            Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222"),
            "宿主 B",
            "10.0.0.7");
        public HostId HostA => HostId.FromProfile(ProfileA);
        public HostId HostB => HostId.FromProfile(ProfileB);

        public void ConnectBoth()
        {
            Connect(ProfileA);
            Connect(ProfileB);
        }

        public void Dispose() => Registry.Shutdown();

        private void Connect(HostProfile profile)
        {
            HostConnectResult result = Registry.ConnectAsync(new HostConnectRequest(
                profile,
                HostChannelState.Available,
                HostChannelState.Available)).GetAwaiter().GetResult();
            TestAssert.True(result.Succeeded, result.Message);
        }
    }

    private sealed class TrackingConnection : IHostManagementConnection;

    private sealed class TrackingCandidate(
        HostProfile profile,
        Action<Guid> onDispose) : IHostSessionCandidate
    {
        private int _disposed;

        public HostTarget Target { get; } = HostTarget.FromProfile(profile);
        public IHostManagementConnection ManagementConnection { get; } = new TrackingConnection();
        public HostChannelState ManagementChannel => HostChannelState.Available;
        public HostChannelState ConsoleChannel => HostChannelState.Available;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose(profile.Id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingConnector : IHostSessionConnector
    {
        private readonly Dictionary<Guid, int> _disposeCounts = [];

        public Task<IHostSessionCandidate> ConnectAsync(
            HostSwitchRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<IHostSessionCandidate>(new TrackingCandidate(request.Profile, RecordDispose));

        public int DisposeCount(Guid profileId) =>
            _disposeCounts.GetValueOrDefault(profileId);

        private void RecordDispose(Guid profileId) =>
            _disposeCounts[profileId] = DisposeCount(profileId) + 1;
    }

    private sealed class SnapshotLoader : IHostBasicSnapshotLoader
    {
        public Task<HostBasicSnapshot> LoadAsync(
            IHostSessionCandidate candidate,
            CancellationToken cancellationToken) => Task.FromResult(new HostBasicSnapshot(
                candidate.Target.DisplayName,
                "Windows",
                "Running",
                2,
                new DateTimeOffset(2026, 8, 15, 20, 30, 0, TimeSpan.FromHours(8))));
    }
}
