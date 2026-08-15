using System;
using System.Windows;
using ExHyperV.Views;
using ExHyperV.Services.Remote.Consoles;

namespace ExHyperV.Interaction
{
    /// <summary>
    /// 应用级导航/窗口门面：页面导航 + 打开独立窗口。
    /// VM 调用本类，内部统一访问 MainWindow（VM 不再认识具体窗口类型）。
    /// </summary>
    public static class Navigation
    {
        /// <summary>导航主窗口的 NavigationView 到指定页类型。</summary>
        public static void NavigateTo(Type pageType)
        {
            if (Application.Current.MainWindow is MainWindow mw)
                mw.RootNavigation.Navigate(pageType);
        }

        /// <summary>打开虚拟机沉浸式控制台窗口；若该 VM 已有窗口则前置，不新开。</summary>
        public static void OpenConsoleWindow(HostConsoleSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            IHostConsoleRegistry registry = HostConsoleWindows.Registry;
            if (registry.TryActivate(session.WindowKey)) return;

            var window = new ConsoleWindow(session);
            registry.Register(session, window);
            window.Closed += (_, _) => registry.Unregister(session.WindowKey, window);
            try
            {
                window.Show();
            }
            catch
            {
                registry.Unregister(session.WindowKey, window);
                throw;
            }
        }
    }
}
