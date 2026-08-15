namespace ExHyperV.Services.Remote.Sessions;

public sealed class ActiveHostStateChangedEventArgs(
    ActiveHostCoordinatorSnapshot previous,
    ActiveHostCoordinatorSnapshot current) : EventArgs
{
    public ActiveHostCoordinatorSnapshot Previous { get; } = previous;
    public ActiveHostCoordinatorSnapshot Current { get; } = current;
}
