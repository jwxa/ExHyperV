using System.Collections.ObjectModel;
using ExHyperV.Services.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Configuration;
using ExHyperV.Services.Remote.Preflight;
using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.ViewModels;

public partial class HostPreflightViewModel(HostPreflightPipeline pipeline) : ObservableObject, IDisposable
{
    private HostProfile? _profile;
    private WindowsCredential? _transientCredential;
    private HostPreflightReport? _report;
    private HostPreflightPlan? _approvedPlan;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _stepIndex;
    [ObservableProperty] private string _hostLabel = "选择远程主机后开始预检";
    [ObservableProperty] private string _summary = "预检只读取远程状态，不会修改账户、注册表、网络或防火墙。";
    [ObservableProperty] private string _preflightLogText = string.Empty;
    [ObservableProperty] private ObservableCollection<HostPreflightFindingItemViewModel> _findings = [];
    [ObservableProperty] private ObservableCollection<HostPreflightAccountOptionViewModel> _localAccounts = [];
    [ObservableProperty] private HostPreflightAccountOptionViewModel? _selectedLocalAccount;
    [ObservableProperty] private bool _useDomainAccount;
    [ObservableProperty] private string _domainAccountName = string.Empty;
    [ObservableProperty] private ObservableCollection<HostPreflightNetworkOptionViewModel> _networks = [];
    [ObservableProperty] private ObservableCollection<HostPreflightCidrOptionViewModel> _detectedCidrs = [];
    [ObservableProperty] private string _manualCidrs = string.Empty;
    [ObservableProperty] private ObservableCollection<HostPreflightChangeItemViewModel> _plannedChanges = [];
    [ObservableProperty] private string _selectionError = string.Empty;
    [ObservableProperty] private string _previewSummary = "完成账户、网络与访问范围选择后生成预览。";
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private bool _hasApplyResult;
    [ObservableProperty] private string _applySummary = string.Empty;
    [ObservableProperty] private string _applyLogText = string.Empty;
    [ObservableProperty] private string? _rollbackScriptPath;
    [ObservableProperty] private string _rollbackInstruction = string.Empty;
    [ObservableProperty] private ObservableCollection<HostConfigurationStepItemViewModel> _applySteps = [];

    public bool IsDetectStep => StepIndex == 0;
    public bool IsAccountStep => StepIndex == 1;
    public bool IsNetworkStep => StepIndex == 2;
    public bool IsPreviewStep => StepIndex == 3;
    public bool CanGoBack => StepIndex > 0 && !IsRunning && !IsApplying;
    public bool CanGoNext => StepIndex < 3 && !IsRunning && !IsApplying && _report is not null;
    public string NextButtonText => StepIndex == 2 ? "生成预览" : "下一步";
    public bool HasPlan => PlannedChanges.Count > 0;
    public bool CanApply => _report is not null && _approvedPlan is not null && HasPlan && !IsRunning && !IsApplying;

    public IReadOnlyList<HostPreflightStepItemViewModel> Steps { get; } =
    [
        new("1", "环境检测", "只读事实与日志") { IsActive = true },
        new("2", "账户授权", "选择本地或域账户"),
        new("3", "网络与防火墙", "选择网络和 CIDR"),
        new("4", "修改预览", "审查但不执行")
    ];

    partial void OnStepIndexChanged(int value)
    {
        for (int index = 0; index < Steps.Count; index++)
            Steps[index].IsActive = index == value;
        OnPropertyChanged(nameof(IsDetectStep));
        OnPropertyChanged(nameof(IsAccountStep));
        OnPropertyChanged(nameof(IsNetworkStep));
        OnPropertyChanged(nameof(IsPreviewStep));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(NextButtonText));
        PreviousStepCommand.NotifyCanExecuteChanged();
        NextStepCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        RunCommand.NotifyCanExecuteChanged();
        PreviousStepCommand.NotifyCanExecuteChanged();
        NextStepCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsApplyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanApply));
        PreviousStepCommand.NotifyCanExecuteChanged();
        NextStepCommand.NotifyCanExecuteChanged();
    }

    partial void OnUseDomainAccountChanged(bool value) => SelectionError = string.Empty;
    partial void OnDomainAccountNameChanged(string value) => SelectionError = string.Empty;
    partial void OnSelectedLocalAccountChanged(HostPreflightAccountOptionViewModel? value) => SelectionError = string.Empty;

    public void SetTarget(HostProfile profile, WindowsCredential? transientCredential)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _transientCredential = transientCredential;
        HostLabel = $"{profile.DisplayName} · {profile.Address}";
        RunCommand.NotifyCanExecuteChanged();
    }

    public void Reset()
    {
        Cancel();
        _report = null;
        _approvedPlan = null;
        StepIndex = 0;
        Findings.Clear();
        LocalAccounts.Clear();
        Networks.Clear();
        DetectedCidrs.Clear();
        PlannedChanges.Clear();
        SelectedLocalAccount = null;
        UseDomainAccount = false;
        DomainAccountName = string.Empty;
        ManualCidrs = string.Empty;
        PreflightLogText = string.Empty;
        SelectionError = string.Empty;
        Summary = "预检只读取远程状态，不会修改账户、注册表、网络或防火墙。";
        PreviewSummary = "完成账户、网络与访问范围选择后生成预览。";
        IsApplying = false;
        HasApplyResult = false;
        ApplySummary = string.Empty;
        ApplyLogText = string.Empty;
        RollbackScriptPath = null;
        RollbackInstruction = string.Empty;
        ApplySteps.Clear();
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(CanApply));
        NextStepCommand.NotifyCanExecuteChanged();
    }

    public void ClearTarget()
    {
        Reset();
        _profile = null;
        _transientCredential = null;
        HostLabel = "选择远程主机后开始预检";
        RunCommand.NotifyCanExecuteChanged();
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation = _cancellation;
        if (cancellation is null) return;
        _cancellation = null;
        cancellation.Cancel();
        IsRunning = false;
        Summary = "预检已取消；未对远程主机执行任何修改。";
    }

    public void Dispose()
    {
        Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanRun), AllowConcurrentExecutions = true)]
    private async Task RunAsync()
    {
        if (_profile is null || IsRunning) return;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsRunning = true;
        StepIndex = 0;
        SelectionError = string.Empty;
        PlannedChanges.Clear();
        _approvedPlan = null;
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(CanApply));
        Findings.Clear();
        Summary = $"正在读取 {_profile.DisplayName} 的账户、组、网络、策略与防火墙状态...";
        try
        {
            HostPreflightReport report = await pipeline.RunAsync(_profile, _transientCredential, cancellation.Token);
            if (!ReferenceEquals(_cancellation, cancellation) || cancellation.IsCancellationRequested) return;
            _report = report;
            ApplyReport(report);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_cancellation, cancellation))
                Summary = "预检已取消；未对远程主机执行任何修改。";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_cancellation, cancellation))
                Summary = $"预检失败：{SensitiveDataRedactor.Redact(ex.Message)}；未对远程主机执行任何修改。";
        }
        finally
        {
            bool isCurrentRun = ReferenceEquals(_cancellation, cancellation);
            if (isCurrentRun)
            {
                _cancellation = null;
                IsRunning = false;
                OnPropertyChanged(nameof(CanGoNext));
                NextStepCommand.NotifyCanExecuteChanged();
            }
            cancellation.Dispose();
        }
    }

    private bool CanRun() => !IsRunning && _profile is not null;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousStep()
    {
        SelectionError = string.Empty;
        StepIndex--;
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextStep()
    {
        SelectionError = string.Empty;
        if (StepIndex == 1 && !ValidateAccount()) return;
        if (StepIndex == 2 && !BuildPreview()) return;
        StepIndex++;
    }

    private bool ValidateAccount()
    {
        if (UseDomainAccount)
        {
            string value = DomainAccountName.Trim();
            bool valid = value.Contains('\\') && value.IndexOf('\\') is > 0 && value.IndexOf('\\') < value.Length - 1
                         || value.Contains('@') && value.IndexOf('@') is > 0 && value.IndexOf('@') < value.Length - 1;
            if (valid) return true;
            SelectionError = "域账户必须使用 DOMAIN\\User 或 user@domain 的格式。";
            return false;
        }
        if (SelectedLocalAccount is not null) return true;
        SelectionError = "请选择一个检测到的已启用本地账户。";
        return false;
    }

    private bool BuildPreview()
    {
        if (_report is null) return false;
        string accountName = UseDomainAccount ? DomainAccountName.Trim() : SelectedLocalAccount?.Name ?? string.Empty;
        string[] manualCidrs = ManualCidrs
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] cidrs = DetectedCidrs.Where(item => item.IsSelected).Select(item => item.Cidr)
            .Concat(manualCidrs)
            .ToArray();
        var selection = new HostPreflightSelection(
            UseDomainAccount ? HostPreflightAccountKind.Domain : HostPreflightAccountKind.Local,
            accountName,
            Networks.Where(item => item.IsSelected).Select(item => item.InterfaceIndex).ToArray(),
            Networks.Where(item => item.IsSelected && item.MakePrivate).Select(item => item.InterfaceIndex).ToArray(),
            cidrs);
        HostPreflightPlanResult result = HostPreflightPlanner.Build(_report, selection);
        if (!result.IsValid)
        {
            SelectionError = string.Join(Environment.NewLine, result.Errors);
            return false;
        }

        PlannedChanges = new ObservableCollection<HostPreflightChangeItemViewModel>(
            result.Plan!.Changes.Select((change, index) => new HostPreflightChangeItemViewModel(index + 1, change)));
        _approvedPlan = result.Plan;
        PreviewSummary = PlannedChanges.Count == 0
            ? "检测结果与当前选择不需要修改。"
            : $"已生成 {PlannedChanges.Count} 项拟执行修改；当前阶段不会应用这些修改。";
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(CanApply));
        return true;
    }

    public bool TryGetApprovedConfiguration(out HostPreflightReport? report, out HostPreflightPlan? plan)
    {
        report = _report;
        plan = _approvedPlan;
        return report is not null && plan is not null && plan.Changes.Count > 0;
    }

    public void BeginApply()
    {
        IsApplying = true;
        HasApplyResult = false;
        ApplySummary = "正在重新检测远程状态并应用已确认修改...";
        ApplyLogText = string.Empty;
        RollbackScriptPath = null;
        RollbackInstruction = string.Empty;
        ApplySteps.Clear();
    }

    public void CompleteApply(HostConfigurationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        IsApplying = false;
        HasApplyResult = true;
        ApplySummary = report.Succeeded
            ? "配置已完成，WMI/DCOM 与 TCP 2179 已自动复检。"
            : report.StalePreview
                ? "修改预览已过期，请返回环境检测并重新运行预检。"
                : report.Started
                    ? "配置部分完成或验证失败，请保留并检查回滚脚本。"
                    : "未执行任何修改。";
        ApplyLogText = string.Join(Environment.NewLine, report.Logs.Select((line, index) => $"[{index + 1:D2}] {line}"));
        RollbackScriptPath = report.RollbackScriptPath;
        RollbackInstruction = report.RollbackScriptPath is null || _profile is null
            ? string.Empty
            : $"请先将脚本复制到目标宿主 {_profile.DisplayName}（{_profile.Address}），再在该宿主本地使用管理员 PowerShell 审查并运行；脚本仍要求输入精确的中文“确认”。";
        ApplySteps = new ObservableCollection<HostConfigurationStepItemViewModel>(
            report.Steps.Select(step => new HostConfigurationStepItemViewModel(step)));
    }

    private void ApplyReport(HostPreflightReport report)
    {
        Findings = new ObservableCollection<HostPreflightFindingItemViewModel>(
            report.Findings.Select(finding => new HostPreflightFindingItemViewModel(finding)));
        PreflightLogText = string.Join(
            Environment.NewLine,
            report.LogEntries.Select(entry => $"[{entry.Timestamp:HH:mm:ss.fff}] [{LogLevelText(entry.Level)}] {entry.Message}"));
        int failures = report.Findings.Count(finding => finding.Status == HostPreflightFindingStatus.Failed);
        int attention = report.Findings.Count(finding => finding.Status == HostPreflightFindingStatus.Attention);
        Summary = failures > 0
            ? $"预检完成：{failures} 项读取失败，{attention} 项需要关注。请查看详细日志后重新检测。"
            : $"预检完成：{attention} 项需要关注，其余项目已读取。未执行任何修改。";

        bool IsAdministrator(string account) => report.Facts.LocalGroups
            .TryGetValue(HostLocalGroupKind.Administrators, out HostLocalGroupSnapshot? group)
            && group.Members.Any(member =>
                string.Equals(member, account, StringComparison.OrdinalIgnoreCase)
                || member.EndsWith($"\\{account}", StringComparison.OrdinalIgnoreCase));
        bool IsMember(HostLocalGroupKind groupKind, string account) => report.Facts.LocalGroups
            .TryGetValue(groupKind, out HostLocalGroupSnapshot? group)
            && group.Members.Any(member =>
                string.Equals(member, account, StringComparison.OrdinalIgnoreCase)
                || member.EndsWith($"\\{account}", StringComparison.OrdinalIgnoreCase));
        LocalAccounts = new ObservableCollection<HostPreflightAccountOptionViewModel>(
            report.Facts.EnabledLocalAccounts
                .Select(account => new HostPreflightAccountOptionViewModel(
                    account.Name,
                    account.Sid,
                    IsAdministrator(account.Name),
                    IsMember(HostLocalGroupKind.HyperVAdministrators, account.Name),
                    IsMember(HostLocalGroupKind.RemoteManagementUsers, account.Name)))
                .OrderByDescending(account => account.IsAdministrator)
                .ThenBy(account => account.Name, StringComparer.OrdinalIgnoreCase));
        SelectedLocalAccount = LocalAccounts.FirstOrDefault();

        Networks = new ObservableCollection<HostPreflightNetworkOptionViewModel>(
            report.Facts.Networks.Select(network => new HostPreflightNetworkOptionViewModel(network)));
        DetectedCidrs = new ObservableCollection<HostPreflightCidrOptionViewModel>(
            report.Facts.Networks
                .SelectMany(network => network.Ipv4Addresses.Select(address => (network.Name, address.Cidr)))
                .Where(item => Ipv4Cidr.IsPrivate(item.Cidr))
                .GroupBy(item => item.Cidr, StringComparer.OrdinalIgnoreCase)
                .Select(group => new HostPreflightCidrOptionViewModel(group.Key, string.Join("、", group.Select(item => item.Name).Distinct()))));
    }

    private static string LogLevelText(HostPreflightLogLevel level) => level switch
    {
        HostPreflightLogLevel.Warning => "警告",
        HostPreflightLogLevel.Error => "错误",
        _ => "信息"
    };
}

public sealed class HostPreflightFindingItemViewModel
{
    public HostPreflightFindingItemViewModel(HostPreflightFinding finding)
    {
        Title = finding.Title;
        Evidence = finding.Evidence;
        (StatusText, StatusIconGlyph, StatusBrush, StatusUsesDot) = finding.Status switch
        {
            HostPreflightFindingStatus.Passed => ("通过", "\uEC61", UiStatusBrushes.Success, false),
            HostPreflightFindingStatus.Attention => ("需关注", string.Empty, UiStatusBrushes.Caution, true),
            _ => ("读取失败", "\uEB90", UiStatusBrushes.Critical, false)
        };
    }

    public string Title { get; }
    public string Evidence { get; }
    public string StatusText { get; }
    public string StatusIconGlyph { get; }
    public System.Windows.Media.Brush StatusBrush { get; }
    public bool StatusUsesDot { get; }
}

public sealed record HostPreflightAccountOptionViewModel(
    string Name,
    string Sid,
    bool IsAdministrator,
    bool IsHyperVAdministrator,
    bool IsRemoteManagementUser)
{
    public string Detail => IsAdministrator ? "本地账户 · 已启用 · 当前为管理员" : "本地账户 · 已启用";
    public string MembershipDetail =>
        $"Hyper-V Administrators：{MembershipText(IsHyperVAdministrator)} · Remote Management Users：{MembershipText(IsRemoteManagementUser)}";

    private static string MembershipText(bool member) => member ? "已加入" : "未加入";
}

public partial class HostPreflightNetworkOptionViewModel : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _makePrivate;

    public HostPreflightNetworkOptionViewModel(HostNetworkSnapshot network)
    {
        InterfaceIndex = network.InterfaceIndex;
        Name = network.Name;
        Category = network.Category;
        CategoryText = network.Category.ToString();
        AddressText = string.Join(" · ", network.Ipv4Addresses.Select(address => address.Cidr));
    }

    public uint InterfaceIndex { get; }
    public string Name { get; }
    public HostNetworkCategory Category { get; }
    public string CategoryText { get; }
    public string AddressText { get; }
    public bool CanMakePrivate => Category == HostNetworkCategory.Public;

    partial void OnIsSelectedChanged(bool value)
    {
        if (!value) MakePrivate = false;
    }
}

public partial class HostPreflightCidrOptionViewModel(string cidr, string source) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    public string Cidr { get; } = cidr;
    public string Source { get; } = source;
}

public sealed class HostPreflightChangeItemViewModel
{
    public HostPreflightChangeItemViewModel(int index, HostPreflightPlannedChange change)
    {
        Number = index.ToString("D2");
        Title = change.Title;
        Detail = change.Detail;
    }

    public string Number { get; }
    public string Title { get; }
    public string Detail { get; }
}

public sealed class HostConfigurationStepItemViewModel
{
    public HostConfigurationStepItemViewModel(HostConfigurationStepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Title = result.Title;
        Detail = result.Message;
        StatusText = result.Succeeded ? "已完成" : "失败";
        StatusBrush = result.Succeeded
            ? UiStatusBrushes.Success
            : UiStatusBrushes.Critical;
    }

    public string Title { get; }
    public string Detail { get; }
    public string StatusText { get; }
    public System.Windows.Media.Brush StatusBrush { get; }
}

public partial class HostPreflightStepItemViewModel(string number, string title, string detail) : ObservableObject
{
    [ObservableProperty] private bool _isActive;
    public string Number { get; } = number;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
}
