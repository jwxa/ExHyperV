namespace ExHyperV.Services.Remote.Profiles;

public sealed class HostProfileValidationException(string message) : ArgumentException(message);
