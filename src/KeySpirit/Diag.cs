using System;
using System.Threading;
using System.Windows.Threading;
using KeySpirit.Core;

namespace KeySpirit;

/// <summary>
/// 现场取证。
///
/// 起因：玩家报「领取每日礼包时卡死闪退」，但日志最后一行是**领取成功**（金币已到账），
/// 之后既没有「已存档」，也没有「未处理异常」，Windows 事件日志与 CrashDumps 里
/// 查不到任何一次崩溃 —— 也就是说进程是在「没抛异常、没走退出流程」的情况下没的。
/// 这种死法只有两种可能：界面线程卡住被人为结束，或者 CLR 之外的原生层直接终止。
/// 两种都不会留下任何痕迹，**必须由程序自己在运行时记录**，事后才能定位。
///
/// 这里做三件事：
///   ① 记「最后动作」：点击哪一行、执行到哪一步。卡死时由看门狗写进日志。
///   ② 界面线程看门狗：后台线程盯界面线程的心跳，停了就是卡死，并写出停了多久 + 最后动作。
///   ③ 兜住非 UI 线程的异常：现在只有 UI 线程的 DispatcherUnhandledException 有人管，
///      别的线程一抛就是整个进程没了（同样一个字都不留）。
///
/// 全部是只读的旁路，不改变任何玩法行为。
/// </summary>
public static class Diag
{
    static volatile string last = "启动";
    static long uiTick;

    /// <summary>最后一步动作。只在触发告警时落盘，平时只改一个字符串，不产生 IO。</summary>
    public static void Note(string what) => last = what;
    public static string Last => last;

    /// <summary>界面线程心跳（由 DispatcherTimer 每秒调一次）。</summary>
    public static void UiAlive() => Interlocked.Increment(ref uiTick);

    static bool started;

    public static void Start()
    {
        if (started) return;
        started = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("线程未处理异常（进程即将结束）：" + e.ExceptionObject);

        var t = new Thread(Loop)
        {
            IsBackground = true,
            Name = "KeySpirit.Watchdog",
        };
        t.Start();
    }

    /// <summary>
    /// 自己写盘，**不走 Log.Write**：卡死的典型情形就是界面线程正卡在 Log.Write 的锁里，
    /// 看门狗再去抢同一把锁，等于把唯一的取证机会也一起卡掉。这里用独立的文件与锁。
    /// </summary>
    static readonly object Gate = new();

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                var dir = Game.Dir;
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "diag.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// <summary>卡住多久开始记：界面线程每秒一拍，连续 10 拍没动静就当它卡了。</summary>
    const int StallSeconds = 10;

    static void Loop()
    {
        long seen = Interlocked.Read(ref uiTick);
        int stall = 0;
        bool reported = false;

        while (true)
        {
            Thread.Sleep(2000);
            long cur = Interlocked.Read(ref uiTick);

            if (cur == seen)
            {
                stall += 2;
                if (stall >= StallSeconds && !reported)
                {
                    reported = true;
                    Write($"⚠ 界面无响应约 {stall}s，最后动作：{last}");
                }
            }
            else
            {
                if (reported) Write($"界面已恢复（卡了约 {stall}s，最后动作：{last}）");
                stall = 0;
                reported = false;
                seen = cur;
            }
        }
    }
}
