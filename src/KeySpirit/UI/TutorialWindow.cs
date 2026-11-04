using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using KeySpirit.Core;

namespace KeySpirit.UI;

/// <summary>教程窗口：全程序唯一的新窗口（8.10）。本地 HTML + WebView2，完全离线。</summary>
public static class TutorialWindow
{
    static Window? _window;

    public static void Open()
    {
        if (_window != null)
        {
            _window.Activate();
            return;
        }

        var win = new Window
        {
            Title = "键灵 · 教程",
            Width = 960,
            Height = 680,
            MinWidth = 720,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0F, 0x13, 0x18)),
            ShowInTaskbar = true,
        };

        var html = Path.Combine(AppContext.BaseDirectory, "Assets", "tutorial.html");
        WebView2? view = null;

        try
        {
            view = new WebView2();
            win.Content = view;
            win.Loaded += async (_, _) =>
            {
                try
                {
                    var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                        .CreateAsync(userDataFolder: Path.Combine(Game.Dir, "webview"));
                    await view.EnsureCoreWebView2Async(env);
                    view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    view.CoreWebView2.Navigate(new Uri(html).AbsoluteUri);
                    Log.Write("教程窗口已打开（WebView2）");
                }
                catch (Exception ex)
                {
                    Log.Write("WebView2 初始化失败，降级为浏览器打开：" + ex.Message);
                    Fallback(win, html);
                }
            };
        }
        catch (Exception ex)
        {
            Log.Write("WebView2 不可用，降级为浏览器打开：" + ex.Message);
            win.Content = new TextBlock
            {
                Text = "当前系统缺少 WebView2 运行时，已改用系统浏览器打开教程。",
                Margin = new Thickness(24),
                Foreground = System.Windows.Media.Brushes.Gainsboro,
                TextWrapping = TextWrapping.Wrap,
            };
            win.Loaded += (_, _) => Fallback(win, html);
        }

        win.Closed += (_, _) => _window = null;
        _window = win;

        // 深色标题栏（Win10 1809+ / Win11），否则白标题栏配深色内容很割裂
        win.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                int on = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（19 是旧版编号）
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch (Exception ex) { Log.Write("深色标题栏设置失败：" + ex.Message); }
        };

        win.Show();
        win.Activate();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static void Fallback(Window win, string html)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(html) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("打开教程失败：" + ex.Message); }
        win.Close();
    }
}
