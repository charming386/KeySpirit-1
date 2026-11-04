using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KeySpirit.Core;

namespace KeySpirit.UI;

/// <summary>
/// 托盘右键菜单（仿 Windows 默认右键菜单）。
/// · 主菜单从鼠标位置展开：右下优先，下方放不下向上、右方放不下向左
/// · 子菜单在父列左右两侧中空间足够的一侧弹出，顶边对齐父项，高度 ≤ 主列 90%
/// · 鼠标悬停 240ms 展开子菜单（与系统一致），点击同样可展开/收起
/// 性能纪律：切页才重建树；运行期每秒只改已有 TextBlock 的 Text，不新建任何控件。
/// </summary>
public class PanelWindow : Window
{
    // ---- 布局常量（8.1）----
    // 字放大 1px 的同时把行高、宽度、内边距一起收紧：字号涨了但菜单反而更小更密。
    // GAP 是两列**面板实体之间**的构图间隙；肉眼看到的缝还要加上两列各自的 3px 投影，
    // 所以 GAP 取 2 时实际观感缝约 8px（原 GAP=8 时约 15px），差不多正好减半。
    const double W_MAIN = 232, W_SUB = 232, W_DETAIL = 306, GAP = 2;
    const double ITEM_H = 21, ITEM_H_SM = 16, PAD_L = 10, PAD_R = 9;
    const double ICON = 14;         // 行内图标边长（行高 21 时 16 会顶满，14 才有呼吸）
    const double ICON_GAP = 5;      // 图标右缘到文字的距离（原 8 看着"断开"）
    const double RADIUS = 8;        // 窗口圆角（Win11 菜单约 8）
    const double SHADOW = 3;        // 投影外扩
    const double INNER = (SHADOW + 1) * 2;   // 每列被 frame 吃掉的宽（阴影 + 边框）
    const double BOTTOM_PAD = 4;    // 每列底部留白
    const double SUB_MAX = 0.9;     // 子列高度上限 = 主列的 90%（同时也是它的固定高度）

    // ---- 字号：只调 FS / FS2 两个数就能整体缩放（8.1）----
    // FS = 主字号偏移（原 14）→ 10；FS2 = 次级字号偏移，比主字号少降 1px。
    const double FS = -4;
    const double FS2 = FS + 1;
    const double FS_ITEM = 14 + FS;    // 10  菜单项
    const double FS_BTN = 14 + FS;     // 10  开始/暂停
    const double FS_HEAD = 13 + FS;    // 9   头部等级、层数
    const double FS_TITLE = 12 + FS2;  // 9   子列标题
    const double FS_HINT = 12 + FS2;   // 9   右侧提示、详情值
    const double FS_TINY = 11 + FS2;   // 8   小节标题、概率公示

    static readonly Brush Bg = Fz(0x1E, 0x22, 0x28);
    static readonly Brush BgSub = Fz(0x1E, 0x22, 0x28);
    static readonly Brush BgHover = Fz(0x2C, 0x36, 0x40);
    // 「当前打开的页」的底色。原来用 0x25303A（比悬停色 0x2C3640 更暗），
    // 结果鼠标停在别的行上时，打开的那一行看起来像"没高亮"——底色深浅反了。
    static readonly Brush BgActive = Fz(0x33, 0x41, 0x4E);
    static readonly Brush Line = Fz(0x33, 0x3E, 0x48);
    static readonly Brush Fg = Fz(0xE8, 0xEE, 0xF3);
    static readonly Brush Dim = Fz(0x8D, 0x9A, 0xA6);
    static readonly Brush Accent = Fz(0x4F, 0xD6, 0xE0);
    static readonly Brush Warn = Fz(0xF2, 0xA6, 0x3B);
    static readonly Brush HpFill = Fz(0x3E, 0xC1, 0x8F);
    static readonly Brush HpRest = Fz(0x6C, 0x74, 0x7E);
    static readonly Brush Track = Fz(0x2A, 0x33, 0x3D);
    static readonly Brush BtnBg = Fz(0x26, 0x30, 0x39);
    static readonly Brush Disabled = Fz(0x56, 0x60, 0x6A);
    static readonly Brush ShadowBrush = Fz(0x3A, 0x00, 0x00, 0x00);

    static readonly FontFamily Font = new("Segoe UI, Microsoft YaHei UI");

    static Brush Fz(byte r, byte g, byte b) => Fz(0xFF, r, g, b);

    static Brush Fz(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();                      // 冻结：避免变更通知与缓存
        return br;
    }

    // ---- 视觉树（只建一次）----
    readonly Grid shell = new();               // 5 列：子列 | 间隙 | 主列 | 间隙 | 子列
    readonly StackPanel mainRoot = new();      // 主列内容（固定高度，不滚动）
    readonly StackPanel subRoot = new();
    readonly Border mainFrame, subFrame;
    readonly ScrollViewer subScroll;

    // 头部常驻文本（只改 Text）
    TextBlock? hLv, hHp, hLine3, hMoney;
    Border? hpHolder, hpFill, expHolder, expFill;
    TextBlock? btnLabel;

    // 行登记（用于每秒刷新文本，不重建）
    readonly List<Row> rows = new();        // 主列 + 子列全部
    readonly List<Row> mainRows = new();    // 常驻主列行（子列重建时保留）
    readonly Dictionary<string, Row> mainByKey = new();

    readonly DispatcherTimer refresh;
    readonly DispatcherTimer hover;
    string hoverWant = "";

    string page = "";
    DateTime rebirthClickAt = DateTime.MinValue;
    readonly bool _keepOpen;
    double subWidth = W_SUB;
    bool subRight = false;              // 子列在右？（否则在左）
    double anchorY;                     // 当前展开项在 mainRoot 内的顶边 Y
    double mainH = 400;                 // 主列整体高度（含 frame）
    Point mainPos;                      // 主列左上角（屏幕 DIP）：只在弹出时由光标决定
    bool posReady;

    // ---- 面板会话内的固定几何 ----
    // 切页时窗口的宽高**一个像素都不许变**：分层窗口（AllowsTransparency）
    // 一旦 SetWindowPos 改了尺寸，DWM 就要重建合成表面，中间那一帧是空的 —— 这就是
    // 用户一直看到的"切次级菜单时闪一下"。所以弹出时把包围盒一次撑到
    // 「主列 + 最大子列宽 × 最大子列高」，之后切页只换内容、只动 subFrame 的
    // Height/Margin（WPF 内部布局，不发 SetWindowPos），屏幕上永远不会出现空白帧。
    double lockW = W_MAIN;              // 窗口宽（DIP）
    double lockH = 400;                 // 窗口高（DIP）
    bool geomLock;                      // 几何已锁定（ShowPanel 之后为 true）

    public string? StartPage;

    public PanelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = Font;

        subScroll = new ScrollViewer
        {
            Content = subRoot,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        try { subScroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), DarkScrollBar()); }
        catch (Exception ex) { Log.Write("滚动条样式失败：" + ex.Message); }

        mainFrame = Frame(Bg, mainRoot);
        subFrame = Frame(BgSub, subScroll);
        subFrame.Visibility = Visibility.Collapsed;
        subFrame.VerticalAlignment = VerticalAlignment.Top;

        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });   // 0 左子列
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });   // 1 间隙
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(W_MAIN) }); // 2 主列
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });   // 3 间隙
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });   // 4 右子列

        Grid.SetColumn(mainFrame, 2);
        Grid.SetColumn(subFrame, 4);
        shell.Children.Add(mainFrame);
        shell.Children.Add(subFrame);

        Content = new Border { Child = shell, Background = Brushes.Transparent };   // 透明区域不参与命中测试

        _keepOpen = Environment.GetEnvironmentVariable("KS_NO_AUTOHIDE") == "1";
        if (_keepOpen) Topmost = true;         // 布局自测：面板常驻且压在最前，便于截图核对
        Deactivated += (_, _) => { if (_keepOpen) return; HidePanel(); };

        // 窗口包围盒比可见面板大一圈（固定几何的代价），点在那圈空白上要能关菜单，
        // 所以给外框铺一层全透明背景让它参与命中测试；点在行/按钮上时 OriginalSource
        // 是行内控件，不会被这里误判。
        if (Content is Border outer)
            outer.MouseLeftButtonDown += (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, outer)) return;
                e.Handled = true;
                HidePanel();
            };

        refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        refresh.Tick += (_, _) => LiveRefresh();

        // 悬停展开（系统默认约 400ms，这里 240ms 更跟手且不会误触）
        hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        hover.Tick += (_, _) =>
        {
            hover.Stop();
            if (hoverWant.Length == 0 || page == hoverWant) return;
            SwitchPage(hoverWant, force: true);
        };
    }

    /// <summary>带 1px 边框 + 投影的圆角面板（投影靠 Padding 露出，不用任何 Effect）。</summary>
    static Border Frame(Brush bg, UIElement child) => new()
    {
        Background = ShadowBrush,
        CornerRadius = new CornerRadius(RADIUS + SHADOW),
        Padding = new Thickness(SHADOW),
        Child = new Border
        {
            Background = bg,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(RADIUS),
            Child = child,
        },
    };

    /// <summary>深色细滚动条（系统默认在深色菜单里是一条白色粗条，很扎眼）。</summary>
    static Style DarkScrollBar()
    {
        const string xaml = """
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
       xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
       TargetType="{x:Type ScrollBar}">
  <Setter Property="Width" Value="6"/>
  <Setter Property="MinWidth" Value="6"/>
  <Setter Property="Background" Value="Transparent"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="{x:Type ScrollBar}">
        <Grid Background="Transparent">
          <Track x:Name="PART_Track" IsDirectionReversed="True">
            <Track.DecreaseRepeatButton>
              <RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/>
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType="{x:Type Thumb}">
                    <Border Background="#5AFFFFFF" CornerRadius="3" Margin="1,0"/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/>
            </Track.IncreaseRepeatButton>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
""";
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    // ================= 显示 / 隐藏 =================
    public void ShowPanel()
    {
        page = StartPage ?? "";
        geomLock = false;
        Build();
        posReady = false;
        EnsureHook();                       // 先把句柄拿到，下面 Scale() 才问得到真实 DPI

        MeasureMain();                      // 主列高度 + 手动 Arrange（这样才能取到各行 anchorY）
        PickSide();                         // 用光标定方向（按最大子列宽判断，切页不换边）
        AnchorAtCursor();                   // 主列落点：只在这里按光标算一次

        lockW = WindowW;                    // 固定包围盒：宽 = 主列 + 最大子列宽
        lockH = LockHeight();               // 高 = 最靠下的父项 + 子列上限（受屏幕限制）
        geomLock = true;

        LayoutColumns();                     // 列宽与子列侧一次定死，之后切页不再动
        UpdateAnchorY();                     // MeasureMain 已 Arrange 过主列，这里取得到行位置
        ApplySubLayout();                    // 子列高度与垂直偏移

        var (l, t, w, h) = ComputeRect();
        PlaceWindow(l, t, w, h);            // Show 之前就摆到位，不出现"先默认再跳"
        mainPos = new Point(l + (subRight ? 0 : w - W_MAIN), t);

        try
        {
            Show();
            UpdateLayout();
            UpdateAnchorY();
            ApplySubLayout();                // Show 之后字体度量才准，重算一次（仍不改窗口几何）
            Activate();
        }
        catch (Exception ex) { Log.Write("面板展开异常：" + ex); }
        RefreshAll();
        refresh.Start();

        if (Environment.GetEnvironmentVariable("KS_DBG_LAYOUT") == "1")
            foreach (var kv in mainByKey)
            {
                try
                {
                    var pt = kv.Value.Box.PointToScreen(new Point(20, kv.Value.Box.ActualHeight / 2));
                    Log.Write($"DBG row {kv.Key} @ {pt.X:0},{pt.Y:0}");
                }
                catch { }
            }
    }

    public void HidePanel()
    {
        refresh.Stop();
        hover.Stop();
        Hide();
        page = "";
    }

    // ================= 尺寸与定位 =================

    /// <summary>窗口固定宽度 = 主列 + 间隙 + **最大**子列宽。取最大值是为了切到详情页时
    /// 不必加宽窗口（加宽 = 一次 SetWindowPos = 一帧空白）。</summary>
    static double WindowW => W_MAIN + GAP + W_DETAIL;

    /// <summary>
    /// 量主列高度，并**手动 Arrange 一次**。手动布局的意义：窗口还没 Show 时也能用
    /// TranslatePoint 取到每一行的 Y，从而在弹出瞬间就把「最靠下的父项」算出来，
    /// 一次定死窗口高度 —— 而不是等 Show 之后再改，那又是一次尺寸变化。
    /// </summary>
    void MeasureMain()
    {
        var wa = SystemParameters.WorkArea;
        double cw = W_MAIN - INNER;

        mainRoot.Measure(new Size(cw, double.PositiveInfinity));
        double ch = Math.Ceiling(mainRoot.DesiredSize.Height);
        mainRoot.Arrange(new Rect(0, 0, cw, ch));

        mainH = ch + INNER;
        if (mainH > wa.Height - 8) mainH = wa.Height - 8;

        mainFrame.Height = mainH;
        mainFrame.VerticalAlignment = VerticalAlignment.Top;
    }

    /// <summary>所有会展开子列的父项里，最靠下的那一行的顶边 Y。
    /// （"退出"没有子列，排除在外。）**仅供调试对照**：Show 之前这个值不可信
    /// （字体度量未定型，实测能差 150px），窗口高度改用常量估算，见 LockHeight。</summary>
    double MaxAnchorY()
    {
        double m = 0;
        foreach (var kv in mainByKey)
        {
            if (kv.Key == "退出") continue;
            try
            {
                var p = kv.Value.Box.TranslatePoint(new Point(0, 0), mainRoot);
                if (p.Y > m) m = p.Y;
            }
            catch { }
        }
        return m;
    }

    /// <summary>窗口固定高度：够「最靠下的父项 + 子列上限」，且不超出屏幕可用空间。
    /// 高一点是无害的 —— 多出来的部分是一个 alpha=0 的透明矩形，屏幕上看不见任何东西。
    ///
    /// 【绝不动主列位置】屏幕下方装不下整块时，只把窗口高度截到「主列顶 → 屏幕底」，
    /// 让子列自己上移（见 ApplySubLayout）。**不能把整块菜单往上提** —— 主列必须留在
    /// 光标处（弹出时主列底边贴光标、向上展开），一提就"飞到天上"，离开托盘十万八千里。
    /// Windows 也是这样：父菜单不动，子菜单可以往上长出父菜单顶部。
    ///
    /// 【为什么不用 MaxAnchorY()】Show 之前字体度量尚未定型，同一份数据实测能测出 74 / 156 / 220
    /// 三个值（差 150px），于是窗口高度忽大忽小、子列被迫上移的量也跟着乱跳。
    /// 最靠下的父项必然在主列底往上「一行 + 底部留白」处，直接用常量估就行。</summary>
    double LockHeight()
    {
        var wa = SystemParameters.WorkArea;
        double lastAnchor = Math.Max(0, mainH - ITEM_H - BOTTOM_PAD - INNER * 0.5);
        double need = lastAnchor + mainH * SUB_MAX + SHADOW + 1;
        double room = posReady ? wa.Bottom - mainPos.Y : wa.Height;

        double h = Math.Max(mainH, Math.Min(need, room));
        if (h > wa.Height - 8) h = wa.Height - 8;
        return Math.Ceiling(h);
    }

    /// <summary>列宽与子列所在侧：整个面板会话只设一次。</summary>
    void LayoutColumns()
    {
        shell.ColumnDefinitions[0].Width = new GridLength(subRight ? 0 : W_DETAIL);
        shell.ColumnDefinitions[1].Width = new GridLength(subRight ? 0 : GAP);
        shell.ColumnDefinitions[3].Width = new GridLength(subRight ? GAP : 0);
        shell.ColumnDefinitions[4].Width = new GridLength(subRight ? W_DETAIL : 0);
        Grid.SetColumn(subFrame, subRight ? 4 : 0);

        // 子列宽度会在 232 / 306 之间变，但列宽恒定 —— 让它贴着主列那一侧对齐，
        // 变窄时向屏幕外侧收，而不是把窗口拽宽。
        subFrame.HorizontalAlignment = subRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    /// <summary>
    /// 摆子列：高度与垂直偏移。**只动 subFrame 的 Height/Margin**，窗口几何不变，
    /// 所以切页时不会触发任何 SetWindowPos（这是"闪一下"的根治点）。
    /// </summary>
    void ApplySubLayout()
    {
        if (!HasSub)
        {
            subFrame.Visibility = Visibility.Collapsed;
            anchorY = 0;
            return;
        }

        subFrame.Visibility = Visibility.Visible;

        // 高度：先解除上一次的限高再量内容（否则 Measure 会受上一轮 MaxHeight 影响，越量越矮）；
        // 量的是 subRoot 而不是 subScroll —— ScrollViewer 的 DesiredSize 会带上一次的 viewport 残值。
        subScroll.MaxHeight = double.PositiveInfinity;
        subRoot.Measure(new Size(Math.Max(40, subWidth - INNER - 6), double.PositiveInfinity));

        double boxH = geomLock ? lockH : mainH;          // ShowPanel 之前还没锁，先用主列高兜底

        // 高度 = 内容高（Windows 原版行为：子菜单正好包住内容，底部不留空白），
        // 只在两种情况下才不按内容走：
        //   ① 内容超高 → 封顶在「主列 × 0.9」，剩余交给 ScrollViewer（这是 90% 的唯一用途）；
        //   ② 窗口内放不下（父项很靠下 / 屏幕矮）→ 压到窗口可用高。
        // 【不要一律撑到 90%】那会让内容少的页（设置、商店）底部空出一大块深色，
        // 看着像"菜单没画完"。子菜单高度短一点是正常的，Windows 也是短的。
        double top = SHADOW + 1, bottom = boxH - SHADOW;
        double content = Math.Ceiling(subRoot.DesiredSize.Height) + INNER;
        double cap = Math.Min(mainH * SUB_MAX, Math.Max(44, bottom - top));
        double h = Math.Min(Math.Max(content, 44), cap);

        double y = anchorY + SHADOW + 1;
        // 太靠下的父项：宁可整体上移，也不让子列探出窗口底（探出去会被窗口边界裁掉）
        if (y + h > bottom) y = Math.Max(top, bottom - h);

        subFrame.Height = h;
        subFrame.Width = subWidth;      // 列宽恒定（306），子列按内容宽（232/306）贴主列那一侧
        subFrame.Margin = new Thickness(0, y, 0, 0);
        subScroll.MaxHeight = h - INNER;

        if (Environment.GetEnvironmentVariable("KS_DBG_LAYOUT") == "1")
            Log.Write($"DBG sub page={page} y={y:0} h={h:0} mainH={mainH:0} boxH={boxH:0} " +
                      $"anchorY={anchorY:0} content={subRoot.DesiredSize.Height:0}");
    }

    /// <summary>按锁定的包围盒算窗口矩形（纯计算，不改任何状态）。</summary>
    (double left, double top, double w, double h) ComputeRect()
    {
        var wa = SystemParameters.WorkArea;
        double w = lockW;
        double left = subRight ? mainPos.X : mainPos.X - (w - W_MAIN);
        if (left + w > wa.Right) left = wa.Right - w;
        if (left < wa.Left) left = wa.Left;

        double top = mainPos.Y;
        if (top + lockH > wa.Bottom) top = wa.Bottom - lockH;
        if (top < wa.Top) top = wa.Top;

        return (Math.Round(left), Math.Round(top), w, lockH);
    }

    /// <summary>
    /// 一次性把窗口放到目标位置与尺寸。
    ///
    /// 【为什么要有 _pinRect】
    /// 改窗口几何是"四次属性赋值"（Width / Height / Left / Top），而 WPF 每赋一个值
    /// 就立刻发一次 SetWindowPos，且**用其它三个属性的旧值**凑矩形。于是设 Width 的那一
    /// 次是"新宽度 + 旧位置"，窗口会先在旧位置闪一下、等 Left 赋完才跳到新位置——这就是
    /// "子菜单闪一下再跑到左边"的根源（软件渲染下内容重绘滞后，这一帧肉眼可见）。
    ///
    /// 修法：在改动期间挂一个 WM_WINDOWPOSCHANGING 钩子，把这一串调用里的矩形**全部改写
    /// 成同一个最终矩形**，于是中间那几次都是"原位原尺寸"，屏幕上只出现最终态一次。
    /// </summary>
    void PlaceWindow(double left, double top, double width, double height)
    {
        EnsureHook();

        var (sx, sy) = Scale();

        // 先落到物理像素，再反算 DIP 喂给 WPF —— 这样 WPF 自己 round 之后与我们完全一致，
        // 不会因为 0.5px 的差异再补一次 SetWindowPos。
        int px = (int)Math.Round(left * sx), py = (int)Math.Round(top * sy);
        int pw = Math.Max(1, (int)Math.Round(width * sx));
        int ph = Math.Max(1, (int)Math.Round(height * sy));

        _pinX = px; _pinY = py; _pinW = pw; _pinH = ph;
        _pin = true;
        if (Environment.GetEnvironmentVariable("KS_DBG_LAYOUT") == "1")
            Log.Write($"DBG place {DateTime.Now:HH:mm:ss.fff} {px},{py} {pw}x{ph} page={page}");
        try
        {
            Width = pw / sx;
            Height = ph / sy;
            Left = px / sx;
            Top = py / sy;
        }
        catch (Exception ex) { Log.Write("窗口定位失败：" + ex.Message); }
        finally { _pin = false; }

        // 兜底：若 WPF 判断"属性没变"而没发调用，这里补齐（值已一致时是空操作）
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            try { SetWindowPos(hwnd, IntPtr.Zero, px, py, pw, ph, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER); }
            catch { }
        }
    }

    // ---- WM_WINDOWPOSCHANGING 改写（见 PlaceWindow 注释）----
    bool _pin;
    int _pinX, _pinY, _pinW, _pinH;
    HwndSource? _src;

    const int WM_WINDOWPOSCHANGING = 0x0046;
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    void EnsureHook()
    {
        if (_src != null) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;   // 未 Show 时会顺手创建句柄
            if (hwnd == IntPtr.Zero) return;
            _src = HwndSource.FromHwnd(hwnd);
            _src?.AddHook(WndProc);
        }
        catch (Exception ex) { Log.Write("挂窗口钩子失败：" + ex.Message); }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_pin && msg == WM_WINDOWPOSCHANGING)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            wp.x = _pinX; wp.y = _pinY; wp.cx = _pinW; wp.cy = _pinH;
            wp.flags &= ~(SWP_NOSIZE | SWP_NOMOVE);
            Marshal.StructureToPtr(wp, lParam, false);
        }
        return IntPtr.Zero;
    }

    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();

    /// <summary>
    /// 本屏 DPI 缩放。**不要用 VisualTreeHelper.GetDpi 做窗口还没 Show 时的换算**——
    /// 那时窗口尚未关联 HwndSource，它返回 1.0，于是 GetCursorPos 的物理像素被当成 DIP，
    /// 菜单会被"越界钳位"一把推到屏幕最右边（和当初"锁在右下角"是同一个坑的另一个入口）。
    /// 这里直接问窗口句柄；句柄也没有时退回系统 DPI。
    /// </summary>
    (double sx, double sy) Scale()
    {
        uint d = 0;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) d = GetDpiForWindow(hwnd);
            if (d == 0) d = GetDpiForSystem();
        }
        catch { d = 0; }

        if (d == 0)
        {
            var vi = VisualTreeHelper.GetDpi(this);
            return (vi.DpiScaleX > 0.01 ? vi.DpiScaleX : 1, vi.DpiScaleY > 0.01 ? vi.DpiScaleY : 1);
        }
        return (d / 96.0, d / 96.0);
    }

    /// <summary>
    /// 主列落点：只在弹出时算一次，之后整个面板会话不再移动。
    /// 水平方向要按**整个窗口宽**留出子列的位置，否则窗口一钳位主列就跟着挪。
    /// </summary>
    void AnchorAtCursor()
    {
        var wa = SystemParameters.WorkArea;
        var raw = TrayHost.CursorPos();                       // 物理像素
        var (sx, sy) = Scale();
        double cx = raw.X / sx, cy = raw.Y / sy;              // → DIP

        // 布局自测：KS_ANCHOR="x,y"（物理像素）把落点钉死，用来验证"屏幕底部"这类
        // 平时要把鼠标挪到托盘才复现的分支。只在显式设置时生效。
        var ap = Environment.GetEnvironmentVariable("KS_ANCHOR");
        if (!string.IsNullOrEmpty(ap))
        {
            var xy = ap.Split(',');
            if (xy.Length == 2 && double.TryParse(xy[0], out var ax) && double.TryParse(xy[1], out var ay))
            {
                cx = ax / sx;
                cy = ay / sy;
            }
        }

        double h = mainH > 1 ? mainH : 400;
        double ww = WindowW;

        if (subRight)
        {
            // 子列在右：窗口左边界 = 主列左边界
            if (cx + ww > wa.Right) cx = Math.Max(wa.Left, wa.Right - ww);
            if (cx < wa.Left) cx = wa.Left;
        }
        else
        {
            // 子列在左：窗口左边界 = 主列左边界 − (窗口宽 − 主列宽)，不能让这块跑出屏幕左边界
            double left = cx - (ww - W_MAIN);
            if (left < wa.Left) cx = wa.Left + (ww - W_MAIN);
            if (cx + W_MAIN > wa.Right) cx = wa.Right - W_MAIN;
        }

        double my = cy;
        if (my + h > wa.Bottom) my = cy - h;                  // 向上展开
        if (my < wa.Top) my = wa.Top;
        if (my + h > wa.Bottom) my = wa.Bottom - h;

        mainPos = new Point(cx, my);
        posReady = true;

        if (Environment.GetEnvironmentVariable("KS_DBG_LAYOUT") == "1")
            Log.Write($"DBG anchor raw={raw.X},{raw.Y} scale={sx:0.##} cx={cx:0} cy={cy:0} " +
                      $"main={cx:0},{my:0} mainH={mainH:0} subRight={subRight} " +
                      $"wa={wa.Left:0},{wa.Top:0}-{wa.Right:0},{wa.Bottom:0}");
    }

    /// <summary>
    /// 子列往哪边弹。**默认向左**：菜单一般都贴着屏幕右侧弹出，左侧有空间才是常态。
    /// 判断用**最大子列宽**（W_DETAIL），这样切到详情页时不会因为"宽了 74px"而换边 ——
    /// 换边就是一次窗口横移，那正是以前看到的"闪一下再跑到另一边"。
    /// </summary>
    void PickSide()
    {
        var wa = SystemParameters.WorkArea;
        var (sx, _) = Scale();
        double cx = TrayHost.CursorPos().X / sx;

        double need = W_DETAIL + GAP;
        double roomLeft = cx - wa.Left;
        double roomRight = wa.Right - (cx + W_MAIN);
        subRight = roomLeft < need && roomRight >= roomLeft;
    }

    /// <summary>
    /// 兼容旧调用点：重新摆一次窗口。**切页不再调用它** —— 切页只换内容、不动几何。
    /// 仅在"内容撑破锁定包围盒"的特殊场合（如窗口被 DPI 变化重建）兜底使用。
    /// </summary>
    void ApplyLayout()
    {
        if (!posReady) return;
        var (left, top, w, h) = ComputeRect();
        PlaceWindow(left, top, w, h);
        mainPos = new Point(left + (subRight ? 0 : w - W_MAIN), top);
    }


    bool HasSub => !string.IsNullOrEmpty(page);

    // ================= 构建 =================

    /// <summary>全量重建（只在打开面板时调用）。</summary>
    void Build()
    {
        rows.Clear();
        mainRows.Clear();
        mainByKey.Clear();
        mainRoot.Children.Clear();
        subRoot.Children.Clear();
        hLv = hHp = hLine3 = hMoney = btnLabel = null;
        hpHolder = hpFill = expHolder = expFill = null;

        BuildMainColumn();

        if (HasSub)
        {
            subFrame.Visibility = Visibility.Visible;
            BuildSubColumn();
        }
        else subFrame.Visibility = Visibility.Collapsed;
    }

    /// <summary>取「当前展开项」在主列里的顶边 Y，用于让子列顶边对齐父项。</summary>
    void UpdateAnchorY()
    {
        anchorY = 0;
        if (!HasSub) return;
        if (!mainByKey.TryGetValue(PageKey(page), out var r)) return;
        try
        {
            var p = r.Box.TranslatePoint(new Point(0, 0), mainRoot);
            if (p.Y >= 0) anchorY = p.Y;
        }
        catch { anchorY = 0; }
    }

    /// <summary>
    /// 切页：主列控件全部复用，只重建子列。
    /// **不碰窗口几何**（不动 Left/Top/Width/Height）—— 窗口尺寸在弹出时已经撑到
    /// 「最大子列」的包围盒，这里只改 subFrame 的高度与偏移，属于 WPF 内部布局，
    /// 不会发 SetWindowPos，屏幕上也就不会有那一帧空白。
    /// </summary>
    void SwitchPage(string target, bool force = false)
    {
        string next = (!force && page == target) ? "" : target;
        if (next == page) return;
        page = next;

        UpdateAnchorY();
        subFrame.Visibility = HasSub ? Visibility.Visible : Visibility.Collapsed;

        subRoot.Children.Clear();
        rows.Clear();
        rows.AddRange(mainRows);
        if (HasSub) BuildSubColumn();

        ApplySubLayout();
        UpdateLayout();
        RefreshAll();
    }

    static string PageKey(string page) => page switch
    {
        "layerInput" => "层数",          // 输入页没有独立主项，挂在「层数」名下（子列锚点也用它）
        _ => page switch
        {
            "claim" => "领取",
            "layers" => "层数",
            "detail" => "角色",
            "stats" => "统计",           // 统计是独立主项，子列要锚到它自己那一行
            "ear" => "本命器",
            "shop" => "商店",
            "settings" => "设置",
            _ => ""
        }
    };

    void RebuildSub()
    {
        // 子列内部变化（买了/强化/跳层）：只重建子列，主列控件与引用全部复用。
        // 同样不碰窗口几何：子列自己变高变矮，窗口包围盒是固定的。
        subRoot.Children.Clear();
        rows.Clear();
        rows.AddRange(mainRows);
        BuildSubColumn();
        ApplySubLayout();
        UpdateLayout();
        RefreshAll();
    }

    void BuildMainColumn()
    {
        var s = Game.I;

        // ---- 头部 5 行：等级·经验 / 经验条 / 生命数值 + 生命条 / 层数·状态 / 三货币 ----
        var head = new StackPanel { Margin = new Thickness(PAD_L, 4, PAD_R, 0) };

        hLv = Text("", FS_HEAD, Fg);

        // 经验条（满宽、青色）
        expFill = new Border
        {
            Height = 3, CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Accent,
        };
        expHolder = new Border
        {
            Height = 3, CornerRadius = new CornerRadius(2), Background = Track,
            Margin = new Thickness(0, 4, 0, 0),
            Child = expFill,
        };

        // 生命：左边数值、右边进度条同排（省一行高度）
        hHp = Text("", FS_TINY, Dim);
        hpFill = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = HpFill,
        };
        hpHolder = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2), Background = Track,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = hpFill,
        };
        var hpRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        hpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hpRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(hHp, 0);
        Grid.SetColumn(hpHolder, 1);
        hpRow.Children.Add(hHp);
        hpRow.Children.Add(hpHolder);

        hLine3 = Text("", FS_HEAD, Fg);
        hLine3.Margin = new Thickness(0, 4, 0, 0);
        hMoney = Text("", FS_HINT, Dim);
        hMoney.Margin = new Thickness(0, 1, 0, 0);

        head.Children.Add(hLv);
        head.Children.Add(expHolder);
        head.Children.Add(hpRow);
        head.Children.Add(hLine3);
        head.Children.Add(hMoney);
        mainRoot.Children.Add(head);

        // ---- 开始 / 暂停 ----
        var btnRow = new Border
        {
            Height = 22,
            Margin = new Thickness(4, 6, 4, 2),
            CornerRadius = new CornerRadius(4),
            Background = BtnBg,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        btnLabel = Text("", FS_BTN, Accent);
        btnLabel.HorizontalAlignment = HorizontalAlignment.Center;
        btnRow.Child = btnLabel;
        btnRow.MouseEnter += (_, _) => btnRow.Background = BgHover;
        btnRow.MouseLeave += (_, _) => btnRow.Background = BtnBg;
        btnRow.MouseLeftButtonUp += (_, _) =>
        {
            Game.I.TogglePause();
            RefreshAll();
        };
        mainRoot.Children.Add(btnRow);
        var btnTick = new Row { Tick = () =>
        {
            bool p = Game.I.S.Paused;
            btnLabel!.Text = p ? "▶  开始战斗" : "❚❚  暂停";
            btnLabel.Foreground = p ? Accent : Fg;
        } };
        rows.Add(btnTick);
        mainRows.Add(btnTick);

        mainRoot.Children.Add(Divider());

        // ---- 8 项 ----
        MainItem("领取", "claim");
        MainItem("层数", "layers");
        MainItem("角色", "detail");
        MainItem("本命器", "ear");
        MainItem("商店", "shop");
        MainItem("统计", "stats");
        MainItem("设置", "settings");

        var exit = RowMake("退出", null, () => { Game.I.Save(); Application.Current.Shutdown(); }, warn: true);
        mainRoot.Children.Add(exit.Box);
        rows.Add(exit);
        mainRows.Add(exit);

        mainRoot.Children.Add(BottomSpacer());   // 底部 4px 留白
    }

    /// <summary>列底部留白：菜单最后一行不贴着边框。</summary>
    static Border BottomSpacer() => new() { Height = BOTTOM_PAD };

    void MainItem(string key, string target)
    {
        var r = RowMake(key, null, () => SwitchPage(target));
        mainByKey[key] = r;
        rows.Add(r);
        mainRows.Add(r);
        mainRoot.Children.Add(r.Box);

        // 悬停即展开（系统右键菜单的行为）
        r.Box.MouseEnter += (_, _) =>
        {
            if (_keepOpen && page == target) return;
            hoverWant = target;
            hover.Stop();
            hover.Start();
        };
        r.Box.MouseLeave += (_, _) =>
        {
            if (hoverWant == target) hoverWant = "";
        };

        // 当前展开项保持高亮：**一次只亮一项**，亮的就是 page 对应的那个主项。
        r.Active = () => IsMainActive(target);
        r.Tick = () => Paint(r);
    }

    /// <summary>
    /// page 是否就是主列里 key=target 的那一项。**只认同名项，不做"同族一起亮"**：
    /// 「角色」和「统计」是两个独立主项，打开统计页时只有「统计」亮。
    /// 唯一例外是「层数」——前往层数是一个输入子页，没有主项，挂在「层数」名下。
    /// </summary>
    bool IsMainActive(string target) => target switch
    {
        "layers" => page == "layers" || page == "layerInput",
        _ => page == target,
    };

    void BuildSubColumn()
    {
        switch (page)
        {
            case "claim": subWidth = W_SUB; SubTitle("领取"); BuildClaim(); break;
            case "layers": subWidth = W_SUB; SubTitle("层数"); BuildLayerMenu(); break;
            case "layerInput": subWidth = W_SUB; SubTitle("前往层数"); BuildLayerInput(); break;
            case "ear": subWidth = W_SUB; SubTitle("本命四器"); BuildEar(); break;
            case "shop": subWidth = W_SUB; SubTitle("商店"); BuildShop(); break;
            case "settings": subWidth = W_SUB; SubTitle("设置"); BuildSettings(); break;
            default: subWidth = W_DETAIL; SubTitle("详情"); BuildDetail(); break;   // detail / stats
        }
        subRoot.Children.Add(BottomSpacer());   // 底部 4px 留白
    }

    void SubTitle(string t)
    {
        var tb = new TextBlock
        {
            Text = t,
            FontSize = FS_TITLE,
            FontFamily = Font,
            Foreground = Accent,
            Margin = new Thickness(PAD_L, 6, PAD_R, 3),
        };
        subRoot.Children.Add(tb);
    }

    // ================= 子页 =================

    void BuildClaim()
    {
        var s = Game.I;
        if (s.S.Bags.Count == 0)
        {
            subRoot.Children.Add(Grey($"暂无可领金币袋·每 {Game.BagKeys} 次敲击掉一袋"));
            return;
        }
        var r = RowMake($"金币袋 ×{s.S.Bags.Count}", $"+{Fmt.Large(s.BagSum)}",
            () => { s.ClaimBags(); RebuildSub(); });
        Add(r);
        Add(RowMake("一键全领", null, () => { s.ClaimBags(); RebuildSub(); }));
    }

    void BuildLayerMenu()
    {
        var s = Game.I;
        Add(RowMake($"当前 第 {s.Layer} 层", $"本轮最高 {s.MaxLayer}", null, enabled: false));
        Add(RowMake("前往层数…", null, () => SwitchPage("layerInput", force: true)));

        Add(RowMake("↑ 上 10 层", null, () => { s.Step(10); RebuildSub(); }, enabled: s.Layer < s.MaxLayer));
        Add(RowMake("↓ 下 10 层", null, () => { s.Step(-10); RebuildSub(); }, enabled: s.Layer > 1));

        Add(RowMake("前往已通最深层", $"第 {s.MaxLayer} 层",
            () => { s.JumpTo(s.MaxLayer); RebuildSub(); }, enabled: s.MaxLayer > s.Layer));

        Add(RowMake($"自动下潜 [{(s.S.AutoDescend ? "开" : "关")}]", null,
            () => { s.S.AutoDescend = !s.S.AutoDescend; RebuildSub(); }));

        var lockRow = RowMake($"锁定本层 [{(s.S.LockLayer ? "开" : "关")}]", null,
            () => { s.S.LockLayer = !s.S.LockLayer; RebuildSub(); });
        lockRow.Tick = () => lockRow.Label.Text = $"锁定本层 [{(Game.I.S.LockLayer ? "开" : "关")}]";
        rows.Add(lockRow);
        Add(lockRow);
    }

    void BuildLayerInput()
    {
        var s = Game.I;
        var box = new TextBox
        {
            Text = s.Layer.ToString(),
            Margin = new Thickness(PAD_L, 2, PAD_R, 4),
            Height = 19,
            FontSize = FS_ITEM,
            FontFamily = Font,
            Background = Track,
            Foreground = Fg,
            BorderBrush = Line,
            CaretBrush = Accent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(5, 0, 5, 0),
        };
        subRoot.Children.Add(box);

        void Jump()
        {
            if (int.TryParse(box.Text.Trim(), out int v)) s.JumpTo(v);
            SwitchPage("layers", force: true);
        }

        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Jump(); };
        Add(RowMake("确定跳转", $"1 ~ {s.MaxLayer}", Jump));

        Dispatcher.BeginInvoke(new Action(() => { box.Focus(); box.SelectAll(); }), DispatcherPriority.Input);
    }

    // ---- 本命四器（5.3）----

    static readonly string[] EarNames = { "墨刃", "墨衣", "墨眸", "墨印" };
    static readonly string[] EarIcons = { "ear-blade", "ear-robe", "ear-eye", "ear-seal" };

    void BuildEar()
    {
        // 单次强化次数档（点击轮换 1 → 10 → 50 → 100 → max）；空图标名 = 只占位保持左对齐
        var batch = RowMake($"单次升级 {BatchText(Game.I.S.EnhBatch)}", "点击切换", () =>
        {
            Game.I.S.EnhBatch = NextBatch(Game.I.S.EnhBatch);
            RebuildSub();
        }, icon: "");
        batch.Tick = () => batch.Label.Text = $"单次升级 {BatchText(Game.I.S.EnhBatch)}";
        rows.Add(batch);
        Add(batch);

        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            int lv = Game.I.EnhLevel(idx);
            int k = AffordableCount(idx);
            var r = RowMake(EarLabel(idx, lv, k), EarHint(idx, k),
                () =>
                {
                    if (AffordableCount(idx) > 0) Game.I.EnhanceByBatch(idx);
                    RebuildSub();               // 点了但墨晶不够：重建后自然变成置灰 + 墨晶不足
                },
                enabled: k > 0, icon: EarIcons[i]);
            r.Tick = () =>
            {
                int L = Game.I.EnhLevel(idx);
                int n = AffordableCount(idx);
                r.Label.Text = EarLabel(idx, L, n);
                if (r.Hint != null)
                {
                    r.Hint.Text = EarHint(idx, n);
                    r.Hint.Foreground = n > 0 ? Dim : Warn;
                }
                r.Label.Foreground = n > 0 ? Fg : Disabled;
            };
            Add(r);

            var eff = RowMake(EffectText(idx, lv), null, null, enabled: false, small: true);
            eff.Tick = () => eff.Label.Text = EffectText(idx, Game.I.EnhLevel(idx));
            rows.Add(eff);
            Add(eff);
        }
    }

    static string EarLabel(int idx, int lv, int gain)
        => gain > 0 ? $"{EarNames[idx]} +{lv} → +{lv + gain}" : $"{EarNames[idx]} +{lv}";

    static string BatchText(int b) => b < 0 ? "max" : $"×{b}";

    static int NextBatch(int b) => b switch { 1 => 10, 10 => 50, 50 => 100, 100 => -1, _ => 1 };

    /// <summary>
    /// 当前档位下这一件实际能升几级。
    /// **倍率是硬要求**：×100 就必须一次付得起 100 级，付不起返回 0
    /// （按钮置灰、右侧提示「墨晶不足」），绝不允许"能升几级就升几级"悄悄降档。
    /// 只有撞到等级上限时才允许少升（×100 但只剩 3 级空间 → 升 3 级）。
    /// </summary>
    static int AffordableCount(int idx)
    {
        var s = Game.I;
        int room = Limits.MaxEnhance - s.EnhLevel(idx);
        if (room <= 0) return 0;

        int cap = s.MaxEnhanceable(idx);                                   // 墨晶最多够几级
        int batch = s.S.EnhBatch < 0 ? int.MaxValue : (s.S.EnhBatch <= 0 ? 1 : s.S.EnhBatch);
        if (batch == int.MaxValue) return cap;                             // max：能升多少升多少

        int want = Math.Min(batch, room);
        return cap >= want ? want : 0;
    }

    /// <summary>右侧提示：够就报总价，不够就明说墨晶不足（并顺带告诉最多够升几级）。</summary>
    static string EarHint(int idx, int gain)
    {
        var s = Game.I;
        if (gain > 0) return $"{Fmt.Large(s.EnhCostTotal(idx, gain))} 墨晶";
        if (s.EnhLevel(idx) >= Limits.MaxEnhance) return "已满级";

        int cap = s.MaxEnhanceable(idx);
        return cap > 0 ? $"墨晶不足 · 够×{cap}" : "墨晶不足";
    }

    static string EffectText(int idx, int lv) => idx switch
    {
        0 => $"　　攻击 {Fmt.Small(Big.Pow(1.06, lv) * 10)} → {Fmt.Small(Big.Pow(1.06, lv + 1) * 10)}",
        1 => $"　　生命 {Fmt.Small(Big.Pow(1.06, lv) * 80)} → {Fmt.Small(Big.Pow(1.06, lv + 1) * 80)}",
        2 => $"　　暴击 {Fmt.Pct(0.02 + 0.0015 * lv, 2)} → {Fmt.Pct(0.02 + 0.0015 * (lv + 1), 2)}",
        _ => $"　　防御 {Fmt.Small(Big.Pow(1.06, lv) * 4)} → {Fmt.Small(Big.Pow(1.06, lv + 1) * 4)}",
    };

    void BuildShop()
    {
        var s = Game.I;
        var info = RowMake($"墨晶 {Fmt.Large(s.Ink)} 颗", $"{Fmt.Large(s.Gold)} 金币", null, enabled: false);
        info.Tick = () =>
        {
            info.Label.Text = $"墨晶 {Fmt.Large(Game.I.Ink)} 颗";
            if (info.Hint != null) info.Hint.Text = $"{Fmt.Large(Game.I.Gold)} 金币";
        };
        rows.Add(info);
        Add(info);

        Add(RowMake("买墨晶 ×1", $"{Game.InkPrice} 金币", () => { Game.I.BuyInk(1); RebuildSub(); },
            enabled: s.Gold.CompareTo(Big.Of(Game.InkPrice)) >= 0, icon: "cur-inkcrystal"));
        Add(RowMake("买墨晶 ×10", $"{Game.InkPrice * 10} 金币", () => { Game.I.BuyInk(10); RebuildSub(); },
            enabled: s.Gold.CompareTo(Big.Of(Game.InkPrice * 10)) >= 0, icon: ""));

        int shortfall = s.InkShortfall(0);
        Add(RowMake("补到够升墨刃",
            shortfall > 0 ? $"差 {shortfall} 颗 · {shortfall * Game.InkPrice} 金币" : "已够",
            () => { Game.I.FillToNext(0); RebuildSub(); }, enabled: shortfall > 0, icon: ""));

        Add(RowMake("开墨匣 ×1", "250 金币", () => { Game.I.OpenBox(1); RebuildSub(); },
            enabled: s.Gold.CompareTo(Big.Of(250)) >= 0, icon: "box-ink"));
        Add(RowMake("开墨匣 ×10", "2,500 金币", () => { Game.I.OpenBox(10); RebuildSub(); },
            enabled: s.Gold.CompareTo(Big.Of(2500)) >= 0, icon: ""));
    }

    void BuildSettings()
    {
        var s = Game.I;
        Add(RowMake("打开教程", "HTML", () => { HidePanel(); TutorialWindow.Open(); }));

        Add(RowMake($"敲击监听 [{(s.S.ListenKeys ? "开" : "关")}]", null,
            () => { Game.I.S.ListenKeys = !Game.I.S.ListenKeys; RebuildSub(); }));

        Add(RowMake($"自动回退 [{(s.S.AutoRetreat ? "开" : "关")}]", null,
            () => { Game.I.S.AutoRetreat = !Game.I.S.AutoRetreat; RebuildSub(); }));

        Add(RowMake($"战斗速度（{s.S.BattleSeconds}s）", "点击切换", () =>
        {
            Game.I.S.BattleSeconds = Game.I.S.BattleSeconds switch { 1 => 2, 2 => 3, 3 => 5, _ => 1 };
            RebuildSub();
        }));

        Add(RowMake("手动存档", null, () => { Game.I.Save(); Flash("已保存"); }));
        Add(RowMake("打开存档目录", null, () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Game.Dir) { UseShellExecute = true }); }
            catch { }
        }));
    }

    void BuildDetail()
    {
        var s = Game.I;
        bool statsOnly = page == "stats";

        if (!statsOnly)
        {
            SectionRow("角色");
            DetailRow("等级", () => $"Lv {Game.I.Level} · 经验 {Fmt.Large(Game.I.Exp)} / {Fmt.Large(Game.I.ExpNeed)}");
            DetailRow("生命", () => $"{Fmt.Large(Game.I.Hp)} / {Fmt.Large(Game.I.HpMax)}");
            DetailRow("攻击", () => Fmt.Small(Game.I.PlayerAtk));
            DetailRow("防御", () => Fmt.Small(Game.I.PlayerDef));
            DetailRow("暴击", () => $"{Fmt.Pct(Game.I.CritRate, 2)} · 暴伤 +{Fmt.Pct(Game.I.CritDmg, 0)}");
            DetailRow("墨根", () => $"{Game.I.S.Root} / {Limits.MaxSoul}（×{Fmt.Large(Big.Pow(1.06, Game.I.S.Root))}）");
            DetailRow("层数", () => $"第 {Game.I.Layer} 层 · 最高 {Game.I.MaxLayer} · 第 {Game.I.S.Cycle} 轮");
            // 状态带倒计时：这一行每秒随 LiveRefresh 重读，所以修整中的秒数会自己往下走。
            DetailRow("状态", () => Game.I.S.Paused ? "已暂停"
                                   : Game.I.Resting ? "力竭休整 " + Fmt.Time(Game.I.RestUntil - DateTime.UtcNow)
                                   : "战斗中");

            var g = s.RebirthGain;
            bool confirm = (DateTime.UtcNow - rebirthClickAt).TotalSeconds < 10;
            string label = confirm ? $"⚠ 确认转生（+{g} 墨根）" : $"转生（+{g} 墨根）";
            var rb = RowMake(label, s.CanRebirth ? "重置至第 1 层" : "需达 50 层", () =>
            {
                if (!Game.I.CanRebirth) return;
                if ((DateTime.UtcNow - rebirthClickAt).TotalSeconds >= 10) { rebirthClickAt = DateTime.UtcNow; RebuildSub(); return; }
                Game.I.Rebirth();
                RebuildSub();
            }, enabled: s.CanRebirth, warn: true);
            rb.Tick = () =>
            {
                int gain = Game.I.RebirthGain;
                bool c = (DateTime.UtcNow - rebirthClickAt).TotalSeconds < 10;
                rb.Label.Text = c ? $"⚠ 确认转生（+{gain} 墨根）" : $"转生（+{gain} 墨根）";
                if (rb.Hint != null) rb.Hint.Text = Game.I.CanRebirth ? "重置至第 1 层" : "需达 50 层";
            };
            rows.Add(rb);
            Add(rb);
        }

        SectionRow("统计");
        DetailRow("累计敲击", () => Game.I.S.TotalKeys.ToString("N0"));
        DetailRow("累计击杀", () => Game.I.S.TotalKills.ToString("N0"));
        DetailRow("累计在线", () => $"{Game.I.S.PlaySeconds / 3600:0.0} 小时");
        DetailRow("转生次数", () => Game.I.S.Rebirths.ToString());
        DetailRow("开匣次数", () => Game.I.S.BoxesOpened.ToString("N0"));

        SectionRow("概率公示");
        StaticRow("金币袋", "碎 70% ×1 · 满 25% ×2.5 · 沉 5% ×6");
        StaticRow("守层者", "必掉墨晶 ×3 · 精英 ×1");
        StaticRow("墨匣（250 金币）", "金币 100/30% · 300/20% · 750/8%");
        StaticRow("", "墨晶 1/25% · 5/10% · 15/5% · 60/2%");
    }

    // ================= 行工厂 =================

    sealed class Row
    {
        public Border Box = null!;
        public TextBlock Label = null!;
        public TextBlock? Hint;
        public Action? Tick;
        public bool Hovered;
        /// <summary>这一项是不是"当前打开的那一页"。只有一项会返回 true。</summary>
        public Func<bool>? Active;
    }

    /// <summary>
    /// 统一的行着色：**唯一**决定底色的地方。
    /// 以前 MouseEnter/Leave 直接把 Background 改成 BgHover/Transparent，会和每秒刷新抢同一个属性，
    /// 谁后跑谁赢 —— 结果是「当前页」那一行的高亮时有时无。现在悬停、移出、刷新都走这里，
    /// 由 Active 状态裁决，不可能互相覆盖。
    /// </summary>
    static void Paint(Row r)
    {
        bool on = r.Active?.Invoke() ?? false;
        r.Box.Background = on ? BgActive : (r.Hovered ? BgHover : Brushes.Transparent);
    }

    Row RowMake(string text, string? hint, Action? click, bool enabled = true, bool warn = false,
                string? icon = null, bool small = false)
    {
        var grid = new Grid { Height = small ? ITEM_H_SM : ITEM_H };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int col = 0;

        if (icon != null)
        {
            var img = new Image
            {
                Source = IconCache.Get(icon),
                Width = ICON, Height = ICON,
                Margin = new Thickness(PAD_L, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            Grid.SetColumn(img, col++);
            grid.Children.Add(img);
        }

        var label = Text(text, small ? FS_TINY : FS_ITEM, enabled ? (warn ? Warn : Fg) : Disabled);
        label.Margin = new Thickness(icon == null ? PAD_L : ICON_GAP, 0, 0, 0);
        Grid.SetColumn(label, col++);
        grid.Children.Add(label);

        var row = new Row { Label = label };
        if (hint != null)
        {
            var h = Text(hint, FS_HINT, Dim);
            h.Margin = new Thickness(8, 0, PAD_R, 0);
            Grid.SetColumn(h, 2);
            grid.Children.Add(h);
            row.Hint = h;
        }

        var box = new Border
        {
            Child = grid,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(3, 0, 3, 0),
            Cursor = enabled && click != null ? Cursors.Hand : Cursors.Arrow,
        };
        row.Box = box;

        if (enabled && click != null)
        {
            box.MouseEnter += (_, _) => { row.Hovered = true; Paint(row); };
            box.MouseLeave += (_, _) => { row.Hovered = false; Paint(row); };
            box.MouseLeftButtonUp += (_, _) => click();
        }
        return row;
    }

    void Add(Row r)
    {
        if (!rows.Contains(r)) rows.Add(r);
        subRoot.Children.Add(r.Box);
    }

    void SectionRow(string t)
    {
        var tb = new TextBlock
        {
            Text = t,
            FontSize = FS_TINY,
            FontFamily = Font,
            Foreground = Dim,
            Margin = new Thickness(PAD_L, 5, PAD_R, 2),
        };
        subRoot.Children.Add(tb);
    }

    /// <summary>详情行：值随每秒刷新（闭包只读数据，不建控件）。</summary>
    void DetailRow(string k, Func<string> val)
    {
        var grid = new Grid { Height = 17, Margin = new Thickness(PAD_L, 0, PAD_R, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var a = Text(k, FS_HINT, Dim);
        var b = Text(val(), FS_HINT, Fg);
        Grid.SetColumn(a, 0); Grid.SetColumn(b, 1);
        grid.Children.Add(a); grid.Children.Add(b);
        subRoot.Children.Add(grid);

        var r = new Row { Box = new Border(), Label = b, Tick = () => b.Text = val() };
        rows.Add(r);
    }

    void StaticRow(string k, string v)
    {
        var grid = new Grid { Height = 15, Margin = new Thickness(PAD_L, 0, PAD_R, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var a = Text(k, FS_TINY, Dim);
        var b = Text(v, FS_TINY, Fg);
        Grid.SetColumn(a, 0); Grid.SetColumn(b, 1);
        grid.Children.Add(a); grid.Children.Add(b);
        subRoot.Children.Add(grid);
    }

    TextBlock Grey(string t)
    {
        var tb = Text(t, FS_HINT, Dim);
        tb.Margin = new Thickness(PAD_L, 5, PAD_R, 8);
        tb.TextWrapping = TextWrapping.Wrap;
        return tb;
    }

    Border Divider()
    {
        var host = new Border { Padding = new Thickness(6, 2, 6, 2), Child = new Border { Height = 1, Background = Line } };
        return host;
    }

    TextBlock Text(string s, double size, Brush brush) => new()
    {
        Text = s,
        FontSize = size,
        FontFamily = Font,
        Foreground = brush,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ================= 刷新 =================

    /// <summary>只改文本，绝不重建控件树。</summary>
    void LiveRefresh()
    {
        if (!IsVisible) { refresh.Stop(); return; }
        RefreshAll();
    }

    public void RefreshAll()
    {
        var s = Game.I;

        if (hLv != null)
        {
            double ep = s.ExpNeed.IsZero ? 0 : Math.Clamp(s.Exp.ToDouble() / s.ExpNeed.ToDouble(), 0, 1);
            hLv.Text = $"Lv {s.Level}   ·   经验 {Math.Min(99, (int)(ep * 100))}%";
            if (expFill != null)
                expFill.Width = Math.Max(0, (W_MAIN - INNER - PAD_L - PAD_R) * ep);
        }

        if (hHp != null)
            hHp.Text = $"生命 {Fmt.Large(s.Hp)} / {Fmt.Large(s.HpMax)}";

        if (hpFill != null && hpHolder != null)
        {
            double pct = s.HpMax.IsZero ? 0 : Math.Clamp(s.Hp.ToDouble() / s.HpMax.ToDouble(), 0, 1);
            // 首次刷新时可能还没排版（ActualWidth=0），退化成整宽估算，下一拍自然修正
            double full = hpHolder.ActualWidth;
            if (full < 1) full = W_MAIN - INNER - PAD_L - PAD_R - 62;
            hpFill.Width = Math.Max(0, full * pct);
            hpFill.Background = s.Resting ? HpRest : pct < 0.3 ? Warn : HpFill;
        }

        if (hLine3 != null)
        {
            // 休整中给倒计时（设计稿 8.2：「休整 2:31」一眼看清还要等多久）
            string st = s.S.Paused ? "已暂停"
                      : s.Resting ? "休整 " + Fmt.Time(s.RestUntil - DateTime.UtcNow)
                      : s.S.LockLayer ? "驻守" : "爬塔";
            hLine3.Text = $"第 {s.Layer} 层   {s.MobIndex}/10   {st}";
            hLine3.Foreground = s.S.Paused ? Dim : Fg;
        }

        if (hMoney != null)
            hMoney.Text = $"金币 {Fmt.Large(s.Gold)}   ·   墨晶 {Fmt.Large(s.Ink)}   ·   墨根 {s.S.Root}";

        for (int i = 0; i < rows.Count; i++)
        {
            try { rows[i].Tick?.Invoke(); }
            catch (Exception ex) { Log.Write("刷新行文本异常：" + ex.Message); }   // 单行失败不影响其余
        }
    }

    void Flash(string msg)
    {
        if (hLine3 == null) return;
        hLine3.Text = msg;
    }
}

/// <summary>图标缓存：每个 ico 只解码一次并冻结，避免反复 IO 与解码。</summary>
internal static class IconCache
{
    static readonly Dictionary<string, ImageSource?> map = new();

    public static ImageSource? Get(string name)
    {
        if (map.TryGetValue(name, out var v)) return v;
        ImageSource? src = null;
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", name + ".ico");
            if (System.IO.File.Exists(path))
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 16;              // 只解 16px，省内存
                bmp.EndInit();
                bmp.Freeze();
                src = bmp;
            }
        }
        catch { src = null; }
        map[name] = src;
        return src;
    }
}
