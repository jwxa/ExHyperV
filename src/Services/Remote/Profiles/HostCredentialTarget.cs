namespace ExHyperV.Services.Remote.Profiles;

public static class HostCredentialTarget
{
    private const string Prefix = "ExHyperV/RemoteHost/";

    public static string ForProfile(Guid profileId)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("主机配置 ID 不能为空。", nameof(profileId));
        return Prefix + profileId.ToString("D");
    }
}
