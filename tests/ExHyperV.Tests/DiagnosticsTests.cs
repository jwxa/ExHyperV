using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Profiles;

internal static class DiagnosticsTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Diagnostics_CurrentIdentityRunsOrderedChecksWithoutCredentialRead", CurrentIdentityRunsOrderedChecksWithoutCredentialRead),
        ("Diagnostics_ExplicitIdentityResolvesCredentialByReference", ExplicitIdentityResolvesCredentialByReference),
        ("Diagnostics_InvalidExplicitCredentialSkipsWmiAndPromptsForPassword", InvalidExplicitCredentialSkipsWmiAndPromptsForPassword),
        ("Diagnostics_ValidatedCredentialTurnsWmiDenialIntoPermissionPrompt", ValidatedCredentialTurnsWmiDenialIntoPermissionPrompt),
        ("Diagnostics_InconclusiveCredentialValidationStillRunsWmi", InconclusiveCredentialValidationStillRunsWmi),
        ("Diagnostics_InconclusiveCredentialDenialPromptsForPasswordFirst", InconclusiveCredentialDenialPromptsForPasswordFirst),
        ("Diagnostics_WindowsCredentialValidatorClassifiesBadPassword", WindowsCredentialValidatorClassifiesBadPassword),
        ("Diagnostics_WindowsCredentialValidatorCleansSuccessfulProbe", WindowsCredentialValidatorCleansSuccessfulProbe),
        ("Diagnostics_WindowsCredentialValidatorPreservesExistingConnection", WindowsCredentialValidatorPreservesExistingConnection),
        ("Diagnostics_WindowsCredentialValidatorSkipsClosedSmb", WindowsCredentialValidatorSkipsClosedSmb),
        ("Diagnostics_WmiSuccessAndTcpFailureIsPartiallyAvailable", WmiSuccessAndTcpFailureIsPartiallyAvailable),
        ("Diagnostics_AuthenticationFailureSkipsWmiButStillChecksTcp", AuthenticationFailureSkipsWmiButStillChecksTcp),
        ("Diagnostics_WmiAuthenticationFailureStillChecksTcp", WmiAuthenticationFailureStillChecksTcp),
        ("Diagnostics_BothChannelsFailIsUnavailable", BothChannelsFailIsUnavailable),
        ("Diagnostics_CancellationProducesDeterministicReport", CancellationProducesDeterministicReport),
        ("Diagnostics_ReportContainsOrderedChineseDetailedLogs", ReportContainsOrderedChineseDetailedLogs),
        ("Diagnostics_LateResultCannotReplaceNewSelection", LateResultCannotReplaceNewSelection)
    ];

    private static void CurrentIdentityRunsOrderedChecksWithoutCredentialRead()
    {
        var calls = new List<string>();
        var store = new TrackingCredentialStore();
        var identity = new RecordingIdentityResolver(new HostIdentityResolver(store), calls);
        var pipeline = CreatePipeline(calls, identity: identity);

        HostDiagnosticReport report = pipeline.RunAsync(Profile(), cancellationToken: default).GetAwaiter().GetResult();

        TestAssert.SequenceEqual(["ipv4", "identity", "wmi", "tcp"], calls);
        TestAssert.Equal(0, store.ReadCount);
        TestAssert.Equal(HostDiagnosticAvailability.FullyAvailable, report.Availability);
        TestAssert.True(report.ManagementAvailable, "WMI management channel should be available.");
        TestAssert.True(report.ConsoleAvailable, "TCP 2179 console channel should be available.");
    }

    private static void ExplicitIdentityResolvesCredentialByReference()
    {
        Guid id = Guid.NewGuid();
        string target = HostCredentialTarget.ForProfile(id);
        var store = new TrackingCredentialStore();
        store.Credentials[target] = new WindowsCredential("LAB\\Operator", "diagnostic-secret");
        ResolvedHostIdentity? observed = null;
        var wmi = new DelegateWmiProbe((_, identity, _) =>
        {
            observed = identity;
            return Task.CompletedTask;
        });
        var pipeline = new HostDiagnosticPipeline(
            new DelegateIpv4Probe((_, _) => Task.CompletedTask),
            new HostIdentityResolver(store),
            ValidCredentialValidator(),
            wmi,
            new DelegateTcpProbe((_, _, _) => Task.CompletedTask));
        var profile = new HostProfile(
            id,
            "实验室宿主",
            "10.0.0.6",
            HostAuthenticationMode.ExplicitCredential,
            "LAB\\Operator",
            target);

        HostDiagnosticReport report = pipeline.RunAsync(profile, cancellationToken: default).GetAwaiter().GetResult();

        TestAssert.Equal(1, store.ReadCount);
        TestAssert.NotNull(observed, "WMI probe did not receive the resolved identity.");
        TestAssert.Equal("LAB\\Operator", observed!.UserName);
        TestAssert.Equal("diagnostic-secret", observed.Password);
        TestAssert.Equal(HostDiagnosticAvailability.FullyAvailable, report.Availability);
    }

    private static void InvalidExplicitCredentialSkipsWmiAndPromptsForPassword()
    {
        var calls = new List<string>();
        var profile = new HostProfile(
            Guid.NewGuid(),
            "错误密码宿主",
            "10.0.0.6",
            HostAuthenticationMode.ExplicitCredential,
            "LAB\\Operator");
        var validator = new DelegateExplicitCredentialValidator((_, identity, _) =>
        {
            calls.Add("credential");
            TestAssert.Equal("wrong-secret", identity.Password);
            return Task.FromResult(new ExplicitCredentialValidationResult(
                ExplicitCredentialValidationStatus.Invalid,
                "显式凭据的用户名或密码错误。请编辑主机配置并重新输入密码。"));
        });
        var pipeline = CreatePipeline(
            calls,
            credentialValidator: validator,
            wmi: new DelegateWmiProbe((_, _, _) =>
            {
                calls.Add("wmi");
                return Task.CompletedTask;
            }));

        HostDiagnosticReport report = pipeline.RunAsync(
            profile,
            new WindowsCredential("LAB\\Operator", "wrong-secret")).GetAwaiter().GetResult();

        TestAssert.SequenceEqual(["ipv4", "identity", "credential", "tcp"], calls);
        HostDiagnosticStepResult identity = report.GetStep(HostDiagnosticStepKind.Identity);
        TestAssert.Equal(HostDiagnosticStepStatus.Failed, identity.Status);
        TestAssert.Equal(HostDiagnosticErrorCode.InvalidCredential, identity.ErrorCode);
        TestAssert.Contains("用户名或密码错误", identity.Explanation);
        TestAssert.False(identity.Explanation.Contains("wrong-secret", StringComparison.Ordinal),
            "The invalid-credential explanation leaked the password.");
        TestAssert.Equal(HostDiagnosticStepStatus.Skipped, report.GetStep(HostDiagnosticStepKind.WmiDcom).Status);
        TestAssert.True(report.ConsoleAvailable, "TCP 2179 must remain independent from credential validation.");
    }

    private static void ValidatedCredentialTurnsWmiDenialIntoPermissionPrompt()
    {
        var profile = new HostProfile(
            Guid.NewGuid(),
            "权限不足宿主",
            "10.0.0.6",
            HostAuthenticationMode.ExplicitCredential,
            "LAB\\Operator");
        var pipeline = CreatePipeline(
            credentialValidator: ValidCredentialValidator(),
            wmi: new DelegateWmiProbe((_, _, _) => throw new HostDiagnosticException(
                HostDiagnosticErrorCode.AuthenticationFailed,
                "WMI/DCOM 身份验证失败或权限不足。")));

        HostDiagnosticReport report = pipeline.RunAsync(
            profile,
            new WindowsCredential("LAB\\Operator", "valid-secret")).GetAwaiter().GetResult();

        HostDiagnosticStepResult wmi = report.GetStep(HostDiagnosticStepKind.WmiDcom);
        TestAssert.Equal(HostDiagnosticErrorCode.AccessDenied, wmi.ErrorCode);
        TestAssert.Contains("显式凭据有效", wmi.Explanation);
        TestAssert.Contains("权限", wmi.Explanation);
        TestAssert.False(wmi.Explanation.Contains("valid-secret", StringComparison.Ordinal),
            "The permission explanation leaked the password.");
    }

    private static void InconclusiveCredentialValidationStillRunsWmi()
    {
        var calls = new List<string>();
        var profile = new HostProfile(
            Guid.NewGuid(),
            "降级宿主",
            "10.0.0.6",
            HostAuthenticationMode.ExplicitCredential,
            "LAB\\Operator");
        var validator = new DelegateExplicitCredentialValidator((_, _, _) =>
        {
            calls.Add("credential");
            return Task.FromResult(new ExplicitCredentialValidationResult(
                ExplicitCredentialValidationStatus.Inconclusive,
                "无法预验证密码。"));
        });
        var pipeline = CreatePipeline(calls, credentialValidator: validator);

        HostDiagnosticReport report = pipeline.RunAsync(
            profile,
            new WindowsCredential("LAB\\Operator", "secret")).GetAwaiter().GetResult();

        TestAssert.SequenceEqual(["ipv4", "identity", "credential", "wmi", "tcp"], calls);
        TestAssert.True(report.ManagementAvailable, "Inconclusive credential validation blocked WMI.");
    }

    private static void InconclusiveCredentialDenialPromptsForPasswordFirst()
    {
        var profile = new HostProfile(
            Guid.NewGuid(),
            "无法独立校验宿主",
            "10.0.0.6",
            HostAuthenticationMode.ExplicitCredential,
            "LAB\\Operator");
        var validator = new DelegateExplicitCredentialValidator((_, _, _) => Task.FromResult(
            new ExplicitCredentialValidationResult(
                ExplicitCredentialValidationStatus.Inconclusive,
                "目标主机未开放 SMB 凭据校验通道。")));
        var pipeline = CreatePipeline(
            credentialValidator: validator,
            wmi: new DelegateWmiProbe((_, _, _) => throw new HostDiagnosticException(
                HostDiagnosticErrorCode.AuthenticationFailed,
                "WMI/DCOM 身份验证失败或权限不足。")));

        HostDiagnosticReport report = pipeline.RunAsync(
            profile,
            new WindowsCredential("LAB\\Operator", "secret")).GetAwaiter().GetResult();

        HostDiagnosticStepResult wmi = report.GetStep(HostDiagnosticStepKind.WmiDcom);
        TestAssert.Equal(HostDiagnosticErrorCode.AuthenticationFailed, wmi.ErrorCode);
        TestAssert.Contains("重新输入用户名和密码", wmi.Explanation);
        TestAssert.Contains("确认正确", wmi.Explanation);
        TestAssert.Contains("权限", wmi.Explanation);
    }

    private static void WindowsCredentialValidatorSkipsClosedSmb()
    {
        var api = new FakeWindowsNetworkCredentialApi { SmbAvailable = false };
        var validator = new WindowsExplicitCredentialValidator(api, TimeSpan.FromSeconds(1));

        ExplicitCredentialValidationResult result = validator.ValidateAsync(
            "10.0.0.6",
            ResolvedHostIdentity.Explicit("LAB\\Operator", "secret"),
            CancellationToken.None).GetAwaiter().GetResult();

        TestAssert.Equal(ExplicitCredentialValidationStatus.Inconclusive, result.Status);
        TestAssert.Contains("未开放 SMB", result.Explanation);
        TestAssert.Equal(0, api.AddCount);
    }

    private static void WindowsCredentialValidatorClassifiesBadPassword()
    {
        var api = new FakeWindowsNetworkCredentialApi { AddResult = 1326 };
        var validator = new WindowsExplicitCredentialValidator(api, TimeSpan.FromSeconds(1));

        ExplicitCredentialValidationResult result = validator.ValidateAsync(
            "10.0.0.6",
            ResolvedHostIdentity.Explicit("LAB\\Operator", "bad-secret"),
            CancellationToken.None).GetAwaiter().GetResult();

        TestAssert.Equal(ExplicitCredentialValidationStatus.Invalid, result.Status);
        TestAssert.Contains("用户名或密码错误", result.Explanation);
        TestAssert.False(result.Explanation.Contains("bad-secret", StringComparison.Ordinal),
            "The native validation result leaked the password.");
        TestAssert.Equal(0, api.CancelCount);
    }

    private static void WindowsCredentialValidatorCleansSuccessfulProbe()
    {
        var api = new FakeWindowsNetworkCredentialApi();
        var validator = new WindowsExplicitCredentialValidator(api, TimeSpan.FromSeconds(1));

        ExplicitCredentialValidationResult result = validator.ValidateAsync(
            "10.0.0.6",
            ResolvedHostIdentity.Explicit("LAB\\Operator", "secret"),
            CancellationToken.None).GetAwaiter().GetResult();

        TestAssert.Equal(ExplicitCredentialValidationStatus.Valid, result.Status);
        TestAssert.Equal(1, api.AddCount);
        TestAssert.Equal(1, api.CancelCount);
        TestAssert.Equal("\\\\10.0.0.6\\IPC$", api.LastRemoteName);
    }

    private static void WindowsCredentialValidatorPreservesExistingConnection()
    {
        var api = new FakeWindowsNetworkCredentialApi { HasConnection = true };
        var validator = new WindowsExplicitCredentialValidator(api, TimeSpan.FromSeconds(1));

        ExplicitCredentialValidationResult result = validator.ValidateAsync(
            "10.0.0.6",
            ResolvedHostIdentity.Explicit("LAB\\Operator", "secret"),
            CancellationToken.None).GetAwaiter().GetResult();

        TestAssert.Equal(ExplicitCredentialValidationStatus.Inconclusive, result.Status);
        TestAssert.Equal(0, api.AddCount);
        TestAssert.Equal(0, api.CancelCount);
        TestAssert.Contains("已有网络会话", result.Explanation);
    }

    private static void WmiSuccessAndTcpFailureIsPartiallyAvailable()
    {
        var pipeline = CreatePipeline(
            tcp: new DelegateTcpProbe((_, _, _) => throw new HostDiagnosticException(
                HostDiagnosticErrorCode.ConnectionRefused,
                "TCP 2179 连接被拒绝。")));

        HostDiagnosticReport report = pipeline.RunAsync(Profile(), cancellationToken: default).GetAwaiter().GetResult();

        TestAssert.Equal(HostDiagnosticAvailability.PartiallyAvailable, report.Availability);
        TestAssert.True(report.ManagementAvailable, "WMI should remain available when TCP 2179 fails.");
        TestAssert.False(report.ConsoleAvailable, "Console channel should be unavailable.");
        TestAssert.Equal(
            HostDiagnosticErrorCode.ConnectionRefused,
            report.GetStep(HostDiagnosticStepKind.Tcp2179).ErrorCode);
    }

    private static void AuthenticationFailureSkipsWmiButStillChecksTcp()
    {
        var calls = new List<string>();
        var profile = new HostProfile(
            Guid.NewGuid(),
            "缺少凭据",
            "10.0.0.6",
            HostAuthenticationMode.ExplicitCredential,
            "LAB\\Operator");
        var pipeline = CreatePipeline(calls, identity: new HostIdentityResolver(new TrackingCredentialStore()));

        HostDiagnosticReport report = pipeline.RunAsync(profile, cancellationToken: default).GetAwaiter().GetResult();

        TestAssert.SequenceEqual(["ipv4", "tcp"], calls);
        TestAssert.Equal(HostDiagnosticErrorCode.CredentialMissing, report.GetStep(HostDiagnosticStepKind.Identity).ErrorCode);
        TestAssert.Equal(HostDiagnosticStepStatus.Skipped, report.GetStep(HostDiagnosticStepKind.WmiDcom).Status);
        TestAssert.Equal(HostDiagnosticStepStatus.Succeeded, report.GetStep(HostDiagnosticStepKind.Tcp2179).Status);
        TestAssert.Equal(HostDiagnosticAvailability.PartiallyAvailable, report.Availability);
    }

    private static void CancellationProducesDeterministicReport()
    {
        using var cancellation = new CancellationTokenSource();
        var ipv4 = new DelegateIpv4Probe((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var pipeline = CreatePipeline(ipv4: ipv4);

        HostDiagnosticReport report = pipeline.RunAsync(Profile(), cancellationToken: cancellation.Token).GetAwaiter().GetResult();

        TestAssert.Equal(HostDiagnosticAvailability.Cancelled, report.Availability);
        TestAssert.Equal(HostDiagnosticStepStatus.Cancelled, report.GetStep(HostDiagnosticStepKind.Ipv4Reachability).Status);
        TestAssert.Equal(HostDiagnosticStepStatus.Skipped, report.GetStep(HostDiagnosticStepKind.Identity).Status);
        TestAssert.Equal(HostDiagnosticStepStatus.Skipped, report.GetStep(HostDiagnosticStepKind.WmiDcom).Status);
        TestAssert.Equal(HostDiagnosticStepStatus.Skipped, report.GetStep(HostDiagnosticStepKind.Tcp2179).Status);
    }

    private static void WmiAuthenticationFailureStillChecksTcp()
    {
        var calls = new List<string>();
        var pipeline = CreatePipeline(
            calls,
            wmi: new DelegateWmiProbe((_, _, _) =>
            {
                calls.Add("wmi");
                throw new HostDiagnosticException(
                    HostDiagnosticErrorCode.AuthenticationFailed,
                    "WMI/DCOM 身份验证失败。" );
            }));

        HostDiagnosticReport report = pipeline.RunAsync(Profile(), cancellationToken: default).GetAwaiter().GetResult();

        TestAssert.SequenceEqual(["ipv4", "identity", "wmi", "tcp"], calls);
        TestAssert.Equal(
            HostDiagnosticErrorCode.AuthenticationFailed,
            report.GetStep(HostDiagnosticStepKind.WmiDcom).ErrorCode);
        TestAssert.True(report.ConsoleAvailable, "TCP 2179 must remain independent from WMI authentication.");
        TestAssert.Equal(HostDiagnosticAvailability.PartiallyAvailable, report.Availability);
    }

    private static void BothChannelsFailIsUnavailable()
    {
        var pipeline = CreatePipeline(
            wmi: new DelegateWmiProbe((_, _, _) => throw new HostDiagnosticException(
                HostDiagnosticErrorCode.AccessDenied,
                "WMI/DCOM 访问被拒绝。")),
            tcp: new DelegateTcpProbe((_, _, _) => throw new HostDiagnosticException(
                HostDiagnosticErrorCode.Timeout,
                "TCP 2179 连接超时。")));

        HostDiagnosticReport report = pipeline.RunAsync(Profile(), cancellationToken: default).GetAwaiter().GetResult();

        TestAssert.False(report.ManagementAvailable, "Management channel should be unavailable.");
        TestAssert.False(report.ConsoleAvailable, "Console channel should be unavailable.");
        TestAssert.Equal(HostDiagnosticAvailability.Unavailable, report.Availability);
    }

    private static void ReportContainsOrderedChineseDetailedLogs()
    {
        var pipeline = CreatePipeline(
            ipv4: new DelegateIpv4Probe((_, _) => throw new HostDiagnosticException(
                HostDiagnosticErrorCode.Unreachable,
                "IPv4 主机未响应 ICMP。")));

        HostDiagnosticReport report = pipeline.RunAsync(Profile(), cancellationToken: default).GetAwaiter().GetResult();
        string text = string.Join('\n', report.LogEntries.Select(entry => entry.Message));

        TestAssert.True(report.LogEntries.Count >= 10, "Detailed diagnostics should log step start and result lines.");
        TestAssert.Contains("开始检测 IPv4 可达性", text);
        TestAssert.Contains("IPv4 主机未响应 ICMP", text);
        TestAssert.Contains("root\\virtualization\\v2", text);
        TestAssert.Contains("TCP 2179", text);
    }

    private static void LateResultCannotReplaceNewSelection()
    {
        using var coordinator = new HostDiagnosticRunCoordinator();
        Guid firstProfile = Guid.NewGuid();
        Guid secondProfile = Guid.NewGuid();
        using HostDiagnosticRun first = coordinator.Begin(firstProfile);

        TestAssert.True(coordinator.IsCurrent(first, firstProfile), "The first diagnostic run was not current.");
        coordinator.Invalidate();
        TestAssert.True(first.Token.IsCancellationRequested, "Changing selection did not cancel the old diagnostic run.");
        TestAssert.False(coordinator.IsCurrent(first, secondProfile), "The old diagnostic run remained current for the new selection.");

        using HostDiagnosticRun second = coordinator.Begin(secondProfile);
        TestAssert.False(coordinator.Complete(first), "A late result completed the replacement diagnostic run.");
        TestAssert.True(coordinator.IsCurrent(second, secondProfile), "The replacement diagnostic run was not current.");
        TestAssert.True(coordinator.Complete(second), "The current diagnostic run could not complete.");
    }

    private static HostDiagnosticPipeline CreatePipeline(
        List<string>? calls = null,
        IHostIdentityResolver? identity = null,
        IIpv4ReachabilityProbe? ipv4 = null,
        IExplicitCredentialValidator? credentialValidator = null,
        IWmiDcomProbe? wmi = null,
        ITcpPortProbe? tcp = null)
    {
        calls ??= [];
        return new HostDiagnosticPipeline(
            ipv4 ?? new DelegateIpv4Probe((_, _) => { calls.Add("ipv4"); return Task.CompletedTask; }),
            identity ?? new RecordingIdentityResolver(new HostIdentityResolver(new TrackingCredentialStore()), calls),
            credentialValidator ?? ValidCredentialValidator(),
            wmi ?? new DelegateWmiProbe((_, _, _) => { calls.Add("wmi"); return Task.CompletedTask; }),
            tcp ?? new DelegateTcpProbe((_, _, _) => { calls.Add("tcp"); return Task.CompletedTask; }));
    }

    private static IExplicitCredentialValidator ValidCredentialValidator() =>
        new DelegateExplicitCredentialValidator((_, _, _) => Task.FromResult(
            new ExplicitCredentialValidationResult(
                ExplicitCredentialValidationStatus.Valid,
                "显式凭据验证通过。")));

    private static HostProfile Profile() => new(Guid.NewGuid(), "实验室宿主", "10.0.0.6");

    private sealed class TrackingCredentialStore : IWindowsCredentialStore
    {
        public Dictionary<string, WindowsCredential> Credentials { get; } = new(StringComparer.Ordinal);
        public int ReadCount { get; private set; }
        public void Save(string target, WindowsCredential credential) => Credentials[target] = credential;
        public bool TryRead(string target, out WindowsCredential? credential)
        {
            ReadCount++;
            return Credentials.TryGetValue(target, out credential);
        }
        public bool Delete(string target) => Credentials.Remove(target);
    }

    private sealed class RecordingIdentityResolver(IHostIdentityResolver inner, List<string> calls) : IHostIdentityResolver
    {
        public ResolvedHostIdentity Resolve(HostProfile profile, WindowsCredential? transientCredential)
        {
            calls.Add("identity");
            return inner.Resolve(profile, transientCredential);
        }
    }

    private sealed class DelegateIpv4Probe(Func<string, CancellationToken, Task> action) : IIpv4ReachabilityProbe
    {
        public Task ProbeAsync(string address, CancellationToken cancellationToken) => action(address, cancellationToken);
    }

    private sealed class DelegateWmiProbe(Func<string, ResolvedHostIdentity, CancellationToken, Task> action) : IWmiDcomProbe
    {
        public Task ProbeAsync(string address, ResolvedHostIdentity identity, CancellationToken cancellationToken) =>
            action(address, identity, cancellationToken);
    }

    private sealed class DelegateTcpProbe(Func<string, int, CancellationToken, Task> action) : ITcpPortProbe
    {
        public Task ProbeAsync(string address, int port, CancellationToken cancellationToken) => action(address, port, cancellationToken);
    }

    private sealed class FakeWindowsNetworkCredentialApi : IWindowsNetworkCredentialApi
    {
        public bool SmbAvailable { get; set; } = true;
        public bool HasConnection { get; set; }
        public int QueryResult { get; set; }
        public int AddResult { get; set; }
        public int CancelResult { get; set; }
        public int AddCount { get; private set; }
        public int CancelCount { get; private set; }
        public string? LastRemoteName { get; private set; }

        public Task<bool> IsSmbAvailableAsync(
            string server,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(SmbAvailable);

        public int HasConnectionToServer(string server, out bool hasConnection)
        {
            hasConnection = HasConnection;
            return QueryResult;
        }

        public int AddTemporaryConnection(string remoteName, string userName, string password)
        {
            AddCount++;
            LastRemoteName = remoteName;
            return AddResult;
        }

        public int CancelConnection(string remoteName)
        {
            CancelCount++;
            return CancelResult;
        }
    }
}

internal sealed class DelegateExplicitCredentialValidator(
    Func<string, ResolvedHostIdentity, CancellationToken, Task<ExplicitCredentialValidationResult>> action)
    : IExplicitCredentialValidator
{
    public Task<ExplicitCredentialValidationResult> ValidateAsync(
        string address,
        ResolvedHostIdentity identity,
        CancellationToken cancellationToken) => action(address, identity, cancellationToken);
}

internal static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected <{expected}> but found <{actual}>.");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"Expected <{string.Join(",", expected)}> but found <{string.Join(",", actual)}>.");
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected text to contain <{expected}>. Actual: <{actual}>.");
    }

    public static void NotNull<T>(T? actual, string message) where T : class
    {
        if (actual is null) throw new InvalidOperationException(message);
    }

    public static void Null<T>(T? actual, string message)
    {
        if (actual is not null) throw new InvalidOperationException(message);
    }
}
