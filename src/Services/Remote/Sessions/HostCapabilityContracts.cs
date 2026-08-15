namespace ExHyperV.Services.Remote.Sessions;

public enum HostCapabilityKind
{
    VmRead,
    VmWrite,
    VmConsole,
    HostHardware,
    VmAdvancedSettings,
    LocalFileSystem,
    VirtualSwitch,
    PcieDevices,
    UsbPassthrough
}

public enum HostCapabilityState
{
    Available,
    ReadOnly,
    Unavailable
}

public enum HostCapabilityReasonCode
{
    None,
    ManagementChannelUnavailable,
    ConsoleChannelUnavailable,
    StaleData,
    HostSwitchInProgress,
    RemoteNotSupported
}

public sealed record HostCapability(
    HostCapabilityKind Kind,
    HostCapabilityState State,
    HostCapabilityReasonCode ReasonCode,
    string Reason)
{
    public bool CanView => State != HostCapabilityState.Unavailable;
    public bool CanExecute => State == HostCapabilityState.Available;
}

public sealed class HostCapabilityMatrix : IEquatable<HostCapabilityMatrix>
{
    private readonly IReadOnlyDictionary<HostCapabilityKind, HostCapability> _entries;

    private HostCapabilityMatrix(IEnumerable<HostCapability> entries)
    {
        _entries = entries.ToDictionary(entry => entry.Kind);
    }

    public bool IsRemoteHost { get; private init; }
    public HostCapability this[HostCapabilityKind kind] => _entries[kind];

    public bool Equals(HostCapabilityMatrix? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || IsRemoteHost != other.IsRemoteHost || _entries.Count != other._entries.Count)
            return false;

        foreach ((HostCapabilityKind kind, HostCapability capability) in _entries)
        {
            if (!other._entries.TryGetValue(kind, out HostCapability? otherCapability)
                || capability != otherCapability)
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is HostCapabilityMatrix other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsRemoteHost);
        foreach (HostCapabilityKind kind in Enum.GetValues<HostCapabilityKind>())
        {
            hash.Add(kind);
            if (_entries.TryGetValue(kind, out HostCapability? capability)) hash.Add(capability);
        }
        return hash.ToHashCode();
    }

    public static HostCapabilityMatrix Create(ActiveHostSession session, bool isSwitching)
    {
        ArgumentNullException.ThrowIfNull(session);
        var entries = new List<HostCapability>(9)
        {
            VmRead(session, isSwitching),
            VmWrite(session, isSwitching),
            Console(session, isSwitching)
        };

        foreach (HostCapabilityKind kind in new[]
                 {
                     HostCapabilityKind.HostHardware,
                     HostCapabilityKind.VmAdvancedSettings,
                     HostCapabilityKind.LocalFileSystem,
                     HostCapabilityKind.VirtualSwitch,
                     HostCapabilityKind.PcieDevices,
                     HostCapabilityKind.UsbPassthrough
                 })
        {
            entries.Add(LocalOnly(kind, session.Target.IsLocal, isSwitching));
        }

        return new HostCapabilityMatrix(entries) { IsRemoteHost = !session.Target.IsLocal };
    }

    private static HostCapability VmRead(ActiveHostSession session, bool isSwitching)
    {
        if (isSwitching)
            return ReadOnly(HostCapabilityKind.VmRead, HostCapabilityReasonCode.HostSwitchInProgress,
                "目标宿主正在连接，当前快照暂时只读。");
        if (session.HasStaleData)
            return ReadOnly(HostCapabilityKind.VmRead, HostCapabilityReasonCode.StaleData,
                "当前显示的是断线前的旧数据，重连成功后才会刷新。");
        if (session.ManagementChannel != HostChannelState.Available)
            return Unavailable(HostCapabilityKind.VmRead, HostCapabilityReasonCode.ManagementChannelUnavailable,
                "目标宿主的 WMI/DCOM 管理通道不可用，无法读取虚拟机数据。");
        return Available(HostCapabilityKind.VmRead);
    }

    private static HostCapability VmWrite(ActiveHostSession session, bool isSwitching)
    {
        if (isSwitching)
            return Unavailable(HostCapabilityKind.VmWrite, HostCapabilityReasonCode.HostSwitchInProgress,
                "目标宿主正在连接，暂时不能执行虚拟机写操作。");
        if (session.HasStaleData)
            return Unavailable(HostCapabilityKind.VmWrite, HostCapabilityReasonCode.StaleData,
                "目标宿主连接已中断，当前显示的是旧数据，写操作将在重连成功后恢复。");
        if (session.ManagementChannel != HostChannelState.Available)
            return Unavailable(HostCapabilityKind.VmWrite, HostCapabilityReasonCode.ManagementChannelUnavailable,
                "目标宿主的 WMI/DCOM 管理通道不可用，不能执行虚拟机写操作。");
        return Available(HostCapabilityKind.VmWrite);
    }

    private static HostCapability Console(ActiveHostSession session, bool isSwitching)
    {
        if (isSwitching)
            return Unavailable(HostCapabilityKind.VmConsole, HostCapabilityReasonCode.HostSwitchInProgress,
                "目标宿主正在连接，暂时不能打开虚拟机控制台。");
        if (session.HasStaleData)
            return Unavailable(HostCapabilityKind.VmConsole, HostCapabilityReasonCode.StaleData,
                "目标宿主连接已中断，控制台将在重连成功后恢复。");
        if (session.ConsoleChannel != HostChannelState.Available)
            return Unavailable(HostCapabilityKind.VmConsole, HostCapabilityReasonCode.ConsoleChannelUnavailable,
                "目标宿主的 TCP 2179 控制台通道不可用。");
        return Available(HostCapabilityKind.VmConsole);
    }

    private static HostCapability LocalOnly(HostCapabilityKind kind, bool isLocal, bool isSwitching)
    {
        if (isSwitching)
            return Unavailable(kind, HostCapabilityReasonCode.HostSwitchInProgress,
                "目标宿主正在连接，暂时不能使用本机专属功能。");
        if (!isLocal)
            return Unavailable(kind, HostCapabilityReasonCode.RemoteNotSupported, RemoteUnsupportedReason(kind));
        return Available(kind);
    }

    private static string RemoteUnsupportedReason(HostCapabilityKind kind) => kind switch
    {
        HostCapabilityKind.HostHardware => "此页面依赖本机硬件枚举、注册表或系统接口，远程宿主暂不支持。",
        HostCapabilityKind.VmAdvancedSettings => "第一阶段远程宿主仅支持虚拟机列表、四项生命周期操作和控制台，高级设置暂不支持。",
        HostCapabilityKind.LocalFileSystem => "此功能依赖应用所在计算机的本地文件或磁盘，不能用于远程宿主。",
        HostCapabilityKind.VirtualSwitch => "虚拟交换机页面尚未接入远程 WMI，当前仅支持本地宿主。",
        HostCapabilityKind.PcieDevices => "PCIe 设备枚举与直通依赖本机设备接口，远程宿主暂不支持。",
        HostCapabilityKind.UsbPassthrough => "USB 转发依赖本机设备和后台服务，远程宿主暂不支持。",
        _ => "此功能在远程宿主模式下暂不支持。"
    };

    private static HostCapability Available(HostCapabilityKind kind) =>
        new(kind, HostCapabilityState.Available, HostCapabilityReasonCode.None, string.Empty);

    private static HostCapability ReadOnly(
        HostCapabilityKind kind,
        HostCapabilityReasonCode reasonCode,
        string reason) => new(kind, HostCapabilityState.ReadOnly, reasonCode, reason);

    private static HostCapability Unavailable(
        HostCapabilityKind kind,
        HostCapabilityReasonCode reasonCode,
        string reason) => new(kind, HostCapabilityState.Unavailable, reasonCode, reason);
}
