using ExHyperV.Services;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Services.Remote.Vms;
using ExHyperV.Tools;

internal static class CapabilityTests
{
    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
    [
        ("Capabilities_LocalSessionHasFullCapability", LocalSessionHasFullCapability),
        ("Capabilities_RemoteChannelsAreIndependent", RemoteChannelsAreIndependent),
        ("Capabilities_StaleSnapshotIsReadOnly", StaleSnapshotIsReadOnly),
        ("Capabilities_SwitchingPublishesTemporaryGates", SwitchingPublishesTemporaryGates),
        ("Capabilities_RemoteUnsupportedReasonsAreSpecific", RemoteUnsupportedReasonsAreSpecific),
        ("Capabilities_MatricesCompareByValue", MatricesCompareByValue),
        ("Capabilities_CoordinatorGatesUseMatrixReasons", CoordinatorGatesUseMatrixReasons),
        ("Capabilities_PostConfigurationDiagnosticRefreshesActiveChannels", PostConfigurationDiagnosticRefreshesActiveChannels),
        ("Capabilities_RemoteVmLifecyclePolicyMatchesApprovedScope", RemoteVmLifecyclePolicyMatchesApprovedScope),
        ("Capabilities_LocalOnlyVmMutationEntrypointsHaveDirectGates", LocalOnlyVmMutationEntrypointsHaveDirectGates),
        ("Capabilities_MemoryMutationsAcquireGlobalWriteLease", MemoryMutationsAcquireGlobalWriteLease),
        ("Capabilities_LateVmListMappingRechecksHostGeneration", LateVmListMappingRechecksHostGeneration),
        ("Capabilities_VisibleConsoleButtonsExposeDisabledReason", VisibleConsoleButtonsExposeDisabledReason)
    ];

    private static void LocalSessionHasFullCapability()
    {
        HostCapabilityMatrix matrix = HostCapabilityMatrix.Create(ActiveHostSession.CreateLocal(), isSwitching: false);

        TestAssert.False(matrix.IsRemoteHost, "Local capability matrix was marked remote.");
        foreach (HostCapabilityKind kind in Enum.GetValues<HostCapabilityKind>())
        {
            TestAssert.Equal(HostCapabilityState.Available, matrix[kind].State);
            TestAssert.Equal(HostCapabilityReasonCode.None, matrix[kind].ReasonCode);
        }
    }

    private static void RemoteChannelsAreIndependent()
    {
        ActiveHostSession session = RemoteSession(
            HostChannelState.Available,
            HostChannelState.Unavailable,
            hasStaleData: false);
        HostCapabilityMatrix matrix = HostCapabilityMatrix.Create(session, isSwitching: false);

        TestAssert.True(matrix[HostCapabilityKind.VmRead].CanExecute, "Available WMI did not enable VM reads.");
        TestAssert.True(matrix[HostCapabilityKind.VmWrite].CanExecute, "Available WMI did not enable VM writes.");
        TestAssert.False(matrix[HostCapabilityKind.VmConsole].CanExecute, "Unavailable TCP 2179 enabled the console.");
        TestAssert.Equal(HostCapabilityReasonCode.ConsoleChannelUnavailable, matrix[HostCapabilityKind.VmConsole].ReasonCode);
    }

    private static void StaleSnapshotIsReadOnly()
    {
        HostCapabilityMatrix matrix = HostCapabilityMatrix.Create(
            RemoteSession(HostChannelState.Available, HostChannelState.Available, hasStaleData: true),
            isSwitching: false);

        TestAssert.Equal(HostCapabilityState.ReadOnly, matrix[HostCapabilityKind.VmRead].State);
        TestAssert.Equal(HostCapabilityReasonCode.StaleData, matrix[HostCapabilityKind.VmRead].ReasonCode);
        TestAssert.Equal(HostCapabilityReasonCode.StaleData, matrix[HostCapabilityKind.VmWrite].ReasonCode);
        TestAssert.Equal(HostCapabilityReasonCode.StaleData, matrix[HostCapabilityKind.VmConsole].ReasonCode);
    }

    private static void SwitchingPublishesTemporaryGates()
    {
        HostCapabilityMatrix matrix = HostCapabilityMatrix.Create(ActiveHostSession.CreateLocal(), isSwitching: true);

        TestAssert.Equal(HostCapabilityState.ReadOnly, matrix[HostCapabilityKind.VmRead].State);
        TestAssert.Equal(HostCapabilityReasonCode.HostSwitchInProgress, matrix[HostCapabilityKind.VmWrite].ReasonCode);
        TestAssert.Equal(HostCapabilityReasonCode.HostSwitchInProgress, matrix[HostCapabilityKind.HostHardware].ReasonCode);

        HostCapabilityMatrix remoteMatrix = HostCapabilityMatrix.Create(
            RemoteSession(HostChannelState.Available, HostChannelState.Available, hasStaleData: false),
            isSwitching: true);
        TestAssert.Equal(
            HostCapabilityReasonCode.HostSwitchInProgress,
            remoteMatrix[HostCapabilityKind.HostHardware].ReasonCode);
    }

    private static void MatricesCompareByValue()
    {
        ActiveHostSession session = RemoteSession(
            HostChannelState.Available,
            HostChannelState.Unavailable,
            hasStaleData: false);
        HostCapabilityMatrix first = HostCapabilityMatrix.Create(session, isSwitching: false);
        HostCapabilityMatrix second = HostCapabilityMatrix.Create(session, isSwitching: false);

        TestAssert.True(first.Equals(second), "Equivalent capability matrices did not compare by value.");
        TestAssert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    private static void CoordinatorGatesUseMatrixReasons()
    {
        var coordinator = new ActiveHostSessionCoordinator();
        var profile = new HostProfile(Guid.NewGuid(), "门控宿主", "10.0.0.41");
        coordinator.SelectProfile(profile);
        coordinator.CommitActiveSession(new ActiveHostSession(
            2,
            HostTarget.FromProfile(profile),
            HostConnectionState.PartiallyAvailable,
            HostChannelState.Unavailable,
            HostChannelState.Available,
            HasStaleData: false));
        HostCapability readCapability = coordinator.Current.Capabilities[HostCapabilityKind.VmRead];
        HostCapability writeCapability = coordinator.Current.Capabilities[HostCapabilityKind.VmWrite];
        var operations = new ActiveHostVmOperations(coordinator, new HostWmiContextResolver());
        bool readBackendCalled = false;
        bool writeBackendCalled = false;

        HostVmReadResult<string> read = operations.ReadAsync(
            (_, _) =>
            {
                readBackendCalled = true;
                return Task.FromResult("unexpected");
            }).GetAwaiter().GetResult();
        HostVmWriteResult write = operations.WriteAsync(
            (_, _) =>
            {
                writeBackendCalled = true;
                return Task.FromResult(HostVmBackendWriteResult.Success());
            }).GetAwaiter().GetResult();

        TestAssert.False(readBackendCalled, "A disabled VM read reached the backend.");
        TestAssert.False(writeBackendCalled, "A disabled VM write reached the backend.");
        TestAssert.Equal(readCapability.Reason, read.Message);
        TestAssert.Equal(writeCapability.Reason, write.Message);

        coordinator.CommitActiveSession(new ActiveHostSession(
            3,
            HostTarget.FromProfile(profile),
            HostConnectionState.PartiallyAvailable,
            HostChannelState.Available,
            HostChannelState.Unavailable,
            HasStaleData: false));
        HostCapability consoleCapability = coordinator.Current.Capabilities[HostCapabilityKind.VmConsole];
        TestAssert.False(
            coordinator.TryCaptureConsoleOperation(out _, out string consoleReason),
            "A disabled console operation was captured.");
        TestAssert.Equal(consoleCapability.Reason, consoleReason);
    }

    private static void PostConfigurationDiagnosticRefreshesActiveChannels()
    {
        var coordinator = new ActiveHostSessionCoordinator();
        var profile = new HostProfile(Guid.NewGuid(), "复检宿主", "10.0.0.42");
        coordinator.SelectProfile(profile);
        coordinator.CommitActiveSession(new ActiveHostSession(
            2,
            HostTarget.FromProfile(profile),
            HostConnectionState.PartiallyAvailable,
            HostChannelState.Available,
            HostChannelState.Unavailable,
            HasStaleData: false));

        bool updated = coordinator.UpdateActiveChannels(
            profile.Id,
            HostChannelState.Available,
            HostChannelState.Available);

        TestAssert.True(updated, "Post-configuration diagnostics did not update the active host.");
        TestAssert.Equal(HostConnectionState.Connected, coordinator.Current.ActiveSession.ConnectionState);
        TestAssert.True(
            coordinator.Current.Capabilities[HostCapabilityKind.VmConsole].CanExecute,
            "TCP 2179 recovery did not enable the console capability.");
        TestAssert.False(
            coordinator.UpdateActiveChannels(Guid.NewGuid(), HostChannelState.Unavailable, HostChannelState.Unavailable),
            "A diagnostic for another profile changed the active host.");
    }

    private static void RemoteUnsupportedReasonsAreSpecific()
    {
        HostCapabilityMatrix matrix = HostCapabilityMatrix.Create(
            RemoteSession(HostChannelState.Available, HostChannelState.Available, hasStaleData: false),
            isSwitching: false);

        foreach (HostCapabilityKind kind in new[]
                 {
                     HostCapabilityKind.HostHardware,
                     HostCapabilityKind.VmAdvancedSettings,
                     HostCapabilityKind.LocalFileSystem,
                     HostCapabilityKind.VirtualSwitch,
                     HostCapabilityKind.PcieDevices,
                     HostCapabilityKind.UsbPassthrough
                 })
        {
            HostCapability capability = matrix[kind];
            TestAssert.Equal(HostCapabilityReasonCode.RemoteNotSupported, capability.ReasonCode);
            TestAssert.True(!string.IsNullOrWhiteSpace(capability.Reason), $"{kind} did not expose a Chinese reason.");
        }
    }

    private static void RemoteVmLifecyclePolicyMatchesApprovedScope()
    {
        foreach (string action in new[] { "Start", "Stop", "TurnOff", "Restart" })
        {
            TestAssert.Equal(
                VmLifecycleActionSupport.Supported,
                VmLifecycleActionPolicy.Evaluate(action, isRemote: true));
        }

        foreach (string action in new[] { "Save", "Suspend" })
        {
            TestAssert.Equal(
                VmLifecycleActionSupport.UnsupportedOnRemote,
                VmLifecycleActionPolicy.Evaluate(action, isRemote: true));
            TestAssert.Equal(
                VmLifecycleActionSupport.Supported,
                VmLifecycleActionPolicy.Evaluate(action, isRemote: false));
        }

        foreach (string? action in new[] { null, string.Empty, "Pause", "Resume", "Unknown" })
        {
            TestAssert.Equal(
                VmLifecycleActionSupport.Unknown,
                VmLifecycleActionPolicy.Evaluate(action, isRemote: false));
            TestAssert.Equal(
                VmLifecycleActionSupport.Unknown,
                VmLifecycleActionPolicy.Evaluate(action, isRemote: true));
        }

        string serviceSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Services", "Vm", "VmPowerService.cs"));
        TestAssert.Contains("VmLifecycleActionPolicy.Evaluate", serviceSource);
        TestAssert.False(
            serviceSource.Replace("\r\n", "\n").Contains(
                "default:\n                    return ApiResponse.Ok()",
                StringComparison.Ordinal),
            "Unknown lifecycle actions still report success.");
    }

    private static void LocalOnlyVmMutationEntrypointsHaveDirectGates()
    {
        string root = FindRepositoryRoot();
        (string File, string Method, string Guard)[] cases =
        [
            ("VirtualMachinesPageViewModel.Boot.cs", "SaveBootOrderAsync", "TryBeginHostWrite(HostCapabilityKind.VmAdvancedSettings"),
            ("VirtualMachinesPageViewModel.Cpu.cs", "ApplyChangesAsync", "TryBeginHostWrite(HostCapabilityKind.VmAdvancedSettings"),
            ("VirtualMachinesPageViewModel.Memory.cs", "MemorySettings_PropertyChanged", "TryBeginHostWrite(HostCapabilityKind.VmAdvancedSettings"),
            ("VirtualMachinesPageViewModel.Network.cs", "UpdateAdapterConnectionAsync", "TryBeginHostWrite(HostCapabilityKind.VmAdvancedSettings"),
            ("VirtualMachinesPageViewModel.Advanced.cs", "ApplyVideoResolutionAsync", "TryBeginHostWrite(HostCapabilityKind.VmAdvancedSettings"),
            ("VirtualMachinesPageViewModel.Security.cs", "ApplySecurityAsync", "TryBeginHostWrite(HostCapabilityKind.VmAdvancedSettings"),
            ("VirtualMachinesPageViewModel.Spacetime.cs", "CaptureMomentAsync", "TryBeginHostWrite(HostCapabilityKind.VmAdvancedSettings"),
            ("VirtualMachinesPageViewModel.Pcie.cs", "ApplyPcieTopologyAsync", "TryBeginHostWrite(HostCapabilityKind.PcieDevices"),
            ("VirtualMachinesPageViewModel.Storage.cs", "EditStoragePath", "TryBeginHostWrite(HostCapabilityKind.LocalFileSystem"),
            ("VirtualMachinesPageViewModel.Create.cs", "CommitRenameAsync", "TryBeginHostWrite(HostCapabilityKind.VmAdvancedSettings"),
            ("VirtualMachinesPageViewModel.Gpu.cs", "RunRealGpuWorkflowAsync", "TryBeginHostWrite(HostCapabilityKind.PcieDevices")
        ];

        foreach ((string file, string method, string guard) in cases)
        {
            string path = Path.Combine(root, "src", "ViewModels", file);
            string source = File.ReadAllText(path);
            System.Text.RegularExpressions.Match declaration = System.Text.RegularExpressions.Regex.Match(
                source,
                $@"(?:private\s+(?:async\s+)?(?:Task|void)|partial\s+void)\s+{System.Text.RegularExpressions.Regex.Escape(method)}\s*\(",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            TestAssert.True(declaration.Success, $"Could not locate {file}:{method}.");
            int bodyStart = source.IndexOf('{', declaration.Index + declaration.Length);
            TestAssert.True(bodyStart >= 0, $"Could not locate the body for {file}:{method}.");
            string entry = source.Substring(bodyStart, Math.Min(2600, source.Length - bodyStart));
            TestAssert.Contains(guard, entry);
            TestAssert.True(
                entry.Contains("using var writeScope = writeLease", StringComparison.Ordinal)
                || entry.Contains("using (writeLease)", StringComparison.Ordinal),
                $"{file}:{method} acquires a host write lease without a scoped release.");
        }
    }

    private static void VisibleConsoleButtonsExposeDisabledReason()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src", "Views", "Pages", "VirtualMachinesPage.xaml"));
        System.Text.RegularExpressions.MatchCollection buttons = System.Text.RegularExpressions.Regex.Matches(
            xaml,
            @"<ui:Button\b(?=[^>]*OpenNativeConnectCommand)[^>]*/>",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant
            | System.Text.RegularExpressions.RegexOptions.Singleline);

        TestAssert.Equal(2, buttons.Count);
        foreach (System.Text.RegularExpressions.Match button in buttons)
        {
            TestAssert.Contains("IsConsoleAvailable", button.Value);
            TestAssert.Contains("ConsoleUnavailableText", button.Value);
            TestAssert.Contains("ToolTipService.ShowOnDisabled=\"True\"", button.Value);
        }
    }

    private static void LateVmListMappingRechecksHostGeneration()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "ViewModels", "VirtualMachinesPageViewModel.cs"));
        int mapping = source.IndexOf("var finalCollection = await Task.Run", StringComparison.Ordinal);
        TestAssert.True(mapping >= 0, "Could not locate the asynchronous VM list mapping step.");
        int assignment = source.IndexOf("VmList = finalCollection", mapping, StringComparison.Ordinal);
        TestAssert.True(assignment > mapping, "Could not locate the mapped VM list assignment.");
        int recheck = source.LastIndexOf(
            "!ReferenceEquals(loadGeneration, _hostGenerationCts)",
            assignment,
            StringComparison.Ordinal);
        TestAssert.True(
            recheck > mapping && recheck < assignment,
            "Mapped VM results can be assigned without rechecking the active host generation.");
    }

    private static void MemoryMutationsAcquireGlobalWriteLease()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "ViewModels", "VirtualMachinesPageViewModel.Memory.cs"));
        foreach (string method in new[]
                 {
                     "ApplyMmioSettingsAsync",
                     "MemorySettings_PropertyChanged",
                     "ApplyMemorySettingsAsync"
                 })
        {
            System.Text.RegularExpressions.Match declaration = System.Text.RegularExpressions.Regex.Match(
                source,
                $@"(?:private\s+(?:async\s+)?(?:Task|void))\s+{System.Text.RegularExpressions.Regex.Escape(method)}\s*\(",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            TestAssert.True(declaration.Success, $"Could not locate memory mutation {method}.");
            int bodyStart = source.IndexOf('{', declaration.Index + declaration.Length);
            string entry = source.Substring(bodyStart, Math.Min(2400, source.Length - bodyStart));
            TestAssert.Contains("TryBeginHostWrite(HostCapabilityKind.VmAdvancedSettings", entry);
            TestAssert.Contains("using (writeLease)", entry);
        }
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

    private static ActiveHostSession RemoteSession(
        HostChannelState management,
        HostChannelState console,
        bool hasStaleData)
    {
        var profile = new HostProfile(Guid.NewGuid(), "测试宿主", "10.0.0.40");
        return new ActiveHostSession(
            3,
            HostTarget.FromProfile(profile),
            hasStaleData ? HostConnectionState.Reconnecting : HostConnectionState.PartiallyAvailable,
            management,
            console,
            hasStaleData);
    }
}
