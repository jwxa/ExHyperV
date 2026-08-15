using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
using ExHyperV.Services;
using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.ViewModels
{
    public partial class USBPageViewModel : PageViewModelBase
    {
        // ===== 字段 =====

        private readonly IActiveHostSessionCoordinator _hostCoordinator = ActiveHostSessions.Current;
        private CancellationTokenSource? _localWorkCts;

        // ===== 绑定属性与命令 =====

        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private bool _isUiEnabled = true;

        public ObservableCollection<UsbDeviceViewModel> Devices { get; } = new();

        // ===== 构造 =====

        public USBPageViewModel()
        {
            _hostCoordinator.StateChanged += OnActiveHostStateChanged;
            ApplyCapabilities(_hostCoordinator.Current.Capabilities);
        }

        // ===== 业务方法 =====

        [RelayCommand]
        private async Task LoadDataAsync()
        {
            if (!EnsureHostCapability(HostCapabilityKind.UsbPassthrough)) return;
            IsLoading = true;
            try
            {
                if (TryBeginHostWrite(HostCapabilityKind.UsbPassthrough, out IHostWriteLease? writeLease))
                {
                    using (writeLease)
                        UsbVmbusService.EnsureServiceRegistered();
                }
                await RefreshListInternal();
            }
            finally { IsLoading = false; }
        }

        private async Task SyncDevicesLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(3000, ct);
                    await App.Current.Dispatcher.InvokeAsync(RefreshListInternal);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        private async Task RefreshListInternal()
        {
            if (!HasHostCapability(HostCapabilityKind.UsbPassthrough)) return;
            var vms = await UsbVmbusService.GetRunningVMsAsync();
            var usbDevices = await UsbVmbusService.GetUsbIpDevicesAsync();
            var vmNames = vms.Select(v => v.Name).ToList();

            // 增量更新 UI 列表
            var newBusIds = usbDevices.Select(d => d.BusId).ToList();

            for (int i = Devices.Count - 1; i >= 0; i--)
            {
                if (!newBusIds.Contains(Devices[i].BusId)) Devices.RemoveAt(i);
            }

            foreach (var dev in usbDevices)
            {
                var existing = Devices.FirstOrDefault(d => d.BusId == dev.BusId);
                if (existing != null)
                {
                    // 更新描述和 VID/PID (处理手机变身)
                    existing.Description = dev.Description;
                    existing.VidPid = dev.VidPid;
                    existing.UpdateOptions(vmNames);
                }
                else
                {
                    Devices.Add(new UsbDeviceViewModel(dev, vmNames));
                }
            }

            // 同步连接状态显示
            foreach (var d in Devices)
            {
                if (UsbVmbusService.ActiveTunnels.TryGetValue(d.BusId, out string vm)) d.CurrentAssignment = vm;
                else if (d.CurrentAssignment != Properties.Resources.USBPageViewModel_Connecting) d.CurrentAssignment = Properties.Resources.UsbDevice_Host;
            }
        }

        [RelayCommand]
        private async Task ChangeAssignmentAsync(object parameter)
        {
            if (!EnsureHostCapability(HostCapabilityKind.UsbPassthrough)) return;
            if (parameter is not object[] parameters || parameters.Length < 2 ||
                parameters[0] is not UsbDeviceViewModel deviceVM ||
                parameters[1] is not string selectedTarget) return;

            if (deviceVM.CurrentAssignment == selectedTarget) return;

            IsUiEnabled = false;
            try
            {
                if (selectedTarget == Properties.Resources.UsbDevice_Host)
                {
                    if (!TryBeginHostWrite(HostCapabilityKind.UsbPassthrough, out IHostWriteLease? writeLease)) return;
                    using (writeLease)
                    {
                        UsbVmbusService.ActiveTunnels.TryRemove(deviceVM.BusId, out _);
                        await UsbVmbusService.StopTunnelAsync(deviceVM.BusId); // 使用 Await 版本
                    }
                    deviceVM.CurrentAssignment = Properties.Resources.UsbDevice_Host;
                }
                else
                {
                    if (!TryBeginHostWrite(HostCapabilityKind.UsbPassthrough, out IHostWriteLease? writeLease)) return;
                    // 1. 先记录意图
                    UsbVmbusService.ActiveTunnels[deviceVM.BusId] = selectedTarget;
                    deviceVM.CurrentAssignment = Properties.Resources.USBPageViewModel_Connecting;

                    // 2. 异步执行切换，内部会处理 Stop 旧隧道 -> Start 新隧道
                    _ = Task.Run(async () => {
                        using (writeLease)
                            await UsbVmbusService.AutoRecoverTunnel(deviceVM.BusId, selectedTarget);
                    });
                }
            }
            finally { IsUiEnabled = true; }
        }
        /// <summary>
        /// 跳转到指定的 URL 网页
        /// </summary>
        [RelayCommand]
        private void OpenUrl(string url) => Shell.OpenUrl(url);

        private void OnActiveHostStateChanged(object? sender, ActiveHostStateChangedEventArgs e) =>
            App.Current.Dispatcher.InvokeAsync(() => ApplyCapabilities(e.Current.Capabilities));

        private void ApplyCapabilities(HostCapabilityMatrix capabilities)
        {
            if (capabilities[HostCapabilityKind.UsbPassthrough].CanExecute)
                StartLocalWork();
            else
                StopLocalWork();
        }

        private void StartLocalWork()
        {
            if (_localWorkCts is not null) return;
            _localWorkCts = new CancellationTokenSource();
            CancellationToken token = _localWorkCts.Token;
            LoadDataCommand.Execute(null);
            _ = Task.Run(() => UsbVmbusService.WatchdogLoopAsync(token));
            _ = Task.Run(() => SyncDevicesLoopAsync(token));
        }

        private void StopLocalWork()
        {
            CancellationTokenSource? cancellation = Interlocked.Exchange(ref _localWorkCts, null);
            cancellation?.Cancel();
            cancellation?.Dispose();
        }
    }
}
