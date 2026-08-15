using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;

internal static class HostLogFeedTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Logging_StructuredEntryIsImmutableAndSanitized", StructuredEntryIsImmutableAndSanitized),
        ("Logging_FeedKeepsBoundedHistoryPerHost", FeedKeepsBoundedHistoryPerHost),
        ("Logging_SubscriptionsAreHostScopedAndDisposable", SubscriptionsAreHostScopedAndDisposable),
        ("Logging_AppLogSharesOneSanitizedEntryWithDiskAndFeed", AppLogSharesOneSanitizedEntryWithDiskAndFeed),
        ("Logging_DiagnosticStepsStreamWithHostAndErrorCategory", DiagnosticStepsStreamWithHostAndErrorCategory)
    ];

    private static void StructuredEntryIsImmutableAndSanitized()
    {
        HostId hostId = HostId.FromProfileId(Guid.Parse("20202020-2020-2020-2020-202020202020"));
        var properties = new Dictionary<string, object?>
        {
            ["password"] = "property-password",
            ["AccessToken"] = "property-token",
            ["identity"] = "LAB\\HyperVAdmin",
            ["auth"] = new SecretCredential("credential-object-secret")
        };

        AppLogEntry entry = AppLogEntry.Create(
            new DateTimeOffset(2026, 8, 15, 21, 30, 0, TimeSpan.FromHours(8)),
            AppLogLevel.Error,
            "连接诊断",
            "password=message-password; token=message-token; credential=message-credential",
            new AppLogContext(
                Host: "10.0.0.6",
                SessionGeneration: 7,
                Properties: properties,
                HostId: hostId,
                ErrorCategory: "InvalidCredential"),
            new InvalidOperationException("client_secret=exception-secret"));

        properties["identity"] = "MUTATED";

        TestAssert.Equal(hostId, entry.HostId);
        TestAssert.Equal("10.0.0.6", entry.Host);
        TestAssert.Equal(AppLogLevel.Error, entry.Level);
        TestAssert.Equal("连接诊断", entry.Source);
        TestAssert.Equal("InvalidCredential", entry.ErrorCategory);
        TestAssert.Equal(7L, entry.SessionGeneration);
        TestAssert.True(
            entry.Properties.Any(property => property.Name == "identity" && property.Value == "LAB\\HyperVAdmin"),
            "The entry did not retain an immutable copy of safe properties.");

        string serialized = string.Join('|',
            entry.Source,
            entry.Message,
            entry.ExceptionText,
            string.Join('|', entry.Properties.Select(property => $"{property.Name}={property.Value}")));
        foreach (string forbidden in new[]
        {
            "message-password", "message-token", "message-credential",
            "property-password", "property-token", "credential-object-secret", "exception-secret",
            "password", "token", "credential", "client_secret"
        })
        {
            TestAssert.False(
                serialized.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"Structured entry leaked sensitive text: {forbidden}");
        }
    }

    private static void FeedKeepsBoundedHistoryPerHost()
    {
        TestAssert.Equal(2_000, new HostLogFeed().MaxEntriesPerHost);
        var feed = new HostLogFeed(maxEntriesPerHost: 3);
        HostId hostA = HostId.FromProfileId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        HostId hostB = HostId.FromProfileId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        for (int index = 0; index < 5; index++)
        {
            feed.Publish(Entry(hostA, $"A-{index}"));
            feed.Publish(Entry(hostB, $"B-{index}"));
        }

        TestAssert.SequenceEqual(
            new[] { "A-2", "A-3", "A-4" },
            feed.GetSnapshot(hostA).Select(entry => entry.Message));
        TestAssert.SequenceEqual(
            new[] { "B-2", "B-3", "B-4" },
            feed.GetSnapshot(hostB).Select(entry => entry.Message));
    }

    private static void SubscriptionsAreHostScopedAndDisposable()
    {
        var feed = new HostLogFeed();
        HostId hostA = HostId.FromProfileId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        HostId hostB = HostId.FromProfileId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var received = new List<string>();

        IDisposable subscription = feed.Subscribe(hostA, entry => received.Add(entry.Message));
        feed.Publish(Entry(hostA, "A-visible"));
        feed.Publish(Entry(hostB, "B-hidden"));
        subscription.Dispose();
        feed.Publish(Entry(hostA, "A-after-dispose"));

        TestAssert.SequenceEqual(new[] { "A-visible" }, received);
    }

    private static void AppLogSharesOneSanitizedEntryWithDiskAndFeed()
    {
        using var temp = new TempDirectory();
        AppLog.Initialize(temp.Path);
        HostId hostId = HostId.FromProfileId(Guid.Parse("20202020-2020-2020-2020-202020202020"));
        var received = new List<AppLogEntry>();
        using IDisposable subscription = AppLog.Feed.Subscribe(hostId, received.Add);

        AppLog.Error(
            "连接诊断",
            "password=disk-message-secret",
            new AppLogContext(
                Host: "10.0.0.6",
                SessionGeneration: 8,
                Properties: new Dictionary<string, object?> { ["token"] = "disk-property-secret" },
                HostId: hostId,
                ErrorCategory: "InvalidCredential"),
            new InvalidOperationException("credential=disk-exception-secret"));

        TestAssert.Equal(1, received.Count);
        AppLogEntry entry = received[0];
        string diskLine = File.ReadLines(
                Path.Combine(AppLog.LogDirectory, RollingFileLogger.CurrentFileName))
            .Last();

        TestAssert.Equal(RollingFileLogger.FormatEntry(entry), diskLine);
        TestAssert.Equal(hostId, entry.HostId);
        TestAssert.Equal("InvalidCredential", entry.ErrorCategory);
        foreach (string forbidden in new[]
        {
            "disk-message-secret", "disk-property-secret", "disk-exception-secret",
            "password=", "token=", "credential=disk"
        })
        {
            TestAssert.False(
                diskLine.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"Disk/live shared entry leaked sensitive text: {forbidden}");
        }
    }

    private static void DiagnosticStepsStreamWithHostAndErrorCategory()
    {
        using var temp = new TempDirectory();
        AppLog.Initialize(temp.Path);
        var profile = new HostProfile(
            Guid.Parse("20202020-2020-2020-2020-202020202020"),
            "实验室宿主",
            "10.0.0.6");
        HostId hostId = HostId.FromProfile(profile);
        using var wmiStarted = new ManualResetEventSlim();
        var releaseWmi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new HostDiagnosticPipeline(
            new InlineIpv4Probe((_, _) => Task.CompletedTask),
            new InlineIdentityResolver(),
            new DelegateExplicitCredentialValidator((_, _, _) => Task.FromResult(
                new ExplicitCredentialValidationResult(
                    ExplicitCredentialValidationStatus.Valid,
                    "凭据有效。"))),
            new InlineWmiProbe(async (_, _, cancellationToken) =>
            {
                wmiStarted.Set();
                await releaseWmi.Task.WaitAsync(cancellationToken);
            }),
            new InlineTcpProbe((_, _, _) => throw new HostDiagnosticException(
                HostDiagnosticErrorCode.ConnectionRefused,
                "TCP 2179 被拒绝。")));

        Task<HostDiagnosticReport> run = pipeline.RunAsync(profile);
        try
        {
            TestAssert.True(
                wmiStarted.Wait(TimeSpan.FromSeconds(2)),
                "Diagnostic did not reach the blocking WMI probe.");
            TestAssert.False(run.IsCompleted, "Diagnostic completed before the blocking probe was released.");
            IReadOnlyList<AppLogEntry> inFlight = AppLog.Feed.GetSnapshot(hostId);
            TestAssert.True(inFlight.Count >= 5, "Diagnostic entries did not stream before the report completed.");
            TestAssert.True(
                inFlight.All(entry => entry.HostId == hostId && entry.Source == "连接诊断"),
                "A diagnostic entry was published outside the selected host scope.");
            TestAssert.True(
                inFlight.Any(entry => entry.Message.Contains("开始查询 WMI/DCOM", StringComparison.Ordinal)),
                "The current diagnostic step was not visible while its probe was running.");
        }
        finally
        {
            releaseWmi.TrySetResult();
        }

        HostDiagnosticReport report = run.GetAwaiter().GetResult();
        TestAssert.Equal(HostDiagnosticAvailability.PartiallyAvailable, report.Availability);
        AppLogEntry tcpFailure = AppLog.Feed.GetSnapshot(hostId)
            .Single(entry => entry.Message == "TCP 2179 被拒绝。");
        TestAssert.Equal("ConnectionRefused", tcpFailure.ErrorCategory);
    }

    private static AppLogEntry Entry(HostId hostId, string message) => AppLogEntry.Create(
        DateTimeOffset.UnixEpoch,
        AppLogLevel.Information,
        "测试",
        message,
        new AppLogContext(Host: hostId.ToString(), HostId: hostId));

    private sealed record SecretCredential(string Password);

    private sealed class InlineIpv4Probe(
        Func<string, CancellationToken, Task> action) : IIpv4ReachabilityProbe
    {
        public Task ProbeAsync(string address, CancellationToken cancellationToken) =>
            action(address, cancellationToken);
    }

    private sealed class InlineIdentityResolver : IHostIdentityResolver
    {
        public ResolvedHostIdentity Resolve(
            HostProfile profile,
            WindowsCredential? transientCredential) => ResolvedHostIdentity.CurrentWindowsIdentity;
    }

    private sealed class InlineWmiProbe(
        Func<string, ResolvedHostIdentity, CancellationToken, Task> action) : IWmiDcomProbe
    {
        public Task ProbeAsync(
            string address,
            ResolvedHostIdentity identity,
            CancellationToken cancellationToken) => action(address, identity, cancellationToken);
    }

    private sealed class InlineTcpProbe(
        Func<string, int, CancellationToken, Task> action) : ITcpPortProbe
    {
        public Task ProbeAsync(
            string address,
            int port,
            CancellationToken cancellationToken) => action(address, port, cancellationToken);
    }
}
