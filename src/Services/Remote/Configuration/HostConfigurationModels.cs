using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Preflight;

namespace ExHyperV.Services.Remote.Configuration;

public static class HostConfigurationConfirmation
{
    public const string RequiredText = "确认";
    public static bool IsExact(string? value) =>
        string.Equals(value, RequiredText, StringComparison.Ordinal);
}

public sealed record HostConfigurationCommand(
    HostPreflightChangeKind Kind,
    string Title,
    string ApplyScript,
    string RollbackScript);

public sealed record HostConfigurationCommandResult(
    bool Succeeded,
    string Message,
    bool MayHaveApplied = false);

public sealed record HostConfigurationStepResult(
    HostPreflightChangeKind Kind,
    string Title,
    bool Succeeded,
    string Message);

public sealed record HostConfigurationReport(
    bool Started,
    bool Succeeded,
    bool StalePreview,
    IReadOnlyList<HostConfigurationStepResult> Steps,
    string? RollbackScriptPath,
    HostPreflightReport? Verification,
    HostDiagnosticReport? Diagnostic,
    IReadOnlyList<string> Logs);
