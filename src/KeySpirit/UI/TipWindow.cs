using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KeySpirit.Core;

namespace KeySpirit.UI;

/// <summary>悬停只读小提示：192×96，4 行，点击穿透、不抢焦点（8.4）。</summary>
public class TipWindow : Window
{
    static readonly Brush Bg = Fz(0x14, 0x18, 0x1E);
    static readonly Brush Line = Fz(0x2C, 0x39, 0x43);
    static readonly Brush Fg = Fz(0xE6, 0xED, 0xF1);
    static readonly Brush Dim = Fz(0x8A, 0x97, 0xA2);
    static readonly Brush Accent = Fz(0x4F, 0xD6, 0xE0);

    static Brush Fz(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
    static readonly FontFamily Font = new("Segoe UI, Microsoft YaHei UI");

    readonly TextBlock l1, l2, l3, l4;
    readonly DispatcherTimer tick;

    public TipWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 176;
        Height = 84;
        ShowActivated = false;

        l1 = Make(11, Fg);
        l2 = Make(10, Dim);
        l3 = Make(10, Accent);
        l4 = Make(10, Dim);

        var panel = new StackPanel { Margin = new Thickness(10, 7, 10, 7) };
        panel.Children.Add(l1);
        panel.Children.Add(l2);
        panel.Children.Add(l3);
        panel.Children.Add(l4);

        Content = new Border
        {
            Background = Bg,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = panel,
        };

        tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) => Refresh(Game.I);

        SourceInitialized += (_, _) => MakeNoActivate();
    }

    TextBlock Make(double size, Brush b) => new()
    {
        FontSize = size, FontFamily = Font, Foreground = b,
        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 2),
    };

    void MakeNoActivate()
    {
        var h = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(h, GWL_EXSTYLE);
        style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
        SetWindowLong(h, GWL_EXSTYLE, style);
    }

    /// <summary>
    /// 定位到托盘图标旁。注意 GetCursorPos 返回物理像素，而 Window.Left/Top 是 DIP，
    /// 高分屏下不换算会把提示框扔到屏幕外（肉眼看就是"根本没有悬停提示"）。
    /// </summary>
    public void ShowAtCursor(Game s)
    {
        var wa = SystemParameters.WorkArea;
        var raw = TrayHost.CursorPos();
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX > 0.01 ? dpi.DpiScaleX : 1;
        double sy = dpi.DpiScaleY > 0.01 ? dpi.DpiScaleY : 1;
        double cx = raw.X / sx, cy = raw.Y / sy;        // → DIP

        double w = Width, h = Height;

        // 默认在光标下方 20px；贴近屏幕底部（任务栏）就改成向上弹
        double left = cx - w / 2;
        double top = (cy > wa.Bottom - h - 48) ? cy - h - 12 : cy + 20;

        left = Math.Clamp(left, wa.Left + 4, Math.Max(wa.Left + 4, wa.Right - w - 4));
        top = Math.Clamp(top, wa.Top + 4, Math.Max(wa.Top + 4, wa.Bottom - h - 4));

        Left = Math.Round(left);
        Top = Math.Round(top);
        if (!IsVisible) Show();
        Refresh(s);
    }

    public void HideTip() => Hide();

    public void Refresh(Game s)
    {
        l1.Text = $"第 {s.Layer} 层 · Lv {s.Level}";
        l2.Text = $"HP {Fmt.Large(s.Hp)} / {Fmt.Large(s.HpMax)}";
        l3.Text = s.S.Paused ? "状态：已暂停" : s.Resting ? "状态：力竭休整" : s.S.LockLayer ? "状态：驻守中" : "状态：爬塔中";
        l4.Text = s.S.Bags.Count > 0 ? $"待领 {s.S.Bags.Count} 袋 · +{Fmt.Large(s.BagSum)}" : $"金币 {Fmt.Large(s.Gold)} · 墨晶 {Fmt.Large(s.Ink)}";
    }

    public void StartAutoRefresh() => tick.Start();

    public new void Hide()
    {
        tick.Stop();
        base.Hide();
    }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int val);
}
