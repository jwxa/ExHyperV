using ExHyperV.Services.Remote.Diagnostics;

namespace ExHyperV.Services.Remote.Preflight;

public interface IHostPreflightReader
{
    Task<IHostPreflightReadSession> OpenAsync(
        string address,
        ResolvedHostIdentity identity,
        CancellationToken cancellationToken);
}

public interface IHostPreflightReadSession : IAsyncDisposable
{
    Task<HostJoinSnapshot> ReadJoinAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<HostLocalAccount>> ReadEnabledLocalAccountsAsync(CancellationToken cancellationToken);
    Task<HostLocalGroupSnapshot> ReadLocalGroupAsync(
        HostLocalGroupKind group,
        CancellationToken cancellationToken);
    Task<HostTokenFilterPolicyState> ReadTokenFilterPolicyAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<HostNetworkSnapshot>> ReadNetworksAsync(CancellationToken cancellationToken);
    Task<HostFirewallSnapshot> ReadFirewallAsync(CancellationToken cancellationToken);
}
