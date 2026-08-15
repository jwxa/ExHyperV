namespace ExHyperV.Services.Remote.Sessions;

public sealed record HostReconnectState(
    bool IsActive,
    int Attempt,
    DateTimeOffset? NextAttemptAt,
    string LastError)
{
    public static HostReconnectState None { get; } = new(false, 0, null, string.Empty);

    public static HostReconnectState Starting(string error) => new(true, 0, null, error);

    public static HostReconnectState Waiting(
        int attempt,
        DateTimeOffset nextAttemptAt,
        string error) => new(true, attempt, nextAttemptAt, error);

    public static HostReconnectState Attempting(int attempt, string error) =>
        new(true, attempt, null, error);

    public static HostReconnectState Stopped(int attempt, string error) =>
        new(false, attempt, null, error);
}

public interface IReconnectScheduler
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemReconnectScheduler : IReconnectScheduler
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
