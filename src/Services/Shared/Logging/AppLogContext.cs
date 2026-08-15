namespace ExHyperV.Services.Logging;

public sealed record AppLogContext(
    string? Host = null,
    long? SessionGeneration = null,
    IReadOnlyDictionary<string, object?>? Properties = null);
