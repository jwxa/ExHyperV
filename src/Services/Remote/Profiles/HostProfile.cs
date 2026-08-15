namespace ExHyperV.Services.Remote.Profiles;

public sealed record HostProfile(
    Guid Id,
    string DisplayName,
    string Address,
    HostAuthenticationMode AuthenticationMode = HostAuthenticationMode.CurrentWindowsIdentity,
    string? UserName = null,
    string? CredentialTarget = null);
