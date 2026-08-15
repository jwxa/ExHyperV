using System.Diagnostics;
using System.IO;
using ExHyperV.Services.Logging;

namespace ExHyperV.Services;

public interface ISupportArtifactLocator
{
    SupportArtifactLocationResult OpenLogDirectory(string path);
    SupportArtifactLocationResult RevealRollbackScript(string path);
}

public sealed record SupportArtifactLocationResult(bool Succeeded, string Message);

public sealed class WindowsSupportArtifactLocator : ISupportArtifactLocator
{
    private readonly Action<ProcessStartInfo> _launch;

    public WindowsSupportArtifactLocator() : this(startInfo => Process.Start(startInfo))
    {
    }

    public WindowsSupportArtifactLocator(Action<ProcessStartInfo> launch)
    {
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
    }

    public SupportArtifactLocationResult OpenLogDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failed("日志目录路径为空。");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return Failed($"日志目录路径无效：{SafeMessage(ex)}");
        }

        if (!Directory.Exists(fullPath))
            return Failed($"日志目录不存在：{fullPath}");

        return Launch(
            new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = Quote(fullPath),
                UseShellExecute = true
            },
            "已打开日志目录。",
            "无法打开日志目录");
    }

    public SupportArtifactLocationResult RevealRollbackScript(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failed("尚未生成回滚脚本。");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return Failed($"回滚脚本路径无效：{SafeMessage(ex)}");
        }

        if (!File.Exists(fullPath))
            return Failed($"回滚脚本不存在或已被移动：{fullPath}");

        return Launch(
            new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,{Quote(fullPath)}",
                UseShellExecute = true
            },
            "已在资源管理器中定位回滚脚本。",
            "无法定位回滚脚本");
    }

    private SupportArtifactLocationResult Launch(
        ProcessStartInfo startInfo,
        string successMessage,
        string failurePrefix)
    {
        try
        {
            _launch(startInfo);
            return new SupportArtifactLocationResult(true, successMessage);
        }
        catch (Exception ex)
        {
            return Failed($"{failurePrefix}：{SafeMessage(ex)}");
        }
    }

    private static SupportArtifactLocationResult Failed(string message) => new(false, message);

    private static string SafeMessage(Exception exception) =>
        SensitiveDataRedactor.Redact(exception.GetBaseException().Message);

    private static string Quote(string value) => $"\"{value}\"";
}
