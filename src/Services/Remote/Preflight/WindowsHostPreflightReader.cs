using System.Management;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Tools;

namespace ExHyperV.Services.Remote.Preflight;

public sealed class WindowsHostPreflightReader(TimeSpan? timeout = null) : IHostPreflightReader
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(12);

    public async Task<IHostPreflightReadSession> OpenAsync(
        string address,
        ResolvedHostIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(identity);
        WmiContext context = identity.UsesCurrentWindowsIdentity
            ? WmiContext.RemoteCurrentWindowsIdentity(address, _timeout)
            : WmiContext.Remote(address, identity.UserName!, identity.Password!, _timeout);
        var session = new WindowsHostPreflightReadSession(context, _timeout);
        try
        {
            await session.InitializeAsync(cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }
}

internal sealed class WindowsHostPreflightReadSession(
    WmiContext context,
    TimeSpan timeout) : IHostPreflightReadSession
{
    private const uint HkeyLocalMachine = 0x80000002;
    private const string TokenFilterKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string TokenFilterValue = "LocalAccountTokenFilterPolicy";
    private const string ConsoleRuleName = "ExHyperV Console (TCP 2179)";
    private static readonly IReadOnlyDictionary<string, object> SystemDefaultsQueryContext =
        new Dictionary<string, object> { ["PolicyStore"] = "SystemDefaults" };

    private static readonly IReadOnlyDictionary<HostLocalGroupKind, string> GroupSids =
        new Dictionary<HostLocalGroupKind, string>
        {
            [HostLocalGroupKind.Administrators] = "S-1-5-32-544",
            [HostLocalGroupKind.HyperVAdministrators] = "S-1-5-32-578",
            [HostLocalGroupKind.RemoteManagementUsers] = "S-1-5-32-580"
        };

    private HostJoinSnapshot? _join;
    private int _disposed;

    public async Task InitializeAsync(CancellationToken cancellationToken) =>
        _join = await QueryJoinAsync(cancellationToken);

    public Task<HostJoinSnapshot> ReadJoinAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return Task.FromResult(_join ?? throw new InvalidOperationException("远程只读会话尚未初始化。"));
    }

    public async Task<IReadOnlyList<HostLocalAccount>> ReadEnabledLocalAccountsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ApiResponse<List<HostLocalAccount>> response = await WmiApi.QueryAsync(
                "SELECT Name,SID FROM Win32_UserAccount WHERE LocalAccount=TRUE AND Disabled=FALSE",
                item => new HostLocalAccount(Value<string>(item, "Name"), Value<string>(item, "SID")),
                WmiScope.CimV2,
                context)
            .WaitAsync(timeout, cancellationToken);
        return Require(response, "读取已启用本地账户").OrderBy(account => account.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<HostLocalGroupSnapshot> ReadLocalGroupAsync(
        HostLocalGroupKind group,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string sid = GroupSids[group];
        ApiResponse<List<WmiGroup>> groupResponse = await WmiApi.QueryAsync(
                $"SELECT Name,Domain FROM Win32_Group WHERE LocalAccount=TRUE AND SID='{sid}'",
                item => new WmiGroup(Value<string>(item, "Name"), Value<string>(item, "Domain")),
                WmiScope.CimV2,
                context)
            .WaitAsync(timeout, cancellationToken);
        WmiGroup? resolvedGroup = Require(groupResponse, $"读取 {HostPreflightPipeline.GroupTitle(group)}")
            .FirstOrDefault();
        if (resolvedGroup is null)
            throw new InvalidOperationException($"远程主机不存在内置组 {HostPreflightPipeline.GroupTitle(group)}（{sid}）。");

        string groupPath = $"Win32_Group.Domain=\"{EscapeObjectPath(resolvedGroup.Domain)}\",Name=\"{EscapeObjectPath(resolvedGroup.Name)}\"";
        ApiResponse<List<string>> membersResponse = await WmiApi.QueryAsync(
                $"ASSOCIATORS OF {{{groupPath}}} WHERE AssocClass=Win32_GroupUser Role=GroupComponent",
                item => AccountName(item),
                WmiScope.CimV2,
                context)
            .WaitAsync(timeout, cancellationToken);
        string[] members = Require(membersResponse, $"读取 {resolvedGroup.Name} 成员")
            .Where(member => !string.IsNullOrWhiteSpace(member))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(member => member, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new HostLocalGroupSnapshot(group, resolvedGroup.Name, members);
    }

    public async Task<HostTokenFilterPolicyState> ReadTokenFilterPolicyAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ApiResponse<ManagementBaseObject> response = await WmiApi.InvokeClassMethodAsync(
                "StdRegProv",
                "GetDWORDValue",
                input =>
                {
                    input["hDefKey"] = HkeyLocalMachine;
                    input["sSubKeyName"] = TokenFilterKey;
                    input["sValueName"] = TokenFilterValue;
                },
                WmiScope.Default,
                context,
                cancellationToken);
        if (!response.Success)
        {
            if (response.Code == 2) return HostTokenFilterPolicyState.Missing;
            throw new InvalidOperationException($"读取 LocalAccountTokenFilterPolicy 失败：{response.Error}");
        }

        using ManagementBaseObject output = response.Data
            ?? throw new InvalidOperationException("读取 LocalAccountTokenFilterPolicy 未返回结果。");
        object? rawValue = output["uValue"];
        if (rawValue is null) return HostTokenFilterPolicyState.Missing;
        return Convert.ToUInt32(rawValue) == 1
            ? HostTokenFilterPolicyState.Enabled
            : HostTokenFilterPolicyState.Disabled;
    }

    public async Task<IReadOnlyList<HostNetworkSnapshot>> ReadNetworksAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ApiResponse<List<WmiNetworkProfile>> profilesResponse = await WmiApi.QueryAsync(
                "SELECT InterfaceIndex,Name,NetworkCategory,IPv4Connectivity FROM MSFT_NetConnectionProfile",
                item => new WmiNetworkProfile(
                    Value<uint>(item, "InterfaceIndex"),
                    Value<string>(item, "Name"),
                    MapNetworkCategory(Value<ushort>(item, "NetworkCategory")),
                    Value<ushort>(item, "IPv4Connectivity")),
                WmiScope.StdCimV2,
                context)
            .WaitAsync(timeout, cancellationToken);
        ApiResponse<List<WmiIpv4Address>> addressesResponse = await WmiApi.QueryAsync(
                "SELECT InterfaceIndex,IPAddress,PrefixLength FROM MSFT_NetIPAddress WHERE AddressFamily=2",
                item => new WmiIpv4Address(
                    Value<uint>(item, "InterfaceIndex"),
                    Value<string>(item, "IPAddress"),
                    Value<byte>(item, "PrefixLength")),
                WmiScope.StdCimV2,
                context)
            .WaitAsync(timeout, cancellationToken);

        List<WmiIpv4Address> addresses = Require(addressesResponse, "读取网络 IPv4 地址");
        return Require(profilesResponse, "读取网络配置文件")
            .Where(profile => profile.Ipv4Connectivity > 0)
            .Select(profile => new HostNetworkSnapshot(
                profile.InterfaceIndex,
                string.IsNullOrWhiteSpace(profile.Name) ? $"网络 {profile.InterfaceIndex}" : profile.Name,
                profile.Category,
                addresses
                    .Where(address => address.InterfaceIndex == profile.InterfaceIndex)
                    .Where(address => !address.Address.StartsWith("169.254.", StringComparison.Ordinal))
                    .Select(address => new HostIpv4Address(address.Address, address.PrefixLength))
                    .ToArray()))
            .Where(network => network.Ipv4Addresses.Count > 0)
            .OrderBy(network => network.InterfaceIndex)
            .ToArray();
    }

    public async Task<HostFirewallSnapshot> ReadFirewallAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ApiResponse<List<WmiFirewallRule>> response = await WmiApi.QueryAsync(
                WindowsFirewallRuleClassifier.InboundRuleQuery,
                FirewallRule,
                WmiScope.StdCimV2,
                context)
            .WaitAsync(timeout, cancellationToken);
        ApiResponse<List<WmiFirewallRule>> systemDefaultsResponse = await WmiApi.QueryAsync(
                WindowsFirewallRuleClassifier.InboundRuleQuery,
                FirewallRule,
                WmiScope.StdCimV2,
                context,
                SystemDefaultsQueryContext)
            .WaitAsync(timeout, cancellationToken);
        ApiResponse<List<WmiFirewallRule>> consoleResponse = await WmiApi.QueryAsync(
                WindowsFirewallRuleClassifier.ConsoleRuleQuery,
                FirewallRule,
                WmiScope.StdCimV2,
                context)
            .WaitAsync(timeout, cancellationToken);
        List<WmiFirewallRule> rules = Require(response, "读取防火墙规则");
        WmiFirewallRule[] wmiRules = rules.Where(rule =>
            WindowsFirewallRuleClassifier.IsWmiBuiltIn(rule.Group)).ToArray();
        WmiFirewallRule[] hyperVRules = rules.Where(rule =>
            WindowsFirewallRuleClassifier.IsHyperVManagementBuiltIn(rule.Group)).ToArray();
        List<WmiFirewallRule> systemDefaults = Require(systemDefaultsResponse, "读取 SystemDefaults 防火墙规则");
        WmiFirewallRule[] defaultWmiRules = systemDefaults.Where(rule =>
            WindowsFirewallRuleClassifier.IsWmiBuiltIn(rule.Group)).ToArray();
        WmiFirewallRule[] defaultHyperVRules = systemDefaults.Where(rule =>
            WindowsFirewallRuleClassifier.IsHyperVManagementBuiltIn(rule.Group)).ToArray();
        string[] activeWmiRuleNames = UniqueRuleNames(wmiRules, "Windows 内置 WMI ActiveStore");
        string[] activeHyperVRuleNames = UniqueRuleNames(hyperVRules, "Windows 内置 Hyper-V ActiveStore");
        string[] defaultWmiRuleNames = UniqueRuleNames(defaultWmiRules, "Windows 内置 WMI SystemDefaults");
        string[] defaultHyperVRuleNames = UniqueRuleNames(defaultHyperVRules, "Windows 内置 Hyper-V SystemDefaults");
        string[] restorableWmiRuleNames = MissingRuleNames(defaultWmiRuleNames, activeWmiRuleNames);
        string[] restorableHyperVRuleNames = MissingRuleNames(defaultHyperVRuleNames, activeHyperVRuleNames);
        string[] disabledWmiRuleNames = MutableDisabledRuleNames(wmiRules, "Windows 内置 WMI");
        string[] disabledHyperVRuleNames = MutableDisabledRuleNames(hyperVRules, "Windows 内置 Hyper-V");
        string[] disabledRestorableWmiRuleNames = DisabledMissingRuleNames(defaultWmiRules, restorableWmiRuleNames);
        string[] disabledRestorableHyperVRuleNames = DisabledMissingRuleNames(defaultHyperVRules, restorableHyperVRuleNames);
        WmiFirewallRule[] consoleRules = Require(consoleResponse, "读取 ExHyperV TCP 2179 防火墙规则").ToArray();
        if (consoleRules.Length > 1)
            throw new InvalidOperationException($"检测到多个名为 {ConsoleRuleName} 的活动防火墙规则，拒绝选择不确定的配置对象。");
        WmiFirewallRule? consoleRule = consoleRules.SingleOrDefault();
        if (consoleRule is not null && consoleRule.Direction != 1)
            throw new InvalidOperationException($"防火墙规则 {ConsoleRuleName} 不是入站规则，ExHyperV 不会修改该规则。");
        if (consoleRule is not null
            && !WindowsFirewallRuleClassifier.IsLocalPersistentPolicy(
                consoleRule.PolicyStoreSourceType,
                consoleRule.PolicyStoreSource))
            throw new InvalidOperationException($"防火墙规则 {ConsoleRuleName} 由非本地策略管理，ExHyperV 不会修改该规则。");
        string[] remoteAddresses = consoleRule is null
            ? Array.Empty<string>()
            : await ReadRemoteAddressesAsync(consoleRule, cancellationToken);
        WmiConsolePortFilter? consolePort = consoleRule is null
            ? null
            : await ReadConsolePortAsync(consoleRule, cancellationToken);
        return new HostFirewallSnapshot(
            wmiRules.Length > 0 && restorableWmiRuleNames.Length == 0 && wmiRules.All(rule => rule.Enabled),
            hyperVRules.Length > 0 && restorableHyperVRuleNames.Length == 0 && hyperVRules.All(rule => rule.Enabled),
            consoleRule?.Enabled == true,
            remoteAddresses,
            disabledWmiRuleNames,
            disabledHyperVRuleNames,
            consoleRule is not null,
            consolePort?.Protocol ?? string.Empty,
            consolePort?.LocalPorts ?? Array.Empty<string>(),
            consoleRule?.Action ?? "Allow",
            consoleRule?.Profiles ?? Array.Empty<string>(),
            wmiRules.Length > 0,
            hyperVRules.Length > 0,
            restorableWmiRuleNames,
            restorableHyperVRuleNames,
            disabledRestorableWmiRuleNames,
            disabledRestorableHyperVRuleNames);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            WmiConnectionCache.Clear(context);
        return ValueTask.CompletedTask;
    }

    private async Task<HostJoinSnapshot> QueryJoinAsync(CancellationToken cancellationToken)
    {
        ApiResponse<HostJoinSnapshot> response = await WmiApi.QueryFirstAsync(
                "SELECT Name,PartOfDomain,Domain,Workgroup FROM Win32_ComputerSystem",
                item =>
                {
                    bool domain = Value<bool>(item, "PartOfDomain");
                    return new HostJoinSnapshot(
                        Value<string>(item, "Name"),
                        domain ? HostJoinKind.Domain : HostJoinKind.Workgroup,
                        domain ? Value<string>(item, "Domain") : Value<string>(item, "Workgroup"));
                },
                WmiScope.CimV2,
                context)
            .WaitAsync(timeout, cancellationToken);
        return RequireOne(response, "读取工作组或域状态");
    }

    private async Task<string[]> ReadRemoteAddressesAsync(
        WmiFirewallRule rule,
        CancellationToken cancellationToken)
    {
        ApiResponse<List<string[]>> response = await WmiApi.WithFirstAsync(
                $"SELECT * FROM MSFT_NetFirewallRule WHERE InstanceID='{EscapeWql(rule.Name)}'",
                async source =>
                {
                    ApiResponse<List<string[]>> related = await WmiApi.QueryRelatedAsync(
                        source,
                        "MSFT_NetAddressFilter",
                        item => item["RemoteAddress"] as string[] ?? Array.Empty<string>(),
                        "MSFT_NetFirewallRuleFilterByAddress",
                        WmiScope.StdCimV2,
                        context);
                    return Require(related, "读取 TCP 2179 远程地址范围");
                },
                WmiScope.StdCimV2,
                context)
            .WaitAsync(timeout, cancellationToken);
        return RequireOne(response, "读取 TCP 2179 防火墙地址过滤器")
            .SelectMany(values => values)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<WmiConsolePortFilter> ReadConsolePortAsync(
        WmiFirewallRule rule,
        CancellationToken cancellationToken)
    {
        ApiResponse<List<WmiConsolePortFilter>> response = await WmiApi.WithFirstAsync(
                $"SELECT * FROM MSFT_NetFirewallRule WHERE InstanceID='{EscapeWql(rule.Name)}'",
                async source =>
                {
                    ApiResponse<List<WmiConsolePortFilter>> related = await WmiApi.QueryRelatedAsync(
                        source,
                        "MSFT_NetProtocolPortFilter",
                        item => new WmiConsolePortFilter(
                            WindowsFirewallRuleClassifier.ProtocolText(item["Protocol"]),
                            item["LocalPort"] as string[] ?? Array.Empty<string>()),
                        "MSFT_NetFirewallRuleFilterByProtocolPort",
                        WmiScope.StdCimV2,
                        context);
                    return Require(related, "读取 TCP 2179 协议和端口");
                },
                WmiScope.StdCimV2,
                context)
            .WaitAsync(timeout, cancellationToken);
        return RequireOne(response, "读取 TCP 2179 协议和端口过滤器").SingleOrDefault()
            ?? throw new InvalidOperationException("TCP 2179 规则没有唯一的协议和端口过滤器。");
    }

    private static List<T> Require<T>(ApiResponse<List<T>> response, string operation)
    {
        if (!response.Success) throw new InvalidOperationException($"{operation}失败：{response.Error}");
        return response.Data ?? [];
    }

    private static T RequireOne<T>(ApiResponse<T> response, string operation)
    {
        if (!response.Success) throw new InvalidOperationException($"{operation}失败：{response.Error}");
        if (response.IsEmpty || response.Data is null) throw new InvalidOperationException($"{operation}未返回数据。");
        return response.Data;
    }

    private static T Value<T>(ManagementBaseObject item, string property)
    {
        object? value = item[property];
        if (value is null) return default!;
        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string AccountName(ManagementBaseObject item)
    {
        string name = Value<string>(item, "Name");
        string domain = Value<string>(item, "Domain");
        return string.IsNullOrWhiteSpace(domain) ? name : $"{domain}\\{name}";
    }

    private static string EscapeObjectPath(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeWql(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);

    private static HostNetworkCategory MapNetworkCategory(ushort category) => category switch
    {
        0 => HostNetworkCategory.Public,
        1 => HostNetworkCategory.Private,
        2 => HostNetworkCategory.DomainAuthenticated,
        _ => HostNetworkCategory.Unknown
    };

    private static IReadOnlyList<string> MapFirewallProfiles(uint value)
    {
        if (value == uint.MaxValue || value == 0) return ["Any"];
        var profiles = new List<string>();
        if ((value & 1) != 0) profiles.Add("Domain");
        if ((value & 2) != 0) profiles.Add("Private");
        if ((value & 4) != 0) profiles.Add("Public");
        return profiles;
    }

    private static WmiFirewallRule FirewallRule(ManagementBaseObject item) => new(
        Value<string>(item, "InstanceID"),
        Value<string>(item, "DisplayName"),
        Value<string>(item, "DisplayGroup"),
        Value<string>(item, "RuleGroup"),
        Value<string>(item, "PolicyStoreSource"),
        Value<ushort>(item, "PolicyStoreSourceType"),
        WindowsFirewallRuleClassifier.IsEnabled(Value<ushort>(item, "Enabled")),
        Value<ushort>(item, "Direction"),
        WindowsFirewallRuleClassifier.ActionText(Value<ushort>(item, "Action")),
        MapFirewallProfiles(Value<uint>(item, "Profiles")));

    private static string[] MutableDisabledRuleNames(
        IReadOnlyList<WmiFirewallRule> rules,
        string title)
    {
        WmiFirewallRule[] policyManaged = rules.Where(rule =>
            !rule.Enabled
            && !WindowsFirewallRuleClassifier.IsLocalPersistentPolicy(
                rule.PolicyStoreSourceType,
                rule.PolicyStoreSource)).ToArray();
        if (policyManaged.Length > 0)
            throw new InvalidOperationException(
                $"{title} 防火墙规则 {string.Join("、", policyManaged.Select(rule => rule.Name))} 由非本地策略管理且当前已禁用，ExHyperV 不会修改这些规则。");
        return rules.Where(rule => !rule.Enabled)
            .Select(rule => rule.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] UniqueRuleNames(IReadOnlyList<WmiFirewallRule> rules, string title)
    {
        string[] names = rules.Select(rule => rule.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"{title} 包含没有 InstanceID 的规则，无法生成精确修改。");
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new InvalidOperationException($"{title} 包含重复 InstanceID，无法生成精确修改。");
        return names;
    }

    private static string[] MissingRuleNames(
        IReadOnlyList<string> systemDefaultNames,
        IReadOnlyList<string> activeNames) =>
        systemDefaultNames.Except(activeNames, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] DisabledMissingRuleNames(
        IReadOnlyList<WmiFirewallRule> systemDefaults,
        IReadOnlyList<string> missingNames)
    {
        var missing = missingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return systemDefaults.Where(rule => !rule.Enabled && missing.Contains(rule.Name))
            .Select(rule => rule.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private sealed record WmiGroup(string Name, string Domain);
    private sealed record WmiNetworkProfile(uint InterfaceIndex, string Name, HostNetworkCategory Category, ushort Ipv4Connectivity);
    private sealed record WmiIpv4Address(uint InterfaceIndex, string Address, byte PrefixLength);
    private sealed record WmiFirewallRule(
        string Name,
        string DisplayName,
        string DisplayGroup,
        string Group,
        string PolicyStoreSource,
        ushort PolicyStoreSourceType,
        bool Enabled,
        ushort Direction,
        string Action,
        IReadOnlyList<string> Profiles);
    private sealed record WmiConsolePortFilter(string Protocol, IReadOnlyList<string> LocalPorts);
}
