using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.ViewModels;

public partial class HostVmGroupViewModel : ObservableObject
{
    public HostVmGroupViewModel(HostSessionSnapshot session, int order)
    {
        ArgumentNullException.ThrowIfNull(session);
        HostId = session.HostId;
        Order = order;
        ApplySession(session);
    }

    public HostId HostId { get; }
    public int Order { get; }
    public bool IsLocal => HostId.IsLocal;
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

    public bool IsHealthy =>
        ManagementChannel == HostChannelState.Available && !HasStaleData;

    public bool IsWarning =>
        !IsHealthy && (HasStaleData || ConnectionState == HostConnectionState.Reconnecting);

    public bool IsUnavailable => !IsHealthy && !IsWarning;

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
        OnPropertyChanged(nameof(IsHealthy));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsUnavailable));
    }
}
