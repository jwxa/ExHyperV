using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.IntegrationTests;

internal sealed record DisconnectAcceptanceEvidence(
    bool StaleDataObserved,
    bool WriteBlockedWhileStale,
    string WriteBlockReason,
    bool StayedOnExpectedRemoteHost,
    bool BackoffGrowthObserved,
    bool BackoffCapRespected,
    IReadOnlyList<double> ScheduledDelaysSeconds,
    int MaximumReconnectAttempt,
    bool FreshGenerationObserved,
    bool SnapshotRefreshed,
    bool CapabilitiesRefreshed)
{
    public bool IsComplete =>
        StaleDataObserved
        && WriteBlockedWhileStale
        && StayedOnExpectedRemoteHost
        && BackoffGrowthObserved
        && BackoffCapRespected
        && FreshGenerationObserved
        && SnapshotRefreshed
        && CapabilitiesRefreshed;

    public string MissingSummary()
    {
        var missing = new List<string>();
        if (!StaleDataObserved) missing.Add("未观察到旧数据状态");
        if (!WriteBlockedWhileStale) missing.Add("未证明旧数据期间写入被拒绝");
        if (!StayedOnExpectedRemoteHost) missing.Add("断线期间活动宿主发生变化或切回本机");
        if (!BackoffGrowthObserved) missing.Add("未观察到至少两级递增重连退避");
        if (!BackoffCapRespected) missing.Add("观察到超过 30 秒上限的重连退避");
        if (!FreshGenerationObserved) missing.Add("未观察到新的远程会话代次");
        if (!SnapshotRefreshed) missing.Add("恢复后基础快照未刷新");
        if (!CapabilitiesRefreshed) missing.Add("恢复后能力未重新计算为可用状态");
        return string.Join("；", missing);
    }
}

internal sealed class DisconnectAcceptanceObserver(
    Guid expectedProfileId,
    long originalGeneration,
    DateTimeOffset originalSnapshotRefreshedAt)
{
    private const double MaximumBackoffSeconds = 30;
    private const double TimingToleranceSeconds = 0.75;
    private readonly object _sync = new();
    private readonly SortedDictionary<int, double> _scheduledDelays = [];
    private bool _staleDataObserved;
    private bool _writeBlockedWhileStale;
    private string _writeBlockReason = string.Empty;
    private bool _stayedOnExpectedRemoteHost = true;
    private int _maximumReconnectAttempt;
    private bool _freshGenerationObserved;
    private bool _snapshotRefreshed;
    private bool _capabilitiesRefreshed;

    public void Observe(ActiveHostCoordinatorSnapshot snapshot, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            ActiveHostSession session = snapshot.ActiveSession;
            if (session.Target.IsLocal || session.Target.ProfileId != expectedProfileId)
                _stayedOnExpectedRemoteHost = false;

            if (session.HasStaleData)
                _staleDataObserved = true;

            _maximumReconnectAttempt = Math.Max(_maximumReconnectAttempt, snapshot.Reconnect.Attempt);
            if (snapshot.Reconnect is { Attempt: > 0, NextAttemptAt: { } nextAttemptAt })
            {
                double remaining = Math.Max(0, (nextAttemptAt - observedAt).TotalSeconds);
                _scheduledDelays.TryAdd(snapshot.Reconnect.Attempt, remaining);
            }

            if (!session.HasStaleData
                && session.Generation > originalGeneration
                && session.Target.ProfileId == expectedProfileId)
            {
                _freshGenerationObserved = true;
                _snapshotRefreshed = snapshot.BasicSnapshot?.RefreshedAt > originalSnapshotRefreshedAt;
                _capabilitiesRefreshed = CapabilitiesMatchRecoveredSession(snapshot);
            }
        }
    }

    public void RecordWriteGate(bool blocked, string reason)
    {
        lock (_sync)
        {
            if (!blocked) return;
            _writeBlockedWhileStale = true;
            _writeBlockReason = reason ?? string.Empty;
        }
    }

    public DisconnectAcceptanceEvidence Capture()
    {
        lock (_sync)
        {
            double[] delays = _scheduledDelays.Values.ToArray();
            bool grows = delays.Length >= 2
                         && delays.Zip(delays.Skip(1), (left, right) => right + TimingToleranceSeconds >= left)
                             .All(nonDecreasing => nonDecreasing)
                         && delays.Zip(delays.Skip(1), (left, right) => right - left)
                             .Any(increase => increase > TimingToleranceSeconds);
            bool capped = delays.All(delay => delay <= MaximumBackoffSeconds + TimingToleranceSeconds);
            return new DisconnectAcceptanceEvidence(
                _staleDataObserved,
                _writeBlockedWhileStale,
                _writeBlockReason,
                _stayedOnExpectedRemoteHost,
                grows,
                capped,
                delays,
                _maximumReconnectAttempt,
                _freshGenerationObserved,
                _snapshotRefreshed,
                _capabilitiesRefreshed);
        }
    }

    private static bool CapabilitiesMatchRecoveredSession(ActiveHostCoordinatorSnapshot snapshot)
    {
        ActiveHostSession session = snapshot.ActiveSession;
        HostCapability read = snapshot.Capabilities[HostCapabilityKind.VmRead];
        HostCapability write = snapshot.Capabilities[HostCapabilityKind.VmWrite];
        HostCapability console = snapshot.Capabilities[HostCapabilityKind.VmConsole];
        bool consoleMatches = session.ConsoleChannel == HostChannelState.Available
            ? console.CanExecute
            : !console.CanExecute
              && console.ReasonCode == HostCapabilityReasonCode.ConsoleChannelUnavailable;
        return session.ManagementChannel == HostChannelState.Available
               && read.CanExecute
               && write.CanExecute
               && consoleMatches;
    }
}
