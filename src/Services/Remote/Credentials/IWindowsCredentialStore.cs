namespace ExHyperV.Services.Remote.Credentials;

public interface IWindowsCredentialStore
{
    void Save(string target, WindowsCredential credential);
    bool TryRead(string target, out WindowsCredential? credential);
    bool Delete(string target);
}
