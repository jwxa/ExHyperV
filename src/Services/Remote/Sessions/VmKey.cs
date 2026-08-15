namespace ExHyperV.Services.Remote.Sessions;

public readonly record struct VmKey
{
    public VmKey(HostId hostId, Guid vmId)
    {
        if (vmId == Guid.Empty)
            throw new ArgumentException("虚拟机 ID 不能为空。", nameof(vmId));

        HostId = hostId;
        VmId = vmId;
    }

    public HostId HostId { get; }

    public Guid VmId { get; }
}
