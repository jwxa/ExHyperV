namespace ExHyperV.Services.Logging;

public sealed class RollingFileLoggerOptions
{
    public const long DefaultMaxFileBytes = 100L * 1024 * 1024;

    public required string LogDirectory { get; init; }
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.Now;
}
