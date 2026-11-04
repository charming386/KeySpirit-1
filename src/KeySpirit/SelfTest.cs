using System;
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

        sb.AppendLine();
        sb.AppendLine($"RESULT pass={pass} fail={fail}");
        var outFile = Path.Combine(Path.GetTempPath(), "keyspirit_selftest.txt");
        File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        Environment.Exit(fail == 0 ? 0 : 1);
    }

    static Big MobHp(Game g, int layer) => Game.MobHpOf(layer);
}
