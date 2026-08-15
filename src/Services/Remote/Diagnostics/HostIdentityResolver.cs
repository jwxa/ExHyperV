using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.Services.Remote.Diagnostics;

public sealed class HostIdentityResolver(IWindowsCredentialStore credentialStore) : IHostIdentityResolver
{
    public ResolvedHostIdentity Resolve(HostProfile profile, WindowsCredential? transientCredential)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.AuthenticationMode == HostAuthenticationMode.CurrentWindowsIdentity)
            return ResolvedHostIdentity.CurrentWindowsIdentity;

        WindowsCredential? credential = transientCredential;
        if (credential is null && profile.CredentialTarget is not null)
        {
            try
            {
                credentialStore.TryRead(profile.CredentialTarget, out credential);
            }
            catch (Exception ex)
            {
                throw new HostDiagnosticException(
                    HostDiagnosticErrorCode.CredentialMissing,
                    "无法从 Windows 凭据管理器读取此主机的凭据。",
                    ex);
            }
        }

        if (credential is null)
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.CredentialMissing,
                "未提供显式凭据，且此主机没有已记住的凭据。请重新输入用户名和密码。");

        if (!string.Equals(profile.UserName, credential.UserName, StringComparison.Ordinal))
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.CredentialMissing,
                "提供的凭据用户名与主机配置不一致，请重新输入凭据。");

        return ResolvedHostIdentity.Explicit(credential.UserName, credential.Password);
    }
}
