using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Services.Remote.Vms;
using ExHyperV.Tools;

internal static class VmOperationTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("VmOps_LocalReadUsesLocalWmiContext", LocalReadUsesLocalWmiContext),
        ("VmOps_RemoteReadUsesActiveConnectionContext", RemoteReadUsesActiveConnectionContext),
        ("VmOps_LateReadIsRejectedAfterHostSwitch", LateReadIsRejectedAfterHostSwitch),
        ("VmOps_LateReadFailureIsRejectedAfterHostSwitch", LateReadFailureIsRejectedAfterHostSwitch),
        ("VmOps_ExpectedOldStampRejectsReadBeforeBackend", ExpectedOldStampRejectsReadBeforeBackend),
        ("VmOps_WriteLeaseBlocksHostSwitch", WriteLeaseBlocksHostSwitch),
        ("VmOps_FrozenSwitchRejectsNewWrite", FrozenSwitchRejectsNewWrite),
        ("VmOps_ConfirmedOldHostWriteIsRejected", ConfirmedOldHostWriteIsRejected),
        ("VmOps_BackendFailureIsExplicit", BackendFailureIsExplicit),
        ("VmOps_RemoteStructuredReadFailureStartsReconnect", RemoteStructuredReadFailureStartsReconnect),
        ("VmOps_RemoteStructuredWriteFailureStartsReconnect", RemoteStructuredWriteFailureStartsReconnect),
        ("VmOps_RemoteComFailureStartsReconnectInRelease", RemoteComFailureStartsReconnectInRelease),
        ("VmOps_RemoteBusinessFailureDoesNotStartReconnect", RemoteBusinessFailureDoesNotStartReconnect)
    ];

    private static void LocalReadUsesLocalWmiContext()
    {
        var operations = new ActiveHostVmOperations(
            new ActiveHostSessionCoordinator(),
            new HostWmiContextResolver());

        HostVmReadResult<string> result = operations.ReadAsync(
            (context, _) => Task.FromResult(context.Host)).GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.Succeeded, result.Status);
        TestAssert.Equal(".", result.Value);
        TestAssert.True(result.Operation!.Target.IsLocal, "Local read was not bound to the local target.");
    }

    private static void RemoteReadUsesActiveConnectionContext()
    {
        WmiContext expected = WmiContext.RemoteCurrentWindowsIdentity("10.0.0.6");
        (ActiveHostSessionCoordinator coordinator, HostProfile profile) = RemoteCoordinator(expected);
        Activate(coordinator, profile);
        var operations = new ActiveHostVmOperations(coordinator, new HostWmiContextResolver());

        HostVmReadResult<WmiContext> result = operations.ReadAsync(
            (context, _) => Task.FromResult(context)).GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.Succeeded, result.Status);
        TestAssert.True(ReferenceEquals(expected, result.Value), "Remote read did not use the active connection WMI context.");
        TestAssert.Equal(profile.Id, result.Operation!.Stamp.ProfileId);
    }

    private static void LateReadIsRejectedAfterHostSwitch()
    {
        (ActiveHostSessionCoordinator coordinator, HostProfile profile) =
            RemoteCoordinator(WmiContext.RemoteCurrentWindowsIdentity("10.0.0.6"));
        Activate(coordinator, profile);
        var operations = new ActiveHostVmOperations(coordinator, new HostWmiContextResolver());

        HostVmReadResult<string> result = operations.ReadAsync(async (_, _) =>
        {
            HostSwitchResult switchResult = await coordinator.SwitchToLocalAsync();
            TestAssert.Equal(HostSwitchStatus.Succeeded, switchResult.Status);
            return "old-host-data";
        }).GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.Stale, result.Status);
        TestAssert.Null(result.Value, "A late old-host value was exposed.");
    }

    private static void WriteLeaseBlocksHostSwitch()
    {
        (ActiveHostSessionCoordinator coordinator, HostProfile profile) =
            RemoteCoordinator(WmiContext.RemoteCurrentWindowsIdentity("10.0.0.6"));
        Activate(coordinator, profile);
        var operations = new ActiveHostVmOperations(coordinator, new HostWmiContextResolver());

        HostVmWriteResult result = operations.WriteAsync(async (_, _) =>
        {
            HostSwitchResult switchResult = await coordinator.SwitchToLocalAsync();
            TestAssert.Equal(HostSwitchStatus.BlockedByActiveWrites, switchResult.Status);
            return HostVmBackendWriteResult.Success();
        }).GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.Succeeded, result.Status);
        TestAssert.Equal(profile.Id, coordinator.Current.ActiveSession.Target.ProfileId);
    }

    private static void LateReadFailureIsRejectedAfterHostSwitch()
    {
        (ActiveHostSessionCoordinator coordinator, HostProfile profile) =
            RemoteCoordinator(WmiContext.RemoteCurrentWindowsIdentity("10.0.0.6"));
        Activate(coordinator, profile);
        var operations = new ActiveHostVmOperations(coordinator, new HostWmiContextResolver());

        HostVmReadResult<string> result = operations.ReadAsync<string>(async (_, _) =>
        {
            HostSwitchResult switchResult = await coordinator.SwitchToLocalAsync();
            TestAssert.Equal(HostSwitchStatus.Succeeded, switchResult.Status);
            throw new InvalidOperationException("old-host failure");
        }).GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.Stale, result.Status);
        TestAssert.False(result.Message.Contains("old-host failure", StringComparison.Ordinal), "An old-host failure leaked into the new host result.");
    }

    private static void ExpectedOldStampRejectsReadBeforeBackend()
    {
        var coordinator = new ActiveHostSessionCoordinator();
        var operations = new ActiveHostVmOperations(coordinator, new HostWmiContextResolver());
        bool backendCalled = false;

        HostVmReadResult<string> result = operations.ReadAsync(
            (_, _) =>
            {
                backendCalled = true;
                return Task.FromResult("unexpected");
            },
            expectedStamp: new HostOperationStamp(99, null)).GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.Stale, result.Status);
        TestAssert.False(backendCalled, "An old-generation console read entered the WMI backend.");
    }

    private static void FrozenSwitchRejectsNewWrite()
    {
        var profile = new HostProfile(Guid.NewGuid(), "远程宿主", "10.0.0.6");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = WmiContext.RemoteCurrentWindowsIdentity(profile.Address);
        var connector = new FakeConnector(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return new FakeCandidate(profile, context);
        });
        var coordinator = new ActiveHostSessionCoordinator(connector, new FakeSnapshotLoader());
        coordinator.SelectProfile(profile);
        Task<HostSwitchResult> switching = coordinator.SwitchToSelectedAsync(Request(profile));
        entered.Task.GetAwaiter().GetResult();
        var operations = new ActiveHostVmOperations(coordinator, new HostWmiContextResolver());

        HostVmWriteResult result = operations.WriteAsync(
            (_, _) => Task.FromResult(HostVmBackendWriteResult.Success())).GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.WriteBlocked, result.Status);
        release.SetResult();
        TestAssert.Equal(HostSwitchStatus.Succeeded, switching.GetAwaiter().GetResult().Status);
    }

    private static void BackendFailureIsExplicit()
    {
        var operations = new ActiveHostVmOperations(
            new ActiveHostSessionCoordinator(),
            new HostWmiContextResolver());

        HostVmWriteResult result = operations.WriteAsync(
            (_, _) => Task.FromResult(HostVmBackendWriteResult.Failure("access denied")))
            .GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.Failed, result.Status);
        TestAssert.Contains("access denied", result.Message);
    }

    private static void RemoteStructuredReadFailureStartsReconnect()
    {
        (ActiveHostSessionCoordinator coordinator, HostProfile profile, Task reconnectStarted) =
            RemoteCoordinatorWithBlockedReconnect();
        Activate(coordinator, profile);
        var operations = new ActiveHostVmOperations(coordinator, new HostWmiContextResolver());
        var response = ApiResponse<string>.Fail(
            "RPC transport failed",
            (int)System.Management.ManagementStatus.TransportFailure,
            ApiErrorSource.Wmi);

        HostVmReadResult<string> result = operations.ReadAsync<string>(
            (_, _) => Task.FromException<string>(response.ToException("读取远程虚拟机清单失败")))
            .GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.Failed, result.Status);
        TestAssert.True(reconnectStarted.Wait(TimeSpan.FromSeconds(2)), "Structured read failure did not start reconnect.");
        TestAssert.True(coordinator.Current.ActiveSession.HasStaleData, "Structured read failure did not mark remote data stale.");
        coordinator.StopReconnect();
    }

    private static void RemoteStructuredWriteFailureStartsReconnect()
    {
        (ActiveHostSessionCoordinator coordinator, HostProfile profile, Task reconnectStarted) =
            RemoteCoordinatorWithBlockedReconnect();
        Activate(coordinator, profile);
        var operations = new ActiveHostVmOperations(coordinator, new HostWmiContextResolver());
        ApiResponse response = ApiResponse.Fail(
            "RPC server unavailable",
            (int)System.Management.ManagementStatus.TransportFailure,
            ApiErrorSource.Wmi);

        HostVmWriteResult result = operations.WriteAsync(
            (_, _) => Task.FromResult(HostVmBackendWriteResult.Failure(response)))
            .GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.Failed, result.Status);
        TestAssert.True(reconnectStarted.Wait(TimeSpan.FromSeconds(2)), "Structured write failure did not start reconnect.");
        TestAssert.True(coordinator.Current.ActiveSession.HasStaleData, "Structured write failure did not mark remote data stale.");
        coordinator.StopReconnect();
    }

    private static void RemoteBusinessFailureDoesNotStartReconnect()
    {
        WmiContext context = WmiContext.RemoteCurrentWindowsIdentity("10.0.0.6");
        (ActiveHostSessionCoordinator coordinator, HostProfile profile) = RemoteCoordinator(context);
        Activate(coordinator, profile);
        var operations = new ActiveHostVmOperations(coordinator, new HostWmiContextResolver());
        ApiResponse response = ApiResponse.Fail("access denied", 5, ApiErrorSource.Wmi);

        HostVmWriteResult result = operations.WriteAsync(
            (_, _) => Task.FromResult(HostVmBackendWriteResult.Failure(response)))
            .GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.Failed, result.Status);
        TestAssert.False(coordinator.Current.ActiveSession.HasStaleData, "A business failure incorrectly marked the remote session stale.");
        TestAssert.False(coordinator.Current.Reconnect.IsActive, "A business failure incorrectly started reconnect.");
    }

    private static void RemoteComFailureStartsReconnectInRelease()
    {
        (ActiveHostSessionCoordinator coordinator, HostProfile profile, Task reconnectStarted) =
            RemoteCoordinatorWithBlockedReconnect();
        Activate(coordinator, profile);
        var operations = new ActiveHostVmOperations(coordinator, new HostWmiContextResolver());
        var rpcFailure = new System.Runtime.InteropServices.COMException(
            "The RPC server is unavailable.",
            unchecked((int)0x800706BA));
        ApiResponse response = ApiResponse.Fail(
            rpcFailure.Message,
            -1,
            ApiErrorSource.None,
            rpcFailure);

        HostVmWriteResult result = operations.WriteAsync(
            (_, _) => Task.FromResult(HostVmBackendWriteResult.Failure(response)))
            .GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.Failed, result.Status);
        TestAssert.True(reconnectStarted.Wait(TimeSpan.FromSeconds(2)), "Release ApiResponse discarded the RPC failure cause.");
        TestAssert.True(coordinator.Current.ActiveSession.HasStaleData, "RPC failure did not mark remote data stale.");
        coordinator.StopReconnect();
    }

    private static void ConfirmedOldHostWriteIsRejected()
    {
        (ActiveHostSessionCoordinator coordinator, HostProfile profile) =
            RemoteCoordinator(WmiContext.RemoteCurrentWindowsIdentity("10.0.0.6"));
        Activate(coordinator, profile);
        HostOperationStamp confirmedStamp = coordinator.CaptureOperationStamp();
        TestAssert.Equal(
            HostSwitchStatus.Succeeded,
            coordinator.SwitchToLocalAsync().GetAwaiter().GetResult().Status);
        var operations = new ActiveHostVmOperations(coordinator, new HostWmiContextResolver());
        bool backendCalled = false;

        HostVmWriteResult result = operations.WriteAsync(
            (_, _) =>
            {
                backendCalled = true;
                return Task.FromResult(HostVmBackendWriteResult.Success());
            },
            confirmedStamp).GetAwaiter().GetResult();

        TestAssert.Equal(HostVmOperationStatus.Stale, result.Status);
        TestAssert.False(backendCalled, "A write confirmed for the old host reached the new host backend.");
    }

    private static (ActiveHostSessionCoordinator Coordinator, HostProfile Profile) RemoteCoordinator(WmiContext context)
    {
        var profile = new HostProfile(Guid.NewGuid(), "远程宿主", context.Host);
        return (
            new ActiveHostSessionCoordinator(
                new FakeConnector((_, _) => Task.FromResult<IHostSessionCandidate>(new FakeCandidate(profile, context))),
                new FakeSnapshotLoader()),
            profile);
    }

    private static (ActiveHostSessionCoordinator Coordinator, HostProfile Profile, Task ReconnectStarted)
        RemoteCoordinatorWithBlockedReconnect()
    {
        WmiContext context = WmiContext.RemoteCurrentWindowsIdentity("10.0.0.6");
        var profile = new HostProfile(Guid.NewGuid(), "远程宿主", context.Host);
        var reconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var connector = new FakeConnector(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return new FakeCandidate(profile, context);
            reconnectStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable reconnect continuation.");
        });
        return (
            new ActiveHostSessionCoordinator(connector, new FakeSnapshotLoader()),
            profile,
            reconnectStarted.Task);
    }

    private static void Activate(ActiveHostSessionCoordinator coordinator, HostProfile profile)
    {
        coordinator.SelectProfile(profile);
        TestAssert.Equal(
            HostSwitchStatus.Succeeded,
            coordinator.SwitchToSelectedAsync(Request(profile)).GetAwaiter().GetResult().Status);
    }

    private static HostSwitchRequest Request(HostProfile profile) =>
        new(profile, HostChannelState.Available, HostChannelState.Available);

    private sealed class FakeConnection(WmiContext context) : IWmiHostManagementConnection
    {
        public WmiContext Context { get; } = context;
    }

    private sealed class FakeCandidate(HostProfile profile, WmiContext context) : IHostSessionCandidate
    {
        public HostTarget Target { get; } = HostTarget.FromProfile(profile);
        public IHostManagementConnection ManagementConnection { get; } = new FakeConnection(context);
        public HostChannelState ManagementChannel => HostChannelState.Available;
        public HostChannelState ConsoleChannel => HostChannelState.Available;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConnector(
        Func<HostSwitchRequest, CancellationToken, Task<IHostSessionCandidate>> connect) : IHostSessionConnector
    {
        public Task<IHostSessionCandidate> ConnectAsync(HostSwitchRequest request, CancellationToken cancellationToken) =>
            connect(request, cancellationToken);
    }

    private sealed class FakeSnapshotLoader : IHostBasicSnapshotLoader
    {
        public Task<HostBasicSnapshot> LoadAsync(IHostSessionCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult(new HostBasicSnapshot(
                candidate.Target.DisplayName,
                "Windows",
                "Running",
                1,
                DateTimeOffset.UtcNow));
    }
}
