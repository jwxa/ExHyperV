using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
using ExHyperV.Services;
using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Configuration;
using ExHyperV.Services.Remote.Consoles;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Preflight;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Views;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels;

public partial class HostConnectionPageViewModel : PageViewModelBase, IDisposable
{
    private readonly HostProfileManager _profileManager;
    private readonly IHostSessionRegistry _sessionRegistry;
    private readonly HostDisconnectCoordinator _disconnectCoordinator;
    private readonly HostDiagnosticPipeline _diagnosticPipeline;
    private readonly HostConfigurationPipeline _configurationPipeline;
    private readonly ISupportArtifactLocator _supportArtifactLocator;
    private readonly HostDiagnosticRunCoordinator _diagnosticRuns = new();
    private readonly Dictionary<Guid, HostDiagnosticReport> _reports = [];
    private readonly Dictionary<Guid, WindowsCredential> _transientCredentials = [];
    private CancellationTokenSource? _switchCancellation;
    private CancellationTokenSource? _configurationCancellation;
    private HostRepairContext? _repairContext;
    private bool _isRepairWorkspaceOpen;
    private bool _isDisconnecting;

    [ObservableProperty] private ObservableCollection<HostProfileListItemViewModel> _hosts = [];
    [ObservableProperty] private HostProfileListItemViewModel? _selectedHost;
    [ObservableProperty] private bool _isDiagnosing;
    [ObservableProperty] private bool _isSwitching;
    [ObservableProperty] private string _diagnosticSummary = "选择远程主机后开始检测。";
    [ObservableProperty] private string _diagnosticLogText = string.Empty;
    [ObservableProperty] private ObservableCollection<HostDiagnosticStepItemViewModel> _diagnosticSteps = [];
    [ObservableProperty] private string _managementStatus = "未检测";
    [ObservableProperty] private string _managementStatusGlyph = string.Empty;
    [ObservableProperty] private bool _managementStatusUsesDot = true;
    [ObservableProperty] private MediaBrush _managementStatusBrush = UiStatusBrushes.Caution;
    [ObservableProperty] private string _managementDetail = $"等待查询 {WindowsWmiDcomProbe.HyperVNamespace}";
    [ObservableProperty] private string _consoleStatus = "未检测";
    [ObservableProperty] private string _consoleStatusGlyph = string.Empty;
    [ObservableProperty] private bool _consoleStatusUsesDot = true;
    [ObservableProperty] private MediaBrush _consoleStatusBrush = UiStatusBrushes.Caution;
    [ObservableProperty] private string _consoleDetail = $"等待检测 TCP {HostDiagnosticPipeline.ConsolePort}";
    [ObservableProperty] private int _selectedWorkspaceTabIndex;

    public HostPreflightViewModel Preflight { get; }
    public HostLogViewModel Logs { get; }

    public HostConnectionPageViewModel(
        HostProfileManager profileManager,
        IHostSessionRegistry sessionRegistry,
        HostDiagnosticPipeline diagnosticPipeline,
        HostPreflightPipeline preflightPipeline,
        HostConfigurationPipeline configurationPipeline,
        ISupportArtifactLocator supportArtifactLocator,
        IHostConsoleRegistry? consoleRegistry = null,
        IHostLogFeed? logFeed = null)
    {
        _profileManager = profileManager;
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _disconnectCoordinator = new HostDisconnectCoordinator(
            _sessionRegistry,
            consoleRegistry ?? ActiveHostConsoleWindows.Registry);
        _diagnosticPipeline = diagnosticPipeline;
        _configurationPipeline = configurationPipeline;
        _supportArtifactLocator = supportArtifactLocator ?? throw new ArgumentNullException(nameof(supportArtifactLocator));
        Preflight = new HostPreflightViewModel(preflightPipeline);
        Logs = new HostLogViewModel(logFeed ?? AppLog.Feed);
        _sessionRegistry.Changed += OnRegistryChanged;
        RefreshProfiles();
        UpdateActiveHostProperties();
    }

    public bool IsRemoteSelected => SelectedHost?.Profile is not null;
    public bool HasProfiles => Hosts.Count > 1;
    public bool IsRepairActionVisible =>
        !_isRepairWorkspaceOpen
        && !IsDiagnosing
        && CurrentRepairDecision.CanOfferRepair;
    public bool IsRepairWorkspaceVisible => _isRepairWorkspaceOpen;
    public string RepairActionToolTip => CurrentRepairDecision.ActionToolTip;
    public string RepairGuidance => CurrentRepairDecision.Guidance;
    public bool HasRepairGuidance => !string.IsNullOrWhiteSpace(RepairGuidance);
    public bool CanConnectToSelectedHost =>
        SelectedHost?.Profile is { } profile
        && !IsSwitching
        && !IsConnected(profile)
        && GetCurrentReport(profile)?.ManagementAvailable == true;
    public bool CanExecuteConnectionAction
    {
        get
        {
            if (SelectedHost?.Profile is not { } profile || IsSwitching) return false;
            return IsConnected(profile)
                ? _sessionRegistry.GetDisconnectAvailability(HostId.FromProfile(profile)).CanDisconnect
                : CanConnectToSelectedHost;
        }
    }
    public string ConnectionActionText
    {
        get
        {
            if (IsSwitching && !_isDisconnecting) return "正在连接";
            return SelectedHost?.Profile is { } profile && IsConnected(profile)
                ? "断开"
                : "连接到此主机";
        }
    }
    public ControlAppearance ConnectionActionAppearance =>
        SelectedHost?.Profile is { } profile && IsConnected(profile)
            ? ControlAppearance.Danger
            : ControlAppearance.Primary;
    public string ConnectionActionToolTip
    {
        get
        {
            if (IsSwitching)
                return _isDisconnecting ? "正在断开远程宿主" : "正在准备并验证宿主快照";
            if (SelectedHost?.Profile is not { } profile) return "选择远程主机后可连接";
            if (!IsConnected(profile)) return ConnectHint;

            HostDisconnectAvailability availability =
                _sessionRegistry.GetDisconnectAvailability(HostId.FromProfile(profile));
            return availability.CanDisconnect
                ? "断开该宿主；保存的主机配置将继续保留。"
                : availability.Reason;
        }
    }
    public string SelectedDisplayName => SelectedHost?.DisplayName ?? "未选择主机";
    public string SelectedAddress => SelectedHost?.Address ?? string.Empty;
    public string SelectedIdentity => SelectedHost?.Profile?.AuthenticationMode switch
    {
        HostAuthenticationMode.CurrentWindowsIdentity => "当前 Windows 身份",
        HostAuthenticationMode.ExplicitCredential => SelectedHost.Profile.UserName ?? "显式凭据",
        _ => "当前 Windows 身份"
    };
    public string SelectedCredentialStorage => SelectedHost?.Profile?.CredentialTarget is not null
        ? "Windows 凭据管理器"
        : SelectedHost?.Profile?.AuthenticationMode == HostAuthenticationMode.ExplicitCredential
            ? "仅本次会话"
            : "无需单独保存";
    public string ActiveHostName => SelectedSession?.Target.DisplayName ?? SelectedDisplayName;
    public string ActiveHostAddress => SelectedSession?.Target.IsLocal == true
        ? Environment.MachineName
        : SelectedSession?.Target.Address ?? SelectedAddress;
    public string ActiveHostStatus => SelectedSession is not { } session
        ? "未连接"
        : session.Target.IsLocal
            ? "本机已连接"
            : session.ConnectionState switch
        {
            HostConnectionState.Connected => "远程已连接",
            HostConnectionState.PartiallyAvailable => "远程部分可用",
            HostConnectionState.RemoteDisconnected => "远程已断开",
            HostConnectionState.Reconnecting => "正在重新连接",
            HostConnectionState.Failed => "远程连接失败",
            _ => "远程状态未知"
        };
    public MediaBrush ActiveHostStatusBrush => SelectedSession?.ConnectionState switch
    {
        HostConnectionState.Connected or HostConnectionState.LocalConnected => UiStatusBrushes.Success,
        HostConnectionState.PartiallyAvailable or HostConnectionState.Reconnecting => UiStatusBrushes.Caution,
        HostConnectionState.RemoteDisconnected or HostConnectionState.Failed => UiStatusBrushes.Critical,
        _ => UiStatusBrushes.Neutral
    };
    public string ActiveHostIdentity => (SelectedSession?.Target.AuthenticationMode
        ?? SelectedHost?.Profile?.AuthenticationMode) switch
    {
        HostAuthenticationMode.ExplicitCredential =>
            SelectedSession?.Target.UserName ?? SelectedHost?.Profile?.UserName ?? "显式凭据",
        _ => "当前 Windows 身份"
    };
    public string ActiveHostSnapshot
    {
        get
        {
            HostBasicSnapshot? snapshot = SelectedSession?.BasicSnapshot;
            return snapshot is null
                ? SelectedSession?.Target.IsLocal == true ? "本地会话" : "等待宿主快照"
                : $"{snapshot.OperatingSystem} · {snapshot.VirtualMachineCount} 台虚拟机";
        }
    }
    public bool IsReconnectVisible =>
        SelectedSession?.HasStaleData == true;
    public bool IsReconnectActive => SelectedSession?.Reconnect.IsActive == true;
    public string ReconnectSummary
    {
        get
        {
            HostReconnectState reconnect = SelectedSession?.Reconnect ?? HostReconnectState.None;
            if (SelectedSession?.HasStaleData != true) return string.Empty;
            if (!reconnect.IsActive)
                return string.IsNullOrWhiteSpace(reconnect.LastError)
                    ? "自动重连已停止，当前保留断线前的旧数据。"
                    : $"自动重连已停止：{reconnect.LastError}";
            if (reconnect.NextAttemptAt is { } next)
                return $"第 {reconnect.Attempt} 次重连失败；下次尝试：{next.ToLocalTime():HH:mm:ss}。";
            return reconnect.Attempt == 0
                ? "连接已中断，正在准备自动重连。"
                : $"正在进行第 {reconnect.Attempt} 次重连。";
        }
    }
    public string ConnectHint
    {
        get
        {
            if (IsSwitching) return "正在准备并验证宿主快照";
            if (SelectedHost?.Profile is not { } profile) return "选择远程主机后可连接";
            if (IsConnected(profile)) return "该主机已连接";
            HostDiagnosticReport? report = GetCurrentReport(profile);
            if (report is null) return "请先运行连接检测";
            return report.ManagementAvailable ? "已检测，可以连接" : "WMI/DCOM 未通过，无法连接";
        }
    }

    partial void OnSelectedHostChanged(HostProfileListItemViewModel? value)
    {
        InvalidateDiagnosticRun();
        InvalidateConfigurationRun();
        _switchCancellation?.Cancel();
        CloseRepairWorkspace();
        Logs.SelectHost(value?.Profile is { } selectedProfile
            ? HostId.FromProfile(selectedProfile)
            : HostId.Local);
        OnSelectionPropertiesChanged();
        ApplyReport(value?.Profile is { } profile && _reports.TryGetValue(profile.Id, out HostDiagnosticReport? report)
            ? report
            : null);
    }

    partial void OnIsDiagnosingChanged(bool value) => NotifyRepairStateChanged();

    partial void OnIsSwitchingChanged(bool value) => NotifyConnectionEligibilityChanged();

    partial void OnSelectedWorkspaceTabIndexChanged(int value)
    {
        if (value != 2)
        {
            Preflight.Cancel();
            _configurationCancellation?.Cancel();
        }
    }

    [RelayCommand]
    private void RefreshProfiles()
    {
        Guid? selectedId = SelectedHost?.Profile?.Id;
        try
        {
            var items = new List<HostProfileListItemViewModel>
            {
                HostProfileListItemViewModel.Local(Environment.MachineName)
            };
            items.AddRange(_profileManager.GetAll().Select(profile =>
                HostProfileListItemViewModel.Remote(
                    profile,
                    _reports.TryGetValue(profile.Id, out HostDiagnosticReport? report) ? report : null)));
            Hosts = new ObservableCollection<HostProfileListItemViewModel>(items);
            SelectedHost = selectedId is null
                ? Hosts[0]
                : Hosts.FirstOrDefault(item => item.Profile?.Id == selectedId) ?? Hosts[0];
            OnPropertyChanged(nameof(HasProfiles));
        }
        catch (Exception ex)
        {
            AppLog.Error("主机配置", "读取主机配置失败。", exception: ex);
            ShowError($"读取主机配置失败：{SensitiveDataRedactor.Redact(ex.Message)}");
            Hosts = new ObservableCollection<HostProfileListItemViewModel>
            {
                HostProfileListItemViewModel.Local(Environment.MachineName)
            };
            SelectedHost = Hosts[0];
        }
    }

    [RelayCommand]
    private async Task AddHostAsync() => await EditProfileAsync(null);

    [RelayCommand(CanExecute = nameof(IsRemoteSelected))]
    private async Task EditSelectedHostAsync()
    {
        if (SelectedHost?.Profile is not { } profile) return;
        if (IsConnected(profile))
        {
            ShowTip("请先断开该主机连接，再编辑主机配置。" );
            return;
        }
        await EditProfileAsync(profile);
    }

    [RelayCommand(CanExecute = nameof(IsRemoteSelected))]
    private async Task DeleteSelectedHostAsync()
    {
        HostProfile? profile = SelectedHost?.Profile;
        if (profile is null) return;
        if (IsConnected(profile))
        {
            ShowTip("请先断开该主机连接，再删除主机配置。" );
            return;
        }
        string credentialText = profile.CredentialTarget is null
            ? string.Empty
            : " 关联的 Windows 凭据也会一并删除。";
        bool confirmed = await Dialogs.ShowConfirmAsync(
            "删除主机配置",
            $"确定删除“{profile.DisplayName}”吗？{credentialText}",
            "删除",
            "取消",
            isDanger: true);
        if (!confirmed) return;

        try
        {
            _diagnosticRuns.CancelCurrent();
            _profileManager.Delete(profile.Id, deleteRememberedCredential: true);
            _reports.Remove(profile.Id);
            _transientCredentials.Remove(profile.Id);
            RefreshProfiles();
            ShowSuccess("主机配置已删除。");
        }
        catch (Exception ex)
        {
            AppLog.Error(
                "主机配置",
                $"删除主机配置 {profile.DisplayName} 失败。",
                new AppLogContext(
                    Host: profile.Address,
                    HostId: HostId.FromProfile(profile),
                    ErrorCategory: "ProfileDeleteFailed"),
                ex);
            ShowError($"删除主机配置失败：{SensitiveDataRedactor.Redact(ex.Message)}");
        }
    }

    [RelayCommand(CanExecute = nameof(IsRemoteSelected))]
    private async Task DiagnoseSelectedHostAsync()
    {
        HostProfile? profile = SelectedHost?.Profile;
        if (profile is null || IsDiagnosing) return;
        WindowsCredential? transientCredential = _transientCredentials.GetValueOrDefault(profile.Id);
        if (profile.AuthenticationMode == HostAuthenticationMode.ExplicitCredential
            && profile.CredentialTarget is null
            && transientCredential is null)
        {
            ShowTip("此配置没有记住密码。请编辑主机配置并输入本次诊断使用的密码。");
            return;
        }

        using HostDiagnosticRun diagnosticRun = _diagnosticRuns.Begin(profile.Id);
        _reports.Remove(profile.Id);
        CloseRepairWorkspace();
        IsDiagnosing = true;
        DiagnoseSelectedHostCommand.NotifyCanExecuteChanged();
        DiagnosticSummary = $"正在检测 {profile.DisplayName}...";
        DiagnosticSteps.Clear();
        DiagnosticLogText = string.Empty;
        try
        {
            HostDiagnosticReport report = await _diagnosticPipeline.RunAsync(
                profile,
                transientCredential,
                diagnosticRun.Token);
            if (!_diagnosticRuns.IsCurrent(diagnosticRun, SelectedHost?.Profile?.Id)) return;
            _reports[profile.Id] = report;
            ApplyReport(report);
            RefreshProfileVisual(profile.Id, report);
        }
        catch (Exception ex)
        {
            if (!_diagnosticRuns.IsCurrent(diagnosticRun, SelectedHost?.Profile?.Id)) return;
            AppLog.Error(
                "连接诊断",
                $"主机 {profile.DisplayName} 的诊断流水线失败。",
                new AppLogContext(
                    Host: profile.Address,
                    HostId: HostId.FromProfile(profile),
                    ErrorCategory: "DiagnosticPipelineFailed"),
                ex);
            DiagnosticSummary = $"诊断失败：{SensitiveDataRedactor.Redact(ex.Message)}";
            ShowError(DiagnosticSummary);
        }
        finally
        {
            if (_diagnosticRuns.Complete(diagnosticRun))
            {
                IsDiagnosing = false;
                DiagnoseSelectedHostCommand.NotifyCanExecuteChanged();
                NotifyConnectionEligibilityChanged();
            }
        }
    }

    [RelayCommand]
    private void CancelDiagnostics() => _diagnosticRuns.CancelCurrent();

    [RelayCommand(CanExecute = nameof(CanOpenRepair))]
    private async Task OpenRepairAsync()
    {
        HostProfile? profile = SelectedHost?.Profile;
        HostDiagnosticReport? report = profile is null ? null : GetCurrentReport(profile);
        if (profile is null || report is null || !HostRepairAdvisor.Evaluate(profile, report).CanOfferRepair)
            return;
        WindowsCredential? transientCredential = _transientCredentials.GetValueOrDefault(profile.Id);
        if (profile.AuthenticationMode == HostAuthenticationMode.ExplicitCredential
            && profile.CredentialTarget is null
            && transientCredential is null)
        {
            ShowTip("此配置没有记住密码。请编辑主机配置并输入本次预检使用的密码。");
            return;
        }

        Preflight.Reset();
        Preflight.SetTarget(profile, transientCredential);
        _repairContext = HostRepairContext.Capture(profile, report);
        _isRepairWorkspaceOpen = true;
        NotifyRepairStateChanged();
        SelectedWorkspaceTabIndex = 2;
        await Preflight.RunCommand.ExecuteAsync(null);
    }

    private bool CanOpenRepair() => IsRepairActionVisible;

    [RelayCommand]
    private void RetryReconnect()
    {
        if (SelectedHost?.Profile is not { } profile
            || !_sessionRegistry.RetryReconnectNow(HostId.FromProfile(profile)))
            ShowTip("重连任务正在执行或停止中，请稍候。" );
    }

    [RelayCommand]
    private void StopReconnect()
    {
        if (SelectedHost?.Profile is { } profile)
            _sessionRegistry.StopReconnect(HostId.FromProfile(profile));
    }

    [RelayCommand]
    private void OpenLogs() => ReportArtifactLocation(
        _supportArtifactLocator.OpenLogDirectory(AppLog.LogDirectory),
        "日志");

    [RelayCommand]
    private void RevealRollbackScript(string? path) => ReportArtifactLocation(
        _supportArtifactLocator.RevealRollbackScript(path ?? string.Empty),
        "配置回滚");

    [RelayCommand]
    private async Task ApplyPreflightAsync()
    {
        HostProfile? profile = SelectedHost?.Profile;
        if (profile is null
            || _repairContext is null
            || !_repairContext.Matches(profile, GetCurrentReport(profile))
            || !Preflight.TryGetApprovedConfiguration(out HostPreflightReport? report, out HostPreflightPlan? plan)
            || report is null
            || plan is null)
        {
            ShowTip("请先完成预检并生成修改预览。");
            return;
        }

        var confirmation = new HostConfigurationDialogViewModel(profile, plan);
        bool confirmed = await Dialogs.ShowHostConfigurationConfirmationAsync(confirmation);
        if (!confirmed || !HostConfigurationConfirmation.IsExact(confirmation.ConfirmationText)) return;
        if (_repairContext is null
            || !_repairContext.Matches(profile, GetCurrentReport(profile)))
        {
            ShowTip("主机选择或最新诊断已变化，请重新打开设置检查。" );
            return;
        }

        InvalidateConfigurationRun();
        var configurationCancellation = new CancellationTokenSource();
        _configurationCancellation = configurationCancellation;
        Preflight.BeginApply();
        HostConfigurationReport result;
        try
        {
            result = await _configurationPipeline.ApplyAsync(
                profile,
                _transientCredentials.GetValueOrDefault(profile.Id),
                report,
                plan,
                confirmation.ConfirmationText,
                configurationCancellation.Token);
        }
        catch (Exception ex)
        {
            result = new HostConfigurationReport(
                false, false, false, [], null, null, null,
                [$"配置执行失败：{SensitiveDataRedactor.Redact(ex.Message)}"]);
        }
        bool isCurrentRun = ReferenceEquals(_configurationCancellation, configurationCancellation);
        if (isCurrentRun)
            _configurationCancellation = null;
        configurationCancellation.Dispose();
        if (!isCurrentRun || SelectedHost?.Profile?.Id != profile.Id) return;

        Preflight.CompleteApply(result);
        Preflight.ExpireApprovedPlan();
        _repairContext = null;
        NotifyRepairStateChanged();

        if (result.Diagnostic is { } diagnostic)
        {
            _reports[profile.Id] = diagnostic;
            RefreshProfileVisual(profile.Id, diagnostic);
            if (SelectedHost?.Profile?.Id == profile.Id) ApplyReport(diagnostic);
            _sessionRegistry.UpdateHostChannels(
                HostId.FromProfile(profile),
                diagnostic.ManagementAvailable ? HostChannelState.Available : HostChannelState.Unavailable,
                diagnostic.ConsoleAvailable ? HostChannelState.Available : HostChannelState.Unavailable,
                diagnostic.GetStep(HostDiagnosticStepKind.WmiDcom).Explanation);
            NotifyConnectionEligibilityChanged();
        }
    }

    private void InvalidateConfigurationRun()
    {
        CancellationTokenSource? cancellation = _configurationCancellation;
        _configurationCancellation = null;
        cancellation?.Cancel();
    }

    private void CloseRepairWorkspace()
    {
        _repairContext = null;
        _isRepairWorkspaceOpen = false;
        Preflight.ClearTarget();
        if (SelectedWorkspaceTabIndex == 2) SelectedWorkspaceTabIndex = 0;
        NotifyRepairStateChanged();
    }

    private void InvalidateDiagnosticRun()
    {
        _diagnosticRuns.Invalidate();
        if (!IsDiagnosing) return;

        IsDiagnosing = false;
        DiagnoseSelectedHostCommand.NotifyCanExecuteChanged();
        NotifyConnectionEligibilityChanged();
    }

    [RelayCommand(CanExecute = nameof(CanExecuteConnectionAction))]
    private async Task ConnectToSelectedHostAsync()
    {
        HostProfile? profile = SelectedHost?.Profile;
        if (profile is not null && IsConnected(profile))
        {
            await DisconnectSelectedHostAsync(profile);
            return;
        }

        HostDiagnosticReport? report = profile is null ? null : GetCurrentReport(profile);
        if (profile is null || report?.ManagementAvailable != true)
        {
            ShowTip("请先完成检测，并确认 WMI/DCOM 管理通道可用。" );
            return;
        }

        bool confirmed = await Dialogs.ShowConfirmAsync(
            "连接到远程主机",
            $"名称：{profile.DisplayName}\nIPv4：{profile.Address}\n身份：{SelectedIdentity}\n\n确认连接并将其虚拟机添加到虚拟机列表吗？",
            "确认连接",
            "取消",
            showIcon: true,
            maxWidth: 420);
        if (!confirmed) return;

        _switchCancellation?.Cancel();
        _switchCancellation?.Dispose();
        _switchCancellation = new CancellationTokenSource();
        IsSwitching = true;
        try
        {
            HostConnectRequest request = HostConnectRequest.ForConfirmedDiagnostic(
                profile,
                report.ConsoleAvailable,
                _transientCredentials.GetValueOrDefault(profile.Id));
            HostConnectResult result = await _sessionRegistry.ConnectAsync(
                request,
                _switchCancellation.Token);
            if (result.Succeeded)
                ShowSuccess(result.Message);
            else
                ShowError(result.Message);
        }
        finally
        {
            IsSwitching = false;
            UpdateActiveHostProperties();
        }
    }

    private async Task DisconnectSelectedHostAsync(HostProfile profile)
    {
        _switchCancellation?.Cancel();
        _switchCancellation?.Dispose();
        _switchCancellation = new CancellationTokenSource();
        _isDisconnecting = true;
        IsSwitching = true;
        try
        {
            HostDisconnectWorkflowResult result = await _disconnectCoordinator.DisconnectAsync(
                HostId.FromProfile(profile),
                profile.DisplayName,
                (prompt, _) => Dialogs.ShowConfirmAsync(
                    "断开远程宿主",
                    prompt.Message,
                    "断开",
                    "取消",
                    isDanger: true,
                    showIcon: true,
                    maxWidth: 440),
                _switchCancellation.Token);
            if (result.Succeeded) ShowSuccess(result.Message);
            else if (!result.Cancelled) ShowError(result.Message);
        }
        catch (OperationCanceledException)
        {
            // Selection changes cancel the pending action without changing host state.
        }
        finally
        {
            _isDisconnecting = false;
            IsSwitching = false;
            UpdateActiveHostProperties();
        }
    }

    private async Task EditProfileAsync(HostProfile? existing)
    {
        var editor = new HostProfileEditorViewModel(existing);
        while (true)
        {
            var view = new HostProfileEditorView { DataContext = editor };
            bool confirmed = await Dialogs.ShowContentDialogAsync(
                existing is null ? "添加局域网主机" : "编辑主机配置",
                view,
                existing is null ? "添加并检测" : "保存并检测");
            if (!confirmed) return;
            if (!editor.TryBuild(out HostProfile? profile, out WindowsCredential? suppliedCredential))
                continue;

            try
            {
                WindowsCredential? credentialToRemember = editor.RememberCredential ? suppliedCredential : null;
                HostProfile saved = _profileManager.Save(profile!, credentialToRemember);
                _reports.Remove(saved.Id);
                if (!editor.RememberCredential && suppliedCredential is not null)
                    _transientCredentials[saved.Id] = suppliedCredential with { UserName = saved.UserName! };
                else if (editor.RememberCredential || saved.AuthenticationMode == HostAuthenticationMode.CurrentWindowsIdentity)
                    _transientCredentials.Remove(saved.Id);

                RefreshProfiles();
                SelectedHost = Hosts.First(item => item.Profile?.Id == saved.Id);
                ShowSuccess("主机配置已保存，正在开始连接诊断。");
                await DiagnoseSelectedHostAsync();
                return;
            }
            catch (Exception ex)
            {
                editor.ErrorMessage = SensitiveDataRedactor.Redact(ex.Message);
                AppLog.Error("主机配置", "保存主机配置失败。", exception: ex);
            }
        }
    }

    private void ApplyReport(HostDiagnosticReport? report)
    {
        NotifyRepairStateChanged();
        if (SelectedHost?.Profile is null)
        {
            DiagnosticSummary = "本地宿主已连接；远程诊断不适用于本机。";
            ManagementStatus = "可用";
            ManagementStatusGlyph = "\uEC61";
            ManagementStatusUsesDot = false;
            ManagementStatusBrush = UiStatusBrushes.Success;
            ManagementDetail = $"本机 {WindowsWmiDcomProbe.HyperVNamespace}";
            ConsoleStatus = "可用";
            ConsoleStatusGlyph = "\uEC61";
            ConsoleStatusUsesDot = false;
            ConsoleStatusBrush = UiStatusBrushes.Success;
            ConsoleDetail = $"本机 TCP {HostDiagnosticPipeline.ConsolePort}";
            DiagnosticSteps.Clear();
            DiagnosticLogText = string.Empty;
            return;
        }

        if (report is null)
        {
            DiagnosticSummary = "尚未检测。WMI/DCOM 与 TCP 2179 将分别验证。";
            ManagementStatus = "未检测";
            ManagementStatusGlyph = string.Empty;
            ManagementStatusUsesDot = true;
            ManagementStatusBrush = UiStatusBrushes.Caution;
            ManagementDetail = $"等待查询 {WindowsWmiDcomProbe.HyperVNamespace}";
            ConsoleStatus = "未检测";
            ConsoleStatusGlyph = string.Empty;
            ConsoleStatusUsesDot = true;
            ConsoleStatusBrush = UiStatusBrushes.Caution;
            ConsoleDetail = $"等待检测 {SelectedAddress}:{HostDiagnosticPipeline.ConsolePort}";
            DiagnosticSteps.Clear();
            DiagnosticLogText = string.Empty;
            return;
        }

        DiagnosticSummary = report.Availability switch
        {
            HostDiagnosticAvailability.FullyAvailable => "全部可用：管理和控制台通道均已通过。",
            HostDiagnosticAvailability.PartiallyAvailable when report.ManagementAvailable => "部分可用：管理功能可用，虚拟机控制台不可用。",
            HostDiagnosticAvailability.PartiallyAvailable => "部分可用：控制台端口可达，但管理通道不可用。",
            HostDiagnosticAvailability.Cancelled => "检测已取消。",
            _ => "不可用：WMI/DCOM 与 TCP 2179 均未通过。"
        };
        HostDiagnosticStepResult wmi = report.GetStep(HostDiagnosticStepKind.WmiDcom);
        HostDiagnosticStepResult tcp = report.GetStep(HostDiagnosticStepKind.Tcp2179);
        ManagementStatus = StatusText(wmi.Status);
        ManagementStatusGlyph = StatusGlyph(wmi.Status);
        ManagementStatusUsesDot = StatusUsesDot(wmi.Status);
        ManagementStatusBrush = StatusBrush(wmi.Status);
        ManagementDetail = wmi.Explanation;
        ConsoleStatus = StatusText(tcp.Status);
        ConsoleStatusGlyph = StatusGlyph(tcp.Status);
        ConsoleStatusUsesDot = StatusUsesDot(tcp.Status);
        ConsoleStatusBrush = StatusBrush(tcp.Status);
        ConsoleDetail = tcp.Explanation;
        DiagnosticSteps = new ObservableCollection<HostDiagnosticStepItemViewModel>(
            report.Steps.Select(step => new HostDiagnosticStepItemViewModel(step)));
        DiagnosticLogText = string.Join(
            Environment.NewLine,
            report.LogEntries.Select(entry =>
                $"[{entry.Timestamp:HH:mm:ss.fff}] [{LogLevelText(entry.Level)}] {entry.Message}"));
    }

    private void RefreshProfileVisual(Guid profileId, HostDiagnosticReport report)
    {
        HostProfileListItemViewModel? item = Hosts.FirstOrDefault(candidate => candidate.Profile?.Id == profileId);
        item?.UpdateReport(report);
    }

    private void OnRegistryChanged(object? sender, HostRegistryChangedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(UpdateActiveHostProperties);
    }

    private void UpdateActiveHostProperties()
    {
        OnPropertyChanged(nameof(ActiveHostName));
        OnPropertyChanged(nameof(ActiveHostAddress));
        OnPropertyChanged(nameof(ActiveHostStatus));
        OnPropertyChanged(nameof(ActiveHostStatusBrush));
        OnPropertyChanged(nameof(ActiveHostIdentity));
        OnPropertyChanged(nameof(ActiveHostSnapshot));
        OnPropertyChanged(nameof(IsReconnectVisible));
        OnPropertyChanged(nameof(IsReconnectActive));
        OnPropertyChanged(nameof(ReconnectSummary));
        NotifyConnectionEligibilityChanged();
    }

    private void OnSelectionPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsRemoteSelected));
        OnPropertyChanged(nameof(SelectedDisplayName));
        OnPropertyChanged(nameof(SelectedAddress));
        OnPropertyChanged(nameof(SelectedIdentity));
        OnPropertyChanged(nameof(SelectedCredentialStorage));
        EditSelectedHostCommand.NotifyCanExecuteChanged();
        DeleteSelectedHostCommand.NotifyCanExecuteChanged();
        DiagnoseSelectedHostCommand.NotifyCanExecuteChanged();
        NotifyRepairStateChanged();
        NotifyConnectionEligibilityChanged();
    }

    private HostRepairDecision CurrentRepairDecision => SelectedHost?.Profile is { } profile
        ? HostRepairAdvisor.Evaluate(profile, GetCurrentReport(profile))
        : HostRepairDecision.None;

    private void NotifyRepairStateChanged()
    {
        OnPropertyChanged(nameof(IsRepairActionVisible));
        OnPropertyChanged(nameof(IsRepairWorkspaceVisible));
        OnPropertyChanged(nameof(RepairActionToolTip));
        OnPropertyChanged(nameof(RepairGuidance));
        OnPropertyChanged(nameof(HasRepairGuidance));
        OpenRepairCommand.NotifyCanExecuteChanged();
    }

    private HostDiagnosticReport? GetCurrentReport(HostProfile profile) =>
        _reports.TryGetValue(profile.Id, out HostDiagnosticReport? report)
        && string.Equals(report.HostAddress, profile.Address, StringComparison.Ordinal)
            ? report
            : null;

    private HostSessionSnapshot? SelectedSession
    {
        get
        {
            HostId hostId = SelectedHost?.Profile is { } profile
                ? HostId.FromProfile(profile)
                : HostId.Local;
            return _sessionRegistry.Current.TryGet(hostId, out HostSessionSnapshot? session)
                ? session
                : null;
        }
    }

    private bool IsConnected(HostProfile profile) =>
        _sessionRegistry.Current.TryGet(HostId.FromProfile(profile), out _);

    private void NotifyConnectionEligibilityChanged()
    {
        OnPropertyChanged(nameof(CanConnectToSelectedHost));
        OnPropertyChanged(nameof(CanExecuteConnectionAction));
        OnPropertyChanged(nameof(ConnectHint));
        OnPropertyChanged(nameof(ConnectionActionText));
        OnPropertyChanged(nameof(ConnectionActionAppearance));
        OnPropertyChanged(nameof(ConnectionActionToolTip));
        ConnectToSelectedHostCommand.NotifyCanExecuteChanged();
    }

    private static string StatusText(HostDiagnosticStepStatus status) => status switch
    {
        HostDiagnosticStepStatus.Succeeded => "可用",
        HostDiagnosticStepStatus.Failed => "不可用",
        HostDiagnosticStepStatus.Cancelled => "已取消",
        _ => "未检测"
    };

    private static MediaBrush StatusBrush(HostDiagnosticStepStatus status) => status switch
    {
        HostDiagnosticStepStatus.Succeeded => UiStatusBrushes.Success,
        HostDiagnosticStepStatus.Failed => UiStatusBrushes.Critical,
        HostDiagnosticStepStatus.Cancelled => UiStatusBrushes.Neutral,
        _ => UiStatusBrushes.Caution
    };

    private static string StatusGlyph(HostDiagnosticStepStatus status) => status switch
    {
        HostDiagnosticStepStatus.Succeeded => "\uEC61",
        HostDiagnosticStepStatus.Failed => "\uEB90",
        _ => string.Empty
    };

    private static bool StatusUsesDot(HostDiagnosticStepStatus status) =>
        status is not HostDiagnosticStepStatus.Succeeded and not HostDiagnosticStepStatus.Failed;

    private static string LogLevelText(HostDiagnosticLogLevel level) => level switch
    {
        HostDiagnosticLogLevel.Information => "信息",
        HostDiagnosticLogLevel.Warning => "警告",
        HostDiagnosticLogLevel.Error => "错误",
        _ => "信息"
    };

    private void ReportArtifactLocation(SupportArtifactLocationResult result, string component)
    {
        if (result.Succeeded)
        {
            AppLog.Information(component, result.Message);
            ShowSuccess(result.Message);
            return;
        }

        AppLog.Warning(component, result.Message);
        ShowError(result.Message);
    }

    public void Dispose()
    {
        _sessionRegistry.Changed -= OnRegistryChanged;
        Preflight.Dispose();
        InvalidateConfigurationRun();
        InvalidateDiagnosticRun();
        _diagnosticRuns.Dispose();
        Logs.Dispose();
        _switchCancellation?.Cancel();
        _switchCancellation?.Dispose();
    }
}

public partial class HostProfileListItemViewModel : ObservableObject
{
    [ObservableProperty] private string _stateText;
    [ObservableProperty] private MediaBrush _stateBrush;

    private HostProfileListItemViewModel(
        HostProfile? profile,
        string displayName,
        string address,
        string stateText,
        MediaBrush stateBrush)
    {
        Profile = profile;
        DisplayName = displayName;
        Address = address;
        _stateText = stateText;
        _stateBrush = stateBrush;
    }

    public HostProfile? Profile { get; }
    public string DisplayName { get; }
    public string Address { get; }
    public string Icon => Profile is null ? "Desktop24" : "Server24";

    public static HostProfileListItemViewModel Local(string computerName) =>
        new(null, "本地计算机", computerName, "已连接", UiStatusBrushes.Success);

    public static HostProfileListItemViewModel Remote(HostProfile profile, HostDiagnosticReport? report)
    {
        var item = new HostProfileListItemViewModel(profile, profile.DisplayName, profile.Address, "未检测", UiStatusBrushes.Caution);
        if (report is not null) item.UpdateReport(report);
        return item;
    }

    public void UpdateReport(HostDiagnosticReport report)
    {
        (StateText, StateBrush) = report.Availability switch
        {
            HostDiagnosticAvailability.FullyAvailable => ("全部可用", UiStatusBrushes.Success),
            HostDiagnosticAvailability.PartiallyAvailable => ("部分可用", UiStatusBrushes.Caution),
            HostDiagnosticAvailability.Cancelled => ("已取消", UiStatusBrushes.Neutral),
            _ => ("不可用", UiStatusBrushes.Critical)
        };
    }
}

public sealed class HostDiagnosticStepItemViewModel
{
    public HostDiagnosticStepItemViewModel(HostDiagnosticStepResult result)
    {
        Name = result.Kind switch
        {
            HostDiagnosticStepKind.Ipv4Reachability => "IPv4 可达性",
            HostDiagnosticStepKind.Identity => "连接身份",
            HostDiagnosticStepKind.WmiDcom => "WMI / DCOM",
            HostDiagnosticStepKind.Tcp2179 => "TCP 2179",
            _ => result.Kind.ToString()
        };
        Explanation = result.Explanation;
        Duration = result.Duration == TimeSpan.Zero ? "-" : $"{result.Duration.TotalMilliseconds:F0} ms";
        (string statusText, string statusIconGlyph, MediaBrush statusBrush, bool statusUsesDot) = result.Status switch
        {
            HostDiagnosticStepStatus.Succeeded => ("通过", "\uEC61", UiStatusBrushes.Success, false),
            HostDiagnosticStepStatus.Failed => ("失败", "\uEB90", UiStatusBrushes.Critical, false),
            HostDiagnosticStepStatus.Cancelled => ("已取消", string.Empty, UiStatusBrushes.Neutral, true),
            _ => ("已跳过", string.Empty, UiStatusBrushes.Caution, true)
        };
        StatusText = statusText;
        StatusIconGlyph = statusIconGlyph;
        StatusBrush = statusBrush;
        StatusUsesDot = statusUsesDot;
    }

    public string Name { get; }
    public string Explanation { get; }
    public string Duration { get; }
    public string StatusText { get; }
    public string StatusIconGlyph { get; }
    public bool StatusUsesDot { get; }
    public MediaBrush StatusBrush { get; }
}
