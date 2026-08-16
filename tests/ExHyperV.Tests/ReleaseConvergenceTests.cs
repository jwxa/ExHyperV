internal static class ReleaseConvergenceTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Release_NoGlobalActiveHostCompatibilityPath", NoGlobalActiveHostCompatibilityPath),
        ("Release_LocalCapabilitiesUseExplicitLocalHost", LocalCapabilitiesUseExplicitLocalHost),
        ("Release_ControlledRunnerUsesMultiHostRegistry", ControlledRunnerUsesMultiHostRegistry),
        ("Release_RemoteSurfacesUseThemeResources", RemoteSurfacesUseThemeResources),
        ("Release_HostIconsUsePackagedGlyph", HostIconsUsePackagedGlyph),
        ("Release_AzureLeasePreservesRemotePowerRouting", AzureLeasePreservesRemotePowerRouting),
        ("Release_PermanentAzureToggleIsRemoved", PermanentAzureToggleIsRemoved),
        ("Release_LocalFolderAndPurgeSafetyAreWired", LocalFolderAndPurgeSafetyAreWired),
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
        string hostViewModel = File.ReadAllText(Path.Combine(root, "src", "ViewModels", "HostConnectionPageViewModel.cs"));
        string vmPage = File.ReadAllText(Path.Combine(root, "src", "Views", "Pages", "VirtualMachinesPage.xaml"));

        TestAssert.False(hostPage.Contains("#0C0C0C", StringComparison.OrdinalIgnoreCase),
            "Host logs still use a hardcoded black background.");
        TestAssert.False(hostPage.Contains("#D0D0D0", StringComparison.OrdinalIgnoreCase),
            "Host logs still use a hardcoded gray foreground.");
        TestAssert.False(hostPage.Contains("Foreground=\"White\"", StringComparison.OrdinalIgnoreCase),
            "The host connection action still overrides theme-aware disabled text with white.");
        TestAssert.False(vmPage.Contains("#0B0B0B", StringComparison.OrdinalIgnoreCase),
            "The VM detail monitor still uses a hardcoded black background.");
        string logSource = Slice(hostPage, "Text=\"{Binding Source}\"", "/>");
        string logMessage = Slice(hostPage, "Text=\"{Binding Message}\"", "/>");
        TestAssert.Contains("TextFillColorPrimaryBrush", logSource);
        TestAssert.Contains("TextFillColorPrimaryBrush", logMessage);

        string connectionAppearance = Slice(
            hostViewModel,
            "public ControlAppearance ConnectionActionAppearance =>",
            "public string ConnectionActionToolTip");
        TestAssert.Contains("!CanExecuteConnectionAction", connectionAppearance);
        TestAssert.Contains("ControlAppearance.Secondary", connectionAppearance);
        TestAssert.Contains("ControlFillColorSecondaryBrush", hostPage);
        TestAssert.Contains("ControlFillColorSecondaryBrush", vmPage);
    }

    private static void HostIconsUsePackagedGlyph()
    {
        string root = FindRepositoryRoot();
        string mainWindow = File.ReadAllText(Path.Combine(root, "src", "Views", "Windows", "MainWindow.xaml"));
        string hostPage = File.ReadAllText(Path.Combine(root, "src", "Views", "Pages", "HostConnectionPage.xaml"));
        string hostViewModel = File.ReadAllText(Path.Combine(root, "src", "ViewModels", "HostConnectionPageViewModel.cs"));

        string hostNavigation = Slice(mainWindow, "Content=\"主机连接\"", "</ui:NavigationViewItem>");
        TestAssert.Contains("Symbol=\"Desktop24\"", hostNavigation);
        TestAssert.Contains("Symbol=\"{Binding SelectedHost.Icon}\"", hostPage);
        TestAssert.Contains("public string Icon => \"Desktop24\";", hostViewModel);

        string hostIconSources = string.Concat(hostNavigation, hostPage, hostViewModel);
        TestAssert.False(hostIconSources.Contains("Server24", StringComparison.Ordinal),
            "Server24 is absent from the packaged subset font and renders as an empty icon.");
    }

    private static void AzureLeasePreservesRemotePowerRouting()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Services", "Vm", "VmPowerService.cs"));
        int startCase = source.IndexOf("case \"Start\":", StringComparison.Ordinal);
        int turnOffCase = source.IndexOf("case \"TurnOff\":", startCase, StringComparison.Ordinal);
        TestAssert.True(startCase >= 0 && turnOffCase > startCase, "Could not isolate the VM start branch.");
        string branch = source[startCase..turnOffCase];

        TestAssert.Contains("ctx: context", branch);
        TestAssert.Contains("cancellationToken: cancellationToken", branch);
        TestAssert.Contains("context.IsLocal", branch);
        TestAssert.Contains("RunTemporarilyDisabledAsync(StartAsync)", branch);
        TestAssert.Contains(": await StartAsync()", branch);
    }

    private static void PermanentAzureToggleIsRemoved()
    {
        string root = FindRepositoryRoot();
        string productSource = ReadSourceTree(Path.Combine(root, "src"));
        string hostPage = File.ReadAllText(Path.Combine(root, "src", "Views", "Pages", "HostPage.xaml"));
        string resources = File.ReadAllText(Path.Combine(root, "src", "Properties", "Resources.resx"));

        TestAssert.False(
            productSource.Contains("AzureFeatureSetChangedMessage", StringComparison.Ordinal),
            "产品源码不应继续引用永久 Azure 功能集变更消息。");
        TestAssert.False(
            productSource.Contains("IsAzureFeatureSetEnabled", StringComparison.Ordinal),
            "产品源码不应继续暴露永久 Azure 功能集状态。");
        TestAssert.False(
            productSource.Contains("HostAzureFeatureSetService.SetEnabled", StringComparison.Ordinal),
            "产品源码不应继续调用永久 Azure 功能集开关。");
        TestAssert.False(
            hostPage.Contains("Menu_AzureFeatureSet", StringComparison.Ordinal),
            "宿主页不应继续显示永久 Azure 功能集开关。");
        TestAssert.False(
            resources.Contains("Menu_AzureFeatureSet", StringComparison.Ordinal),
            "资源文件不应继续保留永久 Azure 功能集菜单文案。");
    }

    private static void LocalFolderAndPurgeSafetyAreWired()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(
            root, "src", "ViewModels", "VirtualMachinesPageViewModel.cs"));
        string folderAccess = File.ReadAllText(Path.Combine(
            root, "src", "Services", "Vm", "VmFolderAccessService.cs"));

        TestAssert.Contains("VmFolderAccessService.EnsureExplorerCanRead(path)", viewModel);
        TestAssert.Contains(".iso", viewModel);
        TestAssert.Contains(".vhd", viewModel);
        TestAssert.Contains(".vhdx", viewModel);
        TestAssert.Contains("UiStatusBrushes.Critical", viewModel);
        TestAssert.False(
            viewModel.Contains("Color.FromRgb(232, 71, 86)", StringComparison.Ordinal),
            "彻底删除预览应复用主题关键色，而不是硬编码 RGB 颜色。");
        TestAssert.Contains("IsDefaultProtectedHyperVPath(fullPath)", folderAccess);
        TestAssert.Contains("FileSystemRights.ReadAndExecute", folderAccess);
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

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = start < 0
            ? -1
            : source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        TestAssert.True(start >= 0 && end > start,
            $"Could not isolate source between '{startMarker}' and '{endMarker}'.");
        return source[start..end];
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

        throw new DirectoryNotFoundException("Could not locate the ExHyperV repository root.");
    }
}
