using ExHyperV.Services.Remote.Credentials;

namespace ExHyperV.Services.Remote.Profiles;

public sealed class HostProfileManager(
    HostProfileStore profileStore,
    IWindowsCredentialStore credentialStore)
{
    private readonly object _sync = new();

    public IReadOnlyList<HostProfile> GetAll()
    {
        lock (_sync) return profileStore.Load();
    }

    public HostProfile Save(HostProfile profile, WindowsCredential? credentialToRemember = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_sync)
        {
            HostProfile[] original = profileStore.Load().ToArray();
            HostProfile? previous = original.SingleOrDefault(item => item.Id == profile.Id);
            HostProfile prepared = PrepareForSave(profile, previous, credentialToRemember);
            HostProfile[] updated = previous is null
                ? [.. original, prepared]
                : original.Select(item => item.Id == prepared.Id ? prepared : item).ToArray();

            profileStore.Save(updated);
            try
            {
                UpdateCredential(previous, prepared, credentialToRemember);
                return prepared;
            }
            catch
            {
                RestoreProfiles(original);
                throw;
            }
        }
    }

    public bool Delete(Guid profileId, bool deleteRememberedCredential)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("主机配置 ID 不能为空。", nameof(profileId));
        lock (_sync)
        {
            HostProfile[] original = profileStore.Load().ToArray();
            HostProfile? profile = original.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return false;

            profileStore.Save(original.Where(item => item.Id != profileId));
            if (!deleteRememberedCredential || profile.CredentialTarget is null) return true;

            try
            {
                credentialStore.Delete(profile.CredentialTarget);
                return true;
            }
            catch
            {
                RestoreProfiles(original);
                throw;
            }
        }
    }

    private static HostProfile PrepareForSave(
        HostProfile profile,
        HostProfile? previous,
        WindowsCredential? credentialToRemember)
    {
        if (credentialToRemember is not null)
        {
            if (profile.AuthenticationMode != HostAuthenticationMode.ExplicitCredential)
                throw new HostProfileValidationException("只有显式凭据模式可以记住凭据。");
            profile = profile with
            {
                UserName = credentialToRemember.UserName,
                CredentialTarget = HostCredentialTarget.ForProfile(profile.Id)
            };
        }

        HostProfile prepared = HostProfileValidator.ValidateAndNormalize(profile);
        if (prepared.CredentialTarget is not null && credentialToRemember is null)
        {
            bool keepsExistingCredential = previous is not null
                && string.Equals(previous.CredentialTarget, prepared.CredentialTarget, StringComparison.Ordinal)
                && string.Equals(previous.UserName, prepared.UserName, StringComparison.Ordinal);
            if (!keepsExistingCredential)
                throw new HostProfileValidationException("新增或更换已记住的凭据时必须提供用户名和密码。");
        }
        return prepared;
    }

    private void UpdateCredential(
        HostProfile? previous,
        HostProfile prepared,
        WindowsCredential? credentialToRemember)
    {
        if (credentialToRemember is not null)
        {
            if (previous?.CredentialTarget is not null
                && !string.Equals(previous.CredentialTarget, prepared.CredentialTarget, StringComparison.Ordinal))
            {
                credentialStore.Delete(previous.CredentialTarget);
            }
            credentialStore.Save(prepared.CredentialTarget!, credentialToRemember with { UserName = prepared.UserName! });
            return;
        }

        if (previous?.CredentialTarget is not null
            && !string.Equals(previous.CredentialTarget, prepared.CredentialTarget, StringComparison.Ordinal))
        {
            credentialStore.Delete(previous.CredentialTarget);
        }
    }

    private void RestoreProfiles(IReadOnlyList<HostProfile> original)
    {
        try
        {
            profileStore.Save(original);
        }
        catch (Exception rollbackError)
        {
            throw new InvalidOperationException("凭据操作失败，并且无法恢复原主机配置。", rollbackError);
        }
    }
}
