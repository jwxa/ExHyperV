using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Preflight;
using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.Services.Remote.Configuration;

public sealed class HostConfigurationPipeline(
    IHostIdentityResolver identityResolver,
    HostPreflightPipeline preflightPipeline,
    IHostConfigurationCommandRunner commandRunner,
    IHostRollbackScriptWriter rollbackWriter,
    HostDiagnosticPipeline diagnosticPipeline)
{
    public async Task<HostConfigurationReport> ApplyAsync(
        HostProfile profile,
        WindowsCredential? transientCredential,
        HostPreflightReport approvedReport,
        HostPreflightPlan approvedPlan,
        string? confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(approvedReport);
        ArgumentNullException.ThrowIfNull(approvedPlan);
        var logs = new List<string>();
        var results = new List<HostConfigurationStepResult>();
        var applied = new List<HostConfigurationCommand>();
        string? rollbackPath = null;
        HostPreflightReport? verification = null;
        HostDiagnosticReport? diagnostic = null;

        if (!HostConfigurationConfirmation.IsExact(confirmation))
        {
            Log("确认文字不完全匹配，未执行任何修改。", warning: true);
            return Report(started: false, succeeded: false, stale: false);
        }
        if (approvedReport.ProfileId != profile.Id
            || !string.Equals(approvedReport.HostAddress, profile.Address, StringComparison.Ordinal))
        {
            Log("预检目标与当前主机不一致，拒绝执行。", warning: true);
            return Report(started: false, succeeded: false, stale: true);
        }

        ResolvedHostIdentity identity;
        try
        {
            identity = identityResolver.Resolve(profile, transientCredential);
            Log($"已确认配置目标 {profile.DisplayName}（{profile.Address}），开始重新检测预览是否仍然有效。");
            HostPreflightReport freshReport = await preflightPipeline.RunAsync(profile, transientCredential, cancellationToken);
            HostPreflightPlanResult freshResult = HostPreflightPlanner.Build(freshReport, SelectionFrom(approvedPlan));
            if (!freshResult.IsValid || !PlansEqual(approvedPlan, freshResult.Plan!))
            {
                Log("远程状态已发生变化，原修改预览已过期；未执行任何修改。", warning: true);
                return Report(started: false, succeeded: false, stale: true, freshReport);
            }

            IReadOnlyList<HostConfigurationCommand> commands = HostConfigurationCommandCompiler.Compile(freshReport, freshResult.Plan!);
            await rollbackWriter.VerifyAvailableAsync(cancellationToken);
            Log("回滚脚本目录写入检查通过，允许开始远程修改。");
            foreach (HostConfigurationCommand command in commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<HostConfigurationCommand> rollbackCandidates = applied
                    .Append(command)
                    .ToArray();
                rollbackPath = await rollbackWriter.WriteAsync(
                    profile.DisplayName,
                    profile.Address,
                    rollbackCandidates,
                    rollbackPath,
                    cancellationToken);
                Log($"远程提交前已持久化保护性回滚脚本：{rollbackPath}。");
                Log($"开始执行：{command.Title}。");
                HostConfigurationCommandResult commandResult;
                try
                {
                    commandResult = await commandRunner.RunAsync(
                        profile.Address,
                        identity,
                        command,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    applied.Add(command);
                    const string message = "远程提交已开始但结果被取消；保护性回滚条目已保留。";
                    results.Add(new(command.Kind, command.Title, false, message));
                    Log($"{command.Title} {message}", warning: true);
                    throw;
                }
                catch (Exception ex)
                {
                    applied.Add(command);
                    string message = $"远程提交结果未知；保护性回滚条目已保留：{SensitiveDataRedactor.Redact(ex.Message)}";
                    results.Add(new(command.Kind, command.Title, false, message));
                    Log($"{command.Title} {message}", warning: true, exception: ex);
                    break;
                }
                if (!commandResult.Succeeded)
                {
                    if (commandResult.MayHaveApplied)
                    {
                        applied.Add(command);
                        Log($"目标状态不确定；已保留包含 {command.Title} 的保护性回滚脚本：{rollbackPath}。", warning: true);
                    }
                    else
                    {
                        try
                        {
                            rollbackPath = await RestoreAppliedRollbackAsync(
                                rollbackWriter,
                                profile,
                                applied,
                                rollbackPath);
                            Log($"远程步骤明确未执行；已从回滚脚本移除 {command.Title}。", warning: true);
                        }
                        catch (Exception ex)
                        {
                            Log(
                                $"远程步骤明确未执行，但收缩回滚脚本失败；保留更保守的幂等条目：{SensitiveDataRedactor.Redact(ex.Message)}",
                                warning: true,
                                exception: ex);
                        }
                    }
                    string message = $"执行失败：{SensitiveDataRedactor.Redact(commandResult.Message)}";
                    results.Add(new(command.Kind, command.Title, false, message));
                    Log($"{command.Title} {message}", warning: true);
                    break;
                }

                applied.Add(command);
                Log($"远程命令已完成；保护性回滚脚本继续覆盖 {command.Title}。");

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    verification = await preflightPipeline.RunAsync(profile, transientCredential, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    const string message = "远程命令已完成且回滚已记录，但状态复检已取消。";
                    results.Add(new(command.Kind, command.Title, false, message));
                    Log($"{command.Title} {message}", warning: true);
                    throw;
                }
                catch (Exception ex)
                {
                    string message = $"远程命令已完成且回滚已记录，但状态复检失败：{SensitiveDataRedactor.Redact(ex.Message)}";
                    results.Add(new(command.Kind, command.Title, false, message));
                    Log($"{command.Title} {message}", warning: true, exception: ex);
                    break;
                }
                if (!IsApplied(command, freshResult.Plan!, verification, out string evidence))
                {
                    string message = $"远程命令已返回成功，但状态验证失败：{evidence}";
                    results.Add(new(command.Kind, command.Title, false, message));
                    Log($"{command.Title} {message}", warning: true);
                    break;
                }
                results.Add(new(command.Kind, command.Title, true, evidence));
                Log($"完成并验证：{command.Title}。{evidence}");
            }
        }
        catch (OperationCanceledException)
        {
            Log("配置执行已取消。已成功应用的修改仍可使用回滚脚本恢复。", warning: true);
        }
        catch (Exception ex)
        {
            Log($"配置执行发生错误：{SensitiveDataRedactor.Redact(ex.Message)}。", warning: true, exception: ex);
        }

        if (applied.Count > 0)
        {
            try
            {
                diagnostic = await diagnosticPipeline.RunAsync(profile, transientCredential, cancellationToken);
                Log($"连接复检完成：WMI/DCOM={(diagnostic.ManagementAvailable ? "可用" : "不可用")}，TCP 2179={(diagnostic.ConsoleAvailable ? "可用" : "不可用")}。");
            }
            catch (Exception ex)
            {
                Log($"连接复检失败：{SensitiveDataRedactor.Redact(ex.Message)}。", warning: true, exception: ex);
            }
        }

        bool succeeded = approvedPlan.Changes.Count > 0
            && results.Count == approvedPlan.Changes.Count
            && results.All(result => result.Succeeded);
        return Report(applied.Count > 0, succeeded, stale: false, verification, diagnostic);

        HostConfigurationReport Report(
            bool started,
            bool succeeded,
            bool stale,
            HostPreflightReport? verified = null,
            HostDiagnosticReport? checkedDiagnostic = null) =>
            new(
                started,
                succeeded,
                stale,
                results.AsReadOnly(),
                rollbackPath,
                verified,
                checkedDiagnostic,
                logs.AsReadOnly());

        void Log(string message, bool warning = false, Exception? exception = null)
        {
            logs.Add(message);
            var context = new AppLogContext(profile.Address);
            if (warning) AppLog.Warning("远程配置", message, context, exception);
            else AppLog.Information("远程配置", message, context);
        }
    }

    private static HostPreflightSelection SelectionFrom(HostPreflightPlan plan) => new(
        plan.AccountKind,
        plan.AccountName,
        plan.SelectedNetworks.Select(network => network.InterfaceIndex).ToArray(),
        plan.NetworksToMakePrivate,
        plan.AllowedIpv4Cidrs);

    private static async Task<string?> RestoreAppliedRollbackAsync(
        IHostRollbackScriptWriter rollbackWriter,
        HostProfile profile,
        IReadOnlyList<HostConfigurationCommand> applied,
        string? rollbackPath)
    {
        if (rollbackPath is null) return null;
        if (applied.Count == 0)
        {
            await rollbackWriter.DeleteAsync(rollbackPath, CancellationToken.None);
            return null;
        }
        return await rollbackWriter.WriteAsync(
            profile.DisplayName,
            profile.Address,
            applied,
            rollbackPath,
            CancellationToken.None);
    }

    private static bool PlansEqual(HostPreflightPlan approved, HostPreflightPlan fresh) =>
        approved.AccountKind == fresh.AccountKind
        && string.Equals(approved.AccountName, fresh.AccountName, StringComparison.Ordinal)
        && approved.SelectedNetworks.Select(network => network.InterfaceIndex)
            .SequenceEqual(fresh.SelectedNetworks.Select(network => network.InterfaceIndex))
        && approved.NetworksToMakePrivate.Order().SequenceEqual(fresh.NetworksToMakePrivate.Order())
        && approved.AllowedIpv4Cidrs.SequenceEqual(fresh.AllowedIpv4Cidrs, StringComparer.OrdinalIgnoreCase)
        && approved.Changes.SequenceEqual(fresh.Changes);

    private static bool IsApplied(
        HostConfigurationCommand command,
        HostPreflightPlan plan,
        HostPreflightReport report,
        out string evidence)
    {
        bool Member(HostLocalGroupKind groupKind) =>
            report.Facts.LocalGroups.TryGetValue(groupKind, out HostLocalGroupSnapshot? group)
            && group.Members.Any(member =>
                string.Equals(member, plan.AccountName, StringComparison.OrdinalIgnoreCase)
                || plan.AccountKind == HostPreflightAccountKind.Local
                   && member.EndsWith($"\\{plan.AccountName}", StringComparison.OrdinalIgnoreCase));

        bool applied = command.Kind switch
        {
            HostPreflightChangeKind.AddHyperVAdministrators => Member(HostLocalGroupKind.HyperVAdministrators),
            HostPreflightChangeKind.AddRemoteManagementUsers => Member(HostLocalGroupKind.RemoteManagementUsers),
            HostPreflightChangeKind.EnableLocalAccountTokenFilterPolicy =>
                report.Facts.TokenFilterPolicy == HostTokenFilterPolicyState.Enabled,
            HostPreflightChangeKind.ChangeNetworkToPrivate => NetworkChangeApplied(command, plan, report),
            HostPreflightChangeKind.RestoreWmiFirewallRules =>
                report.Facts.Firewall?.WmiRuleNamesToRestore.Count == 0,
            HostPreflightChangeKind.RestoreHyperVFirewallRules =>
                report.Facts.Firewall?.HyperVRuleNamesToRestore.Count == 0,
            HostPreflightChangeKind.EnableWmiFirewallRules => report.Facts.Firewall?.WmiBuiltInRulesEnabled == true,
            HostPreflightChangeKind.EnableHyperVFirewallRules => report.Facts.Firewall?.HyperVBuiltInRulesEnabled == true,
            HostPreflightChangeKind.ConfigureConsole2179FirewallRule => ConsoleRuleMatches(report.Facts.Firewall, plan),
            _ => false
        };
        evidence = applied ? "状态复检通过。" : "最新只读预检未观察到预期状态。";
        return applied;
    }

    private static bool NetworkChangeApplied(
        HostConfigurationCommand command,
        HostPreflightPlan plan,
        HostPreflightReport report)
    {
        HostNetworkSnapshot? expected = plan.SelectedNetworks.FirstOrDefault(network =>
            plan.NetworksToMakePrivate.Contains(network.InterfaceIndex)
            && string.Equals(command.Title, $"将“{network.Name}”改为 Private", StringComparison.Ordinal));
        return expected is not null && report.Facts.Networks.Any(network =>
            network.InterfaceIndex == expected.InterfaceIndex
            && network.Category == HostNetworkCategory.Private);
    }

    private static bool ConsoleRuleMatches(HostFirewallSnapshot? firewall, HostPreflightPlan plan)
    {
        if (firewall?.ExHyperVConsole2179RuleEnabled != true || !firewall.Console2179EndpointMatches) return false;
        string[] actual = firewall.Console2179RemoteAddresses
            .Select(value => Ipv4Cidr.TryNormalizeFirewallAddress(value, out string normalized) ? normalized : value)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] expected = plan.AllowedIpv4Cidrs.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        return actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase);
    }
}
