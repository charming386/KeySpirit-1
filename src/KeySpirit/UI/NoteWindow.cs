using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace KeySpirit.UI;

/// <summary>
/// 按钮注释气泡（8.4.1）：鼠标在某一行上停满 1.5 秒才出现，说明这个按钮到底干什么。
///
/// 为什么单独开一个窗口而不是面板里的 ToolTip：
///   · 面板是固定几何的分层窗口，内部没有多余空间，塞进去会压住相邻行；
///   · ToolTip 的默认模板是浅色系统样式，和自绘菜单不搭，改模板反而更重；
///   · 需要「点击穿透 + 不抢焦点」，这两个都是窗口级样式，写在窗口上最直接。
///
/// 只有一块实例（复用），显示时按行矩形定位：优先右侧，放不下换左侧，再放不下放行下方。
/// 全程 WS_EX_TRANSPARENT，鼠标不会打到气泡上，因此不会把行的 MouseLeave 骗出来。
/// </summary>
public class NoteWindow : Window
{
    const double GAP = 8;          // 气泡与行的间距
    const double MAX_TEXT_W = 190; // 文本最大宽（超过就换行，避免一条长龙）

    static readonly Brush Bg = Fz(0x16, 0x1B, 0x21);
    static readonly Brush Line = Fz(0x35, 0x42, 0x4D);
    static readonly Brush Fg = Fz(0xE4, 0xEB, 0xF0);
    static readonly FontFamily Font = new("Segoe UI, Microsoft YaHei UI");

    static NoteWindow? _one;

    readonly TextBlock _text;

    NoteWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;
        IsHitTestVisible = false;

        _text = new TextBlock
        {
            FontSize = 10,
            FontFamily = Font,
            Foreground = Fg,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = MAX_TEXT_W,
            LineHeight = 15,
        };

        Content = new Border
        {
            Background = Bg,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 8),
            Child = _text,
        };

        SourceInitialized += (_, _) => MakeNoActivate();
    }

    /// <summary>
    /// 只做定位计算，不碰窗口。抽出来是为了自测能直接验证「右侧优先 / 放不下翻左侧 / 上下不出界」。
    /// </summary>
    internal static (double X, double Y) Place(Rect row, double cw, double ch, Rect wa)
    {
        double x = row.Right + GAP;
        if (x + cw > wa.Right - 4) x = row.Left - GAP - cw;          // 右侧放不下 → 左侧
        if (x < wa.Left + 4) x = Math.Max(wa.Left + 4, row.Left);     // 两侧都放不下 → 贴行左边

        double y = row.Top + (row.Height - ch) / 2;                   // 默认与行垂直居中对齐
        if (y + ch > wa.Bottom - 4) y = wa.Bottom - 4 - ch;           // 底边够不着 → 往上顶
        if (y < wa.Top + 4) y = wa.Top + 4;
        return (x, y);
    }

    /// <summary>
    /// 在行旁边显示一条注释。rowDip 是这一行在屏幕上的矩形（DIP），
    /// 定位在工作区内做，超出就翻到另一侧。
    /// </summary>
    public static void ShowNote(string text, Rect rowDip)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        _one ??= new NoteWindow();
        var w = _one;

        w._text.Text = text;
        w.Show();                 // 先显示，SizeToContent 才有真实的 DesiredSize
        w.UpdateLayout();

        double cw = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
        double ch = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
        var (x, y) = Place(rowDip, cw, ch, SystemParameters.WorkArea);

        w.Left = x;
        w.Top = y;
    }

    public static void HideNote() => _one?.Hide();

    /// <summary>
    /// 给出图工具用：把一条注释排好版、作为独立视觉返回，不创建窗口、不影响真实运行。
    /// 气泡是顶层窗口，RenderTargetBitmap 拍不到，发布配图只能把它和面板位图拼在一起。
    /// </summary>
    internal static FrameworkElement BuildVisual(string text)
    {
        var w = new NoteWindow();
        w._text.Text = text;
        var b = (FrameworkElement)w.Content;
        b.Measure(new Size(MAX_TEXT_W + 40, double.PositiveInfinity));
        b.Arrange(new Rect(new Point(0, 0), b.DesiredSize));
        b.UpdateLayout();
        return b;
    }

    void MakeNoActivate()
    {
        var h = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(h, GWL_EXSTYLE);
        style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
        SetWindowLong(h, GWL_EXSTYLE, style);
    }

    static Brush Fz(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int val);
}
