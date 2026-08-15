using System.Text.RegularExpressions;
using System.Security;

namespace ExHyperV.Services.Logging;

public static partial class SensitiveDataRedactor
{
    public const string RedactedValue = "[REDACTED]";

    private static readonly string[] SensitiveKeyFragments =
    [
        "password", "passwd", "pwd", "token", "authorization", "credential", "secret",
        "accesskey", "privatekey", "connectionstring"
    ];

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;

        string redacted = AuthorizationHeaderRegex().Replace(
            value,
            match => $"{match.Groups["prefix"].Value}{RedactedValue}");

        redacted = JsonSecretRegex().Replace(
            redacted,
            match => $"\"{match.Groups["key"].Value}\":\"{RedactedValue}\"");

        redacted = KeyValueSecretRegex().Replace(
            redacted,
            match => $"{match.Groups["key"].Value}{match.Groups["separator"].Value}{RedactedValue}");

        return redacted;
    }

    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        string normalized = new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return SensitiveKeyFragments.Any(normalized.Contains);
    }

    public static bool IsSensitiveValue(object? value)
    {
        if (value is null) return false;
        if (value is SecureString) return true;

        string typeName = value.GetType().Name;
        return typeName.Contains("Credential", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Secret", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        "\\\"(?<key>password|passwd|pwd|token|access_token|refresh_token|authorization|credential|client_secret|secret|access_key|private_key|connection_string)\\\"\\s*:\\s*\\\"(?:\\\\.|[^\\\"])*\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex(
        "(?<key>password|passwd|pwd|token|access_token|refresh_token|authorization|credential|client_secret|secret|access_key|private_key|connection_string)(?<separator>\\s*[:=]\\s*)(?:\\\"(?:\\\\.|[^\\\"])*\\\"|'[^']*'|[^,;\\r\\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(
        "(?<prefix>\\b(?:Bearer|Basic|Digest|Negotiate)\\s+)[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();
}
