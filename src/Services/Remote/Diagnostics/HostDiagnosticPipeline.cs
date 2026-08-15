using System.Diagnostics;
using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.Services.Remote.Diagnostics;

public sealed class HostDiagnosticPipeline(
    IIpv4ReachabilityProbe ipv4Probe,
    IHostIdentityResolver identityResolver,
    IExplicitCredentialValidator explicitCredentialValidator,
    IWmiDcomProbe wmiProbe,
    ITcpPortProbe tcpProbe,
    Func<DateTimeOffset>? clock = null)
{
    public const int ConsolePort = 2179;
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public async Task<HostDiagnosticReport> RunAsync(
        HostProfile profile,
        WindowsCredential? transientCredential = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        HostId hostId = HostId.FromProfile(profile);
        DateTimeOffset startedAt = _clock();
        var totalStopwatch = Stopwatch.StartNew();
        var steps = new List<HostDiagnosticStepResult>(4);
        var logs = new List<HostDiagnosticLogEntry>();
        bool cancelled = false;
        ResolvedHostIdentity? identity = null;
        ExplicitCredentialValidationResult? credentialValidation = null;

        Log(null, HostDiagnosticLogLevel.Information, $"开始检测主机 {profile.DisplayName}（{profile.Address}）。");

        HostDiagnosticStepResult ipv4 = await RunAsyncStep(
            HostDiagnosticStepKind.Ipv4Reachability,
            "开始检测 IPv4 可达性。",
            "IPv4 主机可达。",
            token => ipv4Probe.ProbeAsync(profile.Address, token));
        steps.Add(ipv4);
        cancelled = ipv4.Status == HostDiagnosticStepStatus.Cancelled;

        HostDiagnosticStepResult identityStep;
        if (cancelled)
        {
            identityStep = Skipped(HostDiagnosticStepKind.Identity, "检测已取消，未解析连接身份。");
        }
        else
        {
            identityStep = await RunIdentityAsync();
            cancelled = identityStep.Status == HostDiagnosticStepStatus.Cancelled;
        }
        steps.Add(identityStep);

        HostDiagnosticStepResult wmi;
        if (cancelled)
        {
            wmi = Skipped(HostDiagnosticStepKind.WmiDcom, "检测已取消，未执行 WMI/DCOM 查询。");
        }
        else if (identity is null)
        {
            wmi = Skipped(HostDiagnosticStepKind.WmiDcom, "连接身份不可用，已跳过 WMI/DCOM 查询。");
        }
        else
        {
            wmi = await RunAsyncStep(
                HostDiagnosticStepKind.WmiDcom,
                $"开始查询 WMI/DCOM 命名空间 {WindowsWmiDcomProbe.HyperVNamespace}。",
                $"WMI/DCOM {WindowsWmiDcomProbe.HyperVNamespace} 查询成功，管理通道可用。",
                ProbeWmiAsync);
            cancelled = wmi.Status == HostDiagnosticStepStatus.Cancelled;
        }
        steps.Add(wmi);

        HostDiagnosticStepResult tcp;
        if (cancelled)
        {
            tcp = Skipped(HostDiagnosticStepKind.Tcp2179, "检测已取消，未检测 TCP 2179。");
        }
        else
        {
            tcp = await RunAsyncStep(
                HostDiagnosticStepKind.Tcp2179,
                $"开始连接 TCP {profile.Address}:{ConsolePort}。",
                $"TCP {ConsolePort} 连接成功，控制台通道可用。",
                token => tcpProbe.ProbeAsync(profile.Address, ConsolePort, token));
            cancelled = tcp.Status == HostDiagnosticStepStatus.Cancelled;
        }
        steps.Add(tcp);

        totalStopwatch.Stop();
        HostDiagnosticAvailability availability = GetAvailability(cancelled, wmi, tcp);
        Log(
            null,
            availability is HostDiagnosticAvailability.FullyAvailable
                ? HostDiagnosticLogLevel.Information
                : HostDiagnosticLogLevel.Warning,
            $"检测完成，总体状态：{AvailabilityText(availability)}；管理通道：{ChannelText(wmi)}；控制台通道：{ChannelText(tcp)}。");

        return new HostDiagnosticReport(
            profile.Id,
            profile.Address,
            startedAt,
            totalStopwatch.Elapsed,
            availability,
            steps.AsReadOnly(),
            logs.AsReadOnly());

        async Task<HostDiagnosticStepResult> RunIdentityAsync()
        {
            const HostDiagnosticStepKind kind = HostDiagnosticStepKind.Identity;
            Log(kind, HostDiagnosticLogLevel.Information, "开始解析连接身份。");
            var stopwatch = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                identity = identityResolver.Resolve(profile, transientCredential);
                if (!identity.UsesCurrentWindowsIdentity)
                {
                    Log(kind, HostDiagnosticLogLevel.Information, "开始验证显式凭据；密码不会写入日志。");
                    credentialValidation = await explicitCredentialValidator.ValidateAsync(
                        profile.Address,
                        identity,
                        cancellationToken);
                    HostDiagnosticLogLevel validationLevel = credentialValidation.Status switch
                    {
                        ExplicitCredentialValidationStatus.Valid => HostDiagnosticLogLevel.Information,
                        ExplicitCredentialValidationStatus.Invalid => HostDiagnosticLogLevel.Error,
                        _ => HostDiagnosticLogLevel.Warning
                    };
                    Log(
                        kind,
                        validationLevel,
                        credentialValidation.Explanation,
                        errorCode: credentialValidation.Status == ExplicitCredentialValidationStatus.Invalid
                            ? HostDiagnosticErrorCode.InvalidCredential
                            : HostDiagnosticErrorCode.None);
                    if (credentialValidation.Status == ExplicitCredentialValidationStatus.Invalid)
                    {
                        identity = null;
                        return new(
                            kind,
                            HostDiagnosticStepStatus.Failed,
                            stopwatch.Elapsed,
                            credentialValidation.Explanation,
                            HostDiagnosticErrorCode.InvalidCredential);
                    }
                }

                string explanation = identity.UsesCurrentWindowsIdentity
                    ? "将使用当前 Windows 身份，不访问凭据管理器。"
                    : $"已解析显式身份 {identity.UserName}，密码不会写入日志。{CredentialValidationSuffix()}";
                Log(kind, HostDiagnosticLogLevel.Information, explanation);
                return new(kind, HostDiagnosticStepStatus.Succeeded, stopwatch.Elapsed, explanation);
            }
            catch (OperationCanceledException)
            {
                const string explanation = "身份解析已取消。";
                Log(
                    kind,
                    HostDiagnosticLogLevel.Warning,
                    explanation,
                    errorCode: HostDiagnosticErrorCode.Cancelled);
                return new(kind, HostDiagnosticStepStatus.Cancelled, stopwatch.Elapsed, explanation, HostDiagnosticErrorCode.Cancelled);
            }
            catch (HostDiagnosticException ex)
            {
                string explanation = SensitiveDataRedactor.Redact(ex.Message);
                Log(kind, HostDiagnosticLogLevel.Error, explanation, ex, ex.ErrorCode);
                return new(kind, HostDiagnosticStepStatus.Failed, stopwatch.Elapsed, explanation, ex.ErrorCode);
            }
            catch (Exception ex)
            {
                const string explanation = "解析连接身份时发生未预期错误。";
                Log(
                    kind,
                    HostDiagnosticLogLevel.Error,
                    explanation,
                    ex,
                    HostDiagnosticErrorCode.Unexpected);
                return new(kind, HostDiagnosticStepStatus.Failed, stopwatch.Elapsed, explanation, HostDiagnosticErrorCode.Unexpected);
            }
        }

        async Task ProbeWmiAsync(CancellationToken token)
        {
            try
            {
                await wmiProbe.ProbeAsync(profile.Address, identity!, token);
            }
            catch (HostDiagnosticException ex) when (
                credentialValidation?.Status == ExplicitCredentialValidationStatus.Valid
                && ex.ErrorCode == HostDiagnosticErrorCode.AuthenticationFailed)
            {
                throw new HostDiagnosticException(
                    HostDiagnosticErrorCode.AccessDenied,
                    "显式凭据有效，但目标账户没有 WMI/DCOM 或 Hyper-V 管理权限。",
                    ex);
            }
            catch (HostDiagnosticException ex) when (
                credentialValidation?.Status == ExplicitCredentialValidationStatus.Inconclusive
                && ex.ErrorCode == HostDiagnosticErrorCode.AuthenticationFailed)
            {
                throw new HostDiagnosticException(
                    HostDiagnosticErrorCode.AuthenticationFailed,
                    "WMI/DCOM 拒绝了显式凭据，但无法独立确认密码。请先编辑主机配置并重新输入用户名和密码；如果确认正确，再检查目标账户的 WMI/DCOM 和 Hyper-V 权限。",
                    ex);
            }
        }

        string CredentialValidationSuffix() => credentialValidation?.Status switch
        {
            ExplicitCredentialValidationStatus.Valid => "凭据验证通过。",
            ExplicitCredentialValidationStatus.Inconclusive => "密码预验证无法完成，将由 WMI/DCOM 返回最终结果。",
            _ => string.Empty
        };

        async Task<HostDiagnosticStepResult> RunAsyncStep(
            HostDiagnosticStepKind kind,
            string startMessage,
            string successMessage,
            Func<CancellationToken, Task> action)
        {
            Log(kind, HostDiagnosticLogLevel.Information, startMessage);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await action(cancellationToken);
                Log(kind, HostDiagnosticLogLevel.Information, successMessage);
                return new(kind, HostDiagnosticStepStatus.Succeeded, stopwatch.Elapsed, successMessage);
            }
            catch (OperationCanceledException)
            {
                string explanation = $"{StepName(kind)}检测已取消。";
                Log(
                    kind,
                    HostDiagnosticLogLevel.Warning,
                    explanation,
                    errorCode: HostDiagnosticErrorCode.Cancelled);
                return new(kind, HostDiagnosticStepStatus.Cancelled, stopwatch.Elapsed, explanation, HostDiagnosticErrorCode.Cancelled);
            }
            catch (HostDiagnosticException ex)
            {
                string explanation = SensitiveDataRedactor.Redact(ex.Message);
                Log(kind, HostDiagnosticLogLevel.Error, explanation, ex, ex.ErrorCode);
                return new(kind, HostDiagnosticStepStatus.Failed, stopwatch.Elapsed, explanation, ex.ErrorCode);
            }
            catch (Exception ex)
            {
                string explanation = $"{StepName(kind)}检测发生未预期错误。";
                Log(
                    kind,
                    HostDiagnosticLogLevel.Error,
                    explanation,
                    ex,
                    HostDiagnosticErrorCode.Unexpected);
                return new(kind, HostDiagnosticStepStatus.Failed, stopwatch.Elapsed, explanation, HostDiagnosticErrorCode.Unexpected);
            }
        }

        HostDiagnosticStepResult Skipped(HostDiagnosticStepKind kind, string explanation)
        {
            Log(kind, HostDiagnosticLogLevel.Warning, explanation);
            return new(kind, HostDiagnosticStepStatus.Skipped, TimeSpan.Zero, explanation);
        }

        void Log(
            HostDiagnosticStepKind? step,
            HostDiagnosticLogLevel level,
            string message,
            Exception? exception = null,
            HostDiagnosticErrorCode errorCode = HostDiagnosticErrorCode.None)
        {
            logs.Add(new HostDiagnosticLogEntry(_clock(), step, level, message));
            var context = new AppLogContext(
                Host: profile.Address,
                Properties: step is null
                    ? null
                    : new Dictionary<string, object?> { ["诊断步骤"] = step.ToString() },
                HostId: hostId,
                ErrorCategory: errorCode.ToString());
            switch (level)
            {
                case HostDiagnosticLogLevel.Information:
                    AppLog.Information("连接诊断", message, context);
                    break;
                case HostDiagnosticLogLevel.Warning:
                    AppLog.Warning("连接诊断", message, context, exception);
                    break;
                case HostDiagnosticLogLevel.Error:
                    AppLog.Error("连接诊断", message, context, exception);
                    break;
            }
        }
    }

    private static HostDiagnosticAvailability GetAvailability(
        bool cancelled,
        HostDiagnosticStepResult wmi,
        HostDiagnosticStepResult tcp)
    {
        if (cancelled) return HostDiagnosticAvailability.Cancelled;
        bool management = wmi.Status == HostDiagnosticStepStatus.Succeeded;
        bool console = tcp.Status == HostDiagnosticStepStatus.Succeeded;
        if (management && console) return HostDiagnosticAvailability.FullyAvailable;
        if (management || console) return HostDiagnosticAvailability.PartiallyAvailable;
        return HostDiagnosticAvailability.Unavailable;
    }

    private static string StepName(HostDiagnosticStepKind kind) => kind switch
    {
        HostDiagnosticStepKind.Ipv4Reachability => "IPv4 可达性",
        HostDiagnosticStepKind.Identity => "连接身份",
        HostDiagnosticStepKind.WmiDcom => "WMI/DCOM",
        HostDiagnosticStepKind.Tcp2179 => "TCP 2179",
        _ => "连接"
    };

    private static string ChannelText(HostDiagnosticStepResult result) => result.Status switch
    {
        HostDiagnosticStepStatus.Succeeded => "可用",
        HostDiagnosticStepStatus.Failed => "不可用",
        HostDiagnosticStepStatus.Cancelled => "已取消",
        _ => "未检测"
    };

    private static string AvailabilityText(HostDiagnosticAvailability availability) => availability switch
    {
        HostDiagnosticAvailability.FullyAvailable => "全部可用",
        HostDiagnosticAvailability.PartiallyAvailable => "部分可用",
        HostDiagnosticAvailability.Unavailable => "不可用",
        HostDiagnosticAvailability.Cancelled => "已取消",
        _ => "未知"
    };
}
