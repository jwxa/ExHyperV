using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Profiles;

internal static class HostRepairRecommendationTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("RepairEntry_HealthyDiagnosticHasNoActionOrGuidance", HealthyDiagnosticHasNoActionOrGuidance),
        ("RepairEntry_ConsoleFailureOffersContextualRepair", ConsoleFailureOffersContextualRepair),
        ("RepairEntry_AccessDeniedOffersReadOnlyInspection", AccessDeniedOffersReadOnlyInspection),
        ("RepairEntry_InvalidCredentialProvidesGuidanceOnly", InvalidCredentialProvidesGuidanceOnly),
        ("RepairEntry_MissingNamespaceProvidesGuidanceOnly", MissingNamespaceProvidesGuidanceOnly),
        ("RepairEntry_ContextRejectsEditedHostAndNewerDiagnostic", ContextRejectsEditedHostAndNewerDiagnostic),
        ("RepairEntry_PageHidesStandingEntryAndBindsContextualAction", PageHidesStandingEntryAndBindsContextualAction)
    ];

    private static void HealthyDiagnosticHasNoActionOrGuidance()
    {
        HostProfile profile = Profile();
        HostRepairDecision decision = HostRepairAdvisor.Evaluate(
            profile,
            Report(profile, HostDiagnosticErrorCode.None, HostDiagnosticErrorCode.None));

        TestAssert.False(decision.CanOfferRepair, "A healthy host exposed a settings repair action.");
        TestAssert.Equal(string.Empty, decision.Guidance);
    }

    private static void ConsoleFailureOffersContextualRepair()
    {
        HostProfile profile = Profile();
        HostDiagnosticReport report = Report(
            profile,
            HostDiagnosticErrorCode.None,
            HostDiagnosticErrorCode.ConnectionRefused);

        HostRepairDecision decision = HostRepairAdvisor.Evaluate(profile, report);

        TestAssert.True(decision.CanOfferRepair, "A repairable TCP 2179 finding did not expose the repair action.");
        TestAssert.Contains("TCP 2179", decision.ActionToolTip);
        HostRepairContext context = HostRepairContext.Capture(profile, report);
        TestAssert.True(context.Matches(profile, report), "The captured repair context did not match its diagnostic.");
    }

    private static void AccessDeniedOffersReadOnlyInspection()
    {
        HostProfile profile = Profile();
        HostRepairDecision decision = HostRepairAdvisor.Evaluate(
            profile,
            Report(profile, HostDiagnosticErrorCode.AccessDenied, HostDiagnosticErrorCode.None));

        TestAssert.True(decision.CanOfferRepair, "A valid identity with WMI/Hyper-V access denied did not offer inspection.");
        TestAssert.Contains("权限", decision.ActionToolTip);
    }

    private static void InvalidCredentialProvidesGuidanceOnly()
    {
        HostProfile profile = Profile();
        HostRepairDecision decision = HostRepairAdvisor.Evaluate(
            profile,
            Report(
                profile,
                HostDiagnosticErrorCode.None,
                HostDiagnosticErrorCode.None,
                identityError: HostDiagnosticErrorCode.InvalidCredential));

        TestAssert.False(decision.CanOfferRepair, "Invalid credentials incorrectly exposed an automatic repair action.");
        TestAssert.Contains("用户名或密码错误", decision.Guidance);
    }

    private static void MissingNamespaceProvidesGuidanceOnly()
    {
        HostProfile profile = Profile();
        HostRepairDecision decision = HostRepairAdvisor.Evaluate(
            profile,
            Report(profile, HostDiagnosticErrorCode.NamespaceUnavailable, HostDiagnosticErrorCode.None));

        TestAssert.False(decision.CanOfferRepair, "A missing Hyper-V namespace exposed an unsafe repair action.");
        TestAssert.Contains("启用 Hyper-V 角色", decision.Guidance);
    }

    private static void ContextRejectsEditedHostAndNewerDiagnostic()
    {
        HostProfile profile = Profile();
        HostDiagnosticReport report = Report(
            profile,
            HostDiagnosticErrorCode.None,
            HostDiagnosticErrorCode.Timeout);
        HostRepairContext context = HostRepairContext.Capture(profile, report);
        var edited = profile with { Address = "10.0.0.7" };
        HostDiagnosticReport newer = report with { StartedAt = report.StartedAt.AddSeconds(1) };

        TestAssert.False(context.Matches(edited, report), "Editing the target address preserved a stale repair context.");
        TestAssert.False(context.Matches(profile, newer), "A newer diagnostic preserved the previous repair context.");
        TestAssert.False(
            HostRepairAdvisor.Evaluate(edited, report).CanOfferRepair,
            "A diagnostic for the previous address exposed repair on the edited profile.");
    }

    private static void PageHidesStandingEntryAndBindsContextualAction()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(root, "src", "ViewModels", "HostConnectionPageViewModel.cs"));
        string xaml = File.ReadAllText(Path.Combine(root, "src", "Views", "Pages", "HostConnectionPage.xaml"));

        TestAssert.False(
            xaml.Contains("<ui:Button.Content>配置预检</ui:Button.Content>", StringComparison.Ordinal),
            "The standing configuration-preflight action is still rendered.");
        TestAssert.Contains("<ui:Button.Content>检查并修复设置</ui:Button.Content>", xaml);
        TestAssert.Contains("Visibility=\"{Binding IsRepairActionVisible", xaml);
        TestAssert.Contains("ToolTip=\"{Binding RepairActionToolTip}\"", xaml);
        TestAssert.Contains("Header=\"设置修复\"", xaml);
        TestAssert.Contains("Visibility=\"{Binding IsRepairWorkspaceVisible", xaml);
        TestAssert.Contains("Text=\"{Binding RepairGuidance}\"", xaml);
        TestAssert.Contains("HostRepairAdvisor.Evaluate", viewModel);
        TestAssert.Contains("HostRepairContext.Capture", viewModel);
        TestAssert.Contains("Matches(profile, GetCurrentReport(profile))", viewModel);
        TestAssert.True(
            CountOccurrences(viewModel, "Matches(profile, GetCurrentReport(profile))") >= 2,
            "The repair context is not revalidated after the confirmation dialog.");
    }

    private static HostProfile Profile() => new(
        Guid.Parse("21212121-2121-2121-2121-212121212121"),
        "修复测试宿主",
        "10.0.0.6");

    private static HostDiagnosticReport Report(
        HostProfile profile,
        HostDiagnosticErrorCode managementError,
        HostDiagnosticErrorCode consoleError,
        HostDiagnosticErrorCode identityError = HostDiagnosticErrorCode.None)
    {
        HostDiagnosticStepStatus identityStatus = identityError == HostDiagnosticErrorCode.None
            ? HostDiagnosticStepStatus.Succeeded
            : HostDiagnosticStepStatus.Failed;
        HostDiagnosticStepStatus managementStatus = identityStatus == HostDiagnosticStepStatus.Failed
            ? HostDiagnosticStepStatus.Skipped
            : managementError == HostDiagnosticErrorCode.None
                ? HostDiagnosticStepStatus.Succeeded
                : HostDiagnosticStepStatus.Failed;
        HostDiagnosticStepStatus consoleStatus = consoleError == HostDiagnosticErrorCode.None
            ? HostDiagnosticStepStatus.Succeeded
            : HostDiagnosticStepStatus.Failed;
        HostDiagnosticAvailability availability = managementStatus == HostDiagnosticStepStatus.Succeeded
            && consoleStatus == HostDiagnosticStepStatus.Succeeded
                ? HostDiagnosticAvailability.FullyAvailable
                : managementStatus == HostDiagnosticStepStatus.Succeeded
                  || consoleStatus == HostDiagnosticStepStatus.Succeeded
                    ? HostDiagnosticAvailability.PartiallyAvailable
                    : HostDiagnosticAvailability.Unavailable;

        return new HostDiagnosticReport(
            profile.Id,
            profile.Address,
            new DateTimeOffset(2026, 8, 15, 22, 0, 0, TimeSpan.FromHours(8)),
            TimeSpan.FromSeconds(1),
            availability,
            [
                new(HostDiagnosticStepKind.Ipv4Reachability, HostDiagnosticStepStatus.Succeeded, TimeSpan.Zero, "IPv4 可达。"),
                new(HostDiagnosticStepKind.Identity, identityStatus, TimeSpan.Zero, "身份结果。", identityError),
                new(HostDiagnosticStepKind.WmiDcom, managementStatus, TimeSpan.Zero, "管理通道结果。", managementError),
                new(HostDiagnosticStepKind.Tcp2179, consoleStatus, TimeSpan.Zero, "控制台通道结果。", consoleError)
            ],
            []);
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

        throw new DirectoryNotFoundException("Could not locate the ExHyperV repository root.");
    }

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }
        return count;
    }
}
