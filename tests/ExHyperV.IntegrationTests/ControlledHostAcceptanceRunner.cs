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
    private HostSessionRegistry? _sessionRegistry;
    private HostOperationRouter? _vmOperations;
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
                await ConnectPrimaryHostAsync(diagnostic, cancellationToken);
                HostId primaryHostId = HostId.FromProfile(_profile);
                if (_sessionRegistry?.Current.TryGet(primaryHostId, out _) == true)
                {
                    bool primaryRemained = await RunTwoHostCoexistenceAsync(cancellationToken);
                    if (primaryRemained)
                    {
                        await QueryVirtualMachinesAsync(cancellationToken);
                        VerifyPartialAvailability(diagnostic);
                        CaptureConsoleTarget(diagnostic);
                        await RunVmWriteAsync(cancellationToken);
                        await RunConfigurationAndRollbackAsync(preflight, cancellationToken);
                        await RunDisconnectAndReconnectAsync(cancellationToken);
                        DisconnectPrimaryHost();
                    }
                    else
                    {
                        AddCoreSkips("两台远程宿主并行验收后第一宿主不再可用，停止依赖第一宿主的后续验收。 ");
                    }
                }
            }
            else
            {
                _report.Add(
                    "连接与基础快照",
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
            CleanupRemoteSessions();
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
        bool managementReady = diagnostic.ManagementAvailable;
        _report.Add(
            stageName,
            managementReady ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            managementReady
                ? diagnostic.ConsoleAvailable
                    ? "WMI/DCOM 与 TCP 2179 均可用。 "
                    : "WMI/DCOM 可用；TCP 2179 不可用，已记录为受支持的管理可用/控制台不可用降级。 "
                : "受控宿主的 WMI/DCOM 管理通道不可用。 ",
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

    private async Task ConnectPrimaryHostAsync(
        HostDiagnosticReport diagnostic,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var connector = new WindowsHostSessionConnector(
            _identityResolver,
            _options.OperationTimeout,
            new WindowsTcpPortProbe(_options.OperationTimeout));
        _sessionRegistry = new HostSessionRegistry(connector, new WindowsHostBasicSnapshotLoader());
        _vmOperations = new HostOperationRouter(_sessionRegistry, new HostWmiContextResolver());
        HostId hostId = HostId.FromProfile(_profile);
        HostConnectResult result = await _sessionRegistry.ConnectAsync(
            HostConnectRequest.ForConfirmedDiagnostic(
                _profile,
                diagnostic.ConsoleAvailable,
                _transientCredential),
            cancellationToken);
        HostSessionSnapshot? session = result.Snapshot.TryGet(hostId, out HostSessionSnapshot? connected)
            ? connected
            : null;
        HostBasicSnapshot? snapshot = session?.BasicSnapshot;
        _report.Add(
            "连接与基础快照",
            result.Succeeded ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            result.Message,
            new Dictionary<string, object?>
            {
                ["连接状态"] = result.Status.ToString(),
                ["本机始终存在"] = result.Snapshot.TryGet(HostId.Local, out _),
                ["远程宿主"] = session?.Target.Address,
                ["会话代次"] = session?.Generation,
                ["计算机名"] = snapshot?.ComputerName,
                ["操作系统"] = snapshot?.OperatingSystem,
                ["虚拟机数量"] = snapshot?.VirtualMachineCount
            });
    }

    private async Task<bool> RunTwoHostCoexistenceAsync(CancellationToken cancellationToken)
    {
        HostId primaryHostId = HostId.FromProfile(_profile);
        if (_secondProfile is null)
        {
            bool localAndPrimary = _sessionRegistry!.Current.TryGet(HostId.Local, out _)
                                   && _sessionRegistry.Current.TryGet(primaryHostId, out _);
            _report.Add(
                "两台远程宿主并行",
                AcceptanceStatus.Skipped,
                TimeSpan.Zero,
                localAndPrimary
                    ? "本机与第一远程宿主已并行；未设置 EXHYPERV_INTEGRATION_SECOND_HOST，第二远程宿主场景由确定性测试覆盖。 "
                    : "本机与第一远程宿主未同时保留。 ");
            return localAndPrimary;
        }

        var stopwatch = Stopwatch.StartNew();
        HostDiagnosticReport secondDiagnostic = await RunDiagnosticsAsync(
            _secondProfile,
            "第二受控宿主两通道诊断",
            cancellationToken);
        if (!secondDiagnostic.ManagementAvailable)
        {
            _report.Add(
                "两台远程宿主并行",
                AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                "第二受控宿主的 WMI/DCOM 管理通道不可用；第一宿主保持连接。 ");
            return _sessionRegistry!.Current.TryGet(primaryHostId, out _);
        }

        HostSessionSnapshot primaryBefore = _sessionRegistry!.Current.GetRequired(primaryHostId);
        HostId secondHostId = HostId.FromProfile(_secondProfile);
        HostConnectResult secondConnect = await _sessionRegistry.ConnectAsync(
            HostConnectRequest.ForConfirmedDiagnostic(
                _secondProfile,
                secondDiagnostic.ConsoleAvailable,
                _transientCredential),
            cancellationToken);
        HostVmReadResult<List<VmInstance>> secondVmRead = secondConnect.Succeeded
            ? await _vmOperations!.ReadAsync(
                secondHostId,
                (context, token) => new VmQueryService().GetVmListAsync(context, token),
                cancellationToken)
            : new HostVmReadResult<List<VmInstance>>(
                HostVmOperationStatus.Failed,
                null,
                secondConnect.Message,
                null);

        HostRegistrySnapshot concurrent = _sessionRegistry.Current;
        bool localPresent = concurrent.TryGet(HostId.Local, out _);
        bool primaryPresent = concurrent.TryGet(primaryHostId, out HostSessionSnapshot? primaryDuring);
        bool secondPresent = concurrent.TryGet(secondHostId, out HostSessionSnapshot? secondDuring);
        bool threeHosts = localPresent && primaryPresent && secondPresent;
        bool primaryUnchanged = primaryDuring?.Generation == primaryBefore.Generation;

        HostDisconnectResult? secondDisconnect = null;
        if (_sessionRegistry.TryPrepareDisconnect(
                secondHostId,
                out IHostDisconnectPreparation? preparation,
                out _))
        {
            using (preparation)
                secondDisconnect = preparation!.Commit();
        }
        bool primaryRemains = _sessionRegistry.Current.TryGet(HostId.Local, out _)
                              && _sessionRegistry.Current.TryGet(primaryHostId, out _)
                              && !_sessionRegistry.Current.TryGet(secondHostId, out _);
        bool complete = secondConnect.Succeeded
                        && threeHosts
                        && primaryUnchanged
                        && secondVmRead.Succeeded
                        && secondDisconnect?.Succeeded == true
                        && primaryRemains;
        _report.Add(
            "两台远程宿主并行",
            complete ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            complete
                ? "本机、第一远程宿主和第二远程宿主同时存在；第二宿主 VM 读取与断开未改变第一宿主。 "
                : "两台远程宿主的并行、读取、隔离断开证据不完整。 ",
            new Dictionary<string, object?>
            {
                ["第一宿主"] = _profile.Address,
                ["第一宿主代次未变"] = primaryUnchanged,
                ["第二宿主"] = _secondProfile.Address,
                ["第二宿主连接状态"] = secondConnect.Status.ToString(),
                ["第二宿主代次"] = secondDuring?.Generation,
                ["第二宿主快照计算机名"] = secondDuring?.BasicSnapshot?.ComputerName,
                ["第二宿主VM读取状态"] = secondVmRead.Status.ToString(),
                ["第二宿主VM数量"] = secondVmRead.Value?.Count,
                ["三宿主同时存在"] = threeHosts,
                ["第二宿主断开状态"] = secondDisconnect?.Status.ToString(),
                ["断开后本机和第一宿主保留"] = primaryRemains
            });
        return primaryRemains;
    }

    private async Task QueryVirtualMachinesAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        HostId hostId = HostId.FromProfile(_profile);
        HostVmReadResult<List<VmInstance>> result = await _vmOperations!.ReadAsync(
            hostId,
            (context, token) => new VmQueryService().GetVmListAsync(context, token),
            cancellationToken);
        _virtualMachines = result.Value ?? [];
        _vmReadSucceeded = result.Succeeded;
        _report.Add(
            "真实远程虚拟机列表",
            result.Succeeded ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            result.Succeeded ? $"通过指定远程宿主读取到 {_virtualMachines.Count} 台虚拟机。 " : result.Message,
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

        HostId hostId = HostId.FromProfile(_profile);
        var sessions = new HostConsoleSessions(_sessionRegistry!);
        HostConsoleSessionCapture capture = sessions.Capture(
            hostId,
            "baf3c735-4a86-4bb6-85dc-c0b829ce72f6",
            "TCP 2179 能力门控探针");
        PartialAvailabilityEvidence evidence = PartialAvailabilityAcceptance.Evaluate(
            _sessionRegistry!.Current.GetRequired(hostId),
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

        HostId hostId = HostId.FromProfile(_profile);
        var sessions = new HostConsoleSessions(_sessionRegistry!);
        HostConsoleSessionCapture capture = sessions.Capture(hostId, vm.Id.ToString("D"), vm.Name);
        bool valid = capture.Succeeded
                     && capture.Session?.Server == _profile.Address
                     && capture.Session.Port == HostConsoleSessions.ConsolePort
                     && sessions.IsCurrent(capture.Session);
        _report.Add(
            "TCP 2179 控制台捕获",
            valid ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
            stopwatch.Elapsed,
            valid ? "控制台会话绑定到所属宿主、会话代次和 TCP 2179。 " : capture.Message,
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

        HostId hostId = HostId.FromProfile(_profile);
        HostOperationStamp stamp = _sessionRegistry!.CaptureOperationStamp(hostId);
        HostVmWriteResult result = await _vmOperations!.WriteAsync(
            hostId,
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

        HostId hostId = HostId.FromProfile(_profile);
        HostSessionSnapshot initialSnapshot = _sessionRegistry!.Current.GetRequired(hostId);
        long generation = initialSnapshot.Generation;
        HostBasicSnapshot? originalSnapshot = initialSnapshot.BasicSnapshot;
        var observer = new DisconnectAcceptanceObserver(
            _profile.Id,
            generation,
            originalSnapshot?.RefreshedAt ?? DateTimeOffset.MinValue);
        DateTimeOffset detectDeadline = DateTimeOffset.UtcNow + _options.OutageDetectionTimeout;
        void ObserveState(object? sender, HostRegistryChangedEventArgs change)
        {
            if (change.ChangedHostId == hostId
                && change.Current.TryGet(hostId, out HostSessionSnapshot? snapshot))
                observer.Observe(snapshot!, DateTimeOffset.UtcNow);
        }
        _sessionRegistry.Changed += ObserveState;
        try
        {
            observer.Observe(_sessionRegistry.Current.GetRequired(hostId), DateTimeOffset.UtcNow);
            while (DateTimeOffset.UtcNow < detectDeadline && !observer.Capture().StaleDataObserved)
            {
                _ = await _vmOperations!.ReadAsync(
                    hostId,
                    (context, token) => new VmQueryService().GetVmListAsync(context, token),
                    cancellationToken);
                observer.Observe(_sessionRegistry.Current.GetRequired(hostId), DateTimeOffset.UtcNow);
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

            bool writeBlocked = !_sessionRegistry.TryBeginWrite(
                hostId,
                out IHostWriteLease? rejectedLease,
                out string writeBlockReason);
            rejectedLease?.Dispose();
            observer.RecordWriteGate(writeBlocked, writeBlockReason);

            DateTimeOffset reconnectDeadline = DateTimeOffset.UtcNow + _options.ReconnectTimeout;
            bool restorePromptShown = false;
            while (DateTimeOffset.UtcNow < reconnectDeadline)
            {
                observer.Observe(_sessionRegistry.Current.GetRequired(hostId), DateTimeOffset.UtcNow);
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
                            ["恢复后会话代次"] = _sessionRegistry.Current.GetRequired(hostId).Generation,
                            ["观察到旧数据"] = evidence.StaleDataObserved,
                            ["旧数据期间写入被拒绝"] = evidence.WriteBlockedWhileStale,
                            ["写入拒绝原因"] = evidence.WriteBlockReason,
                            ["目标远程宿主始终保留"] = evidence.StayedOnExpectedRemoteHost,
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
            _sessionRegistry.Changed -= ObserveState;
        }
    }

    private void DisconnectPrimaryHost()
    {
        if (_sessionRegistry is null) return;
        HostId hostId = HostId.FromProfile(_profile);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _sessionRegistry.StopReconnect(hostId);
            bool prepared = _sessionRegistry.TryPrepareDisconnect(
                hostId,
                out IHostDisconnectPreparation? preparation,
                out string reason);
            HostDisconnectResult? result = null;
            if (prepared)
            {
                using (preparation)
                    result = preparation!.Commit();
            }
            bool localRemains = _sessionRegistry.Current.TryGet(HostId.Local, out _);
            bool remoteRemoved = !_sessionRegistry.Current.TryGet(hostId, out _);
            bool succeeded = result?.Succeeded == true && localRemains && remoteRemoved;
            _report.Add(
                "主动断开第一远程宿主",
                succeeded ? AcceptanceStatus.Passed : AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                result?.Message ?? reason,
                new Dictionary<string, object?>
                {
                    ["断开状态"] = result?.Status.ToString(),
                    ["本机仍保留"] = localRemains,
                    ["目标远程宿主已移除"] = remoteRemoved
                });
        }
        catch (Exception ex)
        {
            _report.Add(
                "主动断开第一远程宿主",
                AcceptanceStatus.Failed,
                stopwatch.Elapsed,
                $"断开远程宿主失败：{AcceptanceReport.Safe(ex.Message)}");
        }
    }

    private void CleanupRemoteSessions()
    {
        if (_sessionRegistry is null) return;
        foreach (HostId hostId in _sessionRegistry.Current.Hosts
                     .Where(session => !session.HostId.IsLocal)
                     .Select(session => session.HostId)
                     .ToArray())
        {
            _sessionRegistry.StopReconnect(hostId);
            if (!_sessionRegistry.TryPrepareDisconnect(hostId, out IHostDisconnectPreparation? preparation, out _))
                continue;
            using (preparation)
                _ = preparation!.Commit();
        }
        _sessionRegistry.Shutdown();
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
