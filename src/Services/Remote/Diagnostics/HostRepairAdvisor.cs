using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.Services.Remote.Diagnostics;

public sealed record HostRepairDecision(
    bool CanOfferRepair,
    string ActionToolTip,
    string Guidance)
{
    public static HostRepairDecision None { get; } = new(false, string.Empty, string.Empty);
}

public sealed record HostRepairContext(
    Guid ProfileId,
    string HostAddress,
    DateTimeOffset DiagnosticStartedAt)
{
    public static HostRepairContext Capture(HostProfile profile, HostDiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(report);
        if (!MatchesTarget(profile, report))
            throw new ArgumentException("诊断结果不属于当前主机配置。", nameof(report));
        return new HostRepairContext(profile.Id, profile.Address, report.StartedAt);
    }

    public bool Matches(HostProfile profile, HostDiagnosticReport? report) =>
        profile is not null
        && report is not null
        && profile.Id == ProfileId
        && report.ProfileId == ProfileId
        && string.Equals(profile.Address, HostAddress, StringComparison.Ordinal)
        && string.Equals(report.HostAddress, HostAddress, StringComparison.Ordinal)
        && report.StartedAt == DiagnosticStartedAt;

    internal static bool MatchesTarget(HostProfile profile, HostDiagnosticReport report) =>
        report.ProfileId == profile.Id
        && string.Equals(report.HostAddress, profile.Address, StringComparison.Ordinal);
}

public static class HostRepairAdvisor
{
    public static HostRepairDecision Evaluate(HostProfile? profile, HostDiagnosticReport? report)
    {
        if (profile is null || report is null || !HostRepairContext.MatchesTarget(profile, report))
            return HostRepairDecision.None;

        HostDiagnosticStepResult ipv4 = report.GetStep(HostDiagnosticStepKind.Ipv4Reachability);
        HostDiagnosticStepResult identity = report.GetStep(HostDiagnosticStepKind.Identity);
        HostDiagnosticStepResult management = report.GetStep(HostDiagnosticStepKind.WmiDcom);
        HostDiagnosticStepResult console = report.GetStep(HostDiagnosticStepKind.Tcp2179);

        if (identity.Status == HostDiagnosticStepStatus.Failed)
            return GuidanceForManagement(identity.ErrorCode);
        if (identity.Status == HostDiagnosticStepStatus.Cancelled)
            return GuidanceForManagement(HostDiagnosticErrorCode.Cancelled);

        if (management.Status == HostDiagnosticStepStatus.Failed
            && management.ErrorCode == HostDiagnosticErrorCode.AccessDenied)
        {
            return new HostRepairDecision(
                true,
                "当前身份有效，但缺少 WMI/Hyper-V 权限；先只读检查账户、组和防火墙设置。",
                string.Empty);
        }

        if (management.Status == HostDiagnosticStepStatus.Succeeded
            && console.Status == HostDiagnosticStepStatus.Failed
            && IsConsoleRepairCandidate(console.ErrorCode))
        {
            return new HostRepairDecision(
                true,
                "WMI/DCOM 已可用，但 TCP 2179 未通过；先只读检查网络和防火墙设置。",
                string.Empty);
        }

        if (management.Status == HostDiagnosticStepStatus.Succeeded)
        {
            if (console.Status == HostDiagnosticStepStatus.Failed)
                return GuidanceForConsole(console.ErrorCode);
            return HostRepairDecision.None;
        }

        if (management.Status == HostDiagnosticStepStatus.Failed)
            return GuidanceForManagement(management.ErrorCode);
        if (management.Status is HostDiagnosticStepStatus.Skipped or HostDiagnosticStepStatus.Cancelled)
        {
            if (ipv4.Status == HostDiagnosticStepStatus.Failed)
                return GuidanceForManagement(ipv4.ErrorCode);
            return Guidance("WMI/DCOM 未完成检测，当前不会自动修改；请重新运行连接检测。");
        }
        return HostRepairDecision.None;
    }

    private static bool IsConsoleRepairCandidate(HostDiagnosticErrorCode errorCode) => errorCode is
        HostDiagnosticErrorCode.ConnectionRefused
        or HostDiagnosticErrorCode.Timeout
        or HostDiagnosticErrorCode.NetworkError;

    private static HostRepairDecision GuidanceForManagement(HostDiagnosticErrorCode errorCode) =>
        Guidance(errorCode switch
        {
            HostDiagnosticErrorCode.InvalidCredential => "用户名或密码错误，请编辑主机配置并重新输入凭据。",
            HostDiagnosticErrorCode.CredentialMissing => "没有可用凭据，请编辑主机配置并输入连接账户和密码。",
            HostDiagnosticErrorCode.AuthenticationFailed => "目标宿主拒绝了当前身份，请更新凭据后重新检测。",
            HostDiagnosticErrorCode.InvalidIpv4 => "主机 IPv4 地址无效，请编辑主机配置后重新检测。",
            HostDiagnosticErrorCode.Unreachable => "目标主机不可达，请确认主机在线、位于同一局域网并检查 IPv4 地址。",
            HostDiagnosticErrorCode.NamespaceUnavailable => "目标宿主缺少 Hyper-V WMI 命名空间，请先在目标机启用 Hyper-V 角色。",
            HostDiagnosticErrorCode.ConnectionRefused => "WMI/DCOM 连接被拒绝，当前无法安全自动处理；请在目标机检查 RPC/DCOM 与防火墙设置。",
            HostDiagnosticErrorCode.Timeout => "WMI/DCOM 连接超时，当前无法安全自动处理；请检查网络、RPC/DCOM 和防火墙。",
            HostDiagnosticErrorCode.NetworkError => "WMI/DCOM 网络连接失败，当前无法安全自动处理；请先恢复网络和 RPC/DCOM。",
            HostDiagnosticErrorCode.Cancelled => "检测已取消，请重新运行连接检测。",
            _ => "WMI/DCOM 检测发生未知错误，当前不会自动修改；请查看当前宿主日志。"
        });

    private static HostRepairDecision GuidanceForConsole(HostDiagnosticErrorCode errorCode) =>
        Guidance(errorCode switch
        {
            HostDiagnosticErrorCode.Cancelled => "TCP 2179 检测已取消，请重新运行连接检测。",
            _ => "TCP 2179 失败原因无法安全自动处理；请查看当前宿主日志并在目标机检查控制台服务。"
        });

    private static HostRepairDecision Guidance(string message) => new(false, string.Empty, message);
}
