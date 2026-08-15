using ExHyperV.Services.Remote.Consoles;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;

internal static class HostConsoleRegistryTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("ConsoleRegistry_TracksByHostAndVmAndActivatesExisting", TracksByHostAndVmAndActivatesExisting),
        ("ConsoleRegistry_UnregisterRequiresMatchingWindow", UnregisterRequiresMatchingWindow),
        ("ConsoleRegistry_CloseAllTargetsOnlyOneHost", CloseAllTargetsOnlyOneHost)
    ];

    private static void TracksByHostAndVmAndActivatesExisting()
    {
        var registry = new HostConsoleRegistry();
        ActiveHostConsoleSession session = Session(ProfileA, VmA1, generation: 2);
        var window = new FakeConsoleWindow();

        registry.Register(session, window);

        TestAssert.Equal(1, registry.Count(session.HostId));
        TestAssert.Equal(session.HostId, registry.GetOpenWindows(session.HostId).Single().HostId);
        TestAssert.Equal(session.VmKey, registry.GetOpenWindows(session.HostId).Single().VmKey);
        TestAssert.True(registry.TryActivate(session.WindowKey), "Registered console was not activated.");
        TestAssert.Equal(1, window.ActivateCount);
        TestAssert.False(registry.TryActivate("missing-window"), "Unknown console key was activated.");
    }

    private static void UnregisterRequiresMatchingWindow()
    {
        var registry = new HostConsoleRegistry();
        ActiveHostConsoleSession session = Session(ProfileA, VmA1, generation: 2);
        var registered = new FakeConsoleWindow();
        registry.Register(session, registered);

        TestAssert.False(
            registry.Unregister(session.WindowKey, new FakeConsoleWindow()),
            "A different window instance removed the registered console.");
        TestAssert.Equal(1, registry.Count(session.HostId));
        TestAssert.True(registry.Unregister(session.WindowKey, registered),
            "The registered window did not unregister.");
        TestAssert.Equal(0, registry.Count(session.HostId));
    }

    private static void CloseAllTargetsOnlyOneHost()
    {
        var registry = new HostConsoleRegistry();
        ActiveHostConsoleSession sessionA1 = Session(ProfileA, VmA1, generation: 2);
        ActiveHostConsoleSession sessionA2 = Session(ProfileA, VmA2, generation: 2);
        ActiveHostConsoleSession sessionB = Session(ProfileB, VmA1, generation: 3);
        var windowA1 = new FakeConsoleWindow();
        var windowA2 = new FakeConsoleWindow();
        var windowB = new FakeConsoleWindow();
        registry.Register(sessionA1, windowA1);
        registry.Register(sessionA2, windowA2);
        registry.Register(sessionB, windowB);

        HostConsoleCloseResult result = registry.CloseAll(sessionA1.HostId);

        TestAssert.True(result.Succeeded, result.Message);
        TestAssert.Equal(2, result.RequestedCount);
        TestAssert.Equal(2, result.ClosedCount);
        TestAssert.Equal(1, windowA1.CloseCount);
        TestAssert.Equal(1, windowA2.CloseCount);
        TestAssert.Equal(0, windowB.CloseCount);
        TestAssert.Equal(0, registry.Count(sessionA1.HostId));
        TestAssert.Equal(1, registry.Count(sessionB.HostId));
    }

    private static ActiveHostConsoleSession Session(HostProfile profile, Guid vmId, long generation)
    {
        HostTarget target = HostTarget.FromProfile(profile);
        return new ActiveHostConsoleSession(
            target,
            new HostOperationStamp(generation, profile.Id),
            vmId,
            $"VM {vmId:N}",
            profile.Address,
            ActiveHostConsoleSessions.ConsolePort,
            $"{generation}:{profile.Id:N}:{vmId:N}");
    }

    private static readonly HostProfile ProfileA = new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "宿主 A",
        "10.0.0.6");
    private static readonly HostProfile ProfileB = new(
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        "宿主 B",
        "10.0.0.7");
    private static readonly Guid VmA1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid VmA2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class FakeConsoleWindow : IHostConsoleWindow
    {
        public int ActivateCount { get; private set; }
        public int CloseCount { get; private set; }

        public void Activate() => ActivateCount++;
        public void Close() => CloseCount++;
    }
}
