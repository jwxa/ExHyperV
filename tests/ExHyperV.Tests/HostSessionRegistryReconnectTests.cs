using System.Collections.Concurrent;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Services.Remote.Vms;
using ExHyperV.Tools;
using ExHyperV.ViewModels;

internal static class HostSessionRegistryReconnectTests
{
    private static readonly HostProfile ProfileA = new(
        Guid.Parse("18181818-1818-1818-1818-181818181801"),
        "宿主 A",
        "10.0.0.18");
    private static readonly HostProfile ProfileB = new(
        Guid.Parse("18181818-1818-1818-1818-181818181802"),
        "宿主 B",
        "10.0.0.19");

    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("HostRegistry_TargetLossKeepsStaleDataAndOtherHostUsable", TargetLossKeepsStaleDataAndOtherHostUsable),
        ("HostRegistry_TwoHostsReconnectIndependently", TwoHostsReconnectIndependently),
        ("HostRegistry_ImmediateRetryTargetsOnlyRequestedHost", ImmediateRetryTargetsOnlyRequestedHost),
        ("VmGroup_StaleSessionRetainsRowsAndExplainsStatus", StaleSessionRetainsRowsAndExplainsStatus),
        ("ReconnectUi_TargetsSelectedHostAndRefreshesRecoveredGroup", ReconnectUiTargetsSelectedHostAndRefreshesRecoveredGroup)
    ];

    private static void TargetLossKeepsStaleDataAndOtherHostUsable()
    {
        var reconnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new PerHostConnector(async (profile, call, token) =>
        {
            if (call == 1) return new ReconnectCandidate(profile);
            reconnectEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        var registry = new HostSessionRegistry(connector, new ReconnectSnapshotLoader());
        HostId hostA = HostId.FromProfile(ProfileA);
        HostId hostB = HostId.FromProfile(ProfileB);

        try
        {
            Connect(registry, ProfileA);
            Connect(registry, ProfileB);
            HostSessionSnapshot beforeA = Session(registry, hostA);
            HostOperationStamp stampA = registry.CaptureOperationStamp(hostA);
            HostOperationStamp stampB = registry.CaptureOperationStamp(hostB);

            TestAssert.True(registry.ReportConnectionLoss(stampA, "模拟宿主 A 连接中断。"),
                "The target host rejected its current loss report.");
            WaitUntil(() => reconnectEntered.Task.IsCompleted, "Host A reconnect did not start.");

            HostSessionSnapshot staleA = Session(registry, hostA);
            TestAssert.Equal(HostConnectionState.Reconnecting, staleA.ConnectionState);
            TestAssert.True(staleA.HasStaleData, "Host A did not retain a stale marker.");
            TestAssert.Equal(beforeA.BasicSnapshot, staleA.BasicSnapshot);
            TestAssert.False(registry.CanApply(stampA), "The lost host stamp remained applicable.");
            TestAssert.False(registry.ReportConnectionLoss(stampA, "重复报告。"),
                "A duplicate loss report started another reconnect lifecycle.");
            TestAssert.Equal(2, connector.CallCount(ProfileA.Id));

            TestAssert.False(registry.TryBeginWrite(hostA, out IHostWriteLease? blockedLease, out string writeReason),
                "A stale host accepted a write lease.");
            TestAssert.Null(blockedLease, "A blocked stale-host write returned a lease.");
            TestAssert.Contains("旧数据", writeReason);
            TestAssert.False(registry.TryCaptureConsoleOperation(hostA, out _, out string consoleReason),
                "A stale host accepted a console operation.");
            TestAssert.Contains("中断", consoleReason);

            TestAssert.True(registry.CanApply(stampB), "Host A loss invalidated host B's stamp.");
            TestAssert.True(registry.TryCaptureManagementOperation(hostB, out HostManagementOperationContext? readB, out string readReason), readReason);
            TestAssert.Equal(ProfileB.Id, readB!.Target.ProfileId);
            TestAssert.True(registry.TryBeginWrite(hostB, out IHostWriteLease? writeB, out string writeBReason), writeBReason);
            writeB!.Dispose();
            TestAssert.True(registry.TryCaptureConsoleOperation(hostB, out HostConsoleOperationContext? consoleB, out string consoleBReason), consoleBReason);
            TestAssert.Equal(ProfileB.Id, consoleB!.Target.ProfileId);
        }
        finally
        {
            registry.StopReconnect(hostA);
            registry.Shutdown();
        }
    }

    private static void TwoHostsReconnectIndependently()
    {
        var completions = new ConcurrentDictionary<Guid, TaskCompletionSource<IHostSessionCandidate>>();
        var entered = new ConcurrentDictionary<Guid, TaskCompletionSource>();
        var connector = new PerHostConnector((profile, call, token) =>
        {
            if (call == 1) return Task.FromResult<IHostSessionCandidate>(new ReconnectCandidate(profile));
            TaskCompletionSource signal = entered.GetOrAdd(
                profile.Id,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            signal.TrySetResult();
            TaskCompletionSource<IHostSessionCandidate> completion = completions.GetOrAdd(
                profile.Id,
                _ => new TaskCompletionSource<IHostSessionCandidate>(TaskCreationOptions.RunContinuationsAsynchronously));
            return completion.Task.WaitAsync(token);
        });
        var registry = new HostSessionRegistry(connector, new ReconnectSnapshotLoader());
        HostId hostA = HostId.FromProfile(ProfileA);
        HostId hostB = HostId.FromProfile(ProfileB);

        try
        {
            Connect(registry, ProfileA);
            Connect(registry, ProfileB);
            HostOperationStamp stampA = registry.CaptureOperationStamp(hostA);
            HostOperationStamp stampB = registry.CaptureOperationStamp(hostB);

            TestAssert.True(registry.ReportConnectionLoss(stampA, "A 断线。"), "Host A loss was rejected.");
            TestAssert.True(registry.ReportConnectionLoss(stampB, "B 断线。"), "Host B loss was rejected.");
            WaitUntil(
                () => entered.TryGetValue(ProfileA.Id, out TaskCompletionSource? a) && a.Task.IsCompleted
                    && entered.TryGetValue(ProfileB.Id, out TaskCompletionSource? b) && b.Task.IsCompleted,
                "A and B did not enter independent reconnect attempts.");
            TestAssert.Equal(2, connector.CallCount(ProfileA.Id));
            TestAssert.Equal(2, connector.CallCount(ProfileB.Id));

            completions[ProfileB.Id].TrySetResult(new ReconnectCandidate(ProfileB));
            WaitUntil(() => !Session(registry, hostB).HasStaleData, "Host B did not recover independently.");
            TestAssert.True(Session(registry, hostA).HasStaleData, "Host B recovery changed host A.");
            TestAssert.Equal(stampB.Generation + 1, Session(registry, hostB).Generation);

            registry.StopReconnect(hostA);
            WaitUntil(() => Session(registry, hostA).ConnectionState == HostConnectionState.RemoteDisconnected,
                "Stopping A did not publish its stopped state.");
            TestAssert.True(Session(registry, hostA).HasStaleData, "Stopping A cleared its stale data.");
            TestAssert.False(Session(registry, hostA).Reconnect.IsActive, "Stopping A left its reconnect active.");
            TestAssert.False(Session(registry, hostB).HasStaleData, "Stopping A changed recovered host B.");
        }
        finally
        {
            registry.StopReconnect(hostA);
            registry.StopReconnect(hostB);
            registry.Shutdown();
        }
    }

    private static void ImmediateRetryTargetsOnlyRequestedHost()
    {
        var scheduler = new ControlledReconnectScheduler();
        var connector = new PerHostConnector((profile, call, _) =>
        {
            if (profile.Id == ProfileA.Id && call == 2)
                throw new HostSwitchException("第一次重连失败。");
            HostChannelState console = profile.Id == ProfileA.Id && call == 3
                ? HostChannelState.Unavailable
                : HostChannelState.Available;
            return Task.FromResult<IHostSessionCandidate>(new ReconnectCandidate(profile, console));
        });
        var registry = new HostSessionRegistry(connector, new ReconnectSnapshotLoader(), scheduler);
        HostId hostA = HostId.FromProfile(ProfileA);
        HostId hostB = HostId.FromProfile(ProfileB);

        try
        {
            Connect(registry, ProfileA);
            Connect(registry, ProfileB);
            HostOperationStamp stampA = registry.CaptureOperationStamp(hostA);
            HostOperationStamp stampB = registry.CaptureOperationStamp(hostB);

            TestAssert.True(registry.ReportConnectionLoss(stampA, "A 断线。"), "Host A loss was rejected.");
            WaitUntil(() => scheduler.Delays.Count == 1, "Host A did not enter a reconnect delay.");
            TestAssert.False(registry.RetryReconnectNow(hostB), "Immediate retry started for fresh host B.");
            TestAssert.True(registry.RetryReconnectNow(hostA), "Host A's pending delay was not skipped.");
            WaitUntil(() => !Session(registry, hostA).HasStaleData, "Host A did not recover after immediate retry.");

            HostSessionSnapshot recoveredA = Session(registry, hostA);
            TestAssert.Equal(stampA.Generation + 1, recoveredA.Generation);
            TestAssert.Equal(HostConnectionState.PartiallyAvailable, recoveredA.ConnectionState);
            TestAssert.Equal(HostChannelState.Unavailable, recoveredA.ConsoleChannel);
            TestAssert.Equal(HostCapabilityReasonCode.ConsoleChannelUnavailable,
                recoveredA.Capabilities[HostCapabilityKind.VmConsole].ReasonCode);
            TestAssert.True(registry.CanApply(stampB), "Retrying A invalidated host B's stamp.");
            TestAssert.Equal(stampB.Generation, Session(registry, hostB).Generation);
            TestAssert.Equal(3, connector.CallCount(ProfileA.Id));
            TestAssert.Equal(1, connector.CallCount(ProfileB.Id));
        }
        finally
        {
            registry.Shutdown();
        }
    }

    private static void StaleSessionRetainsRowsAndExplainsStatus()
    {
        HostSessionSnapshot fresh = Snapshot(
            ProfileA,
            generation: 2,
            HostConnectionState.Connected,
            stale: false,
            HostReconnectState.None);
        var group = new HostVmGroupViewModel(fresh, order: 1);
        var vm = new VmInstanceViewModel();
        group.Vms.Add(vm);

        HostSessionSnapshot reconnecting = Snapshot(
            ProfileA,
            generation: 2,
            HostConnectionState.Reconnecting,
            stale: true,
            HostReconnectState.Starting("RPC 连接中断。"));
        group.ApplySession(reconnecting);

        TestAssert.Equal(1, group.Vms.Count);
        TestAssert.True(ReferenceEquals(vm, group.Vms[0]), "Applying a stale session replaced the retained VM row.");
        TestAssert.True(group.IsWarning, "A reconnecting stale group did not use the warning state.");
        TestAssert.False(group.Capabilities[HostCapabilityKind.VmWrite].CanExecute,
            "A stale group still exposed VM writes.");
        TestAssert.False(group.Capabilities[HostCapabilityKind.VmConsole].CanExecute,
            "A stale group still exposed console operations.");

        var statusProperty = typeof(HostVmGroupViewModel).GetProperty("StatusText");
        TestAssert.NotNull(statusProperty, "The VM group does not expose a status tooltip text.");
        string reconnectingText = statusProperty!.GetValue(group) as string ?? string.Empty;
        TestAssert.Contains("旧数据", reconnectingText);
        TestAssert.Contains("重连", reconnectingText);

        group.ApplySession(Snapshot(
            ProfileA,
            generation: 2,
            HostConnectionState.RemoteDisconnected,
            stale: true,
            HostReconnectState.Stopped(1, "仍不可用。")));
        string stoppedText = statusProperty.GetValue(group) as string ?? string.Empty;
        TestAssert.Contains("停止", stoppedText);
        TestAssert.Contains("旧数据", stoppedText);

        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Views", "Pages", "VirtualMachinesPage.xaml"));
        TestAssert.Contains("ToolTip=\"{Binding StatusText}\"", xaml);
        group.Dispose();
    }

    private static void ReconnectUiTargetsSelectedHostAndRefreshesRecoveredGroup()
    {
        string root = FindRepositoryRoot();
        string connectionPage = File.ReadAllText(Path.Combine(
            root, "src", "ViewModels", "HostConnectionPageViewModel.cs"));
        string connectionXaml = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Pages", "HostConnectionPage.xaml"));
        string vmPage = File.ReadAllText(Path.Combine(
            root, "src", "ViewModels", "VirtualMachinesPageViewModel.cs"));

        TestAssert.Contains("RetryReconnectNow(HostId.FromProfile(profile))", connectionPage);
        TestAssert.Contains("StopReconnect(HostId.FromProfile(profile))", connectionPage);
        TestAssert.Contains("Command=\"{Binding RetryReconnectCommand}\"", connectionXaml);
        TestAssert.Contains("Command=\"{Binding StopReconnectCommand}\"", connectionXaml);
        TestAssert.Contains("change.ChangedHostId", vmPage);
        TestAssert.Contains("_ = LoadHostGroupAsync(group, showErrors: false)", vmPage);
    }

    private static void Connect(HostSessionRegistry registry, HostProfile profile)
    {
        HostConnectResult result = registry.ConnectAsync(new HostConnectRequest(
            profile,
            HostChannelState.Available,
            HostChannelState.Available)).GetAwaiter().GetResult();
        TestAssert.Equal(HostConnectStatus.Succeeded, result.Status);
    }

    private static HostSessionSnapshot Session(HostSessionRegistry registry, HostId hostId) =>
        registry.Current.Hosts.Single(host => host.HostId == hostId);

    private static HostSessionSnapshot Snapshot(
        HostProfile profile,
        long generation,
        HostConnectionState state,
        bool stale,
        HostReconnectState reconnect)
    {
        HostTarget target = HostTarget.FromProfile(profile);
        var active = new ActiveHostSession(
            generation,
            target,
            state,
            HostChannelState.Available,
            HostChannelState.Available,
            stale);
        return new HostSessionSnapshot(
            HostId.FromProfile(profile),
            generation,
            target,
            state,
            HostChannelState.Available,
            HostChannelState.Available,
            stale)
        {
            BasicSnapshot = new HostBasicSnapshot(profile.DisplayName, "Windows", "Running", 1, DateTimeOffset.UtcNow),
            Reconnect = reconnect,
            Capabilities = HostCapabilityMatrix.Create(active, isSwitching: false)
        };
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

    private static void WaitUntil(Func<bool> condition, string message)
    {
        if (!SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException(message);
    }

    private sealed class ReconnectConnection(WmiContext context) : IWmiHostManagementConnection
    {
        public WmiContext Context { get; } = context;
    }

    private sealed class ReconnectCandidate(
        HostProfile profile,
        HostChannelState console = HostChannelState.Available) : IHostSessionCandidate
    {
        public HostTarget Target { get; } = HostTarget.FromProfile(profile);
        public IHostManagementConnection ManagementConnection { get; } =
            new ReconnectConnection(WmiContext.RemoteCurrentWindowsIdentity(profile.Address));
        public HostChannelState ManagementChannel => HostChannelState.Available;
        public HostChannelState ConsoleChannel { get; } = console;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PerHostConnector(
        Func<HostProfile, int, CancellationToken, Task<IHostSessionCandidate>> connect) : IHostSessionConnector
    {
        private readonly ConcurrentDictionary<Guid, int> _calls = new();

        public int CallCount(Guid profileId) => _calls.GetValueOrDefault(profileId);

        public Task<IHostSessionCandidate> ConnectAsync(
            HostSwitchRequest request,
            CancellationToken cancellationToken)
        {
            int call = _calls.AddOrUpdate(request.Profile.Id, 1, (_, count) => checked(count + 1));
            return connect(request.Profile, call, cancellationToken);
        }
    }

    private sealed class ReconnectSnapshotLoader : IHostBasicSnapshotLoader
    {
        public Task<HostBasicSnapshot> LoadAsync(
            IHostSessionCandidate candidate,
            CancellationToken cancellationToken) => Task.FromResult(new HostBasicSnapshot(
                candidate.Target.DisplayName,
                "Windows",
                "Running",
                1,
                DateTimeOffset.UtcNow));
    }

    private sealed class ControlledReconnectScheduler : IReconnectScheduler
    {
        private readonly ConcurrentQueue<TimeSpan> _delays = new();
        public DateTimeOffset UtcNow => new(2026, 8, 15, 20, 0, 0, TimeSpan.FromHours(8));
        public IReadOnlyList<TimeSpan> Delays => _delays.ToArray();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _delays.Enqueue(delay);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }
    }
}

namespace ExHyperV.ViewModels
{
    public sealed class VmInstanceViewModel
    {
    }
}
