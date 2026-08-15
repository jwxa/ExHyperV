using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.Services.Remote.Consoles;

public sealed record ActiveHostConsoleSession(
    HostTarget Target,
    HostOperationStamp Stamp,
    Guid VmId,
    string VmName,
    string Server,
    int Port,
    string WindowKey);

public sealed record HostConsoleSessionCapture(
    ActiveHostConsoleSession? Session,
    string Message)
{
    public bool Succeeded => Session is not null;

    public static HostConsoleSessionCapture Success(ActiveHostConsoleSession session) =>
        new(session, string.Empty);

    public static HostConsoleSessionCapture Failure(string message) =>
        new(null, message);
}

public sealed class ActiveHostConsoleSessions(IActiveHostSessionCoordinator coordinator)
{
    public const int ConsolePort = 2179;

    private readonly IActiveHostSessionCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public HostConsoleSessionCapture Capture(string vmId, string vmName)
    {
        if (!Guid.TryParse(vmId, out Guid parsedVmId))
            return HostConsoleSessionCapture.Failure("虚拟机标识无效，无法打开控制台。");
        if (string.IsNullOrWhiteSpace(vmName))
            return HostConsoleSessionCapture.Failure("虚拟机名称为空，无法打开控制台。");
        if (!_coordinator.TryCaptureConsoleOperation(out HostConsoleOperationContext? operation, out string reason))
            return HostConsoleSessionCapture.Failure(reason);

        string server = operation!.Target.IsLocal ? "localhost" : operation.Target.Address;
        string profileScope = operation.Stamp.ProfileId?.ToString("N") ?? "local";
        string windowKey = $"{operation.Stamp.Generation}:{profileScope}:{parsedVmId:N}";
        return HostConsoleSessionCapture.Success(new ActiveHostConsoleSession(
            operation.Target,
            operation.Stamp,
            parsedVmId,
            vmName.Trim(),
            server,
            ConsolePort,
            windowKey));
    }

    public bool IsCurrent(ActiveHostConsoleSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _coordinator.CanUseConsole(session.Stamp);
    }
}
