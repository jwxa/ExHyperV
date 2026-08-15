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
        ("Console_RemoteCaptureUsesActiveHostIpv4", RemoteCaptureUsesActiveHostIpv4),
        ("Console_Unavailable2179RejectsCapture", Unavailable2179RejectsCapture),
        ("Console_StaleHostDataRejectsCapture", StaleHostDataRejectsCapture),
        ("Console_InvalidVmIdRejectsCapture", InvalidVmIdRejectsCapture),
        ("Console_HostSwitchInvalidatesCapturedSession", HostSwitchInvalidatesCapturedSession),
        ("Console_WindowIdentityIsScopedToHostGeneration", WindowIdentityIsScopedToHostGeneration),
        ("Console_RegistryCaptureUsesOwningHostId", RegistryCaptureUsesOwningHostId),
        ("Console_UnexpectedDisconnectReportsConnectionLoss", UnexpectedDisconnectReportsConnectionLoss)
    ];

    private static void LocalCaptureUsesLocalhost2179()
    {
        var coordinator = new ActiveHostSessionCoordinator();
        var sessions = new ActiveHostConsoleSessions(coordinator);
        Guid vmId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        HostConsoleSessionCapture result = sessions.Capture(vmId.ToString(), "本机虚拟机");

        TestAssert.True(result.Succeeded, result.Message);
        TestAssert.True(result.Session!.Target.IsLocal, "Local capture did not retain the local target.");
        TestAssert.Equal("localhost", result.Session.Server);
        TestAssert.Equal(2179, result.Session.Port);
        TestAssert.Equal(vmId, result.Session.VmId);
        TestAssert.Equal("本机虚拟机", result.Session.VmName);
    }

    private static void RemoteCaptureUsesActiveHostIpv4()
    {
        var coordinator = new ActiveHostSessionCoordinator();
        HostProfile profile = RemoteProfile("10.0.0.6");
        coordinator.SelectProfile(profile);
        coordinator.CommitActiveSession(RemoteSession(2, profile, HostChannelState.Available));
        var sessions = new ActiveHostConsoleSessions(coordinator);

        HostConsoleSessionCapture result = sessions.Capture(
            "22222222-2222-2222-2222-222222222222",
            "远程虚拟机");

        TestAssert.True(result.Succeeded, result.Message);
        TestAssert.False(result.Session!.Target.IsLocal, "Remote capture was mapped to the local target.");
        TestAssert.Equal("10.0.0.6", result.Session.Server);
        TestAssert.Equal(new HostOperationStamp(2, profile.Id), result.Session.Stamp);
    }

    private static void Unavailable2179RejectsCapture()
    {
        var coordinator = new ActiveHostSessionCoordinator();
        HostProfile profile = RemoteProfile("10.0.0.6");
        coordinator.SelectProfile(profile);
        coordinator.CommitActiveSession(RemoteSession(2, profile, HostChannelState.Unavailable));
        var sessions = new ActiveHostConsoleSessions(coordinator);

        HostConsoleSessionCapture result = sessions.Capture(
            "33333333-3333-3333-3333-333333333333",
            "无控制台通道");

        TestAssert.False(result.Succeeded, "Capture succeeded while TCP 2179 was unavailable.");
        TestAssert.Null(result.Session, "Unavailable console capture returned a session.");
        TestAssert.Contains("TCP 2179", result.Message);
    }

    private static void HostSwitchInvalidatesCapturedSession()
    {
        var coordinator = new ActiveHostSessionCoordinator();
        HostProfile first = RemoteProfile("10.0.0.6");
        coordinator.SelectProfile(first);
        coordinator.CommitActiveSession(RemoteSession(2, first, HostChannelState.Available));
        var sessions = new ActiveHostConsoleSessions(coordinator);
        ActiveHostConsoleSession captured = sessions.Capture(
            "44444444-4444-4444-4444-444444444444",
            "切换前虚拟机").Session!;

        HostProfile second = RemoteProfile("10.0.0.7");
        coordinator.SelectProfile(second);
        coordinator.CommitActiveSession(RemoteSession(3, second, HostChannelState.Available));

        TestAssert.False(sessions.IsCurrent(captured), "Old-host console remained current after switching hosts.");
    }

    private static void StaleHostDataRejectsCapture()
    {
        var coordinator = new ActiveHostSessionCoordinator();
        HostProfile profile = RemoteProfile("10.0.0.6");
        coordinator.SelectProfile(profile);
        coordinator.CommitActiveSession(RemoteSession(
            2,
            profile,
            HostChannelState.Available,
            hasStaleData: true));
        var sessions = new ActiveHostConsoleSessions(coordinator);

        HostConsoleSessionCapture result = sessions.Capture(
            "66666666-6666-6666-6666-666666666666",
            "旧数据虚拟机");

        TestAssert.False(result.Succeeded, "A stale host session exposed a console target.");
        TestAssert.Contains("中断", result.Message);
    }

    private static void InvalidVmIdRejectsCapture()
    {
        var sessions = new ActiveHostConsoleSessions(new ActiveHostSessionCoordinator());

        HostConsoleSessionCapture result = sessions.Capture("not-a-guid", "无效虚拟机");

        TestAssert.False(result.Succeeded, "An invalid VM identifier created a console session.");
        TestAssert.Contains("标识无效", result.Message);
    }

    private static void WindowIdentityIsScopedToHostGeneration()
    {
        var coordinator = new ActiveHostSessionCoordinator();
        HostProfile profile = RemoteProfile("10.0.0.6");
        Guid vmId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        coordinator.SelectProfile(profile);
        coordinator.CommitActiveSession(RemoteSession(2, profile, HostChannelState.Available));
        var sessions = new ActiveHostConsoleSessions(coordinator);
        ActiveHostConsoleSession first = sessions.Capture(vmId.ToString(), "同一虚拟机").Session!;

        coordinator.CommitActiveSession(RemoteSession(3, profile, HostChannelState.Available));
        ActiveHostConsoleSession second = sessions.Capture(vmId.ToString(), "同一虚拟机").Session!;

        TestAssert.False(
            string.Equals(first.WindowKey, second.WindowKey, StringComparison.Ordinal),
            "Console window identity was reused across host generations.");
    }

    private static void RegistryCaptureUsesOwningHostId()
    {
        var registry = new HostSessionRegistry(new RegistryConsoleConnector(), new RegistryConsoleSnapshotLoader());
        HostProfile profile = RemoteProfile("10.0.0.6");
        HostId remoteHostId = HostId.FromProfile(profile);
        Guid vmId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        try
        {
            HostConnectResult connected = registry.ConnectAsync(new HostConnectRequest(
                profile,
                HostChannelState.Available,
                HostChannelState.Available)).GetAwaiter().GetResult();
            TestAssert.True(connected.Succeeded, connected.Message);
            var sessions = new ActiveHostConsoleSessions(registry);

            ActiveHostConsoleSession local = sessions.Capture(
                HostId.Local,
                vmId.ToString(),
                "同 ID 本机虚拟机").Session!;
            ActiveHostConsoleSession remote = sessions.Capture(
                remoteHostId,
                vmId.ToString(),
                "同 ID 远程虚拟机").Session!;

            TestAssert.Equal("localhost", local.Server);
            TestAssert.Equal(profile.Address, remote.Server);
            TestAssert.False(local.WindowKey == remote.WindowKey,
                "The same VM ID on different hosts reused one console window identity.");
        }
        finally
        {
            registry.Shutdown();
        }
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
        TestAssert.Contains("ActiveHostSessions.Registry", windowSource);
        TestAssert.Contains("_session.Target.IsLocal", viewModelSource);
        TestAssert.Contains("read.Value is null || !read.Value.IsRunning", viewModelSource);
        TestAssert.Contains("_sessionRegistry.ReportConnectionLoss(_session.Stamp, reason)", viewModelSource);
        TestAssert.Contains("_session.Stamp,", viewModelSource);
    }

    private static HostProfile RemoteProfile(string address) =>
        new(Guid.NewGuid(), $"宿主 {address}", address);

    private static ActiveHostSession RemoteSession(
        long generation,
        HostProfile profile,
        HostChannelState consoleChannel,
        bool hasStaleData = false) =>
        new(
            generation,
            HostTarget.FromProfile(profile),
            consoleChannel == HostChannelState.Available
                ? HostConnectionState.Connected
                : HostConnectionState.PartiallyAvailable,
            HostChannelState.Available,
            consoleChannel,
            HasStaleData: hasStaleData);

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

    private sealed class RegistryConsoleConnection(WmiContext context) : IWmiHostManagementConnection
    {
        public WmiContext Context { get; } = context;
    }

    private sealed class RegistryConsoleCandidate(HostProfile profile) : IHostSessionCandidate
    {
        public HostTarget Target { get; } = HostTarget.FromProfile(profile);
        public IHostManagementConnection ManagementConnection { get; } =
            new RegistryConsoleConnection(WmiContext.RemoteCurrentWindowsIdentity(profile.Address));
        public HostChannelState ManagementChannel => HostChannelState.Available;
        public HostChannelState ConsoleChannel => HostChannelState.Available;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RegistryConsoleConnector : IHostSessionConnector
    {
        public Task<IHostSessionCandidate> ConnectAsync(
            HostSwitchRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<IHostSessionCandidate>(new RegistryConsoleCandidate(request.Profile));
    }

    private sealed class RegistryConsoleSnapshotLoader : IHostBasicSnapshotLoader
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
