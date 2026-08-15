using System.IO;

namespace ExHyperV.Services;

internal static class AppDataPaths
{
    private static readonly string AppDataDirectory = ResolveAppDataDirectory();

    internal static string ConfigFilePath { get; } = Path.Combine(AppDataDirectory, "Config.xml");
    internal static string HostProfilesFilePath { get; } = Path.Combine(AppDataDirectory, "Hosts.xml");

    private static string ResolveAppDataDirectory()
    {
        string appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExHyperV");

        // 不迁移、不读取、也不回退到 EXE 或工作目录旁的旧配置。
        try { Directory.CreateDirectory(appDataDirectory); }
        catch { }

        return appDataDirectory;
    }
}
