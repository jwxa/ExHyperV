using System.Management;
using System.Runtime.InteropServices;

namespace ExHyperV.Services.Remote.Diagnostics;

public sealed class WindowsWmiDcomProbe(TimeSpan? timeout = null) : IWmiDcomProbe
{
    public const string HyperVNamespace = @"root\virtualization\v2";
    private const int ErrorInvalidPassword = 86;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorAccountLockedOut = 1909;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(8);

    public async Task ProbeAsync(
        string address,
        ResolvedHostIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => QueryHyperVNamespace(address, identity), CancellationToken.None)
                .WaitAsync(_timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.Timeout,
                $"WMI/DCOM 查询 {HyperVNamespace} 超时，请检查 DCOM、防火墙和网络配置。",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw AuthenticationFailure(ex, identity);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            throw AuthenticationFailure(ex, identity);
        }
        catch (ManagementException ex) when (
            ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.NotFound)
        {
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.NamespaceUnavailable,
                $"WMI/DCOM 已响应，但无法查询 {HyperVNamespace}。请确认 Hyper-V 角色已安装。",
                ex);
        }
        catch (ManagementException ex)
        {
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.NetworkError,
                $"WMI/DCOM 查询 {HyperVNamespace} 失败：{ex.ErrorCode}。",
                ex);
        }
        catch (COMException ex) when (!identity.UsesCurrentWindowsIdentity && TryGetCredentialError(ex, out _))
        {
            throw InvalidCredential(ex);
        }
        catch (COMException ex) when ((uint)ex.HResult is 0x80070005 or 0x80041003)
        {
            throw AuthenticationFailure(ex, identity);
        }
        catch (COMException ex)
        {
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.NetworkError,
                $"WMI/DCOM 无法连接到 {address}，错误代码 0x{ex.HResult:X8}。",
                ex);
        }
    }

    private void QueryHyperVNamespace(string address, ResolvedHostIdentity identity)
    {
        var options = new ConnectionOptions
        {
            Authentication = AuthenticationLevel.PacketPrivacy,
            Impersonation = ImpersonationLevel.Impersonate,
            EnablePrivileges = true,
            Timeout = _timeout
        };
        if (!identity.UsesCurrentWindowsIdentity)
        {
            options.Username = identity.UserName;
            options.Password = identity.Password;
        }

        var scope = new ManagementScope($@"\\{address}\{HyperVNamespace}", options);
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT __CLASS FROM Msvm_ComputerSystem"),
            new System.Management.EnumerationOptions { ReturnImmediately = false, Timeout = _timeout });
        using ManagementObjectCollection results = searcher.Get();
        _ = results.Count;
    }

    private static HostDiagnosticException AuthenticationFailure(
        Exception ex,
        ResolvedHostIdentity identity)
    {
        if (!identity.UsesCurrentWindowsIdentity
            && TryGetCredentialError(ex, out int error))
        {
            return error switch
            {
                ErrorInvalidPassword or ErrorLogonFailure => InvalidCredential(ex),
                ErrorAccountLockedOut => new HostDiagnosticException(
                    HostDiagnosticErrorCode.InvalidCredential,
                    "显式凭据对应的账户已锁定。请先在目标主机解锁账户。",
                    ex),
                _ => new HostDiagnosticException(
                    HostDiagnosticErrorCode.AuthenticationFailed,
                    "WMI/DCOM 身份验证失败或当前账户没有 Hyper-V 管理权限。",
                    ex)
            };
        }

        return new HostDiagnosticException(
            HostDiagnosticErrorCode.AuthenticationFailed,
            "WMI/DCOM 身份验证失败或当前账户没有 Hyper-V 管理权限。",
            ex);
    }

    private static HostDiagnosticException InvalidCredential(Exception ex) =>
        new(
            HostDiagnosticErrorCode.InvalidCredential,
            "显式凭据的用户名或密码错误。请编辑主机配置并重新输入密码。",
            ex);

    private static bool TryGetCredentialError(Exception ex, out int error)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            uint hResult = unchecked((uint)current.HResult);
            if ((hResult & 0xFFFF0000u) != 0x80070000u) continue;

            int candidate = (int)(hResult & 0xFFFFu);
            if (candidate is ErrorInvalidPassword or ErrorLogonFailure or ErrorAccountLockedOut)
            {
                error = candidate;
                return true;
            }
        }

        error = 0;
        return false;
    }
}
