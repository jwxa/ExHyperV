internal static class ReleaseConvergenceTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Release_NoGlobalActiveHostCompatibilityPath", NoGlobalActiveHostCompatibilityPath),
        ("Release_LocalCapabilitiesUseExplicitLocalHost", LocalCapabilitiesUseExplicitLocalHost),
        ("Release_ControlledRunnerUsesMultiHostRegistry", ControlledRunnerUsesMultiHostRegistry),
        ("Release_RemoteSurfacesUseThemeResources", RemoteSurfacesUseThemeResources),
        ("Release_UserDocsDescribeMultiHostBehavior", UserDocsDescribeMultiHostBehavior)
    ];

    private static void NoGlobalActiveHostCompatibilityPath()
    {
        string root = FindRepositoryRoot();
        string sessions = Path.Combine(root, "src", "Services", "Remote", "Sessions");
        string vms = Path.Combine(root, "src", "Services", "Remote", "Vms");

        TestAssert.False(File.Exists(Path.Combine(sessions, "IActiveHostSessionCoordinator.cs")),
            "The legacy global active-host interface still exists.");
        TestAssert.False(File.Exists(Path.Combine(sessions, "ActiveHostSessions.cs")),
            "The legacy global active-host service locator still exists.");
        TestAssert.False(File.Exists(Path.Combine(vms, "ActiveHostVmOperations.cs")),
            "The legacy active-host VM operation adapter still exists.");

        string productSource = ReadSourceTree(Path.Combine(root, "src"));
        TestAssert.False(productSource.Contains("ActiveHostSessions.Current", StringComparison.Ordinal),
            "Product code still reads the global active host.");
        TestAssert.False(productSource.Contains("IActiveHostSessionCoordinator", StringComparison.Ordinal),
            "Product code still exposes the global active-host coordinator interface.");
        TestAssert.False(productSource.Contains("SwitchToLocalAsync(", StringComparison.Ordinal),
            "Product code still exposes the legacy switch-back-to-local flow.");
    }

    private static void LocalCapabilitiesUseExplicitLocalHost()
    {
        string root = FindRepositoryRoot();
        string pageBase = File.ReadAllText(Path.Combine(root, "src", "ViewModels", "PageViewModelBase.cs"));
        string mainWindow = File.ReadAllText(Path.Combine(root, "src", "Views", "Windows", "MainWindow.xaml.cs"));
        string usb = File.ReadAllText(Path.Combine(root, "src", "ViewModels", "USBPageViewModel.cs"));

        TestAssert.Contains("HostId.Local", pageBase);
        TestAssert.Contains("HostSessions.Registry", pageBase);
        TestAssert.Contains("HostSessions.Registry", mainWindow);
        TestAssert.Contains("HostSessions.Registry", usb);
        TestAssert.False(mainWindow.Contains("OnActiveHostStateChanged", StringComparison.Ordinal),
            "Main navigation still reacts to a global active-host change.");
    }

    private static void ControlledRunnerUsesMultiHostRegistry()
    {
        string root = FindRepositoryRoot();
        string runner = File.ReadAllText(Path.Combine(
            root, "tests", "ExHyperV.IntegrationTests", "ControlledHostAcceptanceRunner.cs"));

        TestAssert.Contains("HostSessionRegistry", runner);
        TestAssert.Contains("HostOperationRouter", runner);
        TestAssert.Contains("TryPrepareDisconnect", runner);
        TestAssert.False(runner.Contains("ActiveHostVmOperations", StringComparison.Ordinal),
            "Controlled acceptance still routes VM operations through the active-host adapter.");
        TestAssert.False(runner.Contains("SwitchToLocalAsync", StringComparison.Ordinal),
            "Controlled acceptance still uses the switch-back-to-local flow.");
    }

    private static void RemoteSurfacesUseThemeResources()
    {
        string root = FindRepositoryRoot();
        string hostPage = File.ReadAllText(Path.Combine(root, "src", "Views", "Pages", "HostConnectionPage.xaml"));
        string vmPage = File.ReadAllText(Path.Combine(root, "src", "Views", "Pages", "VirtualMachinesPage.xaml"));

        TestAssert.False(hostPage.Contains("#0C0C0C", StringComparison.OrdinalIgnoreCase),
            "Host logs still use a hardcoded black background.");
        TestAssert.False(hostPage.Contains("#D0D0D0", StringComparison.OrdinalIgnoreCase),
            "Host logs still use a hardcoded gray foreground.");
        TestAssert.False(vmPage.Contains("#0B0B0B", StringComparison.OrdinalIgnoreCase),
            "The VM detail monitor still uses a hardcoded black background.");
        TestAssert.Contains("ControlFillColorSecondaryBrush", hostPage);
        TestAssert.Contains("ControlFillColorSecondaryBrush", vmPage);
    }

    private static void UserDocsDescribeMultiHostBehavior()
    {
        string root = FindRepositoryRoot();
        string chinese = File.ReadAllText(Path.Combine(root, "README_zh.md"));
        string english = File.ReadAllText(Path.Combine(root, "README.md"));
        string guide = File.ReadAllText(Path.Combine(root, "doc", "remote-host-management.md"));

        TestAssert.False(chinese.Contains("同一时刻只有一台活动宿主", StringComparison.Ordinal),
            "Chinese README still documents one active host.");
        TestAssert.False(english.Contains("exactly one active host at a time", StringComparison.Ordinal),
            "English README still documents one active host.");
        TestAssert.False(guide.Contains("同一时刻只有一台活动宿主", StringComparison.Ordinal),
            "The user guide still documents one active host.");
        TestAssert.Contains("本机", chinese);
        TestAssert.Contains("multiple remote hosts", english);
        TestAssert.Contains("多台远程宿主", guide);
    }

    private static string ReadSourceTree(string directory) => string.Join(
        Environment.NewLine,
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText));

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

        throw new DirectoryNotFoundException("Could not locate the ExHyperV repository root.");
    }
}
