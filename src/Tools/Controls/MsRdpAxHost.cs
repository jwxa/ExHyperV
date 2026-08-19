using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ExHyperV.Services.Logging;
using MSTSCLib;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 直接托管系统 mstscax.dll 的 RDP ActiveX（CLSID = MsRdpClient9NotSafeForScripting），
    /// 不经 RoyalApps、不经 aximp 生成的 AxInterop——自己派生 <see cref="AxHost"/>。
    /// 标量属性走 IDispatch 晚绑定（dynamic），非脚本属性与事件走类型化 COM 接口。
    /// </summary>
    internal sealed class MsRdpAxHost : AxHost
    {
        private const string MsRdpClient9Clsid = "8b918b82-7985-4c24-89df-c33ad2bbfbcd";
        private bool _smartSizing;   // 当前 SmartSizing 状态缓存（SetSmartSizing 用，避免重复设值闪烁）
        private uint _zoomLevel;     // 当前 ZoomLevel% 缓存（SetZoomLevel 用，基本会话每次布局都会调，仅比例真变时才穿透 OCX）
        private bool _startFullScreenOnConnect;
        private int _connectionGeneration;
        private bool _useAllMonitors;
        private string _server = string.Empty;
        private int _lastRemoteWidth;
        private int _lastRemoteHeight;

        public event Action? Connected;
        public event Action? LoginCompleted;
        public event Action<int>? Disconnected;
        public event Action<int, int>? RemoteDesktopSizeChanged;
        public event Action? EnteredFullScreen;
        public event Action? LeftFullScreen;
        public event Action? FullScreenStartFailed;
        public event Action<int>? FatalError;
        public event Action? MinimizeRequested;   // 连接栏最小化按钮（容器处理全屏）
        public event Action? CloseRequested;       // 连接栏关闭按钮（容器处理全屏）

        public MsRdpAxHost() : base(MsRdpClient9Clsid) { }

        // AxHost 在底层 OCX 创建完成后调用此处——是订阅 COM 事件的规范时机。
        protected override void AttachInterfaces()
        {
            try
            {
                var evt = (IMsTscAxEvents_Event)GetOcx();
                // 每个处理都过 Safe()——COM 事件 sink 绝不能让异常逃回 native，否则 0xC000041D 进程秒退。
                evt.OnConnected += () => Safe(() =>
                {
                    ReportMultiMonitorState("RDP 传输连接完成");
                    // FullScreen 只能在控件已连接后设置；延迟到本次 COM 回调返回，
                    // 避免在 OnConnected 的 native 事件栈内切换顶层窗口。
                    if (_startFullScreenOnConnect)
                    {
                        int generation = _connectionGeneration;
                        BeginInvoke(new Action(() => StartFullScreenIfCurrent(generation)));
                    }
                    Connected?.Invoke();
                });
                evt.OnLoginComplete += () => Safe(() =>
                {
                    ReportMultiMonitorState("RDP 登录完成");
                    LoginCompleted?.Invoke();
                });
                evt.OnDisconnected += reason => Safe(() =>
                {
                    unchecked { _connectionGeneration++; }
                    _lastRemoteWidth = _lastRemoteHeight = 0;
                    Disconnected?.Invoke(reason);
                });
                evt.OnRemoteDesktopSizeChange += (w, h) => Safe(() =>
                {
                    ReportMultiMonitorRemoteSize(w, h);
                    RemoteDesktopSizeChanged?.Invoke(w, h);
                });
                // 容器处理全屏：热键/请求经 OnRequestGo/LeaveFullScreen（非 OnEnter/Leave，那是控件自身全屏才触发）
                evt.OnRequestGoFullScreen += () => Safe(() =>
                {
                    LogFullScreenRequest(true);
                    EnteredFullScreen?.Invoke();
                });
                evt.OnRequestLeaveFullScreen += () => Safe(() =>
                {
                    LogFullScreenRequest(false);
                    LeftFullScreen?.Invoke();
                });
                evt.OnFatalError += code => Safe(() => FatalError?.Invoke(code));
                // 容器处理全屏下，连接栏的最小化/关闭按钮经事件交给容器（窗口）处理
                evt.OnRequestContainerMinimize += () => Safe(() => MinimizeRequested?.Invoke());
                evt.OnConfirmClose += () => { Safe(() => CloseRequested?.Invoke()); return true; };  // 返回值=允许关闭
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Rdp] AttachInterfaces 失败: " + ex);
            }
        }

        /// <summary>应用连接参数并启动连接；返回 false 表示 ActiveX 在启动阶段同步拒绝了请求。</summary>
        public bool ApplyAndConnect(RdpConnectionSettings s)
        {
            try
            {
                unchecked { _connectionGeneration++; }
                _useAllMonitors = s.UseAllMonitors;
                _server = s.Server;
                _lastRemoteWidth = _lastRemoteHeight = 0;
                dynamic rdp = GetOcx();
                rdp.Server = s.Server;

                // UI 父窗口句柄：控件弹出的子窗口需要有效父窗口，否则在框架回调里抛异常逃回 native → 0xC000041D。
                // COMReference(tlbimp) 把它生成成 set_UIParentWindowHandle(ref _RemotableHandle/wireHWND)，需手填：
                //   fContext = WDT_INPROC_CALL(0x48746457)，hInproc = HWND 低 32 位（USER 句柄恒在 32 位内）。
                TrySet("UIParentWindowHandle", () =>
                {
                    var h = new _RemotableHandle { fContext = 0x48746457 };
                    h.u.hInproc = GetAncestor(this.Handle, GA_ROOT).ToInt32();
                    ((IMsRdpClientNonScriptable3)GetOcx()).set_UIParentWindowHandle(ref h);
                });

                dynamic adv = rdp.AdvancedSettings9;
                adv.RDPPort = s.Port;
                adv.AuthenticationLevel = s.AuthenticationLevel;
                if (!string.IsNullOrEmpty(s.AuthenticationServiceClass))
                    adv.AuthenticationServiceClass = s.AuthenticationServiceClass;

                // CredSSP 与 NegotiateSecurityLayer 必须在同一个 NonScriptable3 上、先开 CredSSP 再关协商
                // （官方 VMConnect 示例的顺序；分到不同接口设会让 NegotiateSecurityLayer 报 E_INVALIDARG）。
                var ocx = (IMsRdpClientNonScriptable3)GetOcx();
                if (!string.IsNullOrWhiteSpace(s.ConnectionBarText))
                    TrySet("ConnectionBarText", () => ocx.ConnectionBarText = s.ConnectionBarText);
                TrySet("EnableCredSspSupport", () => ocx.EnableCredSspSupport = s.NetworkLevelAuthentication);
                TrySet("NegotiateSecurityLayer", () => ocx.NegotiateSecurityLayer = s.NegotiateSecurityLayer);

                // DisableCredentialsDelegation 非强类型属性，经 IMsRdpExtendedSettings 字符串属性包设置——
                // 避免 reason=3848（凭据委派被拒），也是 stock typelib 查不到同名属性的原因。
                if (s.DisableCredentialsDelegation)
                    TrySet("DisableCredentialsDelegation", () =>
                    {
                        var ext = (IMsRdpExtendedSettings)GetOcx();
                        object on = true;
                        ext.set_Property("DisableCredentialsDelegation", ref on);
                    });

                // 初始缩放必须在连接前设置，动态显示接口无法影响首次显示的登录界面。
                if (s.DesktopScaleFactor is >= 100 and <= 500)
                    TrySet("DesktopScaleFactor", () =>
                    {
                        var ext = (IMsRdpExtendedSettings)GetOcx();
                        object value = s.DesktopScaleFactor;
                        ext.set_Property("DesktopScaleFactor", ref value);
                    });
                if (s.DeviceScaleFactor is 100 or 140 or 180)
                    TrySet("DeviceScaleFactor", () =>
                    {
                        var ext = (IMsRdpExtendedSettings)GetOcx();
                        object value = s.DeviceScaleFactor;
                        ext.set_Property("DeviceScaleFactor", ref value);
                    });

                // 初值原生不缩放：连上即清晰；之后由 SetSmartSizing 按"画面是否超出画面区"动态开关
                // （装得下原生清晰、超出才缩放铺满）。控件宽高比始终=画面宽高比，故缩放无 #CBCBCB 信箱、鼠标映射准。
                TrySet("SmartSizing", () => adv.SmartSizing = false);
                _smartSizing = false;
                TrySet("EnableAutoReconnect", () => adv.EnableAutoReconnect = true);
                // VMBus 无真实网络：关掉带宽/网络探测，避免连接栏"网络信息"弹窗取退化数据而原生崩溃。
                TrySet("BandwidthDetection", () => adv.BandwidthDetection = false);
                // 连接超时调短：localhost VMBus 正常连接 <1s，调短让连不上的会话(如不支持增强)快速放弃 → 快速回退。
                if (s.ConnectionTimeoutSeconds > 0)
                {
                    TrySet("singleConnectionTimeout", () => adv.singleConnectionTimeout = s.ConnectionTimeoutSeconds);
                    TrySet("overallConnectionTimeout", () => adv.overallConnectionTimeout = s.ConnectionTimeoutSeconds);
                }
                // UseMultimon 负责协议层的监视器拓扑；WPF 容器负责把唯一的 ActiveX 表面
                // 精确铺到本机虚拟桌面。WindowsFormsHost 下依赖 mstscax 自建原生全屏窗口
                // 会留下嵌入窗口，最终表现为单屏里的超宽桌面和横向滚动条。
                var nonScriptable5 = (IMsRdpClientNonScriptable5)GetOcx();
                bool useMultimonSet = TrySet("UseMultimon", () =>
                    nonScriptable5.UseMultimon = s.UseAllMonitors);
                bool useMultimonApplied = TryGet("UseMultimon", () => nonScriptable5.UseMultimon, false);
                bool containerHandledFullScreenSet = TrySet(
                    "ContainerHandledFullScreen",
                    () => adv.ContainerHandledFullScreen = 1);
                int containerHandledFullScreenValue = TryGet(
                    "ContainerHandledFullScreen",
                    () => (int)adv.ContainerHandledFullScreen,
                    0);
                bool containerHandledFullScreenApplied = containerHandledFullScreenValue != 0;
                // mstscax 没有受支持的原生连接栏扩展接口；多屏模式统一显示并固定原生连接栏。
                bool nativeConnectionBarEnabled = TrySet(
                    "DisableConnectionBar",
                    () => nonScriptable5.DisableConnectionBar = false);
                bool displayConnectionBarSet = TrySet(
                    "DisplayConnectionBar",
                    () => adv.DisplayConnectionBar = true);
                bool displayConnectionBarValue = TryGet(
                    "DisplayConnectionBar",
                    () => (bool)adv.DisplayConnectionBar,
                    false);
                bool displayConnectionBarApplied = displayConnectionBarValue;
                bool pinConnectionBarSet = TrySet(
                    "PinConnectionBar",
                    () => adv.PinConnectionBar = true);
                bool pinConnectionBarValue = TryGet(
                    "PinConnectionBar",
                    () => (bool)adv.PinConnectionBar,
                    false);
                bool pinConnectionBarApplied = pinConnectionBarValue;
                bool showPinButtonSet = TrySet(
                    "ConnectionBarShowPinButton",
                    () => adv.ConnectionBarShowPinButton = true);
                bool showPinButtonValue = TryGet(
                    "ConnectionBarShowPinButton",
                    () => (bool)adv.ConnectionBarShowPinButton,
                    false);
                bool showPinButtonApplied = showPinButtonValue;

                if (s.UseAllMonitors)
                {
                    var virtualScreen = SystemInformation.VirtualScreen;
                    var properties = new Dictionary<string, object?>
                    {
                        ["Server"] = s.Server,
                        ["UseMultimonSet"] = useMultimonSet,
                        ["UseMultimonApplied"] = useMultimonApplied,
                        ["ContainerHandledFullScreenSet"] = containerHandledFullScreenSet,
                        ["ContainerHandledFullScreenApplied"] = containerHandledFullScreenApplied,
                        ["ContainerHandledFullScreen"] = containerHandledFullScreenValue,
                        ["NativeConnectionBarEnabled"] = nativeConnectionBarEnabled,
                        ["DisplayConnectionBarSet"] = displayConnectionBarSet,
                        ["DisplayConnectionBarApplied"] = displayConnectionBarApplied,
                        ["DisplayConnectionBar"] = displayConnectionBarValue,
                        ["PinConnectionBarSet"] = pinConnectionBarSet,
                        ["PinConnectionBarApplied"] = pinConnectionBarApplied,
                        ["PinConnectionBar"] = pinConnectionBarValue,
                        ["ConnectionBarShowPinButtonSet"] = showPinButtonSet,
                        ["ConnectionBarShowPinButtonApplied"] = showPinButtonApplied,
                        ["ConnectionBarShowPinButton"] = showPinButtonValue,
                        ["LocalMonitorCount"] = Screen.AllScreens.Length,
                        ["LocalVirtualBounds"] = FormatBounds(
                            virtualScreen.Left,
                            virtualScreen.Top,
                            virtualScreen.Right,
                            virtualScreen.Bottom),
                        ["RequestedDesktop"] = $"{s.DesktopWidth}x{s.DesktopHeight}",
                        ["NativeConnectionBarRequested"] = true,
                    };
                    AppLog.Information("RDP 控制台", "正在建立多显示器会话。",
                        new AppLogContext(Properties: properties));
                    if (!containerHandledFullScreenSet || !containerHandledFullScreenApplied)
                    {
                        AppLog.Error("RDP 控制台",
                            "mstscax 未接受 ContainerHandledFullScreen，已阻止启动多显示器会话。",
                            new AppLogContext(Properties: properties));
                        _startFullScreenOnConnect = false;
                        return false;
                    }
                    if (!nativeConnectionBarEnabled
                        || !displayConnectionBarSet
                        || !displayConnectionBarApplied
                        || !pinConnectionBarSet
                        || !pinConnectionBarApplied
                        || !showPinButtonSet
                        || !showPinButtonApplied)
                    {
                        AppLog.Error("RDP 控制台",
                            "mstscax 未接受显示并固定原生连接栏，已阻止启动多显示器会话。",
                            new AppLogContext(Properties: properties));
                        _startFullScreenOnConnect = false;
                        return false;
                    }
                    if (!useMultimonSet || !useMultimonApplied)
                    {
                        AppLog.Error("RDP 控制台", "mstscax 未接受 UseMultimon，多显示器会话无法按预期工作。",
                            new AppLogContext(Properties: properties));
                        _startFullScreenOnConnect = false;
                        return false;
                    }
                }
                _startFullScreenOnConnect = s.UseAllMonitors;

                // HotKeyFullScreen=可配置 vkey → Ctrl+Alt+<key>；KeyboardHookMode=1 → Win/Alt+Tab 等组合键只要画面有焦点就送 VM（窗口化也送，不止全屏；要切回宿主先点一下别处）。
                TrySet("HotKeyFullScreen", () => adv.HotKeyFullScreen = s.FullScreenHotKeyVirtualKey);
                TrySet("KeyboardHookMode", () => rdp.SecuredSettings.KeyboardHookMode = 1);

                adv.PCB = s.PreConnectionBlob ?? string.Empty;

                if (s.DesktopWidth > 0 && s.DesktopHeight > 0)
                    TrySet("Desktop", () => { rdp.DesktopWidth = s.DesktopWidth; rdp.DesktopHeight = s.DesktopHeight; });

                rdp.Connect();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Rdp] ApplyAndConnect 异常: " + ex);
                AppLog.Error("RDP 控制台", "应用 RDP 连接参数失败。",
                    new AppLogContext(Properties: new Dictionary<string, object?>
                    {
                        ["Server"] = s.Server,
                        ["UseAllMonitors"] = s.UseAllMonitors,
                    }), ex);
                return false;
            }
        }

        public void DisconnectSafe()
        {
            // 先使 OnConnected 已排队的全屏回调失效；ShutdownAndDispose 会泵消息，
            // 不能让旧回调在拆除 ActiveX 期间重新发起容器全屏请求。
            _startFullScreenOnConnect = false;
            unchecked { _connectionGeneration++; }
            try
            {
                dynamic rdp = GetOcx();
                if (_useAllMonitors && (int)rdp.Connected != 0 && (bool)rdp.FullScreen)
                    SetFullScreen(false);
                if ((int)rdp.Connected != 0) rdp.Disconnect();
            }
            catch { /* 未连接 / OCX 未就绪 */ }
        }

        /// <summary>0=未连接 1=已连接 2=连接中（mstscax 的 Connected 取值）。</summary>
        public int ConnectionState
        {
            get { try { dynamic rdp = GetOcx(); return (int)rdp.Connected; } catch { return 0; } }
        }

        /// <summary>底层 mstscax 当前确认的全屏状态；读取失败按未进入全屏处理。</summary>
        public bool IsFullScreen
        {
            get { try { return ((IMsRdpClient9)GetOcx()).FullScreen; } catch { return false; } }
        }

        /// <summary>同步控件全屏状态（容器处理全屏下，按钮发起的全屏需回灌，使 mstscax 内部状态/键盘捕获与窗口一致）。</summary>
        public bool SetFullScreen(bool fullScreen)
        {
            try
            {
                var rdp = (IMsRdpClient9)GetOcx();
                rdp.FullScreen = fullScreen;
                bool applied = rdp.FullScreen == fullScreen;
                if (_useAllMonitors && !applied)
                    AppLog.Warning("RDP 控制台", "mstscax 未确认全屏状态变更。",
                        MultiMonitorLogContext(new Dictionary<string, object?>
                        {
                            ["RequestedFullScreen"] = fullScreen,
                            ["AppliedFullScreen"] = rdp.FullScreen,
                        }));
                return applied;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Rdp] SetFullScreen 失败: " + ex.Message);
                if (_useAllMonitors)
                    AppLog.Warning("RDP 控制台", "切换多显示器全屏失败。",
                        MultiMonitorLogContext(new Dictionary<string, object?>
                        {
                            ["RequestedFullScreen"] = fullScreen,
                        }), ex);
                return false;
            }
        }

        private void StartFullScreenIfCurrent(int generation)
        {
            if (generation != _connectionGeneration
                || !_startFullScreenOnConnect
                || IsDisposed
                || Disposing
                || !IsHandleCreated
                || ConnectionState != 1)
                return;

            if (SetFullScreen(true))
                ReportMultiMonitorState("已请求容器多显示器全屏");
            else
                FullScreenStartFailed?.Invoke();
        }

        private void ReportMultiMonitorRemoteSize(int width, int height)
        {
            if (!_useAllMonitors || (width == _lastRemoteWidth && height == _lastRemoteHeight)) return;
            _lastRemoteWidth = width;
            _lastRemoteHeight = height;
            AppLog.Information("RDP 控制台", "远端桌面尺寸已变化。",
                MultiMonitorLogContext(new Dictionary<string, object?>
                {
                    ["RemoteDesktop"] = $"{width}x{height}",
                }));
        }

        internal void ReportMultiMonitorState(string stage)
        {
            if (!_useAllMonitors) return;
            try
            {
                var nonScriptable5 = (IMsRdpClientNonScriptable5)GetOcx();
                var rdp = (IMsRdpClient9)GetOcx();
                int left = 0, top = 0, right = 0, bottom = 0;
                nonScriptable5.GetRemoteMonitorsBoundingBox(out left, out top, out right, out bottom);
                var virtualScreen = SystemInformation.VirtualScreen;
                string controlScreenBounds = "unavailable";
                try
                {
                    var control = RectangleToScreen(ClientRectangle);
                    controlScreenBounds = FormatBounds(
                        control.Left,
                        control.Top,
                        control.Right,
                        control.Bottom);
                }
                catch
                {
                    // 控件句柄可能正在重建；协商信息仍然有价值。
                }
                AppLog.Information("RDP 控制台", stage,
                    MultiMonitorLogContext(new Dictionary<string, object?>
                    {
                        ["UseMultimon"] = nonScriptable5.UseMultimon,
                        ["RemoteMonitorCount"] = nonScriptable5.RemoteMonitorCount,
                        ["RemoteMonitorLayoutMatchesLocal"] = nonScriptable5.RemoteMonitorLayoutMatchesLocal,
                        ["RemoteMonitorBounds"] = FormatBounds(left, top, right, bottom),
                        ["LocalMonitorCount"] = Screen.AllScreens.Length,
                        ["LocalVirtualBounds"] = FormatBounds(
                            virtualScreen.Left,
                            virtualScreen.Top,
                            virtualScreen.Right,
                            virtualScreen.Bottom),
                        ["FullScreen"] = rdp.FullScreen,
                        ["Desktop"] = $"{rdp.DesktopWidth}x{rdp.DesktopHeight}",
                        ["Control"] = $"{ClientSize.Width}x{ClientSize.Height}",
                        ["ControlScreenBounds"] = controlScreenBounds,
                        ["HorizontalScrollBarVisible"] = rdp.HorizontalScrollBarVisible,
                        ["VerticalScrollBarVisible"] = rdp.VerticalScrollBarVisible,
                    }));
            }
            catch (Exception ex)
            {
                AppLog.Warning("RDP 控制台", $"{stage}，但读取多显示器协商结果失败。",
                    MultiMonitorLogContext(), ex);
            }
        }

        private void LogFullScreenRequest(bool enter)
        {
            if (!_useAllMonitors) return;
            AppLog.Information("RDP 控制台", enter ? "mstscax 请求进入多显示器全屏。" : "mstscax 请求退出多显示器全屏。",
                MultiMonitorLogContext(new Dictionary<string, object?>
                {
                    ["Enter"] = enter,
                }));
        }

        private AppLogContext MultiMonitorLogContext(
            IReadOnlyDictionary<string, object?>? properties = null)
        {
            var merged = new Dictionary<string, object?> { ["Server"] = _server };
            if (properties is not null)
            {
                foreach ((string key, object? value) in properties)
                    merged[key] = value;
            }
            return new AppLogContext(Properties: merged);
        }

        private static string FormatBounds(int left, int top, int right, int bottom) =>
            $"({left},{top})-({right},{bottom}) {right - left}x{bottom - top}";

        /// <summary>动态开关 SmartSizing（基本会话用：VM 分辨率超出画面区时开=缩放铺满，否则关=原生 1:1 清晰）。带缓存避免重复设值闪烁。</summary>
        public void SetSmartSizing(bool on)
        {
            if (_smartSizing == on) return;
            _smartSizing = on;
            try { dynamic rdp = GetOcx(); rdp.AdvancedSettings9.SmartSizing = on; }
            catch (Exception ex) { Debug.WriteLine("[Rdp] SetSmartSizing 失败: " + ex.Message); }
        }

        /// <summary>增强会话改分辨率（不重连）。命名避开 Control.Resize 事件（否则 CS0108 隐藏告警）。</summary>
        public void SetResolution(int width, int height, double dpiScale)
        {
            if (width <= 0 || height <= 0) return;
            try
            {
                dynamic rdp = GetOcx();
                // 参数复刻 VMConnect 的 RdpViewerControl：物理尺寸用毫米(非像素)、desktopScaleFactor=显示器 DPI%、
                // deviceScaleFactor=100。末位传 1 是非法值(合法仅 100/140/180)，会让分辨率协商被拒 → 画面不随分辨率刷新+灰信箱。
                // 使用宿主窗口提供的 DPI，避免 AxHost.DeviceDpi 在首次连接时仍为旧值。
                uint dpi = (uint)Math.Max(96, Math.Round(96.0 * dpiScale));
                uint desktopScaleFactor = (uint)Math.Round(dpi / 96.0 * 100.0);
                uint physW = (uint)Math.Round(width * 25.4 / dpi);
                uint physH = (uint)Math.Round(height * 25.4 / dpi);
                rdp.UpdateSessionDisplaySettings((uint)width, (uint)height, physW, physH, 0u, desktopScaleFactor, 100u);
            }
            catch (Exception ex) { Debug.WriteLine("[Rdp] SetResolution 失败: " + ex.Message); }
        }

        /// <summary>基本会话缩放：设 mstscax 原生 ZoomLevel(百分比，如 100/150/200)。经 IMsRdpExtendedSettings 字符串属性包热设，
        /// 由控件内部缩放——这正是微软 VMConnect "查看→缩放" 的真正机制（SmartSizing 只能缩不能放，放大必须走这里）。
        /// 仅基本会话生效、全屏无效（调用方在全屏时传 100）。</summary>
        public void SetZoomLevel(uint percent)
        {
            if (_zoomLevel == percent) return;   // 去重：LayoutRdpHost 每次布局都调，仅比例真变时穿透 OCX，避免拖动时每帧热设卡顿
            // 未连接时设置 ZoomLevel 可能触发不可恢复的 COM 异常。
            if (ConnectionState != 1) return;
            _zoomLevel = percent;
            try
            {
                var ext = (IMsRdpExtendedSettings)GetOcx();
                object v = percent;
                ext.set_Property("ZoomLevel", ref v);
            }
            catch (Exception ex) { Debug.WriteLine("[Rdp] SetZoomLevel 失败: " + ex.Message); }
        }

        private bool TrySet(string what, Action set)
        {
            try
            {
                set();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Rdp] 设 {what} 失败: {ex.GetType().Name} — {ex.Message}");
                if (_useAllMonitors)
                    AppLog.Warning("RDP 控制台", $"设置 RDP 参数 {what} 失败。",
                        MultiMonitorLogContext(), ex);
                return false;
            }
        }

        private T TryGet<T>(string what, Func<T> get, T fallback)
        {
            try { return get(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Rdp] 读 {what} 失败: {ex.GetType().Name} — {ex.Message}");
                if (_useAllMonitors)
                    AppLog.Warning("RDP 控制台", $"读取 RDP 参数 {what} 失败。",
                        MultiMonitorLogContext(), ex);
                return fallback;
            }
        }

        // COM 事件处理的护栏：异常绝不能逃回 native 回调方（否则 0xC000041D 致命回调异常、进程秒退）。
        private void Safe(Action handler)
        {
            try { handler(); }
            catch (Exception ex) { Debug.WriteLine("[Rdp] 事件处理异常(已拦截): " + ex); }
        }

        private const uint GA_ROOT = 2;
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    }
}
