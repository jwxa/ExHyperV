using System.Diagnostics;
using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.Services.Remote.Preflight;

public sealed class HostPreflightPipeline(
    IHostIdentityResolver identityResolver,
    IHostPreflightReader reader,
    Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public async Task<HostPreflightReport> RunAsync(
        HostProfile profile,
        WindowsCredential? transientCredential = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        HostId hostId = HostId.FromProfile(profile);
        DateTimeOffset startedAt = _clock();
        var stopwatch = Stopwatch.StartNew();
        var findings = new List<HostPreflightFinding>();
        var logs = new List<HostPreflightLogEntry>();
        HostJoinSnapshot? join = null;
        IReadOnlyList<HostLocalAccount> accounts = Array.Empty<HostLocalAccount>();
        var groups = new Dictionary<HostLocalGroupKind, HostLocalGroupSnapshot>();
        HostTokenFilterPolicyState tokenPolicy = HostTokenFilterPolicyState.Unknown;
        IReadOnlyList<HostNetworkSnapshot> networks = Array.Empty<HostNetworkSnapshot>();
        HostFirewallSnapshot? firewall = null;

        Log(HostPreflightStage.Connection, HostPreflightLogLevel.Information,
            $"开始远程配置预检：{profile.DisplayName}（{profile.Address}）。本阶段只读取状态，不会修改远程主机。");

        IHostPreflightReadSession? session = null;
        try
        {
            ResolvedHostIdentity identity = identityResolver.Resolve(profile, transientCredential);
            Log(HostPreflightStage.Connection, HostPreflightLogLevel.Information,
                identity.UsesCurrentWindowsIdentity ? "使用当前 Windows 身份读取预检信息。" : $"使用身份 {identity.UserName} 读取预检信息；密码不会写入日志。");
            session = await reader.OpenAsync(profile.Address, identity, cancellationToken);
            findings.Add(new(HostPreflightStage.Connection, HostPreflightFindingStatus.Passed, "只读连接", "已建立远程只读检测会话。"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            const string message = "无法建立远程只读检测会话，未对远程主机执行任何修改。";
            Log(HostPreflightStage.Connection, HostPreflightLogLevel.Error, message, ex);
            findings.Add(new(HostPreflightStage.Connection, HostPreflightFindingStatus.Failed, "只读连接", message));
        }

        if (session is not null)
        {
            await using (session)
            {
                join = await ReadAsync(
                    HostPreflightStage.HostEnvironment,
                    "主机环境",
                    "读取工作组或域状态。",
                    token => session.ReadJoinAsync(token),
                    value => $"计算机 {value.ComputerName} 属于{(value.Kind == HostJoinKind.Domain ? "域" : "工作组")} {value.JoinName}。",
                    fallback: (HostJoinSnapshot?)null);

                accounts = await ReadAsync(
                    HostPreflightStage.LocalAccounts,
                    "已启用本地账户",
                    "读取已启用的本地账户。",
                    token => session.ReadEnabledLocalAccountsAsync(token),
                    value => $"检测到 {value.Count} 个已启用本地账户。",
                    fallback: Array.Empty<HostLocalAccount>());

                foreach (HostLocalGroupKind groupKind in Enum.GetValues<HostLocalGroupKind>())
                {
                    HostPreflightStage stage = GroupStage(groupKind);
                    HostLocalGroupSnapshot? group = await ReadAsync(
                        stage,
                        GroupTitle(groupKind),
                        $"读取 {GroupTitle(groupKind)} 成员。",
                        token => session.ReadLocalGroupAsync(groupKind, token),
                        value => $"{value.DisplayName} 当前有 {value.Members.Count} 个成员。",
                        fallback: (HostLocalGroupSnapshot?)null);
                    if (group is not null) groups[groupKind] = group;
                }

                tokenPolicy = await ReadAsync(
                    HostPreflightStage.TokenFilterPolicy,
                    "远程本地账户令牌策略",
                    "读取 LocalAccountTokenFilterPolicy。",
                    token => session.ReadTokenFilterPolicyAsync(token),
                    value => $"LocalAccountTokenFilterPolicy 状态：{TokenPolicyText(value)}。",
                    fallback: HostTokenFilterPolicyState.Unknown);

                networks = await ReadAsync(
                    HostPreflightStage.Networks,
                    "网络配置文件",
                    "读取网络配置文件与 IPv4 地址。",
                    token => session.ReadNetworksAsync(token),
                    value => NetworkEvidence(value),
                    fallback: Array.Empty<HostNetworkSnapshot>(),
                    attention: value => value.Count == 0 || value.Any(network => network.Category == HostNetworkCategory.Public));

                HostFirewallSnapshot readFirewall = await ReadAsync(
                    HostPreflightStage.Firewall,
                    "防火墙规则",
                    "读取 Windows 内置 WMI/Hyper-V 规则与 ExHyperV TCP 2179 规则。",
                    token => session.ReadFirewallAsync(token),
                    value => FirewallEvidence(value),
                    fallback: new HostFirewallSnapshot(false, false, false),
                    attention: value => !value.WmiBuiltInRulesEnabled || !value.HyperVBuiltInRulesEnabled || !value.ExHyperVConsole2179RuleEnabled);
                firewall = findings.Last(finding => finding.Stage == HostPreflightStage.Firewall).Status == HostPreflightFindingStatus.Failed
                    ? null
                    : readFirewall;
            }
        }

        stopwatch.Stop();
        Log(HostPreflightStage.Connection, HostPreflightLogLevel.Information,
            $"预检完成：读取失败 {findings.Count(finding => finding.Status == HostPreflightFindingStatus.Failed)} 项，需要关注 {findings.Count(finding => finding.Status == HostPreflightFindingStatus.Attention)} 项；未执行任何修改。");
        return new HostPreflightReport(
            profile.Id,
            profile.Address,
            startedAt,
            stopwatch.Elapsed,
            new HostPreflightFacts(join, accounts, groups, tokenPolicy, networks, firewall),
            findings.AsReadOnly(),
            logs.AsReadOnly());

        async Task<T> ReadAsync<T>(
            HostPreflightStage stage,
            string title,
            string startMessage,
            Func<CancellationToken, Task<T>> action,
            Func<T, string> evidence,
            T fallback,
            Func<T, bool>? attention = null)
        {
            Log(stage, HostPreflightLogLevel.Information, startMessage);
            try
            {
                T value = await action(cancellationToken);
                string message = evidence(value);
                bool needsAttention = attention?.Invoke(value) == true;
                findings.Add(new(stage, needsAttention ? HostPreflightFindingStatus.Attention : HostPreflightFindingStatus.Passed, title, message));
                Log(stage, needsAttention ? HostPreflightLogLevel.Warning : HostPreflightLogLevel.Information, message);
                return value;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string message = $"读取{title}失败：{SafeError(ex)}";
                findings.Add(new(stage, HostPreflightFindingStatus.Failed, title, message));
                Log(stage, HostPreflightLogLevel.Error, message, ex);
                return fallback;
            }
        }

        void Log(HostPreflightStage stage, HostPreflightLogLevel level, string message, Exception? exception = null)
        {
            logs.Add(new HostPreflightLogEntry(_clock(), stage, level, message));
            var context = new AppLogContext(
                Host: profile.Address,
                Properties: new Dictionary<string, object?> { ["预检阶段"] = stage.ToString() },
                HostId: hostId,
                ErrorCategory: exception is null ? "None" : "PreflightReadFailed");
            switch (level)
            {
                case HostPreflightLogLevel.Information:
                    AppLog.Information("配置预检", message, context);
                    break;
                case HostPreflightLogLevel.Warning:
                    AppLog.Warning("配置预检", message, context, exception);
                    break;
                case HostPreflightLogLevel.Error:
                    AppLog.Error("配置预检", message, context, exception);
                    break;
            }
        }
    }

    private static HostPreflightStage GroupStage(HostLocalGroupKind group) => group switch
    {
        HostLocalGroupKind.Administrators => HostPreflightStage.AdministratorsGroup,
        HostLocalGroupKind.HyperVAdministrators => HostPreflightStage.HyperVAdministratorsGroup,
        _ => HostPreflightStage.RemoteManagementUsersGroup
    };

    public static string GroupTitle(HostLocalGroupKind group) => group switch
    {
        HostLocalGroupKind.Administrators => "Administrators",
        HostLocalGroupKind.HyperVAdministrators => "Hyper-V Administrators",
        _ => "Remote Management Users"
    };

    private static string TokenPolicyText(HostTokenFilterPolicyState state) => state switch
    {
        HostTokenFilterPolicyState.Missing => "未设置",
        HostTokenFilterPolicyState.Enabled => "已设置为 1",
        HostTokenFilterPolicyState.Disabled => "已设置但未启用",
        _ => "无法确定"
    };

    private static string NetworkEvidence(IReadOnlyList<HostNetworkSnapshot> networks)
    {
        if (networks.Count == 0) return "未检测到带 IPv4 地址的活动网络。";
        return string.Join("；", networks.Select(network =>
            $"{network.Name}（接口索引 {network.InterfaceIndex}）：{NetworkCategoryText(network.Category)}，{string.Join("、", network.Ipv4Addresses.Select(address => address.Cidr))}"));
    }

    private static string NetworkCategoryText(HostNetworkCategory category) => category switch
    {
        HostNetworkCategory.Public => "Public",
        HostNetworkCategory.Private => "Private",
        HostNetworkCategory.DomainAuthenticated => "DomainAuthenticated",
        _ => "未知"
    };

    private static string FirewallEvidence(HostFirewallSnapshot value) =>
        $"WMI 内置规则：{BuiltInRuleText(value.WmiBuiltInRulesDetected, value.WmiBuiltInRulesEnabled, value.WmiRuleNamesToRestore.Count)}；Hyper-V 内置规则：{BuiltInRuleText(value.HyperVBuiltInRulesDetected, value.HyperVBuiltInRulesEnabled, value.HyperVRuleNamesToRestore.Count)}；ExHyperV TCP 2179：{EnabledText(value.ExHyperVConsole2179RuleEnabled)}{ConsoleScopeText(value)}。";

    private static string ConsoleScopeText(HostFirewallSnapshot value) =>
        value.ExHyperVConsole2179RuleEnabled
            ? $"，远程地址={string.Join("、", value.Console2179RemoteAddresses)}"
            : string.Empty;

    private static string EnabledText(bool enabled) => enabled ? "已启用" : "未启用";

    private static string BuiltInRuleText(bool detected, bool enabled, int restorableCount) =>
        restorableCount > 0
            ? $"缺少 {restorableCount} 条，可从 SystemDefaults 恢复"
            : detected ? EnabledText(enabled) : "未检测到且无法从 SystemDefaults 恢复";

    private static string SafeError(Exception exception) =>
        SensitiveDataRedactor.Redact(exception.Message);
}
