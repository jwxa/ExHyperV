using ExHyperV.Services.Remote.Consoles;
using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Configuration;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Preflight;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Services.Remote.Vms;
using ExHyperV.Tools;

internal static class RemoteHostEndToEndTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("EndToEnd_CurrentIdentityPartialConsoleReconnectFlow", CurrentIdentityPartialConsoleReconnectFlow),
        ("EndToEnd_MultipleProfilesKeepSecretsOutOfConfiguration", MultipleProfilesKeepSecretsOutOfConfiguration),
        ("EndToEnd_PublicRemoteErrorsAreRedacted", PublicRemoteErrorsAreRedacted),
        ("EndToEnd_InitialConnectRequestsConsoleRevalidation", InitialConnectRequestsConsoleRevalidation)
    ];

    private static void InitialConnectRequestsConsoleRevalidation()
    {
        var profile = new HostProfile(Guid.NewGuid(), "复检宿主", "10.0.0.6");
        var credential = new WindowsCredential("LAB\\Operator", "transient-secret");

        HostConnectRequest available = HostConnectRequest.ForConfirmedDiagnostic(
            profile,
            consoleAvailable: true,
            credential);
        TestAssert.Equal(profile, available.Profile);
        TestAssert.Equal(HostChannelState.Available, available.ManagementChannel);
        TestAssert.Equal(HostChannelState.Available, available.ConsoleChannel);
        TestAssert.Equal(credential, available.TransientCredential);
        TestAssert.True(available.RevalidateChannels, "Initial connection did not request a fresh TCP 2179 probe.");

        HostConnectRequest unavailable = HostConnectRequest.ForConfirmedDiagnostic(
            profile,
            consoleAvailable: false);
        TestAssert.Equal(HostChannelState.Unavailable, unavailable.ConsoleChannel);
        TestAssert.True(unavailable.RevalidateChannels, "Partial diagnostic connection did not request channel revalidation.");
    }

    private static void CurrentIdentityPartialConsoleReconnectFlow()
    {
        HostProfile profile = new(
            Guid.Parse("5374fd4a-f750-4ec1-8728-b2839b062238"),
            "实验室 Hyper-V",
            "10.0.0.6");
        var identity = new RecordingCurrentIdentityResolver();
        var diagnostic = new HostDiagnosticPipeline(
            new SuccessfulIpv4Probe(),
            identity,
            new DelegateExplicitCredentialValidator((_, _, _) => Task.FromResult(
                new ExplicitCredentialValidationResult(
                    ExplicitCredentialValidationStatus.Valid,
                    "显式凭据验证通过。"))),
            new SuccessfulWmiProbe(),
            new FailingConsoleProbe());

        HostDiagnosticReport report = diagnostic.RunAsync(profile).GetAwaiter().GetResult();

        TestAssert.Equal(HostDiagnosticAvailability.PartiallyAvailable, report.Availability);
        TestAssert.True(report.ManagementAvailable, "The management channel should remain usable without TCP 2179.");
        TestAssert.False(report.ConsoleAvailable, "The failed TCP 2179 probe was reported as available.");
        TestAssert.True(identity.LastIdentity?.UsesCurrentWindowsIdentity == true,
            "Current Windows identity was not preserved through diagnostics.");
        TestAssert.Null(identity.LastIdentity?.UserName, "Current identity unexpectedly supplied a username.");
        TestAssert.Null(identity.LastIdentity?.Password, "Current identity unexpectedly supplied a password.");

        var reconnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectCompletion = new TaskCompletionSource<IHostSessionCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new SequencedConnector((call, request, _) =>
        {
            TestAssert.Equal(profile.Id, request.Profile.Id);
            if (call == 1)
                return Task.FromResult<IHostSessionCandidate>(new Candidate(profile, HostChannelState.Unavailable));

            reconnectEntered.TrySetResult();
            return reconnectCompletion.Task;
        });
        var snapshots = new SequencedSnapshotLoader();
        var registry = new HostSessionRegistry(connector, snapshots);
        HostId hostId = HostId.FromProfile(profile);
        HostConnectResult connectResult = registry.ConnectAsync(new HostConnectRequest(
            profile,
            HostChannelState.Available,
            HostChannelState.Unavailable)).GetAwaiter().GetResult();

        TestAssert.True(connectResult.Succeeded, connectResult.Message);
        HostSessionSnapshot connected = registry.Current.GetRequired(hostId);
        TestAssert.Equal(HostConnectionState.PartiallyAvailable, connected.ConnectionState);
        TestAssert.Equal(profile.Id, connected.Target.ProfileId);
        TestAssert.Equal(2, connected.BasicSnapshot?.VirtualMachineCount);
        TestAssert.True(connected.Capabilities[HostCapabilityKind.VmRead].CanExecute,
            "Partial availability disabled VM reads.");
        TestAssert.True(connected.Capabilities[HostCapabilityKind.VmWrite].CanExecute,
            "Partial availability disabled VM writes.");
        TestAssert.False(connected.Capabilities[HostCapabilityKind.VmConsole].CanExecute,
            "TCP 2179 failure did not disable the console.");
        TestAssert.Contains(
            "TCP 2179",
            connected.Capabilities[HostCapabilityKind.VmConsole].Reason);

        var vmOperations = new HostOperationRouter(registry, new HostWmiContextResolver());
        HostVmReadResult<string> read = vmOperations.ReadAsync(
            hostId,
            (context, _) => Task.FromResult(context.Host)).GetAwaiter().GetResult();
        TestAssert.Equal(HostVmOperationStatus.Succeeded, read.Status);
        TestAssert.Equal(profile.Address, read.Value);

        HostVmWriteResult write = vmOperations.WriteAsync(
            hostId,
            (_, _) => Task.FromResult(new HostVmBackendWriteResult(true, "虚拟机生命周期操作完成。")))
            .GetAwaiter().GetResult();
        TestAssert.Equal(HostVmOperationStatus.Succeeded, write.Status);

        var consoles = new HostConsoleSessions(registry);
        HostConsoleSessionCapture blockedConsole = consoles.Capture(
            hostId,
            "e687df5f-1db0-4653-a7d8-080c00fef57e",
            "测试虚拟机");
        TestAssert.False(blockedConsole.Succeeded, "Console capture succeeded while TCP 2179 was unavailable.");
        TestAssert.Contains("TCP 2179", blockedConsole.Message);

        TestAssert.True(registry.UpdateHostChannels(
            hostId,
            HostChannelState.Available,
            HostChannelState.Available), "Channel refresh was rejected for the active profile.");
        HostConsoleSessionCapture availableConsole = consoles.Capture(
            hostId,
            "e687df5f-1db0-4653-a7d8-080c00fef57e",
            "测试虚拟机");
        TestAssert.True(availableConsole.Succeeded, availableConsole.Message);
        TestAssert.Equal(profile.Address, availableConsole.Session?.Server);
        TestAssert.Equal(2179, availableConsole.Session?.Port);

        HostBasicSnapshot oldSnapshot = registry.Current.GetRequired(hostId).BasicSnapshot!;
        long oldGeneration = registry.Current.GetRequired(hostId).Generation;
        TestAssert.True(registry.ReportConnectionLoss(
            registry.CaptureOperationStamp(hostId),
            "模拟局域网中断。"), "The active connection loss was not accepted.");
        WaitUntil(() => reconnectEntered.Task.IsCompleted, "Automatic reconnect did not start.");

        HostSessionSnapshot stale = registry.Current.GetRequired(hostId);
        TestAssert.True(stale.HasStaleData, "Disconnect did not retain a stale snapshot.");
        TestAssert.Equal(oldSnapshot, stale.BasicSnapshot);
        TestAssert.Equal(profile.Id, stale.Target.ProfileId);
        TestAssert.True(registry.Current.TryGet(HostId.Local, out _),
            "A remote disconnect removed the fixed local host.");
        TestAssert.False(registry.TryBeginWrite(hostId, out IHostWriteLease? staleLease, out string staleReason),
            "A write lease was granted while remote data was stale.");
        TestAssert.Null(staleLease, "Rejected stale write returned a lease.");
        TestAssert.Contains("旧数据", staleReason);

        reconnectCompletion.TrySetResult(new Candidate(profile, HostChannelState.Available));
        WaitUntil(
            () => registry.Current.GetRequired(hostId).Generation > oldGeneration,
            "A successful reconnect did not publish a fresh host generation.");

        HostSessionSnapshot recovered = registry.Current.GetRequired(hostId);
        TestAssert.False(recovered.HasStaleData,
            "A successful reconnect retained the stale-data marker.");
        TestAssert.Equal(HostConnectionState.Connected, recovered.ConnectionState);
        TestAssert.Equal(7, recovered.BasicSnapshot?.VirtualMachineCount);
        TestAssert.True(recovered.Capabilities[HostCapabilityKind.VmWrite].CanExecute,
            "VM writes did not recover after reconnect.");
        TestAssert.True(recovered.Capabilities[HostCapabilityKind.VmConsole].CanExecute,
            "Console capability did not recover after reconnect.");
        registry.Shutdown();
    }

    private static void MultipleProfilesKeepSecretsOutOfConfiguration()
    {
        using var temp = new EndToEndTempDirectory();
        string profilePath = Path.Combine(temp.Path, "Hosts.xml");
        var credentialStore = new RecordingCredentialStore();
        var manager = new HostProfileManager(new HostProfileStore(profilePath), credentialStore);
        var currentIdentity = new HostProfile(Guid.NewGuid(), "当前身份宿主", "10.0.0.6");
        Guid explicitId = Guid.NewGuid();
        const string secret = "E2E-only-password-value";

        manager.Save(currentIdentity);
        HostProfile explicitProfile = manager.Save(
            new HostProfile(
                explicitId,
                "显式凭据宿主",
                "10.0.0.7",
                HostAuthenticationMode.ExplicitCredential,
                "LAB\\HyperVOperator"),
            new WindowsCredential("LAB\\HyperVOperator", secret));

        IReadOnlyList<HostProfile> profiles = manager.GetAll();
        TestAssert.Equal(2, profiles.Count);
        TestAssert.Equal(HostAuthenticationMode.CurrentWindowsIdentity, profiles[0].AuthenticationMode);
        TestAssert.Equal(HostCredentialTarget.ForProfile(explicitId), explicitProfile.CredentialTarget);
        TestAssert.True(
            credentialStore.TryRead(explicitProfile.CredentialTarget!, out WindowsCredential? storedCredential),
            "The remembered credential was not written to the credential store.");
        TestAssert.Equal(secret, storedCredential?.Password);

        string configuration = File.ReadAllText(profilePath);
        TestAssert.False(configuration.Contains(secret, StringComparison.Ordinal),
            "The profile file contains the remembered password.");
        TestAssert.False(configuration.Contains("password", StringComparison.OrdinalIgnoreCase),
            "The profile file contains a password field.");
        TestAssert.Contains(explicitProfile.CredentialTarget!, configuration);

        AppLog.Initialize(temp.Path);
        var diagnostic = new HostDiagnosticPipeline(
            new SuccessfulIpv4Probe(),
            new HostIdentityResolver(credentialStore),
            new DelegateExplicitCredentialValidator((_, _, _) => Task.FromResult(
                new ExplicitCredentialValidationResult(
                    ExplicitCredentialValidationStatus.Valid,
                    "显式凭据验证通过。"))),
            new LeakingWmiProbe(secret),
            new FailingConsoleProbe());
        HostDiagnosticReport report = diagnostic.RunAsync(explicitProfile).GetAwaiter().GetResult();
        string inMemoryErrors = string.Join('\n',
            report.Steps.Select(step => step.Explanation)
                .Concat(report.LogEntries.Select(entry => entry.Message)));
        TestAssert.False(inMemoryErrors.Contains(secret, StringComparison.Ordinal),
            "The in-memory diagnostic error contains the remembered password.");

        AppLog.Shutdown();
        string fileLog = File.ReadAllText(Path.Combine(temp.Path, "logs", "ExHyperV.log"));
        TestAssert.False(fileLog.Contains(secret, StringComparison.Ordinal),
            "The rolling log contains the remembered password.");
        TestAssert.Contains(SensitiveDataRedactor.RedactedValue, fileLog);

        var rollbackWriter = new HostRollbackScriptWriter(Path.Combine(temp.Path, "logs"));
        string rollbackPath = rollbackWriter.WriteAsync(
            explicitProfile.DisplayName,
            explicitProfile.Address,
            [new HostConfigurationCommand(
                HostPreflightChangeKind.ConfigureConsole2179FirewallRule,
                "恢复 TCP 2179 规则",
                "Write-Output 'apply'",
                "Write-Output 'rollback'")],
            null,
            CancellationToken.None).GetAwaiter().GetResult();
        string rollback = File.ReadAllText(rollbackPath);
        TestAssert.False(rollback.Contains(secret, StringComparison.Ordinal),
            "The rollback script contains the remembered password.");
    }

    private static void PublicRemoteErrorsAreRedacted()
    {
        const string secret = "remote-public-error-secret";
        var profile = new HostProfile(Guid.NewGuid(), "脱敏测试宿主", "10.0.0.9");
        var failedRegistry = new HostSessionRegistry(
            new SequencedConnector((_, _, _) => throw new HostSwitchException($"password={secret}")),
            new SequencedSnapshotLoader());
        HostConnectResult failedConnect = failedRegistry.ConnectAsync(new HostConnectRequest(
            profile,
            HostChannelState.Available,
            HostChannelState.Available)).GetAwaiter().GetResult();

        AssertRedacted(failedConnect.Message, secret, "Host connect result");

        var reconnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new SequencedConnector((call, _, token) => call == 1
            ? Task.FromResult<IHostSessionCandidate>(new Candidate(profile, HostChannelState.Available))
            : WaitForCancellationAsync(reconnectEntered, token));
        var registry = new HostSessionRegistry(connector, new SequencedSnapshotLoader());
        HostId hostId = HostId.FromProfile(profile);
        HostConnectResult connected = registry.ConnectAsync(new HostConnectRequest(
            profile,
            HostChannelState.Available,
            HostChannelState.Available)).GetAwaiter().GetResult();
        TestAssert.True(connected.Succeeded, connected.Message);
        var operations = new HostOperationRouter(registry, new HostWmiContextResolver());

        HostVmWriteResult backendFailure = operations.WriteAsync(
            hostId,
            (_, _) => Task.FromResult(new HostVmBackendWriteResult(
                false,
                $"token={secret}"))).GetAwaiter().GetResult();
        AssertRedacted(backendFailure.Message, secret, "VM backend result");

        HostVmReadResult<string> connectionFailure = operations.ReadAsync<string>(
            hostId,
            (_, _) => throw new TimeoutException($"password={secret}"))
            .GetAwaiter().GetResult();
        WaitUntil(() => reconnectEntered.Task.IsCompleted, "Redaction reconnect did not start.");
        AssertRedacted(connectionFailure.Message, secret, "VM connection error");
        AssertRedacted(registry.Current.GetRequired(hostId).Reconnect.LastError, secret, "Reconnect state");
        registry.StopReconnect(hostId);
        registry.Shutdown();
    }

    private static async Task<IHostSessionCandidate> WaitForCancellationAsync(
        TaskCompletionSource entered,
        CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable");
    }

    private static void AssertRedacted(string value, string secret, string surface)
    {
        TestAssert.False(value.Contains(secret, StringComparison.Ordinal), $"{surface} exposed a secret.");
        TestAssert.Contains(SensitiveDataRedactor.RedactedValue, value);
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        if (!SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException(message);
    }

    private sealed class RecordingCurrentIdentityResolver : IHostIdentityResolver
    {
        public ResolvedHostIdentity? LastIdentity { get; private set; }

        public ResolvedHostIdentity Resolve(HostProfile profile, WindowsCredential? transientCredential)
        {
            TestAssert.Null(transientCredential, "Current identity diagnostics unexpectedly received a transient credential.");
            LastIdentity = ResolvedHostIdentity.CurrentWindowsIdentity;
            return LastIdentity;
        }
    }

    private sealed class SuccessfulIpv4Probe : IIpv4ReachabilityProbe
    {
        public Task ProbeAsync(string address, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SuccessfulWmiProbe : IWmiDcomProbe
    {
        public Task ProbeAsync(
            string address,
            ResolvedHostIdentity identity,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingConsoleProbe : ITcpPortProbe
    {
        public Task ProbeAsync(string address, int port, CancellationToken cancellationToken) =>
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.ConnectionRefused,
                "TCP 2179 不可用。" );
    }

    private sealed class LeakingWmiProbe(string secret) : IWmiDcomProbe
    {
        public Task ProbeAsync(
            string address,
            ResolvedHostIdentity identity,
            CancellationToken cancellationToken)
        {
            TestAssert.Equal(secret, identity.Password);
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.AuthenticationFailed,
                $"password={secret} token={secret}");
        }
    }

    private sealed class Candidate(HostProfile profile, HostChannelState consoleChannel) : IHostSessionCandidate
    {
        public HostTarget Target { get; } = HostTarget.FromProfile(profile);
        public IHostManagementConnection ManagementConnection { get; } =
            new TestManagementConnection(WmiContext.RemoteCurrentWindowsIdentity(profile.Address));
        public HostChannelState ManagementChannel => HostChannelState.Available;
        public HostChannelState ConsoleChannel { get; } = consoleChannel;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestManagementConnection(WmiContext context) : IWmiHostManagementConnection
    {
        public WmiContext Context { get; } = context;
    }

    private sealed class SequencedConnector(
        Func<int, HostSwitchRequest, CancellationToken, Task<IHostSessionCandidate>> connect) : IHostSessionConnector
    {
        private int _calls;

        public Task<IHostSessionCandidate> ConnectAsync(
            HostSwitchRequest request,
            CancellationToken cancellationToken) =>
            connect(Interlocked.Increment(ref _calls), request, cancellationToken);
    }

    private sealed class SequencedSnapshotLoader : IHostBasicSnapshotLoader
    {
        private int _calls;

        public Task<HostBasicSnapshot> LoadAsync(
            IHostSessionCandidate candidate,
            CancellationToken cancellationToken)
        {
            int count = Interlocked.Increment(ref _calls) == 1 ? 2 : 7;
            return Task.FromResult(new HostBasicSnapshot(
                candidate.Target.DisplayName,
                "Windows 11",
                "运行中",
                count,
                DateTimeOffset.Now));
        }
    }

    private sealed class RecordingCredentialStore : IWindowsCredentialStore
    {
        private readonly Dictionary<string, WindowsCredential> _credentials = new(StringComparer.Ordinal);

        public void Save(string target, WindowsCredential credential) => _credentials[target] = credential;

        public bool TryRead(string target, out WindowsCredential? credential) =>
            _credentials.TryGetValue(target, out credential);

        public bool Delete(string target) => _credentials.Remove(target);
    }

    private sealed class EndToEndTempDirectory : IDisposable
    {
        public EndToEndTempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ExHyperV.EndToEndTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
