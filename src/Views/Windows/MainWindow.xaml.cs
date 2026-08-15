using System.Windows;
using ExHyperV.Services;
using ExHyperV.Services.Logging;
using ExHyperV.Services.Remote.Sessions;
using ExHyperV.Views;
using Wpf.Ui.Controls;

namespace ExHyperV.Views
{
    public partial class MainWindow : FluentWindow
    {
        public MainWindow()
        {
            InitializeComponent();
            AppLog.BecameUnavailable += OnLoggingBecameUnavailable;
            Closed += (_, _) =>
            {
                AppLog.BecameUnavailable -= OnLoggingBecameUnavailable;
            };
            ApplyCapabilities(HostSessions.Registry.Current.GetRequired(HostId.Local).Capabilities);
            if (App.PerformanceMode)
            {
                // 无 GPU 模式：关 Mica（省 DWM 合成），改不透明底
                WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
                SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
                // 关页面切换过渡（默认 FadeInWithSlide 的下往上滑入，RDP/软渲染下卡）
                RootNavigation.Transition = Wpf.Ui.Animations.Transition.None;
            }
            Loaded += PagePreload;
            Loaded += ShowLoggingFailureIfNeeded;

            //仅按保存偏好上色;系统主题监听延到 PagePreload 后挂,避开 #146 启动渲染竞争
            SettingsService.ApplySavedTheme();
        }

        private void ShowLoggingFailureIfNeeded(object sender, RoutedEventArgs e)
        {
            Loaded -= ShowLoggingFailureIfNeeded;
            if (AppLog.IsAvailable || string.IsNullOrWhiteSpace(AppLog.UnavailableReason)) return;

            ShowLoggingFailure(AppLog.UnavailableReason);
        }

        private void OnLoggingBecameUnavailable(string reason) =>
            Dispatcher.InvokeAsync(() => ShowLoggingFailure(reason));

        private static void ShowLoggingFailure(string reason)
        {
            bool chinese = System.Globalization.CultureInfo.CurrentUICulture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);
            Interaction.Notifications.ShowSnackbar(
                chinese ? "日志不可用" : "Logging unavailable",
                chinese ? reason : $"ExHyperV cannot write logs. {reason}",
                ControlAppearance.Caution,
                SymbolRegular.Warning24);
        }

        private void PagePreload(object sender, RoutedEventArgs e)
        {
            // 性能模式：不预加载其它页面（省启动内存），只落地首页；其余首次进入时才建
            if (!App.PerformanceMode)
            {
                RootNavigation.Navigate(typeof(PCIePage));
                RootNavigation.Navigate(typeof(HostPage));
                RootNavigation.Navigate(typeof(SwitchPage));
                RootNavigation.Navigate(typeof(VirtualMachinesPage));
                RootNavigation.Navigate(typeof(USBPage));
            }
            RootNavigation.Navigate(typeof(MainPage));

            //预加载/首帧后再挂系统主题监听(跟随模式),与 #146 的 Loaded 渲染竞争错开
            Dispatcher.BeginInvoke(
                new Action(() => SettingsService.EnableSystemThemeWatch(this)),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ApplyCapabilities(HostCapabilityMatrix capabilities)
        {
            ApplyNavigationCapability(VmNavigationItem, capabilities[HostCapabilityKind.VmRead]);
            ApplyNavigationCapability(HostNavigationItem, capabilities[HostCapabilityKind.HostHardware]);
            ApplyNavigationCapability(PcieNavigationItem, capabilities[HostCapabilityKind.PcieDevices]);
            ApplyNavigationCapability(SwitchNavigationItem, capabilities[HostCapabilityKind.VirtualSwitch]);
            ApplyNavigationCapability(UsbNavigationItem, capabilities[HostCapabilityKind.UsbPassthrough]);
        }

        private static void ApplyNavigationCapability(
            NavigationViewItem item,
            HostCapability capability)
        {
            item.IsEnabled = capability.CanView;
            item.ToolTip = capability.CanExecute ? null : capability.Reason;
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(item, true);
        }




    }
}
