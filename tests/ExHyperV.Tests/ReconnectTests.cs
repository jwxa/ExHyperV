using System.Collections.Concurrent;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Services.Remote.Vms;
using ExHyperV.Tools;

internal static class ReconnectTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Reconnect_LossKeepsSnapshotStaleAndBlocksWrites", LossKeepsSnapshotStaleAndBlocksWrites),
        ("Reconnect_BackoffGrowsAndCaps", BackoffGrowsAndCaps),
        ("Reconnect_OnlyOneAttemptRunsAtATime", OnlyOneAttemptRunsAtATime),
        ("Reconnect_UserCanStopWithoutLocalFallback", UserCanStopWithoutLocalFallback),
        ("Reconnect_ImmediateRetrySkipsPendingDelay", ImmediateRetrySkipsPendingDelay),
        ("Reconnect_SuccessPublishesFreshGeneration", SuccessPublishesFreshGeneration),
        ("Reconnect_PostConfigurationManagementLossStartsReconnect", PostConfigurationManagementLossStartsReconnect),
        ("Reconnect_WaitsForActiveWriteLease", WaitsForActiveWriteLease),
        ("Reconnect_RegistersAndReleasesTaskOwnership", RegistersAndReleasesTaskOwnership),
        ("Reconnect_SelectionChangeKeepsActiveHostReconnect", SelectionChangeKeepsActiveHostReconnect),
        ("Reconnect_ShutdownCancelsReconnectAndReleasesCandidates", ShutdownCancelsReconnectAndReleasesCandidates),
        ("Reconnect_ShutdownDoesNotBlockOnConnectorIgnoringCancellation", ShutdownDoesNotBlockOnConnectorIgnoringCancellation)
    ];

    private static void LossKeepsSnapshotStaleAndBlocksWrites()
    {
        HostProfile profile = Profile("10.0.0.20");
        var reconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new SequenceConnector((call, request, token) => call == 1
            ? Task.FromResult<IHostSessionCandidate>(new FakeCandidate(profile))
            : WaitForCancellationAsync(reconnectStarted, token));
        var coordinator = ConnectedCoordinator(profile, connector, new ImmediateSnapshotLoader());
        HostBasicSnapshot before = coordinator.Current.BasicSnapshot!;
        HostOperationStamp stamp = coordinator.CaptureOperationStamp();

        TestAssert.True(coordinator.ReportConnectionLoss(stamp, "模拟 RPC 连接中断。"), "Connection loss was not accepted.");
        WaitUntil(() => reconnectStarted.Task.IsCompleted, "Reconnect attempt did not start.");

        ActiveHostCoordinatorSnapshot current = coordinator.Current;
        TestAssert.Equal(before, current.BasicSnapshot);
        TestAssert.True(current.ActiveSession.HasStaleData, "Last snapshot was not marked stale.");
        TestAssert.Equal(HostConnectionState.Reconnecting, current.ActiveSession.ConnectionState);
        TestAssert.True(current.Reconnect.IsActive, "Reconnect state was not published.");
        TestAssert.False(coordinator.TryBeginWrite(out IHostWriteLease? lease, out string reason), "A write started while data was stale.");
        TestAssert.Null(lease, "Rejected stale write returned a lease.");
        TestAssert.Contains("中断", reason);
        coordinator.StopReconnect();
    }

    private static void BackoffGrowsAndCaps()
    {
        HostProfile profile = Profile("10.0.0.21");
        var scheduler = new ControlledReconnectScheduler();
        var connector = new SequenceConnector((call, _, _) => call == 1
            ? Task.FromResult<IHostSessionCandidate>(new FakeCandidate(profile))
            : throw new HostSwitchException($"模拟第 {call - 1} 次重连失败。"));
        var coordinator = ConnectedCoordinator(profile, connector, new ImmediateSnapshotLoader(), scheduler);

        coordinator.ReportConnectionLoss(coordinator.CaptureOperationStamp(), "模拟断线。");
        TimeSpan[] expected =
        [
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)
        ];
        for (int index = 0; index < expected.Length; index++)
        {
            int delayCount = index + 1;
            WaitUntil(() => scheduler.Delays.Count >= delayCount, $"Reconnect delay {delayCount} was not scheduled.");
            TestAssert.Equal(expected[index], scheduler.Delays[index]);
            if (index + 1 < expected.Length) scheduler.ReleaseNext();
        }

        TestAssert.False(coordinator.Current.ActiveSession.Target.IsLocal, "Repeated reconnect failures silently selected localhost.");
        TestAssert.True(coordinator.Current.ActiveSession.HasStaleData, "Repeated failures cleared stale data.");
        coordinator.StopReconnect();
    }

    private static void OnlyOneAttemptRunsAtATime()
    {
        HostProfile profile = Profile("10.0.0.22");
        int active = 0;
        int maximumActive = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new SequenceConnector(async (call, _, token) =>
        {
            if (call == 1) return new FakeCandidate(profile);
            int nowActive = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, nowActive);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Interlocked.Decrement(ref active); }
            throw new InvalidOperationException("Unreachable");
        });
        var coordinator = ConnectedCoordinator(profile, connector, new ImmediateSnapshotLoader());
        HostOperationStamp stamp = coordinator.CaptureOperationStamp();

        TestAssert.True(coordinator.ReportConnectionLoss(stamp, "模拟断线。"), "First loss report was rejected.");
        WaitUntil(() => entered.Task.IsCompleted, "Reconnect attempt did not enter connector.");
        TestAssert.False(coordinator.ReportConnectionLoss(stamp, "重复断线。"), "A duplicate loss report started another loop.");
        TestAssert.False(coordinator.RetryReconnectNow(), "Immediate retry started while an attempt was already running.");
        Thread.Sleep(30);
        TestAssert.Equal(1, maximumActive);
        TestAssert.Equal(2, connector.CallCount);
        coordinator.StopReconnect();
    }

    private static void UserCanStopWithoutLocalFallback()
    {
        HostProfile profile = Profile("10.0.0.23");
        var scheduler = new ControlledReconnectScheduler();
        var connector = new SequenceConnector((call, _, _) => call == 1
            ? Task.FromResult<IHostSessionCandidate>(new FakeCandidate(profile))
            : throw new HostSwitchException("模拟重连失败。"));
        var coordinator = ConnectedCoordinator(profile, connector, new ImmediateSnapshotLoader(), scheduler);
        coordinator.ReportConnectionLoss(coordinator.CaptureOperationStamp(), "模拟断线。");
        WaitUntil(() => scheduler.Delays.Count == 1, "Reconnect delay was not scheduled.");

        coordinator.StopReconnect();

        ActiveHostCoordinatorSnapshot current = coordinator.Current;
        TestAssert.False(current.Reconnect.IsActive, "Stop left reconnect active.");
        TestAssert.Equal(HostConnectionState.RemoteDisconnected, current.ActiveSession.ConnectionState);
        TestAssert.True(current.ActiveSession.HasStaleData, "Stop cleared stale data.");
        TestAssert.Equal(profile.Id, current.ActiveSession.Target.ProfileId);
        scheduler.ReleaseNext();
        Thread.Sleep(30);
        TestAssert.Equal(2, connector.CallCount);
    }

    private static void ImmediateRetrySkipsPendingDelay()
    {
        HostProfile profile = Profile("10.0.0.24");
        var scheduler = new ControlledReconnectScheduler();
        var connector = new SequenceConnector((call, _, _) => call switch
        {
            1 => Task.FromResult<IHostSessionCandidate>(new FakeCandidate(profile)),
            2 => throw new HostSwitchException("第一次重连失败。"),
            _ => Task.FromResult<IHostSessionCandidate>(new FakeCandidate(profile, console: HostChannelState.Unavailable))
        });
        var coordinator = ConnectedCoordinator(profile, connector, new ImmediateSnapshotLoader(), scheduler);
        coordinator.ReportConnectionLoss(coordinator.CaptureOperationStamp(), "模拟断线。");
        WaitUntil(() => scheduler.Delays.Count == 1, "Reconnect delay was not scheduled.");

        TestAssert.True(coordinator.RetryReconnectNow(), "Pending reconnect delay was not skipped.");
        WaitUntil(() => !coordinator.Current.ActiveSession.HasStaleData, "Immediate retry did not recover the session.");
        TestAssert.Equal(3, connector.CallCount);
        TestAssert.Equal(HostConnectionState.PartiallyAvailable, coordinator.Current.ActiveSession.ConnectionState);
    }

    private static void SuccessPublishesFreshGeneration()
    {
        HostProfile profile = Profile("10.0.0.25");
        var first = new FakeCandidate(profile);
        var recovered = new FakeCandidate(profile, console: HostChannelState.Unavailable);
        var connector = new SequenceConnector((call, _, _) =>
            Task.FromResult<IHostSessionCandidate>(call == 1 ? first : recovered));
        var loader = new SequenceSnapshotLoader((call, candidate) => Snapshot(candidate.Target, call == 1 ? 2 : 7));
        var coordinator = ConnectedCoordinator(profile, connector, loader);
        long oldGeneration = coordinator.Current.ActiveSession.Generation;

        coordinator.ReportConnectionLoss(coordinator.CaptureOperationStamp(), "模拟断线。");
        WaitUntil(() => coordinator.Current.ActiveSession.Generation > oldGeneration, "Successful reconnect did not publish a new generation.");

        ActiveHostCoordinatorSnapshot current = coordinator.Current;
        TestAssert.False(current.ActiveSession.HasStaleData, "Fresh reconnect retained stale marker.");
        TestAssert.Equal(oldGeneration + 1, current.ActiveSession.Generation);
        TestAssert.Equal(7, current.BasicSnapshot!.VirtualMachineCount);
        TestAssert.Equal(HostConnectionState.PartiallyAvailable, current.ActiveSession.ConnectionState);
        TestAssert.False(current.Reconnect.IsActive, "Successful reconnect left reconnect state active.");
        TestAssert.Equal(1, first.DisposeCount);
        TestAssert.Equal(0, recovered.DisposeCount);
    }

    private static void PostConfigurationManagementLossStartsReconnect()
    {
        HostProfile profile = Profile("10.0.0.34");
        var reconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new SequenceConnector((call, _, token) => call == 1
            ? Task.FromResult<IHostSessionCandidate>(new FakeCandidate(profile))
            : WaitForCancellationAsync(reconnectStarted, token));
        var coordinator = ConnectedCoordinator(profile, connector, new ImmediateSnapshotLoader());
        HostBasicSnapshot before = coordinator.Current.BasicSnapshot!;

        TestAssert.True(
            coordinator.UpdateActiveChannels(
                profile.Id,
                HostChannelState.Unavailable,
                HostChannelState.Available,
                "配置复检发现 WMI/DCOM 已断开。"),
            "Post-configuration management loss was rejected.");
        WaitUntil(() => reconnectStarted.Task.IsCompleted, "Post-configuration management loss did not start reconnect.");

        ActiveHostCoordinatorSnapshot current = coordinator.Current;
        TestAssert.True(current.ActiveSession.HasStaleData, "Post-configuration management loss did not mark the snapshot stale.");
        TestAssert.Equal(before, current.BasicSnapshot);
        TestAssert.Equal(HostConnectionState.Reconnecting, current.ActiveSession.ConnectionState);
        TestAssert.False(coordinator.TryBeginWrite(out IHostWriteLease? lease, out string reason),
            "A write started after post-configuration management loss.");
        TestAssert.Null(lease, "Rejected post-configuration write returned a lease.");
        TestAssert.Contains("中断", reason);
        coordinator.StopReconnect();
    }

    private static void WaitsForActiveWriteLease()
    {
        HostProfile profile = Profile("10.0.0.28");
        var reconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new SequenceConnector((call, _, token) => call == 1
            ? Task.FromResult<IHostSessionCandidate>(new FakeCandidate(profile))
            : WaitForCancellationAsync(reconnectStarted, token));
        var coordinator = ConnectedCoordinator(profile, connector, new ImmediateSnapshotLoader());
        TestAssert.True(coordinator.TryBeginWrite(out IHostWriteLease? lease, out string reason), reason);

        coordinator.ReportConnectionLoss(coordinator.CaptureOperationStamp(), "写操作期间模拟断线。");
        Thread.Sleep(50);
        TestAssert.Equal(1, connector.CallCount);
        TestAssert.False(reconnectStarted.Task.IsCompleted, "Reconnect started before the active write drained.");

        lease!.Dispose();
        WaitUntil(() => reconnectStarted.Task.IsCompleted, "Reconnect did not start after the write lease drained.");
        TestAssert.Equal(2, connector.CallCount);
        coordinator.StopReconnect();
    }

    private static void RegistersAndReleasesTaskOwnership()
    {
        HostProfile profile = Profile("10.0.0.29");
        var reconnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectCompletion = new TaskCompletionSource<IHostSessionCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
        ActiveHostSessionCoordinator? coordinator = null;
        bool taskWasRegistered = false;
        var connector = new SequenceConnector((call, _, _) =>
        {
            if (call == 1)
                return Task.FromResult<IHostSessionCandidate>(new FakeCandidate(profile));

            taskWasRegistered = GetReconnectTask(coordinator!) is not null;
            reconnectEntered.TrySetResult();
            return reconnectCompletion.Task;
        });
        coordinator = ConnectedCoordinator(profile, connector, new ImmediateSnapshotLoader());

        coordinator.ReportConnectionLoss(coordinator.CaptureOperationStamp(), "模拟断线。");
        WaitUntil(() => reconnectEntered.Task.IsCompleted, "Reconnect attempt did not enter connector.");
        TestAssert.True(taskWasRegistered, "Reconnect entered the connector before its lifecycle task was registered.");

        reconnectCompletion.TrySetResult(new FakeCandidate(profile));
        WaitUntil(() => !coordinator.Current.ActiveSession.HasStaleData, "Reconnect did not recover the session.");
        WaitUntil(() => GetReconnectTask(coordinator) is null, "Completed reconnect retained its lifecycle task.");
    }

    private static void SelectionChangeKeepsActiveHostReconnect()
    {
        HostProfile activeProfile = Profile("10.0.0.30");
        HostProfile selectedProfile = Profile("10.0.0.31");
        var reconnectCompletion = new TaskCompletionSource<IHostSessionCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new SequenceConnector((call, _, _) => call == 1
            ? Task.FromResult<IHostSessionCandidate>(new FakeCandidate(activeProfile))
            : reconnectCompletion.Task);
        var coordinator = ConnectedCoordinator(activeProfile, connector, new ImmediateSnapshotLoader());

        coordinator.ReportConnectionLoss(coordinator.CaptureOperationStamp(), "模拟断线。");
        coordinator.SelectProfile(selectedProfile);

        TestAssert.Equal(activeProfile.Id, coordinator.Current.ActiveSession.Target.ProfileId);
        TestAssert.Equal(selectedProfile.Id, coordinator.Current.SelectedProfile!.Id);
        reconnectCompletion.TrySetResult(new FakeCandidate(activeProfile));
        WaitUntil(() => !coordinator.Current.ActiveSession.HasStaleData, "Original active host did not finish reconnecting.");
        TestAssert.Equal(activeProfile.Id, coordinator.Current.ActiveSession.Target.ProfileId);
        TestAssert.Equal(selectedProfile.Id, coordinator.Current.SelectedProfile!.Id);
    }

    private static void ShutdownCancelsReconnectAndReleasesCandidates()
    {
        HostProfile profile = Profile("10.0.0.32");
        var reconnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new SequenceConnector((call, _, token) => call == 1
            ? Task.FromResult<IHostSessionCandidate>(new FakeCandidate(profile))
            : WaitForCancellationAsync(reconnectEntered, token));
        var coordinator = ConnectedCoordinator(profile, connector, new ImmediateSnapshotLoader());
        var activeCandidate = (FakeCandidate)GetActiveCandidate(coordinator)!;
        HostOperationStamp stamp = coordinator.CaptureOperationStamp();

        TestAssert.True(coordinator.ReportConnectionLoss(stamp, "模拟应用退出时断线。"), "Connection loss was not accepted.");
        WaitUntil(() => reconnectEntered.Task.IsCompleted, "Reconnect attempt did not start before shutdown.");

        coordinator.Shutdown();
        coordinator.Shutdown();

        TestAssert.Equal(1, activeCandidate.DisposeCount);
        TestAssert.True(GetReconnectTask(coordinator) is null, "Shutdown retained the reconnect lifecycle task.");
        TestAssert.False(coordinator.ReportConnectionLoss(stamp, "关闭后重试。"), "Shutdown coordinator accepted a new loss report.");
        TestAssert.False(coordinator.RetryReconnectNow(), "Shutdown coordinator restarted reconnect.");
        TestAssert.False(coordinator.TryBeginWrite(out IHostWriteLease? lease, out string reason), "Shutdown coordinator accepted a write.");
        TestAssert.Null(lease, "Rejected shutdown write returned a lease.");
        TestAssert.Contains("关闭", reason);
        TestAssert.Equal(2, connector.CallCount);
    }

    private static void ShutdownDoesNotBlockOnConnectorIgnoringCancellation()
    {
        HostProfile profile = Profile("10.0.0.33");
        var reconnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource<IHostSessionCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new SequenceConnector((call, _, _) =>
        {
            if (call == 1) return Task.FromResult<IHostSessionCandidate>(new FakeCandidate(profile));
            reconnectEntered.TrySetResult();
            return neverCompletes.Task;
        });
        var coordinator = ConnectedCoordinator(profile, connector, new ImmediateSnapshotLoader());
        var activeCandidate = (FakeCandidate)GetActiveCandidate(coordinator)!;
        HostOperationStamp stamp = coordinator.CaptureOperationStamp();

        TestAssert.True(
            coordinator.ReportConnectionLoss(stamp, "模拟不响应取消的连接器。"),
            "Connection loss was not accepted for the non-cancellable connector test.");
        WaitUntil(() => reconnectEntered.Task.IsCompleted, "Reconnect attempt did not enter the non-cancellable connector.");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        coordinator.Shutdown();
        stopwatch.Stop();

        TestAssert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "Shutdown blocked on a connector that ignored cancellation.");
        TestAssert.Equal(1, activeCandidate.DisposeCount);
        TestAssert.True(GetReconnectTask(coordinator) is null, "Shutdown retained a non-cancellable reconnect task.");
    }

    private static Task? GetReconnectTask(ActiveHostSessionCoordinator coordinator) =>
        (Task?)typeof(ActiveHostSessionCoordinator)
            .GetField("_reconnectTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator);

    private static IHostSessionCandidate? GetActiveCandidate(ActiveHostSessionCoordinator coordinator) =>
        (IHostSessionCandidate?)typeof(ActiveHostSessionCoordinator)
            .GetField("_activeCandidate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(coordinator);

    private static ActiveHostSessionCoordinator ConnectedCoordinator(
        HostProfile profile,
        IHostSessionConnector connector,
        IHostBasicSnapshotLoader loader,
        IReconnectScheduler? scheduler = null)
    {
        var coordinator = new ActiveHostSessionCoordinator(connector, loader, scheduler);
        coordinator.SelectProfile(profile);
        HostSwitchResult result = coordinator.SwitchToSelectedAsync(Request(profile)).GetAwaiter().GetResult();
        TestAssert.Equal(HostSwitchStatus.Succeeded, result.Status);
        return coordinator;
    }

    private static async Task<IHostSessionCandidate> WaitForCancellationAsync(
        TaskCompletionSource entered,
        CancellationToken token)
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("Unreachable");
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        if (!SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException(message);
    }

    private static HostProfile Profile(string address) => new(Guid.NewGuid(), $"宿主 {address}", address);

    private static HostSwitchRequest Request(HostProfile profile) =>
        new(profile, HostChannelState.Available, HostChannelState.Available);

    private static HostBasicSnapshot Snapshot(HostTarget target, int vmCount) =>
        new(target.DisplayName, "Windows", "Running", vmCount, new DateTimeOffset(2026, 8, 13, 21, 0, 0, TimeSpan.FromHours(8)));

    private sealed class FakeConnection(WmiContext context) : IWmiHostManagementConnection
    {
        public WmiContext Context { get; } = context;
    }

    private sealed class FakeCandidate(
        HostProfile profile,
        HostChannelState management = HostChannelState.Available,
        HostChannelState console = HostChannelState.Available) : IHostSessionCandidate
    {
        public HostTarget Target { get; } = HostTarget.FromProfile(profile);
        public IHostManagementConnection ManagementConnection { get; } =
            new FakeConnection(WmiContext.RemoteCurrentWindowsIdentity(profile.Address));
        public HostChannelState ManagementChannel { get; } = management;
        public HostChannelState ConsoleChannel { get; } = console;
        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SequenceConnector(
        Func<int, HostSwitchRequest, CancellationToken, Task<IHostSessionCandidate>> connect) : IHostSessionConnector
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public Task<IHostSessionCandidate> ConnectAsync(HostSwitchRequest request, CancellationToken cancellationToken) =>
            connect(Interlocked.Increment(ref _callCount), request, cancellationToken);
    }

    private sealed class ImmediateSnapshotLoader : IHostBasicSnapshotLoader
    {
        public Task<HostBasicSnapshot> LoadAsync(IHostSessionCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot(candidate.Target, 2));
    }

    private sealed class SequenceSnapshotLoader(Func<int, IHostSessionCandidate, HostBasicSnapshot> load) : IHostBasicSnapshotLoader
    {
        private int _callCount;
        public Task<HostBasicSnapshot> LoadAsync(IHostSessionCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult(load(Interlocked.Increment(ref _callCount), candidate));
    }

    private sealed class ControlledReconnectScheduler : IReconnectScheduler
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _pending = new();
        private readonly ConcurrentQueue<TimeSpan> _delays = new();
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 13, 21, 0, 0, TimeSpan.Zero);
        public IReadOnlyList<TimeSpan> Delays => _delays.ToArray();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _delays.Enqueue(delay);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            _pending.Enqueue(completion);
            return completion.Task;
        }

        public void ReleaseNext()
        {
            if (_pending.TryDequeue(out TaskCompletionSource? completion)) completion.TrySetResult();
        }
    }
}
