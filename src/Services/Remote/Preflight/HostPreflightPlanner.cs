namespace ExHyperV.Services.Remote.Preflight;

public static class HostPreflightPlanner
{
    public static HostPreflightPlanResult Build(
        HostPreflightReport report,
        HostPreflightSelection selection)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(selection);
        var errors = new List<string>();
        string accountName = selection.AccountName.Trim();
        HostLocalAccount? localAccount = null;

        if (report.HasReadFailures)
            errors.Add("预检存在读取失败，无法生成完整且可信的修改预览；请查看日志并重新检测。");
        if (report.Facts.Join is null)
            errors.Add("缺少工作组或域状态，无法判断条件策略。");
        foreach (HostLocalGroupKind group in Enum.GetValues<HostLocalGroupKind>())
        {
            if (!report.Facts.LocalGroups.ContainsKey(group))
                errors.Add($"缺少 {HostPreflightPipeline.GroupTitle(group)} 成员状态。");
        }
        if (report.Facts.Firewall is null)
            errors.Add("缺少防火墙规则状态，无法生成完整预览。");
        else
        {
            if (!report.Facts.Firewall.WmiBuiltInRulesDetected
                && report.Facts.Firewall.WmiRuleNamesToRestore.Count == 0)
                errors.Add("未检测到 Windows 内置 WMI 防火墙规则组，无法生成仅复用系统内置规则且可精确回滚的修改；请先在目标机恢复该内置规则组并重新检测。");
            if (!report.Facts.Firewall.HyperVBuiltInRulesDetected
                && report.Facts.Firewall.HyperVRuleNamesToRestore.Count == 0)
                errors.Add("未检测到 Windows 内置 Hyper-V 防火墙规则组，无法生成仅复用系统内置规则且可精确回滚的修改；请先在目标机恢复该内置规则组并重新检测。");
            if (!report.Facts.Firewall.WmiBuiltInRulesEnabled
                && report.Facts.Firewall.WmiRuleNamesToRestore.Count == 0
                && report.Facts.Firewall.WmiRuleNamesToEnable.Count == 0)
                errors.Add("Windows 内置 WMI 防火墙规则状态不完整，且没有可精确恢复或启用的规则，无法生成修改预览。");
            if (!report.Facts.Firewall.HyperVBuiltInRulesEnabled
                && report.Facts.Firewall.HyperVRuleNamesToRestore.Count == 0
                && report.Facts.Firewall.HyperVRuleNamesToEnable.Count == 0)
                errors.Add("Windows 内置 Hyper-V 防火墙规则状态不完整，且没有可精确恢复或启用的规则，无法生成修改预览。");
        }

        if (selection.AccountKind == HostPreflightAccountKind.Local)
        {
            localAccount = report.Facts.EnabledLocalAccounts.FirstOrDefault(account =>
                string.Equals(account.Name, accountName, StringComparison.OrdinalIgnoreCase));
            if (localAccount is null)
                errors.Add("请选择检测到的已启用本地账户。");
        }
        else if (!IsDomainAccount(accountName))
        {
            errors.Add("域账户必须使用 DOMAIN\\User 或 user@domain 的格式。");
        }

        uint[] selectedIndexes = selection.SelectedNetworkInterfaceIndexes.Distinct().ToArray();
        HostNetworkSnapshot[] selectedNetworks = report.Facts.Networks
            .Where(network => selectedIndexes.Contains(network.InterfaceIndex))
            .ToArray();
        if (selectedIndexes.Length == 0)
            errors.Add("请至少选择一个检测到的目标网络。");
        else if (selectedNetworks.Length != selectedIndexes.Length)
            errors.Add("选择的目标网络已不在最新检测结果中，请重新检测。");

        var normalizedCidrs = new List<string>();
        var seenCidrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string cidr in selection.AllowedIpv4Cidrs)
        {
            if (!Ipv4Cidr.TryNormalize(cidr, out string normalized, out string error))
            {
                errors.Add(error);
                continue;
            }
            if (!Ipv4Cidr.IsPrivate(normalized))
            {
                errors.Add($"IPv4 CIDR“{normalized}”不完全位于 RFC1918 私有地址范围内，不能用于 TCP 2179 入站规则。");
                continue;
            }
            if (!seenCidrs.Add(normalized))
            {
                errors.Add($"IPv4 CIDR“{normalized}”重复，请只保留一项。");
                continue;
            }
            normalizedCidrs.Add(normalized);
        }
        if (normalizedCidrs.Count == 0)
            errors.Add("请至少选择或输入一个允许访问的 IPv4 CIDR。");

        if (errors.Count > 0) return new(null, errors.AsReadOnly());

        var changes = new List<HostPreflightPlannedChange>();
        if (!IsMember(report, HostLocalGroupKind.HyperVAdministrators, accountName, selection.AccountKind))
        {
            changes.Add(new(
                HostPreflightChangeKind.AddHyperVAdministrators,
                "加入 Hyper-V Administrators",
                $"将 {accountName} 加入 Hyper-V Administrators；不会授予 Administrators。"));
        }
        if (!IsMember(report, HostLocalGroupKind.RemoteManagementUsers, accountName, selection.AccountKind))
        {
            changes.Add(new(
                HostPreflightChangeKind.AddRemoteManagementUsers,
                "加入 Remote Management Users",
                $"将 {accountName} 加入 Remote Management Users。"));
        }

        bool isLocalAdministrator = selection.AccountKind == HostPreflightAccountKind.Local
            && localAccount is not null
            && IsMember(report, HostLocalGroupKind.Administrators, accountName, selection.AccountKind);
        if (report.Facts.Join?.Kind == HostJoinKind.Workgroup
            && isLocalAdministrator
            && report.Facts.TokenFilterPolicy == HostTokenFilterPolicyState.Missing)
        {
            changes.Add(new(
                HostPreflightChangeKind.EnableLocalAccountTokenFilterPolicy,
                "设置远程本地账户令牌策略",
                "设置 LocalAccountTokenFilterPolicy=1；仅因当前为工作组、本地管理员且策略缺失而建议。"));
        }

        var makePrivate = selection.NetworksToMakePrivate.ToHashSet();
        foreach (HostNetworkSnapshot network in selectedNetworks)
        {
            if (network.Category == HostNetworkCategory.Public && makePrivate.Contains(network.InterfaceIndex))
            {
                changes.Add(new(
                    HostPreflightChangeKind.ChangeNetworkToPrivate,
                    $"将“{network.Name}”改为 Private",
                    $"网络接口 {network.InterfaceIndex} 当前为 Public；仅在最终确认后修改。"));
            }
        }

        if (report.Facts.Firewall is { } firewall)
        {
            AddFirewallChanges(
                changes,
                firewall.WmiBuiltInRulesEnabled,
                firewall.WmiRuleNamesToRestore,
                firewall.WmiRuleNamesToEnable,
                HostPreflightChangeKind.RestoreWmiFirewallRules,
                HostPreflightChangeKind.EnableWmiFirewallRules,
                "WMI");
            AddFirewallChanges(
                changes,
                firewall.HyperVBuiltInRulesEnabled,
                firewall.HyperVRuleNamesToRestore,
                firewall.HyperVRuleNamesToEnable,
                HostPreflightChangeKind.RestoreHyperVFirewallRules,
                HostPreflightChangeKind.EnableHyperVFirewallRules,
                "Hyper-V");
            string[] configuredCidrs = firewall.Console2179RemoteAddresses
                .Select(TryNormalizeFirewallAddress)
                .Where(value => value is not null)
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] requestedCidrs = normalizedCidrs
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            bool consoleRuleMatches = firewall.ExHyperVConsole2179RuleEnabled
                && firewall.Console2179EndpointMatches
                && configuredCidrs.SequenceEqual(requestedCidrs, StringComparer.OrdinalIgnoreCase);
            if (!consoleRuleMatches)
                changes.Add(new(
                    HostPreflightChangeKind.ConfigureConsole2179FirewallRule,
                    firewall.ExHyperVConsole2179RuleEnabled
                        ? "更新 ExHyperV TCP 2179 入站规则范围"
                        : "配置 ExHyperV TCP 2179 入站规则",
                    $"仅允许 {string.Join("、", normalizedCidrs)} 访问 TCP 2179。"));
        }

        return new(
            new HostPreflightPlan(
                selection.AccountKind,
                accountName,
                selectedNetworks,
                selection.NetworksToMakePrivate.Distinct().ToArray(),
                normalizedCidrs,
                changes.AsReadOnly()),
            Array.Empty<string>());
    }

    private static bool IsDomainAccount(string value) =>
        value.Contains('\\') && value.IndexOf('\\') is > 0 && value.IndexOf('\\') < value.Length - 1
        || value.Contains('@') && value.IndexOf('@') is > 0 && value.IndexOf('@') < value.Length - 1;

    private static void AddFirewallChanges(
        ICollection<HostPreflightPlannedChange> changes,
        bool enabled,
        IReadOnlyList<string> ruleNamesToRestore,
        IReadOnlyList<string> ruleNamesToEnable,
        HostPreflightChangeKind restoreKind,
        HostPreflightChangeKind enableKind,
        string groupName)
    {
        if (ruleNamesToRestore.Count > 0)
        {
            changes.Add(new(
                restoreKind,
                $"恢复 Windows 内置 {groupName} 防火墙规则",
                $"从 SystemDefaults 向 PersistentStore 精确复制 {ruleNamesToRestore.Count} 条入站规则：{string.Join("、", ruleNamesToRestore)}。"));
        }
        if (!enabled && ruleNamesToEnable.Count > 0)
        {
            changes.Add(new(
                enableKind,
                $"启用 Windows 内置 {groupName} 防火墙规则",
                $"仅启用 {ruleNamesToEnable.Count} 条已检测规则：{string.Join("、", ruleNamesToEnable)}；不修改动态 RPC 端口范围。"));
        }
    }

    private static string? TryNormalizeFirewallAddress(string value)
    {
        string address = value.Trim();
        if (string.Equals(address, "Any", StringComparison.OrdinalIgnoreCase)
            || string.Equals(address, "LocalSubnet", StringComparison.OrdinalIgnoreCase))
            return null;
        return Ipv4Cidr.TryNormalizeFirewallAddress(address, out string normalized) ? normalized : null;
    }

    private static bool IsMember(
        HostPreflightReport report,
        HostLocalGroupKind groupKind,
        string accountName,
        HostPreflightAccountKind accountKind)
    {
        if (!report.Facts.LocalGroups.TryGetValue(groupKind, out HostLocalGroupSnapshot? group)) return false;
        foreach (string member in group.Members)
        {
            if (string.Equals(member, accountName, StringComparison.OrdinalIgnoreCase)) return true;
            if (accountKind == HostPreflightAccountKind.Local
                && member.EndsWith($"\\{accountName}", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
