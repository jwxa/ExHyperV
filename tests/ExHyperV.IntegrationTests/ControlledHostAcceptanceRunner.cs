using System.Diagnostics;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Services.Remote.Configuration;
using ExHyperV.Services.Remote.Consoles;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Preflight;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Services.Remote.Vms;
using ExHyperV.Services.Remote.Windows;
using ExHyperV.Tools;

namespace ExHyperV.IntegrationTests;

internal sealed class ControlledHostAcceptanceRunner(IntegrationOptions options)
{
    private readonly IntegrationOptions _options = options;
    private readonly AcceptanceReport _report = new()
    {
        HostAddress = options.HostAddress,
        HostDisplayName = options.DisplayName,
        AuthenticationMode = options.AuthenticationMode == HostAuthenticationMode.CurrentWindowsIdentity
            ? "当前 Windows 身份"
            : "Windows Credential Manager 显式凭据",
        DangerousSwitches = new Dictionary<string, bool>
        {
            ["VM 生命周期写入"] = options.EnableVmWrite,
            ["人工网络中断与自动重连"] = options.EnableDisconnect,
            ["远程宿主配置"] = options.EnableConfiguration,
            ["人工执行回滚后的复检"] = options.EnableRollbackVerification
        }
    };

    private readonly WindowsCredentialStore _credentialStore = new();
    private WindowsCredential? _transientCredential;
    private HostProfile _profile = null!;
    private HostProfile? _secondProfile;
    private HostIdentityResolver _identityResolver = null!;
    private HostDiagnosticPipeline _diagnosticPipeline = null!;
    private HostPreflightPipeline _preflightPipeline = null!;
    private ActiveHostSessionCoordinator? _coordinator;
    private ActiveHostVmOperations? _vmOperations;
    private List<VmInstance> _virtualMachines = [];
    private bool _vmReadSucceeded;

    public async Task<AcceptanceReport> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            Initialize();
            if (!VerifySavedProfiles())
            {
                AddPreNetworkSkips("受控宿主配置证据无法保存；已在访问网络前停止验收。");
                return _report;
            }
            HostDiagnosticReport diagnostic = await RunDiagnosticsAsync(cancellationToken);
            HostPreflightReport preflight = await RunPreflightAsync("只读配置预检", cancellationToken);

            if (diagnostic.ManagementAvailable)
            {
                await ActivateHostAsync(diagnostic, cancellationToken);
                if (_coordinator?.Current.ActiveSession.Target.ProfileId == _profile.Id)
                {
                    bool primaryRestored = await RunTwoHostSwitchAsync(diagnostic, cancellationToken);
                    if (primaryRestored)
                    {
                        await QueryVirtualMachinesAsync(cancellationToken);
                        VerifyPartialAvailability(diagnostic);
                        CaptureConsoleTarget(diagnostic);
                        await RunVmWriteAsync(cancellationToken);
                        await RunConfigurationAndRollbackAsync(preflight, cancellationToken);
                        await RunDisconnectAndReconnectAsync(cancellationToken);
                    }
                    else
                    {
                        AddCoreSkips("两台受控宿主切换后未能恢复第一宿主，停止依赖第一宿主的后续验收。 ");
                    }
                }
            }
            else
            {
                _report.Add(
                    "原子激活与基础快照",
                    AcceptanceStatus.Skipped,
                    TimeSpan.Zero,
                    "WMI/DCOM 管理通道不可用，未建立候选会话。 ");
                AddCoreSkips("WMI/DCOM 管理通道不可用。 ");
            }
        }
        catch (OperationCanceledException)
        {
            _report.Add(
                "运行器",
                AcceptanceStatus.Failed,
                TimeSpan.Zero,
                "集成验收达到总超时或被取消。 ");
        }
        catch (Exception ex)
        {
            _report.Add(
                "运行器",
                AcceptanceStatus.Failed,
                TimeSpan.Zero,
                $"未处理错误：{AcceptanceReport.Safe(ex.Message)}");
        }
        finally
        {
            await ReturnToLocalAsync();
            WmiApi.ClearConnectionCache();
        }

        return _report;
    }

    private void Initialize()
    {
        _transientCredential = _options.Password is null || _options.UserName is null
            ? null
            : new WindowsCredential(_options.UserName, _options.Password);
        if (_options.Password is not null)
            Environment.SetEnvironmentVariable("EXHYPERV_INTEGRATION_PASSWORD", null);

        string? credentialTarget = _options.AuthenticationMode == HostAuthenticationMode.ExplicitCredential
                                   && _options.Password is null
            ? HostCredentialTarget.ForProfile(_options.ProfileId)
            : null;
        _profile = HostProfileValidator.ValidateAndNormalize(new HostProfile(
            _options.ProfileId,
            _options.DisplayName,
            _options.HostAddress,
            _options.AuthenticationMode,
            _options.UserName,
            credentialTarget));
        if (_options.SecondHostAddress is not null)
        {
            string? secondCredentialTarget = _options.AuthenticationMode == HostAuthenticationMode.ExplicitCredential
                                             && _options.Password is null
                ? HostCredentialTarget.ForProfile(_options.SecondProfileId!.Value)
                : null;
            _secondProfile = HostProfileValidator.ValidateAndNormalize(new HostProfile(
                _options.SecondProfileId!.Value,
                _options.SecondDisplayName!,
                _options.SecondHostAddress,
                _options.AuthenticationMode,
                _options.UserName,
                secondCredentialTarget));
        }
        _identityResolver = new HostIdentityResolver(_credentialStore);
        _diagnosticPipeline = new HostDiagnosticPipeline(
            new WindowsIpv4ReachabilityProbe(_options.OperationTimeout),
            _identityResolver,
            new WindowsExplicitCredentialValidator(_options.OperationTimeout),
            new WindowsWmiDcomProbe(_options.OperationTimeout),
            new WindowsTcpPortProbe(_options.OperationTimeout));
        _preflightPipeline = new HostPreflightPipeline(
            _identityResolver,
            new WindowsHostPreflightReader(_options.OperationTimeout));
    }

    private bool VerifySavedProfiles()
    {
        var stopwatch = Stopwatch.StartNew();
        string path = Path.ChangeExtension(_options.ReportPath, ".hosts.xml");
        HostProfile[] expected = _secondProfile is null
            ? [_profile]
            : [_profile, _secondProfile];
        try
        {
            var store = new HostProfileStore(path);
            store.Save(expected);
            HostProfile[] loaded = store.Load().ToArray();
            bool exact = loaded.SequenceEqual(expected);
            _report.Add(
                "受控宿主配置保存",
                exact ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                exact
                    ? $"已使用产品配置存储保存并重载 {loaded.Length} 个受控宿主配置。 "
                    : "保存后重载的受控宿主配置与输入不一致。 ",
                new Dictionary<string, object?>
                {
                    ["配置文件"] = path,
                    ["配置数量"] = loaded.Length,
                    ["包含第二宿主"] = _secondProfile is not null
                });
            return exact;
        }
        catch (Exception ex)
        {
            _report.Add(
                "受控宿主配置保存",
                AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                $"保存或重载受控宿主配置失败：{AcceptanceReport.Safe(ex.Message)}");
            return false;
        }
    }

    private Task<HostDiagnosticReport> RunDiagnosticsAsync(CancellationToken cancellationToken) =>
        RunDiagnosticsAsync(_profile, "两通道诊断", cancellationToken);

    private async Task<HostDiagnosticReport> RunDiagnosticsAsync(
        HostProfile profile,
        string stageName,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"[检测] {profile.DisplayName} ({profile.Address})");
        var stopwatch = Stopwatch.StartNew();
        HostDiagnosticReport diagnostic = await _diagnosticPipeline.RunAsync(
            profile,
            transientCredential: _transientCredential,
            cancellationToken);
        bool allRequiredChannels = diagnostic.ManagementAvailable && diagnostic.ConsoleAvailable;
        _report.Add(
            stageName,
            allRequiredChannels ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            allRequiredChannels
                ? "WMI/DCOM 与 TCP 2179 均可用。 "
                : "受控宿主未同时满足 WMI/DCOM 与 TCP 2179 验收条件。 ",
            new Dictionary<string, object?>
            {
                ["总体"] = diagnostic.Availability.ToString(),
                ["WMI/DCOM"] = diagnostic.GetStep(HostDiagnosticStepKind.WmiDcom).Status.ToString(),
                ["WMI/DCOM错误码"] = diagnostic.GetStep(HostDiagnosticStepKind.WmiDcom).ErrorCode.ToString(),
                ["TCP2179"] = diagnostic.GetStep(HostDiagnosticStepKind.Tcp2179).Status.ToString(),
                ["TCP2179错误码"] = diagnostic.GetStep(HostDiagnosticStepKind.Tcp2179).ErrorCode.ToString(),
                ["详细日志"] = diagnostic.LogEntries.Select(entry => new
                {
                    time = entry.Timestamp,
                    step = entry.Step?.ToString(),
                    level = entry.Level.ToString(),
                    message = AcceptanceReport.Safe(entry.Message)
                }).ToArray()
            });
        return diagnostic;
    }

    private async Task<HostPreflightReport> RunPreflightAsync(
        string stageName,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        HostPreflightReport preflight = await _preflightPipeline.RunAsync(
            _profile,
            transientCredential: _transientCredential,
            cancellationToken);
        bool IsGroupMember(HostLocalGroupKind kind, string accountName) =>
            preflight.Facts.LocalGroups.TryGetValue(kind, out HostLocalGroupSnapshot? group)
            && group.Members.Any(member =>
                string.Equals(member, accountName, StringComparison.OrdinalIgnoreCase)
                || member.EndsWith($"\\{accountName}", StringComparison.OrdinalIgnoreCase));
        _report.Add(
            stageName,
            preflight.HasReadFailures ? AcceptanceStatus.Failed : AcceptanceStatus.Passed,
            stopwatch.Elapsed,
            preflight.HasReadFailures
                ? "只读预检存在失败项；未修改远程宿主。 "
                : "只读预检完成；未修改远程宿主。 ",
            new Dictionary<string, object?>
            {
                ["计算机名"] = preflight.Facts.Join?.ComputerName,
                ["网络数量"] = preflight.Facts.Networks.Count,
                ["Public网络数量"] = preflight.Facts.Networks.Count(network => network.Category == HostNetworkCategory.Public),
                ["已启用本地账户"] = preflight.Facts.EnabledLocalAccounts.Select(account => new
                {
                    name = AcceptanceReport.Safe(account.Name),
                    sid = account.Sid,
                    administrator = IsGroupMember(HostLocalGroupKind.Administrators, account.Name),
                    hyperVAdministrator = IsGroupMember(HostLocalGroupKind.HyperVAdministrators, account.Name),
                    remoteManagementUser = IsGroupMember(HostLocalGroupKind.RemoteManagementUsers, account.Name)
                }).ToArray(),
                ["可选网络"] = preflight.Facts.Networks.Select(network => new
                {
                    interfaceIndex = network.InterfaceIndex,
                    name = AcceptanceReport.Safe(network.Name),
                    category = network.Category.ToString(),
                    cidrs = network.Ipv4Addresses.Select(address => address.Cidr).ToArray()
                }).ToArray(),
                ["失败项"] = preflight.Findings.Count(finding => finding.Status == HostPreflightFindingStatus.Failed),
                ["需关注项"] = preflight.Findings.Count(finding => finding.Status == HostPreflightFindingStatus.Attention),
                ["详细日志"] = preflight.LogEntries.Select(entry => new
                {
                    time = entry.Timestamp,
                    stage = entry.Stage.ToString(),
                    level = entry.Level.ToString(),
                    message = AcceptanceReport.Safe(entry.Message)
                }).ToArray()
            });
        return preflight;
    }

    private async Task ActivateHostAsync(
        HostDiagnosticReport diagnostic,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var connector = new WindowsHostSessionConnector(
            _identityResolver,
            _options.OperationTimeout,
            new WindowsTcpPortProbe(_options.OperationTimeout));
        _coordinator = new ActiveHostSessionCoordinator(
            connector,
            new WindowsHostBasicSnapshotLoader());
        _vmOperations = new ActiveHostVmOperations(_coordinator, new HostWmiContextResolver());
        _coordinator.SelectProfile(_profile);
        HostSwitchResult result = await _coordinator.SwitchToSelectedAsync(
            HostSwitchRequest.ForConfirmedDiagnostic(
                _profile,
                diagnostic.ConsoleAvailable,
                _transientCredential),
            cancellationToken);
        HostBasicSnapshot? snapshot = result.Snapshot.BasicSnapshot;
        _report.Add(
            "原子激活与基础快照",
            result.Succeeded ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            result.Message,
            new Dictionary<string, object?>
            {
                ["切换状态"] = result.Status.ToString(),
                ["活动宿主"] = result.Snapshot.ActiveSession.Target.Address,
                ["会话代次"] = result.Snapshot.ActiveSession.Generation,
                ["计算机名"] = snapshot?.ComputerName,
                ["操作系统"] = snapshot?.OperatingSystem,
                ["虚拟机数量"] = snapshot?.VirtualMachineCount
            });
    }

    private async Task<bool> RunTwoHostSwitchAsync(
        HostDiagnosticReport firstDiagnostic,
        CancellationToken cancellationToken)
    {
        if (_secondProfile is null)
        {
            _report.Add(
                "两台受控宿主切换",
                AcceptanceStatus.Skipped,
                TimeSpan.Zero,
                "未设置 EXHYPERV_INTEGRATION_SECOND_HOST；本次运行不能证明两台远程宿主之间的切换。 ");
            return true;
        }

        var stopwatch = Stopwatch.StartNew();
        HostDiagnosticReport secondDiagnostic = await RunDiagnosticsAsync(
            _secondProfile,
            "第二受控宿主两通道诊断",
            cancellationToken);
        if (!secondDiagnostic.ManagementAvailable)
        {
            _report.Add(
                "两台受控宿主切换",
                AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                "第二受控宿主的 WMI/DCOM 管理通道不可用，未离开第一活动宿主。 ",
                new Dictionary<string, object?>
                {
                    ["第一宿主"] = _profile.Address,
                    ["第二宿主"] = _secondProfile.Address
                });
            return IsPrimaryActive();
        }

        long firstGeneration = _coordinator!.Current.ActiveSession.Generation;
        _coordinator.SelectProfile(_secondProfile);
        HostSwitchResult secondSwitch = await _coordinator.SwitchToSelectedAsync(
            HostSwitchRequest.ForConfirmedDiagnostic(
                _secondProfile,
                secondDiagnostic.ConsoleAvailable,
                _transientCredential),
            cancellationToken);
        if (!secondSwitch.Succeeded)
        {
            _coordinator.SelectProfile(_profile);
            _report.Add(
                "两台受控宿主切换",
                AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                $"从第一宿主切换到第二宿主失败：{AcceptanceReport.Safe(secondSwitch.Message)}",
                new Dictionary<string, object?>
                {
                    ["第一宿主"] = _profile.Address,
                    ["第二宿主"] = _secondProfile.Address,
                    ["第二宿主切换状态"] = secondSwitch.Status.ToString()
                });
            return IsPrimaryActive();
        }

        long secondGeneration = secondSwitch.Snapshot.ActiveSession.Generation;
        HostVmReadResult<List<VmInstance>> secondVmRead = await _vmOperations!.ReadAsync(
            (context, token) => new VmQueryService().GetVmListAsync(context, token),
            cancellationToken);

        _coordinator.SelectProfile(_profile);
        HostSwitchResult primarySwitch = await _coordinator.SwitchToSelectedAsync(
            HostSwitchRequest.ForConfirmedDiagnostic(
                _profile,
                firstDiagnostic.ConsoleAvailable,
                _transientCredential),
            cancellationToken);
        bool secondActive = secondSwitch.Snapshot.ActiveSession.Target.ProfileId == _secondProfile.Id
                            && secondGeneration > firstGeneration
                            && secondSwitch.Snapshot.BasicSnapshot is not null;
        bool primaryRestored = primarySwitch.Succeeded
                               && primarySwitch.Snapshot.ActiveSession.Target.ProfileId == _profile.Id
                               && primarySwitch.Snapshot.ActiveSession.Generation > secondGeneration
                               && primarySwitch.Snapshot.BasicSnapshot is not null;
        bool complete = secondActive && secondVmRead.Succeeded && primaryRestored;
        _report.Add(
            "两台受控宿主切换",
            complete ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            complete
                ? "已完成第一宿主 → 第二宿主 → 第一宿主的原子切换，并通过第二宿主活动上下文读取真实 VM 列表。 "
                : "两台受控宿主的往返切换或第二宿主 VM 读取证据不完整。 ",
            new Dictionary<string, object?>
            {
                ["第一宿主"] = _profile.Address,
                ["第一宿主初始代次"] = firstGeneration,
                ["第二宿主"] = _secondProfile.Address,
                ["第二宿主切换状态"] = secondSwitch.Status.ToString(),
                ["第二宿主代次"] = secondGeneration,
                ["第二宿主快照计算机名"] = secondSwitch.Snapshot.BasicSnapshot?.ComputerName,
                ["第二宿主VM读取状态"] = secondVmRead.Status.ToString(),
                ["第二宿主VM数量"] = secondVmRead.Value?.Count,
                ["返回第一宿主状态"] = primarySwitch.Status.ToString(),
                ["第一宿主恢复代次"] = primarySwitch.Snapshot.ActiveSession.Generation,
                ["第一宿主快照计算机名"] = primarySwitch.Snapshot.BasicSnapshot?.ComputerName
            });
        return primaryRestored;

        bool IsPrimaryActive() =>
            _coordinator!.Current.ActiveSession.Target.ProfileId == _profile.Id;
    }

    private async Task QueryVirtualMachinesAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        HostVmReadResult<List<VmInstance>> result = await _vmOperations!.ReadAsync(
            (context, token) => new VmQueryService().GetVmListAsync(context, token),
            cancellationToken);
        _virtualMachines = result.Value ?? [];
        _vmReadSucceeded = result.Succeeded;
        _report.Add(
            "真实远程虚拟机列表",
            result.Succeeded ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            result.Succeeded ? $"通过活动会话读取到 {_virtualMachines.Count} 台虚拟机。 " : result.Message,
            new Dictionary<string, object?>
            {
                ["操作状态"] = result.Status.ToString(),
                ["虚拟机数量"] = _virtualMachines.Count,
                ["虚拟机"] = _virtualMachines.Select(vm => new
                {
                    id = vm.Id,
                    name = vm.Name,
                    stateCode = vm.StateCode,
                    state = vm.StateText
                }).ToArray()
            });
    }

    private void VerifyPartialAvailability(HostDiagnosticReport diagnostic)
    {
        if (diagnostic.ConsoleAvailable)
        {
            _report.Add(
                "TCP 2179 不可用时的管理降级",
                AcceptanceStatus.Skipped,
                TimeSpan.Zero,
                "本次运行 TCP 2179 可用；请在受控场景中临时阻断 2179 后另行记录管理降级证据。 ");
            return;
        }

        var sessions = new ActiveHostConsoleSessions(_coordinator!);
        HostConsoleSessionCapture capture = sessions.Capture(
            "baf3c735-4a86-4bb6-85dc-c0b829ce72f6",
            "TCP 2179 能力门控探针");
        PartialAvailabilityEvidence evidence = PartialAvailabilityAcceptance.Evaluate(
            _coordinator!.Current,
            _vmReadSucceeded,
            capture);
        _report.Add(
            "TCP 2179 不可用时的管理降级",
            evidence.IsComplete ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            TimeSpan.Zero,
            evidence.IsComplete
                ? "真实 WMI 虚拟机读取保持可用，控制台能力置灰并返回明确的 TCP 2179 原因。 "
                : "未完整证明 TCP 2179 故障只禁用控制台而不影响管理。 ",
            new Dictionary<string, object?>
            {
                ["真实VM读取成功"] = evidence.ManagementReadSucceeded,
                ["VM读取能力可用"] = evidence.VmReadAvailable,
                ["VM写入能力可用"] = evidence.VmWriteAvailable,
                ["控制台能力已禁用"] = evidence.ConsoleUnavailable,
                ["控制台置灰原因"] = evidence.ConsoleUnavailableReason,
                ["控制台捕获被拒绝"] = evidence.ConsoleCaptureRejected
            });
    }

    private void CaptureConsoleTarget(HostDiagnosticReport diagnostic)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!diagnostic.ConsoleAvailable)
        {
            _report.Add(
                "TCP 2179 控制台捕获",
                AcceptanceStatus.Skipped,
                stopwatch.Elapsed,
                "TCP 2179 诊断失败；本次运行已由管理降级场景验证控制台能力门控，不执行可用通道捕获。 ");
            return;
        }
        VmInstance? vm = _virtualMachines.FirstOrDefault();
        if (vm is null)
        {
            _report.Add(
                "TCP 2179 控制台捕获",
                AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                "远程宿主没有可用于控制台捕获的虚拟机。 ");
            return;
        }

        var sessions = new ActiveHostConsoleSessions(_coordinator!);
        HostConsoleSessionCapture capture = sessions.Capture(vm.Id.ToString("D"), vm.Name);
        bool valid = capture.Succeeded
                     && capture.Session?.Server == _profile.Address
                     && capture.Session.Port == ActiveHostConsoleSessions.ConsolePort
                     && sessions.IsCurrent(capture.Session);
        _report.Add(
            "TCP 2179 控制台捕获",
            valid ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            valid ? "控制台会话绑定到当前活动宿主、会话代次和 TCP 2179。 " : capture.Message,
            new Dictionary<string, object?>
            {
                ["服务器"] = capture.Session?.Server,
                ["端口"] = capture.Session?.Port,
                ["虚拟机ID"] = capture.Session?.VmId,
                ["仍属当前会话"] = capture.Session is not null && sessions.IsCurrent(capture.Session)
            });
    }

    private async Task RunVmWriteAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableVmWrite)
        {
            _report.Add(
                "VM 生命周期写入",
                AcceptanceStatus.Skipped,
                TimeSpan.Zero,
                "未设置 EXHYPERV_INTEGRATION_VM_WRITE=确认；未执行任何 VM 写入。 ");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        VmInstance? vm = ResolveVm(_options.VmSelector!);
        if (vm is null)
        {
            _report.Add(
                "VM 生命周期写入",
                AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                "指定的 VM 名称或 GUID 不在本次远程列表中。 ");
            return;
        }

        HostOperationStamp stamp = _coordinator!.CaptureOperationStamp();
        HostVmWriteResult result = await _vmOperations!.WriteAsync(
            async (context, token) =>
            {
                ApiResponse response = await VmPowerService.ExecuteControlActionAsync(
                    vm.Name,
                    _options.VmAction!,
                    context,
                    token);
                return response.Success
                    ? HostVmBackendWriteResult.Success("远程 VM 生命周期操作成功。 ")
                    : HostVmBackendWriteResult.Failure(response);
            },
            stamp,
            cancellationToken);
        _report.Add(
            "VM 生命周期写入",
            result.Succeeded ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            result.Succeeded ? "受控 VM 生命周期操作完成。 " : result.Message,
            new Dictionary<string, object?>
            {
                ["操作"] = _options.VmAction,
                ["虚拟机ID"] = vm.Id,
                ["虚拟机名称"] = vm.Name,
                ["状态"] = result.Status.ToString()
            });
    }

    private async Task RunConfigurationAndRollbackAsync(
        HostPreflightReport baseline,
        CancellationToken cancellationToken)
    {
        if (!_options.PreviewConfiguration && !_options.EnableConfiguration)
        {
            _report.Add(
                "远程宿主配置",
                AcceptanceStatus.Skipped,
                TimeSpan.Zero,
                "未设置 EXHYPERV_INTEGRATION_CONFIGURE=确认；只保留预检结果。 ");
            _report.Add(
                "回滚复检",
                AcceptanceStatus.Skipped,
                TimeSpan.Zero,
                "未启用远程配置，因此没有本次运行生成的回滚脚本。 ");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var selection = new HostPreflightSelection(
            _options.ConfigurationAccountKind!.Value,
            _options.ConfigurationAccountName!,
            _options.ConfigurationNetworkIndexes,
            _options.NetworksToMakePrivate,
            _options.AllowedIpv4Cidrs);
        HostPreflightPlanResult planResult = HostPreflightPlanner.Build(baseline, selection);
        if (!planResult.IsValid)
        {
            _report.Add(
                "远程宿主配置",
                AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                string.Join("；", planResult.Errors));
            AddRollbackSkip("配置计划无效，未生成回滚脚本。 ");
            return;
        }

        HostPreflightPlan plan = planResult.Plan!;
        if (plan.Changes.Count == 0)
        {
            _report.Add(
                "远程宿主配置",
                AcceptanceStatus.Passed,
                stopwatch.Elapsed,
                "只读预检表明目标状态已满足要求，没有执行修改。 ",
                new Dictionary<string, object?> { ["计划修改数量"] = 0 });
            AddRollbackSkip("未发生配置修改，不需要回滚。 ");
            return;
        }

        if (_options.PreviewConfiguration && !_options.EnableConfiguration)
        {
            _report.Add(
                "远程宿主配置",
                AcceptanceStatus.Passed,
                stopwatch.Elapsed,
                "已根据本次只读预检生成配置计划；预览模式未执行任何修改。 ",
                new Dictionary<string, object?>
                {
                    ["只读预览"] = true,
                    ["目标账户"] = AcceptanceReport.Safe(plan.AccountName),
                    ["目标网络"] = plan.SelectedNetworks.Select(network => new
                    {
                        interfaceIndex = network.InterfaceIndex,
                        name = AcceptanceReport.Safe(network.Name),
                        category = network.Category.ToString()
                    }).ToArray(),
                    ["允许CIDR"] = plan.AllowedIpv4Cidrs.ToArray(),
                    ["计划修改数量"] = plan.Changes.Count,
                    ["计划修改"] = plan.Changes.Select(change => new
                    {
                        kind = change.Kind.ToString(),
                        title = AcceptanceReport.Safe(change.Title),
                        detail = AcceptanceReport.Safe(change.Detail)
                    }).ToArray()
                });
            AddRollbackSkip("配置预览模式未执行修改，因此没有生成回滚脚本。 ");
            return;
        }

        var pipeline = new HostConfigurationPipeline(
            _identityResolver,
            _preflightPipeline,
            new WindowsHostConfigurationCommandRunner(_options.OperationTimeout + TimeSpan.FromSeconds(30)),
            new HostRollbackScriptWriter(),
            _diagnosticPipeline);
        HostConfigurationReport configuration = await pipeline.ApplyAsync(
            _profile,
            transientCredential: _transientCredential,
            baseline,
            plan,
            IntegrationOptions.Confirmation,
            cancellationToken);
        _report.Add(
            "远程宿主配置",
            configuration.Succeeded ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            configuration.Succeeded
                ? "配置已逐项执行、复检，并生成回滚脚本。 "
                : configuration.StalePreview
                    ? "配置预览已过期，未按旧预览继续执行。 "
                    : "配置未全部成功；请检查结果和回滚脚本。 ",
            new Dictionary<string, object?>
            {
                ["计划修改数量"] = plan.Changes.Count,
                ["成功步骤"] = configuration.Steps.Count(step => step.Succeeded),
                ["失败步骤"] = configuration.Steps.Count(step => !step.Succeeded),
                ["回滚脚本"] = configuration.RollbackScriptPath,
                ["复检WMI/DCOM"] = configuration.Diagnostic?.ManagementAvailable,
                ["复检TCP2179"] = configuration.Diagnostic?.ConsoleAvailable,
                ["步骤"] = configuration.Steps.Select(step => new
                {
                    kind = step.Kind.ToString(),
                    title = AcceptanceReport.Safe(step.Title),
                    succeeded = step.Succeeded,
                    message = AcceptanceReport.Safe(step.Message)
                }).ToArray(),
                ["详细日志"] = configuration.Logs.Select(AcceptanceReport.Safe).ToArray()
            });

        if (!_options.EnableRollbackVerification)
        {
            AddRollbackSkip(
                configuration.RollbackScriptPath is null
                    ? "本次配置没有生成回滚脚本。 "
                    : "未设置 EXHYPERV_INTEGRATION_ROLLBACK_VERIFY=确认；没有等待或自动执行回滚。 ");
            return;
        }
        await VerifyManualRollbackAsync(baseline, configuration.RollbackScriptPath, cancellationToken);
    }

    private async Task VerifyManualRollbackAsync(
        HostPreflightReport baseline,
        string? rollbackScriptPath,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (rollbackScriptPath is null)
        {
            _report.Add(
                "回滚复检",
                AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                "配置流程没有生成可供人工执行的回滚脚本。 ");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"[回滚] 脚本：{rollbackScriptPath}");
        Console.WriteLine("请在目标宿主本地使用管理员 PowerShell 运行该脚本。脚本会再次要求输入中文“确认”。");
        Console.Write("回滚完成后，在此精确输入中文“确认”开始只读复检：");
        string? confirmation = Console.ReadLine();
        if (!string.Equals(confirmation, IntegrationOptions.Confirmation, StringComparison.Ordinal))
        {
            _report.Add(
                "回滚复检",
                AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                "未收到精确的中文确认；运行器没有假定回滚已经完成。 ");
            return;
        }

        HostPreflightReport restored = await _preflightPipeline.RunAsync(
            _profile,
            transientCredential: _transientCredential,
            cancellationToken);
        bool equal = Equivalent(baseline.Facts, restored.Facts, out string difference);
        HostDiagnosticReport diagnostic = await _diagnosticPipeline.RunAsync(
            _profile,
            transientCredential: _transientCredential,
            cancellationToken);
        _report.Add(
            "回滚复检",
            equal && diagnostic.ManagementAvailable ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            equal ? "只读状态与配置前基线一致。 " : difference,
            new Dictionary<string, object?>
            {
                ["WMI/DCOM"] = diagnostic.ManagementAvailable,
                ["TCP2179"] = diagnostic.ConsoleAvailable,
                ["状态恢复"] = equal
            });
    }

    private async Task RunDisconnectAndReconnectAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableDisconnect)
        {
            _report.Add(
                "真实断线与自动重连",
                AcceptanceStatus.Skipped,
                TimeSpan.Zero,
                "未设置 EXHYPERV_INTEGRATION_DISCONNECT=确认；未要求制造网络中断。 ");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine();
        Console.WriteLine(
            $"[断线] 请在 {_options.OutageStartDelay.TotalSeconds:0} 秒内从外部阻断受控宿主网络，并保持阻断，直到运行器明确提示可以恢复。运行器不会修改本机或远程网络配置。 ");
        await Task.Delay(_options.OutageStartDelay, cancellationToken);

        long generation = _coordinator!.Current.ActiveSession.Generation;
        HostBasicSnapshot? originalSnapshot = _coordinator.Current.BasicSnapshot;
        var observer = new DisconnectAcceptanceObserver(
            _profile.Id,
            generation,
            originalSnapshot?.RefreshedAt ?? DateTimeOffset.MinValue);
        DateTimeOffset detectDeadline = DateTimeOffset.UtcNow + _options.OutageDetectionTimeout;
        void ObserveState(object? sender, ActiveHostStateChangedEventArgs change)
            => observer.Observe(change.Current, DateTimeOffset.UtcNow);
        _coordinator.StateChanged += ObserveState;
        try
        {
            observer.Observe(_coordinator.Current, DateTimeOffset.UtcNow);
            while (DateTimeOffset.UtcNow < detectDeadline && !observer.Capture().StaleDataObserved)
            {
                _ = await _vmOperations!.ReadAsync(
                    (context, token) => new VmQueryService().GetVmListAsync(context, token),
                    cancellationToken);
                observer.Observe(_coordinator.Current, DateTimeOffset.UtcNow);
                if (!observer.Capture().StaleDataObserved)
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            if (!observer.Capture().StaleDataObserved)
            {
                _report.Add(
                    "真实断线与自动重连",
                    AcceptanceStatus.Failed,
                    stopwatch.Elapsed,
                    "在检测窗口内没有观察到真实 WMI 连接失败和旧数据状态。 ");
                return;
            }

            bool writeBlocked = !_coordinator.TryBeginWrite(
                out IHostWriteLease? rejectedLease,
                out string writeBlockReason);
            rejectedLease?.Dispose();
            observer.RecordWriteGate(writeBlocked, writeBlockReason);

            DateTimeOffset reconnectDeadline = DateTimeOffset.UtcNow + _options.ReconnectTimeout;
            bool restorePromptShown = false;
            while (DateTimeOffset.UtcNow < reconnectDeadline)
            {
                observer.Observe(_coordinator.Current, DateTimeOffset.UtcNow);
                DisconnectAcceptanceEvidence evidence = observer.Capture();
                if (!restorePromptShown && evidence.BackoffGrowthObserved)
                {
                    restorePromptShown = true;
                    Console.WriteLine(
                        $"[断线] 已观察到递增退避（{string.Join(" / ", evidence.ScheduledDelaysSeconds.Select(value => $"{value:0.#}s"))}），现在请恢复受控宿主网络。 ");
                }
                if (evidence.FreshGenerationObserved)
                {
                    string status = evidence.IsComplete
                        ? AcceptanceStatus.Passed
                        : AcceptanceStatus.Failed;
                    _report.Add(
                        "真实断线与自动重连",
                        status,
                        stopwatch.Elapsed,
                        evidence.IsComplete
                            ? "已验证旧数据、写入禁用、递增且封顶的退避、无本机回退，以及新快照和能力恢复。 "
                            : $"已恢复连接，但验收证据不完整：{evidence.MissingSummary()}。 ",
                        new Dictionary<string, object?>
                        {
                            ["断线前会话代次"] = generation,
                            ["恢复后会话代次"] = _coordinator.Current.ActiveSession.Generation,
                            ["观察到旧数据"] = evidence.StaleDataObserved,
                            ["旧数据期间写入被拒绝"] = evidence.WriteBlockedWhileStale,
                            ["写入拒绝原因"] = evidence.WriteBlockReason,
                            ["始终保持远程活动宿主"] = evidence.StayedOnExpectedRemoteHost,
                            ["退避递增"] = evidence.BackoffGrowthObserved,
                            ["退避未超过30秒"] = evidence.BackoffCapRespected,
                            ["观察到的退避秒数"] = evidence.ScheduledDelaysSeconds,
                            ["最大重连尝试"] = evidence.MaximumReconnectAttempt,
                            ["基础快照已刷新"] = evidence.SnapshotRefreshed,
                            ["能力已刷新"] = evidence.CapabilitiesRefreshed
                        });
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            _report.Add(
                "真实断线与自动重连",
                AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                $"已观察到断线旧数据状态，但在恢复窗口内没有完成自动重连。{observer.Capture().MissingSummary()}。 ");
        }
        finally
        {
            _coordinator.StateChanged -= ObserveState;
        }
    }

    private async Task ReturnToLocalAsync()
    {
        if (_coordinator is null) return;
        try
        {
            _coordinator.StopReconnect();
            using var cancellation = new CancellationTokenSource(_options.OperationTimeout);
            HostSwitchResult result = await _coordinator.SwitchToLocalAsync(cancellation.Token);
            _report.Add(
                "清理并返回本机",
                result.Succeeded || result.Snapshot.ActiveSession.Target.IsLocal
                    ? AcceptanceStatus.Passed
                    : AcceptanceStatus.Failed,
                TimeSpan.Zero,
                result.Message,
                new Dictionary<string, object?>
                {
                    ["活动宿主为本机"] = result.Snapshot.ActiveSession.Target.IsLocal,
                    ["会话代次"] = result.Snapshot.ActiveSession.Generation
                });
        }
        catch (Exception ex)
        {
            _report.Add(
                "清理并返回本机",
                AcceptanceStatus.Failed,
                TimeSpan.Zero,
                $"返回本机会话失败：{AcceptanceReport.Safe(ex.Message)}");
        }
    }

    private VmInstance? ResolveVm(string selector)
    {
        if (Guid.TryParse(selector, out Guid id))
            return _virtualMachines.FirstOrDefault(vm => vm.Id == id);
        return _virtualMachines.FirstOrDefault(vm =>
            string.Equals(vm.Name, selector, StringComparison.Ordinal));
    }

    private void AddCoreSkips(string reason)
    {
        foreach (string stage in new[]
                 {
                     "真实远程虚拟机列表",
                     "TCP 2179 不可用时的管理降级",
                     "TCP 2179 控制台捕获",
                     "VM 生命周期写入",
                     "远程宿主配置",
                     "回滚复检",
                     "真实断线与自动重连"
                 })
        {
            _report.Add(stage, AcceptanceStatus.Skipped, TimeSpan.Zero, reason);
        }
    }

    private void AddPreNetworkSkips(string reason)
    {
        foreach (string stage in new[]
                 {
                     "两通道诊断",
                     "只读配置预检",
                     "原子激活与基础快照",
                     "两台受控宿主切换"
                 })
        {
            _report.Add(stage, AcceptanceStatus.Skipped, TimeSpan.Zero, reason);
        }
        AddCoreSkips(reason);
    }

    private void AddRollbackSkip(string reason) =>
        _report.Add("回滚复检", AcceptanceStatus.Skipped, TimeSpan.Zero, reason);

    private static bool Equivalent(
        HostPreflightFacts expected,
        HostPreflightFacts actual,
        out string difference)
    {
        if (expected.TokenFilterPolicy != actual.TokenFilterPolicy)
            return Different("LocalAccountTokenFilterPolicy 未恢复到配置前状态。 ", out difference);
        foreach (HostLocalGroupKind kind in Enum.GetValues<HostLocalGroupKind>())
        {
            string[] expectedMembers = expected.LocalGroups.TryGetValue(kind, out HostLocalGroupSnapshot? left)
                ? left.Members.Order(StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
            string[] actualMembers = actual.LocalGroups.TryGetValue(kind, out HostLocalGroupSnapshot? right)
                ? right.Members.Order(StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
            if (!expectedMembers.SequenceEqual(actualMembers, StringComparer.OrdinalIgnoreCase))
                return Different($"{kind} 成员未恢复到配置前状态。 ", out difference);
        }

        var expectedNetworks = expected.Networks.ToDictionary(network => network.InterfaceIndex);
        var actualNetworks = actual.Networks.ToDictionary(network => network.InterfaceIndex);
        if (expectedNetworks.Count != actualNetworks.Count
            || expectedNetworks.Any(item =>
                !actualNetworks.TryGetValue(item.Key, out HostNetworkSnapshot? network)
                || network.Category != item.Value.Category))
            return Different("网络配置文件类别未恢复到配置前状态。 ", out difference);

        if (!FirewallEquivalent(expected.Firewall, actual.Firewall))
            return Different("防火墙状态未恢复到配置前状态。 ", out difference);

        difference = string.Empty;
        return true;
    }

    private static bool FirewallEquivalent(HostFirewallSnapshot? left, HostFirewallSnapshot? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.WmiBuiltInRulesEnabled == right.WmiBuiltInRulesEnabled
               && left.HyperVBuiltInRulesEnabled == right.HyperVBuiltInRulesEnabled
               && left.ExHyperVConsole2179RuleEnabled == right.ExHyperVConsole2179RuleEnabled
               && left.Console2179RuleExists == right.Console2179RuleExists
               && string.Equals(left.ExHyperVConsole2179Protocol, right.ExHyperVConsole2179Protocol, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.ExHyperVConsole2179Action, right.ExHyperVConsole2179Action, StringComparison.OrdinalIgnoreCase)
               && SetEqual(left.Console2179RemoteAddresses, right.Console2179RemoteAddresses)
               && SetEqual(left.Console2179LocalPorts, right.Console2179LocalPorts)
               && SetEqual(left.Console2179Profiles, right.Console2179Profiles)
               && SetEqual(left.WmiRuleNamesToEnable, right.WmiRuleNamesToEnable)
               && SetEqual(left.HyperVRuleNamesToEnable, right.HyperVRuleNamesToEnable);
    }

    private static bool SetEqual(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(right.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static bool Different(string message, out string difference)
    {
        difference = message;
        return false;
    }
}
