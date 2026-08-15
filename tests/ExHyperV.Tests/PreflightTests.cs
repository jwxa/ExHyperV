using System.Management;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Preflight;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.ViewModels;

internal static class PreflightTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Preflight_ReadOnlyPipelineReturnsOrderedChineseEvidence", ReadOnlyPipelineReturnsOrderedChineseEvidence),
        ("Preflight_LogsStreamBeforeReadOnlyReportCompletes", LogsStreamBeforeReadOnlyReportCompletes),
        ("Preflight_PartialReadFailurePreservesOtherFacts", PartialReadFailurePreservesOtherFacts),
        ("Preflight_PartialReadFailureBlocksUnsafePreview", PartialReadFailureBlocksUnsafePreview),
        ("Preflight_WorkgroupLocalAdministratorGetsConditionalMinimumPlan", WorkgroupLocalAdministratorGetsConditionalMinimumPlan),
        ("Preflight_DomainAccountNeverGetsTokenFilterPolicy", DomainAccountNeverGetsTokenFilterPolicy),
        ("Preflight_PrivateNetworkDoesNotRequestCategoryChange", PrivateNetworkDoesNotRequestCategoryChange),
        ("Preflight_Existing2179RuleWithDifferentScopeGetsUpdated", Existing2179RuleWithDifferentScopeGetsUpdated),
        ("Preflight_CidrNormalizesAndRejectsDuplicateIpv6AndInvalid", CidrNormalizesAndRejectsDuplicateIpv6AndInvalid),
        ("Preflight_CidrRejectsPublicAndPrivateSupernetRanges", CidrRejectsPublicAndPrivateSupernetRanges),
        ("Preflight_BuiltInFirewallClassifierExcludesUnrelatedRules", BuiltInFirewallClassifierExcludesUnrelatedRules),
        ("Preflight_FirewallProtocolUsesCmdletNames", FirewallProtocolUsesCmdletNames),
        ("Preflight_FirewallReaderUsesNativeCimSchema", FirewallReaderUsesNativeCimSchema),
        ("Preflight_WindowsFirewallReaderBuildsRealSnapshot", WindowsFirewallReaderBuildsRealSnapshot),
        ("PreflightViewModel_DisplaysMembershipAndTracksActiveStep", ViewModelDisplaysMembershipAndTracksActiveStep),
        ("PreflightViewModel_RejectsMalformedDomainAccount", ViewModelRejectsMalformedDomainAccount),
        ("PreflightViewModel_PublicNetworkChangeRequiresExplicitChoice", ViewModelPublicNetworkChangeRequiresExplicitChoice),
        ("PreflightViewModel_SelectedNetworkAndCidrBuildExpectedPreview", ViewModelSelectedNetworkAndCidrBuildExpectedPreview),
        ("PreflightViewModel_ApprovedPlanCanExpireWithoutHidingEvidence", ApprovedPlanCanExpireWithoutHidingEvidence),
        ("PreflightViewModel_LateCancelledResultCannotReplaceClearedTarget", ViewModelLateCancelledResultCannotReplaceClearedTarget)
    ];

    private static void BuiltInFirewallClassifierExcludesUnrelatedRules()
    {
        TestAssert.True(
            WindowsFirewallRuleClassifier.IsWmiBuiltIn("@FirewallAPI.dll,-34251"),
            "The stable Windows WMI resource group was not recognized.");
        TestAssert.True(
            WindowsFirewallRuleClassifier.IsHyperVManagementBuiltIn(
                @"@%systemroot%\system32\vmms.exe,-210"),
            "The stable Hyper-V management resource group was not recognized.");

        TestAssert.False(
            WindowsFirewallRuleClassifier.IsHyperVManagementBuiltIn(
                @"@%systemroot%\system32\vmms.exe,-251"),
            "Hyper-V Replica rules were classified as management rules.");
        TestAssert.False(
            WindowsFirewallRuleClassifier.IsHyperVManagementBuiltIn(
                "@FirewallAPI.dll,-60201"),
            "Hyper-V client rules were classified as host management rules.");
        TestAssert.False(
            WindowsFirewallRuleClassifier.IsHyperVManagementBuiltIn(string.Empty),
            "A display-only Hyper-V rule was classified as built-in.");
        TestAssert.False(
            WindowsFirewallRuleClassifier.IsWmiBuiltIn(string.Empty),
            "A display-only WMI rule was classified as built-in.");

        TestAssert.True(WindowsFirewallRuleClassifier.IsEnabled(1), "CIM enabled state was classified as disabled.");
        TestAssert.False(WindowsFirewallRuleClassifier.IsEnabled(2), "CIM disabled state was classified as enabled.");
        TestAssert.Equal("Allow", WindowsFirewallRuleClassifier.ActionText(2));
        TestAssert.Equal("Block", WindowsFirewallRuleClassifier.ActionText(4));
        TestAssert.True(
            WindowsFirewallRuleClassifier.IsLocalPersistentPolicy(1, "PersistentStore"),
            "A local persistent rule was rejected.");
        TestAssert.False(
            WindowsFirewallRuleClassifier.IsLocalPersistentPolicy(2, "Domain GPO"),
            "A policy-managed rule was accepted as locally owned.");
    }

    private static void FirewallReaderUsesNativeCimSchema()
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\standardcimv2",
            WindowsFirewallRuleClassifier.InboundRuleQuery);
        using ManagementObjectCollection rules = searcher.Get();
        using ManagementObject? first = rules.Cast<ManagementObject>().FirstOrDefault();
        TestAssert.NotNull(first, "The native MSFT_NetFirewallRule query returned no inbound rules.");
        _ = first!["InstanceID"];
        _ = first["RuleGroup"];
        _ = first["Profiles"];

        using var consoleSearcher = new ManagementObjectSearcher(
            @"root\standardcimv2",
            WindowsFirewallRuleClassifier.ConsoleRuleQuery);
        using ManagementObjectCollection consoleRules = consoleSearcher.Get();
    }

    private static void FirewallProtocolUsesCmdletNames()
    {
        TestAssert.Equal("ICMPv4", WindowsFirewallRuleClassifier.ProtocolText(1));
        TestAssert.Equal("TCP", WindowsFirewallRuleClassifier.ProtocolText(6));
        TestAssert.Equal("UDP", WindowsFirewallRuleClassifier.ProtocolText(17));
        TestAssert.Equal("ICMPv6", WindowsFirewallRuleClassifier.ProtocolText(58));
        TestAssert.Equal("255", WindowsFirewallRuleClassifier.ProtocolText(255));
        TestAssert.Equal("TCP", WindowsFirewallRuleClassifier.ProtocolText("TCP"));
        TestAssert.Equal("TCP", WindowsFirewallRuleClassifier.ProtocolText("6"));
    }

    private static void WindowsFirewallReaderBuildsRealSnapshot()
    {
        var reader = new WindowsHostPreflightReader(TimeSpan.FromSeconds(20));
        IHostPreflightReadSession session = reader.OpenAsync(
                ".",
                ResolvedHostIdentity.CurrentWindowsIdentity,
                CancellationToken.None)
            .GetAwaiter().GetResult();
        try
        {
            HostFirewallSnapshot snapshot = session.ReadFirewallAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            TestAssert.True(
                snapshot.WmiRuleNamesToEnable.All(name => name.StartsWith("WMI-", StringComparison.OrdinalIgnoreCase)),
                "The real WMI snapshot included a rule outside the Windows WMI group.");
            TestAssert.False(
                snapshot.HyperVRuleNamesToEnable.Any(name =>
                    name.StartsWith("VIRT-HVR", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("VIRTCL-", StringComparison.OrdinalIgnoreCase)),
                "The real Hyper-V management snapshot included Replica or client rules.");
        }
        finally
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void ReadOnlyPipelineReturnsOrderedChineseEvidence()
    {
        var session = CompleteSession();
        var reader = new RecordingReader(session);
        var pipeline = CreatePipeline(reader);

        HostPreflightReport report = pipeline.RunAsync(Profile()).GetAwaiter().GetResult();

        TestAssert.SequenceEqual(
            ["open", "join", "accounts", "group:Administrators", "group:HyperVAdministrators", "group:RemoteManagementUsers", "policy", "networks", "firewall", "dispose"],
            reader.Calls);
        TestAssert.Equal(9, report.Findings.Count);
        TestAssert.False(report.HasReadFailures, "Complete read-only preflight should not report read failures.");
        string log = string.Join('\n', report.LogEntries.Select(entry => entry.Message));
        TestAssert.Contains("只读取状态，不会修改远程主机", log);
        TestAssert.Contains("工作组 WORKGROUP", log);
        TestAssert.Contains("以太网（接口索引 12）：Public", log);
        TestAssert.Contains("未执行任何修改", log);
    }

    private static void LogsStreamBeforeReadOnlyReportCompletes()
    {
        FakeReadSession session = CompleteSession();
        session.JoinResult = new TaskCompletionSource<HostJoinSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var streamed = new List<HostPreflightLogEntry>();
        var pipeline = CreatePipeline(new RecordingReader(session));

        Task<HostPreflightReport> run = pipeline.RunAsync(
            Profile(),
            transientCredential: null,
            streamed.Add,
            CancellationToken.None);
        try
        {
            TestAssert.True(
                SpinWait.SpinUntil(() => session.Calls.Contains("join"), TimeSpan.FromSeconds(2)),
                "Preflight did not reach the blocking read stage.");
            TestAssert.False(run.IsCompleted, "The read-only report completed before the blocked read was released.");
            TestAssert.True(streamed.Count >= 3, "Preflight logs were withheld until the complete report.");
            TestAssert.True(
                streamed.Zip(streamed.Skip(1), (left, right) => left.Timestamp <= right.Timestamp).All(value => value),
                "Streamed preflight logs were not ordered by arrival time.");
            TestAssert.Contains("只读取状态", string.Join('\n', streamed.Select(entry => entry.Message)));
        }
        finally
        {
            session.JoinResult.TrySetResult(session.Join);
        }

        HostPreflightReport report = run.GetAwaiter().GetResult();
        TestAssert.SequenceEqual(report.LogEntries, streamed);
    }

    private static void PartialReadFailurePreservesOtherFacts()
    {
        var session = CompleteSession();
        session.FirewallError = new InvalidOperationException("防火墙查询不可用");
        var pipeline = CreatePipeline(new RecordingReader(session));

        HostPreflightReport report = pipeline.RunAsync(Profile()).GetAwaiter().GetResult();

        TestAssert.True(report.HasReadFailures, "Firewall failure should be visible in the report.");
        TestAssert.Equal(2, report.Facts.EnabledLocalAccounts.Count);
        TestAssert.Equal(2, report.Facts.Networks.Count);
        TestAssert.Null(report.Facts.Firewall, "Failed firewall facts must not be fabricated.");
        TestAssert.Contains("读取防火墙规则失败", string.Join('\n', report.LogEntries.Select(entry => entry.Message)));
    }

    private static void PartialReadFailureBlocksUnsafePreview()
    {
        var session = CompleteSession();
        session.FirewallError = new InvalidOperationException("防火墙查询不可用");
        HostPreflightReport report = Report(session);
        var selection = new HostPreflightSelection(
            HostPreflightAccountKind.Local,
            "Administrator",
            [12],
            [12],
            ["10.0.0.0/24"]);

        HostPreflightPlanResult result = HostPreflightPlanner.Build(report, selection);

        TestAssert.False(result.IsValid, "A partial read must not produce a guessed configuration preview.");
        string errors = string.Join('\n', result.Errors);
        TestAssert.Contains("读取失败", errors);
        TestAssert.Contains("缺少防火墙规则状态", errors);
    }

    private static void WorkgroupLocalAdministratorGetsConditionalMinimumPlan()
    {
        HostPreflightReport report = Report(CompleteSession());
        var selection = new HostPreflightSelection(
            HostPreflightAccountKind.Local,
            "Administrator",
            [12],
            [12],
            ["10.0.0.42/24"]);

        HostPreflightPlanResult result = HostPreflightPlanner.Build(report, selection);

        TestAssert.True(result.IsValid, string.Join("; ", result.Errors));
        TestAssert.SequenceEqual(
            [
                HostPreflightChangeKind.AddRemoteManagementUsers,
                HostPreflightChangeKind.EnableLocalAccountTokenFilterPolicy,
                HostPreflightChangeKind.ChangeNetworkToPrivate,
                HostPreflightChangeKind.EnableHyperVFirewallRules,
                HostPreflightChangeKind.ConfigureConsole2179FirewallRule
            ],
            result.Plan!.Changes.Select(change => change.Kind));
        TestAssert.SequenceEqual(["10.0.0.0/24"], result.Plan.AllowedIpv4Cidrs);
        TestAssert.False(
            result.Plan.Changes.Any(change => change.Title.Contains("Administrators", StringComparison.Ordinal)
                                              && change.Kind != HostPreflightChangeKind.AddHyperVAdministrators),
            "The plan must never grant the Administrators group.");
    }

    private static void DomainAccountNeverGetsTokenFilterPolicy()
    {
        HostPreflightReport report = Report(CompleteSession());
        var selection = new HostPreflightSelection(
            HostPreflightAccountKind.Domain,
            "LAB\\Operator",
            [12],
            Array.Empty<uint>(),
            ["10.0.0.0/24"]);

        HostPreflightPlanResult result = HostPreflightPlanner.Build(report, selection);

        TestAssert.True(result.IsValid, string.Join("; ", result.Errors));
        TestAssert.False(
            result.Plan!.Changes.Any(change => change.Kind == HostPreflightChangeKind.EnableLocalAccountTokenFilterPolicy),
            "A domain account must never trigger LocalAccountTokenFilterPolicy.");
        TestAssert.True(
            result.Plan.Changes.Any(change => change.Kind == HostPreflightChangeKind.AddHyperVAdministrators),
            "The domain account should be offered least-privilege Hyper-V membership.");
    }

    private static void PrivateNetworkDoesNotRequestCategoryChange()
    {
        var session = CompleteSession();
        session.Networks =
        [
            new HostNetworkSnapshot(44, "管理网", HostNetworkCategory.Private, [new HostIpv4Address("192.168.50.8", 24)])
        ];
        HostPreflightReport report = Report(session);
        var selection = new HostPreflightSelection(
            HostPreflightAccountKind.Local,
            "Administrator",
            [44],
            [44],
            ["192.168.50.0/24"]);

        HostPreflightPlanResult result = HostPreflightPlanner.Build(report, selection);

        TestAssert.True(result.IsValid, string.Join("; ", result.Errors));
        TestAssert.False(
            result.Plan!.Changes.Any(change => change.Kind == HostPreflightChangeKind.ChangeNetworkToPrivate),
            "A Private network must not produce a category change even if selected by stale UI state.");
    }

    private static void CidrNormalizesAndRejectsDuplicateIpv6AndInvalid()
    {
        TestAssert.Equal("172.16.5.0/24", Ipv4Cidr.Normalize(" 172.16.5.93/24 "));
        TestAssert.True(Ipv4Cidr.IsPrivate("172.16.5.93/24"), "RFC1918 CIDR should be recognized as private.");
        TestAssert.True(
            Ipv4Cidr.TryNormalizeFirewallAddress("10.0.0.0/255.255.255.0", out string firewallAddress),
            "Windows firewall dotted mask should normalize to CIDR notation.");
        TestAssert.Equal("10.0.0.0/24", firewallAddress);
        TestAssert.Equal("10.0.0.0/255.255.255.0", Ipv4Cidr.ToWindowsFirewallAddress("10.0.0.7/24"));
        TestAssert.False(
            Ipv4Cidr.TryNormalizeFirewallAddress("10.0.0.0/255.0.255.0", out _),
            "A non-contiguous firewall mask was accepted.");

        HostPreflightReport report = Report(CompleteSession());
        var selection = new HostPreflightSelection(
            HostPreflightAccountKind.Local,
            "Administrator",
            [12],
            Array.Empty<uint>(),
            ["10.0.0.7/24", "10.0.0.0/24", "fe80::/64", "broken"]);

        HostPreflightPlanResult result = HostPreflightPlanner.Build(report, selection);

        TestAssert.False(result.IsValid, "Duplicate, IPv6, and invalid CIDR input must block the preview.");
        string errors = string.Join('\n', result.Errors);
        TestAssert.Contains("重复", errors);
        TestAssert.Contains("不接受主机名或 IPv6", errors);
        TestAssert.Contains("不是有效的 IPv4 CIDR", errors);
    }

    private static void Existing2179RuleWithDifferentScopeGetsUpdated()
    {
        var session = CompleteSession();
        session.Firewall = new HostFirewallSnapshot(true, true, true, ["192.168.50.0/24"]);
        HostPreflightReport report = Report(session);
        var selection = new HostPreflightSelection(
            HostPreflightAccountKind.Local,
            "Administrator",
            [12],
            Array.Empty<uint>(),
            ["10.0.0.0/24"]);

        HostPreflightPlanResult result = HostPreflightPlanner.Build(report, selection);

        TestAssert.True(result.IsValid, string.Join("; ", result.Errors));
        HostPreflightPlannedChange change = result.Plan!.Changes.Single(item => item.Kind == HostPreflightChangeKind.ConfigureConsole2179FirewallRule);
        TestAssert.Contains("更新", change.Title);
        TestAssert.Contains("10.0.0.0/24", change.Detail);
    }

    private static void CidrRejectsPublicAndPrivateSupernetRanges()
    {
        TestAssert.False(Ipv4Cidr.IsPrivate("8.8.8.0/24"), "A public CIDR was classified as private.");
        TestAssert.False(Ipv4Cidr.IsPrivate("10.0.0.0/7"), "A supernet extending outside 10/8 was classified as private.");
        TestAssert.False(Ipv4Cidr.IsPrivate("172.16.0.0/11"), "A supernet extending outside 172.16/12 was classified as private.");
        TestAssert.False(Ipv4Cidr.IsPrivate("192.168.0.0/15"), "A supernet extending outside 192.168/16 was classified as private.");
        TestAssert.True(Ipv4Cidr.IsPrivate("10.0.0.0/8"), "The complete 10/8 private block was rejected.");
        TestAssert.True(Ipv4Cidr.IsPrivate("172.16.0.0/12"), "The complete 172.16/12 private block was rejected.");
        TestAssert.True(Ipv4Cidr.IsPrivate("192.168.0.0/16"), "The complete 192.168/16 private block was rejected.");

        HostPreflightReport report = Report(CompleteSession());
        var selection = new HostPreflightSelection(
            HostPreflightAccountKind.Local,
            "Administrator",
            [12],
            Array.Empty<uint>(),
            ["8.8.8.0/24", "10.0.0.0/7"]);

        HostPreflightPlanResult result = HostPreflightPlanner.Build(report, selection);

        TestAssert.False(result.IsValid, "Public or over-broad CIDRs must block the configuration preview.");
        string errors = string.Join('\n', result.Errors);
        TestAssert.Contains("8.8.8.0/24", errors);
        TestAssert.Contains("10.0.0.0/7", errors);
        TestAssert.Contains("RFC1918", errors);
    }

    private static void ViewModelDisplaysMembershipAndTracksActiveStep()
    {
        using HostPreflightViewModel viewModel = CreateViewModel();

        TestAssert.True(viewModel.Steps[0].IsActive, "The first wizard step should be active initially.");
        TestAssert.False(viewModel.Steps[1].IsActive, "Only one wizard step should be active initially.");
        HostPreflightAccountOptionViewModel administrator = viewModel.LocalAccounts.Single(account => account.Name == "Administrator");
        TestAssert.True(administrator.IsAdministrator, "Administrator membership was not displayed.");
        TestAssert.True(administrator.IsHyperVAdministrator, "Hyper-V Administrators membership was not displayed.");
        TestAssert.False(administrator.IsRemoteManagementUser, "Unexpected Remote Management Users membership was displayed.");

        viewModel.NextStepCommand.Execute(null);

        TestAssert.Equal(1, viewModel.StepIndex);
        TestAssert.False(viewModel.Steps[0].IsActive, "The previous wizard step remained active.");
        TestAssert.True(viewModel.Steps[1].IsActive, "The account wizard step was not highlighted.");
    }

    private static void ViewModelRejectsMalformedDomainAccount()
    {
        using HostPreflightViewModel viewModel = CreateViewModel();
        viewModel.NextStepCommand.Execute(null);
        viewModel.UseDomainAccount = true;
        viewModel.DomainAccountName = "LAB-Operator";

        viewModel.NextStepCommand.Execute(null);

        TestAssert.Equal(1, viewModel.StepIndex);
        TestAssert.Contains("DOMAIN\\User", viewModel.SelectionError);
    }

    private static void ViewModelPublicNetworkChangeRequiresExplicitChoice()
    {
        using HostPreflightViewModel viewModel = CreateViewModel();
        SelectMinimumPreviewInputs(viewModel, makePrivate: false);

        viewModel.NextStepCommand.Execute(null);

        TestAssert.Equal(3, viewModel.StepIndex);
        TestAssert.False(
            viewModel.PlannedChanges.Any(change => change.Title.Contains("改为 Private", StringComparison.Ordinal)),
            "A Public network must not be changed without an explicit user choice.");
    }

    private static void ViewModelSelectedNetworkAndCidrBuildExpectedPreview()
    {
        using HostPreflightViewModel viewModel = CreateViewModel();
        SelectMinimumPreviewInputs(viewModel, makePrivate: true);

        viewModel.NextStepCommand.Execute(null);

        TestAssert.Equal(3, viewModel.StepIndex);
        TestAssert.Contains("拟执行修改", viewModel.PreviewSummary);
        TestAssert.True(
            viewModel.PlannedChanges.Any(change => change.Title.Contains("以太网", StringComparison.Ordinal)
                                                   && change.Title.Contains("Private", StringComparison.Ordinal)),
            "The explicitly selected Public network change was absent from the preview.");
        TestAssert.True(
            viewModel.PlannedChanges.Any(change => change.Detail.Contains("10.0.0.0/24", StringComparison.Ordinal)),
            "The selected CIDR was absent from the preview.");
    }

    private static void ApprovedPlanCanExpireWithoutHidingEvidence()
    {
        using HostPreflightViewModel viewModel = CreateViewModel();
        SelectMinimumPreviewInputs(viewModel, makePrivate: true);
        viewModel.NextStepCommand.Execute(null);
        int visibleChanges = viewModel.PlannedChanges.Count;
        TestAssert.True(viewModel.CanApply, "The complete preview was not eligible for confirmation.");

        viewModel.ExpireApprovedPlan();

        TestAssert.False(viewModel.CanApply, "An expired approved plan remained applicable.");
        TestAssert.Equal(visibleChanges, viewModel.PlannedChanges.Count);
    }

    private static void ViewModelLateCancelledResultCannotReplaceClearedTarget()
    {
        var reader = new DelayedReader();
        using var viewModel = new HostPreflightViewModel(CreatePipeline(reader));
        viewModel.SetTarget(Profile(), null);

        Task run = viewModel.RunCommand.ExecuteAsync(null);
        viewModel.ClearTarget();
        TestAssert.False(viewModel.IsRunning, "Cancelling a reader that ignores cancellation must release the UI immediately.");
        viewModel.SetTarget(new HostProfile(Guid.NewGuid(), "备用宿主", "10.0.0.7"), null);
        TestAssert.True(viewModel.RunCommand.CanExecute(null), "A cancelled stale reader blocked preflight for the new target.");
        reader.Complete(CompleteSession());
        run.GetAwaiter().GetResult();

        TestAssert.Equal("备用宿主 · 10.0.0.7", viewModel.HostLabel);
        TestAssert.Equal(0, viewModel.Findings.Count);
        TestAssert.Equal(0, viewModel.LocalAccounts.Count);
        TestAssert.False(viewModel.CanGoNext, "A late cancelled result re-enabled the cleared wizard.");
    }

    private static HostPreflightViewModel CreateViewModel()
    {
        var viewModel = new HostPreflightViewModel(CreatePipeline(new RecordingReader(CompleteSession())));
        viewModel.SetTarget(Profile(), null);
        viewModel.RunCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return viewModel;
    }

    private static void SelectMinimumPreviewInputs(HostPreflightViewModel viewModel, bool makePrivate)
    {
        viewModel.NextStepCommand.Execute(null);
        viewModel.NextStepCommand.Execute(null);
        HostPreflightNetworkOptionViewModel network = viewModel.Networks.Single(item => item.InterfaceIndex == 12);
        network.IsSelected = true;
        network.MakePrivate = makePrivate;
        viewModel.DetectedCidrs.Single(item => item.Cidr == "10.0.0.0/24").IsSelected = true;
    }

    private static HostPreflightPipeline CreatePipeline(IHostPreflightReader reader) =>
        new(new CurrentIdentityResolver(), reader);

    private static HostPreflightReport Report(FakeReadSession session) =>
        CreatePipeline(new RecordingReader(session)).RunAsync(Profile()).GetAwaiter().GetResult();

    private static HostProfile Profile() => new(Guid.NewGuid(), "实验室宿主", "10.0.0.6");

    private static FakeReadSession CompleteSession() => new()
    {
        Join = new HostJoinSnapshot("LAB-HV-06", HostJoinKind.Workgroup, "WORKGROUP"),
        Accounts =
        [
            new HostLocalAccount("Administrator", "S-1-5-21-1-500"),
            new HostLocalAccount("jwxa", "S-1-5-21-1-1001")
        ],
        Groups = new Dictionary<HostLocalGroupKind, HostLocalGroupSnapshot>
        {
            [HostLocalGroupKind.Administrators] = new(HostLocalGroupKind.Administrators, "Administrators", ["LAB-HV-06\\Administrator"]),
            [HostLocalGroupKind.HyperVAdministrators] = new(HostLocalGroupKind.HyperVAdministrators, "Hyper-V Administrators", ["LAB-HV-06\\Administrator"]),
            [HostLocalGroupKind.RemoteManagementUsers] = new(HostLocalGroupKind.RemoteManagementUsers, "Remote Management Users", Array.Empty<string>())
        },
        Policy = HostTokenFilterPolicyState.Missing,
        Networks =
        [
            new HostNetworkSnapshot(12, "以太网", HostNetworkCategory.Public, [new HostIpv4Address("10.0.0.6", 24)]),
            new HostNetworkSnapshot(18, "备份网", HostNetworkCategory.Private, [new HostIpv4Address("192.168.50.6", 24)])
        ],
        Firewall = new HostFirewallSnapshot(
            WmiBuiltInRulesEnabled: true,
            HyperVBuiltInRulesEnabled: false,
            ExHyperVConsole2179RuleEnabled: false,
            DisabledHyperVRuleNames: ["Hyper-V-VMMS-In-TCP"])
    };

    private sealed class CurrentIdentityResolver : IHostIdentityResolver
    {
        public ResolvedHostIdentity Resolve(HostProfile profile, WindowsCredential? transientCredential) =>
            ResolvedHostIdentity.CurrentWindowsIdentity;
    }

    private sealed class RecordingReader(FakeReadSession session) : IHostPreflightReader
    {
        public List<string> Calls { get; } = [];

        public Task<IHostPreflightReadSession> OpenAsync(
            string address,
            ResolvedHostIdentity identity,
            CancellationToken cancellationToken)
        {
            Calls.Add("open");
            session.Calls = Calls;
            return Task.FromResult<IHostPreflightReadSession>(session);
        }
    }

    private sealed class DelayedReader : IHostPreflightReader
    {
        private readonly TaskCompletionSource<IHostPreflightReadSession> _session =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IHostPreflightReadSession> OpenAsync(
            string address,
            ResolvedHostIdentity identity,
            CancellationToken cancellationToken) => _session.Task;

        public void Complete(IHostPreflightReadSession session) => _session.SetResult(session);
    }

    private sealed class FakeReadSession : IHostPreflightReadSession
    {
        public List<string> Calls { get; set; } = [];
        public HostJoinSnapshot Join { get; set; } = null!;
        public IReadOnlyList<HostLocalAccount> Accounts { get; set; } = [];
        public IReadOnlyDictionary<HostLocalGroupKind, HostLocalGroupSnapshot> Groups { get; set; } = new Dictionary<HostLocalGroupKind, HostLocalGroupSnapshot>();
        public HostTokenFilterPolicyState Policy { get; set; }
        public IReadOnlyList<HostNetworkSnapshot> Networks { get; set; } = [];
        public HostFirewallSnapshot Firewall { get; set; } = null!;
        public Exception? FirewallError { get; set; }
        public TaskCompletionSource<HostJoinSnapshot>? JoinResult { get; set; }

        public Task<HostJoinSnapshot> ReadJoinAsync(CancellationToken cancellationToken)
        {
            Calls.Add("join");
            return JoinResult?.Task ?? Task.FromResult(Join);
        }

        public Task<IReadOnlyList<HostLocalAccount>> ReadEnabledLocalAccountsAsync(CancellationToken cancellationToken)
        {
            Calls.Add("accounts");
            return Task.FromResult(Accounts);
        }

        public Task<HostLocalGroupSnapshot> ReadLocalGroupAsync(HostLocalGroupKind group, CancellationToken cancellationToken)
        {
            Calls.Add($"group:{group}");
            return Task.FromResult(Groups[group]);
        }

        public Task<HostTokenFilterPolicyState> ReadTokenFilterPolicyAsync(CancellationToken cancellationToken)
        {
            Calls.Add("policy");
            return Task.FromResult(Policy);
        }

        public Task<IReadOnlyList<HostNetworkSnapshot>> ReadNetworksAsync(CancellationToken cancellationToken)
        {
            Calls.Add("networks");
            return Task.FromResult(Networks);
        }

        public Task<HostFirewallSnapshot> ReadFirewallAsync(CancellationToken cancellationToken)
        {
            Calls.Add("firewall");
            return FirewallError is null ? Task.FromResult(Firewall) : Task.FromException<HostFirewallSnapshot>(FirewallError);
        }

        public ValueTask DisposeAsync()
        {
            Calls.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }
}
