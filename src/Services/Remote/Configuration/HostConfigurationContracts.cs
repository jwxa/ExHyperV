using ExHyperV.Services.Remote.Diagnostics;

namespace ExHyperV.Services.Remote.Configuration;

public interface IHostConfigurationCommandRunner
{
    Task<HostConfigurationCommandResult> RunAsync(
        string address,
        ResolvedHostIdentity identity,
        HostConfigurationCommand command,
        CancellationToken cancellationToken);
}

public interface IHostRollbackScriptWriter
{
    Task VerifyAvailableAsync(CancellationToken cancellationToken);

    Task DeleteAsync(string path, CancellationToken cancellationToken);

    Task<string> WriteAsync(
        string hostName,
        string hostAddress,
        IReadOnlyList<HostConfigurationCommand> appliedCommands,
        string? existingPath,
        CancellationToken cancellationToken);
}
