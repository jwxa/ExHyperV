using System.Text;
using ExHyperV.IntegrationTests;
using ExHyperV.Services.Remote.Consoles;
using ExHyperV.Services.Remote.Preflight;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;

internal static class IntegrationRunnerTests
{
    private static readonly string[] EnvironmentVariableNames =
    [
        "EXHYPERV_INTEGRATION_RUN",
        "EXHYPERV_INTEGRATION_HOST",
        "EXHYPERV_INTEGRATION_DISPLAY_NAME",
        "EXHYPERV_INTEGRATION_AUTH",
        "EXHYPERV_INTEGRATION_PROFILE_ID",
        "EXHYPERV_INTEGRATION_USERNAME",
        "EXHYPERV_INTEGRATION_PASSWORD",
        "EXHYPERV_INTEGRATION_SECOND_HOST",
        "EXHYPERV_INTEGRATION_SECOND_DISPLAY_NAME",
        "EXHYPERV_INTEGRATION_SECOND_PROFILE_ID",
        "EXHYPERV_INTEGRATION_OPERATION_TIMEOUT_SECONDS",
        "EXHYPERV_INTEGRATION_TOTAL_TIMEOUT_SECONDS",
        "EXHYPERV_INTEGRATION_REPORT",
        "EXHYPERV_INTEGRATION_VM_WRITE",
        "EXHYPERV_INTEGRATION_VM",
        "EXHYPERV_INTEGRATION_VM_ACTION",
        "EXHYPERV_INTEGRATION_DISCONNECT",
        "EXHYPERV_INTEGRATION_OUTAGE_START_DELAY_SECONDS",
        "EXHYPERV_INTEGRATION_OUTAGE_DETECT_SECONDS",
        "EXHYPERV_INTEGRATION_RECONNECT_SECONDS",
        "EXHYPERV_INTEGRATION_CONFIGURE_PREVIEW",
        "EXHYPERV_INTEGRATION_CONFIGURE",
        "EXHYPERV_INTEGRATION_ACCOUNT_KIND",
        "EXHYPERV_INTEGRATION_ACCOUNT",
        "EXHYPERV_INTEGRATION_NETWORKS",
        "EXHYPERV_INTEGRATION_MAKE_PRIVATE",
        "EXHYPERV_INTEGRATION_CIDRS",
        "EXHYPERV_INTEGRATION_ROLLBACK_VERIFY"
    ];

    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("IntegrationRunner_MasterSwitchRequiresExactChineseConfirmation", MasterSwitchRequiresExactChineseConfirmation),
        ("IntegrationRunner_DangerousSwitchesRejectAliasesAndWhitespace", DangerousSwitchesRejectAliasesAndWhitespace),
        ("IntegrationRunner_CurrentIdentityRejectsExplicitCredentialVariables", CurrentIdentityRejectsExplicitCredentialVariables),
        ("IntegrationRunner_CredentialManagerRequiresStableProfileAndUser", CredentialManagerRequiresStableProfileAndUser),
        ("IntegrationRunner_SecondHostOptionsAreCoherent", SecondHostOptionsAreCoherent),
        ("IntegrationRunner_RollbackCannotRunWithoutConfiguration", RollbackCannotRunWithoutConfiguration),
        ("IntegrationRunner_ConfigurationPreviewIsReadOnlyAndUsesSameSelection", ConfigurationPreviewIsReadOnlyAndUsesSameSelection),
        ("IntegrationRunner_ConfigurationValidatesNetworksAndPrivateCidrs", ConfigurationValidatesNetworksAndPrivateCidrs),
        ("IntegrationRunner_ToStringNeverDisclosesPassword", ToStringNeverDisclosesPassword),
        ("IntegrationRunner_OneTimePasswordIsClearedDuringLoad", OneTimePasswordIsClearedDuringLoad),
        ("IntegrationRunner_ReportPathIsNormalizedAndValidated", ReportPathIsNormalizedAndValidated),
        ("IntegrationRunner_ReportComputesFailedPartialAndPassedStatus", ReportComputesFailedPartialAndPassedStatus),
        ("IntegrationRunner_ReportIsAtomicUtf8AndRedacted", ReportIsAtomicUtf8AndRedacted),
        ("IntegrationRunner_CancelledReportPreservesExistingFile", CancelledReportPreservesExistingFile),
        ("IntegrationRunner_DisconnectEvidenceRequiresEveryAcceptanceInvariant", DisconnectEvidenceRequiresEveryAcceptanceInvariant),
        ("IntegrationRunner_PartialAvailabilityRequiresManagementAndConsoleGate", PartialAvailabilityRequiresManagementAndConsoleGate)
    ];

    private static void PartialAvailabilityRequiresManagementAndConsoleGate()
    {
        var profile = new HostProfile(Guid.NewGuid(), "部分可用宿主", "10.0.0.6");
        var session = new ActiveHostSession(
            3,
            HostTarget.FromProfile(profile),
            HostConnectionState.PartiallyAvailable,
            HostChannelState.Available,
            HostChannelState.Unavailable,
            HasStaleData: false);
        HostSessionSnapshot snapshot = Snapshot(
            profile,
            generation: 3,
            stale: false,
            DateTimeOffset.MinValue,
            HostReconnectState.None,
            consoleChannel: HostChannelState.Unavailable,
            connectionState: HostConnectionState.PartiallyAvailable);
        var rejected = HostConsoleSessionCapture.Failure("目标宿主的 TCP 2179 控制台通道不可用。");

        PartialAvailabilityEvidence complete = PartialAvailabilityAcceptance.Evaluate(
            snapshot,
            managementReadSucceeded: true,
            rejected);
        TestAssert.True(complete.IsComplete, "Valid partial availability evidence was rejected.");
        TestAssert.True(complete.VmWriteAvailable, "Partial availability disabled VM writes.");

        PartialAvailabilityEvidence missingRead = PartialAvailabilityAcceptance.Evaluate(
            snapshot,
            managementReadSucceeded: false,
            rejected);
        TestAssert.False(missingRead.IsComplete, "Capability state replaced proof of a real WMI VM read.");

        var accepted = HostConsoleSessionCapture.Success(new HostConsoleSession(
            HostTarget.FromProfile(profile),
            new HostOperationStamp(3, profile.Id),
            Guid.NewGuid(),
            "VM",
            profile.Address,
            2179,
            "window"));
        TestAssert.False(
            PartialAvailabilityAcceptance.Evaluate(snapshot, true, accepted).IsComplete,
            "A successful console capture was accepted as unavailable-channel evidence.");

        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tests", "ExHyperV.IntegrationTests", "ControlledHostAcceptanceRunner.cs"));
        int methodStart = source.IndexOf("private void CaptureConsoleTarget", StringComparison.Ordinal);
        int methodEnd = source.IndexOf("private async Task RunVmWriteAsync", methodStart, StringComparison.Ordinal);
        TestAssert.True(methodStart >= 0 && methodEnd > methodStart, "Could not locate console capture acceptance logic.");
        string captureMethod = source.Substring(methodStart, methodEnd - methodStart);
        TestAssert.Contains("if (!diagnostic.ConsoleAvailable)", captureMethod);
        TestAssert.Contains("AcceptanceStatus.Skipped", captureMethod);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "ExHyperV.csproj")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void DisconnectEvidenceRequiresEveryAcceptanceInvariant()
    {
        Guid profileId = Guid.Parse("d3e12e66-dd30-4355-afd9-bc67cc1b1460");
        var profile = new HostProfile(profileId, "受控宿主", "10.0.0.6");
        DateTimeOffset startedAt = new(2026, 8, 14, 8, 0, 0, TimeSpan.Zero);
        var observer = new DisconnectAcceptanceObserver(profileId, 4, startedAt);

        observer.Observe(
            Snapshot(profile, 4, stale: true, startedAt, HostReconnectState.Starting("RPC 中断")),
            startedAt.AddSeconds(1));
        observer.RecordWriteGate(blocked: true, "目标宿主连接已中断，当前显示的是旧数据。");
        observer.Observe(
            Snapshot(profile, 4, stale: true, startedAt,
                HostReconnectState.Waiting(1, startedAt.AddSeconds(4), "第一次失败")),
            startedAt.AddSeconds(2));
        observer.Observe(
            Snapshot(profile, 4, stale: true, startedAt,
                HostReconnectState.Waiting(2, startedAt.AddSeconds(9), "第二次失败")),
            startedAt.AddSeconds(5));

        DisconnectAcceptanceEvidence incomplete = observer.Capture();
        TestAssert.False(incomplete.IsComplete, "Evidence passed before a fresh generation was observed.");
        TestAssert.Contains("新", incomplete.MissingSummary());

        observer.Observe(
            Snapshot(profile, 5, stale: false, startedAt.AddMinutes(1), HostReconnectState.None),
            startedAt.AddMinutes(1));
        DisconnectAcceptanceEvidence complete = observer.Capture();
        TestAssert.True(complete.IsComplete, complete.MissingSummary());
        TestAssert.True(complete.BackoffGrowthObserved, "Observed 2s/4s delays were not recognized as growing backoff.");
        TestAssert.True(complete.BackoffCapRespected, "Valid reconnect delays exceeded the cap.");
        TestAssert.True(complete.StayedOnExpectedRemoteHost, "Remote target was incorrectly treated as a local fallback.");

        var localFallback = new DisconnectAcceptanceObserver(profileId, 4, startedAt);
        localFallback.Observe(
            HostSessionSnapshot.CreateLocal() with { Generation = 5 },
            startedAt.AddSeconds(1));
        TestAssert.False(localFallback.Capture().StayedOnExpectedRemoteHost,
            "A silent fallback to localhost was accepted.");
    }

    private static void MasterSwitchRequiresExactChineseConfirmation()
    {
        using var environment = new IntegrationEnvironment();
        foreach (string? value in new[] { null, string.Empty, "true", "1", " 确认", "确认 ", "确认\r\n" })
        {
            environment.Set(IntegrationOptions.RunVariable, value);
            TestAssert.False(IntegrationOptions.IsEnabled(), $"Master switch accepted <{value}>.");
        }

        environment.Set(IntegrationOptions.RunVariable, IntegrationOptions.Confirmation);
        TestAssert.True(IntegrationOptions.IsEnabled(), "Master switch rejected exact Chinese confirmation.");
    }

    private static void DangerousSwitchesRejectAliasesAndWhitespace()
    {
        using var environment = ValidCurrentIdentityEnvironment();
        string[] switches =
        [
            "EXHYPERV_INTEGRATION_VM_WRITE",
            "EXHYPERV_INTEGRATION_DISCONNECT",
            "EXHYPERV_INTEGRATION_CONFIGURE_PREVIEW",
            "EXHYPERV_INTEGRATION_CONFIGURE",
            "EXHYPERV_INTEGRATION_ROLLBACK_VERIFY"
        ];

        foreach (string value in new[] { "true", "1", " 确认", "确认 ", "确认\n" })
        {
            foreach (string name in switches) environment.Set(name, value);
            IntegrationOptions options = IntegrationOptions.Load();
            TestAssert.False(options.EnableVmWrite, $"VM write accepted <{value}>.");
            TestAssert.False(options.EnableDisconnect, $"Disconnect accepted <{value}>.");
            TestAssert.False(options.PreviewConfiguration, $"Configuration preview accepted <{value}>.");
            TestAssert.False(options.EnableConfiguration, $"Configuration accepted <{value}>.");
            TestAssert.False(options.EnableRollbackVerification, $"Rollback accepted <{value}>.");
        }
    }

    private static void CurrentIdentityRejectsExplicitCredentialVariables()
    {
        using var environment = ValidCurrentIdentityEnvironment();
        environment.Set("EXHYPERV_INTEGRATION_USERNAME", "LAB\\Operator");
        ExpectOptionError("current mode", IntegrationOptions.Load);

        environment.Set("EXHYPERV_INTEGRATION_USERNAME", null);
        environment.Set("EXHYPERV_INTEGRATION_PASSWORD", "must-not-be-used");
        ExpectOptionError("current mode", IntegrationOptions.Load);
    }

    private static void CredentialManagerRequiresStableProfileAndUser()
    {
        using var environment = ValidCurrentIdentityEnvironment();
        environment.Set("EXHYPERV_INTEGRATION_AUTH", "credential-manager");
        ExpectOptionError("PROFILE_ID", IntegrationOptions.Load);

        Guid profileId = Guid.Parse("2de9873f-d0b0-4390-93aa-f8ca6fbd3589");
        environment.Set("EXHYPERV_INTEGRATION_PROFILE_ID", profileId.ToString("D"));
        ExpectOptionError("USERNAME", IntegrationOptions.Load);

        environment.Set("EXHYPERV_INTEGRATION_USERNAME", "LAB\\Operator");
        IntegrationOptions options = IntegrationOptions.Load();
        TestAssert.Equal(profileId, options.ProfileId);
        TestAssert.Equal(HostAuthenticationMode.ExplicitCredential, options.AuthenticationMode);
    }

    private static void SecondHostOptionsAreCoherent()
    {
        using var environment = ValidCurrentIdentityEnvironment();
        environment.Set("EXHYPERV_INTEGRATION_SECOND_DISPLAY_NAME", "第二宿主");
        ExpectOptionError("require EXHYPERV_INTEGRATION_SECOND_HOST", IntegrationOptions.Load);

        environment.Set("EXHYPERV_INTEGRATION_SECOND_DISPLAY_NAME", null);
        environment.Set("EXHYPERV_INTEGRATION_SECOND_HOST", "10.0.0.006");
        ExpectOptionError("must differ", IntegrationOptions.Load);

        Guid firstId = Guid.Parse("7712b521-5bb1-43c8-9928-22ef8742f1ad");
        environment.Set("EXHYPERV_INTEGRATION_PROFILE_ID", firstId.ToString("D"));
        environment.Set("EXHYPERV_INTEGRATION_SECOND_HOST", "10.0.0.7");
        environment.Set("EXHYPERV_INTEGRATION_SECOND_PROFILE_ID", firstId.ToString("D"));
        ExpectOptionError("different profile IDs", IntegrationOptions.Load);

        environment.Set("EXHYPERV_INTEGRATION_SECOND_PROFILE_ID", null);
        IntegrationOptions current = IntegrationOptions.Load();
        TestAssert.Equal("10.0.0.7", current.SecondHostAddress);
        TestAssert.Equal("Controlled host 10.0.0.7", current.SecondDisplayName);
        TestAssert.True(current.SecondProfileId is not null && current.SecondProfileId != Guid.Empty,
            "Current-identity second host did not receive a profile ID.");
        TestAssert.False(current.SecondProfileId == current.ProfileId,
            "The two controlled hosts reused one profile ID.");

        environment.Set("EXHYPERV_INTEGRATION_AUTH", "credential-manager");
        environment.Set("EXHYPERV_INTEGRATION_USERNAME", "LAB\\Operator");
        ExpectOptionError("SECOND_PROFILE_ID", IntegrationOptions.Load);

        Guid secondId = Guid.Parse("e75d0245-a1e6-4bad-b977-e5cbbce30eab");
        environment.Set("EXHYPERV_INTEGRATION_SECOND_PROFILE_ID", secondId.ToString("D"));
        IntegrationOptions explicitCredential = IntegrationOptions.Load();
        TestAssert.Equal(secondId, explicitCredential.SecondProfileId);
        TestAssert.Equal(HostAuthenticationMode.ExplicitCredential, explicitCredential.AuthenticationMode);
    }

    private static void RollbackCannotRunWithoutConfiguration()
    {
        using var environment = ValidCurrentIdentityEnvironment();
        environment.Set("EXHYPERV_INTEGRATION_ROLLBACK_VERIFY", IntegrationOptions.Confirmation);
        ExpectOptionError("requires configuration", IntegrationOptions.Load);
    }

    private static void ConfigurationPreviewIsReadOnlyAndUsesSameSelection()
    {
        using IntegrationEnvironment environment = ValidConfigurationEnvironment();
        environment.Set("EXHYPERV_INTEGRATION_CONFIGURE", null);
        environment.Set("EXHYPERV_INTEGRATION_CONFIGURE_PREVIEW", IntegrationOptions.Confirmation);

        IntegrationOptions options = IntegrationOptions.Load();
        TestAssert.True(options.PreviewConfiguration, "Exact configuration preview switch was rejected.");
        TestAssert.False(options.EnableConfiguration, "Preview mode enabled remote configuration.");
        TestAssert.Equal(HostPreflightAccountKind.Local, options.ConfigurationAccountKind);
        TestAssert.Equal(".\\HyperVOperator", options.ConfigurationAccountName);
        TestAssert.SequenceEqual(new uint[] { 7 }, options.ConfigurationNetworkIndexes);
        TestAssert.SequenceEqual(new[] { "10.0.0.0/24" }, options.AllowedIpv4Cidrs);

        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tests", "ExHyperV.IntegrationTests", "ControlledHostAcceptanceRunner.cs"));
        int previewGuard = source.IndexOf(
            "if (_options.PreviewConfiguration && !_options.EnableConfiguration)",
            StringComparison.Ordinal);
        int pipelineCreation = source.IndexOf("var pipeline = new HostConfigurationPipeline", StringComparison.Ordinal);
        TestAssert.True(previewGuard >= 0 && pipelineCreation > previewGuard,
            "Preview guard does not precede configuration pipeline creation.");
        string previewBranch = source.Substring(previewGuard, pipelineCreation - previewGuard);
        TestAssert.Contains("预览模式未执行任何修改", previewBranch);
        TestAssert.Contains("return;", previewBranch);
        TestAssert.Contains("[\"已启用本地账户\"]", source);
        TestAssert.Contains("[\"可选网络\"]", source);
        TestAssert.Contains("interfaceIndex = network.InterfaceIndex", source);
    }

    private static void ConfigurationValidatesNetworksAndPrivateCidrs()
    {
        using var environment = ValidConfigurationEnvironment();

        environment.Set("EXHYPERV_INTEGRATION_NETWORKS", "0");
        ExpectOptionError("interface index", IntegrationOptions.Load);

        environment.Set("EXHYPERV_INTEGRATION_NETWORKS", ",,");
        ExpectOptionError("at least one interface index", IntegrationOptions.Load);

        environment.Set("EXHYPERV_INTEGRATION_NETWORKS", "7,9");
        environment.Set("EXHYPERV_INTEGRATION_MAKE_PRIVATE", "10");
        ExpectOptionError("subset", IntegrationOptions.Load);

        environment.Set("EXHYPERV_INTEGRATION_MAKE_PRIVATE", "9");
        environment.Set("EXHYPERV_INTEGRATION_CIDRS", "not-a-cidr");
        ExpectOptionError("invalid IPv4 CIDR", IntegrationOptions.Load);

        environment.Set("EXHYPERV_INTEGRATION_CIDRS", "8.8.8.0/24");
        ExpectOptionError("RFC1918", IntegrationOptions.Load);

        environment.Set("EXHYPERV_INTEGRATION_CIDRS", "10.0.0.1/24;10.0.0.0/24");
        ExpectOptionError("duplicate", IntegrationOptions.Load);

        environment.Set("EXHYPERV_INTEGRATION_CIDRS", "10.0.0.1/24;192.168.50.0/24");
        IntegrationOptions options = IntegrationOptions.Load();
        TestAssert.SequenceEqual(new uint[] { 7, 9 }, options.ConfigurationNetworkIndexes);
        TestAssert.SequenceEqual(new uint[] { 9 }, options.NetworksToMakePrivate);
        TestAssert.SequenceEqual(new[] { "10.0.0.0/24", "192.168.50.0/24" }, options.AllowedIpv4Cidrs);
    }

    private static void ToStringNeverDisclosesPassword()
    {
        using var environment = ValidCurrentIdentityEnvironment();
        environment.Set("EXHYPERV_INTEGRATION_AUTH", "credential-manager");
        environment.Set("EXHYPERV_INTEGRATION_PROFILE_ID", "49af426f-cdb8-4b14-95b7-827f4f352aee");
        environment.Set("EXHYPERV_INTEGRATION_USERNAME", "LAB\\Operator");
        environment.Set("EXHYPERV_INTEGRATION_PASSWORD", "report-to-string-secret");

        string text = IntegrationOptions.Load().ToString();
        TestAssert.False(text.Contains("report-to-string-secret", StringComparison.Ordinal), "ToString disclosed the integration password.");
        TestAssert.Contains("Password=[REDACTED]", text);
    }

    private static void OneTimePasswordIsClearedDuringLoad()
    {
        using var environment = new IntegrationEnvironment();
        environment.Set("EXHYPERV_INTEGRATION_PASSWORD", "earliest-validation-secret");
        ExpectOptionError("EXHYPERV_INTEGRATION_HOST", IntegrationOptions.Load);
        TestAssert.True(
            Environment.GetEnvironmentVariable("EXHYPERV_INTEGRATION_PASSWORD") is null,
            "A password remained in the process environment after the earliest option validation failed.");

        environment.Set("EXHYPERV_INTEGRATION_HOST", "10.0.0.6");
        environment.Set("EXHYPERV_INTEGRATION_AUTH", "credential-manager");
        environment.Set("EXHYPERV_INTEGRATION_PROFILE_ID", "49af426f-cdb8-4b14-95b7-827f4f352aee");
        environment.Set("EXHYPERV_INTEGRATION_USERNAME", "LAB\\Operator");
        environment.Set("EXHYPERV_INTEGRATION_PASSWORD", "one-time-secret");

        _ = IntegrationOptions.Load();
        TestAssert.True(
            Environment.GetEnvironmentVariable("EXHYPERV_INTEGRATION_PASSWORD") is null,
            "A successfully captured integration password remained in the process environment.");

        environment.Set("EXHYPERV_INTEGRATION_USERNAME", null);
        environment.Set("EXHYPERV_INTEGRATION_PASSWORD", "invalid-config-secret");
        ExpectOptionError("USERNAME", IntegrationOptions.Load);
        TestAssert.True(
            Environment.GetEnvironmentVariable("EXHYPERV_INTEGRATION_PASSWORD") is null,
            "A password remained in the process environment after option validation failed.");
    }

    private static void ReportPathIsNormalizedAndValidated()
    {
        using var environment = ValidCurrentIdentityEnvironment();
        using var temp = new IntegrationTempDirectory();

        environment.Set("EXHYPERV_INTEGRATION_REPORT", Path.Combine(temp.Path, "acceptance.txt"));
        ExpectOptionError(".json file", IntegrationOptions.Load);

        string directoryPath = Path.Combine(temp.Path, "directory.json");
        Directory.CreateDirectory(directoryPath);
        environment.Set("EXHYPERV_INTEGRATION_REPORT", directoryPath);
        ExpectOptionError("not a directory", IntegrationOptions.Load);

        string relativePath = Path.Combine(".", ".codex-tasks", "remote-host-management", "raw", "acceptance.JSON");
        environment.Set("EXHYPERV_INTEGRATION_REPORT", relativePath);
        IntegrationOptions options = IntegrationOptions.Load();
        TestAssert.True(Path.IsPathFullyQualified(options.ReportPath), "The report path was not normalized to an absolute path.");
        TestAssert.True(
            string.Equals(Path.GetExtension(options.ReportPath), ".JSON", StringComparison.Ordinal),
            "The normalized report path changed the caller-provided extension casing.");
    }

    private static void ReportComputesFailedPartialAndPassedStatus()
    {
        using var temp = new IntegrationTempDirectory();

        var failed = NewReport();
        failed.Add("诊断", AcceptanceStatus.Failed, TimeSpan.Zero, "连接失败");
        Write(failed, Path.Combine(temp.Path, "failed.json"));
        TestAssert.Equal(AcceptanceStatus.Failed, failed.OverallStatus);
        TestAssert.True(failed.HasFailures, "Failed report did not expose HasFailures.");

        var partial = NewReport();
        partial.Add("诊断", AcceptanceStatus.Passed, TimeSpan.Zero, "通过");
        partial.Add("写入", AcceptanceStatus.Skipped, TimeSpan.Zero, "未授权");
        Write(partial, Path.Combine(temp.Path, "partial.json"));
        TestAssert.Equal(AcceptanceStatus.Partial, partial.OverallStatus);

        var passed = NewReport();
        passed.Add("诊断", AcceptanceStatus.Passed, TimeSpan.Zero, "通过");
        Write(passed, Path.Combine(temp.Path, "passed.json"));
        TestAssert.Equal(AcceptanceStatus.Passed, passed.OverallStatus);
    }

    private static void ReportIsAtomicUtf8AndRedacted()
    {
        using var temp = new IntegrationTempDirectory();
        string path = Path.Combine(temp.Path, "acceptance.json");
        File.WriteAllText(path, "old-report", new UTF8Encoding(false));
        var report = new AcceptanceReport
        {
            HostAddress = "10.0.0.6",
            HostDisplayName = "password=top-level-secret",
            AuthenticationMode = "token=top-level-token"
        };
        report.Add(
            "中文验收",
            AcceptanceStatus.Passed,
            TimeSpan.FromMilliseconds(-1),
            "password=message-secret",
            new Dictionary<string, object?>
            {
                ["detail"] = "token=detail-secret",
                ["password"] = "bare-detail-secret"
            });

        string written = Write(report, path);
        TestAssert.Equal(Path.GetFullPath(path), written);
        byte[] bytes = File.ReadAllBytes(path);
        TestAssert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "Acceptance report contains a UTF-8 BOM.");
        string json = new UTF8Encoding(false, true).GetString(bytes);
        foreach (string secret in new[] { "top-level-secret", "top-level-token", "message-secret", "detail-secret", "bare-detail-secret" })
            TestAssert.False(json.Contains(secret, StringComparison.Ordinal), $"Acceptance report disclosed {secret}.");
        TestAssert.Contains("中文验收", json);
        TestAssert.Contains("[REDACTED]", json);
        TestAssert.Equal(0, Directory.GetFiles(temp.Path, "*.tmp-*", SearchOption.TopDirectoryOnly).Length);
    }

    private static void CancelledReportPreservesExistingFile()
    {
        using var temp = new IntegrationTempDirectory();
        string path = Path.Combine(temp.Path, "acceptance.json");
        byte[] original = Encoding.UTF8.GetBytes("existing-report");
        File.WriteAllBytes(path, original);
        var report = NewReport();
        report.Add("诊断", AcceptanceStatus.Passed, TimeSpan.Zero, "通过");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            report.WriteAsync(path, cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancelled report write unexpectedly succeeded.");
        }
        catch (OperationCanceledException)
        {
        }

        TestAssert.SequenceEqual(original, File.ReadAllBytes(path));
        TestAssert.Equal(0, Directory.GetFiles(temp.Path, "*.tmp-*", SearchOption.TopDirectoryOnly).Length);
    }

    private static IntegrationEnvironment ValidCurrentIdentityEnvironment()
    {
        var environment = new IntegrationEnvironment();
        environment.Set("EXHYPERV_INTEGRATION_HOST", "10.0.0.6");
        return environment;
    }

    private static IntegrationEnvironment ValidConfigurationEnvironment()
    {
        IntegrationEnvironment environment = ValidCurrentIdentityEnvironment();
        environment.Set("EXHYPERV_INTEGRATION_CONFIGURE", IntegrationOptions.Confirmation);
        environment.Set("EXHYPERV_INTEGRATION_ACCOUNT_KIND", "local");
        environment.Set("EXHYPERV_INTEGRATION_ACCOUNT", ".\\HyperVOperator");
        environment.Set("EXHYPERV_INTEGRATION_NETWORKS", "7");
        environment.Set("EXHYPERV_INTEGRATION_CIDRS", "10.0.0.0/24");
        return environment;
    }

    private static AcceptanceReport NewReport() => new()
    {
        HostAddress = "10.0.0.6",
        HostDisplayName = "受控宿主",
        AuthenticationMode = "当前 Windows 身份"
    };

    private static HostSessionSnapshot Snapshot(
        HostProfile profile,
        long generation,
        bool stale,
        DateTimeOffset refreshedAt,
        HostReconnectState reconnect,
        HostChannelState consoleChannel = HostChannelState.Available,
        HostConnectionState? connectionState = null)
    {
        var session = new ActiveHostSession(
            generation,
            HostTarget.FromProfile(profile),
            connectionState ?? (stale ? HostConnectionState.Reconnecting : HostConnectionState.Connected),
            HostChannelState.Available,
            consoleChannel,
            stale);
        return new HostSessionSnapshot(
            HostId.FromProfile(profile),
            generation,
            session.Target,
            session.ConnectionState,
            session.ManagementChannel,
            session.ConsoleChannel,
            session.HasStaleData)
        {
            Reconnect = reconnect,
            BasicSnapshot = new HostBasicSnapshot("LAB-HV-06", "Windows", "Running", 2, refreshedAt),
            Capabilities = HostCapabilityMatrix.Create(session, isSwitching: false)
        };
    }

    private static string Write(AcceptanceReport report, string path) =>
        report.WriteAsync(path).GetAwaiter().GetResult();

    private static void ExpectOptionError(string expected, Func<IntegrationOptions> action)
    {
        try
        {
            _ = action();
        }
        catch (IntegrationOptionException ex)
        {
            TestAssert.Contains(expected, ex.Message);
            return;
        }
        throw new InvalidOperationException($"Expected IntegrationOptionException containing <{expected}>.");
    }

    private sealed class IntegrationEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _original = EnvironmentVariableNames.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        public IntegrationEnvironment()
        {
            foreach (string name in EnvironmentVariableNames)
                Environment.SetEnvironmentVariable(name, null);
        }

        public void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

        public void Dispose()
        {
            foreach ((string name, string? value) in _original)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class IntegrationTempDirectory : IDisposable
    {
        public IntegrationTempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ExHyperV.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
