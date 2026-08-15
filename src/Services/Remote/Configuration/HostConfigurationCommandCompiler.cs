using System.Globalization;
using ExHyperV.Services.Remote.Preflight;

namespace ExHyperV.Services.Remote.Configuration;

public static class HostConfigurationCommandCompiler
{
    private const string RollbackRequiredMarker = "EXHYPERV_ROLLBACK_REQUIRED";
    private const string ConsoleRuleName = "ExHyperV Console (TCP 2179)";
    private const string TokenPolicyPath = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public static IReadOnlyList<HostConfigurationCommand> Compile(
        HostPreflightReport report,
        HostPreflightPlan plan)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(plan);
        HostFirewallSnapshot firewall = report.Facts.Firewall
            ?? throw new InvalidOperationException("缺少防火墙原始状态，无法生成可回滚命令。");
        ValidateAllowedCidrs(plan.AllowedIpv4Cidrs);
        var commands = new List<HostConfigurationCommand>();
        foreach (HostPreflightPlannedChange change in plan.Changes)
        {
            commands.Add(change.Kind switch
            {
                HostPreflightChangeKind.AddHyperVAdministrators => AddGroupMember(change, "S-1-5-32-578", plan.AccountName, plan.AccountKind),
                HostPreflightChangeKind.AddRemoteManagementUsers => AddGroupMember(change, "S-1-5-32-580", plan.AccountName, plan.AccountKind),
                HostPreflightChangeKind.EnableLocalAccountTokenFilterPolicy => TokenPolicy(change),
                HostPreflightChangeKind.ChangeNetworkToPrivate => NetworkToPrivate(change, plan),
                HostPreflightChangeKind.RestoreWmiFirewallRules => RestoreFirewallRules(
                    change,
                    firewall.WmiRuleNamesToRestore,
                    WindowsFirewallRuleClassifier.WmiResourceGroup),
                HostPreflightChangeKind.RestoreHyperVFirewallRules => RestoreFirewallRules(
                    change,
                    firewall.HyperVRuleNamesToRestore,
                    WindowsFirewallRuleClassifier.HyperVManagementResourceGroup),
                HostPreflightChangeKind.EnableWmiFirewallRules => EnableFirewallRules(
                    change,
                    firewall.WmiRuleNamesToEnable,
                    WindowsFirewallRuleClassifier.WmiResourceGroup),
                HostPreflightChangeKind.EnableHyperVFirewallRules => EnableFirewallRules(
                    change,
                    firewall.HyperVRuleNamesToEnable,
                    WindowsFirewallRuleClassifier.HyperVManagementResourceGroup),
                HostPreflightChangeKind.ConfigureConsole2179FirewallRule => ConfigureConsoleRule(change, firewall, plan.AllowedIpv4Cidrs),
                _ => throw new InvalidOperationException($"不支持的配置修改：{change.Kind}。")
            });
        }
        return commands.AsReadOnly();
    }

    private static void ValidateAllowedCidrs(IReadOnlyList<string> cidrs)
    {
        if (cidrs.Count == 0)
            throw new InvalidOperationException("TCP 2179 入站规则至少需要一个私有 IPv4 CIDR。");

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string cidr in cidrs)
        {
            if (!Ipv4Cidr.TryNormalize(cidr, out string value, out string error))
                throw new InvalidOperationException(error);
            if (!Ipv4Cidr.IsPrivate(value))
                throw new InvalidOperationException($"IPv4 CIDR“{value}”不完全位于 RFC1918 私有地址范围内。");
            if (!normalized.Add(value))
                throw new InvalidOperationException($"IPv4 CIDR“{value}”重复。");
        }
    }

    private static HostConfigurationCommand AddGroupMember(
        HostPreflightPlannedChange change,
        string groupSid,
        string accountName,
        HostPreflightAccountKind accountKind)
    {
        string account = Quote(accountName);
        string sid = Quote(groupSid);
        string resolve = $"$group = Get-LocalGroup -SID {sid} -ErrorAction Stop";
        string memberCheck = accountKind == HostPreflightAccountKind.Local
            ? $"@(Get-LocalGroupMember -Group $group -ErrorAction Stop | Where-Object {{ $_.Name -ieq {account} -or $_.Name.EndsWith(('\\' + {account}), [StringComparison]::OrdinalIgnoreCase) }}).Count -gt 0"
            : $"@(Get-LocalGroupMember -Group $group -ErrorAction Stop | Where-Object {{ $_.Name -ieq {account} }}).Count -gt 0";
        return new HostConfigurationCommand(
            change.Kind,
            change.Title,
            $"{resolve}; if ({memberCheck}) {{ throw '账户已在目标组中，预检结果已过期。' }}; Add-LocalGroupMember -Group $group -Member {account} -ErrorAction Stop",
            $"{resolve}; if ({memberCheck}) {{ Remove-LocalGroupMember -Group $group -Member {account} -ErrorAction Stop }}");
    }

    private static HostConfigurationCommand TokenPolicy(HostPreflightPlannedChange change) => new(
        change.Kind,
        change.Title,
        $"if ((Get-ItemProperty -LiteralPath {Quote(TokenPolicyPath)} -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy -ne $null) {{ throw '令牌策略已存在，预检结果已过期。' }}; New-Item -Path {Quote(TokenPolicyPath)} -Force | Out-Null; New-ItemProperty -LiteralPath {Quote(TokenPolicyPath)} -Name LocalAccountTokenFilterPolicy -PropertyType DWord -Value 1 -Force | Out-Null",
        $"$value = (Get-ItemProperty -LiteralPath {Quote(TokenPolicyPath)} -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy; if ($null -eq $value) {{ return }}; if ($value -ne 1) {{ throw 'LocalAccountTokenFilterPolicy 已被其他操作修改，未覆盖当前值。' }}; Remove-ItemProperty -LiteralPath {Quote(TokenPolicyPath)} -Name LocalAccountTokenFilterPolicy -Force -ErrorAction Stop");

    private static HostConfigurationCommand NetworkToPrivate(
        HostPreflightPlannedChange change,
        HostPreflightPlan plan)
    {
        HostNetworkSnapshot network = plan.SelectedNetworks.Single(item =>
            plan.NetworksToMakePrivate.Contains(item.InterfaceIndex)
            && string.Equals(change.Title, $"将“{item.Name}”改为 Private", StringComparison.Ordinal));
        string index = network.InterfaceIndex.ToString(CultureInfo.InvariantCulture);
        return new(
            change.Kind,
            change.Title,
            $"$profile = Get-NetConnectionProfile -InterfaceIndex {index} -ErrorAction Stop; if ($profile.NetworkCategory -ne 'Public') {{ throw '网络配置文件已不是 Public，预检结果已过期。' }}; Set-NetConnectionProfile -InterfaceIndex {index} -NetworkCategory Private -ErrorAction Stop",
            $"$profile = Get-NetConnectionProfile -InterfaceIndex {index} -ErrorAction SilentlyContinue; if ($null -eq $profile) {{ throw '目标网络配置文件已不存在，无法安全回滚。' }}; if ($profile.NetworkCategory -eq 'Public') {{ return }}; if ($profile.NetworkCategory -ne 'Private') {{ throw '目标网络配置文件已被其他操作修改，未覆盖当前类别。' }}; Set-NetConnectionProfile -InterfaceIndex {index} -NetworkCategory Public -ErrorAction Stop");
    }

    private static HostConfigurationCommand EnableFirewallRules(
        HostPreflightPlannedChange change,
        IReadOnlyList<string> ruleNames,
        string expectedGroup)
    {
        if (ruleNames.Count == 0)
            throw new InvalidOperationException($"{change.Title}没有检测到可精确恢复的禁用规则，拒绝生成命令。");
        string names = ArrayLiteral(ruleNames);
        string group = Quote(expectedGroup);
        string ownership = FirewallOwnershipCheck("$rule", group);
        string pipelineOwnership = FirewallOwnershipCheck("$_", group);
        string compensate = $"$names | ForEach-Object {{ $rule = Get-NetFirewallRule -Name $_ -ErrorAction SilentlyContinue; if ($null -eq $rule -or $rule.Enabled -ne 'True') {{ return }}; if (-not ({ownership})) {{ throw '防火墙规则所有权已变化，未执行失败补偿。' }}; $rule | Disable-NetFirewallRule -ErrorAction Stop }}";
        string rollback = $"$names | ForEach-Object {{ $rule = Get-NetFirewallRule -Name $_ -ErrorAction SilentlyContinue; if ($null -eq $rule) {{ throw ('防火墙规则已不存在，无法安全回滚：' + $_) }}; if (-not ({ownership})) {{ throw ('防火墙规则所有权已变化，未修改当前规则：' + $_) }}; if ($rule.Enabled -eq 'False') {{ return }}; if ($rule.Enabled -ne 'True') {{ throw ('防火墙规则状态已被其他操作修改，未覆盖当前状态：' + $_) }}; $rule | Disable-NetFirewallRule -ErrorAction Stop }}";
        return new(
            change.Kind,
            change.Title,
            $"$names = {names}; $rules = @($names | ForEach-Object {{ Get-NetFirewallRule -Name $_ -ErrorAction Stop }}); if (@($rules | Where-Object {{ -not ({pipelineOwnership}) -or $_.Enabled -ne 'False' }}).Count -gt 0) {{ throw '防火墙规则状态或所有权已变化，预检结果已过期。' }}; try {{ $rules | Enable-NetFirewallRule -ErrorAction Stop }} catch {{ $originalError = $_; try {{ {compensate} }} catch {{ throw '{RollbackRequiredMarker}' }}; throw $originalError }}",
            $"$names = {names}; {rollback}");
    }

    private static HostConfigurationCommand RestoreFirewallRules(
        HostPreflightPlannedChange change,
        IReadOnlyList<string> ruleNames,
        string expectedGroup)
    {
        ValidateFirewallRuleNames(change, ruleNames);
        string names = ArrayLiteral(ruleNames);
        string group = Quote(expectedGroup);
        string helpers = FirewallRestoreHelpers();
        string apply =
            $"{helpers} $names = {names}; $copied = [Collections.Generic.List[string]]::new(); " +
            "try { foreach ($name in $names) { " +
            "$active = Get-ExHyperVExactFirewallRule -PolicyStore 'ActiveStore' -Name $name -Required $false; " +
            "if ($null -ne $active) { throw ('防火墙规则已出现在 ActiveStore，预检结果已过期：' + $name) }; " +
            "$source = Get-ExHyperVExactFirewallRule -PolicyStore 'SystemDefaults' -Name $name -Required $true; " +
            $"if (-not (Test-ExHyperVFirewallSource -Rule $source -ExpectedGroup {group})) {{ throw ('SystemDefaults 防火墙规则状态或组已变化，预检结果已过期：' + $name) }}; " +
            "$source | Copy-NetFirewallRule -NewPolicyStore PersistentStore -ErrorAction Stop | Out-Null; " +
            "[void]$copied.Add($name); " +
            "$target = Get-ExHyperVExactFirewallRule -PolicyStore 'PersistentStore' -Name $name -Required $true; " +
            $"if (-not (Test-ExHyperVCopiedFirewallRule -Source $source -Target $target -ExpectedGroup {group})) {{ throw ('复制后的防火墙规则未通过精确校验：' + $name) }} " +
            $"}} }} catch {{ $originalError = $_; try {{ for ($index = $copied.Count - 1; $index -ge 0; $index--) {{ Remove-ExHyperVRestoredFirewallRule -Name $copied[$index] -ExpectedGroup {group} }} }} catch {{ throw '{RollbackRequiredMarker}' }}; throw $originalError }}";
        string rollback =
            $"{helpers} $names = {names}; $targets = [Collections.Generic.List[object]]::new(); " +
            $"foreach ($name in $names) {{ $target = Get-ExHyperVRestoredFirewallRuleForRemoval -Name $name -ExpectedGroup {group}; " +
            "if ($null -ne $target) { [void]$targets.Add($target) } }; " +
            "for ($index = $targets.Count - 1; $index -ge 0; $index--) { $targets[$index] | Remove-NetFirewallRule -ErrorAction Stop }";
        return new(change.Kind, change.Title, apply, rollback);
    }

    private static void ValidateFirewallRuleNames(
        HostPreflightPlannedChange change,
        IReadOnlyList<string> ruleNames)
    {
        if (ruleNames.Count == 0)
            throw new InvalidOperationException($"{change.Title}没有可精确恢复的规则名称，拒绝生成命令。");

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in ruleNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException($"{change.Title}包含空规则名称，拒绝生成命令。");
            if (name.IndexOfAny(['*', '?', '[', ']']) >= 0)
                throw new InvalidOperationException($"防火墙规则名称“{name}”包含通配符，拒绝生成命令。");
            if (!unique.Add(name))
                throw new InvalidOperationException($"防火墙规则名称“{name}”重复，拒绝生成命令。");
        }
    }

    private static string FirewallRestoreHelpers() =>
        "function ConvertTo-ExHyperVCanonicalValue { param([object]$Value) " +
        "return (@($Value | ForEach-Object { if ($null -eq $_) { '<null>' } else { $_.ToString() } } | Sort-Object) -join ',') }; " +
        "function Get-ExHyperVExactFirewallRule { param([string]$PolicyStore,[string]$Name,[bool]$Required) " +
        "$matches = @(Get-NetFirewallRule -PolicyStore $PolicyStore -Name $Name -ErrorAction SilentlyContinue | Where-Object { $_.Name.ToString() -ceq $Name }); " +
        "if ($matches.Count -gt 1 -or ($Required -and $matches.Count -ne 1)) { throw ('无法在 ' + $PolicyStore + ' 中唯一定位防火墙规则：' + $Name) }; " +
        "if ($matches.Count -eq 0) { return $null }; return $matches[0] }; " +
        "function Get-ExHyperVFirewallSignature { param([object]$Rule) " +
        "$address = $Rule | Get-NetFirewallAddressFilter -ErrorAction Stop; " +
        "$port = $Rule | Get-NetFirewallPortFilter -ErrorAction Stop; " +
        "$application = $Rule | Get-NetFirewallApplicationFilter -ErrorAction Stop; " +
        "$service = $Rule | Get-NetFirewallServiceFilter -ErrorAction Stop; " +
        "$interface = $Rule | Get-NetFirewallInterfaceFilter -ErrorAction Stop; " +
        "$interfaceType = $Rule | Get-NetFirewallInterfaceTypeFilter -ErrorAction Stop; " +
        "$security = $Rule | Get-NetFirewallSecurityFilter -ErrorAction Stop; " +
        "$values = @( " +
        "(ConvertTo-ExHyperVCanonicalValue $Rule.Name), (ConvertTo-ExHyperVCanonicalValue $Rule.Group), " +
        "(ConvertTo-ExHyperVCanonicalValue $Rule.Direction), (ConvertTo-ExHyperVCanonicalValue $Rule.Action), " +
        "(ConvertTo-ExHyperVCanonicalValue $Rule.Enabled), (ConvertTo-ExHyperVCanonicalValue $Rule.Profile), " +
        "(ConvertTo-ExHyperVCanonicalValue $Rule.EdgeTraversalPolicy), (ConvertTo-ExHyperVCanonicalValue $Rule.LooseSourceMapping), " +
        "(ConvertTo-ExHyperVCanonicalValue $Rule.LocalOnlyMapping), (ConvertTo-ExHyperVCanonicalValue $Rule.Owner), " +
        "(ConvertTo-ExHyperVCanonicalValue $address.LocalAddress), (ConvertTo-ExHyperVCanonicalValue $address.RemoteAddress), " +
        "(ConvertTo-ExHyperVCanonicalValue $port.Protocol), (ConvertTo-ExHyperVCanonicalValue $port.LocalPort), " +
        "(ConvertTo-ExHyperVCanonicalValue $port.RemotePort), (ConvertTo-ExHyperVCanonicalValue $port.IcmpType), " +
        "(ConvertTo-ExHyperVCanonicalValue $port.DynamicTarget), (ConvertTo-ExHyperVCanonicalValue $application.Program), " +
        "(ConvertTo-ExHyperVCanonicalValue $application.Package), (ConvertTo-ExHyperVCanonicalValue $service.Service), " +
        "(ConvertTo-ExHyperVCanonicalValue $interface.InterfaceAlias), (ConvertTo-ExHyperVCanonicalValue $interfaceType.InterfaceType), " +
        "(ConvertTo-ExHyperVCanonicalValue $security.Authentication), (ConvertTo-ExHyperVCanonicalValue $security.Encryption), " +
        "(ConvertTo-ExHyperVCanonicalValue $security.OverrideBlockRules), (ConvertTo-ExHyperVCanonicalValue $security.LocalUser), " +
        "(ConvertTo-ExHyperVCanonicalValue $security.RemoteUser), (ConvertTo-ExHyperVCanonicalValue $security.RemoteMachine)); " +
        "return ($values -join [char]31) }; " +
        "function Test-ExHyperVFirewallSource { param([object]$Rule,[string]$ExpectedGroup) " +
        "return ($Rule.Name -and $Rule.Group.ToString() -ceq $ExpectedGroup -and $Rule.Direction.ToString() -ceq 'Inbound') }; " +
        "function Test-ExHyperVCopiedFirewallRule { param([object]$Source,[object]$Target,[string]$ExpectedGroup) " +
        "$owned = $Target.Direction.ToString() -ceq 'Inbound' -and $Target.Group.ToString() -ceq $ExpectedGroup " +
        "-and $Target.PolicyStoreSourceType.ToString() -ceq 'Local' -and $Target.PolicyStoreSource.ToString() -ceq 'PersistentStore'; " +
        "return ($owned -and (Get-ExHyperVFirewallSignature $Source) -ceq (Get-ExHyperVFirewallSignature $Target)) }; " +
        "function Get-ExHyperVRestoredFirewallRuleForRemoval { param([string]$Name,[string]$ExpectedGroup) " +
        "$target = Get-ExHyperVExactFirewallRule -PolicyStore 'PersistentStore' -Name $Name -Required $false; " +
        "if ($null -eq $target) { return $null }; " +
        "$source = Get-ExHyperVExactFirewallRule -PolicyStore 'SystemDefaults' -Name $Name -Required $true; " +
        "if (-not (Test-ExHyperVFirewallSource -Rule $source -ExpectedGroup $ExpectedGroup) " +
        "-or -not (Test-ExHyperVCopiedFirewallRule -Source $source -Target $target -ExpectedGroup $ExpectedGroup)) { " +
        "throw ('防火墙规则状态、过滤器或所有权已变化，未删除当前规则：' + $Name) }; " +
        "return $target }; " +
        "function Remove-ExHyperVRestoredFirewallRule { param([string]$Name,[string]$ExpectedGroup) " +
        "$target = Get-ExHyperVRestoredFirewallRuleForRemoval -Name $Name -ExpectedGroup $ExpectedGroup; " +
        "if ($null -ne $target) { $target | Remove-NetFirewallRule -ErrorAction Stop } };";

    private static HostConfigurationCommand ConfigureConsoleRule(
        HostPreflightPlannedChange change,
        HostFirewallSnapshot firewall,
        IReadOnlyList<string> cidrs)
    {
        string rule = Quote(ConsoleRuleName);
        string remote = ArrayLiteral(cidrs.Select(Ipv4Cidr.ToWindowsFirewallAddress));
        string apply;
        string rollback;
        if (!firewall.Console2179RuleExists)
        {
            apply = $"if (Get-NetFirewallRule -Name {rule} -ErrorAction SilentlyContinue) {{ throw '同名 TCP 2179 规则已出现，预检结果已过期。' }}; New-NetFirewallRule -Name {rule} -DisplayName {rule} -Direction Inbound -Action Allow -Protocol TCP -LocalPort 2179 -RemoteAddress {remote} -Profile Private,Domain -Enabled True -ErrorAction Stop | Out-Null";
            string appliedState = FirewallStateCheck("True", Quote("Allow"), Quote("TCP"), "'Private','Domain'", "@('2179')", remote);
            rollback = $"$rule = Get-NetFirewallRule -Name {rule} -ErrorAction SilentlyContinue; if ($null -eq $rule) {{ return }}; {ReadFirewallFilters()} $isApplied = {appliedState}; if (-not $isApplied -or -not ({FirewallOwnershipCheck()})) {{ throw 'TCP 2179 规则已被其他操作修改或替换，未删除当前规则。' }}; $rule | Remove-NetFirewallRule -ErrorAction Stop";
        }
        else
        {
            string originalRemote = ArrayLiteral(firewall.Console2179RemoteAddresses);
            string originalPorts = ArrayLiteral(firewall.Console2179LocalPorts);
            string originalProfile = ProfileLiteral(firewall.Console2179Profiles);
            string originalEnabled = firewall.ExHyperVConsole2179RuleEnabled ? "True" : "False";
            string originalAction = Quote(firewall.ExHyperVConsole2179Action);
            string originalProtocol = Quote(firewall.ExHyperVConsole2179Protocol);
            string restore = $"$rule | Set-NetFirewallRule -Enabled {originalEnabled} -Action {originalAction} -Profile {originalProfile} -ErrorAction Stop; $rule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol {originalProtocol} -LocalPort {originalPorts} -ErrorAction Stop; $rule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress {originalRemote} -ErrorAction Stop";
            string originalState = FirewallStateCheck(originalEnabled, originalAction, originalProtocol, originalProfile, originalPorts, originalRemote);
            string appliedState = FirewallStateCheck("True", Quote("Allow"), Quote("TCP"), "'Private','Domain'", "@('2179')", remote);
            string rollbackCompatible = FirewallStateCompatible(
                originalEnabled, originalAction, originalProtocol, originalProfile, originalPorts, originalRemote,
                "True", Quote("Allow"), Quote("TCP"), "'Private','Domain'", "@('2179')", remote);
            string assertOriginal = $"{ReadFirewallFilters()} if (-not ({FirewallOwnershipCheck()}) -or -not ({originalState})) {{ throw 'TCP 2179 规则状态或所有权已变化，预检结果已过期。' }}";
            apply = $"$rule = Get-NetFirewallRule -Name {rule} -ErrorAction Stop; {assertOriginal}; try {{ $rule | Set-NetFirewallRule -Enabled True -Action Allow -Profile Private,Domain -ErrorAction Stop; $rule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort 2179 -ErrorAction Stop; $rule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress {remote} -ErrorAction Stop }} catch {{ $originalError = $_; try {{ {restore} }} catch {{ throw '{RollbackRequiredMarker}' }}; throw $originalError }}";
            rollback = $"$rule = Get-NetFirewallRule -Name {rule} -ErrorAction SilentlyContinue; if ($null -eq $rule) {{ throw 'TCP 2179 规则已不存在，无法安全回滚。' }}; if (-not ({FirewallOwnershipCheck()})) {{ throw 'TCP 2179 规则所有权已变化，未覆盖当前规则。' }}; {ReadFirewallFilters()} $isOriginal = {originalState}; if ($isOriginal) {{ return }}; $isCompatible = {rollbackCompatible}; if (-not $isCompatible) {{ throw 'TCP 2179 规则已被其他操作修改，未覆盖当前状态。' }}; {restore}";
        }
        return new(change.Kind, change.Title, apply, rollback);
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string ArrayLiteral(IEnumerable<string> values)
    {
        string[] items = values.Select(Quote).ToArray();
        return items.Length == 0 ? "@()" : $"@({string.Join(",", items)})";
    }

    private static string ProfileLiteral(IReadOnlyList<string> profiles) =>
        profiles.Count == 0 ? Quote("Any") : string.Join(",", profiles.Select(Quote));

    private static string ReadFirewallFilters() =>
        "$portFilter = $rule | Get-NetFirewallPortFilter; $addressFilter = $rule | Get-NetFirewallAddressFilter;";

    private static string FirewallOwnershipCheck(
        string ruleExpression = "$rule",
        string? expectedGroup = null)
    {
        string group = expectedGroup is null
            ? string.Empty
            : $" -and {ruleExpression}.Group.ToString() -ceq {expectedGroup}";
        return $"{ruleExpression}.Direction.ToString() -ceq 'Inbound' " +
               $"-and {ruleExpression}.PolicyStoreSourceType.ToString() -ceq 'Local' " +
               $"-and {ruleExpression}.PolicyStoreSource.ToString() -ceq 'PersistentStore'" + group;
    }

    private static string FirewallStateCheck(
        string enabled,
        string action,
        string protocol,
        string profiles,
        string ports,
        string remoteAddresses) =>
        $"& {{ $expectedProfiles = @({profiles}); $expectedPorts = {ports}; $expectedRemote = {remoteAddresses}; " +
        $"$actualProfiles = @($rule.Profile.ToString().Split(',') | ForEach-Object {{ $_.Trim() }} | Sort-Object); " +
        "$actualPorts = @($portFilter.LocalPort | Sort-Object); $actualRemote = @($addressFilter.RemoteAddress | Sort-Object); " +
        $"return ($rule.Enabled.ToString() -ceq '{enabled}' -and $rule.Action.ToString() -ceq {action} " +
        $"-and $portFilter.Protocol.ToString() -ceq {protocol} " +
        "-and ($actualProfiles -join ',') -ceq (@($expectedProfiles | Sort-Object) -join ',') " +
        "-and ($actualPorts -join ',') -ceq (@($expectedPorts | Sort-Object) -join ',') " +
        "-and ($actualRemote -join ',') -ceq (@($expectedRemote | Sort-Object) -join ',')) }";

    private static string FirewallStateCompatible(
        string originalEnabled,
        string originalAction,
        string originalProtocol,
        string originalProfiles,
        string originalPorts,
        string originalRemoteAddresses,
        string appliedEnabled,
        string appliedAction,
        string appliedProtocol,
        string appliedProfiles,
        string appliedPorts,
        string appliedRemoteAddresses) =>
        $"& {{ $originalProfiles = @({originalProfiles}); $appliedProfiles = @({appliedProfiles}); " +
        $"$originalPorts = {originalPorts}; $appliedPorts = {appliedPorts}; " +
        $"$originalRemote = {originalRemoteAddresses}; $appliedRemote = {appliedRemoteAddresses}; " +
        "$enabled = $rule.Enabled.ToString(); $action = $rule.Action.ToString(); $protocol = $portFilter.Protocol.ToString(); " +
        "$actualProfiles = @($rule.Profile.ToString().Split(',') | ForEach-Object { $_.Trim() } | Sort-Object); " +
        "$actualPorts = @($portFilter.LocalPort | Sort-Object); $actualRemote = @($addressFilter.RemoteAddress | Sort-Object); " +
        $"return (($enabled -ceq '{originalEnabled}' -or $enabled -ceq '{appliedEnabled}') " +
        $"-and ($action -ceq {originalAction} -or $action -ceq {appliedAction}) " +
        $"-and ($protocol -ceq {originalProtocol} -or $protocol -ceq {appliedProtocol}) " +
        "-and ((($actualProfiles -join ',') -ceq (@($originalProfiles | Sort-Object) -join ',')) -or (($actualProfiles -join ',') -ceq (@($appliedProfiles | Sort-Object) -join ','))) " +
        "-and ((($actualPorts -join ',') -ceq (@($originalPorts | Sort-Object) -join ',')) -or (($actualPorts -join ',') -ceq (@($appliedPorts | Sort-Object) -join ','))) " +
        "-and ((($actualRemote -join ',') -ceq (@($originalRemote | Sort-Object) -join ',')) -or (($actualRemote -join ',') -ceq (@($appliedRemote | Sort-Object) -join ',')))) }";
}
