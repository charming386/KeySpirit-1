using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KeySpirit.Core;
using KeySpirit.UI;

namespace KeySpirit;

/// <summary>
/// 发布配图用的离屏出图工具（`KeySpirit.exe --shot <目录>`）。
///
/// 为什么要有它：GitHub Release 讲新功能只靠文字描述说不清，必须配界面图。
/// 手动截图又依赖桌面环境、鼠标位置、窗口层级，每次发布都要人盯着，不可重复。
/// 这里改成「用固定演示存档 + 逐页渲染成 PNG」：
///   · 不读真实存档（Boot 后立刻覆盖成演示数值），不会把玩家进度带进截图；
///   · 每页一次 RenderTargetBitmap，2 倍像素密度，透明背景，不受遮挡影响；
///   · 子列特意固定放右侧（先把光标挪到屏幕左上角再展开），保证多次出图构图一致。
///
/// 只写 PNG，不写存档、不启动托盘、不进入游戏主循环。
/// </summary>
public static class Shot
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

    /// <summary>页面清单：文件名（不含扩展名）→ 主列 key。空串 = 只展开主列。</summary>
    static readonly (string File, string Page)[] Pages =
    {
        ("01-main",    ""),
        ("02-battle",  "battle"),
        ("03-claim",   "claim"),
        ("04-shop",    "shop"),
        ("05-ear",     "ear"),
        ("06-detail",  "detail"),
        ("07-note",    "detail"),        // 带悬停注释气泡，走 SnapWithNote
    };

    /// <summary>07 那张图解释哪一行（按可见文字模糊匹配）。</summary>
    const string NoteRowKey = "转生";

    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);

        Game.I.Boot();
        Demo();

        // 光标挪到左上角：PickSide 据此把子列放到主列右侧，构图每次都一样。
        try { SetCursorPos(0, 0); } catch { }

        foreach (var (file, page) in Pages)
        {
            var w = new PanelWindow { StartPage = page };
            try
            {
                w.ShowPanel();
                Pump();
                string path = Path.Combine(outDir, file + ".png");
                if (file == "07-note") SnapWithNote(w, path, NoteRowKey);
                else Snap(w, path);
                Console.WriteLine("shot: " + path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("shot failed (" + file + "): " + ex);
            }
            finally
            {
                try { w.HidePanel(); w.Close(); } catch { }
            }
        }

        // 玩家血条三档（绿 / 琥珀 / 红）要并排对比才说得清，单独出三张只截顶部，
        // 由 make_release_shots.py 拼成一张。怪物血条固定红，静态就有。
        var hpMax = Game.I.HpMax;
        foreach (var (name, ratio) in new[] { ("hp-full", 1.00), ("hp-mid", 0.45), ("hp-low", 0.12) })
        {
            var w = new PanelWindow { StartPage = "" };
            try
            {
                Set(Game.I, "Hp", new Big(hpMax.M * ratio, hpMax.E));
                w.ShowPanel();
                Pump();
                string path = Path.Combine(outDir, name + ".png");
                Snap(w, path);
                Console.WriteLine("shot: " + path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("shot failed (" + name + "): " + ex.Message);
            }
            finally
            {
                try { w.HidePanel(); w.Close(); } catch { }
            }
        }
    }

    /// <summary>
    /// 造一个「有信息量」的演示存档：中期练度、有一层正在打、今天敲了一万多下、
    /// 两档每日礼包都还没领、金币够买几批墨晶。数值只用于出图，不落盘。
    /// </summary>
    static void Demo()
    {
        var g = Game.I;
        var s = g.S;

        s.Level = 26;
        s.Layer = 14;
        s.MaxLayer = 14;
        s.Cycle = 1;
        s.EnhBlade = 38;
        s.EnhRobe = 34;
        s.EnhEye = 22;
        s.EnhSeal = 28;
        s.GoldM = 5.0; s.GoldE = 5;       // 500,000 金币：1K 档的买墨晶/开墨匣都买得起
        s.InkM = 2.47; s.InkE = 2;        // 247 墨晶
        s.TotalKeys = 148326;
        s.TotalKills = 1268;
        s.PlaySeconds = 3600 * 7.5;
        s.DailyDate = DateTime.Now.ToString("yyyy-MM-dd");
        s.DailyKeys = 8000;               // 5,000 档已领、20,000 档还在路上（进度文案才对）
        s.DailyMask = 1;
        s.ShopBatch = 1000;               // 商店截图要能看出「一档控制数量」的效果
        s.Bags.Clear();
        s.Bags.Add(new[] { 1.2, 0 });     // 金币袋 ×2
        s.Bags.Add(new[] { 3.0, 1 });
        s.Paused = false;

        g.EnsureDaily();
        g.ResetFight();

        // 战斗详情要展示「本层第几只」和「掉了一截血」，这两项只有 private set。
        Set(g, "MobIndex", 4);
        typeof(Game).GetMethod("SpawnMob", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(g, null);
        Set(g, "MobHp", g.MobHpMax * 0.63);
    }

    static void Set(Game g, string prop, object value)
    {
        var p = typeof(Game).GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        p!.SetValue(g, value);
    }

    /// <summary>让 Dispatcher 把布局/渲染队列跑完，否则拿到的是上一帧的控件树。</summary>
    static void Pump()
    {
        for (int i = 0; i < 3; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    static void Snap(PanelWindow w, string path)
    {
        if (w.Content is not FrameworkElement root) return;
        root.UpdateLayout();

        // 行内小图标是 14px 的像素图，2 倍渲染时默认的双线性插值会把它糊成一团墨点。
        // 出图时改成最近邻，放大后是清楚的方块（真实 1x 屏幕上看不出差别，只影响配图观感）。
        SetNearest(root);

        double cw = root.ActualWidth > 0 ? root.ActualWidth : w.Width;
        double ch = root.ActualHeight > 0 ? root.ActualHeight : w.Height;
        const double Scale = 2;                       // 2 倍图，README 里缩放显示也清晰
        int pw = Math.Max(1, (int)Math.Ceiling(cw * Scale));
        int ph = Math.Max(1, (int)Math.Ceiling(ch * Scale));

        var bmp = new RenderTargetBitmap(pw, ph, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bmp.Render(root);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>
    /// 带悬停注释气泡的一张图。气泡是独立顶层窗口（WS_EX_NOACTIVATE + TRANSPARENT），
    /// RenderTargetBitmap 拍不到，所以分成两半：面板与气泡各自真实渲染，再按行位置拼回来。
    /// 注释文字直接读该行的 Note 字段，图里的文案与代码永远一致。
    /// </summary>
    static void SnapWithNote(PanelWindow w, string path, string rowKeyword)
    {
        string? note = w.RowNoteOf(rowKeyword);
        if (note == null) { Snap(w, path); return; }      // 行没挂注释就退化成普通截图

        if (w.Content is not FrameworkElement root) return;
        root.UpdateLayout();
        SetNearest(root);

        const double Scale = 2;
        const double Gap = 8;                             // 与 NoteWindow.GAP 一致

        // 窗口与面板背景都是铺满的（背景刷透明但几何撑满），几何边界等于窗口大小。
        // 要贴紧内容只能按 alpha 实际像素裁，否则气泡会被顶到空白区外，中间空出几十像素。
        double pwDip = root.ActualWidth, phDip = root.ActualHeight;
        var (panel, panelOffX, panelOffY) = CropAlpha(Render(root, pwDip, phDip, Scale));
        double cw0 = panel.PixelWidth / Scale, ch0 = panel.PixelHeight / Scale;

        var noteEl = NoteWindow.BuildVisual(note);
        var (bubble, _, _) = CropAlpha(Render(noteEl, noteEl.ActualWidth, noteEl.ActualHeight, Scale));
        double nwDip = bubble.PixelWidth / Scale, nhDip = bubble.PixelHeight / Scale;

        // 与行垂直居中，且不出内容上下边（真实运行时 NoteWindow 也会往工作区内顶）
        var r = w.RowRectInRoot(rowKeyword);
        double nyDip = r.IsEmpty ? (ch0 - nhDip) / 2
                                 : (r.Top * Scale - panelOffY) / Scale + r.Height / 2 - nhDip / 2;
        nyDip = Math.Max(0, Math.Min(nyDip, Math.Max(0, ch0 - nhDip)));

        double outW = cw0 + Gap + nwDip;
        double outH = Math.Max(ch0, nyDip + nhDip);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(panel, new Rect(0, 0, cw0, ch0));
            dc.DrawImage(bubble, new Rect(cw0 + Gap, nyDip, nwDip, nhDip));
        }

        var outBmp = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(outW * Scale)),
            Math.Max(1, (int)Math.Ceiling(outH * Scale)),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        outBmp.Render(dv);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(outBmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>把某个视觉按 DIP 尺寸 × 倍率渲染成位图。</summary>
    static RenderTargetBitmap Render(Visual v, double wDip, double hDip, double scale)
    {
        var bmp = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(wDip * scale)),
            Math.Max(1, (int)Math.Ceiling(hDip * scale)),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(v);
        return bmp;
    }

    /// <summary>
    /// 按 alpha 裁掉四周全透明的像素，返回裁后位图与偏移量（像素）。
    /// 合成时偏移量用来把「行在窗口里的坐标」换算到裁后的图上。
    /// </summary>
    static (BitmapSource Bmp, int OffX, int OffY) CropAlpha(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                if (px[row + x * 4 + 3] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                maxY = y;
            }
        }
        if (maxX < 0) return (src, 0, 0);              // 整张全透明，原样返回
        var cropped = new CroppedBitmap(src, new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        cropped.Freeze();
        return (cropped, minX, minY);
    }

    /// <summary>把整棵可视树里的 Image 都设成最近邻缩放。</summary>
    static void SetNearest(DependencyObject node)
    {
        if (node is System.Windows.Controls.Image img)
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++) SetNearest(VisualTreeHelper.GetChild(node, i));
    }
}
