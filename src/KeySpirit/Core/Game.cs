using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeySpirit.Core;

public class SaveDto
{
    public string Version { get; set; } = "0.12";
    public int Layer { get; set; } = 1;
    public int MaxLayer { get; set; } = 0;      // 本轮已通最高层
    public int Cycle { get; set; } = 1;         // 轮次
    public int Level { get; set; } = 1;
    public double ExpM { get; set; }
    public int ExpE { get; set; }
    public double GoldM { get; set; }
    public int GoldE { get; set; }
    public double InkM { get; set; }
    public int InkE { get; set; }
    public int Root { get; set; }
    public int EnhBlade { get; set; }
    public int EnhRobe { get; set; }
    public int EnhEye { get; set; }
    public int EnhSeal { get; set; }
    public List<double[]> Bags { get; set; } = new();   // [mantissa, exp]
    public long TotalKeys { get; set; }
    public long TotalKills { get; set; }
    public int Rebirths { get; set; }
    public long BoxesOpened { get; set; }
    public double PlaySeconds { get; set; }
    public bool ListenKeys { get; set; } = true;
    public bool AutoDescend { get; set; } = true;
    public bool AutoRetreat { get; set; } = true;
    public bool Paused { get; set; }
    public bool LockLayer { get; set; }
    public int BattleSeconds { get; set; } = 2;
    public int EnhBatch { get; set; } = 1;      // 单次强化次数：1/10/50/100，-1 = max
    public DateTime LastSaveUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>游戏状态与全部规则实现（对应策划稿 3~7 章）。</summary>
public class Game
{
    public static Game I { get; } = new Game();

    public SaveDto S { get; private set; } = new();

    // ---- 运行期 ----
    public Big Hp { get; private set; }
    public Big HpMax { get; private set; }
    public int MobIndex { get; private set; } = 1;      // 1..10（5 精英 / 10 守层者）
    public Big MobHp { get; private set; }
    public Big MobHpMax { get; private set; }
    public bool Resting { get; private set; }
    public DateTime RestUntil { get; private set; }
    public int ConsecutiveLoss { get; private set; }
    public string Status { get; private set; } = "战斗中";

    double roundAccum;

    public event Action? Changed;

    [JsonIgnore] public int Layer => S.Layer;
    [JsonIgnore] public int MaxLayer => S.MaxLayer;
    [JsonIgnore] public int Level => S.Level;
    [JsonIgnore] public Big Gold => new Big(S.GoldM, S.GoldE);
    [JsonIgnore] public Big Ink => new Big(S.InkM, S.InkE);
    [JsonIgnore] public Big Exp => new Big(S.ExpM, S.ExpE);

    // ---- 静态表：怪物（3.4）----
    static double MoChao(int n) => n > 9000 ? Math.Pow(1.11, n - 9000) : 1.0;

    public static Big MobHpOf(int n) => Big.Clamp(Big.Pow(1.06, n - 1) * (50 * MoChao(n)));
    public static Big MobAtk(int n) => Big.Clamp(Big.Pow(1.05, n - 1) * (6 * MoChao(n)));
    public static Big MobDef(int n) => Big.Clamp(Big.Pow(1.03, n - 1) * (2 * MoChao(n)));
    public static Big MobGold(int n) => Big.Clamp(Big.Pow(1.06, n - 1) * (5 * MoChao(n)));
    public static Big MobExp(int n) => Big.Clamp(Big.Pow(1.05, n - 1) * (12 * MoChao(n)));

    public Big PlayerAtk => CalcAtk();
    public Big PlayerDef => CalcDef();

    Big LvMul => Big.Of(1 + 0.015 * (S.Level - 1));
    Big RootMul => Big.Pow(1.06, S.Root);

    Big CalcAtk() => Big.Clamp(((Big.Pow(1.06, S.EnhBlade) * 10) + Big.Of(10)) * LvMul * RootMul);
    Big CalcHp() => Big.Clamp(((Big.Pow(1.06, S.EnhRobe) * 80) + Big.Of(100)) * LvMul * RootMul);
    Big CalcDef() => Big.Clamp(((Big.Pow(1.06, S.EnhSeal) * 4) + Big.Of(3)) * LvMul * RootMul);

    /// <summary>暴击率 2% + 墨眸 0.15%/级，60% 封顶，超出转暴伤。</summary>
    public double CritRate
    {
        get
        {
            double raw = 0.02 + 0.0015 * S.EnhEye;
            return Math.Min(0.60, raw);
        }
    }
    public double CritDmg
    {
        get
        {
            double raw = 0.02 + 0.0015 * S.EnhEye;
            return 0.5 + Math.Max(0, raw - 0.60) * 1.0;
        }
    }

    public Big ExpNeed => Big.Clamp(Big.Of(40) * Big.Pow(S.Level, 1) * Big.Of(Math.Pow(S.Level, 0.35)));

    public double LayerProgress => MobIndex / 10.0;

    // ---- 生命/初始化 ----
    public void Boot()
    {
        Load();
        HpMax = CalcHp();
        Hp = HpMax;
        SpawnMob();
        ApplyOffline();
    }

    void SpawnMob()
    {
        int n = S.Layer;
        var hp = MobHpOf(n);
        if (MobIndex == 5) hp = Big.Clamp(hp * 1.6);
        else if (MobIndex == 10) hp = Big.Clamp(hp * 2.5);
        MobHpMax = hp;
        MobHp = hp;
    }

    public void ResetFight()
    {
        HpMax = CalcHp();
        Hp = HpMax;
        MobIndex = 1;
        SpawnMob();
        roundAccum = 0;
    }

    // ---- 主循环 ----
    public void Tick(TimeSpan dt)
    {
        if (S.Paused) { Status = "暂停"; return; }

        if (Resting)
        {
            if (DateTime.UtcNow >= RestUntil)
            {
                Resting = false;
                Hp = HpMax = CalcHp();
                Status = "战斗中";
            }
            else
            {
                Status = "休整 " + Fmt.Time(RestUntil - DateTime.UtcNow);
                return;
            }
        }

        roundAccum += dt.TotalSeconds;
        double round = Math.Max(1, S.BattleSeconds);
        int guard = 0;
        while (roundAccum >= round && !Resting && !S.Paused && guard++ < 200)
        {
            roundAccum -= round;
            ResolveRound();
        }
        S.PlaySeconds += dt.TotalSeconds;
    }

    void ResolveRound()
    {
        // 玩家先手
        var atk = PlayerAtk;
        var dmg = atk - MobDef(S.Layer) * 0.5;
        if (dmg.CompareTo(Big.Zero) < 0) dmg = Big.Of(1);
        bool crit = Rng.NextDouble() < CritRate;
        if (crit) dmg = dmg * (1 + CritDmg);
        MobHp = MobHp - dmg;
        if (MobHp.CompareTo(Big.Zero) <= 0)
        {
            KillMob();
            return;
        }

        // 怪物反击
        var taken = MobAtk(S.Layer) - PlayerDef * 0.5;
        if (taken.CompareTo(Big.Of(1)) < 0) taken = Big.Of(1);
        Hp = Hp - taken;
        if (Hp.CompareTo(Big.Zero) <= 0) Exhaust();
    }

    void KillMob()
    {
        S.TotalKills++;
        var g = MobGold(S.Layer);
        var e = MobExp(S.Layer);
        var ink = Big.Zero;
        if (MobIndex == 5) { g = g * 1.5; e = e * 1.5; ink = Big.Of(1); }
        else if (MobIndex == 10) { g = g * 3; e = e * 3; ink = Big.Of(3); }
        AddGold(g);
        AddInk(ink);
        AddExp(e);

        bool wasKeeper = MobIndex == 10;
        MobIndex++;
        if (MobIndex > 10)
        {
            if (S.Layer < Limits.MaxLayer)
            {
                S.Layer++;
                if (S.Layer > S.MaxLayer) S.MaxLayer = S.Layer;
            }
            MobIndex = 1;
            if (S.Layer >= Limits.MaxLayer) S.Layer = Limits.MaxLayer;
        }
        else if (wasKeeper && S.AutoDescend && !S.LockLayer)
        {
            // 守层者击杀后自动下潜由上面的层数推进完成
        }
        SpawnMob();
        Changed?.Invoke();
    }

    void Exhaust()
    {
        Hp = Big.Zero;
        Resting = true;
        RestUntil = DateTime.UtcNow.AddSeconds(120);
        ConsecutiveLoss++;
        if (S.AutoRetreat && !S.LockLayer && ConsecutiveLoss >= 3 && S.Layer > 1)
        {
            S.Layer--;
            ConsecutiveLoss = 0;
            MobIndex = 1;
            SpawnMob();
        }
        Log.Write($"力竭于第 {S.Layer} 层，休整 120s");
    }

    // ---- 资源 ----
    public void AddGold(Big v)
    {
        var g = Big.Clamp(Gold + v);
        S.GoldM = g.M; S.GoldE = g.E;
    }
    public void AddInk(Big v)
    {
        if (v.IsZero) return;
        var i = Big.Clamp(Ink + v);
        S.InkM = i.M; S.InkE = i.E;
    }
    public void AddExp(Big v)
    {
        var e = Big.Clamp(Exp + v);
        S.ExpM = e.M; S.ExpE = e.E;
        while (e.CompareTo(ExpNeed) >= 0)
        {
            e = e - ExpNeed;
            S.Level++;
            var h = CalcHp();
            // 升级只抬上限、**不回血**（与强化同一套规则）。
            // 原来这里是 `Hp = h`，等于每升一级就满血：挂机时怪打得没升级快，
            // 血条永远满的，力竭与休整这套机制直接被架空。
            // 现在留一个"缺口"：上限涨了、当前值原地不动，掉的血要自己慢慢还。
            // 会回满的只剩三种情况：新档 / 转生 / 休整结束。
            HpMax = h;
            if (Hp.CompareTo(HpMax) > 0) Hp = HpMax;
        }
        S.ExpM = e.M; S.ExpE = e.E;
    }

    // ---- 键盘金币袋（第七章）----
    /// <summary>掉落一袋所需敲击数（第七章：300 键 = 1 袋）。</summary>
    public const int BagKeys = 300;

    long bagCounter;
    public void OnKeyPress()
    {
        if (!S.ListenKeys) return;
        S.TotalKeys++;
        bagCounter++;
        if (bagCounter < BagKeys) return;
        bagCounter = 0;

        double tier = Rng.NextDouble();
        double mult = tier < 0.70 ? 1 : tier < 0.95 ? 2.5 : 6;
        var baseVal = MobGold(S.Layer) * 5 * (1 + 0.015 * (S.Level - 1)) * mult;        var val = Big.Clamp(baseVal);
        if (S.Bags.Count >= 20)
        {
            AddGold(val * 0.3);
            Log.Write("金币袋超出上限，按 30% 折算入账");
        }
        else
        {
            S.Bags.Add(new[] { val.M, (double)val.E });
        }
        Changed?.Invoke();
    }

    public Big BagSum
    {
        get
        {
            var sum = Big.Zero;
            foreach (var b in S.Bags) sum = sum + new Big(b[0], (int)b[1]);
            return sum;
        }
    }

    public void ClaimBags()
    {
        if (S.Bags.Count == 0) return;
        var sum = BagSum;
        AddGold(sum);
        Log.Write($"领取金币袋 ×{S.Bags.Count}（+{Fmt.Large(sum)} 金币）");
        S.Bags.Clear();
        Changed?.Invoke();
    }

    // ---- 本命器强化（5.3）----
    public static double EnhFactor(int which) => which switch { 0 => 1.00, 1 => 0.70, 2 => 0.85, _ => 0.70 };

    public Big EnhCost(int which, int level)
        => Big.Clamp(Big.Pow(1.062, level - 1) * (2 * EnhFactor(which)));

    public int EnhLevel(int which) => which switch
    {
        0 => S.EnhBlade, 1 => S.EnhRobe, 2 => S.EnhEye, _ => S.EnhSeal
    };

    public bool CanEnhance(int which)
        => EnhLevel(which) < Limits.MaxEnhance && Ink.CompareTo(EnhCost(which, EnhLevel(which) + 1)) >= 0;

    public bool Enhance(int which)
    {
        int lv = EnhLevel(which);
        if (lv >= Limits.MaxEnhance) return false;
        var cost = EnhCost(which, lv + 1);
        if (Ink.CompareTo(cost) < 0) return false;
        var ink = Ink - cost;
        S.InkM = ink.M; S.InkE = ink.E;
        switch (which)
        {
            case 0: S.EnhBlade++; break;
            case 1: S.EnhRobe++; break;
            case 2: S.EnhEye++; break;
            default: S.EnhSeal++; break;
        }
        // 强化只抬上限，**不回血**：原来这里 `Hp = HpMax`，等于每点一次强化就满血，
        // 连点几下就是无限续航（力竭之前点一下直接回满）。上限涨了、当前值留在原处，
        // 缺口按比例变大 —— 这才是「变强但不送血」。
        // （等级提升（AddExp）仍然回满，那是设计稿 13.x 写明的"升级回满血"，与本条无关。）
        HpMax = CalcHp();
        if (Hp.CompareTo(HpMax) > 0) Hp = HpMax;
        Changed?.Invoke();
        return true;
    }

    /// <summary>连升 n 级的总成本（等比求和，避免逐级累加）。</summary>
    public Big EnhCostTotal(int which, int times)
    {
        if (times <= 0) return Big.Zero;
        int lv = EnhLevel(which);
        if (lv >= Limits.MaxEnhance) return Big.Zero;
        if (lv + times > Limits.MaxEnhance) times = Limits.MaxEnhance - lv;
        var c1 = EnhCost(which, lv + 1);                        // 首级成本
        var rn = Big.Pow(1.062, times);
        return Big.Clamp(c1 * ((rn - Big.Of(1)) / 0.062));
    }

    /// <summary>连升 n 级；返回实际升了几级（墨晶不够或到顶就停）。</summary>
    public int EnhanceMany(int which, int times)
    {
        int done = 0;
        for (int i = 0; i < times; i++)
        {
            if (!Enhance(which)) break;
            done++;
        }
        return done;
    }

    /// <summary>当前墨晶能升的最大级数（1..剩余空间），用于「max」档。</summary>
    public int MaxEnhanceable(int which)
    {
        int lv = EnhLevel(which);
        int room = Limits.MaxEnhance - lv;
        if (room <= 0) return 0;

        // 等比级数反解：ink ≥ c1·(r^n − 1)/(r − 1)
        double c1 = 2 * EnhFactor(which);
        double ink = Ink.ToDouble();
        double n = Math.Log(1 + ink * 0.062 / c1) / Math.Log(1.062);
        int k = (int)Math.Floor(n);
        if (k < 0) k = 0;
        if (k > room) k = room;

        // 浮点误差校正（最多几步）
        while (k > 0 && EnhCostTotal(which, k).CompareTo(Ink) > 0) k--;
        while (k < room && EnhCostTotal(which, k + 1).CompareTo(Ink) <= 0) k++;
        return k;
    }

    /// <summary>
    /// 按当前「次数档」执行强化；返回实际级数。max = -1。
    /// **倍率是硬承诺**：×100 就要求墨晶一次付得起 100 级，付不起则一颗都不升。
    /// 唯一允许"少升几级"的情况是撞到等级上限（×100 但只剩 3 级空间）——那是上限而不是墨晶不够。
    /// </summary>
    public int EnhanceByBatch(int which)
    {
        int batch = S.EnhBatch == 0 ? 1 : S.EnhBatch;
        int room = Limits.MaxEnhance - EnhLevel(which);
        if (room <= 0) return 0;

        int want = batch < 0 ? MaxEnhanceable(which) : Math.Min(batch, room);
        if (want <= 0) return 0;
        if (EnhCostTotal(which, want).CompareTo(Ink) > 0) return 0;

        return EnhanceMany(which, want);
    }

    // ---- 商店（5.7）----
    public const int InkPrice = 25;
    public bool BuyInk(int count)
    {
        var cost = Big.Of((double)InkPrice * count);
        if (Gold.CompareTo(cost) < 0) return false;
        var g = Gold - cost;
        S.GoldM = g.M; S.GoldE = g.E;
        AddInk(Big.Of(count));
        Changed?.Invoke();
        return true;
    }

    public int InkShortfall(int which)
    {
        if (EnhLevel(which) >= Limits.MaxEnhance) return 0;
        var cost = EnhCost(which, EnhLevel(which) + 1);
        double need = cost.ToDouble() - Ink.ToDouble();
        if (need <= 0) return 0;
        return (int)Math.Ceiling(need);
    }

    public void FillToNext(int which)
    {
        int need = InkShortfall(which);
        if (need <= 0) return;
        int afford = (int)Math.Floor(Gold.ToDouble() / InkPrice);
        int buy = Math.Min(need, afford);
        if (buy > 0) BuyInk(buy);
    }

    /// <summary>墨匣：250 金币，7 档概率（期望 87%）。</summary>
    public static readonly (double p, bool isInk, double amount)[] BoxTable =
    {
        (0.30, false, 100), (0.20, false, 300), (0.08, false, 750),
        (0.25, true, 1),    (0.10, true, 5),     (0.05, true, 15),
        (0.02, true, 60),
    };

    public string? OpenBox(int count)
    {
        if (count <= 0) return null;
        var cost = Big.Of(250.0 * count);
        if (Gold.CompareTo(cost) < 0) return "金币不足";
        var g = Gold - cost;
        S.GoldM = g.M; S.GoldE = g.E;
        string last = "";
        for (int i = 0; i < count; i++)
        {
            S.BoxesOpened++;
            double r = Rng.NextDouble(), acc = 0;
            foreach (var row in BoxTable)
            {
                acc += row.p;
                if (r <= acc)
                {
                    if (row.isInk) { AddInk(Big.Of(row.amount)); last = $"墨晶 +{row.amount:0}"; }
                    else { AddGold(Big.Of(row.amount)); last = $"金币 +{row.amount:0}"; }
                    break;
                }
            }
            Log.Write("墨匣：" + last);
        }
        Changed?.Invoke();
        return count == 1 ? last : $"{count} 匣已开（末档 {last}）";
    }

    // ---- 转生（第六章）----
    public bool CanRebirth => S.MaxLayer >= 50;
    public int RebirthGain => Math.Max(1, (int)Math.Floor(0.35 * S.MaxLayer));

    public void Rebirth()
    {
        if (!CanRebirth) return;
        int gain = RebirthGain;
        S.Root = Math.Min(Limits.MaxSoul, S.Root + gain);
        S.Rebirths++;
        S.Layer = 1; S.MaxLayer = 0; S.Level = 1;
        S.ExpM = 0; S.ExpE = 0;
        ConsecutiveLoss = 0;
        Resting = false;
        ResetFight();
        Log.Write($"转生：墨根 +{gain}（累计 {S.Root}）");
        Changed?.Invoke();
    }

    // ---- 层数控制（3.7）----
    public void JumpTo(int layer)
    {
        int target = Math.Clamp(layer, 1, Math.Max(1, S.MaxLayer));
        S.Layer = target;
        MobIndex = 1;
        SpawnMob();
        Resting = false;
        ConsecutiveLoss = 0;
        Changed?.Invoke();
    }

    public void Step(int delta)
    {
        JumpTo(S.Layer + delta);
    }

    public void TogglePause()
    {
        S.Paused = !S.Paused;
        Status = S.Paused ? "暂停" : "战斗中";
        Changed?.Invoke();
    }

    // ---- 存档 ----
    public static string? DirOverride;

    public static string Dir =>
        DirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeySpirit");
    public static string SavePath => Path.Combine(Dir, "save.json");

    public void Save()
    {
        try
        {
            S.LastSaveUtc = DateTime.UtcNow;
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(S, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SavePath, json);
            Log.Write("已存档");
        }
        catch (Exception ex) { Log.Write("存档失败：" + ex.Message); }
    }

    void Load()
    {
        try
        {
            if (File.Exists(SavePath))
            {
                var dto = JsonSerializer.Deserialize<SaveDto>(File.ReadAllText(SavePath));
                if (dto != null) S = dto;
            }
        }
        catch (Exception ex) { Log.Write("读档失败：" + ex.Message); }
        S.Bags ??= new List<double[]>();
        if (S.BattleSeconds < 1) S.BattleSeconds = 2;
    }

    void ApplyOffline()
    {
        var gap = DateTime.UtcNow - S.LastSaveUtc;
        if (gap.TotalSeconds < 30) return;
        double hours = Math.Min(8, gap.TotalHours);
        double eff = hours * 3600 * 0.60;
        double kills = eff / 16.0;                       // 一场约 16s
        AddGold(MobGold(S.Layer) * kills);
        AddExp(MobExp(S.Layer) * kills);
        Log.Write($"离线 {hours:0.0}h（60% 效率）→ 约 {kills:0} 场战斗");
    }
}

public static class Rng
{
    static readonly Random R = new();
    public static double NextDouble() => R.NextDouble();
}

public static class Log
{
    static readonly object Gate = new();
    public static string Path => System.IO.Path.Combine(Game.Dir, "key spirit.log");

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                var dir = Game.Dir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
            }
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
