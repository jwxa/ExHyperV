using System.Collections.ObjectModel;
using System.Globalization;
using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.Services.Logging;

public sealed record AppLogProperty(string Name, string Value);

public sealed record AppLogEntry
{
    private AppLogEntry(
        DateTimeOffset timestamp,
        HostId hostId,
        string host,
        AppLogLevel level,
        string source,
        string message,
        string errorCategory,
        long? sessionGeneration,
        IReadOnlyList<AppLogProperty> properties,
        string exceptionText)
    {
        Timestamp = timestamp;
        HostId = hostId;
        Host = host;
        Level = level;
        Source = source;
        Message = message;
        ErrorCategory = errorCategory;
        SessionGeneration = sessionGeneration;
        Properties = properties;
        ExceptionText = exceptionText;
    }

    public DateTimeOffset Timestamp { get; }
    public HostId HostId { get; }
    public string Host { get; }
    public AppLogLevel Level { get; }
    public string Source { get; }
    public string Message { get; }
    public string ErrorCategory { get; }
    public long? SessionGeneration { get; }
    public IReadOnlyList<AppLogProperty> Properties { get; }
    public string ExceptionText { get; }

    public static AppLogEntry Create(
        DateTimeOffset timestamp,
        AppLogLevel level,
        string source,
        string message,
        AppLogContext? context = null,
        Exception? exception = null)
    {
        HostId hostId = context?.HostId ?? HostId.Local;
        string host = string.IsNullOrWhiteSpace(context?.Host)
            ? hostId.IsLocal ? Environment.MachineName : hostId.ToString()
            : context.Host;
        var properties = new List<AppLogProperty>(context?.Properties?.Count ?? 0);

        if (context?.Properties is { Count: > 0 } sourceProperties)
        {
            foreach ((string key, object? value) in sourceProperties.OrderBy(
                pair => pair.Key,
                StringComparer.Ordinal))
            {
                bool sensitive = SensitiveDataRedactor.IsSensitiveKey(key)
                    || SensitiveDataRedactor.IsSensitiveValue(value);
                string safeName = sensitive
                    ? SensitiveDataRedactor.RedactedValue
                    : NormalizeSingleLine(SensitiveDataRedactor.Redact(key));
                string safeValue = sensitive
                    ? SensitiveDataRedactor.RedactedValue
                    : NormalizeSingleLine(SensitiveDataRedactor.Redact(
                        Convert.ToString(value, CultureInfo.InvariantCulture)));
                properties.Add(new AppLogProperty(safeName, safeValue));
            }
        }

        string errorCategory = string.IsNullOrWhiteSpace(context?.ErrorCategory)
            ? exception is null ? "None" : "Unexpected"
            : context.ErrorCategory;

        return new AppLogEntry(
            timestamp,
            hostId,
            NormalizeSingleLine(SensitiveDataRedactor.Redact(host)),
            level,
            NormalizeSingleLine(SensitiveDataRedactor.Redact(source)),
            NormalizeSingleLine(SensitiveDataRedactor.Redact(message)),
            NormalizeSingleLine(SensitiveDataRedactor.Redact(errorCategory)),
            context?.SessionGeneration,
            new ReadOnlyCollection<AppLogProperty>(properties),
            exception is null
                ? string.Empty
                : NormalizeSingleLine(SensitiveDataRedactor.Redact(exception.ToString())));
    }

    internal static string NormalizeSingleLine(string? value) =>
        (value ?? string.Empty)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
