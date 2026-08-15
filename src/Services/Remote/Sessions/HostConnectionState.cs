namespace ExHyperV.Services.Remote.Sessions;

public enum HostConnectionState
{
    LocalConnected,
    RemoteDisconnected,
    Connecting,
    Connected,
    PartiallyAvailable,
    Reconnecting,
    Failed
}
