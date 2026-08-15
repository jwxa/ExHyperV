using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Tools;

namespace ExHyperV.Services.Remote.Windows;

public sealed class WindowsHostBasicSnapshotLoader : IHostBasicSnapshotLoader
{
    public async Task<HostBasicSnapshot> LoadAsync(
        IHostSessionCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.ManagementConnection is not WindowsHostManagementConnection connection)
            throw new HostSwitchException("候选会话不包含 Windows WMI 管理上下文。" );

        WmiContext context = connection.Context;
        ApiResponse<string> computer = await WmiApi.QueryFirstAsync(
                "SELECT Name FROM Win32_ComputerSystem",
                item => item.TryGetString("Name") ?? candidate.Target.DisplayName,
                WmiScope.CimV2,
                context)
            .WaitAsync(cancellationToken);
        EnsureSuccess(computer, "读取远程计算机名称");

        ApiResponse<string> operatingSystem = await WmiApi.QueryFirstAsync(
                "SELECT Caption, Version FROM Win32_OperatingSystem",
                item => FormatOperatingSystem(item.TryGetString("Caption"), item.TryGetString("Version")),
                WmiScope.CimV2,
                context)
            .WaitAsync(cancellationToken);
        EnsureSuccess(operatingSystem, "读取远程操作系统信息");

        ApiResponse<List<string>> virtualMachines = await WmiApi.QueryAsync(
                "SELECT Name FROM Msvm_SummaryInformation",
                item => item.TryGetString("Name") ?? string.Empty,
                WmiScope.HyperV,
                context)
            .WaitAsync(cancellationToken);
        EnsureSuccess(virtualMachines, "读取远程虚拟机摘要");

        return new HostBasicSnapshot(
            computer.Data ?? candidate.Target.DisplayName,
            operatingSystem.Data ?? "Windows",
            "运行中",
            virtualMachines.Data?.Count ?? 0,
            DateTimeOffset.Now);
    }

    private static string FormatOperatingSystem(string? caption, string? version)
    {
        string name = string.IsNullOrWhiteSpace(caption) ? "Windows" : caption.Trim();
        return string.IsNullOrWhiteSpace(version) ? name : $"{name} ({version.Trim()})";
    }

    private static void EnsureSuccess<T>(ApiResponse<T> response, string operation)
    {
        if (!response.Success)
            throw new HostSwitchException($"{operation}失败（{response.ErrorSource}:{response.Code}）。" );
        if (!response.HasData)
            throw new HostSwitchException($"{operation}未返回数据。" );
    }
}
