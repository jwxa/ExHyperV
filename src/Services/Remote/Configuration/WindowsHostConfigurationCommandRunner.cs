using System.Management;
using System.IO;
using System.IO.Compression;
using System.Text;
using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Tools;

namespace ExHyperV.Services.Remote.Configuration;

public sealed class WindowsHostConfigurationCommandRunner(TimeSpan? timeout = null) : IHostConfigurationCommandRunner
{
    private const uint HkeyLocalMachine = 0x80000002;
    private const uint MarkerSucceeded = 0;
    private const uint MarkerFailed = 1;
    private const uint MarkerPending = 2;
    private const uint MarkerRollbackFailed = 3;
    private const string RollbackRequiredMarker = "EXHYPERV_ROLLBACK_REQUIRED";
    private const string MarkerKey = @"SOFTWARE\ExHyperV\ConfigurationRuns";
    private const int WindowsCommandLineLimit = 32767;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(45);

    public async Task<HostConfigurationCommandResult> RunAsync(
        string address,
        ResolvedHostIdentity identity,
        HostConfigurationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(command);
        string marker = "Run_" + Guid.NewGuid().ToString("N");
        string commandLine;
        try
        {
            string wrapper = BuildWrapper(command.ApplyScript, command.RollbackScript, marker);
            commandLine = BuildCommandLine(wrapper);
        }
        catch (Exception ex)
        {
            return new(false, $"无法生成远程配置命令：{SensitiveDataRedactor.Redact(ex.Message)}");
        }
        WmiContext context = identity.UsesCurrentWindowsIdentity
            ? WmiContext.RemoteCurrentWindowsIdentity(address, _timeout)
            : WmiContext.Remote(address, identity.UserName!, identity.Password!, _timeout);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            HostConfigurationCommandResult markerResult = await CreateMarkerAsync(context, marker, cancellationToken);
            if (!markerResult.Succeeded) return markerResult;
            cancellationToken.ThrowIfCancellationRequested();

            // Once process creation is submitted, wait for its marker so the caller can persist rollback state.
            ApiResponse<ManagementBaseObject> create = await WmiApi.InvokeClassMethodAsync(
                    "Win32_Process",
                    "Create",
                    input => input["CommandLine"] = commandLine,
                    WmiScope.CimV2,
                    context,
                    CancellationToken.None);
            if (!create.Success)
                return new(false, $"远程进程创建结果不确定：{create.Error}", MayHaveApplied: true);
            create.Data?.Dispose();

            DateTimeOffset deadline = DateTimeOffset.UtcNow + _timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                uint? result = await TryReadMarkerAsync(context, marker, CancellationToken.None);
                if (result is not null && result != MarkerPending)
                    return result switch
                    {
                        MarkerSucceeded => new(true, "远程命令已完成并返回成功标记。"),
                        MarkerRollbackFailed => new(false, "远程命令失败，且本步骤自动补偿未能完成。", MayHaveApplied: true),
                        _ => new(false, $"远程命令执行失败，错误标记为 {result}。")
                    };
                await Task.Delay(250);
            }
            return new(false, $"等待远程配置完成超过 {_timeout.TotalSeconds:0} 秒，目标状态未知。", MayHaveApplied: true);
        }
        finally
        {
            await TryDeleteMarkerAsync(context, marker);
            WmiConnectionCache.Clear(context);
        }
    }

    private static string BuildWrapper(string applyScript, string rollbackScript, string marker)
    {
        string key = $"HKLM:\\{MarkerKey}";
        string markerName = Quote(marker);
        string keyPath = Quote(key);
        string setSuccess = "New-ItemProperty -LiteralPath " + keyPath + " -Name " + markerName
                            + " -PropertyType DWord -Value " + MarkerSucceeded + " -Force | Out-Null";
        string setFailure = "New-ItemProperty -LiteralPath " + keyPath + " -Name " + markerName
                            + " -PropertyType DWord -Value " + MarkerFailed + " -Force | Out-Null";
        string setRollbackFailure = "New-ItemProperty -LiteralPath " + keyPath + " -Name " + markerName
                                    + " -PropertyType DWord -Value " + MarkerRollbackFailed + " -Force | Out-Null";
        return "$ErrorActionPreference='Stop'; $applied=$false; try { & { " + applyScript
               + " }; $applied=$true; " + setSuccess
               + " } catch { $needsRollback = $applied -or $_.Exception.Message -eq '" + RollbackRequiredMarker
               + "'; $rollbackFailed=$false; if ($needsRollback) { try { & { " + rollbackScript
               + " } } catch { $rollbackFailed=$true } }; try { if ($rollbackFailed) { " + setRollbackFailure
               + " } else { " + setFailure + " } } catch {}; exit 1 }";
    }

    private static string BuildCommandLine(string wrapper)
    {
        byte[] wrapperBytes = Encoding.UTF8.GetBytes(wrapper);
        using var compressedStream = new MemoryStream();
        using (var gzip = new GZipStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(wrapperBytes, 0, wrapperBytes.Length);

        string payload = Convert.ToBase64String(compressedStream.ToArray());
        string bootstrap =
            "$data=[Convert]::FromBase64String('" + payload + "');" +
            "$memory=[IO.MemoryStream]::new($data);" +
            "$gzip=[IO.Compression.GZipStream]::new($memory,[IO.Compression.CompressionMode]::Decompress);" +
            "$reader=[IO.StreamReader]::new($gzip,[Text.Encoding]::UTF8);" +
            "try { & ([ScriptBlock]::Create($reader.ReadToEnd())) } finally { $reader.Dispose(); $gzip.Dispose(); $memory.Dispose() }";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
        string commandLine =
            $"powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";
        if (commandLine.Length >= WindowsCommandLineLimit)
            throw new InvalidOperationException(
                $"远程配置命令压缩后仍有 {commandLine.Length} 个字符，超过 Windows 进程命令行上限。");
        return commandLine;
    }

    private async Task<HostConfigurationCommandResult> CreateMarkerAsync(
        WmiContext context,
        string marker,
        CancellationToken cancellationToken)
    {
        ApiResponse<ManagementBaseObject> createKey = await WmiApi.InvokeClassMethodAsync(
                "StdRegProv",
                "CreateKey",
                input =>
                {
                    input["hDefKey"] = HkeyLocalMachine;
                    input["sSubKeyName"] = MarkerKey;
                },
                WmiScope.Default,
                context,
                CancellationToken.None);
        using ManagementBaseObject? createKeyOutput = createKey.Data;
        if (!createKey.Success)
            return new(false, $"无法创建远程完成标记目录：{createKey.Error}");

        ApiResponse<ManagementBaseObject> setValue = await WmiApi.InvokeClassMethodAsync(
                "StdRegProv",
                "SetDWORDValue",
                input =>
                {
                    input["hDefKey"] = HkeyLocalMachine;
                    input["sSubKeyName"] = MarkerKey;
                    input["sValueName"] = marker;
                    input["uValue"] = MarkerPending;
                },
                WmiScope.Default,
                context,
                CancellationToken.None);
        using ManagementBaseObject? setValueOutput = setValue.Data;
        return setValue.Success
            ? new(true, "远程执行中标记已建立。")
            : new(false, $"无法创建远程执行中标记：{setValue.Error}");
    }

    private async Task<uint?> TryReadMarkerAsync(
        WmiContext context,
        string marker,
        CancellationToken cancellationToken)
    {
        ApiResponse<ManagementBaseObject> response = await WmiApi.InvokeClassMethodAsync(
                "StdRegProv",
                "GetDWORDValue",
                input =>
                {
                    input["hDefKey"] = HkeyLocalMachine;
                    input["sSubKeyName"] = MarkerKey;
                    input["sValueName"] = marker;
                },
                WmiScope.Default,
                context,
                cancellationToken);
        if (!response.Success) return null;
        using ManagementBaseObject? output = response.Data;
        return output?["uValue"] is null ? null : Convert.ToUInt32(output["uValue"]);
    }

    private static async Task TryDeleteMarkerAsync(WmiContext context, string marker)
    {
        try
        {
            ApiResponse<ManagementBaseObject> response = await WmiApi.InvokeClassMethodAsync(
                "StdRegProv",
                "DeleteValue",
                input =>
                {
                    input["hDefKey"] = HkeyLocalMachine;
                    input["sSubKeyName"] = MarkerKey;
                    input["sValueName"] = marker;
                },
                WmiScope.Default,
                context,
                CancellationToken.None);
            response.Data?.Dispose();
        }
        catch { }
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
