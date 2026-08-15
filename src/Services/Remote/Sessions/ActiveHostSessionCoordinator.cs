using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.Services.Remote.Sessions;

public sealed class ActiveHostSessionCoordinator : IActiveHostSessionCoordinator
{
    private readonly object _sync = new();
    private readonly IHostSessionConnector _connector;
    private readonly IHostBasicSnapshotLoader _snapshotLoader;
    private readonly IReconnectScheduler _reconnectScheduler;
    private ActiveHostCoordinatorSnapshot _current = new(ActiveHostSession.CreateLocal(), null);
    private IHostSessionCandidate? _activeCandidate;
    private HostSwitchRequest? _activeRequest;
    private CancellationTokenSource? _reconnectCancellation;
    private CancellationTokenSource? _reconnectDelayCancellation;
    private Task? _reconnectTask;
    private int _activeWriteCount;
    private TaskCompletionSource? _writesDrained;
    private bool _isWriteFrozen;
    private bool _switchInProgress;
    private bool _reconnectAttemptInProgress;
    private bool _isShutdown;

    private static readonly TimeSpan[] ReconnectBackoff =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30)
    ];
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(1);

    public ActiveHostSessionCoordinator()
        : this(new UnconfiguredConnector(), new UnconfiguredSnapshotLoader())
    {
    }

    public ActiveHostSessionCoordinator(
        IHostSessionConnector connector,
        IHostBasicSnapshotLoader snapshotLoader,
        IReconnectScheduler? reconnectScheduler = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _snapshotLoader = snapshotLoader ?? throw new ArgumentNullException(nameof(snapshotLoader));
        _reconnectScheduler = reconnectScheduler ?? new SystemReconnectScheduler();
    }

    public ActiveHostCoordinatorSnapshot Current
    {
        get
        {
            lock (_sync) return _current;
        }
    }

    public bool IsWriteFrozen
    {
        get
        {
            lock (_sync) return _isShutdown || _isWriteFrozen || _current.ActiveSession.HasStaleData;
        }
    }

    public event EventHandler<ActiveHostStateChangedEventArgs>? StateChanged;

    public void SelectProfile(HostProfile? profile)
    {
        HostProfile? selected = profile is null
            ? null
            : HostProfileValidator.ValidateAndNormalize(profile);
        ActiveHostStateChangedEventArgs? change;
        lock (_sync)
        {
            if (_isShutdown) return;
            ActiveHostCoordinatorSnapshot next = _current with { SelectedProfile = selected };
            change = SetCurrentLocked(next);
        }
        Publish(change);
    }

    public void ResetToLocal() => SwitchToLocalAsync().GetAwaiter().GetResult();

    public async Task<HostSwitchResult> SwitchToSelectedAsync(
        HostSwitchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await StopReconnectAndWaitAsync();
        HostProfile profile = HostProfileValidator.ValidateAndNormalize(request.Profile);
        long originalGeneration;
        HostSwitchResult? blocked;
        ActiveHostStateChangedEventArgs? freezeChange;
        lock (_sync)
        {
            blocked = TryFreezeForSwitchLocked(profile.Id, out originalGeneration);
            freezeChange = blocked is null ? RefreshCapabilitiesLocked() : null;
        }
        if (blocked is not null) return blocked;
        Publish(freezeChange);

        IHostSessionCandidate? candidate = null;
        IHostSessionCandidate? previousCandidate = null;
        ActiveHostStateChangedEventArgs? change = null;
        bool committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppLog.Information("宿主切换", $"开始准备远程宿主 {profile.DisplayName}。", CreateLogContext(profile, originalGeneration));
            candidate = await _connector.ConnectAsync(request with { Profile = profile }, cancellationToken);
            ValidateCandidate(profile, candidate);
            HostBasicSnapshot snapshot = await _snapshotLoader.LoadAsync(candidate, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                if (_isShutdown)
                {
                    return ResultLocked(
                        HostSwitchStatus.Shutdown,
                        "应用正在关闭，未切换活动宿主。");
                }
                if (_current.ActiveSession.Generation != originalGeneration
                    || _current.SelectedProfile != profile)
                {
                    return ResultLocked(
                        HostSwitchStatus.StaleSelection,
                        "候选主机准备完成前选择、主机配置或活动会话已改变，未执行切换。");
                }

                long nextGeneration = checked(originalGeneration + 1);
                var session = new ActiveHostSession(
                    nextGeneration,
                    candidate.Target,
                    GetConnectionState(candidate.ManagementChannel, candidate.ConsoleChannel),
                    candidate.ManagementChannel,
                    candidate.ConsoleChannel,
                    HasStaleData: false);
                ActiveHostCoordinatorSnapshot next = _current with
                {
                    ActiveSession = session,
                    BasicSnapshot = snapshot,
                    Reconnect = HostReconnectState.None
                };
                previousCandidate = _activeCandidate;
                _activeCandidate = candidate;
                _activeRequest = request with { Profile = profile };
                candidate = null;
                _isWriteFrozen = false;
                _switchInProgress = false;
                committed = true;
                change = SetCurrentLocked(next);
            }

            Publish(change);
            if (previousCandidate is not null) await DisposeCandidateAsync(previousCandidate);
            AppLog.Information("宿主切换", $"远程宿主 {profile.DisplayName} 已原子激活。", CreateLogContext(profile, originalGeneration + 1));
            return new HostSwitchResult(HostSwitchStatus.Succeeded, "活动宿主切换成功。", Current);
        }
        catch (OperationCanceledException)
        {
            AppLog.Warning("宿主切换", $"切换到 {profile.DisplayName} 已取消。", CreateLogContext(profile, originalGeneration, "Cancelled"));
            return Result(HostSwitchStatus.Cancelled, "宿主切换已取消，原活动宿主保持不变。");
        }
        catch (Exception ex)
        {
            AppLog.Error("宿主切换", $"切换到 {profile.DisplayName} 失败，原活动宿主保持不变。", CreateLogContext(profile, originalGeneration, "ConnectionFailed"), ex);
            return Result(HostSwitchStatus.Failed, $"宿主切换失败：{SensitiveDataRedactor.Redact(ex.Message)}");
        }
        finally
        {
            ActiveHostStateChangedEventArgs? capabilityChange = null;
            if (!committed && candidate is not null) await DisposeCandidateAsync(candidate);
            if (!committed)
            {
                lock (_sync)
                {
                    if (!_isShutdown) _isWriteFrozen = false;
                    _switchInProgress = false;
                    capabilityChange = RefreshCapabilitiesLocked();
                }
            }
            Publish(capabilityChange);
        }
    }

    public async Task<HostSwitchResult> SwitchToLocalAsync(CancellationToken cancellationToken = default)
    {
        await StopReconnectAndWaitAsync();
        long originalGeneration;
        HostSwitchResult? blocked;
        ActiveHostStateChangedEventArgs? freezeChange;
        lock (_sync)
        {
            blocked = TryFreezeForSwitchLocked(expectedProfileId: null, out originalGeneration, requireSelectionMatch: false);
            freezeChange = blocked is null ? RefreshCapabilitiesLocked() : null;
        }
        if (blocked is not null) return blocked;
        Publish(freezeChange);

        IHostSessionCandidate? previousCandidate = null;
        ActiveHostStateChangedEventArgs? change = null;
        bool committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_isShutdown)
                    return ResultLocked(HostSwitchStatus.Shutdown, "应用正在关闭，未切换活动宿主。");
                if (_current.ActiveSession.Generation != originalGeneration)
                    return ResultLocked(HostSwitchStatus.StaleSelection, "活动会话已改变，未切回本机。");

                bool alreadyLocal = _current.ActiveSession.Target.IsLocal;
                long nextGeneration = alreadyLocal
                    ? originalGeneration
                    : checked(originalGeneration + 1);
                var next = new ActiveHostCoordinatorSnapshot(
                    ActiveHostSession.CreateLocal(nextGeneration),
                    selectedProfile: null,
                    basicSnapshot: null);
                previousCandidate = _activeCandidate;
                _activeCandidate = null;
                _activeRequest = null;
                _isWriteFrozen = false;
                _switchInProgress = false;
                committed = true;
                change = SetCurrentLocked(next);
            }

            Publish(change);
            if (previousCandidate is not null) await DisposeCandidateAsync(previousCandidate);
            AppLog.Information("宿主切换", "用户已切回本地计算机。", new AppLogContext(SessionGeneration: Current.ActiveSession.Generation));
            return new HostSwitchResult(HostSwitchStatus.Succeeded, "已切回本地计算机。", Current);
        }
        catch (OperationCanceledException)
        {
            return Result(HostSwitchStatus.Cancelled, "切回本机已取消，原活动宿主保持不变。");
        }
        finally
        {
            ActiveHostStateChangedEventArgs? capabilityChange = null;
            if (!committed)
            {
                lock (_sync)
                {
                    if (!_isShutdown) _isWriteFrozen = false;
                    _switchInProgress = false;
                    capabilityChange = RefreshCapabilitiesLocked();
                }
            }
            Publish(capabilityChange);
        }
    }

    public bool TryBeginWrite(out IHostWriteLease? lease, out string reason)
    {
        lock (_sync)
        {
            if (_isShutdown)
            {
                lease = null;
                reason = "应用正在关闭，不能开始新的写操作。";
                return false;
            }
            HostCapability capability = _current.Capabilities[HostCapabilityKind.VmWrite];
            if (!capability.CanExecute)
            {
                lease = null;
                reason = capability.Reason;
                return false;
            }

            _activeWriteCount++;
            lease = new HostWriteLease(this, CaptureOperationStampLocked());
            reason = string.Empty;
            return true;
        }
    }

    public bool TryCaptureManagementOperation(
        out HostManagementOperationContext? context,
        out string reason)
    {
        lock (_sync)
        {
            if (_isShutdown)
            {
                context = null;
                reason = "应用正在关闭，不能开始新的管理操作。";
                return false;
            }
            ActiveHostSession session = _current.ActiveSession;
            HostCapability capability = _current.Capabilities[HostCapabilityKind.VmRead];
            if (!capability.CanExecute)
            {
                context = null;
                reason = capability.Reason;
                return false;
            }

            IHostManagementConnection connection;
            if (session.Target.IsLocal)
            {
                connection = LocalHostManagementConnection.Instance;
            }
            else if (_activeCandidate is not null
                     && _activeCandidate.Target.ProfileId == session.Target.ProfileId)
            {
                connection = _activeCandidate.ManagementConnection;
            }
            else
            {
                context = null;
                reason = "活动远程宿主没有可用的管理连接。";
                return false;
            }

            context = new HostManagementOperationContext(
                session.Target,
                CaptureOperationStampLocked(),
                connection);
            reason = string.Empty;
            return true;
        }
    }

    public HostOperationStamp CaptureOperationStamp()
    {
        lock (_sync) return CaptureOperationStampLocked();
    }

    public bool TryCaptureConsoleOperation(
        out HostConsoleOperationContext? context,
        out string reason)
    {
        lock (_sync)
        {
            if (_isShutdown)
            {
                context = null;
                reason = "应用正在关闭，不能打开新的控制台。";
                return false;
            }
            ActiveHostSession session = _current.ActiveSession;
            HostCapability capability = _current.Capabilities[HostCapabilityKind.VmConsole];
            if (!capability.CanExecute)
            {
                context = null;
                reason = capability.Reason;
                return false;
            }

            context = new HostConsoleOperationContext(
                session.Target,
                CaptureOperationStampLocked());
            reason = string.Empty;
            return true;
        }
    }

    public bool CanApply(HostOperationStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        lock (_sync)
        {
            return !_isShutdown
                   && stamp == CaptureOperationStampLocked()
                   && !_current.ActiveSession.HasStaleData;
        }
    }

    public bool CanUseConsole(HostOperationStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        lock (_sync)
        {
            return !_isShutdown
                   && stamp == CaptureOperationStampLocked()
                   && _current.Capabilities[HostCapabilityKind.VmConsole].CanExecute;
        }
    }

    public bool UpdateActiveChannels(
        Guid profileId,
        HostChannelState managementChannel,
        HostChannelState consoleChannel,
        string? managementFailureReason = null)
    {
        ActiveHostStateChangedEventArgs? change = null;
        HostOperationStamp? lossStamp = null;
        lock (_sync)
        {
            if (_isShutdown) return false;
            ActiveHostSession current = _current.ActiveSession;
            if (current.Target.IsLocal
                || current.Target.ProfileId != profileId
                || current.HasStaleData
                || _switchInProgress)
                return false;

            if (managementChannel != HostChannelState.Available && _activeRequest is not null)
            {
                lossStamp = CaptureOperationStampLocked();
            }
            else
            {
                HostConnectionState connectionState = managementChannel == HostChannelState.Available
                                                      && consoleChannel == HostChannelState.Available
                    ? HostConnectionState.Connected
                    : managementChannel == HostChannelState.Available
                      || consoleChannel == HostChannelState.Available
                        ? HostConnectionState.PartiallyAvailable
                        : HostConnectionState.Failed;
                change = SetCurrentLocked(_current with
                {
                    ActiveSession = current with
                    {
                        ConnectionState = connectionState,
                        ManagementChannel = managementChannel,
                        ConsoleChannel = consoleChannel
                    }
                });
            }
        }

        if (lossStamp is not null)
        {
            return ReportConnectionLoss(
                lossStamp,
                string.IsNullOrWhiteSpace(managementFailureReason)
                    ? "配置复检发现 WMI/DCOM 管理通道不可用。"
                    : managementFailureReason);
        }

        Publish(change);
        return true;
    }

    public bool ReportConnectionLoss(HostOperationStamp stamp, string reason)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        reason = string.IsNullOrWhiteSpace(reason)
            ? "远程宿主连接已中断。"
            : SensitiveDataRedactor.Redact(reason.Trim());

        ActiveHostStateChangedEventArgs? change;
        CancellationTokenSource cancellation;
        HostSwitchRequest request;
        lock (_sync)
        {
            if (_isShutdown) return false;
            ActiveHostSession session = _current.ActiveSession;
            if (session.Target.IsLocal
                || stamp != CaptureOperationStampLocked()
                || session.HasStaleData
                || _activeRequest is null
                || _activeRequest.Profile.Id != session.Target.ProfileId)
                return false;

            cancellation = new CancellationTokenSource();
            _reconnectCancellation = cancellation;
            request = _activeRequest with { RevalidateChannels = true };
            var staleSession = session with
            {
                ConnectionState = HostConnectionState.Reconnecting,
                HasStaleData = true
            };
            change = SetCurrentLocked(_current with
            {
                ActiveSession = staleSession,
                Reconnect = HostReconnectState.Starting(reason.Trim())
            });
        }

        Publish(change);
        AppLog.Warning(
            "自动重连",
            $"远程宿主连接中断，开始自动重连：{reason.Trim()}",
            CreateLogContext(request.Profile, stamp.Generation, "ConnectionLost"));
        StartReconnectLoop(request, stamp, cancellation);
        return true;
    }

    public bool RetryReconnectNow()
    {
        CancellationTokenSource? delayCancellation = null;
        CancellationTokenSource? reconnectCancellation = null;
        HostSwitchRequest? request = null;
        HostOperationStamp stamp = default!;
        ActiveHostStateChangedEventArgs? change = null;
        lock (_sync)
        {
            if (_isShutdown) return false;
            if (_current.Reconnect.IsActive)
            {
                if (_reconnectAttemptInProgress || _reconnectDelayCancellation is null)
                    return false;
                delayCancellation = _reconnectDelayCancellation;
                _reconnectDelayCancellation = null;
            }
            else if (_current.ActiveSession.HasStaleData
                     && !_current.ActiveSession.Target.IsLocal
                     && _activeRequest is not null
                     && _reconnectCancellation is null)
            {
                reconnectCancellation = new CancellationTokenSource();
                _reconnectCancellation = reconnectCancellation;
                request = _activeRequest with { RevalidateChannels = true };
                stamp = CaptureOperationStampLocked();
                change = SetCurrentLocked(_current with
                {
                    ActiveSession = _current.ActiveSession with { ConnectionState = HostConnectionState.Reconnecting },
                    Reconnect = HostReconnectState.Starting(_current.Reconnect.LastError)
                });
            }
            else
            {
                return false;
            }
        }

        if (delayCancellation is not null) delayCancellation.Cancel();
        if (reconnectCancellation is not null && request is not null)
        {
            Publish(change);
            StartReconnectLoop(request, stamp, reconnectCancellation);
        }
        return true;
    }

    public void StopReconnect()
    {
        CancellationTokenSource? reconnectCancellation;
        CancellationTokenSource? delayCancellation;
        ActiveHostStateChangedEventArgs? change = null;
        lock (_sync)
        {
            if (_isShutdown) return;
            reconnectCancellation = _reconnectCancellation;
            delayCancellation = _reconnectDelayCancellation;
            _reconnectDelayCancellation = null;
            _reconnectAttemptInProgress = false;
            if (_current.Reconnect.IsActive)
            {
                ActiveHostSession session = _current.ActiveSession with
                {
                    ConnectionState = HostConnectionState.RemoteDisconnected,
                    HasStaleData = true
                };
                change = SetCurrentLocked(_current with
                {
                    ActiveSession = session,
                    Reconnect = HostReconnectState.Stopped(
                        _current.Reconnect.Attempt,
                        _current.Reconnect.LastError)
                });
            }
        }
        reconnectCancellation?.Cancel();
        delayCancellation?.Cancel();
        Publish(change);
    }

    public void Shutdown()
    {
        Task? reconnectTask;
        IHostSessionCandidate? activeCandidate;
        CancellationTokenSource? reconnectCancellation;
        CancellationTokenSource? delayCancellation;
        lock (_sync)
        {
            if (_isShutdown) return;
            _isShutdown = true;
            _isWriteFrozen = true;
            reconnectTask = _reconnectTask;
            reconnectCancellation = _reconnectCancellation;
            delayCancellation = _reconnectDelayCancellation;
            _reconnectCancellation = null;
            _reconnectDelayCancellation = null;
            _reconnectTask = null;
            _reconnectAttemptInProgress = false;
            activeCandidate = _activeCandidate;
            _activeCandidate = null;
            _activeRequest = null;
        }

        reconnectCancellation?.Cancel();
        delayCancellation?.Cancel();
        if (reconnectTask is not null)
            WaitForShutdownTask(reconnectTask, "远程宿主重连任务未在退出等待时间内停止，应用将继续退出。");
        if (activeCandidate is not null)
            WaitForShutdownTask(
                DisposeCandidateAsync(activeCandidate),
                "活动远程宿主资源未在退出等待时间内释放，应用将继续退出。");
    }

    private static void WaitForShutdownTask(Task task, string timeoutMessage)
    {
        try
        {
            Task completed = Task.WhenAny(task, Task.Delay(ShutdownWaitTimeout)).GetAwaiter().GetResult();
            if (!ReferenceEquals(completed, task))
            {
                AppLog.Warning("应用退出", timeoutMessage);
                return;
            }
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warning("应用退出", "停止远程宿主后台任务时发生错误。", exception: ex);
        }
    }

    private void StartReconnectLoop(
        HostSwitchRequest request,
        HostOperationStamp stamp,
        CancellationTokenSource owner)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task task = RunReconnectAfterStartAsync(start.Task, request, stamp, owner);
        lock (_sync)
        {
            if (ReferenceEquals(_reconnectCancellation, owner))
                _reconnectTask = task;
        }
        start.SetResult();
        _ = task.ContinueWith(
            _ =>
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_reconnectCancellation, owner))
                        _reconnectCancellation = null;
                    if (ReferenceEquals(_reconnectTask, task))
                        _reconnectTask = null;
                }
                owner.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunReconnectAfterStartAsync(
        Task start,
        HostSwitchRequest request,
        HostOperationStamp stamp,
        CancellationTokenSource owner)
    {
        await start;
        await RunReconnectAsync(request, stamp, owner);
    }

    private async Task StopReconnectAndWaitAsync()
    {
        Task? reconnectTask;
        lock (_sync) reconnectTask = _reconnectTask;
        StopReconnect();
        if (reconnectTask is null) return;
        try { await reconnectTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warning("自动重连", "等待自动重连任务停止时发生错误。", exception: ex);
        }
    }

    private async Task RunReconnectAsync(
        HostSwitchRequest request,
        HostOperationStamp lostStamp,
        CancellationTokenSource owner)
    {
        try
        {
            await WaitForWritesToDrainAsync(owner.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        int attempt = 0;
        while (!owner.IsCancellationRequested)
        {
            attempt++;
            ActiveHostStateChangedEventArgs? attemptingChange;
            lock (_sync)
            {
                if (!OwnsReconnectLocked(owner, lostStamp)) return;
                _reconnectAttemptInProgress = true;
                attemptingChange = SetCurrentLocked(_current with
                {
                    Reconnect = HostReconnectState.Attempting(attempt, _current.Reconnect.LastError)
                });
            }
            Publish(attemptingChange);

            IHostSessionCandidate? candidate = null;
            try
            {
                AppLog.Information(
                    "自动重连",
                    $"开始第 {attempt} 次重连 {request.Profile.DisplayName}。",
                    CreateLogContext(request.Profile, lostStamp.Generation));
                candidate = await _connector.ConnectAsync(request, owner.Token);
                ValidateCandidate(request.Profile, candidate);
                HostBasicSnapshot snapshot = await _snapshotLoader.LoadAsync(candidate, owner.Token);
                owner.Token.ThrowIfCancellationRequested();

                IHostSessionCandidate? previousCandidate;
                ActiveHostStateChangedEventArgs? recoveredChange;
                lock (_sync)
                {
                    if (!OwnsReconnectLocked(owner, lostStamp)) return;
                    long generation = checked(lostStamp.Generation + 1);
                    var recovered = new ActiveHostSession(
                        generation,
                        candidate.Target,
                        GetConnectionState(candidate.ManagementChannel, candidate.ConsoleChannel),
                        candidate.ManagementChannel,
                        candidate.ConsoleChannel,
                        HasStaleData: false);
                    previousCandidate = _activeCandidate;
                    _activeCandidate = candidate;
                    candidate = null;
                    _activeRequest = request with
                    {
                        ManagementChannel = recovered.ManagementChannel,
                        ConsoleChannel = recovered.ConsoleChannel,
                        RevalidateChannels = false
                    };
                    _reconnectCancellation = null;
                    _reconnectDelayCancellation = null;
                    _reconnectAttemptInProgress = false;
                    recoveredChange = SetCurrentLocked(_current with
                    {
                        ActiveSession = recovered,
                        BasicSnapshot = snapshot,
                        Reconnect = HostReconnectState.None
                    });
                }

                Publish(recoveredChange);
                if (previousCandidate is not null) await DisposeCandidateAsync(previousCandidate);
                AppLog.Information(
                    "自动重连",
                    $"远程宿主 {request.Profile.DisplayName} 重连成功。",
                    CreateLogContext(request.Profile, lostStamp.Generation + 1));
                return;
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (candidate is not null)
                {
                    await DisposeCandidateAsync(candidate);
                    candidate = null;
                }

                TimeSpan delay = ReconnectBackoff[Math.Min(attempt - 1, ReconnectBackoff.Length - 1)];
                CancellationTokenSource delayCancellation;
                ActiveHostStateChangedEventArgs? waitingChange;
                lock (_sync)
                {
                    if (!OwnsReconnectLocked(owner, lostStamp)) return;
                    _reconnectAttemptInProgress = false;
                    delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
                    _reconnectDelayCancellation = delayCancellation;
                    waitingChange = SetCurrentLocked(_current with
                    {
                        Reconnect = HostReconnectState.Waiting(
                            attempt,
                            _reconnectScheduler.UtcNow.Add(delay),
                            SensitiveDataRedactor.Redact(ex.Message))
                    });
                }
                Publish(waitingChange);
                AppLog.Warning(
                    "自动重连",
                    $"第 {attempt} 次重连失败，{delay.TotalSeconds:F0} 秒后重试。",
                    CreateLogContext(request.Profile, lostStamp.Generation, "ReconnectFailed"),
                    ex);

                try
                {
                    await _reconnectScheduler.DelayAsync(delay, delayCancellation.Token);
                }
                catch (OperationCanceledException) when (!owner.IsCancellationRequested)
                {
                    // 用户要求立即重试，仅取消当前等待。
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_reconnectDelayCancellation, delayCancellation))
                            _reconnectDelayCancellation = null;
                    }
                    delayCancellation.Dispose();
                }
            }
            finally
            {
                if (candidate is not null) await DisposeCandidateAsync(candidate);
            }
        }
    }

    private bool OwnsReconnectLocked(CancellationTokenSource owner, HostOperationStamp lostStamp) =>
        !_isShutdown
        && ReferenceEquals(_reconnectCancellation, owner)
        && _current.Reconnect.IsActive
        && _current.ActiveSession.HasStaleData
        && CaptureOperationStampLocked() == lostStamp;

    internal void CommitActiveSession(ActiveHostSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ActiveHostStateChangedEventArgs? change;
        lock (_sync)
        {
            if (session.Generation <= _current.ActiveSession.Generation)
                throw new InvalidOperationException("新活动宿主会话必须使用更高的会话代次。");
            if (session.Target.IsLocal)
                throw new InvalidOperationException("返回本机必须使用 ResetToLocal。");
            if (_current.SelectedProfile?.Id != session.Target.ProfileId)
                throw new InvalidOperationException("新活动宿主会话必须对应当前选中的主机配置。");
            if (session.ConnectionState == HostConnectionState.LocalConnected)
                throw new InvalidOperationException("远程活动宿主不能使用本机连接状态。");
            change = SetCurrentLocked(_current with { ActiveSession = session });
        }
        Publish(change);
    }

    private HostSwitchResult? TryFreezeForSwitchLocked(
        Guid? expectedProfileId,
        out long originalGeneration,
        bool requireSelectionMatch = true)
    {
        originalGeneration = _current.ActiveSession.Generation;
        if (_isShutdown)
            return ResultLocked(HostSwitchStatus.Shutdown, "应用正在关闭，不能切换活动宿主。");
        if (_switchInProgress)
            return ResultLocked(HostSwitchStatus.SwitchInProgress, "已有宿主切换正在进行。" );
        if (_activeWriteCount > 0)
            return ResultLocked(HostSwitchStatus.BlockedByActiveWrites, $"当前有 {_activeWriteCount} 个写操作尚未完成，不能切换宿主。" );
        if (requireSelectionMatch && _current.SelectedProfile?.Id != expectedProfileId)
            return ResultLocked(HostSwitchStatus.NoSelection, "目标主机不是当前选中的配置，请重新选择后再切换。" );

        _switchInProgress = true;
        _isWriteFrozen = true;
        return null;
    }

    private static void ValidateCandidate(HostProfile profile, IHostSessionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Target.IsLocal || candidate.Target.ProfileId != profile.Id)
            throw new HostSwitchException("连接器返回了与目标配置不一致的候选会话。" );
        if (candidate.ManagementChannel != HostChannelState.Available)
            throw new HostSwitchException("WMI/DCOM 管理通道不可用，不能激活远程宿主。" );
    }

    private static HostConnectionState GetConnectionState(
        HostChannelState management,
        HostChannelState console) =>
        management == HostChannelState.Available && console == HostChannelState.Available
            ? HostConnectionState.Connected
            : HostConnectionState.PartiallyAvailable;

    private HostOperationStamp CaptureOperationStampLocked() =>
        new(_current.ActiveSession.Generation, _current.ActiveSession.Target.ProfileId);

    private void EndWrite()
    {
        TaskCompletionSource? writesDrained = null;
        lock (_sync)
        {
            if (_activeWriteCount > 0) _activeWriteCount--;
            if (_activeWriteCount == 0)
            {
                writesDrained = _writesDrained;
                _writesDrained = null;
            }
        }
        writesDrained?.TrySetResult();
    }

    private Task WaitForWritesToDrainAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_sync)
        {
            if (_activeWriteCount == 0) return Task.CompletedTask;
            _writesDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = _writesDrained.Task;
        }
        return task.WaitAsync(cancellationToken);
    }

    private HostSwitchResult Result(HostSwitchStatus status, string message)
    {
        lock (_sync) return ResultLocked(status, message);
    }

    private HostSwitchResult ResultLocked(HostSwitchStatus status, string message) =>
        new(status, message, _current);

    private ActiveHostStateChangedEventArgs? SetCurrentLocked(ActiveHostCoordinatorSnapshot next)
    {
        next = next with
        {
            Capabilities = HostCapabilityMatrix.Create(next.ActiveSession, _switchInProgress)
        };
        if (next == _current) return null;
        ActiveHostCoordinatorSnapshot previous = _current;
        _current = next;
        return new ActiveHostStateChangedEventArgs(previous, next);
    }

    private ActiveHostStateChangedEventArgs? RefreshCapabilitiesLocked() =>
        SetCurrentLocked(_current);

    private void Publish(ActiveHostStateChangedEventArgs? change)
    {
        if (change is null) return;
        foreach (EventHandler<ActiveHostStateChangedEventArgs> handler in
                 StateChanged?.GetInvocationList().Cast<EventHandler<ActiveHostStateChangedEventArgs>>()
                 ?? [])
        {
            try { handler(this, change); }
            catch { }
        }
    }

    private static AppLogContext CreateLogContext(
        HostProfile profile,
        long? generation,
        string? errorCategory = null) => new(
            Host: profile.Address,
            SessionGeneration: generation,
            HostId: HostId.FromProfile(profile),
            ErrorCategory: errorCategory);

    private static async Task DisposeCandidateAsync(IHostSessionCandidate candidate)
    {
        try { await candidate.DisposeAsync(); }
        catch (Exception ex) { AppLog.Warning("宿主切换", "释放旧宿主会话资源失败。", exception: ex); }
    }

    private sealed class HostWriteLease(ActiveHostSessionCoordinator owner, HostOperationStamp stamp) : IHostWriteLease
    {
        private ActiveHostSessionCoordinator? _owner = owner;
        public HostOperationStamp Stamp { get; } = stamp;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndWrite();
    }

    private sealed class UnconfiguredConnector : IHostSessionConnector
    {
        public Task<IHostSessionCandidate> ConnectAsync(HostSwitchRequest request, CancellationToken cancellationToken) =>
            throw new HostSwitchException("远程宿主连接器尚未配置。" );
    }

    private sealed class UnconfiguredSnapshotLoader : IHostBasicSnapshotLoader
    {
        public Task<HostBasicSnapshot> LoadAsync(IHostSessionCandidate candidate, CancellationToken cancellationToken) =>
            throw new HostSwitchException("远程宿主快照加载器尚未配置。" );
    }
}
