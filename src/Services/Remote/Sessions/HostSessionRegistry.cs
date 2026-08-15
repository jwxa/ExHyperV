namespace ExHyperV.Services.Remote.Sessions;

public sealed class HostSessionRegistry : IHostSessionRegistry
{
    private readonly object _sync = new();
    private readonly ActiveHostSessionCoordinator _localCoordinator = new();
    private readonly Func<ActiveHostSessionCoordinator> _coordinatorFactory;
    private readonly Dictionary<HostId, ActiveHostSessionCoordinator> _remoteCoordinators = [];
    private readonly List<HostId> _remoteOrder = [];
    private HostRegistrySnapshot _current = new([HostSessionSnapshot.CreateLocal()]);
    private bool _isShutdown;

    public HostSessionRegistry()
    {
        _coordinatorFactory = static () => new ActiveHostSessionCoordinator();
    }

    public HostSessionRegistry(
        IHostSessionConnector connector,
        IHostBasicSnapshotLoader snapshotLoader,
        IReconnectScheduler? reconnectScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(snapshotLoader);
        _coordinatorFactory = () => new ActiveHostSessionCoordinator(
            connector,
            snapshotLoader,
            reconnectScheduler);
    }

    public HostRegistrySnapshot Current
    {
        get
        {
            lock (_sync) return _current;
        }
    }

    public event EventHandler<HostRegistryChangedEventArgs>? Changed;

    public async Task<HostConnectResult> ConnectAsync(
        HostConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        HostId hostId = HostId.FromProfile(request.Profile);
        lock (_sync)
        {
            if (_isShutdown)
                return ResultLocked(HostConnectStatus.Shutdown, "应用正在关闭，不能连接远程宿主。", hostId);
            if (_remoteCoordinators.ContainsKey(hostId))
                return ResultLocked(HostConnectStatus.AlreadyConnected, "该远程宿主已连接。", hostId);
        }

        ActiveHostSessionCoordinator coordinator = _coordinatorFactory();
        coordinator.SelectProfile(request.Profile);
        HostSwitchResult switchResult = await coordinator.SwitchToSelectedAsync(
            request.ToSwitchRequest(),
            cancellationToken);
        if (!switchResult.Succeeded)
        {
            coordinator.Shutdown();
            HostConnectStatus status = switchResult.Status switch
            {
                HostSwitchStatus.Cancelled => HostConnectStatus.Cancelled,
                HostSwitchStatus.Shutdown => HostConnectStatus.Shutdown,
                _ => HostConnectStatus.Failed
            };
            lock (_sync) return ResultLocked(status, switchResult.Message, hostId);
        }

        HostRegistryChangedEventArgs? change = null;
        HostConnectResult result;
        bool discardCoordinator = false;
        lock (_sync)
        {
            if (_isShutdown)
            {
                discardCoordinator = true;
                result = ResultLocked(HostConnectStatus.Shutdown, "应用正在关闭，未加入远程宿主。", hostId);
            }
            else if (_remoteCoordinators.ContainsKey(hostId))
            {
                discardCoordinator = true;
                result = ResultLocked(HostConnectStatus.AlreadyConnected, "该远程宿主已连接。", hostId);
            }
            else
            {
                HostRegistrySnapshot previous = _current;
                _remoteCoordinators.Add(hostId, coordinator);
                _remoteOrder.Add(hostId);
                coordinator.StateChanged += (_, _) => OnCoordinatorChanged(hostId, coordinator);
                _current = BuildSnapshotLocked();
                change = new HostRegistryChangedEventArgs(hostId, previous, _current);
                result = ResultLocked(HostConnectStatus.Succeeded, "远程宿主连接成功。", hostId);
            }
        }

        if (discardCoordinator) coordinator.Shutdown();
        Publish(change);
        return result;
    }

    public HostOperationStamp CaptureOperationStamp(HostId hostId)
    {
        lock (_sync)
        {
            if (_isShutdown) throw new InvalidOperationException("应用正在关闭，不能捕获宿主操作代次。");
            if (hostId.IsLocal) return _localCoordinator.CaptureOperationStamp();
            if (!_remoteCoordinators.TryGetValue(hostId, out ActiveHostSessionCoordinator? coordinator))
                throw new KeyNotFoundException("指定宿主不在会话注册表中。");

            return coordinator.CaptureOperationStamp();
        }
    }

    public bool TryBeginWrite(
        HostId hostId,
        out IHostWriteLease? lease,
        out string reason)
    {
        ActiveHostSessionCoordinator? coordinator;
        lock (_sync)
        {
            if (_isShutdown)
            {
                lease = null;
                reason = "应用正在关闭，不能开始宿主写操作。";
                return false;
            }
            if (hostId.IsLocal)
                return _localCoordinator.TryBeginWrite(out lease, out reason);
            if (!_remoteCoordinators.TryGetValue(hostId, out coordinator))
            {
                lease = null;
                reason = "指定宿主未连接。";
                return false;
            }
        }

        return coordinator.TryBeginWrite(out lease, out reason);
    }

    public bool TryCaptureManagementOperation(
        HostId hostId,
        out HostManagementOperationContext? context,
        out string reason)
    {
        ActiveHostSessionCoordinator? coordinator;
        lock (_sync)
        {
            if (_isShutdown)
            {
                context = null;
                reason = "应用正在关闭，不能开始宿主操作。";
                return false;
            }
            if (hostId.IsLocal)
                return _localCoordinator.TryCaptureManagementOperation(out context, out reason);
            if (!_remoteCoordinators.TryGetValue(hostId, out coordinator))
            {
                context = null;
                reason = "指定宿主未连接。";
                return false;
            }
        }

        return coordinator.TryCaptureManagementOperation(out context, out reason);
    }

    public bool TryCaptureConsoleOperation(
        HostId hostId,
        out HostConsoleOperationContext? context,
        out string reason)
    {
        ActiveHostSessionCoordinator? coordinator;
        lock (_sync)
        {
            if (_isShutdown)
            {
                context = null;
                reason = "应用正在关闭，不能打开虚拟机控制台。";
                return false;
            }
            if (hostId.IsLocal)
                return _localCoordinator.TryCaptureConsoleOperation(out context, out reason);
            if (!_remoteCoordinators.TryGetValue(hostId, out coordinator))
            {
                context = null;
                reason = "指定宿主未连接。";
                return false;
            }
        }

        return coordinator.TryCaptureConsoleOperation(out context, out reason);
    }

    public bool CanApply(HostOperationStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ActiveHostSessionCoordinator? coordinator;
        lock (_sync)
        {
            if (_isShutdown) return false;
            if (stamp.ProfileId is null) return _localCoordinator.CanApply(stamp);
            if (!_remoteCoordinators.TryGetValue(HostId.FromProfileId(stamp.ProfileId.Value), out coordinator))
                return false;
        }

        return coordinator.CanApply(stamp);
    }

    public bool CanUseConsole(HostOperationStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ActiveHostSessionCoordinator? coordinator;
        lock (_sync)
        {
            if (_isShutdown) return false;
            if (stamp.ProfileId is null) return _localCoordinator.CanUseConsole(stamp);
            if (!_remoteCoordinators.TryGetValue(HostId.FromProfileId(stamp.ProfileId.Value), out coordinator))
                return false;
        }

        return coordinator.CanUseConsole(stamp);
    }

    public bool ReportConnectionLoss(HostOperationStamp stamp, string reason)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (stamp.ProfileId is null) return false;

        ActiveHostSessionCoordinator? coordinator;
        lock (_sync)
        {
            if (_isShutdown
                || !_remoteCoordinators.TryGetValue(HostId.FromProfileId(stamp.ProfileId.Value), out coordinator))
                return false;
        }

        return coordinator.ReportConnectionLoss(stamp, reason);
    }

    public bool RetryReconnectNow(HostId hostId)
    {
        ActiveHostSessionCoordinator? coordinator;
        lock (_sync)
        {
            if (_isShutdown
                || hostId.IsLocal
                || !_remoteCoordinators.TryGetValue(hostId, out coordinator))
                return false;
        }

        return coordinator.RetryReconnectNow();
    }

    public void StopReconnect(HostId hostId)
    {
        ActiveHostSessionCoordinator? coordinator;
        lock (_sync)
        {
            if (_isShutdown
                || hostId.IsLocal
                || !_remoteCoordinators.TryGetValue(hostId, out coordinator))
                return;
        }

        coordinator.StopReconnect();
    }

    public bool UpdateHostChannels(
        HostId hostId,
        HostChannelState managementChannel,
        HostChannelState consoleChannel,
        string? managementFailureReason = null)
    {
        if (hostId.IsLocal) return false;
        ActiveHostSessionCoordinator? coordinator;
        lock (_sync)
        {
            if (_isShutdown || !_remoteCoordinators.TryGetValue(hostId, out coordinator))
                return false;
        }

        return coordinator.UpdateActiveChannels(
            hostId.ProfileId,
            managementChannel,
            consoleChannel,
            managementFailureReason);
    }

    public void Shutdown()
    {
        ActiveHostSessionCoordinator[] coordinators;
        lock (_sync)
        {
            if (_isShutdown) return;
            _isShutdown = true;
            coordinators = _remoteCoordinators.Values.ToArray();
        }

        foreach (ActiveHostSessionCoordinator coordinator in coordinators)
            coordinator.Shutdown();
        _localCoordinator.Shutdown();
    }

    private HostRegistrySnapshot BuildSnapshotLocked()
    {
        var hosts = new List<HostSessionSnapshot>(_remoteOrder.Count + 1)
        {
            _current.Hosts[0]
        };
        hosts.AddRange(_remoteOrder.Select(hostId =>
            HostSessionSnapshot.FromActive(_remoteCoordinators[hostId].Current)));
        return new HostRegistrySnapshot(hosts);
    }

    private HostConnectResult ResultLocked(HostConnectStatus status, string message, HostId hostId) =>
        new(status, message, hostId, _current);

    private void OnCoordinatorChanged(HostId hostId, ActiveHostSessionCoordinator coordinator)
    {
        HostRegistryChangedEventArgs? change = null;
        lock (_sync)
        {
            if (_isShutdown
                || !_remoteCoordinators.TryGetValue(hostId, out ActiveHostSessionCoordinator? currentCoordinator)
                || !ReferenceEquals(currentCoordinator, coordinator))
                return;

            HostRegistrySnapshot previous = _current;
            _current = BuildSnapshotLocked();
            change = new HostRegistryChangedEventArgs(hostId, previous, _current);
        }
        Publish(change);
    }

    private void Publish(HostRegistryChangedEventArgs? change)
    {
        if (change is null) return;
        Delegate[] subscribers = Changed?.GetInvocationList() ?? [];
        foreach (Delegate subscriber in subscribers)
        {
            try
            {
                ((EventHandler<HostRegistryChangedEventArgs>)subscriber)(this, change);
            }
            catch
            {
                // A UI subscriber must not break the registry state transition.
            }
        }
    }
}
