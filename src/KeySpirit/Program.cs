using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using KeySpirit.Core;
using KeySpirit.UI;

namespace KeySpirit;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // 单实例：第二个进程直接退出，不弹任何提示。
        // --selftest 用独立互斥名，游戏正开着时也能跑自测（否则会误读到上一次的结果文件）。
        bool selfTest = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--selftest");
        using var mutex = new Mutex(true, selfTest ? @"Global\KeySpirit_SelfTest" : @"Global\KeySpirit_SingleInstance", out bool created);
        if (!created) return;

        // 本机 GPU 渲染路径异常（空 WPF 窗口硬件渲染吃掉 690MB），强制软件渲染后只占 ~100MB。
        // 本作无任何逐帧动画、面板仅 288 宽且只在打开时绘制，软件渲染无感知。
        // 需要硬件渲染时设 KS_HW_RENDER=1。
        if (Environment.GetEnvironmentVariable("KS_HW_RENDER") != "1")
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        if (Array.Exists(Environment.GetCommandLineArgs(), a => a == "--selftest"))
        {
            SelfTest.Run();
            return;
        }

        Game.I.Boot();
        var tray = new TrayHost();
        tray.Start();

        if (Environment.GetEnvironmentVariable("KS_OPEN_TUTORIAL") == "1")
        {
            var tt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            tt.Tick += (_, _) => { tt.Stop(); TutorialWindow.Open(); };
            tt.Start();
        }

        // 开发自检用：KS_SHOW_PANEL=1 时启动后自动展开面板（便于截图核对布局）
        if (Environment.GetEnvironmentVariable("KS_SHOW_PANEL") == "1")
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                var pg = Environment.GetEnvironmentVariable("KS_PANEL_PAGE");
                if (!string.IsNullOrEmpty(pg)) tray.Panel.StartPage = pg;
                tray.Panel.ShowPanel();
            };
            t.Start();
        }

        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("未处理异常：" + e.Exception);
            e.Handled = true;
        };
        app.Exit += (_, _) => tray.Dispose();
        app.Run();
    }
}
