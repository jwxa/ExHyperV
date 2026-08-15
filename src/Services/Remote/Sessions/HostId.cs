using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.Services.Remote.Sessions;

public readonly record struct HostId
{
    private HostId(Guid profileId)
    {
        ProfileId = profileId;
    }

    public static HostId Local => default;

    public Guid ProfileId { get; }

    public bool IsLocal => ProfileId == Guid.Empty;

    public static HostId FromProfile(HostProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return FromProfileId(profile.Id);
    }

    public static HostId FromProfileId(Guid profileId)
    {
        if (profileId == Guid.Empty)
            throw new ArgumentException("远程宿主配置 ID 不能为空。", nameof(profileId));

        return new HostId(profileId);
    }

    public override string ToString() => IsLocal ? "local" : ProfileId.ToString("D");
}
