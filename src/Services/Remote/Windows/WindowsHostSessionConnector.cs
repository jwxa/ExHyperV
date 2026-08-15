using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Services.Remote.Vms;
using ExHyperV.Tools;

namespace ExHyperV.Services.Remote.Windows;

public sealed class WindowsHostSessionConnector(
    IHostIdentityResolver identityResolver,
    TimeSpan? timeout = null,
    ITcpPortProbe? tcpPortProbe = null) : IHostSessionConnector
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(8);
    private readonly ITcpPortProbe _tcpPortProbe = tcpPortProbe ?? new WindowsTcpPortProbe();

    public async Task<IHostSessionCandidate> ConnectAsync(
        HostSwitchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ManagementChannel != HostChannelState.Available)
            throw new HostSwitchException("最新检测未确认 WMI/DCOM 管理通道可用。" );

        ResolvedHostIdentity identity;
        try
        {
            identity = identityResolver.Resolve(request.Profile, request.TransientCredential);
        }
        catch (HostDiagnosticException ex)
        {
            throw new HostSwitchException(SensitiveDataRedactor.Redact(ex.Message), ex);
        }

        WmiContext context = identity.UsesCurrentWindowsIdentity
            ? WmiContext.RemoteCurrentWindowsIdentity(request.Profile.Address, _timeout)
            : WmiContext.Remote(request.Profile.Address, identity.UserName!, identity.Password!, _timeout);
        Task<System.Management.ManagementScope> connectTask = Task.Run(
            () => WmiConnectionCache.GetManagementScope(WmiScope.HyperV, context),
            CancellationToken.None);
        try
        {
            await connectTask.WaitAsync(_timeout, cancellationToken);
            HostChannelState consoleChannel = request.ConsoleChannel;
            if (request.RevalidateChannels)
            {
                try
                {
                    await _tcpPortProbe.ProbeAsync(
                        request.Profile.Address,
                        HostDiagnosticPipeline.ConsolePort,
                        cancellationToken);
                    consoleChannel = HostChannelState.Available;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    consoleChannel = HostChannelState.Unavailable;
                }
            }
            return new WindowsHostSessionCandidate(
                request.Profile,
                context,
                consoleChannel);
        }
        catch (OperationCanceledException)
        {
            WmiConnectionCache.Clear(context);
            _ = ObserveConnectCompletionAsync(connectTask);
            throw;
        }
        catch (TimeoutException ex)
        {
            WmiConnectionCache.Clear(context);
            _ = ObserveConnectCompletionAsync(connectTask);
            throw new HostSwitchException("连接 WMI/DCOM 超时，现有宿主会话保持不变。", ex);
        }
        catch (Exception ex)
        {
            WmiConnectionCache.Clear(context);
            throw new HostSwitchException("无法建立 WMI/DCOM 管理会话，现有宿主会话保持不变。", ex);
        }
    }

    private static async Task ObserveConnectCompletionAsync(
        Task<System.Management.ManagementScope> connectTask)
    {
        try { await connectTask; }
        catch { }
    }
}

public sealed class WindowsHostManagementConnection(WmiContext context) : IWmiHostManagementConnection
{
    public WmiContext Context { get; } = context ?? throw new ArgumentNullException(nameof(context));
}

internal sealed class WindowsHostSessionCandidate : IHostSessionCandidate
{
    private readonly WmiContext _context;
    private int _disposed;

    public WindowsHostSessionCandidate(
        Profiles.HostProfile profile,
        WmiContext context,
        HostChannelState consoleChannel)
    {
        Target = HostTarget.FromProfile(profile);
        _context = context;
        ManagementConnection = new WindowsHostManagementConnection(context);
        ConsoleChannel = consoleChannel;
    }

    public HostTarget Target { get; }
    public IHostManagementConnection ManagementConnection { get; }
    public HostChannelState ManagementChannel => HostChannelState.Available;
    public HostChannelState ConsoleChannel { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            WmiConnectionCache.Clear(_context);
        return ValueTask.CompletedTask;
    }
}
