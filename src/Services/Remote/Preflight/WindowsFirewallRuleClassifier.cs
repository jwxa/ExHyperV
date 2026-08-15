namespace ExHyperV.Services.Remote.Preflight;

internal static class WindowsFirewallRuleClassifier
{
    public const string WmiResourceGroup = "@FirewallAPI.dll,-34251";
    public const string HyperVManagementResourceGroup = @"@%systemroot%\system32\vmms.exe,-210";

    public const string InboundRuleQuery =
        "SELECT InstanceID,DisplayName,DisplayGroup,RuleGroup,PolicyStoreSource," +
        "PolicyStoreSourceType,Enabled,Direction,Action,Profiles " +
        "FROM MSFT_NetFirewallRule WHERE Direction=1";

    public const string ConsoleRuleQuery =
        "SELECT InstanceID,DisplayName,DisplayGroup,RuleGroup,PolicyStoreSource," +
        "PolicyStoreSourceType,Enabled,Direction,Action,Profiles " +
        "FROM MSFT_NetFirewallRule WHERE InstanceID='ExHyperV Console (TCP 2179)'";

    public static bool IsWmiBuiltIn(string group) =>
        EqualsValue(group, WmiResourceGroup);

    public static bool IsHyperVManagementBuiltIn(string group) =>
        EqualsValue(group, HyperVManagementResourceGroup);

    public static bool IsEnabled(ushort value) => value == 1;

    public static string ActionText(ushort value) => value switch
    {
        2 => "Allow",
        4 => "Block",
        _ => $"Unknown({value})"
    };

    public static string ProtocolText(ushort value) => value switch
    {
        1 => "ICMPv4",
        6 => "TCP",
        17 => "UDP",
        58 => "ICMPv6",
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    public static string ProtocolText(object? value)
    {
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)?.Trim()
            ?? string.Empty;
        if (ushort.TryParse(
                text,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out ushort protocol))
            return ProtocolText(protocol);

        return text.ToUpperInvariant() switch
        {
            "ICMPV4" => "ICMPv4",
            "TCP" => "TCP",
            "UDP" => "UDP",
            "ICMPV6" => "ICMPv6",
            _ => text
        };
    }

    public static bool IsLocalPersistentPolicy(ushort sourceType, string source) =>
        sourceType == 1 && EqualsValue(source, "PersistentStore");

    private static bool EqualsValue(string value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}
