using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.Services.Remote.Sessions;

public sealed record ActiveHostCoordinatorSnapshot
{
    public ActiveHostCoordinatorSnapshot(
        ActiveHostSession activeSession,
        HostProfile? selectedProfile,
        HostBasicSnapshot? basicSnapshot = null,
        HostCapabilityMatrix? capabilities = null)
    {
        ActiveSession = activeSession ?? throw new ArgumentNullException(nameof(activeSession));
        SelectedProfile = selectedProfile;
        BasicSnapshot = basicSnapshot;
        Capabilities = capabilities ?? HostCapabilityMatrix.Create(activeSession, isSwitching: false);
    }

    public ActiveHostSession ActiveSession { get; init; }
    public HostProfile? SelectedProfile { get; init; }
    public HostBasicSnapshot? BasicSnapshot { get; init; }
    public HostReconnectState Reconnect { get; init; } = HostReconnectState.None;
    public HostCapabilityMatrix Capabilities { get; init; }
    public bool IsLocalSelected => SelectedProfile is null;
}
