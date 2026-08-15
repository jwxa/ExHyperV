using System.Text;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Services.Logging;

var tests = new List<(string Name, Action Run)>
{
    ("Logging_WritesExpectedPathAndUtf8WithoutBom", WritesExpectedPathAndUtf8WithoutBom),
    ("Logging_RedactsMessagesPropertiesAndCredentialObjects", RedactsMessagesPropertiesAndCredentialObjects),
    ("Logging_RotatesAtLimitAndKeepsOnlyTwoFiles", RotatesAtLimitAndKeepsOnlyTwoFiles),
    ("Logging_AppendsAcrossRestart", AppendsAcrossRestart),
    ("Logging_SerializesConcurrentWriters", SerializesConcurrentWriters),
    ("Logging_TrimsOversizedExistingLogWithoutBreakingUtf8", TrimsOversizedExistingLogWithoutBreakingUtf8),
    ("Logging_TruncatesEntriesWithoutBreakingUtf8", TruncatesEntriesWithoutBreakingUtf8),
    ("Logging_ReportsInitializationFailureWithoutThrowing", ReportsInitializationFailureWithoutThrowing),
    ("Logging_ReportsRuntimeWriteFailureWithoutThrowing", ReportsRuntimeWriteFailureWithoutThrowing),
    ("Profiles_SaveMultipleWithoutSecretsAndLoadThem", SaveMultipleProfilesWithoutSecretsAndLoadThem),
    ("Profiles_RejectInvalidIpv4AndAuthenticationReferences", RejectInvalidIpv4AndAuthenticationReferences),
    ("Profiles_ValidationFailurePreservesExistingFile", ProfileValidationFailurePreservesExistingFile),
    ("Profiles_DuplicateAddressIsRejectedWithoutReplacingFile", DuplicateAddressIsRejectedWithoutReplacingFile),
    ("Profiles_CorruptXmlIsExplainableAndPreserved", CorruptXmlIsExplainableAndPreserved),
    ("Profiles_UnsupportedVersionIsExplainableAndPreserved", UnsupportedVersionIsExplainableAndPreserved),
    ("Profiles_EditRememberedProfileWithoutReadingPassword", EditRememberedProfileWithoutReadingPassword),
    ("Profiles_ReplacingRememberedCredentialCleansOldTarget", ReplacingRememberedCredentialCleansOldTarget),
    ("Profiles_DeleteCanCleanCredentialAndRollbackOnFailure", DeleteProfileCanCleanCredentialAndRollbackOnFailure),
    ("Credentials_WriteReadAndDeleteWindowsCredential", WriteReadAndDeleteWindowsCredential),
    ("Sessions_NewCoordinatorAlwaysStartsLocal", NewCoordinatorAlwaysStartsLocal),
    ("Sessions_SelectingProfileDoesNotActivateOrAdvanceGeneration", SelectingProfileDoesNotActivateOrAdvanceGeneration),
    ("Sessions_StateEventsContainImmutableCoherentSnapshots", SessionStateEventsContainImmutableCoherentSnapshots),
    ("Sessions_ConcurrentSelectionKeepsLocalSessionCoherent", ConcurrentSelectionKeepsLocalSessionCoherent)
};
tests.AddRange(DiagnosticsTests.All);
tests.AddRange(HostSessionRegistryTests.All);
tests.AddRange(HostSessionRegistryReconnectTests.All);
tests.AddRange(HostDisconnectTests.All);
tests.AddRange(HostOperationRouterTests.All);
tests.AddRange(VirtualMachinesMultiHostWiringTests.All);
tests.AddRange(SessionSwitchTests.All);
tests.AddRange(VmOperationTests.All);
tests.AddRange(ConsoleSessionTests.All);
tests.AddRange(HostConsoleRegistryTests.All);
tests.AddRange(ReconnectTests.All);
tests.AddRange(CapabilityTests.All);
tests.AddRange(PreflightTests.All);
tests.AddRange(ConfigurationTests.All);
tests.AddRange(WmiApiTests.All);
tests.AddRange(RemoteHostEndToEndTests.All);
tests.AddRange(IntegrationRunnerTests.All);
tests.AddRange(SupportArtifactTests.All);

int failures = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}\n{ex}");
    }
    finally
    {
        AppLog.Shutdown();
    }
}

Console.WriteLine($"RESULT total={tests.Count} passed={tests.Count - failures} failed={failures}");
return failures == 0 ? 0 : 1;

static void WritesExpectedPathAndUtf8WithoutBom()
{
    using var temp = new TempDirectory();
    var timestamp = new DateTimeOffset(2026, 8, 13, 10, 20, 30, 456, TimeSpan.FromHours(8));
    using var logger = CreateLogger(temp.Path, clock: () => timestamp);

    logger.Write(
        AppLogLevel.Information,
        "连接检测",
        "主机管理功能可用。",
        new AppLogContext("LAB-HV-06", 7));

    string expectedPath = Path.Combine(temp.Path, RollingFileLogger.CurrentFileName);
    Assert.True(File.Exists(expectedPath), "Current log was not created in the configured directory.");
    byte[] bytes = File.ReadAllBytes(expectedPath);
    Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 BOM must not be written.");

    string text = new UTF8Encoding(false, true).GetString(bytes);
    Assert.Contains("[2026-08-13 10:20:30.456 +08:00] [信息] [连接检测]", text);
    Assert.Contains("[宿主=LAB-HV-06] [会话=7] 主机管理功能可用。", text);
}

static void RedactsMessagesPropertiesAndCredentialObjects()
{
    using var temp = new TempDirectory();
    using var logger = CreateLogger(temp.Path);
    var properties = new Dictionary<string, object?>
    {
        ["password"] = "plain-property-password",
        ["password=property-key-secret"] = "must-also-be-redacted",
        ["AccessToken"] = "plain-property-token",
        ["identity"] = "JWXA\\Administrator",
        ["auth"] = new FakeCredential("credential-object-secret")
    };

    logger.Write(
        AppLogLevel.Error,
        "身份",
        "password=message-password; token:message-token; passwd=multi word password, Authorization: Bearer bearer-token {\"client_secret\":\"json-secret\"}",
        new AppLogContext(Properties: properties),
        new InvalidOperationException("refresh_token=exception-token"));

    string text = File.ReadAllText(logger.CurrentFilePath, Encoding.UTF8);
    foreach (string secret in new[]
    {
        "plain-property-password", "property-key-secret", "must-also-be-redacted",
        "plain-property-token", "credential-object-secret",
        "message-password", "message-token", "multi word password", "bearer-token", "json-secret", "exception-token"
    })
    {
        Assert.DoesNotContain(secret, text);
    }

    Assert.Contains("password=[REDACTED]", text);
    Assert.Contains("token:[REDACTED]", text);
    Assert.Contains("identity=JWXA\\Administrator", text);
    Assert.Contains("auth=[REDACTED]", text);
}

static void RotatesAtLimitAndKeepsOnlyTwoFiles()
{
    using var temp = new TempDirectory();
    const long maxBytes = 1024;
    using var logger = CreateLogger(temp.Path, maxBytes);

    for (int index = 0; index < 60; index++)
        logger.Write(AppLogLevel.Information, "轮转", $"entry-{index:D3} {new string('数', 32)}");

    string[] files = Directory.GetFiles(temp.Path).Select(Path.GetFileName).OrderBy(name => name).ToArray()!;
    Assert.SequenceEqual(new[] { RollingFileLogger.PreviousFileName, RollingFileLogger.CurrentFileName }, files);
    Assert.InRange(new FileInfo(logger.CurrentFilePath).Length, 1, maxBytes);
    Assert.InRange(new FileInfo(logger.PreviousFilePath).Length, 1, maxBytes);
}

static void AppendsAcrossRestart()
{
    using var temp = new TempDirectory();
    using (var first = CreateLogger(temp.Path))
        first.Write(AppLogLevel.Information, "重启", "before-restart");
    long firstLength = new FileInfo(Path.Combine(temp.Path, RollingFileLogger.CurrentFileName)).Length;

    using (var second = CreateLogger(temp.Path))
        second.Write(AppLogLevel.Information, "重启", "after-restart");

    string text = File.ReadAllText(Path.Combine(temp.Path, RollingFileLogger.CurrentFileName), Encoding.UTF8);
    Assert.Contains("before-restart", text);
    Assert.Contains("after-restart", text);
    Assert.True(new FileInfo(Path.Combine(temp.Path, RollingFileLogger.CurrentFileName)).Length > firstLength, "Restart should append to the current log.");
}

static void SerializesConcurrentWriters()
{
    using var temp = new TempDirectory();
    using var logger = CreateLogger(temp.Path, 4 * 1024 * 1024);
    const int workers = 12;
    const int perWorker = 80;

    Parallel.For(0, workers, worker =>
    {
        for (int index = 0; index < perWorker; index++)
            logger.Write(AppLogLevel.Debug, "并发", $"event-{worker:D2}-{index:D3}");
    });

    string[] lines = File.ReadAllLines(logger.CurrentFilePath, Encoding.UTF8);
    Assert.Equal(workers * perWorker, lines.Length);
    Assert.Equal(lines.Length, lines.Distinct(StringComparer.Ordinal).Count());
    Assert.True(lines.All(line => line.Contains("[调试] [并发]", StringComparison.Ordinal)), "A concurrent log line was corrupted.");
}

static void TrimsOversizedExistingLogWithoutBreakingUtf8()
{
    using var temp = new TempDirectory();
    string current = Path.Combine(temp.Path, RollingFileLogger.CurrentFileName);
    File.WriteAllText(current, string.Concat(Enumerable.Repeat("中文日志行\n", 200)), new UTF8Encoding(false));

    using var logger = CreateLogger(temp.Path, 257);
    Assert.False(File.Exists(current), "Oversized current log should be moved before the next write.");
    byte[] previousBytes = File.ReadAllBytes(logger.PreviousFilePath);
    Assert.InRange(previousBytes.LongLength, 1, 257);
    _ = new UTF8Encoding(false, true).GetString(previousBytes);

    logger.Write(AppLogLevel.Information, "恢复", "new-current");
    Assert.Contains("new-current", File.ReadAllText(current, Encoding.UTF8));
}

static void TruncatesEntriesWithoutBreakingUtf8()
{
    for (long maxBytes = 1; maxBytes <= 128; maxBytes++)
    {
        using var temp = new TempDirectory();
        using var logger = CreateLogger(temp.Path, maxBytes);
        logger.Write(AppLogLevel.Information, "编码", string.Concat(Enumerable.Repeat("中文😀", 100)));

        byte[] bytes = File.ReadAllBytes(logger.CurrentFilePath);
        Assert.InRange(bytes.LongLength, 0, maxBytes);
        _ = new UTF8Encoding(false, true).GetString(bytes);
    }
}

static void ReportsInitializationFailureWithoutThrowing()
{
    using var temp = new TempDirectory();
    string blockingFile = Path.Combine(temp.Path, "not-a-directory");
    File.WriteAllText(blockingFile, "block");
    string? reported = null;
    void Handler(string reason) => reported = reason;
    AppLog.BecameUnavailable += Handler;
    try
    {
        AppLog.Initialize(blockingFile);
        Assert.False(AppLog.IsAvailable, "Logger should report unavailable for an invalid base directory.");
        Assert.NotNullOrWhiteSpace(AppLog.UnavailableReason);
        Assert.Equal(AppLog.UnavailableReason, reported);
        reported = null;
        AppLog.Information("初始化失败", "unavailable logging must remain a no-op");
        Assert.Null(reported, "A write after initialization failure should not retry the unavailable logger.");
    }
    finally
    {
        AppLog.BecameUnavailable -= Handler;
    }
}

static void ReportsRuntimeWriteFailureWithoutThrowing()
{
    using var temp = new TempDirectory();
    AppLog.Initialize(temp.Path);
    Assert.True(AppLog.IsAvailable, "Logger did not initialize for runtime failure test.");

    string logDirectory = AppLog.LogDirectory;
    File.Delete(Path.Combine(logDirectory, RollingFileLogger.CurrentFileName));
    Directory.Delete(logDirectory);
    File.WriteAllText(logDirectory, "block future writes");

    string? reported = null;
    void Handler(string reason) => reported = reason;
    AppLog.BecameUnavailable += Handler;
    try
    {
        AppLog.Information("运行时", "this write must fail without escaping");
        Assert.False(AppLog.IsAvailable, "Runtime write failure should make logging unavailable.");
        Assert.NotNullOrWhiteSpace(reported);
    }
    finally
    {
        AppLog.BecameUnavailable -= Handler;
        File.Delete(logDirectory);
    }
}

static void SaveMultipleProfilesWithoutSecretsAndLoadThem()
{
    using var temp = new TempDirectory();
    string filePath = Path.Combine(temp.Path, "Hosts.xml");
    var store = new HostProfileStore(filePath);
    Guid currentIdentityId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    Guid storedCredentialId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    HostProfile[] expected =
    [
        new(currentIdentityId, "  实验室主机  ", "10.0.0.6"),
        new(
            storedCredentialId,
            "备用主机",
            "192.168.50.8",
            HostAuthenticationMode.ExplicitCredential,
            "LAB\\HyperVAdmin",
            HostCredentialTarget.ForProfile(storedCredentialId))
    ];

    store.Save(expected);

    byte[] bytes = File.ReadAllBytes(filePath);
    Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "Profile XML must not contain a UTF-8 BOM.");
    string xml = new UTF8Encoding(false, true).GetString(bytes);
    Assert.Contains("<HostProfiles version=\"1\">", xml);
    Assert.Contains("authentication=\"CurrentWindowsIdentity\"", xml);
    Assert.Contains("authentication=\"ExplicitCredential\"", xml);
    Assert.Contains("userName=\"LAB\\HyperVAdmin\"", xml);
    Assert.Contains(HostCredentialTarget.ForProfile(storedCredentialId), xml);
    Assert.DoesNotContain("Password", xml);
    Assert.DoesNotContain("P@ssword", xml);

    IReadOnlyList<HostProfile> actual = store.Load();
    Assert.Equal(2, actual.Count);
    Assert.Equal(expected[0] with { DisplayName = "实验室主机" }, actual[0]);
    Assert.Equal(expected[1], actual[1]);
}

static void RejectInvalidIpv4AndAuthenticationReferences()
{
    foreach (string address in new[] { "host.example", "::1", "127.0.0.1", "0.1.2.3", "224.0.0.1", "10.1", "10.0.0.999" })
    {
        Assert.Throws<HostProfileValidationException>(() =>
            HostProfileValidator.ValidateAndNormalize(new HostProfile(Guid.NewGuid(), "无效地址", address)));
    }

    Guid currentIdentityId = Guid.NewGuid();
    Assert.Throws<HostProfileValidationException>(() =>
        HostProfileValidator.ValidateAndNormalize(new HostProfile(
            currentIdentityId,
            "当前身份",
            "10.0.0.6",
            HostAuthenticationMode.CurrentWindowsIdentity,
            null,
            HostCredentialTarget.ForProfile(currentIdentityId))));

    Assert.Throws<HostProfileValidationException>(() =>
        HostProfileValidator.ValidateAndNormalize(new HostProfile(
            Guid.NewGuid(),
            "错误引用",
            "10.0.0.7",
            HostAuthenticationMode.ExplicitCredential,
            "LAB\\HyperVAdmin",
            "ExHyperV/RemoteHost/not-this-profile")));

    HostProfile transientCredential = HostProfileValidator.ValidateAndNormalize(new HostProfile(
        Guid.NewGuid(),
        "不记住凭据",
        "10.0.0.8",
        HostAuthenticationMode.ExplicitCredential,
        "LAB\\Operator"));
    Assert.Equal("LAB\\Operator", transientCredential.UserName);
    Assert.Null(transientCredential.CredentialTarget, "An explicit credential can be used without being remembered.");
}

static void ProfileValidationFailurePreservesExistingFile()
{
    using var temp = new TempDirectory();
    string filePath = Path.Combine(temp.Path, "Hosts.xml");
    var store = new HostProfileStore(filePath);
    var original = new HostProfile(Guid.NewGuid(), "有效主机", "10.0.0.6");
    store.Save([original]);
    byte[] before = File.ReadAllBytes(filePath);

    Assert.Throws<HostProfileValidationException>(() =>
        store.Save([original, new HostProfile(Guid.NewGuid(), "无效主机", "not-an-ipv4-address")]));

    Assert.SequenceEqual(before, File.ReadAllBytes(filePath));
    Assert.Equal(original, store.Load().Single());
    Assert.Equal(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
}

static void DuplicateAddressIsRejectedWithoutReplacingFile()
{
    using var temp = new TempDirectory();
    string filePath = Path.Combine(temp.Path, "Hosts.xml");
    var store = new HostProfileStore(filePath);
    var original = new HostProfile(Guid.NewGuid(), "主机 A", "10.0.0.6");
    store.Save([original]);
    byte[] before = File.ReadAllBytes(filePath);

    try
    {
        store.Save(
        [
            original,
            new HostProfile(Guid.NewGuid(), "主机 B", "010.000.000.006")
        ]);
        throw new InvalidOperationException("Duplicate IPv4 profile was accepted.");
    }
    catch (HostProfileValidationException ex)
    {
        Assert.Contains("10.0.0.6", ex.Message);
        Assert.Contains("已存在", ex.Message);
    }

    Assert.SequenceEqual(before, File.ReadAllBytes(filePath));
    Assert.Equal(original, store.Load().Single());
    Assert.Equal(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
}

static void CorruptXmlIsExplainableAndPreserved()
{
    using var temp = new TempDirectory();
    string profilePath = Path.Combine(temp.Path, "Hosts.xml");
    string settingsPath = Path.Combine(temp.Path, "Config.xml");
    byte[] corruptProfiles = new UTF8Encoding(false).GetBytes("<HostProfiles version=\"1\"><Host");
    byte[] existingSettings = new UTF8Encoding(false).GetBytes("<Config><Language>zh-CN</Language></Config>");
    File.WriteAllBytes(profilePath, corruptProfiles);
    File.WriteAllBytes(settingsPath, existingSettings);
    var store = new HostProfileStore(profilePath);

    try
    {
        store.Load();
        throw new InvalidOperationException("Malformed Hosts.xml was accepted.");
    }
    catch (InvalidDataException ex)
    {
        Assert.Contains("主机配置文件格式损坏", ex.Message);
        Assert.Contains("Hosts.xml", ex.Message);
        Assert.True(ex.InnerException is System.Xml.XmlException, "The original XML parse cause must remain available for diagnostics.");
    }

    Assert.SequenceEqual(corruptProfiles, File.ReadAllBytes(profilePath));
    Assert.SequenceEqual(existingSettings, File.ReadAllBytes(settingsPath));
}

static void UnsupportedVersionIsExplainableAndPreserved()
{
    using var temp = new TempDirectory();
    string profilePath = Path.Combine(temp.Path, "Hosts.xml");
    byte[] unsupportedProfiles = new UTF8Encoding(false).GetBytes("<HostProfiles version=\"99\" />");
    File.WriteAllBytes(profilePath, unsupportedProfiles);
    var store = new HostProfileStore(profilePath);

    try
    {
        store.Load();
        throw new InvalidOperationException("Unsupported Hosts.xml version was accepted.");
    }
    catch (NotSupportedException ex)
    {
        Assert.Contains("不支持的主机配置版本：99", ex.Message);
    }

    Assert.SequenceEqual(unsupportedProfiles, File.ReadAllBytes(profilePath));
}

static void EditRememberedProfileWithoutReadingPassword()
{
    using var temp = new TempDirectory();
    var profileStore = new HostProfileStore(Path.Combine(temp.Path, "Hosts.xml"));
    var credentialStore = new FakeWindowsCredentialStore();
    var manager = new HostProfileManager(profileStore, credentialStore);
    Guid id = Guid.NewGuid();
    var credential = new WindowsCredential("LAB\\Operator", "not-serialized-password");

    HostProfile saved = manager.Save(
        new HostProfile(id, "初始名称", "10.0.0.6", HostAuthenticationMode.ExplicitCredential, credential.UserName),
        credential);
    manager.Save(new HostProfile(Guid.NewGuid(), "第二台主机", "10.0.0.7"));
    HostProfile edited = manager.Save(saved with { DisplayName = "编辑后的名称" });

    Assert.Equal("编辑后的名称", edited.DisplayName);
    Assert.Equal(id, manager.GetAll()[0].Id);
    Assert.Equal(1, credentialStore.SaveCount);
    Assert.Equal(0, credentialStore.ReadCount);
    Assert.Equal(0, credentialStore.DeleteCount);
    Assert.DoesNotContain(credential.Password, File.ReadAllText(profileStore.FilePath, Encoding.UTF8));
}

static void DeleteProfileCanCleanCredentialAndRollbackOnFailure()
{
    using var temp = new TempDirectory();
    var profileStore = new HostProfileStore(Path.Combine(temp.Path, "Hosts.xml"));
    var credentialStore = new FakeWindowsCredentialStore();
    var manager = new HostProfileManager(profileStore, credentialStore);
    Guid id = Guid.NewGuid();
    var credential = new WindowsCredential("LAB\\Operator", "delete-test-password");
    manager.Save(
        new HostProfile(id, "待删除主机", "10.0.0.6", HostAuthenticationMode.ExplicitCredential, credential.UserName),
        credential);

    credentialStore.ThrowOnDelete = true;
    Assert.Throws<InvalidOperationException>(() => manager.Delete(id, deleteRememberedCredential: true));
    Assert.Equal(id, manager.GetAll().Single().Id);

    credentialStore.ThrowOnDelete = false;
    Assert.True(manager.Delete(id, deleteRememberedCredential: true), "Profile deletion should succeed after credential cleanup succeeds.");
    Assert.Equal(0, manager.GetAll().Count);
    Assert.False(credentialStore.Contains(HostCredentialTarget.ForProfile(id)), "Credential target remained after profile deletion.");
}

static void ReplacingRememberedCredentialCleansOldTarget()
{
    using var temp = new TempDirectory();
    var profileStore = new HostProfileStore(Path.Combine(temp.Path, "Hosts.xml"));
    var credentialStore = new FakeWindowsCredentialStore();
    var manager = new HostProfileManager(profileStore, credentialStore);
    Guid id = Guid.NewGuid();
    string target = HostCredentialTarget.ForProfile(id);
    HostProfile saved = manager.Save(
        new HostProfile(id, "凭据切换", "10.0.0.6", HostAuthenticationMode.ExplicitCredential, "LAB\\Old"),
        new WindowsCredential("LAB\\Old", "old-password"));

    HostProfile transient = manager.Save(
        saved with { UserName = "LAB\\New", CredentialTarget = null },
        credentialToRemember: null);

    Assert.Null(transient.CredentialTarget, "Transient explicit credential must not retain a stored target.");
    Assert.False(credentialStore.Contains(target), "Removing the credential reference must clean the old target.");
}

static void WriteReadAndDeleteWindowsCredential()
{
    string target = "ExHyperV.Tests/" + Guid.NewGuid().ToString("N");
    var store = new WindowsCredentialStore();
    var expected = new WindowsCredential("LAB\\Administrator", "中文-P@ssword-" + Guid.NewGuid().ToString("N"));
    try
    {
        store.Save(target, expected);
        Assert.True(store.TryRead(target, out WindowsCredential? actual), "Saved Windows credential could not be read.");
        Assert.Equal(expected, actual);
        Assert.True(store.Delete(target), "Saved Windows credential could not be deleted.");
        Assert.False(store.TryRead(target, out _), "Deleted Windows credential was still readable.");
        Assert.False(store.Delete(target), "Deleting a missing Windows credential should return false.");
    }
    finally
    {
        store.Delete(target);
    }
}

static void NewCoordinatorAlwaysStartsLocal()
{
    var coordinator = new ActiveHostSessionCoordinator();

    ActiveHostCoordinatorSnapshot snapshot = coordinator.Current;
    Assert.True(snapshot.ActiveSession.Target.IsLocal, "A new coordinator must start with the local host active.");
    Assert.Equal(1L, snapshot.ActiveSession.Generation);
    Assert.Equal(HostConnectionState.LocalConnected, snapshot.ActiveSession.ConnectionState);
    Assert.Equal(HostChannelState.Available, snapshot.ActiveSession.ManagementChannel);
    Assert.Equal(HostChannelState.Available, snapshot.ActiveSession.ConsoleChannel);
    Assert.False(snapshot.ActiveSession.HasStaleData, "Local startup data must not be stale.");
    Assert.Null(snapshot.SelectedProfile, "No saved remote profile should be selected at startup.");
}

static void SelectingProfileDoesNotActivateOrAdvanceGeneration()
{
    var coordinator = new ActiveHostSessionCoordinator();
    ActiveHostCoordinatorSnapshot before = coordinator.Current;
    var profile = new HostProfile(Guid.NewGuid(), "远程主机", "10.0.0.6");

    coordinator.SelectProfile(profile);

    ActiveHostCoordinatorSnapshot after = coordinator.Current;
    Assert.Equal(profile, after.SelectedProfile);
    Assert.Equal(before.ActiveSession, after.ActiveSession);
    Assert.Equal(before.ActiveSession.Generation, after.ActiveSession.Generation);
    Assert.True(after.ActiveSession.Target.IsLocal, "Selecting a profile must not switch the active host.");
    Assert.Null(before.SelectedProfile, "Previously captured snapshots must remain immutable.");
}

static void SessionStateEventsContainImmutableCoherentSnapshots()
{
    var coordinator = new ActiveHostSessionCoordinator();
    var profile = new HostProfile(Guid.NewGuid(), "远程主机", "10.0.0.6");
    var changes = new List<ActiveHostStateChangedEventArgs>();
    coordinator.StateChanged += (_, change) => changes.Add(change);

    coordinator.SelectProfile(profile);
    coordinator.SelectProfile(profile);
    coordinator.StateChanged += (_, _) => throw new InvalidOperationException("Subscriber failure must be isolated.");
    coordinator.ResetToLocal();

    Assert.Equal(3, changes.Count);
    Assert.Null(changes[0].Previous.SelectedProfile, "First event must contain the pre-selection snapshot.");
    Assert.Equal(profile, changes[0].Current.SelectedProfile);
    Assert.Equal(profile, changes[1].Previous.SelectedProfile);
    Assert.Equal(
        HostCapabilityReasonCode.HostSwitchInProgress,
        changes[1].Current.Capabilities[HostCapabilityKind.VmWrite].ReasonCode);
    Assert.Equal(1L, changes[1].Current.ActiveSession.Generation);
    Assert.Null(changes[2].Current.SelectedProfile, "Resetting an already-local session must only clear selection.");
    Assert.Equal(1L, changes[2].Current.ActiveSession.Generation);
    Assert.Equal(HostCapabilityReasonCode.None, changes[2].Current.Capabilities[HostCapabilityKind.VmWrite].ReasonCode);

    coordinator.SelectProfile(profile);
    coordinator.CommitActiveSession(new ActiveHostSession(
        2,
        HostTarget.FromProfile(profile),
        HostConnectionState.Connected,
        HostChannelState.Available,
        HostChannelState.Available,
        HasStaleData: false));
    coordinator.ResetToLocal();

    Assert.Equal(7, changes.Count);
    Assert.Equal(
        HostCapabilityReasonCode.HostSwitchInProgress,
        changes[5].Current.Capabilities[HostCapabilityKind.VmWrite].ReasonCode);
    Assert.Equal(2L, changes[5].Current.ActiveSession.Generation);
    Assert.Equal(3L, changes[6].Current.ActiveSession.Generation);
    Assert.True(changes[6].Current.ActiveSession.Target.IsLocal, "Reset must publish a local active session.");
    Assert.Null(changes[6].Current.SelectedProfile, "Returning to local must clear the remote profile selection.");
}

static void ConcurrentSelectionKeepsLocalSessionCoherent()
{
    var coordinator = new ActiveHostSessionCoordinator();
    HostProfile[] profiles = Enumerable.Range(1, 32)
        .Select(index => new HostProfile(Guid.NewGuid(), $"主机 {index}", $"10.0.0.{index}"))
        .ToArray();

    Parallel.ForEach(profiles, profile =>
    {
        coordinator.SelectProfile(profile);
        ActiveHostCoordinatorSnapshot snapshot = coordinator.Current;
        Assert.True(snapshot.ActiveSession.Target.IsLocal, "Concurrent selection changed the active host.");
        Assert.Equal(1L, snapshot.ActiveSession.Generation);
    });

    ActiveHostCoordinatorSnapshot final = coordinator.Current;
    Assert.True(final.ActiveSession.Target.IsLocal, "Concurrent selection left a non-local active host.");
    Assert.Equal(1L, final.ActiveSession.Generation);
    Assert.True(profiles.Any(profile => profile == final.SelectedProfile), "Final selection was not one of the submitted profiles.");
}

static RollingFileLogger CreateLogger(string directory, long maxBytes = 1024 * 1024, Func<DateTimeOffset>? clock = null) =>
    new(new RollingFileLoggerOptions
    {
        LogDirectory = directory,
        MaxFileBytes = maxBytes,
        Clock = clock ?? (() => new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.FromHours(8)))
    });

sealed record FakeCredential(string Password);

sealed class FakeWindowsCredentialStore : IWindowsCredentialStore
{
    private readonly Dictionary<string, WindowsCredential> _credentials = new(StringComparer.Ordinal);

    public int SaveCount { get; private set; }
    public int ReadCount { get; private set; }
    public int DeleteCount { get; private set; }
    public bool ThrowOnDelete { get; set; }

    public void Save(string target, WindowsCredential credential)
    {
        SaveCount++;
        _credentials[target] = credential;
    }

    public bool TryRead(string target, out WindowsCredential? credential)
    {
        ReadCount++;
        return _credentials.TryGetValue(target, out credential);
    }

    public bool Delete(string target)
    {
        DeleteCount++;
        if (ThrowOnDelete) throw new InvalidOperationException("Simulated credential deletion failure.");
        return _credentials.Remove(target);
    }

    public bool Contains(string target) => _credentials.ContainsKey(target);
}

sealed class TempDirectory : IDisposable
{
    public TempDirectory()
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

static class Assert
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

    public static void DoesNotContain(string expected, string actual)
    {
        if (actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Text unexpectedly contained sensitive value <{expected}>.");
    }

    public static void InRange(long actual, long minimum, long maximum)
    {
        if (actual < minimum || actual > maximum)
            throw new InvalidOperationException($"Expected {actual} to be in range [{minimum}, {maximum}].");
    }

    public static void NotNullOrWhiteSpace(string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual)) throw new InvalidOperationException("Expected a non-empty value.");
    }

    public static void Null<T>(T? actual, string message)
    {
        if (actual is not null) throw new InvalidOperationException(message);
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Expected {typeof(TException).Name}, but found {ex.GetType().Name}.", ex);
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
    }
}
