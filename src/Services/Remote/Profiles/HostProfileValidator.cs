using System.Globalization;
using System.Net;

namespace ExHyperV.Services.Remote.Profiles;

public static class HostProfileValidator
{
    public const int MaxDisplayNameLength = 100;

    public static HostProfile ValidateAndNormalize(HostProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty) throw new HostProfileValidationException("主机配置 ID 不能为空。");

        string displayName = profile.DisplayName.Trim();
        if (displayName.Length == 0) throw new HostProfileValidationException("主机配置名称不能为空。");
        if (displayName.Length > MaxDisplayNameLength)
            throw new HostProfileValidationException($"主机配置名称不能超过 {MaxDisplayNameLength} 个字符。");

        string[] parts = profile.Address.Trim().Split('.');
        if (parts.Length != 4
            || parts.Any(part => !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            throw new HostProfileValidationException("远程主机地址必须是有效的 IPv4 地址。");
        }

        byte[] addressBytes = parts
            .Select(part => byte.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture))
            .ToArray();
        var address = new IPAddress(addressBytes);
        if (addressBytes[0] is 0 or 127 or >= 224)
        {
            throw new HostProfileValidationException("远程主机地址必须是可连接的单播 IPv4 地址。");
        }

        string? userName = NullIfWhiteSpace(profile.UserName);
        string? credentialTarget = NullIfWhiteSpace(profile.CredentialTarget);
        switch (profile.AuthenticationMode)
        {
            case HostAuthenticationMode.CurrentWindowsIdentity when userName is not null || credentialTarget is not null:
                throw new HostProfileValidationException("当前 Windows 身份模式不能包含用户名或凭据引用。");
            case HostAuthenticationMode.ExplicitCredential when userName is null:
                throw new HostProfileValidationException("显式凭据模式必须包含用户名。");
            case HostAuthenticationMode.ExplicitCredential when credentialTarget is not null:
                if (!string.Equals(credentialTarget, HostCredentialTarget.ForProfile(profile.Id), StringComparison.Ordinal))
                    throw new HostProfileValidationException("已记住的显式凭据必须使用当前主机配置对应的凭据引用。");
                break;
            case not HostAuthenticationMode.CurrentWindowsIdentity and not HostAuthenticationMode.ExplicitCredential:
                throw new HostProfileValidationException("主机配置包含不支持的身份模式。");
        }

        return profile with
        {
            DisplayName = displayName,
            Address = address.ToString(),
            UserName = userName,
            CredentialTarget = credentialTarget
        };
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
