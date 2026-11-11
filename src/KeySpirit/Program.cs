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
        // 发布配图：--shot <目录>，同样用独立互斥名，游戏开着也能出图（见 Shot.cs）。
        var argv = Environment.GetCommandLineArgs();
        int shotAt = Array.IndexOf(argv, "--shot");
        bool shot = shotAt >= 0 && shotAt + 1 < argv.Length;
        // 崩溃复现：--click <页> <行关键字>，把鼠标事件真的发到那一行上（见 Repro.cs）
        int clickAt = Array.IndexOf(argv, "--click");
        bool click = clickAt >= 0 && clickAt + 2 < argv.Length;
        string mutexName = selfTest ? @"Global\KeySpirit_SelfTest"
                         : shot ? @"Global\KeySpirit_Shot"
                         : click ? @"Global\KeySpirit_Repro"
                         : @"Global\KeySpirit_SingleInstance";
        using var mutex = new Mutex(true, mutexName, out bool created);
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

        if (shot)
        {
            Shot.Run(argv[shotAt + 1]);
            return;
        }

        if (click)
        {
            if (argv[clickAt + 1] == "full" && clickAt + 3 < argv.Length)
            {
                int hover = clickAt + 4 < argv.Length && int.TryParse(argv[clickAt + 4], out int h) ? h : 1700;
                Repro.RunFull(argv[clickAt + 2], argv[clickAt + 3], hover);
            }
            else if (argv[clickAt + 1] == "soak")
            {
                int times = clickAt + 2 < argv.Length && int.TryParse(argv[clickAt + 2], out int n) ? n : 200;
                Repro.RunSoak(times);
            }
            else if (argv[clickAt + 1] == "stall")
            {
                int sec = clickAt + 2 < argv.Length && int.TryParse(argv[clickAt + 2], out int s) ? s : 20;
                Repro.RunStall(sec);
            }
            else
                Repro.Run(argv[clickAt + 1], argv[clickAt + 2]);
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
        app.Exit += (_, _) =>
        {
            // 有这一行 = 程序自己走完了退出流程；没有 = 进程是被结束或直接没的。
            // 「卡死闪退」那次日志里就没有这一行，靠它以后能一眼分辨。
            Log.Write("程序退出（正常路径）");
            tray.Dispose();
        };

        // 界面线程心跳：看门狗据此判断界面是否卡住（见 Diag.cs）
        var uiBeat = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        uiBeat.Tick += (_, _) => Diag.UiAlive();
        uiBeat.Start();
        Diag.Start();

        app.Run();
    }
}
