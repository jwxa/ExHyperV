using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Tools;

namespace ExHyperV.Services.Remote.Vms;

public interface IWmiHostManagementConnection : IHostManagementConnection
{
    WmiContext Context { get; }
}

public interface IHostWmiContextResolver
{
    WmiContext Resolve(HostManagementOperationContext operation);
}

public sealed class HostWmiContextResolver : IHostWmiContextResolver
{
    public WmiContext Resolve(HostManagementOperationContext operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Target.IsLocal) return WmiContext.Local;

        return operation.ManagementConnection is IWmiHostManagementConnection connection
            ? connection.Context
            : throw new InvalidOperationException("活动远程宿主的管理连接不提供 WMI 上下文。");
    }
}

public enum HostVmOperationStatus
{
    Succeeded,
    Failed,
    WriteBlocked,
    Stale,
    Cancelled
}

public sealed record HostVmReadResult<T>(
    HostVmOperationStatus Status,
    T? Value,
    string Message,
    HostManagementOperationContext? Operation)
{
    public bool Succeeded => Status == HostVmOperationStatus.Succeeded;
}

public sealed record HostVmBackendWriteResult(
    bool Succeeded,
    string Message,
    Exception? FailureException = null)
{
    public static HostVmBackendWriteResult Success(string message = "") => new(true, message);
    public static HostVmBackendWriteResult Failure(string message) => new(false, message);
    public static HostVmBackendWriteResult Failure(ApiResponse response, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new(false, message ?? response.Error, response.ToException());
    }
}

public sealed record HostVmWriteResult(
    HostVmOperationStatus Status,
    string Message,
    HostManagementOperationContext? Operation)
{
    public bool Succeeded => Status == HostVmOperationStatus.Succeeded;
}
