internal static class ConnectionBarRegressionTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Console_MultiMonitorUsesNativeConnectionBarOnly", MultiMonitorUsesNativeConnectionBarOnly)
    ];

    private static void MultiMonitorUsesNativeConnectionBarOnly()
    {
        string root = FindRepositoryRoot();
        string axHostSource = File.ReadAllText(Path.Combine(
            root, "src", "Tools", "Controls", "MsRdpAxHost.cs"));
        string windowSource = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Windows", "ConsoleWindow.xaml.cs"));
        string overlayPath = Path.Combine(
            root, "src", "Views", "Windows", "MultiMonitorConnectionBarOverlay.cs");

        int displayBar = axHostSource.IndexOf(
            "adv.DisplayConnectionBar = true",
            StringComparison.Ordinal);
        int pinBar = axHostSource.IndexOf(
            "adv.PinConnectionBar = true",
            StringComparison.Ordinal);
        int showPinButton = axHostSource.IndexOf(
            "adv.ConnectionBarShowPinButton = true",
            StringComparison.Ordinal);
        int connect = axHostSource.IndexOf("rdp.Connect();", StringComparison.Ordinal);

        TestAssert.True(
            displayBar >= 0
                && pinBar > displayBar
                && showPinButton > pinBar
                && connect > showPinButton,
            "The native RDP connection bar and its pin control are not enabled before connecting.");
        TestAssert.Contains("NativeConnectionBarRequested\"] = true", axHostSource);
        TestAssert.False(
            File.Exists(overlayPath),
            "The application-owned connection bar still exists alongside the native RDP bar.");
        TestAssert.False(
            windowSource.Contains("MultiMonitorConnectionBarOverlay", StringComparison.Ordinal),
            "ConsoleWindow still creates an application-owned connection bar.");
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

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
