using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.Services.Remote.Consoles;

public sealed record HostConsoleSession(
    HostTarget Target,
    HostOperationStamp Stamp,
    Guid VmId,
    string VmName,
    string Server,
    int Port,
    string WindowKey)
{
    public HostId HostId => Stamp.ProfileId is { } profileId
        ? HostId.FromProfileId(profileId)
        : HostId.Local;

    public VmKey VmKey => new(HostId, VmId);
}

public sealed record HostConsoleSessionCapture(
    HostConsoleSession? Session,
    string Message)
{
    public bool Succeeded => Session is not null;

    public static HostConsoleSessionCapture Success(HostConsoleSession session) =>
        new(session, string.Empty);

    public static HostConsoleSessionCapture Failure(string message) =>
        new(null, message);
}

public sealed class HostConsoleSessions(IHostSessionRegistry registry)
{
    public const int ConsolePort = 2179;

    private readonly IHostSessionRegistry _registry =
        registry ?? throw new ArgumentNullException(nameof(registry));

    public HostConsoleSessionCapture Capture(HostId hostId, string vmId, string vmName)
    {
        if (!Guid.TryParse(vmId, out Guid parsedVmId))
            return HostConsoleSessionCapture.Failure("虚拟机标识无效，无法打开控制台。");
        if (string.IsNullOrWhiteSpace(vmName))
            return HostConsoleSessionCapture.Failure("虚拟机名称为空，无法打开控制台。");
        if (!_registry.TryCaptureConsoleOperation(hostId, out HostConsoleOperationContext? operation, out string reason))
            return HostConsoleSessionCapture.Failure(reason);

        string server = operation!.Target.IsLocal ? "localhost" : operation.Target.Address;
        string profileScope = operation.Stamp.ProfileId?.ToString("N") ?? "local";
        string windowKey = $"{operation.Stamp.Generation}:{profileScope}:{parsedVmId:N}";
        return HostConsoleSessionCapture.Success(new HostConsoleSession(
            operation.Target,
            operation.Stamp,
            parsedVmId,
            vmName.Trim(),
            server,
            ConsolePort,
            windowKey));
    }

    public bool IsCurrent(HostConsoleSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _registry.CanUseConsole(session.Stamp);
    }
}
