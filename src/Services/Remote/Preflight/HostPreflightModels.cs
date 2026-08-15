namespace ExHyperV.Services.Remote.Preflight;

public enum HostJoinKind
{
    Workgroup,
    Domain
}

public enum HostLocalGroupKind
{
    Administrators,
    HyperVAdministrators,
    RemoteManagementUsers
}

public enum HostTokenFilterPolicyState
{
    Missing,
    Enabled,
    Disabled,
    Unknown
}

public enum HostNetworkCategory
{
    Public,
    Private,
    DomainAuthenticated,
    Unknown
}

public enum HostPreflightStage
{
    Connection,
    HostEnvironment,
    LocalAccounts,
    AdministratorsGroup,
    HyperVAdministratorsGroup,
    RemoteManagementUsersGroup,
    TokenFilterPolicy,
    Networks,
    Firewall
}

public enum HostPreflightFindingStatus
{
    Passed,
    Attention,
    Failed
}

public enum HostPreflightLogLevel
{
    Information,
    Warning,
    Error
}

public sealed record HostJoinSnapshot(string ComputerName, HostJoinKind Kind, string JoinName);

public sealed record HostLocalAccount(string Name, string Sid);

public sealed record HostLocalGroupSnapshot(
    HostLocalGroupKind Kind,
    string DisplayName,
    IReadOnlyList<string> Members);

public sealed record HostIpv4Address(string Address, int PrefixLength)
{
    public string Cidr => Ipv4Cidr.Normalize($"{Address}/{PrefixLength}");
}

public sealed record HostNetworkSnapshot(
    uint InterfaceIndex,
    string Name,
    HostNetworkCategory Category,
    IReadOnlyList<HostIpv4Address> Ipv4Addresses);

public sealed record HostFirewallSnapshot(
    bool WmiBuiltInRulesEnabled,
    bool HyperVBuiltInRulesEnabled,
    bool ExHyperVConsole2179RuleEnabled,
    IReadOnlyList<string>? ExHyperVConsole2179RemoteAddresses = null,
    IReadOnlyList<string>? DisabledWmiRuleNames = null,
    IReadOnlyList<string>? DisabledHyperVRuleNames = null,
    bool? ExHyperVConsole2179RuleExists = null,
    string ExHyperVConsole2179Protocol = "TCP",
    IReadOnlyList<string>? ExHyperVConsole2179LocalPorts = null,
    string ExHyperVConsole2179Action = "Allow",
    IReadOnlyList<string>? ExHyperVConsole2179Profiles = null,
    bool? WmiBuiltInRuleGroupDetected = null,
    bool? HyperVBuiltInRuleGroupDetected = null,
    IReadOnlyList<string>? RestorableWmiRuleNames = null,
    IReadOnlyList<string>? RestorableHyperVRuleNames = null,
    IReadOnlyList<string>? DisabledRestorableWmiRuleNames = null,
    IReadOnlyList<string>? DisabledRestorableHyperVRuleNames = null)
{
    public IReadOnlyList<string> Console2179RemoteAddresses { get; } =
        ExHyperVConsole2179RemoteAddresses ?? Array.Empty<string>();
    public IReadOnlyList<string> WmiRuleNamesToRestore { get; } = RestorableWmiRuleNames ?? Array.Empty<string>();
    public IReadOnlyList<string> HyperVRuleNamesToRestore { get; } = RestorableHyperVRuleNames ?? Array.Empty<string>();
    public IReadOnlyList<string> WmiRuleNamesToEnable { get; } = MergeRuleNames(
        DisabledWmiRuleNames,
        DisabledRestorableWmiRuleNames);
    public IReadOnlyList<string> HyperVRuleNamesToEnable { get; } = MergeRuleNames(
        DisabledHyperVRuleNames,
        DisabledRestorableHyperVRuleNames);
    public bool WmiBuiltInRulesDetected { get; } = WmiBuiltInRuleGroupDetected
        ?? (WmiBuiltInRulesEnabled || (DisabledWmiRuleNames?.Count ?? 0) > 0);
    public bool HyperVBuiltInRulesDetected { get; } = HyperVBuiltInRuleGroupDetected
        ?? (HyperVBuiltInRulesEnabled || (DisabledHyperVRuleNames?.Count ?? 0) > 0);
    public bool Console2179RuleExists { get; } = ExHyperVConsole2179RuleExists ?? ExHyperVConsole2179RuleEnabled;
    public IReadOnlyList<string> Console2179LocalPorts { get; } = ExHyperVConsole2179LocalPorts ?? ["2179"];
    public IReadOnlyList<string> Console2179Profiles { get; } = ExHyperVConsole2179Profiles ?? ["Private", "Domain"];
    public bool Console2179EndpointMatches =>
        string.Equals(ExHyperVConsole2179Action, "Allow", StringComparison.OrdinalIgnoreCase)
        &&
        string.Equals(ExHyperVConsole2179Protocol, "TCP", StringComparison.OrdinalIgnoreCase)
        && Console2179LocalPorts.Count == 1
        && string.Equals(Console2179LocalPorts[0], "2179", StringComparison.OrdinalIgnoreCase)
        && Console2179Profiles.Count > 0
        && Console2179Profiles.All(profile =>
            string.Equals(profile, "Private", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profile, "Domain", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> MergeRuleNames(
        IReadOnlyList<string>? activeDisabled,
        IReadOnlyList<string>? restorableDisabled) =>
        (activeDisabled ?? Array.Empty<string>())
        .Concat(restorableDisabled ?? Array.Empty<string>())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public sealed record HostPreflightFinding(
    HostPreflightStage Stage,
    HostPreflightFindingStatus Status,
    string Title,
    string Evidence);

public sealed record HostPreflightLogEntry(
    DateTimeOffset Timestamp,
    HostPreflightStage Stage,
    HostPreflightLogLevel Level,
    string Message);

public sealed record HostPreflightFacts(
    HostJoinSnapshot? Join,
    IReadOnlyList<HostLocalAccount> EnabledLocalAccounts,
    IReadOnlyDictionary<HostLocalGroupKind, HostLocalGroupSnapshot> LocalGroups,
    HostTokenFilterPolicyState TokenFilterPolicy,
    IReadOnlyList<HostNetworkSnapshot> Networks,
    HostFirewallSnapshot? Firewall);

public sealed record HostPreflightReport(
    Guid ProfileId,
    string HostAddress,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    HostPreflightFacts Facts,
    IReadOnlyList<HostPreflightFinding> Findings,
    IReadOnlyList<HostPreflightLogEntry> LogEntries)
{
    public bool HasReadFailures => Findings.Any(finding => finding.Status == HostPreflightFindingStatus.Failed);
}

public enum HostPreflightAccountKind
{
    Local,
    Domain
}

public sealed record HostPreflightSelection(
    HostPreflightAccountKind AccountKind,
    string AccountName,
    IReadOnlyList<uint> SelectedNetworkInterfaceIndexes,
    IReadOnlyList<uint> NetworksToMakePrivate,
    IReadOnlyList<string> AllowedIpv4Cidrs);

public enum HostPreflightChangeKind
{
    AddHyperVAdministrators,
    AddRemoteManagementUsers,
    EnableLocalAccountTokenFilterPolicy,
    ChangeNetworkToPrivate,
    RestoreWmiFirewallRules,
    RestoreHyperVFirewallRules,
    EnableWmiFirewallRules,
    EnableHyperVFirewallRules,
    ConfigureConsole2179FirewallRule
}

public sealed record HostPreflightPlannedChange(
    HostPreflightChangeKind Kind,
    string Title,
    string Detail);

public sealed record HostPreflightPlan(
    HostPreflightAccountKind AccountKind,
    string AccountName,
    IReadOnlyList<HostNetworkSnapshot> SelectedNetworks,
    IReadOnlyList<uint> NetworksToMakePrivate,
    IReadOnlyList<string> AllowedIpv4Cidrs,
    IReadOnlyList<HostPreflightPlannedChange> Changes);

public sealed record HostPreflightPlanResult(
    HostPreflightPlan? Plan,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Plan is not null && Errors.Count == 0;
}
