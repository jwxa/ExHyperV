using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Services.Remote.Vms;
using ExHyperV.Tools;

internal static class HostOperationRouterTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("HostRouter_ReadsUseExplicitLocalOrRemoteHostId", ReadsUseExplicitLocalOrRemoteHostId),
        ("HostRouter_WritesAndStaleResultsAreScopedToTargetHost", WritesAndStaleResultsAreScopedToTargetHost)
    ];

    private static void ReadsUseExplicitLocalOrRemoteHostId()
    {
        var connector = new RouterConnector();
        var registry = new HostSessionRegistry(connector, new RouterSnapshotLoader());
        IHostOperationRouter router = new HostOperationRouter(registry, new HostWmiContextResolver());
        var profile = new HostProfile(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            "路由测试宿主",
            "10.0.0.10");
        HostId remoteHostId = HostId.FromProfile(profile);

        try
        {
            HostConnectResult connected = registry.ConnectAsync(new HostConnectRequest(
                profile,
                HostChannelState.Available,
                HostChannelState.Available)).GetAwaiter().GetResult();
            TestAssert.True(connected.Succeeded, connected.Message);

            WmiContext? remoteContext = null;
            HostVmReadResult<string> remote = router.ReadAsync(
                remoteHostId,
                (context, _) =>
                {
                    remoteContext = context;
                    return Task.FromResult("remote");
                }).GetAwaiter().GetResult();

            WmiContext? localContext = null;
            HostVmReadResult<string> local = router.ReadAsync(
                HostId.Local,
                (context, _) =>
                {
                    localContext = context;
                    return Task.FromResult("local");
                }).GetAwaiter().GetResult();

            TestAssert.True(remote.Succeeded, remote.Message);
            TestAssert.Equal("remote", remote.Value);
            TestAssert.Equal(profile.Address, remoteContext!.Host);
            TestAssert.False(remoteContext.IsLocal, "The remote HostId resolved a local WMI context.");
            TestAssert.True(local.Succeeded, local.Message);
            TestAssert.Equal("local", local.Value);
            TestAssert.True(localContext!.IsLocal, "HostId.Local resolved a remote WMI context.");
        }
        finally
        {
            registry.Shutdown();
        }
    }

    private static void WritesAndStaleResultsAreScopedToTargetHost()
    {
        var connector = new RouterConnector();
        var registry = new HostSessionRegistry(
            connector,
            new RouterSnapshotLoader(),
            new BlockingReconnectScheduler());
        IHostOperationRouter router = new HostOperationRouter(registry, new HostWmiContextResolver());
        var profile = new HostProfile(
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            "写入测试宿主",
            "10.0.0.11");
        HostId remoteHostId = HostId.FromProfile(profile);
        var remoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            TestAssert.True(
                registry.ConnectAsync(new HostConnectRequest(
                    profile,
                    HostChannelState.Available,
                    HostChannelState.Available)).GetAwaiter().GetResult().Succeeded,
                "The remote test host did not connect.");
            HostOperationStamp localStamp = registry.CaptureOperationStamp(HostId.Local);

            Task<HostVmWriteResult> remoteWrite = router.WriteAsync(
                remoteHostId,
                async (context, token) =>
                {
                    remoteEntered.SetResult();
                    await releaseRemote.Task.WaitAsync(token);
                    return HostVmBackendWriteResult.Success();
                });
            TestAssert.True(remoteEntered.Task.Wait(TimeSpan.FromSeconds(5)), "The remote write did not start.");

            HostVmWriteResult localWrite = router.WriteAsync(
                HostId.Local,
                (context, token) => Task.FromResult(HostVmBackendWriteResult.Success())).GetAwaiter().GetResult();
            TestAssert.True(localWrite.Succeeded, localWrite.Message);

            releaseRemote.SetResult();
            TestAssert.True(remoteWrite.GetAwaiter().GetResult().Succeeded, "The released remote write did not complete.");

            HostOperationStamp remoteStamp = registry.CaptureOperationStamp(remoteHostId);
            HostVmWriteResult staleWrite = router.WriteAsync(
                remoteHostId,
                (context, token) =>
                {
                    TestAssert.True(
                        registry.ReportConnectionLoss(remoteStamp, "模拟写入期间连接丢失。"),
                        "The current remote stamp did not report connection loss.");
                    return Task.FromResult(HostVmBackendWriteResult.Success());
                }).GetAwaiter().GetResult();

            TestAssert.Equal(HostVmOperationStatus.Stale, staleWrite.Status);
            TestAssert.True(registry.CanApply(localStamp), "Remote loss invalidated the local host stamp.");
        }
        finally
        {
            releaseRemote.TrySetResult();
            registry.Shutdown();
        }
    }

    private sealed class RouterConnection(WmiContext context) : IWmiHostManagementConnection
    {
        public WmiContext Context { get; } = context;
    }

    private sealed class RouterCandidate(HostProfile profile) : IHostSessionCandidate
    {
        public HostTarget Target { get; } = HostTarget.FromProfile(profile);
        public IHostManagementConnection ManagementConnection { get; } =
            new RouterConnection(WmiContext.RemoteCurrentWindowsIdentity(profile.Address));
        public HostChannelState ManagementChannel => HostChannelState.Available;
        public HostChannelState ConsoleChannel => HostChannelState.Available;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RouterConnector : IHostSessionConnector
    {
        public Task<IHostSessionCandidate> ConnectAsync(
            HostSwitchRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<IHostSessionCandidate>(new RouterCandidate(request.Profile));
    }

    private sealed class RouterSnapshotLoader : IHostBasicSnapshotLoader
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

    private sealed class BlockingReconnectScheduler : IReconnectScheduler
    {
        public DateTimeOffset UtcNow => new(2026, 8, 15, 18, 0, 0, TimeSpan.FromHours(8));

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
