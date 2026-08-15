using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ExHyperV.Services.Remote.Diagnostics;

public sealed class WindowsIpv4ReachabilityProbe(TimeSpan? timeout = null) : IIpv4ReachabilityProbe
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(2);

    public async Task ProbeAsync(string address, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(address, out IPAddress? ipAddress)
            || ipAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.InvalidIpv4,
                "目标地址不是有效的 IPv4 地址。");
        }

        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(ipAddress, _timeout).WaitAsync(cancellationToken);
            if (reply.Status != IPStatus.Success)
            {
                throw new HostDiagnosticException(
                    HostDiagnosticErrorCode.Unreachable,
                    $"IPv4 主机未响应 ICMP，状态：{reply.Status}。后续通道检测仍会继续。");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HostDiagnosticException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.Timeout,
                "IPv4 可达性检测超时。后续通道检测仍会继续。",
                ex);
        }
        catch (PingException ex)
        {
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.NetworkError,
                "无法完成 IPv4 可达性检测。后续通道检测仍会继续。",
                ex);
        }
    }
}
