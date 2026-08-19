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
        ("Console_UnexpectedDisconnectReportsConnectionLoss", UnexpectedDisconnectReportsConnectionLoss),
        ("Console_MultiMonitorLaunchFlowIsWired", MultiMonitorLaunchFlowIsWired),
        ("Console_DisplayModeDialogFitsLocalizedOptions", DisplayModeDialogFitsLocalizedOptions),
        ("Console_EnhancedUnavailableCanFallbackToBasic", EnhancedUnavailableCanFallbackToBasic),
        ("Console_MultiMonitorManualRefitIsWired", MultiMonitorManualRefitIsWired),
        ("Console_MultiMonitorAcceptedFullScreenRequestHasWatchdog", MultiMonitorAcceptedFullScreenRequestHasWatchdog),
        ("Console_MultiMonitorContainerFailureRollsBackNativeFullScreen", MultiMonitorContainerFailureRollsBackNativeFullScreen),
        ("Console_MultiMonitorSettingsAreAppliedBeforeConnect", MultiMonitorSettingsAreAppliedBeforeConnect),
        ("Console_MultiMonitorAvoidsSingleDisplayNegotiation", MultiMonitorAvoidsSingleDisplayNegotiation),
        ("Console_MultiMonitorRestoresWhenLeavePrecedesLock", MultiMonitorRestoresWhenLeavePrecedesLock),
        ("Console_MultiMonitorUserLeaveClearsRecoveryIntent", MultiMonitorUserLeaveClearsRecoveryIntent),
        ("Console_MultiMonitorSystemTransitionPreservesRecoveryIntent", MultiMonitorSystemTransitionPreservesRecoveryIntent),
        ("Console_MultiMonitorNativeHotKeyOverridesSystemTransition", MultiMonitorNativeHotKeyOverridesSystemTransition),
        ("Console_MultiMonitorRecoveryFailurePreservesIntentWithoutLoop", MultiMonitorRecoveryFailurePreservesIntentWithoutLoop),
        ("Console_MultiMonitorMinimizedRestoreDoesNotActivate", MultiMonitorMinimizedRestoreDoesNotActivate),
        ("Console_MultiMonitorInternalPlacementSuppressesDpiRecovery", MultiMonitorInternalPlacementSuppressesDpiRecovery),
        ("Console_MultiMonitorContentAlignmentRemovesPixelInset", MultiMonitorContentAlignmentRemovesPixelInset),
        ("Console_MultiMonitorLeaveTokensCapturedAtEventBoundary", MultiMonitorLeaveTokensCapturedAtEventBoundary),
        ("Console_MultiMonitorPlacementVerificationHonorsMinimize", MultiMonitorPlacementVerificationHonorsMinimize),
        ("Console_MultiMonitorStableTopologyCanShrink", MultiMonitorStableTopologyCanShrink),
        ("Console_MultiMonitorRecoveryKeepsLockBaseline", MultiMonitorRecoveryKeepsLockBaseline),
        ("Console_MultiMonitorTopologyDetectsSplitChanges", MultiMonitorTopologyDetectsSplitChanges),
        ("Console_SystemLeaveExpectationIsOneShot", SystemLeaveExpectationIsOneShot),
        ("Console_WindowsSessionLockStateUsesNativeAlignment", WindowsSessionLockStateUsesNativeAlignment),
        ("Console_MultiMonitorRecoversAfterDisplayOrSessionChange", MultiMonitorRecoversAfterDisplayOrSessionChange)
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

    private static void MultiMonitorLaunchFlowIsWired()
    {
        string root = FindRepositoryRoot();
        string displayModeSource = File.ReadAllText(Path.Combine(
            root, "src", "Interaction", "ConsoleDisplayMode.cs"));
        string dialogsSource = File.ReadAllText(Path.Combine(
            root, "src", "Interaction", "Dialogs.cs"));
        string navigationSource = File.ReadAllText(Path.Combine(
            root, "src", "Interaction", "Navigation.cs"));
        string pageSource = File.ReadAllText(Path.Combine(
            root, "src", "ViewModels", "VirtualMachinesPageViewModel.cs"));

        TestAssert.Contains("SingleMonitor", displayModeSource);
        TestAssert.Contains("AllMonitors", displayModeSource);
        TestAssert.Contains("Task<ConsoleDisplayMode?> ShowConsoleDisplayModeSelectionAsync()", dialogsSource);
        TestAssert.Contains("ContentDialogResult.Secondary => ConsoleDisplayMode.SingleMonitor", dialogsSource);
        TestAssert.Contains("ContentDialogResult.Primary => ConsoleDisplayMode.AllMonitors", dialogsSource);
        TestAssert.Contains("bool TryActivateConsoleWindow(HostConsoleSession session)", navigationSource);
        TestAssert.Contains("new ConsoleWindow(session, displayMode, forceBasicSession)", navigationSource);

        int activate = pageSource.IndexOf("Navigation.TryActivateConsoleWindow(session)", StringComparison.Ordinal);
        int prompt = pageSource.IndexOf("Dialogs.ShowConsoleDisplayModeSelectionAsync()", StringComparison.Ordinal);
        int open = pageSource.IndexOf(
            "Navigation.OpenConsoleWindow(session, displayMode.Value, forceBasicSession)",
            StringComparison.Ordinal);
        TestAssert.True(activate >= 0 && prompt > activate && open > prompt,
            "Existing console activation, display selection, and new-window opening are not ordered correctly.");
        TestAssert.Contains("VmConsoleService.IsEnhancedSessionAvailableAsync", pageSource);
        TestAssert.Contains("ConsoleDisplayMode_EnhancedRequired", pageSource);
    }

    private static void DisplayModeDialogFitsLocalizedOptions()
    {
        string root = FindRepositoryRoot();
        string dialogsSource = File.ReadAllText(Path.Combine(
            root, "src", "Interaction", "Dialogs.cs"));

        TestAssert.Contains("DialogWidth = 640", dialogsSource);
        TestAssert.Contains("DialogMaxWidth = 640", dialogsSource);

        VerifyDisplayModeButtonLabelsFit(
            ["Use all monitors", "Use one monitor", "Cancel"],
            ["使用所有监视器", "使用单个监视器", "取消"]);
    }

    private static void VerifyDisplayModeButtonLabelsFit(params string[][] localizedLabels)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
                };
                application.Resources.MergedDictionaries.Add(
                    new Wpf.Ui.Markup.ThemesDictionary
                    {
                        Theme = Wpf.Ui.Appearance.ApplicationTheme.Light
                    });
                application.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

                foreach (string[] labels in localizedLabels)
                {
                    var dialog = new Wpf.Ui.Controls.ContentDialog
                    {
                        DialogWidth = 640,
                        DialogMaxWidth = 640,
                        PrimaryButtonText = labels[0],
                        SecondaryButtonText = labels[1],
                        CloseButtonText = labels[2]
                    };

                    ExHyperV.Interaction.ConsoleDisplayModeDialogLayout.EnsureButtonsFit(dialog);
                    var window = new System.Windows.Window
                    {
                        Width = 800,
                        Height = 600,
                        Left = -10000,
                        Top = -10000,
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        WindowStyle = System.Windows.WindowStyle.None,
                        Content = dialog
                    };
                    window.Show();
                    dialog.ApplyTemplate();
                    dialog.Measure(new System.Windows.Size(640, 400));
                    dialog.Arrange(new System.Windows.Rect(dialog.DesiredSize));
                    dialog.UpdateLayout();

                    Wpf.Ui.Controls.Button[] buttons = FindVisualChildren<Wpf.Ui.Controls.Button>(dialog)
                        .Where(button => labels.Contains(button.Content?.ToString(), StringComparer.Ordinal))
                        .ToArray();
                    TestAssert.Equal(labels.Length, buttons.Length);
                    foreach (Wpf.Ui.Controls.Button button in buttons)
                    {
                        double arrangedButtonWidth = button.ActualWidth;
                        button.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                        double requiredButtonWidth = button.DesiredSize.Width
                            - button.Margin.Left
                            - button.Margin.Right;
                        TestAssert.True(
                            arrangedButtonWidth + 0.5 >= requiredButtonWidth,
                            $"Button '{button.Content}' is clipped: actual={arrangedButtonWidth}, required={requiredButtonWidth}.");
                    }

                    window.Close();
                }

                application.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }

    private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            System.Windows.DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
                yield return typed;
            foreach (T descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static void EnhancedUnavailableCanFallbackToBasic()
    {
        string root = FindRepositoryRoot();
        string pageSource = File.ReadAllText(Path.Combine(
            root, "src", "ViewModels", "VirtualMachinesPageViewModel.cs"));
        string navigationSource = File.ReadAllText(Path.Combine(
            root, "src", "Interaction", "Navigation.cs"));
        string windowSource = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Windows", "ConsoleWindow.xaml.cs"));
        string viewModelSource = File.ReadAllText(Path.Combine(
            root, "src", "ViewModels", "ConsoleViewModel.cs"));

        int enhancedCheck = pageSource.IndexOf("if (enhanced.Value != true)", StringComparison.Ordinal);
        int confirmation = pageSource.IndexOf("Dialogs.ShowConfirmAsync(", enhancedCheck, StringComparison.Ordinal);
        int fallback = pageSource.IndexOf(
            "displayMode = ConsoleDisplayMode.SingleMonitor;",
            confirmation,
            StringComparison.Ordinal);
        int forceBasic = pageSource.IndexOf("forceBasicSession = true;", fallback, StringComparison.Ordinal);
        int open = pageSource.IndexOf(
            "Navigation.OpenConsoleWindow(session, displayMode.Value, forceBasicSession)",
            StringComparison.Ordinal);

        TestAssert.True(
            enhancedCheck >= 0
                && confirmation > enhancedCheck
                && fallback > confirmation
                && forceBasic > fallback
                && open > forceBasic,
            "Enhanced-session unavailability does not offer a confirmed single-monitor fallback before opening the console.");
        TestAssert.Contains("bool forceBasicSession = false", navigationSource);
        TestAssert.Contains("new ConsoleWindow(session, displayMode, forceBasicSession)", navigationSource);
        TestAssert.Contains("forceBasicSession = false", windowSource);
        TestAssert.Contains("forceBasicSession);", windowSource);
        TestAssert.Contains("&& (_useAllMonitors", viewModelSource);
        TestAssert.Contains("!forceBasicSession", viewModelSource);
    }

    private static void MultiMonitorManualRefitIsWired()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Windows", "ConsoleWindow.xaml"));
        string viewModelSource = File.ReadAllText(Path.Combine(
            root, "src", "ViewModels", "ConsoleViewModel.cs"));
        string windowSource = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Windows", "ConsoleWindow.xaml.cs"));
        string axHostSource = File.ReadAllText(Path.Combine(
            root, "src", "Tools", "Controls", "MsRdpAxHost.cs"));
        string clientHostSource = File.ReadAllText(Path.Combine(
            root, "src", "Tools", "Controls", "RdpClientHost.cs"));

        TestAssert.Contains("RefitDisplaysCommand", xaml);
        TestAssert.Contains("Symbol=ArrowSync24", xaml);
        TestAssert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml);
        TestAssert.Contains("RefitDisplaysRequested", viewModelSource);
        TestAssert.Contains("!IsRefittingDisplays", viewModelSource);
        TestAssert.Contains("RefitDisplaysRequested += OnRefitDisplaysRequested", windowSource);
        TestAssert.Contains("RefitDisplaysRequested -= OnRefitDisplaysRequested", windowSource);
        TestAssert.Contains("private void OnRefitDisplaysRequested()", windowSource);
        TestAssert.Contains("RdpHost.ConnectionState != 1", windowSource);
        TestAssert.Contains("expectedMonitorCount: topology.MonitorCount", windowSource);
        TestAssert.Contains("forceReconnect: true", windowSource);
        TestAssert.Contains("if (forceReconnect || topologyChanged)", windowSource);
        int refitHandler = windowSource.IndexOf(
            "private void OnRefitDisplaysRequested()",
            StringComparison.Ordinal);
        int markBusy = windowSource.IndexOf(
            "_vm.IsRefittingDisplays = true;",
            refitHandler,
            StringComparison.Ordinal);
        int scheduleRecovery = windowSource.IndexOf(
            "ScheduleMultiMonitorRecovery(",
            markBusy,
            StringComparison.Ordinal);
        TestAssert.True(refitHandler >= 0 && markBusy > refitHandler && scheduleRecovery > markBusy,
            "Manual display refit is not marked busy before recovery is scheduled.");
        int connectedHandler = windowSource.IndexOf(
            "RdpHost.Connected += () =>",
            StringComparison.Ordinal);
        int confirmationTimeout = windowSource.IndexOf(
            "VerifyManualDisplayRefitFullScreenAsync(",
            connectedHandler,
            StringComparison.Ordinal);
        TestAssert.True(connectedHandler >= 0 && confirmationTimeout > connectedHandler,
            "Manual display refit does not verify native full-screen confirmation after reconnecting.");
        TestAssert.Contains("FullScreenStartFailed +=", windowSource);
        TestAssert.Contains("mstscax 拒绝进入多显示器全屏", windowSource);
        int fullScreenHandler = windowSource.IndexOf(
            "RdpHost.FullScreenRequested += fs =>",
            StringComparison.Ordinal);
        int applyFullScreen = windowSource.IndexOf(
            "SetMultiMonitorFullScreenState(fs);",
            fullScreenHandler,
            StringComparison.Ordinal);
        int alignedContent = windowSource.IndexOf(
            "多显示器全屏内容边界已校准",
            applyFullScreen,
            StringComparison.Ordinal);
        int finishConfirmedRefit = windowSource.IndexOf(
            "FinishManualDisplayRefit();",
            alignedContent,
            StringComparison.Ordinal);
        TestAssert.True(
            fullScreenHandler >= 0
            && applyFullScreen > fullScreenHandler
            && alignedContent > applyFullScreen
            && finishConfirmedRefit > alignedContent,
            "Manual display refit finishes before native full-screen entry and content alignment are confirmed.");
        TestAssert.Contains(
            "bool fullScreenApplied = SetMultiMonitorFullScreenState(fs);",
            windowSource);
        TestAssert.Contains("if (fs && !fullScreenApplied)", windowSource);
        TestAssert.Contains("RdpHost.SetFullScreen(false);", windowSource);
        TestAssert.Contains("OnConsoleMinimizeRequested", windowSource);
        TestAssert.Contains("OnConsoleCloseRequested", windowSource);

        TestAssert.Contains("public bool ApplyAndConnect", axHostSource);
        TestAssert.Contains("FullScreenStartFailed?.Invoke()", axHostSource);
        TestAssert.Contains("nonScriptable5.DisableConnectionBar = false", axHostSource);
        TestAssert.Contains("adv.DisplayConnectionBar = true", axHostSource);
        TestAssert.Contains("adv.PinConnectionBar = true", axHostSource);
        TestAssert.Contains("adv.ConnectionBarShowPinButton = true", axHostSource);
        TestAssert.Contains("public bool Connect", clientHostSource);
        int connectionStarted = windowSource.IndexOf(
            "bool connectionStarted = RdpHost.Connect(",
            StringComparison.Ordinal);
        int connectionRejected = windowSource.IndexOf(
            "if (!connectionStarted)",
            connectionStarted,
            StringComparison.Ordinal);
        int finishRejectedRefit = windowSource.IndexOf(
            "FinishManualDisplayRefit();",
            connectionRejected,
            StringComparison.Ordinal);
        TestAssert.True(
            connectionStarted >= 0
            && connectionRejected > connectionStarted
            && finishRejectedRefit > connectionRejected,
            "A synchronously rejected RDP connection leaves manual display refit busy.");

        int runningBranch = windowSource.IndexOf(
            "if (_vm.IsRunning)",
            connectionStarted - 2500,
            StringComparison.Ordinal);
        int stoppedBranch = windowSource.IndexOf(
            "else\r\n            {\r\n                FinishManualDisplayRefit();",
            runningBranch,
            StringComparison.Ordinal);
        if (stoppedBranch < 0)
            stoppedBranch = windowSource.IndexOf(
                "else\n            {\n                FinishManualDisplayRefit();",
                runningBranch,
                StringComparison.Ordinal);
        TestAssert.True(runningBranch >= 0 && stoppedBranch > runningBranch,
            "A VM that stops during reconnect leaves manual display refit busy.");
    }

    private static void MultiMonitorAcceptedFullScreenRequestHasWatchdog()
    {
        string root = FindRepositoryRoot();
        string windowSource = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Windows", "ConsoleWindow.xaml.cs"));

        int connectedHandler = windowSource.IndexOf(
            "RdpHost.Connected += () =>",
            StringComparison.Ordinal);
        int loginHandler = windowSource.IndexOf(
            "RdpHost.LoginCompleted +=",
            connectedHandler,
            StringComparison.Ordinal);
        int connectedWatchdog = windowSource.IndexOf(
            "QueueMultiMonitorFullScreenConfirmation(",
            connectedHandler,
            StringComparison.Ordinal);
        TestAssert.True(
            connectedHandler >= 0
            && connectedWatchdog > connectedHandler
            && connectedWatchdog < loginHandler,
            "A multi-monitor connection can accept FullScreen without raising the native event, but no confirmation watchdog is queued.");

        int toggleHandler = windowSource.IndexOf(
            "private void OnFullScreenToggleRequested()",
            StringComparison.Ordinal);
        int toggleEnd = windowSource.IndexOf(
            "private void OnConsoleMinimizeRequested()",
            toggleHandler,
            StringComparison.Ordinal);
        int acceptedRequest = windowSource.IndexOf(
            "bool requestAccepted = RdpHost.SetFullScreen(fullScreen);",
            toggleHandler,
            StringComparison.Ordinal);
        int acceptedBranch = windowSource.IndexOf(
            "if (requestAccepted)",
            acceptedRequest,
            StringComparison.Ordinal);
        int acceptedWatchdog = windowSource.IndexOf(
            "QueueMultiMonitorFullScreenConfirmation(",
            acceptedBranch,
            StringComparison.Ordinal);
        TestAssert.True(
            toggleHandler >= 0
            && acceptedRequest > toggleHandler
            && acceptedBranch > acceptedRequest
            && acceptedWatchdog > acceptedBranch
            && acceptedWatchdog < toggleEnd,
            "An accepted user full-screen request is not watched for a missing native full-screen event.");

        int confirmation = windowSource.IndexOf(
            "private async Task ConfirmMultiMonitorFullScreenAsync(",
            StringComparison.Ordinal);
        int confirmationEnd = windowSource.IndexOf(
            "private void RollbackNativeFullScreenAfterContainerFailure(",
            confirmation,
            StringComparison.Ordinal);
        string confirmationSource = windowSource.Substring(
            confirmation,
            confirmationEnd - confirmation);
        int delay = confirmationSource.IndexOf(
            "Task.Delay(MultiMonitorFullScreenConfirmationTimeoutMs)",
            StringComparison.Ordinal);
        int confirmedState = confirmationSource.IndexOf(
            "if (_isFullScreen)",
            StringComparison.Ordinal);
        int nativeStateGuard = confirmationSource.IndexOf(
            "if (!RdpHost.IsFullScreen)",
            StringComparison.Ordinal);
        int synchronizeContainer = confirmationSource.IndexOf(
            "SetMultiMonitorFullScreenState(true)",
            StringComparison.Ordinal);
        TestAssert.True(
            delay >= 0
            && confirmedState > delay
            && nativeStateGuard > confirmedState
            && synchronizeContainer > nativeStateGuard,
            "The full-screen watchdog does not actively synchronize the container when the native event is missing.");
        TestAssert.Contains(
            "confirmationGeneration != Volatile.Read(ref _fullScreenConfirmationGeneration)",
            confirmationSource);
        TestAssert.Contains(
            "connectionGeneration != Volatile.Read(ref _rdpConnectionGeneration)",
            confirmationSource);
        TestAssert.Contains("RdpHost.ConnectionState != 1", confirmationSource);
        TestAssert.Contains("if (!RdpHost.IsFullScreen)", confirmationSource);
        int cancelRecovery = windowSource.IndexOf(
            "private void CancelMultiMonitorRecovery()",
            StringComparison.Ordinal);
        int cancelRecoveryEnd = windowSource.IndexOf(
            "private void PrepareForExpectedSystemLeave(",
            cancelRecovery,
            StringComparison.Ordinal);
        string cancelSource = windowSource.Substring(
            cancelRecovery,
            cancelRecoveryEnd - cancelRecovery);
        TestAssert.Contains(
            "Interlocked.Increment(ref _fullScreenConfirmationGeneration)",
            cancelSource);
    }

    private static void MultiMonitorContainerFailureRollsBackNativeFullScreen()
    {
        string root = FindRepositoryRoot();
        string windowSource = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Windows", "ConsoleWindow.xaml.cs"));

        int eventHandler = windowSource.IndexOf(
            "RdpHost.FullScreenRequested += fs =>",
            StringComparison.Ordinal);
        int eventHandlerEnd = windowSource.IndexOf(
            "RdpHost.FullScreenStartFailed +=",
            eventHandler,
            StringComparison.Ordinal);
        string eventSource = windowSource.Substring(
            eventHandler,
            eventHandlerEnd - eventHandler);
        int applyContainer = eventSource.IndexOf(
            "bool fullScreenApplied = SetMultiMonitorFullScreenState(fs);",
            StringComparison.Ordinal);
        int failedContainer = eventSource.IndexOf(
            "if (fs && !fullScreenApplied)",
            applyContainer,
            StringComparison.Ordinal);
        int rollback = eventSource.IndexOf(
            "RollbackNativeFullScreenAfterContainerFailure(",
            failedContainer,
            StringComparison.Ordinal);
        TestAssert.True(
            applyContainer >= 0 && failedContainer > applyContainer && rollback > failedContainer,
            "A native full-screen event can leave mstscax full-screen when the application container fails to expand.");

        int rollbackMethod = windowSource.IndexOf(
            "private void RollbackNativeFullScreenAfterContainerFailure(",
            StringComparison.Ordinal);
        int rollbackEnd = windowSource.IndexOf(
            "private async Task VerifyManualDisplayRefitFullScreenAsync(",
            rollbackMethod,
            StringComparison.Ordinal);
        string rollbackSource = windowSource.Substring(
            rollbackMethod,
            rollbackEnd - rollbackMethod);
        int armExpectedLeave = rollbackSource.IndexOf(
            "ArmExpectedRecoveryFailureLeave();",
            StringComparison.Ordinal);
        int leaveNativeFullScreen = rollbackSource.IndexOf(
            "RdpHost.SetFullScreen(false)",
            StringComparison.Ordinal);
        TestAssert.True(
            armExpectedLeave >= 0 && leaveNativeFullScreen > armExpectedLeave,
            "Container failure does not arm the one-shot Leave token before rolling mstscax back to windowed mode.");

        int enterContainer = windowSource.IndexOf(
            "private bool EnterMultiMonitorFullScreen(",
            StringComparison.Ordinal);
        int verification = windowSource.IndexOf(
            "private void QueueMultiMonitorPlacementVerification()",
            enterContainer,
            StringComparison.Ordinal);
        string enterSource = windowSource.Substring(
            enterContainer,
            verification - enterContainer);
        int positionFailure = enterSource.IndexOf(
            "if (!positioned)",
            StringComparison.Ordinal);
        int clearContainerState = enterSource.IndexOf(
            "_isFullScreen = false;",
            positionFailure,
            StringComparison.Ordinal);
        int restoreWindow = enterSource.IndexOf(
            "RestoreMultiMonitorWindow(hwnd);",
            clearContainerState,
            StringComparison.Ordinal);
        int rejectContainer = enterSource.IndexOf(
            "return false;",
            restoreWindow,
            StringComparison.Ordinal);
        TestAssert.True(
            positionFailure >= 0
            && clearContainerState > positionFailure
            && restoreWindow > clearContainerState
            && rejectContainer > restoreWindow,
            "A failed virtual-desktop placement does not restore the original application window before reporting failure.");

        TestAssert.Equal(
            MultiMonitorLeaveDisposition.PreserveIntent,
            MultiMonitorLeavePolicy.Resolve(
                userFullScreenHotKeyPressed: false,
                sessionTransitionPending: false,
                expectedSystemLeave: false,
                expectedRecoveryFailureLeave: true));
    }

    private static void MultiMonitorSettingsAreAppliedBeforeConnect()
    {
        string root = FindRepositoryRoot();
        string settingsSource = File.ReadAllText(Path.Combine(
            root, "src", "Tools", "Controls", "RdpConnectionSettings.cs"));
        string axHostSource = File.ReadAllText(Path.Combine(
            root, "src", "Tools", "Controls", "MsRdpAxHost.cs"));
        string clientHostSource = File.ReadAllText(Path.Combine(
            root, "src", "Tools", "Controls", "RdpClientHost.cs"));

        TestAssert.Contains("public bool UseAllMonitors", settingsSource);
        int useMultimon = axHostSource.IndexOf("IMsRdpClientNonScriptable5", StringComparison.Ordinal);
        int connect = axHostSource.IndexOf("rdp.Connect();", StringComparison.Ordinal);
        TestAssert.True(useMultimon >= 0 && connect > useMultimon,
            "UseMultimon must be applied before the RDP ActiveX Connect call.");
        TestAssert.Contains("UseMultimon = s.UseAllMonitors", axHostSource);
        TestAssert.Contains("adv.ContainerHandledFullScreen = 1", axHostSource);
        TestAssert.Contains("containerHandledFullScreenValue = TryGet(", axHostSource);
        TestAssert.Contains("if (!containerHandledFullScreenSet || !containerHandledFullScreenApplied)", axHostSource);
        TestAssert.Contains("nonScriptable5.DisableConnectionBar = false", axHostSource);
        TestAssert.Contains("adv.DisplayConnectionBar = true", axHostSource);
        TestAssert.Contains("adv.PinConnectionBar = true", axHostSource);
        TestAssert.Contains("adv.ConnectionBarShowPinButton = true", axHostSource);
        TestAssert.Contains("displayConnectionBarValue = TryGet(", axHostSource);
        TestAssert.Contains("pinConnectionBarValue = TryGet(", axHostSource);
        TestAssert.Contains("!displayConnectionBarSet", axHostSource);
        TestAssert.Contains("!pinConnectionBarSet", axHostSource);
        TestAssert.Contains("mstscax 未接受显示并固定原生连接栏，已阻止启动多显示器会话。", axHostSource);
        TestAssert.Contains("if (!useMultimonSet || !useMultimonApplied)", axHostSource);
        TestAssert.False(axHostSource.Contains("s.UseAllMonitors ? 0 : 1", StringComparison.Ordinal),
            "Multi-monitor must use the WPF container's virtual-desktop full-screen host.");
        TestAssert.Contains("_startFullScreenOnConnect = s.UseAllMonitors", axHostSource);
        TestAssert.Contains("BeginInvoke(new Action(() => StartFullScreenIfCurrent(generation)))", axHostSource);
        TestAssert.Contains("generation != _connectionGeneration", axHostSource);
        TestAssert.Contains("ConnectionState != 1", axHostSource);
        TestAssert.Contains("_startFullScreenOnConnect = false", axHostSource);
        TestAssert.Contains("RemoteMonitorCount", axHostSource);
        TestAssert.Contains("RemoteMonitorLayoutMatchesLocal", axHostSource);
        TestAssert.Contains("GetRemoteMonitorsBoundingBox", axHostSource);
        TestAssert.Contains("ControlScreenBounds", axHostSource);
        TestAssert.Contains("HorizontalScrollBarVisible", axHostSource);
        TestAssert.Contains("public bool SetFullScreen(bool fullScreen)", clientHostSource);
    }

    private static void MultiMonitorAvoidsSingleDisplayNegotiation()
    {
        string root = FindRepositoryRoot();
        string windowSource = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Windows", "ConsoleWindow.xaml.cs"));
        string viewModelSource = File.ReadAllText(Path.Combine(
            root, "src", "ViewModels", "ConsoleViewModel.cs"));

        TestAssert.Contains("new ConsoleViewModel(", windowSource);
        TestAssert.Contains("_useAllMonitors,", windowSource);
        TestAssert.Contains("forceBasicSession);", windowSource);
        TestAssert.Contains("SystemInformation.VirtualScreen", windowSource);
        TestAssert.Contains("multiMonitorTopology?.Width ?? virtualScreen.Width", windowSource);
        TestAssert.Contains("multiMonitorTopology?.Height ?? virtualScreen.Height", windowSource);
        TestAssert.Contains("UseAllMonitors = useAllMonitors", windowSource);
        TestAssert.Contains("if (!_vm.IsEnhancedMode || _useAllMonitors) return;", windowSource);
        TestAssert.Contains("FullScreenToggleRequested += OnFullScreenToggleRequested", windowSource);
        TestAssert.Contains("RdpHost.ConnectionState != 1", windowSource);
        TestAssert.Contains("FullScreenToggleRequested?.Invoke()", viewModelSource);
        TestAssert.Contains("EnterMultiMonitorFullScreen", windowSource);
        TestAssert.Contains("ExitMultiMonitorFullScreen", windowSource);
        TestAssert.Contains("SetWindowPos", windowSource);
        TestAssert.Contains("SWP_FRAMECHANGED", windowSource);
        TestAssert.Contains("SystemInformation.VirtualScreen", windowSource);
        TestAssert.Contains("GetWindowPlacement", windowSource);
        TestAssert.Contains("SetWindowPlacement", windowSource);
        TestAssert.Contains("WM_DPICHANGED", windowSource);
        TestAssert.Contains("QueueMultiMonitorPlacementVerification", windowSource);
        TestAssert.Contains("多显示器全屏边界校准失败", windowSource);
        TestAssert.Contains("SetMultiMonitorFullScreenState(false)", windowSource);
        TestAssert.Contains("generation != Volatile.Read(ref _rdpConnectionGeneration)", windowSource);
        TestAssert.Contains("&& !applied", windowSource);
        TestAssert.Contains("\"多显示器全屏窗口铺设失败\"", windowSource);
        TestAssert.Contains("if (_isFullScreen && _useAllMonitors)", windowSource);
        TestAssert.False(windowSource.Contains("CloseAfterMultimonFailureAsync", StringComparison.Ordinal),
            "A transient enhanced-session failure must not close a multi-monitor console.");
        TestAssert.Contains("if (_useAllMonitors) return;", windowSource);
        TestAssert.Contains("_preferEnhanced = !forceBasicSession", viewModelSource);
        TestAssert.Contains("&& (_useAllMonitors", viewModelSource);
        TestAssert.Contains("CanChangeResolution => IsEnhancedMode && !_useAllMonitors", viewModelSource);
    }

    private static void MultiMonitorRecoversAfterDisplayOrSessionChange()
    {
        string root = FindRepositoryRoot();
        string windowSource = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Windows", "ConsoleWindow.xaml.cs"));
        string stateSource = File.ReadAllText(Path.Combine(
            root, "src", "Tools", "Controls", "MultiMonitorRecoveryState.cs"));

        TestAssert.Contains("SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged", windowSource);
        TestAssert.Contains("SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged", windowSource);
        TestAssert.Contains("SystemEvents.SessionSwitch += OnSystemSessionSwitch", windowSource);
        TestAssert.Contains("SystemEvents.SessionSwitch -= OnSystemSessionSwitch", windowSource);
        TestAssert.Contains("SessionSwitchReason.SessionLock", windowSource);
        TestAssert.Contains("SessionSwitchReason.SessionUnlock", windowSource);
        TestAssert.Contains("MultiMonitorRecoveryState", windowSource);
        TestAssert.Contains("BeginPotentialUserLeave", windowSource);
        TestAssert.Contains("ConfirmPotentialMultiMonitorLeaveAsync", windowSource);
        TestAssert.Contains("WindowsSessionLockState.QueryCurrent()", windowSource);
        TestAssert.Contains("_multiMonitorUserMinimized", windowSource);
        TestAssert.Contains("if (_multiMonitorUserMinimized) return;", windowSource);
        TestAssert.Contains("placement.ShowCmd = SW_SHOWMINNOACTIVE", windowSource);
        TestAssert.Contains("restorePlan.NoActivate ? SWP_NOACTIVATE : 0", windowSource);
        TestAssert.Contains("WM_DISPLAYCHANGE", windowSource);
        TestAssert.Contains("MultiMonitorRecoveryDelaysMs", windowSource);
        TestAssert.Contains("QueueMultiMonitorSystemTransition", windowSource);
        TestAssert.Contains("RecoverMultiMonitorLayoutIfCurrent", windowSource);
        TestAssert.Contains("ReconnectForMultiMonitorTopologyChange", windowSource);
        TestAssert.Contains("_negotiatedMultiMonitorTopology", windowSource);
        TestAssert.Contains("MonitorLayout", windowSource);
        TestAssert.Contains("_expectedSystemLeave.TryConsume()", windowSource);
        TestAssert.Contains("_expectedRecoveryFailureLeave.TryConsume()", windowSource);
        TestAssert.Contains("IsFullScreenHotKeyPressed()", windowSource);
        TestAssert.Contains("GetAsyncKeyState", windowSource);
        TestAssert.Contains("MultiMonitorLeavePolicy.Resolve(", windowSource);
        TestAssert.Contains("MultiMonitorDisplayEventPolicy.ShouldQueueDpiRecovery(", windowSource);
        TestAssert.Contains("BeginMultiMonitorWindowPlacement()", windowSource);
        TestAssert.Contains("scheduleRecoveryOnFailure", windowSource);
        TestAssert.Contains("if (restorePlan.NormalizeBeforeRestore) WindowState = WindowState.Normal;", windowSource);
        TestAssert.Contains("transitionGeneration != Volatile.Read(ref _systemTransitionGeneration)", windowSource);
        TestAssert.Contains("bool topologyStable = stableSamples >= 2", windowSource);
        TestAssert.Contains("UnsubscribeMultiMonitorSystemEvents()", windowSource);
        TestAssert.Contains("RecordStableTopology", stateSource);
    }

    private static void MultiMonitorRestoresWhenLeavePrecedesLock()
    {
        var state = new MultiMonitorRecoveryState(fullScreenDesired: true);
        state.RecordStableTopology(2);

        int leaveGeneration = state.BeginPotentialUserLeave();
        state.Lock(currentMonitorCount: 1);

        TestAssert.False(state.ConfirmPotentialUserLeave(
            leaveGeneration,
            systemTransitionPending: false),
            "A Leave event that preceded SessionLock was incorrectly treated as a user exit.");
        MultiMonitorUnlockRecovery recovery = state.Unlock();
        TestAssert.True(recovery.ShouldRecover,
            "Unlock did not preserve the pre-lock multi-monitor full-screen intent.");
        TestAssert.Equal(2, recovery.ExpectedMonitorCount);
    }

    private static void MultiMonitorUserLeaveClearsRecoveryIntent()
    {
        var state = new MultiMonitorRecoveryState(fullScreenDesired: true);

        int leaveGeneration = state.BeginPotentialUserLeave();

        TestAssert.True(state.ConfirmPotentialUserLeave(
            leaveGeneration,
            systemTransitionPending: false),
            "A native full-screen leave without a system transition was not accepted as user intent.");
        TestAssert.False(state.FullScreenDesired,
            "The recovery intent remained active after a confirmed user exit.");
    }

    private static void MultiMonitorSystemTransitionPreservesRecoveryIntent()
    {
        var state = new MultiMonitorRecoveryState(fullScreenDesired: true);

        int leaveGeneration = state.BeginPotentialUserLeave();
        state.InvalidatePendingLeave();

        TestAssert.False(state.ConfirmPotentialUserLeave(
            leaveGeneration,
            systemTransitionPending: true),
            "A display transition was incorrectly committed as a user full-screen exit.");
        TestAssert.True(state.FullScreenDesired,
            "A display transition cleared the multi-monitor recovery intent.");
    }

    private static void MultiMonitorNativeHotKeyOverridesSystemTransition()
    {
        MultiMonitorLeaveDisposition disposition = MultiMonitorLeavePolicy.Resolve(
            userFullScreenHotKeyPressed: true,
            sessionTransitionPending: true,
            expectedSystemLeave: true,
            expectedRecoveryFailureLeave: true);

        TestAssert.Equal(MultiMonitorLeaveDisposition.UserRequestedWindowed, disposition);
    }

    private static void MultiMonitorRecoveryFailurePreservesIntentWithoutLoop()
    {
        TestAssert.Equal(
            MultiMonitorLeaveDisposition.PreserveIntent,
            MultiMonitorLeavePolicy.Resolve(
                userFullScreenHotKeyPressed: false,
                sessionTransitionPending: false,
                expectedSystemLeave: false,
                expectedRecoveryFailureLeave: true));
        TestAssert.Equal(
            MultiMonitorLeaveDisposition.PreserveIntentAndRecover,
            MultiMonitorLeavePolicy.Resolve(
                userFullScreenHotKeyPressed: false,
                sessionTransitionPending: false,
                expectedSystemLeave: true,
                expectedRecoveryFailureLeave: false));
        TestAssert.Equal(
            MultiMonitorLeaveDisposition.ConfirmPotentialUserLeave,
            MultiMonitorLeavePolicy.Resolve(
                userFullScreenHotKeyPressed: false,
                sessionTransitionPending: false,
                expectedSystemLeave: false,
                expectedRecoveryFailureLeave: false));
    }

    private static void MultiMonitorMinimizedRestoreDoesNotActivate()
    {
        MultiMonitorWindowRestorePlan plan = MultiMonitorWindowRestorePlan.Create(
            preserveMinimized: true);

        TestAssert.False(plan.NormalizeBeforeRestore,
            "A minimized console would be restored to Normal before its native placement was applied.");
        TestAssert.True(plan.NoActivate,
            "A minimized console restore would activate the window.");
        TestAssert.True(plan.RestoreMinimized,
            "A minimized console restore lost its minimized state.");
    }

    private static void MultiMonitorInternalPlacementSuppressesDpiRecovery()
    {
        TestAssert.False(MultiMonitorDisplayEventPolicy.ShouldQueueDpiRecovery(
                useAllMonitors: true,
                potentialUserLeavePending: false,
                internalPlacementSuppressed: true),
            "A DPI event caused by ExHyperV's own window placement started another recovery.");
        TestAssert.True(MultiMonitorDisplayEventPolicy.ShouldQueueDpiRecovery(
                useAllMonitors: true,
                potentialUserLeavePending: false,
                internalPlacementSuppressed: false),
            "An external DPI change was not eligible for recovery.");
    }

    private static void MultiMonitorContentAlignmentRemovesPixelInset()
    {
        string root = FindRepositoryRoot();
        string windowSource = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Windows", "ConsoleWindow.xaml.cs"));
        string clientHostSource = File.ReadAllText(Path.Combine(
            root, "src", "Tools", "Controls", "RdpClientHost.cs"));
        var target = new MultiMonitorPixelBounds(0, 0, 3840, 1080);
        var currentWindow = new MultiMonitorPixelBounds(0, 0, 3840, 1080);
        var onePixelRightShift = new MultiMonitorPixelBounds(1, 0, 3840, 1080);

        MultiMonitorPixelBounds corrected = MultiMonitorContentAlignment.CalculateWindowBounds(
            target,
            currentWindow,
            onePixelRightShift);

        TestAssert.Equal(new MultiMonitorPixelBounds(-1, 0, 3840, 1080), corrected);

        var onePixelFrame = new MultiMonitorPixelBounds(1, 1, 3839, 1079);
        corrected = MultiMonitorContentAlignment.CalculateWindowBounds(
            target,
            currentWindow,
            onePixelFrame);

        TestAssert.Equal(new MultiMonitorPixelBounds(-1, -1, 3841, 1081), corrected);

        var negativeTarget = new MultiMonitorPixelBounds(-1920, 0, 1920, 1080);
        var negativeContent = new MultiMonitorPixelBounds(-1919, 0, 1920, 1080);
        corrected = MultiMonitorContentAlignment.CalculateWindowBounds(
            negativeTarget,
            negativeTarget,
            negativeContent);
        TestAssert.Equal(new MultiMonitorPixelBounds(-1921, 0, 1920, 1080), corrected);
        TestAssert.Equal(
            corrected,
            MultiMonitorContentAlignment.CalculateWindowBounds(
                negativeTarget,
                corrected,
                negativeTarget));

        int visualMethod = windowSource.IndexOf(
            "private void ApplyMultiMonitorFullScreenVisuals",
            StringComparison.Ordinal);
        int resizeMode = windowSource.IndexOf(
            "ResizeMode = ResizeMode.NoResize;",
            visualMethod,
            StringComparison.Ordinal);
        int backdrop = windowSource.IndexOf(
            "WindowBackdropType = WindowBackdropType.None;",
            visualMethod,
            StringComparison.Ordinal);
        TestAssert.True(visualMethod >= 0 && resizeMode > visualMethod && backdrop > resizeMode,
            "WPF-UI rebuilt WindowChrome before multi-monitor resize borders were disabled.");
        int alignmentMethod = windowSource.IndexOf(
            "private bool TryAlignMultiMonitorContentBounds",
            visualMethod,
            StringComparison.Ordinal);
        string visualSource = windowSource.Substring(
            visualMethod,
            alignmentMethod - visualMethod);
        TestAssert.False(visualSource.Contains("Background =", StringComparison.Ordinal),
            "Multi-monitor full-screen replaced the window's dynamic Background resource.");
        TestAssert.False(visualSource.Contains("BorderBrush =", StringComparison.Ordinal),
            "Multi-monitor full-screen replaced the WPF-UI BorderBrush value source.");
        TestAssert.Contains("ReadLocalValue(WindowCornerPreferenceProperty)", windowSource);
        TestAssert.Contains("RestoreLocalValue(this, WindowCornerPreferenceProperty", windowSource);
        TestAssert.Contains("target.ClearValue(property)", windowSource);
        TestAssert.Contains("RdpHost.TryGetContentScreenBounds", windowSource);
        TestAssert.Contains("MultiMonitorContentAlignment.CalculateWindowBounds", windowSource);
        TestAssert.Contains("WindowCornerPreference.DoNotRound", windowSource);
        TestAssert.Contains("protected override void OnDeactivated", windowSource);
        TestAssert.Contains("ReapplyMultiMonitorDwmFrame", windowSource);
        TestAssert.Contains("internal bool TryGetContentScreenBounds", clientHostSource);
    }

    private static void MultiMonitorLeaveTokensCapturedAtEventBoundary()
    {
        string root = FindRepositoryRoot();
        string windowSource = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Windows", "ConsoleWindow.xaml.cs"));
        int eventStart = windowSource.IndexOf("RdpHost.FullScreenRequested += fs =>", StringComparison.Ordinal);
        int dispatcher = windowSource.IndexOf("Dispatcher.BeginInvoke", eventStart, StringComparison.Ordinal);
        int tokenCapture = windowSource.IndexOf(
            "_expectedRecoveryFailureLeave.TryConsume()",
            eventStart,
            StringComparison.Ordinal);

        TestAssert.True(eventStart >= 0 && tokenCapture > eventStart && tokenCapture < dispatcher,
            "The recovery-failure Leave token was not captured before the Dispatcher boundary.");
    }

    private static void MultiMonitorPlacementVerificationHonorsMinimize()
    {
        string root = FindRepositoryRoot();
        string windowSource = File.ReadAllText(Path.Combine(
            root, "src", "Views", "Windows", "ConsoleWindow.xaml.cs"));
        int verification = windowSource.IndexOf(
            "private void QueueMultiMonitorPlacementVerification()",
            StringComparison.Ordinal);
        int setWindowPos = windowSource.IndexOf("SetWindowPos(", verification, StringComparison.Ordinal);
        int minimizedGuard = windowSource.IndexOf(
            "_multiMonitorUserMinimized",
            verification,
            StringComparison.Ordinal);

        TestAssert.True(
            verification >= 0 && minimizedGuard > verification && minimizedGuard < setWindowPos,
            "The delayed placement verification can touch a user-minimized console window.");
    }

    private static void MultiMonitorStableTopologyCanShrink()
    {
        var state = new MultiMonitorRecoveryState(fullScreenDesired: true);
        state.RecordStableTopology(3);
        state.RecordStableTopology(2);

        state.Lock(currentMonitorCount: 1);
        MultiMonitorUnlockRecovery recovery = state.Unlock();

        TestAssert.Equal(2, recovery.ExpectedMonitorCount);
    }

    private static void MultiMonitorRecoveryKeepsLockBaseline()
    {
        var state = new MultiMonitorRecoveryState(fullScreenDesired: true);
        state.RecordStableTopology(2);

        TestAssert.Equal(2, state.ResolveExpectedMonitorCount(
            requestedMonitorCount: 0,
            currentMonitorCount: 1));
        TestAssert.Equal(3, state.ResolveExpectedMonitorCount(
            requestedMonitorCount: 0,
            currentMonitorCount: 3));
    }

    private static void MultiMonitorTopologyDetectsSplitChanges()
    {
        var equalWidth = new MultiMonitorTopology(
            2, 0, 0, 3840, 1080,
            "P:(0,0)-(1920,1080);S:(1920,0)-(3840,1080)");
        var unequalWidth = new MultiMonitorTopology(
            2, 0, 0, 3840, 1080,
            "P:(0,0)-(2560,1080);S:(2560,0)-(3840,1080)");

        TestAssert.False(equalWidth == unequalWidth,
            "Per-monitor split changes were hidden by the unchanged virtual desktop bounds.");
    }

    private static void SystemLeaveExpectationIsOneShot()
    {
        var state = new ExpectedSystemLeaveState();
        state.Arm(1);

        TestAssert.True(state.TryConsume(), "The expected system Leave was not consumed.");
        TestAssert.False(state.TryConsume(), "One expected system Leave consumed more than once.");

        state.Arm(2);
        state.Expire(1);
        TestAssert.True(state.TryConsume(),
            "An older transition expiry cleared the current system Leave expectation.");
    }

    private static void WindowsSessionLockStateUsesNativeAlignment()
    {
        TestAssert.Equal(12, WindowsSessionLockState.SessionFlagsOffsetForPointerSize(4));
        TestAssert.Equal(16, WindowsSessionLockState.SessionFlagsOffsetForPointerSize(8));
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
