using System.Net.Sockets;

namespace ExHyperV.Services.Remote.Diagnostics;

public sealed class WindowsTcpPortProbe(TimeSpan? timeout = null) : ITcpPortProbe
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(3);

    public async Task ProbeAsync(string address, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(address, port, cancellationToken).AsTask().WaitAsync(_timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.Timeout,
                $"TCP {port} 连接超时，请检查远程主机防火墙入站规则。",
                ex);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.ConnectionRefused,
                $"TCP {port} 连接被拒绝，请确认 Hyper-V 控制台服务和防火墙规则。",
                ex);
        }
        catch (SocketException ex)
        {
            throw new HostDiagnosticException(
                HostDiagnosticErrorCode.NetworkError,
                $"TCP {port} 无法连接：{ex.SocketErrorCode}。",
                ex);
        }
    }
}
