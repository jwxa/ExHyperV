using ExHyperV.Services.Remote.Consoles;

namespace ExHyperV.Services.Remote.Sessions;

public sealed record HostDisconnectPrompt(
    HostId HostId,
    string HostDisplayName,
    int ConsoleCount,
    string Message);

public enum HostDisconnectWorkflowStatus
{
    Succeeded,
    Cancelled,
    Blocked,
    ConsoleCloseFailed,
    Failed
}

public sealed record HostDisconnectWorkflowResult(
    HostDisconnectWorkflowStatus Status,
    string Message,
    HostId HostId)
{
    public bool Succeeded => Status == HostDisconnectWorkflowStatus.Succeeded;
    public bool Cancelled => Status == HostDisconnectWorkflowStatus.Cancelled;
}

public sealed class HostDisconnectCoordinator
{
    private readonly IHostSessionRegistry _sessionRegistry;
    private readonly IHostConsoleRegistry _consoleRegistry;

    public HostDisconnectCoordinator(
        IHostSessionRegistry sessionRegistry,
        IHostConsoleRegistry consoleRegistry)
    {
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _consoleRegistry = consoleRegistry ?? throw new ArgumentNullException(nameof(consoleRegistry));
    }

    public async Task<HostDisconnectWorkflowResult> DisconnectAsync(
        HostId hostId,
        string hostDisplayName,
        Func<HostDisconnectPrompt, CancellationToken, Task<bool>> confirmAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostDisplayName);
        ArgumentNullException.ThrowIfNull(confirmAsync);

        HostDisconnectAvailability availability = _sessionRegistry.GetDisconnectAvailability(hostId);
        if (!availability.CanDisconnect)
            return Result(HostDisconnectWorkflowStatus.Blocked, availability.Reason, hostId);

        int consoleCount = _consoleRegistry.Count(hostId);
        if (consoleCount > 0)
        {
            var prompt = new HostDisconnectPrompt(
                hostId,
                hostDisplayName.Trim(),
                consoleCount,
                $"断开宿主“{hostDisplayName.Trim()}”将关闭该宿主的 {consoleCount} 个控制台窗口，"
                + "并从虚拟机列表移除该宿主的虚拟机。其他宿主和已保存的主机配置不会受影响。");
            bool confirmed = await confirmAsync(prompt, cancellationToken);
            if (!confirmed)
                return Result(HostDisconnectWorkflowStatus.Cancelled, "已取消断开，宿主和控制台保持不变。", hostId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessionRegistry.TryPrepareDisconnect(
                hostId,
                out IHostDisconnectPreparation? preparation,
                out string prepareReason))
            return Result(HostDisconnectWorkflowStatus.Blocked, prepareReason, hostId);

        using (preparation)
        {
            HostConsoleCloseResult closeResult = _consoleRegistry.CloseAll(hostId);
            if (!closeResult.Succeeded)
                return Result(HostDisconnectWorkflowStatus.ConsoleCloseFailed, closeResult.Message, hostId);

            HostDisconnectResult disconnectResult = preparation!.Commit();
            return Result(
                disconnectResult.Succeeded
                    ? HostDisconnectWorkflowStatus.Succeeded
                    : HostDisconnectWorkflowStatus.Failed,
                disconnectResult.Message,
                hostId);
        }
    }

    private static HostDisconnectWorkflowResult Result(
        HostDisconnectWorkflowStatus status,
        string message,
        HostId hostId) => new(status, message, hostId);
}
