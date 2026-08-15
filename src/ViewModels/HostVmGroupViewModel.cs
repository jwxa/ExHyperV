using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.ViewModels;

public partial class HostVmGroupViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _operationCancellation = new();
    private int _isDisposed;

    public HostVmGroupViewModel(HostSessionSnapshot session, int order)
    {
        ArgumentNullException.ThrowIfNull(session);
        HostId = session.HostId;
        Order = order;
        ApplySession(session);
    }

    internal static IReadOnlyList<HostVmGroupViewModel> CreateOrdered(HostRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Array.AsReadOnly(snapshot.Hosts
            .Select((session, order) => new HostVmGroupViewModel(session, order))
            .ToArray());
    }

    public HostId HostId { get; }
    public int Order { get; }
    public bool IsLocal => HostId.IsLocal;
    public CancellationToken OperationToken => _operationCancellation.Token;
    public ObservableCollection<VmInstanceViewModel> Vms { get; } = [];

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _displayAddress = string.Empty;
    [ObservableProperty] private HostConnectionState _connectionState;
    [ObservableProperty] private HostChannelState _managementChannel;
    [ObservableProperty] private bool _hasStaleData;
    [ObservableProperty] private HostCapabilityMatrix _capabilities =
        HostCapabilityMatrix.Create(ActiveHostSession.CreateLocal(), isSwitching: false);
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _loadError = string.Empty;
    public HostReconnectState Reconnect { get; private set; } = HostReconnectState.None;

    public bool IsHealthy =>
        ManagementChannel == HostChannelState.Available && !HasStaleData;

    public bool IsWarning =>
        !IsHealthy && (HasStaleData || ConnectionState == HostConnectionState.Reconnecting);

    public bool IsUnavailable => !IsHealthy && !IsWarning;

    public string StatusText
    {
        get
        {
            if (IsLocal) return "本地计算机已连接。";
            if (HasStaleData)
            {
                if (!Reconnect.IsActive)
                    return "自动重连已停止；当前显示断线前的旧数据。";
                if (Reconnect.NextAttemptAt is { } nextAttempt)
                    return $"第 {Reconnect.Attempt} 次重连失败；将在 {nextAttempt.ToLocalTime():HH:mm:ss} 再次尝试。当前显示断线前的旧数据。";
                return "连接已中断，正在自动重连；当前显示断线前的旧数据。";
            }

            return ConnectionState switch
            {
                HostConnectionState.Connected => "远程宿主已连接。",
                HostConnectionState.PartiallyAvailable =>
                    Capabilities[HostCapabilityKind.VmConsole].CanExecute
                        ? "远程宿主部分可用。"
                        : Capabilities[HostCapabilityKind.VmConsole].Reason,
                HostConnectionState.RemoteDisconnected => "远程宿主已断开。",
                HostConnectionState.Reconnecting => "正在重新连接远程宿主。",
                HostConnectionState.Failed => "远程宿主连接失败。",
                _ => "远程宿主状态未知。"
            };
        }
    }

    public void ApplySession(HostSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.HostId != HostId)
            throw new ArgumentException("宿主快照与 VM 分组身份不一致。", nameof(session));

        DisplayName = session.Target.DisplayName;
        DisplayAddress = session.Target.IsLocal ? Environment.MachineName : session.Target.Address;
        ConnectionState = session.ConnectionState;
        ManagementChannel = session.ManagementChannel;
        HasStaleData = session.HasStaleData;
        Capabilities = session.Capabilities;
        Reconnect = session.Reconnect;
        OnPropertyChanged(nameof(IsHealthy));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(StatusText));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
        _operationCancellation.Cancel();
        _operationCancellation.Dispose();
    }
}
