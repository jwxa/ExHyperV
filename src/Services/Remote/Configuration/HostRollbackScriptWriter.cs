using System.IO;
using System.Text;
using ExHyperV.Services.Logging;

namespace ExHyperV.Services.Remote.Configuration;

public sealed class HostRollbackScriptWriter(
    string? directory = null,
    Func<DateTimeOffset>? clock = null) : IHostRollbackScriptWriter
{
    private readonly string _directory = directory ?? AppLog.LogDirectory;
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public async Task VerifyAvailableAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        string probePath = Path.Combine(_directory, $".rollback-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            await using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                useAsync: true);
            await stream.WriteAsync(new byte[] { 0x58 }, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            try { if (File.Exists(probePath)) File.Delete(probePath); } catch { }
        }
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = Path.GetFullPath(_directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("回滚脚本路径不在 ExHyperV 日志目录中。");
        if (File.Exists(fullPath)) File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public async Task<string> WriteAsync(
        string hostName,
        string hostAddress,
        IReadOnlyList<HostConfigurationCommand> appliedCommands,
        string? existingPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostAddress);
        ArgumentNullException.ThrowIfNull(appliedCommands);
        if (appliedCommands.Count == 0)
            throw new InvalidOperationException("尚未发生配置修改，不生成空回滚脚本。");

        Directory.CreateDirectory(_directory);
        string path = existingPath ?? Path.Combine(
            _directory,
            $"rollback-{_clock():yyyyMMdd-HHmmss}-{SafeFilePart(hostName)}-{Guid.NewGuid():N}.ps1");
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = Path.GetFullPath(_directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("回滚脚本路径不在 ExHyperV 日志目录中。");

        string tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        string script = BuildScript(hostName, hostAddress, appliedCommands);
        try
        {
            await File.WriteAllTextAsync(tempPath, script, new UTF8Encoding(false), cancellationToken);
            File.Move(tempPath, fullPath, overwrite: true);
            return fullPath;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static string BuildScript(
        string hostName,
        string hostAddress,
        IReadOnlyList<HostConfigurationCommand> commands)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#requires -RunAsAdministrator");
        builder.AppendLine("# ExHyperV 远程主机配置回滚脚本");
        builder.AppendLine($"# 目标主机：{Comment(hostName)} ({Comment(hostAddress)})");
        builder.AppendLine("# 仅撤销本次向导已确认成功的修改；每项均带重复执行保护。");
        builder.AppendLine("[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)");
        builder.AppendLine("[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)");
        builder.AppendLine("function ConvertFrom-Utf8Base64([string]$Value) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value)) }");
        builder.AppendLine($"$confirmation = Read-Host (ConvertFrom-Utf8Base64 '{Utf8Base64("输入精确的中文“确认”以执行回滚")}')");
        builder.AppendLine($"if ($confirmation -cne (ConvertFrom-Utf8Base64 '{Utf8Base64("确认")}')) {{ Write-Host (ConvertFrom-Utf8Base64 '{Utf8Base64("输入不匹配，未执行任何回滚。")}'); exit 2 }}");
        builder.AppendLine("$ErrorActionPreference = 'Stop'");
        builder.AppendLine("$failed = $false");
        foreach (HostConfigurationCommand command in commands.Reverse())
        {
            builder.AppendLine();
            builder.AppendLine($"# 回滚：{Comment(command.Title)}");
            builder.AppendLine($"$title = ConvertFrom-Utf8Base64 '{Utf8Base64(command.Title)}'");
            builder.AppendLine($"$rollback = ConvertFrom-Utf8Base64 '{Utf8Base64(command.RollbackScript)}'");
            builder.AppendLine("try {");
            builder.AppendLine("    & ([ScriptBlock]::Create($rollback))");
            builder.AppendLine($"    Write-Host (('[' + (ConvertFrom-Utf8Base64 '{Utf8Base64("完成")}') + '] ') + $title)");
            builder.AppendLine("} catch {");
            builder.AppendLine("    $failed = $true");
            builder.AppendLine($"    Write-Error ((('[' + (ConvertFrom-Utf8Base64 '{Utf8Base64("失败")}') + '] ') + $title + ': ') + $_.Exception.Message) -ErrorAction Continue");
            builder.AppendLine("}");
        }
        builder.AppendLine();
        builder.AppendLine($"if ($failed) {{ Write-Host (ConvertFrom-Utf8Base64 '{Utf8Base64("回滚存在失败项，请查看上方错误。")}'); exit 1 }}");
        builder.AppendLine($"Write-Host (ConvertFrom-Utf8Base64 '{Utf8Base64("本次 ExHyperV 配置修改已回滚。")}')");
        builder.AppendLine("exit 0");
        return builder.ToString();
    }

    private static string SafeFilePart(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "remote-host" : safe;
    }

    private static string Comment(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    private static string Utf8Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
