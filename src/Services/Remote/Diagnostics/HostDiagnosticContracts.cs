using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.Services.Remote.Diagnostics;

public interface IIpv4ReachabilityProbe
{
    Task ProbeAsync(string address, CancellationToken cancellationToken);
}

public interface IHostIdentityResolver
{
    ResolvedHostIdentity Resolve(HostProfile profile, WindowsCredential? transientCredential);
}

public interface IExplicitCredentialValidator
{
    Task<ExplicitCredentialValidationResult> ValidateAsync(
        string address,
        ResolvedHostIdentity identity,
        CancellationToken cancellationToken);
}

public interface IWmiDcomProbe
{
    Task ProbeAsync(string address, ResolvedHostIdentity identity, CancellationToken cancellationToken);
}

public interface ITcpPortProbe
{
    Task ProbeAsync(string address, int port, CancellationToken cancellationToken);
}

public sealed record ResolvedHostIdentity(
    bool UsesCurrentWindowsIdentity,
    string? UserName = null,
    string? Password = null)
{
    public static ResolvedHostIdentity CurrentWindowsIdentity { get; } = new(true);

    public static ResolvedHostIdentity Explicit(string userName, string password) =>
        new(false, userName, password);
}

public enum ExplicitCredentialValidationStatus
{
    Valid,
    Invalid,
    Inconclusive
}

public sealed record ExplicitCredentialValidationResult(
    ExplicitCredentialValidationStatus Status,
    string Explanation);
