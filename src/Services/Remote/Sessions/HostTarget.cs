using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.Services.Remote.Sessions;

public sealed record HostTarget(
    Guid? ProfileId,
    string DisplayName,
    string Address,
    HostAuthenticationMode AuthenticationMode,
    string? UserName,
    string? CredentialTarget)
{
    public bool IsLocal => ProfileId is null;

    public static HostTarget Local { get; } = new(
        null,
        "本地计算机",
        ".",
        HostAuthenticationMode.CurrentWindowsIdentity,
        null,
        null);

    public static HostTarget FromProfile(HostProfile profile)
    {
        HostProfile normalized = HostProfileValidator.ValidateAndNormalize(profile);
        return new HostTarget(
            normalized.Id,
            normalized.DisplayName,
            normalized.Address,
            normalized.AuthenticationMode,
            normalized.UserName,
            normalized.CredentialTarget);
    }
}
