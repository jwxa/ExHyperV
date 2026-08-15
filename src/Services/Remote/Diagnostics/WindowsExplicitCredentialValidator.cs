using System.Runtime.InteropServices;
using System.Net.Sockets;

namespace ExHyperV.Services.Remote.Diagnostics;

public sealed class WindowsExplicitCredentialValidator : IExplicitCredentialValidator
{
    private const int ErrorSuccess = 0;
    private const int ErrorInvalidPassword = 86;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorAccountRestriction = 1327;
    private const int ErrorPasswordExpired = 1330;
    private const int ErrorAccountDisabled = 1331;
    private const int ErrorLogonTypeNotGranted = 1385;
    private const int ErrorAccountExpired = 1793;
    private const int ErrorAccountLockedOut = 1909;
    private const int ErrorBadUsername = 2202;
    private const int ErrorNotConnected = 2250;

    private readonly IWindowsNetworkCredentialApi _networkApi;
    private readonly TimeSpan _timeout;

    public WindowsExplicitCredentialValidator(TimeSpan? timeout = null)
        : this(new WindowsNetworkCredentialApi(), timeout)
    {
    }

    internal WindowsExplicitCredentialValidator(
        IWindowsNetworkCredentialApi networkApi,
        TimeSpan? timeout = null)
    {
        _networkApi = networkApi ?? throw new ArgumentNullException(nameof(networkApi));
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task<ExplicitCredentialValidationResult> ValidateAsync(
        string address,
        ResolvedHostIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.UsesCurrentWindowsIdentity)
            throw new ArgumentException("当前 Windows 身份不需要显式凭据验证。", nameof(identity));
        if (string.IsNullOrWhiteSpace(identity.UserName) || identity.Password is null)
            throw new ArgumentException("显式凭据缺少用户名或密码。", nameof(identity));

        TimeSpan availabilityTimeout = _timeout < TimeSpan.FromSeconds(2)
            ? _timeout
            : TimeSpan.FromSeconds(2);
        if (!await _networkApi.IsSmbAvailableAsync(address, availabilityTimeout, cancellationToken))
        {
            return Inconclusive(
                "目标主机未开放 SMB 凭据校验通道，无法独立确认密码；将继续由 WMI/DCOM 返回最终结果。");
        }

        try
        {
            return await Task.Run(
                    () => ValidateCore(address, identity.UserName, identity.Password),
                    CancellationToken.None)
                .WaitAsync(_timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return Inconclusive("显式凭据预验证超时，将继续由 WMI/DCOM 返回最终结果。");
        }
    }

    private ExplicitCredentialValidationResult ValidateCore(
        string address,
        string userName,
        string password)
    {
        int queryError = _networkApi.HasConnectionToServer(address, out bool hasConnection);
        if (queryError != ErrorSuccess)
        {
            return Inconclusive(
                $"无法检查目标主机的现有网络会话（Win32 错误 {queryError}），将继续由 WMI/DCOM 返回最终结果。");
        }
        if (hasConnection)
        {
            return Inconclusive(
                "检测到与目标主机已有网络会话；为避免中断现有连接，未执行密码预验证，将继续由 WMI/DCOM 返回最终结果。");
        }

        string remoteName = $@"\\{address}\IPC$";
        int error = _networkApi.AddTemporaryConnection(remoteName, userName, password);
        if (error == ErrorSuccess)
        {
            int cleanupError = _networkApi.CancelConnection(remoteName);
            return cleanupError is ErrorSuccess or ErrorNotConnected
                ? new(ExplicitCredentialValidationStatus.Valid, "显式凭据验证通过。")
                : new(
                    ExplicitCredentialValidationStatus.Valid,
                    $"显式凭据验证通过；临时验证连接清理返回 Win32 错误 {cleanupError}。");
        }

        return error switch
        {
            ErrorInvalidPassword or ErrorLogonFailure or ErrorBadUsername => Invalid(
                "显式凭据的用户名或密码错误。请编辑主机配置并重新输入密码。"),
            ErrorAccountLockedOut => Invalid("显式凭据对应的账户已锁定。请先在目标主机解锁账户。"),
            ErrorPasswordExpired => Invalid("显式凭据对应的密码已过期。请先在目标主机更新密码。"),
            ErrorAccountDisabled => Invalid("显式凭据对应的账户已禁用。请在目标主机选择可用账户。"),
            ErrorAccountExpired => Invalid("显式凭据对应的账户已过期。请在目标主机选择可用账户。"),
            ErrorAccountRestriction or ErrorLogonTypeNotGranted => Invalid(
                "目标主机拒绝此账户进行网络登录。请检查账户登录限制和用户权限分配。"),
            _ => Inconclusive(
                $"无法通过 Windows 网络凭据服务确认用户名和密码（Win32 错误 {error}），将继续由 WMI/DCOM 返回最终结果。")
        };
    }

    private static ExplicitCredentialValidationResult Invalid(string explanation) =>
        new(ExplicitCredentialValidationStatus.Invalid, explanation);

    private static ExplicitCredentialValidationResult Inconclusive(string explanation) =>
        new(ExplicitCredentialValidationStatus.Inconclusive, explanation);
}

internal interface IWindowsNetworkCredentialApi
{
    Task<bool> IsSmbAvailableAsync(string server, TimeSpan timeout, CancellationToken cancellationToken);
    int HasConnectionToServer(string server, out bool hasConnection);
    int AddTemporaryConnection(string remoteName, string userName, string password);
    int CancelConnection(string remoteName);
}

internal sealed class WindowsNetworkCredentialApi : IWindowsNetworkCredentialApi
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int MaxPreferredLength = -1;
    private const int ResourceTypeDisk = 1;
    private const int ConnectTemporary = 0x00000004;

    public async Task<bool> IsSmbAvailableAsync(
        string server,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(server, 445, cancellationToken).AsTask()
                .WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public int HasConnectionToServer(string server, out bool hasConnection)
    {
        hasConnection = false;
        int resumeHandle = 0;
        string prefix = $@"\\{server}\";
        do
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int result = NetUseEnum(
                    null,
                    0,
                    out buffer,
                    MaxPreferredLength,
                    out int entriesRead,
                    out _,
                    ref resumeHandle);
                if (result is not ErrorSuccess and not ErrorMoreData) return result;

                int size = Marshal.SizeOf<UseInfo0>();
                for (int index = 0; index < entriesRead; index++)
                {
                    UseInfo0 entry = Marshal.PtrToStructure<UseInfo0>(buffer + index * size);
                    if (entry.Remote is not null
                        && entry.Remote.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        hasConnection = true;
                        return ErrorSuccess;
                    }
                }
                if (result == ErrorSuccess) return ErrorSuccess;
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
        }
        while (true);
    }

    public int AddTemporaryConnection(string remoteName, string userName, string password)
    {
        var resource = new NetResource
        {
            Scope = 0,
            Type = ResourceTypeDisk,
            DisplayType = 0,
            Usage = 0,
            LocalName = null,
            RemoteName = remoteName,
            Comment = null,
            Provider = null
        };
        return WNetAddConnection2(ref resource, password, userName, ConnectTemporary);
    }

    public int CancelConnection(string remoteName) =>
        WNetCancelConnection2(remoteName, 0, force: false);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UseInfo0
    {
        public string? Local;
        public string? Remote;
    }

    [DllImport("mpr.dll", EntryPoint = "WNetAddConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(
        ref NetResource netResource,
        string password,
        string userName,
        int flags);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUseEnum(
        string? uncServerName,
        int level,
        out IntPtr buffer,
        int preferredMaximumLength,
        out int entriesRead,
        out int totalEntries,
        ref int resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
