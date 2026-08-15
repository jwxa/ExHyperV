internal static class VirtualMachinesMultiHostWiringTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("VmPage_ExposesLocalFirstHostGroups", ExposesLocalFirstHostGroups),
        ("VmPage_AssignsOwningHostIdentityToRows", AssignsOwningHostIdentityToRows),
        ("VmPage_RoutesReadsAndWritesByOwningHost", RoutesReadsAndWritesByOwningHost),
        ("VmPage_XamlGroupsRowsByHost", XamlGroupsRowsByHost)
    ];

    private static void ExposesLocalFirstHostGroups()
    {
        string source = ReadSource("ViewModels", "VirtualMachinesPageViewModel.cs");

        TestAssert.Contains("ObservableCollection<HostVmGroupViewModel> _hostGroups", source);
        TestAssert.Contains("EnsureHostGroup(HostSessionSnapshot.CreateLocal())", source);
        TestAssert.Contains("HostGroups.Insert(0", source);
    }

    private static void AssignsOwningHostIdentityToRows()
    {
        string rowSource = ReadSource("ViewModels", "VmInstanceViewModel.cs");
        string pageSource = ReadSource("ViewModels", "VirtualMachinesPageViewModel.cs");

        TestAssert.Contains("public HostId HostId", rowSource);
        TestAssert.Contains("public VmKey VmKey", rowSource);
        TestAssert.Contains("new VmInstanceViewModel(group.HostId, snapshot)", pageSource);
    }

    private static void RoutesReadsAndWritesByOwningHost()
    {
        string source = ReadSource("ViewModels", "VirtualMachinesPageViewModel.cs");

        TestAssert.Contains("_hostOperationRouter.ReadAsync(", source);
        TestAssert.Contains("group.HostId", source);
        TestAssert.Contains("_hostOperationRouter.WriteAsync(", source);
        TestAssert.Contains("instance.HostId", source);
    }

    private static void XamlGroupsRowsByHost()
    {
        string xaml = ReadSource("Views", "Pages", "VirtualMachinesPage.xaml");

        TestAssert.Contains("ItemsSource=\"{Binding HostGroups}\"", xaml);
        TestAssert.Contains("ItemsSource=\"{Binding Vms}\"", xaml);
        TestAssert.Contains("DisplayName", xaml);
        TestAssert.Contains("DisplayAddress", xaml);
    }

    private static string ReadSource(params string[] segments) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), "src", .. segments]));

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
