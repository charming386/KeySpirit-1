using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using KeySpirit.Core;
using KeySpirit.UI;

namespace KeySpirit;

/// <summary>
/// 崩溃复现工具（`KeySpirit.exe --click <页> <行关键字>`）。
///
/// 为什么需要它：托盘面板里的点击是「改状态 + 立刻重建子列」，只有真的把鼠标事件发到
/// 那一行上，才走得到玩家踩过的那条路径。读一遍代码看不出 WPF 在重建控件树时会怎么反应，
/// 而这正是「点一下就没进程了」唯一能重来的办法。
///
/// · 存档先复制到临时目录再读，**不动玩家的真实存档**；
/// · 不注册 DispatcherUnhandledException（Program 里那段），所以异常会原样抛出来，
///   而不是像正式运行那样被吞掉——这一步就是来拿堆栈的；
/// · 不启托盘、不进游戏主循环。
/// </summary>
public static class Repro
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

    public static void Run(string page, string keyword)
    {
        var src = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeySpirit");
        var tmp = Path.Combine(Path.GetTempPath(), "keyspirit_repro");
        Directory.CreateDirectory(tmp);

        var save = Path.Combine(src, "save.json");
        if (File.Exists(save)) File.Copy(save, Path.Combine(tmp, "save.json"), true);
        Game.DirOverride = tmp;

        Game.I.Boot();
        Console.WriteLine($"state: 第 {Game.I.Layer} 层 Lv{Game.I.Level} " +
                          $"今日敲击 {Game.I.S.DailyKeys:N0} mask {Game.I.S.DailyMask} " +
                          $"袋 {Game.I.S.Bags.Count} 金币 {Fmt.Large(Game.I.Gold)}");

        try { SetCursorPos(0, 0); } catch { }

        var w = new PanelWindow { StartPage = page };
        w.ShowPanel();
        Pump();

        Console.WriteLine("click: " + w.ShotClick(keyword));
        Pump();
        Pump();

        Console.WriteLine($"after: 金币 {Fmt.Large(Game.I.Gold)} 墨晶 {Fmt.Large(Game.I.Ink)} " +
                          $"mask {Game.I.S.DailyMask}");
        Console.WriteLine("alive");

        try { w.HidePanel(); w.Close(); } catch { }
    }

    static void Pump()
    {
        for (int i = 0; i < 3; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    // ================= 完整运行时复现 =================

    /// <summary>看门狗输出：UI 线程卡住时它照样在写，用它区分「卡死」与「进程没了」。</summary>
    static string BeatPath => Path.Combine(Path.GetTempPath(), "keyspirit_beat.txt");

    static void Beat(string msg)
    {
        try { File.AppendAllText(BeatPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}"); }
        catch { }
    }

    /// <summary>
    /// 与真实运行一致：托盘、心跳、存档计时器、悬停气泡全开，然后按秒表演一遍
    /// 「展开面板 → 停在这行上等气泡 → 点下去 → 再活 15 秒」。
    /// 「卡死」不会留下任何日志或系统事件，只能靠这种逐步标记定位到具体语句。
    /// </summary>
    public static void RunFull(string page, string keyword, int hoverMs = 1700)
    {
        File.Delete(BeatPath);
        Beat("start");

        // UI 线程之外的异常：正式运行里被 DispatcherUnhandledException 挡掉的只有 UI 线程那一路，
        // 别的线程一抛就是整个进程没了，这里先抓住看看到底有没有。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Beat("APPDOMAIN 未处理异常: " + e.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            Beat("任务未观察异常: " + e.Exception);

        // 看门狗：独立线程每秒写一行
        using var wd = new System.Threading.Timer(_ => Beat("  watchdog"), null, 1000, 1000);

        var src = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeySpirit");
        var tmp = Path.Combine(Path.GetTempPath(), "keyspirit_repro");
        Directory.CreateDirectory(tmp);
        var save = Path.Combine(src, "save.json");
        if (File.Exists(save)) File.Copy(save, Path.Combine(tmp, "save.json"), true);
        Game.DirOverride = tmp;

        Game.I.Boot();
        Beat($"boot 第 {Game.I.Layer} 层 Lv{Game.I.Level} 敲击 {Game.I.S.DailyKeys:N0} mask {Game.I.S.DailyMask}");

        var tray = new TrayHost();
        tray.Start();
        Beat("tray started");

        var w = new PanelWindow();     // 不预设 StartPage：先停在主列，再点「领取」进页

        void At(int ms, Action a, string tag) => Timer(ms, () =>
        {
            Beat(">> " + tag);
            try { a(); } catch (Exception ex) { Beat("!! " + tag + " 抛异常: " + ex); }
            Beat("<< " + tag);
        });

        At(1200, () =>
        {
            // 模拟「从托盘图标展开」：先把光标放到托盘位置，面板会贴着屏幕底边挤着长，
            // 子列可用高度被压到很小 —— 与正常位置完全是两套布局数值。
            var cur = Environment.GetEnvironmentVariable("KS_CURSOR");
            if (!string.IsNullOrEmpty(cur) && cur.Contains(','))
            {
                var pp = cur.Split(',');
                if (int.TryParse(pp[0], out int cx) && int.TryParse(pp[1], out int cy)) SetCursorPos(cx, cy);
                Beat("光标放到 " + cur);
            }
            w.ShowPanel();
        }, "ShowPanel");
        // 真实流程是「先点主列『领取』进页」，而不是带着 StartPage 直接建出来——两条路都要走一遍
        At(2000, () => w.ShotClick("领取"), "点主列『领取』");
        At(2600, () => MoveTo(w, keyword), "把光标移到行上");
        At(2600 + hoverMs, () => RealClick(w, keyword), $"悬停 {hoverMs}ms 后真点击");
        At(2600 + hoverMs + 2000, () => Beat($"金币 {Fmt.Large(Game.I.Gold)} mask {Game.I.S.DailyMask}"), "2s 后状态");
        At(2600 + hoverMs + 12000, () => Beat("存活到结束"), "12s 后状态");
        At(2600 + hoverMs + 14000, () => { Beat("done"); Application.Current.Shutdown(); }, "收工");

        // UI 线程自己的心跳：它停了、看门狗还在写 → 就是 UI 线程卡死（而不是进程没了）
        var ui = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ui.Tick += (_, _) => Beat("      ui hb");
        ui.Start();

        Application.Current.Run();
        Beat("exit");
    }

    static void Timer(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    /// <summary>
    /// 验证看门狗：故意把界面线程堵住 N 秒，确认 diag.log 里真的会出现
    /// 「界面无响应 Ns，最后动作：…」以及恢复记录。取证代码没验过等于没有。
    /// </summary>
    public static void RunStall(int sec)
    {
        File.Delete(BeatPath);
        var tmp = Path.Combine(Path.GetTempPath(), "keyspirit_repro");
        Directory.CreateDirectory(tmp);
        Game.DirOverride = tmp;
        var diagFile = Path.Combine(tmp, "diag.log");
        if (File.Exists(diagFile)) File.Delete(diagFile);

        Diag.Start();
        var ui = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(1) };
        ui.Tick += (_, _) => Diag.UiAlive();
        ui.Start();

        Diag.Note("测试：执行「5,000 档」");
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Diag.Note("测试：卡住之前");
            Beat($"堵住界面线程 {sec}s");
            System.Threading.Thread.Sleep(sec * 1000);      // 界面线程真的停住
            Diag.Note("测试：恢复之后");
            Beat("界面已恢复");
            Timer(4000, () => Application.Current.Shutdown());
        };
        t.Start();

        Application.Current.Run();

        Beat("exit");
        Beat(File.Exists(diagFile) ? "diag.log:\n" + File.ReadAllText(diagFile) : "diag.log 不存在");
    }
    public static void RunSoak(int times)
    {
        File.Delete(BeatPath);
        Beat("soak start");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Beat("APPDOMAIN 未处理异常: " + e.ExceptionObject);
        using var wd = new System.Threading.Timer(_ => Beat("  watchdog"), null, 1000, 1000);

        var src = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeySpirit");
        var tmp = Path.Combine(Path.GetTempPath(), "keyspirit_repro");
        Directory.CreateDirectory(tmp);
        var save = Path.Combine(src, "save.json");
        if (File.Exists(save)) File.Copy(save, Path.Combine(tmp, "save.json"), true);
        Game.DirOverride = tmp;

        Game.I.Boot();
        var tray = new TrayHost();
        tray.Start();
        var w = new PanelWindow();

        var ui = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ui.Tick += (_, _) => Beat("      ui hb");
        ui.Start();

        int n = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        t.Tick += (_, _) =>
        {
            try
            {
                if (n == 0) w.ShowPanel();
                else if (n == 1) w.ShotClick("领取");
                else if (n % 40 == 0) w.ShotClick("5,000");     // 隔一阵把表单重建一次做干扰
                else RealClick(w, "5,000");
            }
            catch (Exception ex) { Beat("!! 第 " + n + " 次抛异常: " + ex); }
            if (++n % 10 == 0) Beat($"已点 {n} 次 · 金币 {Fmt.Large(Game.I.Gold)} mask {Game.I.S.DailyMask}");
            if (n >= times)
            {
                t.Stop();
                Beat("soak done");
                Application.Current.Shutdown();
            }
        };
        t.Start();

        Application.Current.Run();
        Beat("exit");
    }

    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;

    /// <summary>把光标挪到这一行正中（物理像素）。WPF 会自己发 MouseEnter，注释计时随之启动。</summary>
    static void MoveTo(PanelWindow w, string keyword)
    {
        var r = w.RowRectOnScreen(keyword);
        if (r.IsEmpty) { Beat("MoveTo: 找不到行 " + keyword); return; }
        SetCursorPos((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        Beat($"光标移到 {(int)(r.Left + r.Width / 2)},{(int)(r.Top + r.Height / 2)}");
    }

    /// <summary>
    /// 真点击：光标已经停在这一行上，直接按下再抬起。
    /// 不用 RaiseEvent 是因为那样绕过了鼠标捕获、命中测试与 WM_MOUSE 消息——
    /// 而玩家遇到的正是那条路。
    /// </summary>
    static void RealClick(PanelWindow w, string keyword)
    {
        var r = w.RowRectOnScreen(keyword);
        if (r.IsEmpty) { Beat("RealClick: 找不到行 " + keyword); return; }
        SetCursorPos((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Beat("已注入真实点击");
    }
}
