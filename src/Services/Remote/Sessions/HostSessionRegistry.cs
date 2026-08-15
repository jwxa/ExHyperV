namespace ExHyperV.Services.Remote.Sessions;

public sealed class HostSessionRegistry : IHostSessionRegistry
{
    private readonly object _sync = new();
    private readonly ActiveHostSessionCoordinator _localCoordinator = new();
    private readonly Func<ActiveHostSessionCoordinator> _coordinatorFactory;
    private readonly Dictionary<HostId, ActiveHostSessionCoordinator> _remoteCoordinators = [];
    private readonly List<HostId> _remoteOrder = [];
    private readonly Dictionary<HostId, int> _activeWriteCounts = [];
    private readonly Dictionary<HostId, Guid> _disconnectPreparations = [];
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
                _activeWriteCounts.Add(hostId, 0);
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
        HostRegistryChangedEventArgs? change = null;
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
            if (_disconnectPreparations.ContainsKey(hostId))
            {
                lease = null;
                reason = "宿主正在断开，不能开始新的写操作。";
                return false;
            }
            if (!_remoteCoordinators.TryGetValue(hostId, out ActiveHostSessionCoordinator? coordinator))
            {
                lease = null;
                reason = "指定宿主未连接。";
                return false;
            }
            if (!coordinator.TryBeginWrite(out IHostWriteLease? coordinatorLease, out reason))
            {
                lease = null;
                return false;
            }

            HostRegistrySnapshot previous = _current;
            _activeWriteCounts[hostId] = _activeWriteCounts.GetValueOrDefault(hostId) + 1;
            _current = BuildSnapshotLocked();
            change = new HostRegistryChangedEventArgs(hostId, previous, _current);
            lease = new RegistryWriteLease(this, hostId, coordinatorLease!);
        }
        Publish(change);
        return true;
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
            if (_disconnectPreparations.ContainsKey(hostId))
            {
                context = null;
                reason = "宿主正在断开，不能打开新的控制台。";
                return false;
            }
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

    public HostDisconnectAvailability GetDisconnectAvailability(HostId hostId)
    {
        lock (_sync) return GetDisconnectAvailabilityLocked(hostId);
    }

    public bool TryPrepareDisconnect(
        HostId hostId,
        out IHostDisconnectPreparation? preparation,
        out string reason)
    {
        lock (_sync)
        {
            HostDisconnectAvailability availability = GetDisconnectAvailabilityLocked(hostId);
            if (!availability.CanDisconnect)
            {
                preparation = null;
                reason = availability.Reason;
                return false;
            }

            Guid token = Guid.NewGuid();
            _disconnectPreparations.Add(hostId, token);
            preparation = new HostDisconnectPreparation(this, hostId, token);
            reason = string.Empty;
            return true;
        }
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
            HostSessionSnapshot.FromActive(_remoteCoordinators[hostId].Current) with
            {
                ActiveWriteCount = _activeWriteCounts.GetValueOrDefault(hostId)
            }));
        return new HostRegistrySnapshot(hosts);
    }

    private HostDisconnectAvailability GetDisconnectAvailabilityLocked(HostId hostId)
    {
        if (_isShutdown)
            return new HostDisconnectAvailability(false, 0, "应用正在关闭，不能断开宿主。");
        if (hostId.IsLocal)
            return new HostDisconnectAvailability(false, 0, "本机会话始终保留，不能断开。");
        if (!_remoteCoordinators.ContainsKey(hostId))
            return new HostDisconnectAvailability(false, 0, "指定宿主未连接。");
        if (_disconnectPreparations.ContainsKey(hostId))
            return new HostDisconnectAvailability(false, 0, "宿主正在断开，请稍候。");

        int activeWriteCount = _activeWriteCounts.GetValueOrDefault(hostId);
        return activeWriteCount > 0
            ? new HostDisconnectAvailability(
                false,
                activeWriteCount,
                $"当前有 {activeWriteCount} 个写操作尚未完成，暂时不能断开。")
            : new HostDisconnectAvailability(true, 0, string.Empty);
    }

    private HostDisconnectResult CommitDisconnect(HostId hostId, Guid token)
    {
        ActiveHostSessionCoordinator? coordinator = null;
        HostRegistryChangedEventArgs? change = null;
        HostDisconnectResult result;
        lock (_sync)
        {
            if (_isShutdown)
            {
                result = DisconnectResultLocked(
                    HostDisconnectStatus.Shutdown,
                    "应用正在关闭，未执行宿主断开。",
                    hostId);
            }
            else if (!_disconnectPreparations.TryGetValue(hostId, out Guid currentToken)
                     || currentToken != token)
            {
                result = DisconnectResultLocked(
                    HostDisconnectStatus.InvalidPreparation,
                    "断开准备已失效，请重试。",
                    hostId);
            }
            else if (!_remoteCoordinators.Remove(hostId, out coordinator))
            {
                _disconnectPreparations.Remove(hostId);
                result = DisconnectResultLocked(
                    HostDisconnectStatus.NotConnected,
                    "指定宿主已断开。",
                    hostId);
            }
            else
            {
                HostRegistrySnapshot previous = _current;
                _remoteOrder.Remove(hostId);
                _activeWriteCounts.Remove(hostId);
                _disconnectPreparations.Remove(hostId);
                _current = BuildSnapshotLocked();
                change = new HostRegistryChangedEventArgs(hostId, previous, _current);
                result = DisconnectResultLocked(
                    HostDisconnectStatus.Succeeded,
                    "远程宿主已断开，保存的主机配置仍然保留。",
                    hostId);
            }
        }

        coordinator?.Shutdown();
        Publish(change);
        return result;
    }

    private void CancelDisconnect(HostId hostId, Guid token)
    {
        lock (_sync)
        {
            if (_disconnectPreparations.TryGetValue(hostId, out Guid currentToken)
                && currentToken == token)
                _disconnectPreparations.Remove(hostId);
        }
    }

    private void EndWrite(HostId hostId, IHostWriteLease coordinatorLease)
    {
        coordinatorLease.Dispose();
        HostRegistryChangedEventArgs? change = null;
        lock (_sync)
        {
            if (!_activeWriteCounts.TryGetValue(hostId, out int count) || count <= 0) return;
            _activeWriteCounts[hostId] = count - 1;
            if (_isShutdown || !_remoteCoordinators.ContainsKey(hostId)) return;

            HostRegistrySnapshot previous = _current;
            _current = BuildSnapshotLocked();
            change = new HostRegistryChangedEventArgs(hostId, previous, _current);
        }
        Publish(change);
    }

    private HostConnectResult ResultLocked(HostConnectStatus status, string message, HostId hostId) =>
        new(status, message, hostId, _current);

    private HostDisconnectResult DisconnectResultLocked(
        HostDisconnectStatus status,
        string message,
        HostId hostId) => new(status, message, hostId, _current);

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

    private sealed class RegistryWriteLease : IHostWriteLease
    {
        private LeaseState? _state;

        public RegistryWriteLease(
            HostSessionRegistry owner,
            HostId hostId,
            IHostWriteLease coordinatorLease)
        {
            _state = new LeaseState(owner, hostId, coordinatorLease);
            Stamp = coordinatorLease.Stamp;
        }

        public HostOperationStamp Stamp { get; }

        public void Dispose() => Interlocked.Exchange(ref _state, null)?.Dispose();

        private sealed class LeaseState(
            HostSessionRegistry owner,
            HostId hostId,
            IHostWriteLease coordinatorLease)
        {
            public void Dispose() => owner.EndWrite(hostId, coordinatorLease);
        }
    }

    private sealed class HostDisconnectPreparation : IHostDisconnectPreparation
    {
        private readonly HostSessionRegistry _registry;
        private readonly Guid _token;
        private HostSessionRegistry? _owner;

        public HostDisconnectPreparation(HostSessionRegistry owner, HostId hostId, Guid token)
        {
            _registry = owner;
            _owner = owner;
            HostId = hostId;
            _token = token;
        }

        public HostId HostId { get; }

        public HostDisconnectResult Commit()
        {
            HostSessionRegistry? currentOwner = Interlocked.Exchange(ref _owner, null);
            if (currentOwner is not null) return currentOwner.CommitDisconnect(HostId, _token);

            lock (_registry._sync)
            {
                return _registry.DisconnectResultLocked(
                    HostDisconnectStatus.InvalidPreparation,
                    "断开准备已失效，请重试。",
                    HostId);
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.CancelDisconnect(HostId, _token);
        }
    }
}
