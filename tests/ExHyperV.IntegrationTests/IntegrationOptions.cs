using System.Globalization;
using ExHyperV.Services.Remote.Preflight;
using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.IntegrationTests;

internal sealed record IntegrationOptions(
    string HostAddress,
    string DisplayName,
    Guid ProfileId,
    HostAuthenticationMode AuthenticationMode,
    string? UserName,
    string? Password,
    TimeSpan OperationTimeout,
    TimeSpan TotalTimeout,
    string ReportPath,
    bool EnableVmWrite,
    string? VmSelector,
    string? VmAction,
    bool EnableDisconnect,
    TimeSpan OutageStartDelay,
    TimeSpan OutageDetectionTimeout,
    TimeSpan ReconnectTimeout,
    bool PreviewConfiguration,
    bool EnableConfiguration,
    HostPreflightAccountKind? ConfigurationAccountKind,
    string? ConfigurationAccountName,
    IReadOnlyList<uint> ConfigurationNetworkIndexes,
    IReadOnlyList<uint> NetworksToMakePrivate,
    IReadOnlyList<string> AllowedIpv4Cidrs,
    bool EnableRollbackVerification,
    string? SecondHostAddress,
    string? SecondDisplayName,
    Guid? SecondProfileId)
{
    // Keep the confirmation value exact while keeping this source ASCII-only.
    public const string Confirmation = "\u786e\u8ba4";
    public const string RunVariable = "EXHYPERV_INTEGRATION_RUN";

    public static bool IsEnabled() => Confirmed(RunVariable);

    public static IntegrationOptions Load()
    {
        string? password = Secret("EXHYPERV_INTEGRATION_PASSWORD");
        string host = Required("EXHYPERV_INTEGRATION_HOST");
        string displayName = Optional("EXHYPERV_INTEGRATION_DISPLAY_NAME") ?? $"Controlled host {host}";
        string authentication = Optional("EXHYPERV_INTEGRATION_AUTH") ?? "current";
        HostAuthenticationMode authenticationMode = authentication.ToLowerInvariant() switch
        {
            "current" => HostAuthenticationMode.CurrentWindowsIdentity,
            "credential-manager" => HostAuthenticationMode.ExplicitCredential,
            _ => throw new IntegrationOptionException("EXHYPERV_INTEGRATION_AUTH must be current or credential-manager.")
        };

        string? profileIdValue = Optional("EXHYPERV_INTEGRATION_PROFILE_ID");
        Guid profileId = profileIdValue is null
            ? Guid.NewGuid()
            : Guid.TryParse(profileIdValue, out Guid parsed)
                ? parsed
                : throw new IntegrationOptionException("EXHYPERV_INTEGRATION_PROFILE_ID is not a valid GUID.");
        if (profileId == Guid.Empty)
            throw new IntegrationOptionException("EXHYPERV_INTEGRATION_PROFILE_ID cannot be empty.");
        string? userName = Optional("EXHYPERV_INTEGRATION_USERNAME");
        if (authenticationMode == HostAuthenticationMode.ExplicitCredential)
        {
            if (profileIdValue is null)
                throw new IntegrationOptionException("credential-manager mode requires EXHYPERV_INTEGRATION_PROFILE_ID.");
            if (userName is null)
                throw new IntegrationOptionException("credential-manager mode requires EXHYPERV_INTEGRATION_USERNAME.");
        }
        else if (userName is not null || password is not null)
        {
            throw new IntegrationOptionException("current mode cannot set explicit credential environment variables.");
        }

        HostProfile primaryTarget = ValidateTarget(profileId, displayName, host, authenticationMode, userName);
        host = primaryTarget.Address;
        displayName = primaryTarget.DisplayName;

        string? secondHost = Optional("EXHYPERV_INTEGRATION_SECOND_HOST");
        string? secondDisplayName = Optional("EXHYPERV_INTEGRATION_SECOND_DISPLAY_NAME");
        string? secondProfileIdValue = Optional("EXHYPERV_INTEGRATION_SECOND_PROFILE_ID");
        Guid? secondProfileId = null;
        if (secondHost is null)
        {
            if (secondDisplayName is not null || secondProfileIdValue is not null)
                throw new IntegrationOptionException(
                    "EXHYPERV_INTEGRATION_SECOND_DISPLAY_NAME and EXHYPERV_INTEGRATION_SECOND_PROFILE_ID require EXHYPERV_INTEGRATION_SECOND_HOST.");
        }
        else
        {
            if (authenticationMode == HostAuthenticationMode.ExplicitCredential && secondProfileIdValue is null)
                throw new IntegrationOptionException(
                    "credential-manager mode requires EXHYPERV_INTEGRATION_SECOND_PROFILE_ID for the second host.");
            secondProfileId = secondProfileIdValue is null
                ? Guid.NewGuid()
                : Guid.TryParse(secondProfileIdValue, out Guid parsedSecondProfileId)
                    ? parsedSecondProfileId
                    : throw new IntegrationOptionException("EXHYPERV_INTEGRATION_SECOND_PROFILE_ID is not a valid GUID.");
            if (secondProfileId == Guid.Empty)
                throw new IntegrationOptionException("EXHYPERV_INTEGRATION_SECOND_PROFILE_ID cannot be empty.");
            if (secondProfileId == profileId)
                throw new IntegrationOptionException("The two controlled hosts must use different profile IDs.");
            secondDisplayName ??= $"Controlled host {secondHost}";
            HostProfile secondTarget = ValidateTarget(
                secondProfileId.Value,
                secondDisplayName,
                secondHost,
                authenticationMode,
                userName);
            if (string.Equals(secondTarget.Address, host, StringComparison.OrdinalIgnoreCase))
                throw new IntegrationOptionException("EXHYPERV_INTEGRATION_SECOND_HOST must differ from EXHYPERV_INTEGRATION_HOST.");
            secondHost = secondTarget.Address;
            secondDisplayName = secondTarget.DisplayName;
        }

        bool vmWrite = Confirmed("EXHYPERV_INTEGRATION_VM_WRITE");
        string? vmSelector = Optional("EXHYPERV_INTEGRATION_VM");
        string? vmAction = Optional("EXHYPERV_INTEGRATION_VM_ACTION");
        if (vmWrite)
        {
            if (vmSelector is null)
                throw new IntegrationOptionException("VM write requires EXHYPERV_INTEGRATION_VM (name or GUID).");
            if (vmAction is not ("Start" or "Stop" or "TurnOff" or "Restart"))
                throw new IntegrationOptionException("EXHYPERV_INTEGRATION_VM_ACTION must be Start, Stop, TurnOff, or Restart.");
        }

        bool previewConfiguration = Confirmed("EXHYPERV_INTEGRATION_CONFIGURE_PREVIEW");
        bool configure = Confirmed("EXHYPERV_INTEGRATION_CONFIGURE");
        bool rollback = Confirmed("EXHYPERV_INTEGRATION_ROLLBACK_VERIFY");
        if (rollback && !configure)
            throw new IntegrationOptionException("Rollback verification requires configuration to be enabled in the same run.");

        HostPreflightAccountKind? accountKind = null;
        string? accountName = null;
        IReadOnlyList<uint> networkIndexes = [];
        IReadOnlyList<uint> makePrivate = [];
        IReadOnlyList<string> cidrs = [];
        if (previewConfiguration || configure)
        {
            accountKind = Required("EXHYPERV_INTEGRATION_ACCOUNT_KIND").ToLowerInvariant() switch
            {
                "local" => HostPreflightAccountKind.Local,
                "domain" => HostPreflightAccountKind.Domain,
                _ => throw new IntegrationOptionException("EXHYPERV_INTEGRATION_ACCOUNT_KIND must be local or domain.")
            };
            accountName = Required("EXHYPERV_INTEGRATION_ACCOUNT");
            networkIndexes = UIntList("EXHYPERV_INTEGRATION_NETWORKS", required: true);
            makePrivate = UIntList("EXHYPERV_INTEGRATION_MAKE_PRIVATE", required: false);
            if (makePrivate.Except(networkIndexes).Any())
                throw new IntegrationOptionException("EXHYPERV_INTEGRATION_MAKE_PRIVATE must be a subset of EXHYPERV_INTEGRATION_NETWORKS.");
            cidrs = CidrList("EXHYPERV_INTEGRATION_CIDRS");
        }

        string reportPath = NormalizeReportPath(
            Optional("EXHYPERV_INTEGRATION_REPORT")
            ?? Path.Combine(
                FindRepositoryRoot(),
                ".codex-tasks",
                "remote-host-management",
                "raw",
                $"controlled-host-acceptance-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json"));

        return new IntegrationOptions(
            host,
            displayName,
            profileId,
            authenticationMode,
            userName,
            password,
            Seconds("EXHYPERV_INTEGRATION_OPERATION_TIMEOUT_SECONDS", 15, 2, 120),
            Seconds("EXHYPERV_INTEGRATION_TOTAL_TIMEOUT_SECONDS", 600, 30, 3600),
            reportPath,
            vmWrite,
            vmSelector,
            vmAction,
            Confirmed("EXHYPERV_INTEGRATION_DISCONNECT"),
            Seconds("EXHYPERV_INTEGRATION_OUTAGE_START_DELAY_SECONDS", 10, 0, 300),
            Seconds("EXHYPERV_INTEGRATION_OUTAGE_DETECT_SECONDS", 90, 5, 600),
            Seconds("EXHYPERV_INTEGRATION_RECONNECT_SECONDS", 180, 10, 900),
            previewConfiguration,
            configure,
            accountKind,
            accountName,
            networkIndexes,
            makePrivate,
            cidrs,
            rollback,
            secondHost,
            secondDisplayName,
            secondProfileId);
    }

    public override string ToString() =>
        $"IntegrationOptions(HostAddress={HostAddress}, SecondHostAddress={SecondHostAddress ?? "[none]"}, AuthenticationMode={AuthenticationMode}, Password=[REDACTED])";

    private static bool Confirmed(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), Confirmation, StringComparison.Ordinal);

    private static HostProfile ValidateTarget(
        Guid profileId,
        string displayName,
        string address,
        HostAuthenticationMode authenticationMode,
        string? userName)
    {
        try
        {
            return HostProfileValidator.ValidateAndNormalize(new HostProfile(
                profileId,
                displayName,
                address,
                authenticationMode,
                userName));
        }
        catch (HostProfileValidationException ex)
        {
            throw new IntegrationOptionException(ex.Message);
        }
    }

    private static string Required(string name) =>
        Optional(name) ?? throw new IntegrationOptionException($"Environment variable {name} is required.");

    private static string? Optional(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? Secret(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        // Capture once and remove the process-level secret before any later validation can fail.
        Environment.SetEnvironmentVariable(name, null);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static TimeSpan Seconds(string name, int defaultValue, int minimum, int maximum)
    {
        string? value = Optional(name);
        if (value is null) return TimeSpan.FromSeconds(defaultValue);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            || seconds < minimum
            || seconds > maximum)
        {
            throw new IntegrationOptionException($"{name} must be an integer from {minimum} to {maximum}.");
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private static IReadOnlyList<uint> UIntList(string name, bool required)
    {
        string? value = Optional(name);
        if (value is null)
        {
            if (required) throw new IntegrationOptionException($"Environment variable {name} is required.");
            return [];
        }
        var result = new List<uint>();
        foreach (string item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!uint.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out uint number) || number == 0)
                throw new IntegrationOptionException($"{name} contains an invalid interface index: {item}.");
            result.Add(number);
        }
        if (required && result.Count == 0)
            throw new IntegrationOptionException($"Environment variable {name} must contain at least one interface index.");
        return result.Distinct().ToArray();
    }

    private static IReadOnlyList<string> CidrList(string name)
    {
        string value = Required(name);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Ipv4Cidr.TryNormalize(item, out string normalized, out string error))
                throw new IntegrationOptionException($"{name} contains an invalid IPv4 CIDR: {error}");
            if (!Ipv4Cidr.IsPrivate(normalized))
                throw new IntegrationOptionException($"{name} contains a CIDR outside RFC1918 private address space: {normalized}.");
            if (!seen.Add(normalized))
                throw new IntegrationOptionException($"{name} contains a duplicate IPv4 CIDR: {normalized}.");
            result.Add(normalized);
        }
        if (result.Count == 0)
            throw new IntegrationOptionException($"Environment variable {name} must contain at least one IPv4 CIDR.");
        return result;
    }

    private static string NormalizeReportPath(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
                throw new IntegrationOptionException("EXHYPERV_INTEGRATION_REPORT must name a .json file.");
            if (Directory.Exists(fullPath))
                throw new IntegrationOptionException("EXHYPERV_INTEGRATION_REPORT must name a file, not a directory.");
            return fullPath;
        }
        catch (IntegrationOptionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IntegrationOptionException("EXHYPERV_INTEGRATION_REPORT is not a valid file path.");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md"))
                && Directory.Exists(Path.Combine(current.FullName, "src")))
                return current.FullName;
            current = current.Parent;
        }
        return Environment.CurrentDirectory;
    }
}

internal sealed class IntegrationOptionException(string message) : Exception(message);
