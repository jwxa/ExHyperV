using ExHyperV.Services.Remote.Consoles;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Services.Remote.Vms;
using ExHyperV.Tools;

internal static class ConsoleSessionTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Console_LocalCaptureUsesLocalhost2179", LocalCaptureUsesLocalhost2179),
        ("Console_RemoteCaptureUsesOwningHostIpv4", RemoteCaptureUsesOwningHostIpv4),
        ("Console_Unavailable2179RejectsCapture", Unavailable2179RejectsCapture),
        ("Console_StaleHostDataRejectsCapture", StaleHostDataRejectsCapture),
        ("Console_InvalidVmIdRejectsCapture", InvalidVmIdRejectsCapture),
        ("Console_SameVmIdIsScopedByHost", SameVmIdIsScopedByHost),
        ("Console_UnexpectedDisconnectReportsConnectionLoss", UnexpectedDisconnectReportsConnectionLoss)
    ];

    private static void LocalCaptureUsesLocalhost2179()
    {
        var registry = new HostSessionRegistry();
        var sessions = new HostConsoleSessions(registry);
        Guid vmId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        HostConsoleSessionCapture result = sessions.Capture(HostId.Local, vmId.ToString(), "本机虚拟机");

        TestAssert.True(result.Succeeded, result.Message);
        TestAssert.True(result.Session!.Target.IsLocal, "Local capture did not retain the local target.");
        TestAssert.Equal("localhost", result.Session.Server);
        TestAssert.Equal(2179, result.Session.Port);
        registry.Shutdown();
    }

    private static void RemoteCaptureUsesOwningHostIpv4()
    {
        HostProfile profile = RemoteProfile("10.0.0.6");
        HostSessionRegistry registry = ConnectedRegistry(profile, HostChannelState.Available);
        HostId hostId = HostId.FromProfile(profile);
        var sessions = new HostConsoleSessions(registry);

        HostConsoleSessionCapture result = sessions.Capture(
            hostId,
            "22222222-2222-2222-2222-222222222222",
            "远程虚拟机");

        TestAssert.True(result.Succeeded, result.Message);
        TestAssert.Equal(profile.Address, result.Session!.Server);
        TestAssert.Equal(profile.Id, result.Session.Stamp.ProfileId);
        registry.Shutdown();
    }

    private static void Unavailable2179RejectsCapture()
    {
        HostProfile profile = RemoteProfile("10.0.0.6");
        HostSessionRegistry registry = ConnectedRegistry(profile, HostChannelState.Unavailable);
        var sessions = new HostConsoleSessions(registry);

        HostConsoleSessionCapture result = sessions.Capture(
            HostId.FromProfile(profile),
            "33333333-3333-3333-3333-333333333333",
            "无控制台通道");

        TestAssert.False(result.Succeeded, "Capture succeeded while TCP 2179 was unavailable.");
        TestAssert.Contains("TCP 2179", result.Message);
        registry.Shutdown();
    }

    private static void StaleHostDataRejectsCapture()
    {
        HostProfile profile = RemoteProfile("10.0.0.6");
        var reconnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new ConsoleConnector(
            HostChannelState.Available,
            reconnectEntered);
        var registry = new HostSessionRegistry(connector, new ConsoleSnapshotLoader());
        HostId hostId = HostId.FromProfile(profile);
        Connect(registry, profile, HostChannelState.Available);
        HostOperationStamp stamp = registry.CaptureOperationStamp(hostId);

        TestAssert.True(registry.ReportConnectionLoss(stamp, "模拟控制台宿主断线。"),
            "The registry rejected the owning host connection loss.");
        TestAssert.True(SpinWait.SpinUntil(
            () => registry.Current.GetRequired(hostId).HasStaleData,
            TimeSpan.FromSeconds(2)), "The target host did not become stale.");
        HostConsoleSessionCapture result = new HostConsoleSessions(registry).Capture(
            hostId,
            "44444444-4444-4444-4444-444444444444",
            "旧数据虚拟机");

        TestAssert.False(result.Succeeded, "A stale host session exposed a console target.");
        TestAssert.Contains("中断", result.Message);
        registry.StopReconnect(hostId);
        registry.Shutdown();
    }

    private static void InvalidVmIdRejectsCapture()
    {
        var registry = new HostSessionRegistry();
        HostConsoleSessionCapture result = new HostConsoleSessions(registry).Capture(
            HostId.Local,
            "not-a-guid",
            "无效虚拟机");

        TestAssert.False(result.Succeeded, "An invalid VM identifier created a console session.");
        TestAssert.Contains("标识无效", result.Message);
        registry.Shutdown();
    }

    private static void SameVmIdIsScopedByHost()
    {
        HostProfile profile = RemoteProfile("10.0.0.6");
        HostSessionRegistry registry = ConnectedRegistry(profile, HostChannelState.Available);
        var sessions = new HostConsoleSessions(registry);
        Guid vmId = Guid.Parse("55555555-5555-5555-5555-555555555555");

        HostConsoleSession local = sessions.Capture(HostId.Local, vmId.ToString(), "本机虚拟机").Session!;
        HostConsoleSession remote = sessions.Capture(
            HostId.FromProfile(profile), vmId.ToString(), "远程虚拟机").Session!;

        TestAssert.False(local.WindowKey == remote.WindowKey,
            "The same VM ID on different hosts reused one console window identity.");
        registry.Shutdown();
    }

    private static void UnexpectedDisconnectReportsConnectionLoss()
    {
        string root = FindRepositoryRoot();
        string windowSource = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Windows", "ConsoleWindow.xaml.cs"));
        string viewModelSource = File.ReadAllText(Path.Combine(
            root, "src", "ViewModels", "ConsoleViewModel.cs"));

        TestAssert.Contains("RdpHost.Disconnected +=", windowSource);
        TestAssert.Contains("RdpHost.FatalError +=", windowSource);
        TestAssert.Contains("ReportUnexpectedConnectionLossAsync", windowSource);
        TestAssert.Contains("HostSessions.Registry", windowSource);
        TestAssert.Contains("_session.Target.IsLocal", viewModelSource);
        TestAssert.Contains("_sessionRegistry.ReportConnectionLoss(_session.Stamp, reason)", viewModelSource);
    }

    private static HostSessionRegistry ConnectedRegistry(
        HostProfile profile,
        HostChannelState consoleChannel)
    {
        var registry = new HostSessionRegistry(
            new ConsoleConnector(consoleChannel),
            new ConsoleSnapshotLoader());
        Connect(registry, profile, consoleChannel);
        return registry;
    }

    private static void Connect(
        HostSessionRegistry registry,
        HostProfile profile,
        HostChannelState consoleChannel)
    {
        HostConnectResult result = registry.ConnectAsync(new HostConnectRequest(
            profile,
            HostChannelState.Available,
            consoleChannel)).GetAwaiter().GetResult();
        TestAssert.True(result.Succeeded, result.Message);
    }

    private static HostProfile RemoteProfile(string address) =>
        new(Guid.NewGuid(), $"宿主 {address}", address);

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

    private sealed class ConsoleConnection(WmiContext context) : IWmiHostManagementConnection
    {
        public WmiContext Context { get; } = context;
    }

    private sealed class ConsoleCandidate(HostProfile profile, HostChannelState consoleChannel) : IHostSessionCandidate
    {
        public HostTarget Target { get; } = HostTarget.FromProfile(profile);
        public IHostManagementConnection ManagementConnection { get; } =
            new ConsoleConnection(WmiContext.RemoteCurrentWindowsIdentity(profile.Address));
        public HostChannelState ManagementChannel => HostChannelState.Available;
        public HostChannelState ConsoleChannel { get; } = consoleChannel;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ConsoleConnector(
        HostChannelState consoleChannel,
        TaskCompletionSource? reconnectEntered = null) : IHostSessionConnector
    {
        private int _calls;

        public async Task<IHostSessionCandidate> ConnectAsync(
            HostSwitchRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) > 1 && reconnectEntered is not null)
            {
                reconnectEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new ConsoleCandidate(request.Profile, consoleChannel);
        }
    }

    private sealed class ConsoleSnapshotLoader : IHostBasicSnapshotLoader
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
}
