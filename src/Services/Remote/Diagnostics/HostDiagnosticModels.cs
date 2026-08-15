namespace ExHyperV.Services.Remote.Diagnostics;

public enum HostDiagnosticStepKind
{
    Ipv4Reachability,
    Identity,
    WmiDcom,
    Tcp2179
}

public enum HostDiagnosticStepStatus
{
    Succeeded,
    Failed,
    Skipped,
    Cancelled
}

public enum HostDiagnosticAvailability
{
    FullyAvailable,
    PartiallyAvailable,
    Unavailable,
    Cancelled
}

public enum HostDiagnosticErrorCode
{
    None,
    InvalidIpv4,
    Unreachable,
    CredentialMissing,
    InvalidCredential,
    AuthenticationFailed,
    AccessDenied,
    NamespaceUnavailable,
    ConnectionRefused,
    Timeout,
    NetworkError,
    Cancelled,
    Unexpected
}

public enum HostDiagnosticLogLevel
{
    Information,
    Warning,
    Error
}

public sealed record HostDiagnosticStepResult(
    HostDiagnosticStepKind Kind,
    HostDiagnosticStepStatus Status,
    TimeSpan Duration,
    string Explanation,
    HostDiagnosticErrorCode ErrorCode = HostDiagnosticErrorCode.None);

public sealed record HostDiagnosticLogEntry(
    DateTimeOffset Timestamp,
    HostDiagnosticStepKind? Step,
    HostDiagnosticLogLevel Level,
    string Message);

public sealed record HostDiagnosticReport(
    Guid ProfileId,
    string HostAddress,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    HostDiagnosticAvailability Availability,
    IReadOnlyList<HostDiagnosticStepResult> Steps,
    IReadOnlyList<HostDiagnosticLogEntry> LogEntries)
{
    public bool ManagementAvailable =>
        GetStep(HostDiagnosticStepKind.WmiDcom).Status == HostDiagnosticStepStatus.Succeeded;

    public bool ConsoleAvailable =>
        GetStep(HostDiagnosticStepKind.Tcp2179).Status == HostDiagnosticStepStatus.Succeeded;

    public HostDiagnosticStepResult GetStep(HostDiagnosticStepKind kind) =>
        Steps.Single(step => step.Kind == kind);
}

public sealed class HostDiagnosticException(
    HostDiagnosticErrorCode errorCode,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public HostDiagnosticErrorCode ErrorCode { get; } = errorCode;
}
