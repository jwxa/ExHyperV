using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.ViewModels;

public partial class HostProfileEditorViewModel : ObservableObject
{
    private readonly HostProfile? _original;

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private HostAuthenticationMode _authenticationMode;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _rememberCredential;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public HostProfileEditorViewModel(HostProfile? profile = null)
    {
        _original = profile;
        DisplayName = profile?.DisplayName ?? string.Empty;
        Address = profile?.Address ?? string.Empty;
        AuthenticationMode = profile?.AuthenticationMode ?? HostAuthenticationMode.CurrentWindowsIdentity;
        UserName = profile?.UserName ?? string.Empty;
        RememberCredential = profile?.CredentialTarget is not null;
    }

    public bool IsEditing => _original is not null;
    public bool UsesExplicitCredential => AuthenticationMode == HostAuthenticationMode.ExplicitCredential;
    public bool HasRememberedCredential => _original?.CredentialTarget is not null;
    public string PasswordHint => HasRememberedCredential
        ? "已保存密码不会回显。留空可继续使用；输入新密码将替换原凭据。"
        : "密码只用于本次会话；勾选“记住凭据”后保存到 Windows 凭据管理器。";

    partial void OnAuthenticationModeChanged(HostAuthenticationMode value)
    {
        OnPropertyChanged(nameof(UsesExplicitCredential));
        ErrorMessage = string.Empty;
        if (value == HostAuthenticationMode.CurrentWindowsIdentity)
        {
            UserName = string.Empty;
            Password = string.Empty;
            RememberCredential = false;
        }
    }

    partial void OnRememberCredentialChanged(bool value) =>
        OnPropertyChanged(nameof(PasswordHint));

    public bool TryBuild(out HostProfile? profile, out WindowsCredential? suppliedCredential)
    {
        profile = null;
        suppliedCredential = null;
        ErrorMessage = string.Empty;
        Guid id = _original?.Id ?? Guid.NewGuid();

        if (AuthenticationMode == HostAuthenticationMode.ExplicitCredential)
        {
            string userName = UserName.Trim();
            if (userName.Length == 0)
            {
                ErrorMessage = "请输入本地账户或域账户用户名。";
                return false;
            }

            bool keepsRememberedCredential = RememberCredential
                && HasRememberedCredential
                && string.Equals(userName, _original!.UserName, StringComparison.Ordinal)
                && string.IsNullOrEmpty(Password);
            if (string.IsNullOrEmpty(Password) && !keepsRememberedCredential)
            {
                ErrorMessage = RememberCredential
                    ? "请输入要保存到 Windows 凭据管理器的密码。"
                    : "请输入本次连接诊断使用的密码。";
                return false;
            }

            if (!string.IsNullOrEmpty(Password))
                suppliedCredential = new WindowsCredential(userName, Password);

            profile = new HostProfile(
                id,
                DisplayName,
                Address,
                AuthenticationMode,
                userName,
                keepsRememberedCredential ? _original!.CredentialTarget : null);
        }
        else
        {
            profile = new HostProfile(id, DisplayName, Address);
        }

        try
        {
            profile = HostProfileValidator.ValidateAndNormalize(profile);
            return true;
        }
        catch (HostProfileValidationException ex)
        {
            ErrorMessage = ex.Message;
            profile = null;
            suppliedCredential = null;
            return false;
        }
    }
}
