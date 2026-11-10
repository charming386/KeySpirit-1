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
    public long DailyKeys { get; set; }                          // 今日敲击（本地日切重置）
    public string DailyDate { get; set; } = "";                  // 上面这个数属于哪一天，yyyy-MM-dd
    public int DailyMask { get; set; }                           // 每日礼包领没领：bit0 = 5,000 档，bit1 = 20,000 档
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
    public int ShopBatch { get; set; } = 1;     // 商店单次数量：1/10/100/1K/1M/1B，-1 = max
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

    /// <summary>
    /// 怪物种类（3.4）。倍率乘在本层基准值上，**同池均值恰好 = 1.000**，
    /// 所以加这套表不会改变推层速度与整体数值曲线，只是让单只有胖瘦快慢之分。
    /// 名字是物种不是人，和「墨渊/墨晶/守层者」这套世界观对齐。
    /// </summary>
    public readonly record struct MobKind(string Name, string Type, double HpMul, double AtkMul, double DefMul);

    /// <summary>小怪 10 种：hp / atk / def 三列各自求和都是 10.00。</summary>
    static readonly MobKind[] Minions =
    {
        new("墨蛛",   "小怪", 0.95, 1.04, 0.95),   // 结网猎手，脆而快
        new("砚龟",   "小怪", 1.10, 0.88, 1.15),   // 背甲是凝固的墨块
        new("纸魅",   "小怪", 0.92, 1.06, 0.92),   // 从废稿里爬出的薄影
        new("笔魈",   "小怪", 1.00, 1.02, 0.98),   // 断笔成精，善刺
        new("墨蚊",   "小怪", 0.90, 1.08, 0.90),   // 吸食墨气的小虫
        new("字蠹",   "小怪", 1.02, 0.96, 1.08),   // 蛀食经卷文字的蠹虫
        new("灰蛾",   "小怪", 0.96, 1.00, 0.96),   // 扑向灯焰的灰翼虫
        new("池蛭",   "小怪", 1.04, 0.98, 1.02),   // 墨池底部的吸血虫
        new("残页魇", "小怪", 1.06, 0.94, 1.06),   // 缺页残卷里凝成的梦魇
        new("拓影犬", "小怪", 1.05, 1.04, 0.98),   // 从拓印上走下来的影子犬
    };

    /// <summary>精英 5 种：层内第 5 只。三列同样各自求和 5.00。</summary>
    static readonly MobKind[] Elites =
    {
        new("镇纸兽", "精英", 1.10, 0.94, 1.10),   // 压纸的重甲兽，硬而迟
        new("朱砂魈", "精英", 0.96, 1.08, 0.94),   // 朱砂点睛，赤目好斗
        new("墨甲卒", "精英", 1.04, 1.00, 1.06),   // 披墨甲的无面守卒
        new("断锋螳", "精英", 0.94, 1.08, 0.94),   // 以断笔为镰的刀客
        new("碑蠹王", "精英", 0.96, 0.90, 0.96),   // 啃穿石碑的虫王
    };

    /// <summary>守层者 3 种：层内第 10 只。三列各自求和 3.00。</summary>
    static readonly MobKind[] Keepers =
    {
        new("渊墨巨魇", "守层者", 1.04, 1.04, 0.98),   // 墨渊深处聚合的魇体
        new("无字碑灵", "守层者", 0.96, 1.00, 1.06),   // 空白碑文里醒来的守碑之灵
        new("千钧砚主", "守层者", 1.00, 0.96, 0.96),   // 一方悬空巨砚，碾一切字迹
    };

    /// <summary>
    /// 取本层第 index 只怪的种类。**必须是纯函数**：面板每秒刷新一次，
    /// 若带随机，同一只怪的名字会在屏幕上乱跳。用 (层, 序号) 做哈希取模即可，
    /// 同一层同一只永远同一个物种，换层则重新洗牌。
    /// </summary>
    public static MobKind MobKindAt(int layer, int index)
    {
        if (index >= 10) return Keepers[Pick(layer, 10, Keepers.Length)];
        if (index == 5) return Elites[Pick(layer, 5, Elites.Length)];
        return Minions[Pick(layer, index, Minions.Length)];
    }

    static int Pick(int layer, int index, int mod)
    {
        unchecked
        {
            uint h = (uint)layer * 73856093u ^ (uint)index * 19349663u;
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            return (int)(h % (uint)mod);
        }
    }

    /// <summary>当前正在打的那一只。</summary>
    [JsonIgnore] public MobKind Mob => MobKindAt(S.Layer, MobIndex);
    [JsonIgnore] public string MobName => Mob.Name;
    [JsonIgnore] public string MobType => Mob.Type;
    /// <summary>本层第几只是它（1~10）。</summary>
    [JsonIgnore] public int MobNo => MobIndex;
    /// <summary>怪物实际的攻防（已乘种类倍率）——战斗用它，界面也用它，两处必须同一个来源。</summary>
    [JsonIgnore] public Big MobAtkNow => Big.Clamp(MobAtk(S.Layer) * Mob.AtkMul);
    [JsonIgnore] public Big MobDefNow => Big.Clamp(MobDef(S.Layer) * Mob.DefMul);

    public Big PlayerAtk => CalcAtk();
    public Big PlayerDef => CalcDef();

    Big LvMul => Big.Of(1 + 0.015 * (S.Level - 1));
    Big RootMul => Big.Pow(1.06, S.Root);

    // ---- 等级加点（v0.13.0）----
    // 等级不再只是乘区：每级直接给基础攻/血/防加一点「本体数值」。
    // 量级刻意做小（占后期总属性千分之一以下），作用是**让"升级"在属性面板上有个看得见的东西**，
    // 前 50 层（基础值就是全部）时这点加成是实打实的强度。真正的长线增长仍靠乘区。
    public const double LvAtkGain = 1.0;
    public const double LvHpGain = 10.0;
    public const double LvDefGain = 0.5;

    /// <summary>基础攻击 = 10 + 每级加点（未乘等级乘区/墨根，供面板拆解用）。</summary>
    public Big BaseAtk => Big.Clamp(Big.Of(10 + LvAtkGain * (S.Level - 1)));
    public Big BaseHp => Big.Clamp(Big.Of(100 + LvHpGain * (S.Level - 1)));
    public Big BaseDef => Big.Clamp(Big.Of(3 + LvDefGain * (S.Level - 1)));

    /// <summary>
    /// 「假如这一件已经到 lv 级」时的最终属性：**本体值 + 强化 + 等级乘区 + 墨根乘区**，一份齐全。
    ///
    /// 面板的强化预览行必须走这三个方法。旧版面板自己写了 `Big.Pow(1.06, lv) * 10`，
    /// 只算了强化那一项 —— 同一块屏幕上强化页写「攻击 3.0B」、角色页写「攻击 35.0B」，
    /// 差的正是等级乘区（×11.65）；转生之后还会再差一个墨根乘区（墨根 50 时 ×214）。
    /// 这类「面板自己抄一遍公式」的错已经不是第一次了（暴击封顶也是同一个毛病）。
    /// </summary>
    public Big AtkIfBlade(int lv) => Big.Clamp((Big.Pow(1.06, lv) * 10 + BaseAtk) * LvMul * RootMul);
    public Big HpIfRobe(int lv) => Big.Clamp((Big.Pow(1.06, lv) * 80 + BaseHp) * LvMul * RootMul);
    public Big DefIfSeal(int lv) => Big.Clamp((Big.Pow(1.06, lv) * 4 + BaseDef) * LvMul * RootMul);

    Big CalcAtk() => AtkIfBlade(S.EnhBlade);
    Big CalcHp() => HpIfRobe(S.EnhRobe);
    Big CalcDef() => DefIfSeal(S.EnhSeal);

    /// <summary>
    /// 暴击率 = 2% + 墨眸 0.15%/级，60% 封顶。
    /// **面板与实战共用这一份**：旧版面板自己写成 `0.02 + 0.0015×lv` 没封顶，
    /// 墨眸过 387 级后行内会显示 62%、66% 这种根本不可能出现的暴击率。
    /// </summary>
    public static double CritRateOf(int eyeLv) => Math.Min(0.60, 0.02 + 0.0015 * eyeLv);

    /// <summary>
    /// 暴击伤害 = **50% × 1.06^墨眸等级**（每级 +6%，指数）。
    ///
    /// v0.13.0 由「0.5 + 封顶后溢出的暴击率线性转成暴伤」改成指数成长。
    /// 原因：另外三件每级都是 ×1.06 的指数，而暴伤是线性 —— 同样一笔墨晶，
    /// 投墨眸的期望伤害回报只有投墨刃的 1/68。指数成本买线性收益，这条线必然是废属性。
    /// 改成指数后期望伤害 ≈ 1 + 0.6 × 暴伤（暴击率封顶 0.6），与墨刃同阶，
    /// 前期（暴击率还低）依然略弱一点，靠「暴击有惊喜感」补回来。
    ///
    /// 注意：10000 级时 1.06^10000 ≈ 1e+253，仍在 double 范围内；
    /// 再往上乘到伤害上会被 Big.Clamp 收到 1.8e+303，不会溢出。
    /// </summary>
    public static double CritDmgOf(int eyeLv) => 0.5 * Math.Pow(1.06, eyeLv);

    public double CritRate => CritRateOf(S.EnhEye);
    public double CritDmg => CritDmgOf(S.EnhEye);

    // ---- 等级与经验（4.1，v0.13.0 重做）----
    /// <summary>升级需求 = 「本轮最高层」单只经验 × 需要的只数。基础只数。</summary>
    public const double ExpKillsBase = 2.0;
    /// <summary>需要只数随层数的增长系数：k = 2 + 0.58√层。</summary>
    public const double ExpKillsSlope = 0.58;

    /// <summary>
    /// 算需求用的层数：取**本轮到达过的最高层**，不是当前层。
    /// 用当前层的话，退回第 1 层可以「1 级 / 2.6 只怪」无限刷级——这条线必须只增不减。
    /// </summary>
    public int ExpLayer => Math.Max(1, Math.Max(S.Layer, S.MaxLayer));

    /// <summary>升一级大约要打几只同层小怪（精英 1.5×、守层者 3× 折算）。</summary>
    public double ExpKills => ExpKillsBase + ExpKillsSlope * Math.Sqrt(ExpLayer);

    /// <summary>
    /// 升级所需经验：第 1 层约 2.6 只小怪一级，300 层约 12 只（≈一层），
    /// 3000 层约 34 只（≈两三层）。**难度只增不减**，不会再出现后期一只怪连升几千级。
    /// </summary>
    public Big ExpNeed => Big.Clamp(MobExp(ExpLayer) * ExpKills);

    /// <summary>
    /// 精英的经验倍率（相对同层单只小怪）：**2 只精英 ≈ 一级**。
    /// 低层取 1.5 的下限（那时 ExpKills 只有 2 点几，按比例算会让第 5 只一次给半级多）。
    /// 3000 层 ExpKills≈34 → 16.9 倍；386 层 ≈ 13.4 → 6.7 倍。
    /// </summary>
    public double EliteExpMul => Math.Max(1.5, ExpKills * 0.5);

    /// <summary>守层者的经验倍率：**一只守层者 ≈ 一级**（下限 3 保住前期）。</summary>
    public double KeeperExpMul => Math.Max(3.0, ExpKills);


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
        // 基准值 × 种类倍率（同一层内 10 只均值 = 基准，故总体曲线不变）
        var hp = MobHpOf(n) * MobKindAt(n, MobIndex).HpMul;
        if (MobIndex == 5) hp = hp * 1.6;
        else if (MobIndex == 10) hp = hp * 2.5;
        MobHpMax = Big.Clamp(hp);
        MobHp = MobHpMax;
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
        EnsureDaily();   // 跨零点时窗口一直开着也要自己翻页，不能等重启
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
        var dmg = atk - MobDefNow * 0.5;
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
        Hp = Hp - TakenDamage(MobAtkNow, PlayerDef);
        if (Hp.CompareTo(Big.Zero) <= 0) Exhaust();
    }

    /// <summary>
    /// 玩家这一回合挨多少伤害：**atk² / (atk + def×3)**，下限 atk × 5%。
    ///
    /// v0.13.0 由「atk − def×0.5」（减法减伤，最低 1 血）改成这个软曲线。原来那套有两个死结：
    ///   · 防御一旦超过怪攻的 2 倍，伤害被 clamp 到 **1 血/回合**，血量上亿也打不完，
    ///     力竭 → 休整 → 自动回退这套机制在中期彻底作废；
    ///   · 怪攻只要涨过那个阈值，伤害立刻从「1」跳到「按亿计」，两回合把人打死 ——
    ///     中间没有任何过渡带。减法减伤遇到两边都在指数增长，跨阈值必然断崖。
    ///
    /// 新公式的性质：def = atk 时受伤 = atk/4（减伤 75%）；def 是 atk 的 10 倍时 = atk/31；
    /// def → 0 时 ≈ atk（全吃），与旧公式一致；永远平滑，没有阈值。
    /// 保留 5% 下限是**故意**的：防御再高也总会掉一点血，力竭机制必须一直活着。
    /// </summary>
    public static Big TakenDamage(Big atk, Big def)
    {
        if (atk.IsZero) return Big.Zero;
        var soft = atk * atk / (atk + def * 3);
        var low = atk * 0.05;
        return soft.CompareTo(low) < 0 ? low : soft;
    }

    void KillMob()
    {
        S.TotalKills++;
        // 打赢一只就算「没在连败」——自动回退判的是**连败** 3 次，
        // 旧版这里漏了清零，实际变成「累计 3 次力竭」，中间赢多少只都拦不住。
        ConsecutiveLoss = 0;

        var g = MobGold(S.Layer);
        var e = MobExp(S.Layer);
        var ink = Big.Zero;
        // 经验倍率随层数放大，金币倍率保持 1.5 / 3 不变（金币是经济，经验是节奏，两件事）。
        // 目的：几千层时「两三只精英 = 一级」「一只守层者 = 一级」，与设计稿 4.1 的口径对齐；
        // 低层（ExpKills 还小）不加成，保持「打两只小怪升一级」的前期手感。
        if (MobIndex == 5) { g = g * 1.5; e = e * EliteExpMul; ink = Big.Of(1); }
        else if (MobIndex == 10) { g = g * 3; e = e * KeeperExpMul; ink = Big.Of(3); }
        AddGold(g);
        AddInk(ink);
        AddExp(e);

        MobIndex++;
        if (MobIndex > 10)
        {
            // 打赢守层者 = 本层已通。这跟「要不要继续往下走」是两件事：
            // 定层驻守时也得把本层记进 MaxLayer，否则界面上「本轮最高」永远差一层，
            // 刚开局就锁在第 1 层的话更是永远停在 0。
            if (S.Layer > S.MaxLayer) S.MaxLayer = S.Layer;

            // 只有「自动下潜 且 未锁层」才往下走。旧版这里是无条件 S.Layer++，
            // 所谓「定层驻守」根本锁不住——打完第 10 只照样被推到下一层。
            if (S.AutoDescend && !S.LockLayer && S.Layer < Limits.MaxLayer) S.Layer++;
            if (S.Layer >= Limits.MaxLayer) S.Layer = Limits.MaxLayer;

            MobIndex = 1;
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

    /// <summary>
    /// 一个金币袋折合多少只同层怪的金币（第七章）。
    /// 原为 5.0，实测打字收入过重（300 键 ≈ 半层战斗收入），按需求**减半为 2.5**。
    /// 只砍袋值，不改掉率（300 键一袋）与品质倍率（×1 / ×2.5 / ×6）。
    /// </summary>
    public const double BagMobMul = 2.5;

    /// <summary>单袋金币（含等级乘区与品质倍率）。袋值与每日礼包共用同一把尺子。</summary>
    public Big BagValue(double mult)
        => Big.Clamp(MobGold(S.Layer) * BagMobMul * (1 + 0.015 * (S.Level - 1)) * mult);

    // ---- 每日敲击（4.x / 7.x）----
    /// <summary>每日礼包档位。索引即 <see cref="SaveDto.DailyMask"/> 的位序。</summary>
    public static readonly int[] DailyTiers = { 5000, 20000 };

    /// <summary>
    /// 日切。**本地日期**变了就把今日敲击与领取标记清空。
    /// 三个入口都要调：主循环（跨零点时窗口开着）、敲击回调（暂停时 Tick 不跑）、读档后。
    /// </summary>
    public void EnsureDaily()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (S.DailyDate == today) return;
        S.DailyDate = today;
        S.DailyKeys = 0;
        S.DailyMask = 0;
    }

    /// <summary>该档位是否已达门槛且未领。</summary>
    public bool DailyReady(int tier)
    {
        EnsureDaily();
        if (tier < 0 || tier >= DailyTiers.Length) return false;
        return S.DailyKeys >= DailyTiers[tier] && (S.DailyMask & (1 << tier)) == 0;
    }

    public bool DailyClaimed(int tier)
        => tier >= 0 && tier < DailyTiers.Length && (S.DailyMask & (1 << tier)) != 0;

    /// <summary>礼包金币：按当前层单只金币缩放，与金币袋同一把尺子（1 袋 = 2.5 只怪，同样吃等级乘区）。</summary>
    public Big DailyGold(int tier)
        => Big.Clamp(MobGold(S.Layer) * (tier == 0 ? 60 : 240) * (1 + 0.015 * (S.Level - 1)));

    /// <summary>礼包墨晶：随本轮最高层缓慢长，封顶——前期是真保底，后期不抢商店生意。</summary>
    public int DailyInk(int tier)
    {
        int n = tier == 0 ? 8 + S.MaxLayer / 8 : 30 + S.MaxLayer / 4;
        return Math.Clamp(n, tier == 0 ? 8 : 30, tier == 0 ? 40 : 150);
    }

    public bool ClaimDaily(int tier, bool silent = false)
    {
        if (!DailyReady(tier)) return false;
        var gold = DailyGold(tier);
        int ink = DailyInk(tier);
        AddGold(gold);
        AddInk(Big.Of(ink));
        S.DailyMask |= 1 << tier;
        if (!silent) Log.Write($"每日礼包（{DailyTiers[tier]:N0} 档）：金币 +{Fmt.Large(gold)} · 墨晶 +{ink}");
        Changed?.Invoke();
        return true;
    }

    // 注：这里原先还有一个 ClaimAll「一键全领」，会把每日礼包两档连同金币袋一次带走。
    // 按需求改成**每日礼包只能逐档手动领**，因此撤掉了这个批量入口——
    // 金币袋本身点一行就是全部入账（ClaimBags），礼包则必须玩家自己点档位行。

    long bagCounter;
    public void OnKeyPress()
    {
        if (!S.ListenKeys) return;
        EnsureDaily();
        S.TotalKeys++;
        S.DailyKeys++;
        bagCounter++;
        if (bagCounter < BagKeys) return;
        bagCounter = 0;

        double tier = Rng.NextDouble();
        double mult = tier < 0.70 ? 1 : tier < 0.95 ? 2.5 : 6;
        var val = BagValue(mult);
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

    /// <summary>
    /// 每级成本倍率。v0.13.0 由 1.062 上调到 **1.07**：
    /// 原值只比金币收入（×1.06/层）快 0.2%，等级乘区一补就把差距吃掉，等于没有墙；
    /// 1.07 让每 10 级相对变弱约 9%，玩家才真的会在某个层数停下来考虑转生。
    /// </summary>
    public const double EnhRate = 1.07;
    const double EnhRateMinus1 = EnhRate - 1;

    public Big EnhCost(int which, int level)
        => Big.Clamp(Big.Pow(EnhRate, level - 1) * (2 * EnhFactor(which)));

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
        // （升级（AddExp）同样只抬上限、不回血，见 4.x；会回满的只有转生与休整结束。）
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
        var rn = Big.Pow(EnhRate, times);
        return Big.Clamp(c1 * ((rn - Big.Of(1)) / EnhRateMinus1));
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
        double n = Math.Log(1 + ink * EnhRateMinus1 / c1) / Math.Log(EnhRate);
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

    /// <summary>
    /// 「max」档到底是多少：把当前金币全花光能买的颗数。
    /// 金币是 Big（1.8e+303 封顶），所以数量也用 Big —— 早先按 int 封顶 10 亿，
    /// 到中期就变成「max 反而不 max」，玩家看到的 ×1B 是个假的 max。
    /// </summary>
    public Big AffordCount(double unitPrice)
    {
        if (unitPrice <= 0) return Big.Zero;
        double d = Gold.ToDouble() / unitPrice;
        if (double.IsNaN(d) || d < 1) return Big.Zero;
        var n = Big.Of(Math.Floor(d));
        // 浮点除法可能多算出一颗，往下退到真的买得起为止（差千分之一看不出来）
        var p = Big.Of(unitPrice);
        for (int i = 0; i < 6 && p * n > Gold; i++) n = n * 0.9999;
        return p * n > Gold ? Big.Zero : n;
    }

    public bool BuyInk(int count) => BuyInk(Big.Of(count));

    public bool BuyInk(Big count)
    {
        var cost = Big.Of((double)InkPrice) * count;
        if (count.CompareTo(Big.Of(1)) < 0) return false;
        if (Gold.CompareTo(cost) < 0) return false;
        var g = Gold - cost;
        S.GoldM = g.M; S.GoldE = g.E;
        AddInk(count);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 离下一级还差多少墨晶。返回 Big：墨刃升到 300 级以上时单价就过 20 亿了，
    /// 早先按 int 返回会直接溢出成负数，界面上变成「已够」但其实差着一大截。
    /// </summary>
    public Big InkShortfall(int which)
    {
        if (EnhLevel(which) >= Limits.MaxEnhance) return Big.Zero;
        var cost = EnhCost(which, EnhLevel(which) + 1);
        var need = cost - Ink;
        return need.CompareTo(Big.Zero) > 0 ? need : Big.Zero;
    }

    public void FillToNext(int which)
    {
        var need = InkShortfall(which);
        if (need.CompareTo(Big.Zero) <= 0) return;
        var afford = AffordCount(InkPrice);
        var buy = Big.Min(need + Big.Of(1), afford);   // +1 收掉浮点零头，免得点完还差一丝
        if (buy.CompareTo(Big.Of(1)) < 0) return;
        BuyInk(buy);
    }

    /// <summary>墨匣：250 金币，7 档概率（期望 87%）。</summary>
    public static readonly (double p, bool isInk, double amount)[] BoxTable =
    {
        (0.30, false, 100), (0.20, false, 300), (0.08, false, 750),
        (0.25, true, 1),    (0.10, true, 5),     (0.05, true, 15),
        (0.02, true, 60),
    };

    public string? OpenBox(int count) => OpenBox(Big.Of(count));

    public string? OpenBox(Big count)
    {
        if (count.CompareTo(Big.Of(1)) < 0) return null;
        var cost = Big.Of(250.0) * count;
        if (Gold.CompareTo(cost) < 0) return "金币不足";
        var g = Gold - cost;
        S.GoldM = g.M; S.GoldE = g.E;

        // 「开匣次数」是 long 统计量：max 档在金极多时一次开的是天文数字，饱和而不是溢出
        double dc = count.ToDouble();
        long add = dc >= long.MaxValue - S.BoxesOpened
            ? long.MaxValue - S.BoxesOpened
            : (long)Math.Round(dc);
        S.BoxesOpened += add;

        var hits = RollBoxes(count);

        var gold = Big.Zero;
        var ink = Big.Zero;
        for (int i = 0; i < BoxTable.Length; i++)
        {
            if (hits[i] <= 0) continue;
            var amt = Big.Of(BoxTable[i].amount) * Big.Of(hits[i]);
            if (BoxTable[i].isInk) { AddInk(amt); ink = ink + amt; }
            else { AddGold(amt); gold = gold + amt; }
        }

        Changed?.Invoke();

        if (count.CompareTo(Big.Of(2)) < 0)
        {
            // 单开保留原来的逐档文案（"末档"这种说法对一匣没意义）
            for (int i = 0; i < BoxTable.Length; i++)
                if (hits[i] > 0)
                    return (BoxTable[i].isInk ? "墨晶 +" : "金币 +") + BoxTable[i].amount.ToString("0");
            return "";
        }

        var part = new List<string>();
        if (gold.CompareTo(Big.Zero) > 0) part.Add("金币 +" + Fmt.Large(gold));
        if (ink.CompareTo(Big.Zero) > 0) part.Add("墨晶 +" + Fmt.Large(ink));
        string brief = $"{Fmt.Large(count)} 匣：" + string.Join(" · ", part);
        Log.Write(brief);
        return brief;
    }

    /// <summary>
    /// 掷 n 个墨匣，返回每一档命中的次数（只管抽，不发放奖励）。
    /// n 小时逐次抽；n 大时改用多项分布的正态近似 —— 2000 次以上时两者在统计上
    /// 不可分辨，但耗时恒定，否则 max 档一次开上亿个会把界面冻死。
    /// 计数用 double 而不是 long：max 档的数量跟着金币走，能到 1e+300。
    /// </summary>
    static double[] RollBoxes(Big count)
    {
        int m = BoxTable.Length;
        var hits = new double[m];

        if (count.CompareTo(Big.Of(2000)) <= 0)
        {
            int n = (int)Math.Round(count.ToDouble());
            if (n < 0) n = 0;
            for (int i = 0; i < n; i++)
            {
                double r = Rng.NextDouble(), acc = 0;
                for (int k = 0; k < m; k++)
                {
                    acc += BoxTable[k].p;
                    if (r <= acc) { hits[k]++; break; }
                }
            }
            return hits;
        }

        double nn = count.ToDouble();
        var v = new double[m];
        double sum = 0;
        for (int k = 0; k < m; k++)
        {
            double mu = nn * BoxTable[k].p;
            double sd = Math.Sqrt(mu * (1 - BoxTable[k].p));
            double x = mu + sd * Gaussian();
            if (x < 0) x = 0;
            v[k] = x;
            sum += x;
        }
        if (sum <= 0) { hits[0] = nn; return hits; }

        // 先算占比再乘总数：v[k]*n 在 n≈1e300 时会溢出成 inf
        double others = 0;
        for (int k = 1; k < m; k++)
        {
            hits[k] = v[k] / sum * nn;
            others += hits[k];
        }
        hits[0] = nn - others;                        // 零头补回第一档（通常只差个位数）
        if (hits[0] < 0) hits[0] = 0;
        return hits;
    }

    /// <summary>标准正态随机数（Box–Muller）。</summary>
    static double Gaussian()
    {
        double u1 = 1.0 - Rng.NextDouble();
        double u2 = Rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    // ---- 转生（第六章）----
    public bool CanRebirth => S.MaxLayer >= 50;
    public int RebirthGain => Math.Max(1, (int)Math.Floor(0.35 * S.MaxLayer));

    /// <summary>
    /// 转生。界线是「能不能带走」：**现金没收，投入保留**。
    ///
    /// 金币、墨晶、待领金币袋清零；四件强化等级、墨根、统计、设置、每日领取位全部保留。
    /// 理由是喂进强化里的墨晶已经变成永久等级，那属于「投入」；而躺在账上的钱是「现金」，
    /// 它随着重推的指数收入会迅速缩成零头，留着只会让转生少一层"从头再来"的分量。
    /// 顺带逼出一步正经决策：转生前把墨晶全喂进强化，带不走的才不该剩。
    ///
    /// 待领金币袋必须一起清：袋子是按当前层算好值的，留着等于让玩家转生后领走旧层的钱。
    /// </summary>
    public void Rebirth()
    {
        if (!CanRebirth) return;
        int gain = RebirthGain;
        S.Root = Math.Min(Limits.MaxSoul, S.Root + gain);
        S.Rebirths++;

        // 先记账再清零：日志里要留一行「清了多少」，否则玩家事后无法核对，
        // 只会记得"我转生之后钱没了"。
        var goldBefore = Gold;
        var inkBefore = Ink;
        int bagN = S.Bags.Count;
        S.GoldM = 0; S.GoldE = 0;
        S.InkM = 0; S.InkE = 0;
        S.Bags.Clear();

        S.Layer = 1; S.MaxLayer = 0; S.Level = 1;
        S.ExpM = 0; S.ExpE = 0;
        ConsecutiveLoss = 0;
        Resting = false;
        RestUntil = DateTime.MinValue;
        bagCounter = 0;          // 袋值按当前层算，转生回第 1 层就不该带着旧层的敲击进度
        ResetFight();
        Log.Write($"转生：墨根 +{gain}（累计 {S.Root}）· 清零金币 {Fmt.Large(goldBefore)} / 墨晶 {Fmt.Large(inkBefore)}"
                  + (bagN > 0 ? $" / 金币袋 ×{bagN}" : ""));
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

    // ---- 初始化重置（设置 › 危险操作 › 初始化重置）----

    /// <summary>
    /// 把存档恢复成「刚下载」的样子。
    ///
    /// 做法是整份 <see cref="SaveDto"/> 换新对象，而不是逐字段清零 —— 逐字段清最容易漏，
    /// 以后往 SaveDto 加字段时还得记得回来补一行。新对象的属性默认值就是新档该有的值
    /// （第 1 层、Lv1、无金币墨晶、设置全开、各项统计 0）。
    ///
    /// 重置完**立刻落盘**：否则玩家重置后直接结束进程，下次启动又会读回旧档，
    /// 表现成「重置没生效」。
    /// </summary>
    public void ResetAll()
    {
        S = new SaveDto();

        // 运行期状态不在 SaveDto 里，得单独归零，否则界面上还挂着旧的血量/怪物/力竭
        HpMax = CalcHp();
        Hp = HpMax;
        MobIndex = 1;
        Resting = false;
        RestUntil = DateTime.MinValue;
        ConsecutiveLoss = 0;
        roundAccum = 0;
        bagCounter = 0;
        Status = "战斗中";
        SpawnMob();

        Save();
        Log.Write("初始化重置：存档已恢复为初始状态（层数/等级/金币/墨晶/本命器/墨根/统计/设置全部清零）");
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
            EnsureDaily();
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
        EnsureDaily();   // 关了一整夜再开：今日敲击从 0 起算
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
