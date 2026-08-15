using System.Diagnostics;
using ExHyperV.Services;

internal static class SupportArtifactTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("SupportArtifacts_LogDirectoryUsesExplorer", LogDirectoryUsesExplorer),
        ("SupportArtifacts_RollbackScriptUsesExplorerSelection", RollbackScriptUsesExplorerSelection),
        ("SupportArtifacts_MissingPathDoesNotLaunch", MissingPathDoesNotLaunch),
        ("SupportArtifacts_LaunchFailureIsRedacted", LaunchFailureIsRedacted)
    ];

    private static void LogDirectoryUsesExplorer()
    {
        using var temp = new TempDirectory();
        ProcessStartInfo? captured = null;
        var locator = new WindowsSupportArtifactLocator(startInfo => captured = startInfo);

        SupportArtifactLocationResult result = locator.OpenLogDirectory(temp.Path);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("explorer.exe", captured?.FileName);
        Assert.Equal($"\"{Path.GetFullPath(temp.Path)}\"", captured?.Arguments);
        Assert.True(captured?.UseShellExecute == true, "Explorer must launch through the Windows shell.");
        Assert.Contains("日志目录", result.Message);
    }

    private static void RollbackScriptUsesExplorerSelection()
    {
        using var temp = new TempDirectory();
        string rollbackPath = Path.Combine(temp.Path, "回滚-远程主机.ps1");
        File.WriteAllText(rollbackPath, "# 确认", new System.Text.UTF8Encoding(false));
        ProcessStartInfo? captured = null;
        var locator = new WindowsSupportArtifactLocator(startInfo => captured = startInfo);

        SupportArtifactLocationResult result = locator.RevealRollbackScript(rollbackPath);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("explorer.exe", captured?.FileName);
        Assert.Equal($"/select,\"{Path.GetFullPath(rollbackPath)}\"", captured?.Arguments);
        Assert.Contains("定位回滚脚本", result.Message);
    }

    private static void MissingPathDoesNotLaunch()
    {
        using var temp = new TempDirectory();
        int launches = 0;
        var locator = new WindowsSupportArtifactLocator(_ => launches++);

        SupportArtifactLocationResult missingLog = locator.OpenLogDirectory(Path.Combine(temp.Path, "missing-logs"));
        SupportArtifactLocationResult missingRollback = locator.RevealRollbackScript(Path.Combine(temp.Path, "missing.ps1"));
        SupportArtifactLocationResult emptyRollback = locator.RevealRollbackScript(string.Empty);

        Assert.False(missingLog.Succeeded, "A missing log directory must fail explicitly.");
        Assert.False(missingRollback.Succeeded, "A missing rollback script must fail explicitly.");
        Assert.False(emptyRollback.Succeeded, "An empty rollback path must fail explicitly.");
        Assert.Equal(0, launches);
        Assert.Contains("不存在", missingLog.Message);
        Assert.Contains("不存在或已被移动", missingRollback.Message);
        Assert.Contains("尚未生成", emptyRollback.Message);
    }

    private static void LaunchFailureIsRedacted()
    {
        using var temp = new TempDirectory();
        const string secret = "support-artifact-secret";
        var locator = new WindowsSupportArtifactLocator(_ =>
            throw new InvalidOperationException($"password={secret}"));

        SupportArtifactLocationResult result = locator.OpenLogDirectory(temp.Path);

        Assert.False(result.Succeeded, "A shell launch exception must be reported as failure.");
        Assert.Contains("无法打开日志目录", result.Message);
        Assert.Contains("[REDACTED]", result.Message);
        Assert.DoesNotContain(secret, result.Message);
    }
}
