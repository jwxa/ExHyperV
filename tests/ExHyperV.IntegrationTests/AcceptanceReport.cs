using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExHyperV.Services.Logging;

namespace ExHyperV.IntegrationTests;

internal static class AcceptanceStatus
{
    public const string Passed = "通过";
    public const string Failed = "失败";
    public const string Skipped = "跳过";
    public const string Attention = "需关注";
    public const string Partial = "部分通过";
}

internal sealed record AcceptanceStageResult(
    string Name,
    string Status,
    long DurationMilliseconds,
    string Message,
    IReadOnlyDictionary<string, object?> Details);

internal sealed class AcceptanceReport
{
    private string _title = "ExHyperV 受控宿主集成验收报告";
    private string _hostAddress = string.Empty;
    private string _hostDisplayName = string.Empty;
    private string _authenticationMode = string.Empty;

    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Title { get => _title; init => _title = Safe(value); }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset FinishedAt { get; set; }
    public string HostAddress { get => _hostAddress; init => _hostAddress = Safe(value); }
    public string HostDisplayName { get => _hostDisplayName; init => _hostDisplayName = Safe(value); }
    public string AuthenticationMode { get => _authenticationMode; init => _authenticationMode = Safe(value); }
    public string OverallStatus { get; set; } = AcceptanceStatus.Failed;
    public List<AcceptanceStageResult> Stages { get; } = [];
    public IReadOnlyDictionary<string, bool> DangerousSwitches { get; init; } =
        new Dictionary<string, bool>();

    public bool HasFailures => Stages.Any(stage => stage.Status == AcceptanceStatus.Failed);

    public void Add(
        string name,
        string status,
        TimeSpan duration,
        string message,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        Stages.Add(new AcceptanceStageResult(
            name,
            status,
            Math.Max(0, (long)duration.TotalMilliseconds),
            Safe(message),
            Sanitize(details)));
    }

    public async Task<string> WriteAsync(string path, CancellationToken cancellationToken = default)
    {
        FinishedAt = DateTimeOffset.Now;
        OverallStatus = HasFailures
            ? AcceptanceStatus.Failed
            : Stages.Any(stage => stage.Status == AcceptanceStatus.Skipped)
                ? AcceptanceStatus.Partial
                : AcceptanceStatus.Passed;
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        try
        {
            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, this, options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
            return fullPath;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static IReadOnlyDictionary<string, object?> Sanitize(
        IReadOnlyDictionary<string, object?>? details)
    {
        if (details is null || details.Count == 0)
            return new Dictionary<string, object?>();

        return details.ToDictionary(
            item => item.Key,
            item => SensitiveDataRedactor.IsSensitiveKey(item.Key)
                ? SensitiveDataRedactor.RedactedValue
                : item.Value is string text ? Safe(text) : item.Value,
            StringComparer.Ordinal);
    }

    internal static string Safe(string? value) =>
        SensitiveDataRedactor.Redact(string.IsNullOrWhiteSpace(value) ? "未提供详细信息。" : value.Trim());
}
