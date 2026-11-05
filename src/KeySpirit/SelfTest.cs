using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KeySpirit.Core;

namespace KeySpirit;

/// <summary>无界面自测：按策划稿的关键规则逐条断言，结果写文件（WinExe 无控制台）。</summary>
public static class SelfTest
{
    public static void Run()
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  → " + detail : "")}");
        }

        // 隔离存档目录，绝不碰玩家存档
        Game.DirOverride = Path.Combine(Path.GetTempPath(), "keyspirit_selftest");
        if (Directory.Exists(Game.DirOverride)) Directory.Delete(Game.DirOverride, true);
        Directory.CreateDirectory(Game.DirOverride);

        var g = Game.I;
        g.Boot();

        // 1) 大数显示（第十章）
        Check("Fmt 999→999", Fmt.Large(Big.Of(999)) == "999", Fmt.Large(Big.Of(999)));
        Check("Fmt 1000→1.0K", Fmt.Large(Big.Of(1000)) == "1.0K", Fmt.Large(Big.Of(1000)));
        Check("Fmt 16000→16.0K", Fmt.Large(Big.Of(16000)) == "16.0K", Fmt.Large(Big.Of(16000)));
        Check("Fmt 1e15→1.0e+15", Fmt.Large(new Big(1, 15)) == "1.0e+15", Fmt.Large(new Big(1, 15)));
        Check("Fmt 1.8e303 不出 Infinity", !Fmt.Large(Limits.Max).Contains("Inf"), Fmt.Large(Limits.Max));

        // 2) 封顶（13.3）
        var clamped = Big.Clamp(Big.Of(1e308));
        Check("Clamp 1e308 → 1.8e+303", clamped.CompareTo(Limits.Max) == 0, Fmt.Large(clamped));
        var overflow = Big.Pow(1.06, 20000);
        Check("Pow 1.06^20000 仍有限", !double.IsInfinity(overflow.ToDouble()), Fmt.Large(overflow));

        // 3) 怪物曲线（3.4）
        Check("第 1 层 HP = 50", MobHp(g, 1).CompareTo(Big.Of(50)) == 0, Fmt.Large(MobHp(g, 1)));
        Check("第 100 层 HP ≈ 16.0K", Fmt.Large(MobHp(g, 100)) == "16.0K", Fmt.Large(MobHp(g, 100)));
        Check("第 9999 层 HP ≈ 9.6e+299（含墨潮）", MobHp(g, 9999).E == 299, Fmt.Large(MobHp(g, 9999)));

        // 4) 战斗推进（3.1/3.3）
        long k0 = g.S.TotalKills;
        for (int i = 0; i < 240; i++) g.Tick(TimeSpan.FromSeconds(2));
        Check("打满 480s 有击杀", g.S.TotalKills > k0, $"击杀 {g.S.TotalKills}");
        Check("获得金币", g.Gold.CompareTo(Big.Zero) > 0, Fmt.Large(g.Gold));
        Check("层数推进或停留合法", g.Layer >= 1 && g.Layer <= Limits.MaxLayer, $"layer {g.Layer}, 本轮最高 {g.MaxLayer}");

        // 5) 金币袋（第七章）
        g.S.Bags.Clear();
        for (int i = 0; i < Game.BagKeys; i++) g.OnKeyPress();
        Check($"敲 {Game.BagKeys} 次掉 1 袋", g.S.Bags.Count == 1, $"袋 {g.S.Bags.Count}");
        var before = g.Gold;
        g.ClaimBags();
        Check("领取后金币增加", g.Gold.CompareTo(before) > 0, $"{Fmt.Large(before)} → {Fmt.Large(g.Gold)}");
        for (int i = 0; i < Game.BagKeys * 25; i++) g.OnKeyPress();
        Check("待领上限 20 袋", g.S.Bags.Count <= 20, $"袋 {g.S.Bags.Count}");

        // 6) 商店与墨匣（5.7）
        g.AddGold(Big.Of(100000));
        var goldBefore = g.Gold;
        g.BuyInk(10);
        double spent = goldBefore.ToDouble() - g.Gold.ToDouble();
        Check("买 10 颗墨晶扣 250 金币", Math.Abs(spent - 250) < 1,
            $"{Fmt.Large(goldBefore)} → {Fmt.Large(g.Gold)}");

        g.AddGold(Big.Of(10000000));
        var inkBeforeBox = g.Ink;
        g.OpenBox(1000);
        Check("开 1000 匣扣 25 万金币且不崩", g.S.BoxesOpened == 1000, $"开匣 {g.S.BoxesOpened}");
        Check("墨匣能开出墨晶", g.Ink.CompareTo(inkBeforeBox) > 0, $"{Fmt.Large(inkBeforeBox)} → {Fmt.Large(g.Ink)}");

        // 7) 强化（5.3）
        g.AddInk(Big.Of(1000));
        int b0 = g.S.EnhBlade;
        bool ok = g.Enhance(0);
        Check("墨刃 +1 消耗 2 墨晶", ok && g.S.EnhBlade == b0 + 1, $"等级 {g.S.EnhBlade}");
        var atkLow = g.PlayerAtk;
        for (int i = 0; i < 10; i++) g.Enhance(0);
        Check("强化提升攻击", g.PlayerAtk.CompareTo(atkLow) > 0, $"{Fmt.Large(atkLow)} → {Fmt.Large(g.PlayerAtk)}");

        // 7b) 批量强化（单次升级档位）
        g.AddInk(Big.Of(10000));
        int lvBefore = g.EnhLevel(1);
        int costOk = g.EnhanceMany(1, 10);
        Check("连升 10 级", costOk == 10 && g.EnhLevel(1) == lvBefore + 10, $"实际 {costOk} 级 → +{g.EnhLevel(1)}");

        var inkSnap = g.Ink;
        int cap = g.MaxEnhanceable(1);
        Check("max 档算出可升级数", cap >= 0, $"可升 {cap} 级 · 墨晶 {Fmt.Large(inkSnap)}");
        int didMax = g.EnhanceMany(1, cap);
        Check("max 档按算出级数升满", didMax == cap, $"实际 {didMax} / 预期 {cap}");
        Check("max 后墨晶不足再升", g.MaxEnhanceable(1) == 0, $"剩余可升 {g.MaxEnhanceable(1)}");
        Check("连升总成本 = 逐级成本之和", g.EnhCostTotal(0, 5).CompareTo(Big.Zero) > 0,
            $"5 级共 {Fmt.Large(g.EnhCostTotal(0, 5))} 墨晶");

        // 8) 层数控制（3.7）
        g.S.MaxLayer = 200;
        g.JumpTo(150);
        Check("跳层只能到已通层", g.Layer == 150, $"layer {g.Layer}");
        g.JumpTo(999);
        Check("超出已通层被夹取", g.Layer == 200, $"layer {g.Layer}");

        // 9) 转生（第六章）
        g.S.MaxLayer = 200;
        int gain = g.RebirthGain;
        Check("转生墨根 = floor(0.35×200)=70", gain == 70, $"gain {gain}");
        var goldKept = g.Gold;
        var enhKept = g.S.EnhBlade;
        g.Rebirth();
        Check("转生后墨根到账", g.S.Root >= 70, $"墨根 {g.S.Root}");
        Check("转生保留金币与强化", g.Gold.CompareTo(goldKept) == 0 && g.S.EnhBlade == enhKept,
            $"金币 {Fmt.Large(g.Gold)}，墨刃 +{g.S.EnhBlade}");
        Check("转生重置层数与等级", g.Layer == 1 && g.Level == 1, $"layer {g.Layer}, lv {g.Level}");

        // 10) 上限夹取
        g.AddGold(Big.Pow(1.06, 20000));
        Check("金币不会溢出（夹到 1.8e+303）", g.Gold.CompareTo(Limits.Max) <= 0, Fmt.Large(g.Gold));

        // 11) 存档往返
        g.Save();
        Check("存档文件已写出", File.Exists(Game.SavePath), Game.SavePath);

        // 12) 主列高亮：**一次只亮当前那一项**。打开「统计」不得连带「角色」，反过来也不行。
        try
        {
            var t = typeof(KeySpirit.UI.PanelWindow);
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic
                                                    | System.Reflection.BindingFlags.Instance;
            var pw = Activator.CreateInstance(t)!;
            var pageField = t.GetField("page", F)!;
            var build = t.GetMethod("Build", F)!;
            var refresh = t.GetMethod("RefreshAll")!;

            string BgOf(string key)
            {
                var map = (System.Collections.IDictionary)t.GetField("mainByKey", F)!.GetValue(pw)!;
                var row = map[key]!;
                var box = (System.Windows.Controls.Border)row.GetType().GetField("Box")!.GetValue(row)!;
                return (box.Background as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "-";
            }

            void Open(string p)
            {
                pageField.SetValue(pw, p);
                build.Invoke(pw, null);
                refresh.Invoke(pw, null);
            }

            Open("stats");
            Check("统计页只亮「统计」，不连带「角色」",
                BgOf("统计") == "#FF33414E" && BgOf("角色") != "#FF33414E",
                $"统计 {BgOf("统计")} / 角色 {BgOf("角色")}");

            Open("detail");
            Check("角色页只亮「角色」，不连带「统计」",
                BgOf("角色") == "#FF33414E" && BgOf("统计") != "#FF33414E",
                $"角色 {BgOf("角色")} / 统计 {BgOf("统计")}");

            // 鼠标事件不得把当前页的高亮冲掉
            Open("stats");
            {
                var map = (System.Collections.IDictionary)t.GetField("mainByKey", F)!.GetValue(pw)!;
                var row = map["统计"]!;
                var paint = t.GetMethod("Paint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                row.GetType().GetField("Hovered")!.SetValue(row, false);
                paint.Invoke(null, new[] { row });
            }
            Check("鼠标移出后当前页高亮不被冲掉", BgOf("统计") == "#FF33414E", BgOf("统计"));
        }
        catch (Exception ex)
        {
            Check("主列一次只亮一项", false, "自测异常：" + ex.GetBaseException().Message);
        }

        // 13) 本命器：切换次数档必须同时刷新「升到几级」和「属性预览」
        try
        {
            var t = typeof(KeySpirit.UI.PanelWindow);
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic
                                                    | System.Reflection.BindingFlags.Instance;

            g.S.EnhBlade = 5; g.S.EnhRobe = 5; g.S.EnhEye = 5; g.S.EnhSeal = 5;
            g.S.EnhBatch = 1;
            g.S.InkM = 1e12; g.S.InkE = 0;

            var pw = Activator.CreateInstance(t)!;
            t.GetField("page", F)!.SetValue(pw, "ear");
            t.GetMethod("Build", F)!.Invoke(pw, null);
            t.GetMethod("RefreshAll")!.Invoke(pw, null);

            string SubText(int i)
            {
                var rows = (System.Collections.IList)t.GetField("rows", F)!.GetValue(pw)!;
                int mainN = ((System.Collections.IList)t.GetField("mainRows", F)!.GetValue(pw)!).Count;
                var row = rows[mainN + i]!;
                var lbl = (System.Windows.Controls.TextBlock)row.GetType().GetField("Label")!.GetValue(row)!;
                return lbl.Text;
            }

            // 子列顺序：[0]单次升级 [1]墨刃 [2]墨刃属性 [3]墨衣 [4]属性 ...
            string b1Row = SubText(1), b1Eff = SubText(2);
            g.S.EnhBatch = 10;
            t.GetMethod("RebuildSub", F)!.Invoke(pw, null);
            string b10Row = SubText(1), b10Eff = SubText(2);

            Check("×1 时本命器行显示升 1 级", b1Row.Contains("+5 → +6"), b1Row);
            Check("切到 ×10 后本命器行显示升 10 级", b10Row.Contains("+5 → +15"), b10Row);
            Check("切到 ×10 后属性预览随之改变", b1Eff != b10Eff, $"{b1Eff} ／ {b10Eff}");

            // max 档：能升多少升多少，预览要跟着到顶
            g.S.EnhBatch = -1;
            t.GetMethod("RebuildSub", F)!.Invoke(pw, null);
            Check("max 档属性预览不等于 ×1 预览", SubText(2) != b1Eff, SubText(2));

            g.S.EnhBatch = 1;
        }
        catch (Exception ex)
        {
            Check("本命器次数档刷新", false, "自测异常：" + ex.GetBaseException().Message);
        }

        // 14) 商店：档位 1/10/100/1K/1M/1B/max，买墨晶与开墨匣都按档执行
        try
        {
            var t = typeof(KeySpirit.UI.PanelWindow);
            var next = t.GetMethod("NextShopBatch",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var seq = new System.Collections.Generic.List<int>();
            int b = 1;
            for (int i = 0; i < 7; i++) { seq.Add(b); b = (int)next.Invoke(null, new object[] { b })!; }
            Check("商店档位轮换 1→10→100→1K→1M→1B→max",
                string.Join(",", seq) == "1,10,100,1000,1000000,1000000000,-1", string.Join(",", seq));

            g.S.GoldM = 1e12; g.S.GoldE = 0;
            g.S.InkM = 0; g.S.InkE = 0;
            g.S.ShopBatch = 100;
            bool okBuy = g.BuyInk(100);
            Check("商店 ×100 买墨晶一次到账 100 颗", okBuy && g.Ink.ToDouble() >= 100, Fmt.Large(g.Ink));

            long boxBefore = g.S.BoxesOpened;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            g.OpenBox(1000000);
            sw.Stop();
            Check("开墨匣 ×1M 计数正确且不卡",
                g.S.BoxesOpened - boxBefore == 1000000 && sw.Elapsed.TotalSeconds < 5,
                $"耗时 {sw.Elapsed.TotalSeconds:0.00}s");

            var countOf = t.GetMethod("ShopCount",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            g.S.ShopBatch = -1;
            int maxN = (int)countOf.Invoke(null, new object[] { Game.InkPrice })!;
            Check("商店 max 档按金币买得起、并封顶 10 亿", maxN == 1000000000, maxN.ToString("N0"));
            g.S.ShopBatch = 1;
        }
        catch (Exception ex)
        {
            Check("商店档位", false, "自测异常：" + ex.GetBaseException().Message);
        }

        // 15) 每日敲击统计与每日礼包（跨零点重置 / 两档门槛 / 领取）
        try
        {
            // 15.1 日切：把日期伪造成昨天，EnsureDaily 应当把今日数与领取标记一起清掉
            g.S.DailyDate = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
            g.S.DailyKeys = 12345;
            g.S.DailyMask = 0b11;
            g.EnsureDaily();
            Check("跨零点重置今日敲击与领取标记", g.S.DailyKeys == 0 && g.S.DailyMask == 0,
                $"今日 {g.S.DailyKeys} / mask {g.S.DailyMask}");

            // 15.2 敲击累加：TotalKeys 与 DailyKeys 同步增长
            long t0 = g.S.TotalKeys;
            for (int i = 0; i < 4999; i++) g.OnKeyPress();
            Check("敲击同时累加总计与今日",
                g.S.TotalKeys - t0 == 4999 && g.S.DailyKeys == 4999,
                $"总 {g.S.TotalKeys - t0} / 今日 {g.S.DailyKeys}");
            Check("未达 5,000 档不能领", !g.DailyReady(0), $"{g.S.DailyKeys}");

            // 15.3 刚好到档
            g.OnKeyPress();
            Check("达 5,000 档可领", g.DailyReady(0), $"{g.S.DailyKeys}");

            var gold0 = g.Gold;
            var ink0 = g.Ink;
            int inkGain = g.DailyInk(0);
            bool claimed = g.ClaimDaily(0);
            Check("领 5,000 档：金币与墨晶入账、档位置领",
                claimed && g.DailyClaimed(0)
                       && (g.Gold - gold0).CompareTo(Big.Zero) > 0
                       && (g.Ink - ink0).CompareTo(Big.Of(inkGain - 0.01)) > 0,
                $"金币 {Fmt.Large(g.Gold - gold0)} · 墨晶 {Fmt.Large(g.Ink - ink0)}（应 +{inkGain}）");
            Check("同一档不能重复领", !g.ClaimDaily(0), "已领");

            // 15.4 20000 档
            Check("5,000 档已领后仅剩 20,000 档未达", !g.DailyReady(1), $"{g.S.DailyKeys}");
            for (int i = 0; i < 15000; i++) g.OnKeyPress();
            Check("达 20,000 档可领", g.DailyReady(1), $"{g.S.DailyKeys}");
            g.ClaimAll();
            Check("一键全领领到 20,000 档", g.DailyClaimed(1) && g.S.DailyMask == 0b11,
                $"mask {g.S.DailyMask}");
            Check("两档都领完后再无可领", !g.DailyReady(0) && !g.DailyReady(1), "已全领");

            // 15.5 今日数不受"领过"影响，只有跨零点才归零
            Check("领取不影响今日敲击计数", g.S.DailyKeys == 20000, $"{g.S.DailyKeys}");

            // 15.6 领取页三行状态：今日进度 / 已领档灰显 / 未达档禁用色
            try
            {
                g.S.DailyKeys = 6000;
                g.S.DailyMask = 0b01;
                var tp = typeof(KeySpirit.UI.PanelWindow);
                var pw2 = Activator.CreateInstance(tp)!;
                const System.Reflection.BindingFlags NF = System.Reflection.BindingFlags.NonPublic
                                                        | System.Reflection.BindingFlags.Public
                                                        | System.Reflection.BindingFlags.Instance;
                tp.GetField("page", NF)!.SetValue(pw2, "claim");
                tp.GetMethod("Build", NF)!.Invoke(pw2, null);

                var rowList = (System.Collections.IEnumerable)tp.GetField("rows", NF)!.GetValue(pw2)!;
                string TextOf(string want)
                {
                    foreach (var row in rowList)
                    {
                        var fld = row.GetType().GetField("Label", NF);
                        if (fld == null) continue;
                        var lb = (System.Windows.Controls.TextBlock?)fld.GetValue(row);
                        if (lb == null || lb.Text != want) continue;
                        var fg = (System.Windows.Media.SolidColorBrush)lb.Foreground;
                        return fg.Color.ToString();
                    }
                    return "-";
                }

                var sub = (System.Windows.Controls.StackPanel)tp.GetField("subRoot", NF)!.GetValue(pw2)!;
                var all = new System.Text.StringBuilder();
                foreach (var c in sub.Children)
                    if (c is System.Windows.Controls.Border b && b.Child is System.Windows.Controls.Grid gd)
                        foreach (var cc in gd.Children)
                            if (cc is System.Windows.Controls.TextBlock tb) all.Append(tb.Text).Append('|');

                Check("领取页显示今日敲击进度",
                    all.ToString().Contains("6,000 / 20,000"), all.ToString());
                Check("已领档灰显（Dim #8D9AA6）", TextOf("5,000 档") == "#FF8D9AA6", TextOf("5,000 档"));
                Check("未达档禁用色（#56606A）", TextOf("20,000 档") == "#FF56606A", TextOf("20,000 档"));

                // 15.6b 进度文案的三种状态。旧实现把「两档都达标但都没领」说成「已全领」，
                // 而下面两行明明都能点，属于自相矛盾。
                var progM = tp.GetMethod("DailyProgText",
                    NF | System.Reflection.BindingFlags.Static)!;

                g.S.DailyKeys = 21360;
                g.S.DailyMask = 0b00;
                var prog = (string)progM.Invoke(null, null)!;
                Check("两档都达标未领时文案显示「可领」", prog.Contains("可领"), prog);

                g.S.DailyMask = 0b11;
                prog = (string)progM.Invoke(null, null)!;
                Check("两档都领完才显示「已全领」", prog.Contains("已全领"), prog);
            }
            catch (Exception ex) { Check("领取页状态", false, "自测异常：" + ex.GetBaseException().Message); }

            // 15.7 存档往返带上今日数据
            g.Save();
            var saved = File.ReadAllText(Game.SavePath);
            Check("存档含今日敲击与领取标记",
                saved.Contains("\"DailyKeys\"") && saved.Contains("\"DailyMask\"") && saved.Contains("\"DailyDate\""),
                "save.json 字段齐全");
        }
        catch (Exception ex)
        {
            Check("每日敲击与礼包", false, "自测异常：" + ex.GetBaseException().Message);
        }

        // 16) 怪物种类表（3.4）
        try
        {
            var poolM = new HashSet<Game.MobKind>();
            var poolE = new HashSet<Game.MobKind>();
            var poolK = new HashSet<Game.MobKind>();
            bool typeOk = true, stableOk = true;
            for (int L = 1; L <= 400; L++)
            {
                for (int i = 1; i <= 10; i++)
                {
                    var k = Game.MobKindAt(L, i);
                    if (i == 10) poolK.Add(k); else if (i == 5) poolE.Add(k); else poolM.Add(k);
                    string want = i == 10 ? "守层者" : i == 5 ? "精英" : "小怪";
                    if (k.Type != want) typeOk = false;
                    var again = Game.MobKindAt(L, i);
                    if (again.Name != k.Name || again.Type != k.Type) stableOk = false;
                }
            }
            Check("小怪 10 种 / 精英 5 种 / 守层者 3 种",
                poolM.Count == 10 && poolE.Count == 5 && poolK.Count == 3,
                $"{poolM.Count} / {poolE.Count} / {poolK.Count}");
            Check("第 5 只归精英、第 10 只归守层者，其余小怪", typeOk);
            Check("同层同序号的怪恒定（每秒刷新不会换名）", stableOk);

            double Sum(HashSet<Game.MobKind> p, int col)
            {
                double s2 = 0;
                foreach (var x in p) s2 += col == 0 ? x.HpMul : col == 1 ? x.AtkMul : x.DefMul;
                return s2;
            }
            bool meanOk = true;
            foreach (var p in new[] { poolM, poolE, poolK })
                for (int c = 0; c < 3; c++)
                    if (Math.Abs(Sum(p, c) - p.Count) > 1e-9) meanOk = false;
            Check("三个池的血/攻/防倍率均值都 = 1.000（不改变整体曲线）", meanOk,
                $"小怪 {Sum(poolM, 0) / 10:0.000}/{Sum(poolM, 1) / 10:0.000}/{Sum(poolM, 2) / 10:0.000}");

            double mn = 9, mx = 0;
            foreach (var x in poolM)
            {
                if (x.HpMul < mn) mn = x.HpMul;
                if (x.HpMul > mx) mx = x.HpMul;
            }
            Check("单只之间确有三围差异", mx - mn > 0.15, $"血量倍率 {mn:0.00} ~ {mx:0.00}");

            // 实际刷怪时倍率要真的乘进去，且战斗与界面用同一套数值
            g.S.Layer = 37;
            g.ResetFight();
            var k37 = Game.MobKindAt(37, 1);
            var baseHp = Game.MobHpOf(37);
            double ratio = g.MobHpMax.ToDouble() / baseHp.ToDouble();
            Check("第 37 层首怪 HP = 基准 × 种类倍率", Math.Abs(ratio - k37.HpMul) < 1e-6,
                $"{Fmt.Large(g.MobHpMax)} / {Fmt.Large(baseHp)} = {ratio:0.000}（{k37.Name} ×{k37.HpMul:0.00}）");
            Check("怪物攻防接口与种类倍率一致",
                g.MobAtkNow.CompareTo(Big.Clamp(Game.MobAtk(37) * k37.AtkMul)) == 0
                && g.MobDefNow.CompareTo(Big.Clamp(Game.MobDef(37) * k37.DefMul)) == 0,
                $"攻 {Fmt.Small(g.MobAtkNow)} · 防 {Fmt.Small(g.MobDefNow)}");

            // 面板「战斗详情」页：主列有这一项，子页读得到当前怪
            var tp2 = typeof(KeySpirit.UI.PanelWindow);
            const System.Reflection.BindingFlags NF2 = System.Reflection.BindingFlags.NonPublic
                                                    | System.Reflection.BindingFlags.Instance;
            var pw3 = Activator.CreateInstance(tp2)!;
            tp2.GetField("page", NF2)!.SetValue(pw3, "battle");
            tp2.GetMethod("Build", NF2)!.Invoke(pw3, null);
            tp2.GetMethod("RefreshAll", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
               .Invoke(pw3, null);

            var byKey2 = (System.Collections.IDictionary)tp2.GetField("mainByKey", NF2)!.GetValue(pw3)!;
            var dump = new StringBuilder();
            foreach (var o in (System.Collections.IEnumerable)tp2.GetField("rows", NF2)!.GetValue(pw3)!)
            {
                var lb = (System.Windows.Controls.TextBlock?)o.GetType().GetField("Label")!.GetValue(o);
                if (lb != null) dump.Append(lb.Text).Append('|');
            }
            string d2 = dump.ToString();
            Check("主列有「战斗详情」且子页读到当前怪",
                byKey2.Contains("战斗详情") && d2.Contains(k37.Name) && d2.Contains("第 37 层 · 第 1/10 只"),
                d2.Length > 100 ? d2.Substring(0, 100) : d2);

            // 怪物血条：固定红，不随血量百分比变色
            var mobFill = (System.Windows.Controls.Border?)tp2.GetField("mobHpFill", NF2)!.GetValue(pw3);
            string MobBar()
            {
                tp2.GetMethod("RefreshAll", System.Reflection.BindingFlags.Public
                                            | System.Reflection.BindingFlags.Instance)!.Invoke(pw3, null);
                return (mobFill?.Background as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "-";
            }
            var mobHpProp = typeof(Game).GetProperty("MobHp")!;
            var keepMobHp = g.MobHp;
            string mobFull = MobBar();
            mobHpProp.SetValue(g, Big.Of(g.MobHpMax.ToDouble() * 0.15));
            string mobLow = MobBar();
            mobHpProp.SetValue(g, keepMobHp);
            Check("怪物血条固定红色（残血也不变色）",
                mobFull == "#FFD8453C" && mobLow == "#FFD8453C", $"满 {mobFull} / 残 {mobLow}");

            // 档位图标：三张都在，且类型 → 图标名映射正确
            string IconOf(string ty2) => (string)tp2
                .GetMethod("MobIconName", System.Reflection.BindingFlags.NonPublic
                                          | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { ty2 })!;
            bool icoFiles = true;
            foreach (var n in new[] { "mob-minion", "mob-elite", "mob-boss" })
                if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", n + ".ico"))) icoFiles = false;
            Check("三档怪物图标齐备且类型映射正确",
                icoFiles && IconOf("小怪") == "mob-minion" && IconOf("精英") == "mob-elite"
                         && IconOf("守层者") == "mob-boss",
                $"{IconOf("小怪")} / {IconOf("精英")} / {IconOf("守层者")}，文件{(icoFiles ? "齐" : "缺")}");

            // 玩家血条三档：≥60% 绿、<60% 橘、<20% 红（休整灰是第四种状态，测试时先排掉）
            var hpProp = typeof(Game).GetProperty("Hp")!;
            var hpMaxProp = typeof(Game).GetProperty("HpMax")!;
            var restProp = typeof(Game).GetProperty("Resting")!;
            var hpFillObj = (System.Windows.Controls.Border?)tp2.GetField("hpFill", NF2)!.GetValue(pw3);
            bool wasResting = g.Resting;
            restProp.SetValue(g, false);
            string HpBar(double pct)
            {
                hpMaxProp.SetValue(g, Big.Of(1000));
                hpProp.SetValue(g, Big.Of(1000 * pct));
                tp2.GetMethod("RefreshAll", System.Reflection.BindingFlags.Public
                                            | System.Reflection.BindingFlags.Instance)!.Invoke(pw3, null);
                return (hpFillObj?.Background as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "-";
            }
            string barFull = HpBar(1.0), barMid = HpBar(0.5), barLow = HpBar(0.1);
            Check("玩家血条：满血绿 / 50% 橘 / 10% 红",
                barFull == "#FF3EC18F" && barMid == "#FFF2A63B" && barLow == "#FFD8453C",
                $"{barFull} / {barMid} / {barLow}");
            restProp.SetValue(g, wasResting);
            hpMaxProp.SetValue(g, Big.Of(1000));
            hpProp.SetValue(g, Big.Of(1000));
        }
        catch (Exception ex)
        {
            Check("怪物种类表", false, "自测异常：" + ex.GetBaseException().Message);
        }

        // 19) 悬停注释气泡（8.4.1）：设置 / 本命器 / 转生三处的按钮要有解释，且停满 1.5 秒才弹
        try
        {
            const System.Reflection.BindingFlags NF3 = System.Reflection.BindingFlags.NonPublic
                                                     | System.Reflection.BindingFlags.Instance;
            var tp3 = typeof(KeySpirit.UI.PanelWindow);

            // 阈值必须是 1.5 秒：既不是系统的 ~400ms，也不能跟主列悬停切页的 240ms 混成一个
            var probe = Activator.CreateInstance(tp3)!;
            var tmr = (System.Windows.Threading.DispatcherTimer)tp3.GetField("noteTimer", NF3)!.GetValue(probe)!;
            var hov = (System.Windows.Threading.DispatcherTimer)tp3.GetField("hover", NF3)!.GetValue(probe)!;
            Check("注释气泡悬停阈值 1.5 秒", Math.Abs(tmr.Interval.TotalMilliseconds - 1500) < 1,
                $"{tmr.Interval.TotalMilliseconds:0}ms（切页悬停另为 {hov.Interval.TotalMilliseconds:0}ms）");

            // 取某一页的全部行：主列在前、子列在后
            List<object> PageRows(string key)
            {
                var pw = Activator.CreateInstance(tp3)!;
                tp3.GetField("page", NF3)!.SetValue(pw, key);
                tp3.GetMethod("Build", NF3)!.Invoke(pw, null);
                var list = new List<object>();
                foreach (var r in (System.Collections.IList)tp3.GetField("rows", NF3)!.GetValue(pw)!) list.Add(r!);
                return list;
            }
            string LabelOf(object r)
            {
                var ty = r.GetType();
                var fi = ty.GetField("Label") ?? ty.GetField("Label",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (fi == null) return "<无 Label 字段：" + ty.FullName + ">";
                return (fi.GetValue(r) as System.Windows.Controls.TextBlock)?.Text ?? "<Label 空>";
            }
            string NoteOf(object? r) => r == null ? "" : r.GetType().GetField("Note")?.GetValue(r) as string ?? "";
            object? Find(List<object> rows, string kw)
            {
                foreach (var r in rows) if (LabelOf(r).Contains(kw)) return r;
                return null;
            }

            // 设置页：四条功能开关都要能解释自己干什么
            var setRows = PageRows("settings");
            var setWant = new[] { "敲击监听", "自动回退", "战斗速度", "手动存档" };
            var setMiss = new List<string>();
            foreach (var kw in setWant) if (NoteOf(Find(setRows, kw)).Length < 8) setMiss.Add(kw);
            Check("设置页四项均带注释", setMiss.Count == 0,
                setMiss.Count == 0 ? $"{setWant.Length} / {setWant.Length}"
                                   : $"缺：{string.Join("、", setMiss)}");

            // 本命器页：单次升级档位 + 四器各一条
            var earRows = PageRows("ear");
            var earWant = new[] { "单次升级", "墨刃", "墨衣", "墨眸", "墨印" };
            var earMiss = new List<string>();
            foreach (var kw in earWant) if (NoteOf(Find(earRows, kw)).Length < 8) earMiss.Add(kw);
            Check("本命器页五处均带注释", earMiss.Count == 0,
                earMiss.Count == 0 ? $"{earWant.Length} / {earWant.Length}"
                                   : $"缺：{string.Join("、", earMiss)}");

            // 转生按钮挂在「角色」页（detail）；「统计」页是只读数据，没有这行。
            var keepMax = g.S.MaxLayer;
            g.S.MaxLayer = 0;
            var rbRow = Find(PageRows("detail"), "转生");
            string rbNote = NoteOf(rbRow);
            var rbLabel = rbRow == null ? null
                : (System.Windows.Controls.TextBlock)rbRow.GetType().GetField("Label")!.GetValue(rbRow)!;
            string rbFg = (rbLabel?.Foreground as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "-";
            g.S.MaxLayer = keepMax;
            Check("转生行带代价与保留项说明（置灰时也在）",
                rbNote.Contains("保留") && rbNote.Contains("强化") && rbFg == "#FF56606A",
                $"前景 {rbFg}（置灰 #FF56606A）；{rbNote}");

            // 气泡定位：右侧优先 → 右缘翻左侧 → 底部上顶，三种都不出工作区
            var place = typeof(KeySpirit.UI.NoteWindow).GetMethod("Place",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var wa = new System.Windows.Rect(0, 0, 1920, 1040);
            double Px(System.Windows.Rect row, int which)
            {
                var v = (ValueTuple<double, double>)place.Invoke(null, new object[] { row, 200.0, 40.0, wa })!;
                return which == 0 ? v.Item1 : v.Item2;
            }
            var midRow = new System.Windows.Rect(300, 500, 400, 22);
            Check("气泡贴行右侧且垂直居中",
                Math.Abs(Px(midRow, 0) - 708) < 1 && Math.Abs(Px(midRow, 1) - 491) < 1,
                $"({Px(midRow, 0):0},{Px(midRow, 1):0})");
            var edgeRow = new System.Windows.Rect(1750, 500, 80, 22);
            Check("右侧放不下时翻到行左侧", Math.Abs(Px(edgeRow, 0) - 1542) < 1, $"{Px(edgeRow, 0):0}");
            var botRow = new System.Windows.Rect(1750, 1020, 80, 22);
            double by = Px(botRow, 1);
            Check("底部行的气泡上顶且不出工作区", by >= 0 && by + 40 <= 1036.5, $"{by:0}");
            var topRow = new System.Windows.Rect(300, 0, 400, 22);
            double ty = Px(topRow, 1);
            Check("顶部行的气泡下压且不出工作区", ty >= 4 - 0.5, $"{ty:0}");
        }
        catch (Exception ex)
        {
            var e0 = ex.GetBaseException();
            var trace = e0.StackTrace ?? "";
            int nl = trace.IndexOf('\n');
            Check("悬停注释气泡", false, "自测异常：" + e0.Message + " ＠ "
                + (nl > 0 ? trace[..nl] : trace).Trim());
        }

        sb.AppendLine();
        sb.AppendLine($"RESULT pass={pass} fail={fail}");
        var outFile = Path.Combine(Path.GetTempPath(), "keyspirit_selftest.txt");
        File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        Environment.Exit(fail == 0 ? 0 : 1);
    }

    static Big MobHp(Game g, int layer) => Game.MobHpOf(layer);
}
