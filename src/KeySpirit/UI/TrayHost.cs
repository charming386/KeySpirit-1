using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using KeySpirit.Core;
using WinForms = System.Windows.Forms;

namespace KeySpirit.UI;

/// <summary>托盘 + 隐藏消息窗口 + Raw Input 键盘计数 + 全局心跳。</summary>
public class TrayHost : IDisposable
{
    const int WM_INPUT = 0x00FF;

    readonly WinForms.NotifyIcon tray;
    HwndSource? src;
    readonly DispatcherTimer heartbeat;
    readonly DispatcherTimer saveTimer;
    PanelWindow? panel;
    TipWindow? tip;
    readonly DispatcherTimer tipDelay;
    readonly DispatcherTimer tipLifetime;
    readonly DispatcherTimer trayPoll;
    bool tipWanted;
    bool tipCool;                       // 自动收起后的冷却：光标不动就不反复弹

    public PanelWindow Panel => panel ??= new PanelWindow();

    public TrayHost()
    {
        tray = new WinForms.NotifyIcon
        {
            Icon = LoadIcon("icon-base"),
            Text = "",                    // 设计稿：szTip 置空，悬停改出只读小提示
            Visible = true,
        };
        tray.MouseMove += OnTrayMove;
        tray.MouseUp += (_, e) =>
        {
            HideTip();
            if (e.Button == WinForms.MouseButtons.Left || e.Button == WinForms.MouseButtons.Right)
                TogglePanel();
        };

        heartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        heartbeat.Tick += (_, _) =>
        {
            Game.I.Tick(TimeSpan.FromMilliseconds(250));
            UpdateTrayIcon();
        };

        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        saveTimer.Tick += (_, _) => Game.I.Save();

        tipDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        tipDelay.Tick += (_, _) =>
        {
            tipDelay.Stop();
            if (tipWanted) ShowTip();
        };

        tipLifetime = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        tipLifetime.Tick += (_, _) => { tipLifetime.Stop(); HideTip(); tipCool = true; };

        // 悬停检测：WinForms 的 NotifyIcon.MouseMove 在 WPF 宿主里不派发（点击事件正常），
        // 所以改为轮询光标是否落在托盘图标矩形内 —— 这也是系统 tooltip 用的判据。
        trayPoll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        trayPoll.Tick += (_, _) => PollTray();
    }

    public void Start()
    {
        var p = new HwndSourceParameters("KeySpiritHidden")
        {
            Width = 0, Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080,   // WS_EX_TOOLWINDOW
        };
        src = new HwndSource(p);
        src.AddHook(WndProc);
        RegisterKeyboard();

        heartbeat.Start();
        saveTimer.Start();
        trayPoll.Start();
        Log.Write("启动完成（隐藏消息窗口已建立）");
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            // 只计数，不解析键值、不落盘任何按键内容。
            // 一次敲击系统会送两条 WM_INPUT（按下 + 抬起），只算按下那条，否则计数翻倍。
            if (IsKeyDown(lParam)) Game.I.OnKeyPress();
            handled = false;
        }
        return IntPtr.Zero;
    }

    void TogglePanel()
    {
        var host = Panel;
        if (host.IsVisible) { host.HidePanel(); return; }
        host.ShowPanel();
    }

    void OnTrayMove(object? sender, WinForms.MouseEventArgs e) => PollTray();

    void ShowTip()
    {
        if (!tipWanted) return;
        try
        {
            tip ??= new TipWindow();
            tip.ShowAtCursor(Game.I);
            tip.Refresh(Game.I);
            tip.StartAutoRefresh();
            tipLifetime.Stop();
            tipLifetime.Start();
        }
        catch (Exception ex) { Log.Write("悬停提示失败：" + ex); }
    }

    public void HideTip()
    {
        tipWanted = false;
        tipDelay.Stop();
        tip?.HideTip();
    }

    void UpdateTrayIcon()
    {
        string name = "icon-base";
        if (Game.I.S.Paused) name = "icon-pause";
        else if (Game.I.Resting) name = "icon-rest";
        else if (Game.I.S.Bags.Count > 0) name = "icon-reward";
        if (tray.Icon != null && _currentIcon == name) return;
        _currentIcon = name;
        tray.Icon = LoadIcon(name);
        if (tip != null && tip.IsVisible) tip.Refresh(Game.I);
    }

    string _currentIcon = "";

    public static System.Drawing.Icon LoadIcon(string name)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", name + ".ico");
        if (File.Exists(path)) return new System.Drawing.Icon(path);
        return System.Drawing.SystemIcons.Application;
    }

    // ---- Raw Input ----
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER
    {
        public uint dwType, dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWKEYBOARD
    {
        public ushort MakeCode, Flags, Reserved, VKey;
        public uint Message;
        public IntPtr ExtraInformation;
    }

    const uint RID_INPUT = 0x10000003;
    const ushort RI_KEY_BREAK = 0x01;    // 断码 = 抬起
    const ushort VK_FAKE = 0xFF;         // 伪键（SendInput 之类），不计

    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr hRawInput, uint cmd, IntPtr data, ref uint size, uint headerSize);

    /// <summary>
    /// 这次 WM_INPUT 是不是「按下」。只读 Flags / VKey 用于过滤，**键值不记录、不落盘**。
    /// 一次敲击会先后产生 MAKE（按下）与 BREAK（抬起）两条 WM_INPUT，只认 MAKE。
    /// </summary>
    bool IsKeyDown(IntPtr lParam)
    {
        try
        {
            uint hdr = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            uint size = 0;
            if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, hdr) != 0) return false;
            if (size == 0 || size > 1024) return false;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                uint got = size;
                if (GetRawInputData(lParam, RID_INPUT, buf, ref got, hdr) != got) return false;
                var head = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                if (head.dwType != 1) return false;                 // RIM_TYPEKEYBOARD
                var kb = Marshal.PtrToStructure<RAWKEYBOARD>(IntPtr.Add(buf, (int)hdr));
                if (kb.VKey == 0 || kb.VKey == VK_FAKE) return false;
                return (kb.Flags & RI_KEY_BREAK) == 0;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    bool registered;

    void RegisterKeyboard()
    {
        var rid = new RAWINPUTDEVICE[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x06,                 // keyboard
                dwFlags = 0x00000100,           // RIDEV_INPUTSINK：后台也收，不抢焦点
                hwndTarget = src!.Handle,
            }
        };
        registered = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        Log.Write("Raw Input 键盘监听：" + (registered ? "已注册" : "注册失败"));
    }

    /// <summary>注销监听：dwFlags=RIDEV_REMOVE(0x1)，hwnd=0。</summary>
    public void UnregisterKeyboard()
    {
        if (!registered) return;
        var rid = new RAWINPUTDEVICE[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = 0x00000001, hwndTarget = IntPtr.Zero }
        };
        RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        registered = false;
        Log.Write("Raw Input 键盘监听：已注销");
    }

    // ---- 托盘图标矩形（系统 tooltip 的定位依据，也是"鼠标是否停在图标上"的唯一可靠判据）----
    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll", SetLastError = true)]
    static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);

    /// <summary>
    /// 取托盘图标在屏幕上的矩形（物理像素）。WinForms 的 NotifyIcon 不暴露句柄，
    /// 只能反射它内部的 NativeWindow；接口号固定为 1。
    /// 拿不到就返回 false，调用方退回事件驱动路径。
    /// </summary>
    bool TryGetTrayRect(out RECT r)
    {
        r = default;
        try
        {
            if (_trayHwnd == IntPtr.Zero)
            {
                // 字段名随 .NET 版本变过（window / _window），直接按类型找更稳
                foreach (var f in typeof(WinForms.NotifyIcon).GetFields(
                             System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    if (f.GetValue(tray) is WinForms.NativeWindow nw && nw.Handle != IntPtr.Zero)
                    {
                        _trayHwnd = nw.Handle;
                        Log.Write("悬停提示：已取到托盘图标句柄（" + f.Name + "）");
                        break;
                    }
                }
            }
            if (_trayHwnd == IntPtr.Zero) return false;

            var id = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                hWnd = _trayHwnd,
                uID = 1,
            };
            bool ok = Shell_NotifyIconGetRect(ref id, out r) == 0 && r.Right > r.Left;
            if (ok && !_rectLogged)
            {
                _rectLogged = true;
                Log.Write($"托盘图标区 {r.Left},{r.Top}-{r.Right},{r.Bottom}（悬停检测已启用）");
            }
            return ok;
        }
        catch (Exception ex)
        {
            if (_trayRectWarned) return false;
            _trayRectWarned = true;
            Log.Write("取托盘图标矩形失败：" + ex.Message);
            return false;
        }
    }

    IntPtr _trayHwnd;
    bool _trayRectWarned;
    bool _rectLogged;

    bool CursorOnTray()
    {
        var p = CursorPos();
        // 便宜的前置判断：光标还在桌面工作区内就肯定不在托盘上，省掉一次 shell 调用
        var wa = WinForms.Screen.PrimaryScreen?.WorkingArea;
        if (wa != null && wa.Value.Contains((int)p.X, (int)p.Y)) return false;

        if (!TryGetTrayRect(out var r)) return false;
        // 上下各放宽 4px：图标贴边时手抖也不会闪断
        return p.X >= r.Left - 4 && p.X <= r.Right + 4
            && p.Y >= r.Top - 4 && p.Y <= r.Bottom + 4;
    }

    /// <summary>鼠标在托盘图标上停留的轮询（NotifyIcon.MouseMove 在 WPF 宿主里不派发）。</summary>
    void PollTray()
    {
        if (panel != null && panel.IsVisible) return;    // 菜单开着就别再弹提示
        bool on = CursorOnTray();

        if (!on)
        {
            tipCool = false;                              // 光标离开 → 解除冷却，下次停留还能再弹
            if (!tipWanted) return;
            tipWanted = false;
            tipDelay.Stop();
            // 光标移开就收，别让提示挂在屏幕上挡路
            if (tip != null && tip.IsVisible) tip.HideTip();
            return;
        }

        if (tipWanted || tipCool) return;
        tipWanted = true;
        if (tip == null || !tip.IsVisible) { tipDelay.Stop(); tipDelay.Start(); }
    }

    public static Point CursorPos()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    public void Dispose()
    {
        heartbeat.Stop();
        saveTimer.Stop();
        trayPoll.Stop();
        tipLifetime.Stop();
        tipDelay.Stop();
        Game.I.Save();
        UnregisterKeyboard();
        tray.Visible = false;
        tray.Dispose();
        src?.Dispose();
    }
}
