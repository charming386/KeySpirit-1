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
    public int DailyBags { get; set; }                           // 今日已掉几袋（上限 BagKeysPerDay，日切重置）
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
    /// <summary>
    /// 存档结构版本。老档没有这个字段，反序列化出来是 0 —— 迁移逻辑正是靠这一点
    /// 判断「这是没迁过的档」。每次改动旧档语义都要 +1，并在 MigrateSave 里补一段。
    /// </summary>
    public int Schema { get; set; }
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
    /// <summary>
    /// 墨潮倍率：第 9,000 层起每层 ×1.11，**到第 10,000 层封顶**（×1.11^1000 ≈ 1e+45.8）。
    ///
    /// v0.17.0 补上的封顶。原来写成 `1.11^(n-9000)` 一路涨，只因为层数被锁在 10,000 层，
    /// 永远不会算到 1 万层以上。层数上限抬到 10 万之后，这条曲线会在第 48,000 层左右
    /// 把怪血顶到 1.8e+3000 并 clamp —— 再往上怪血不再增长，玩家永远打不死。
    /// 让墨潮在万层处停住，10,000 层以上的怪回到 `1.06^层` 的正常曲线：
    /// 「万层冲刺」的设计保留（9,999 层怪血仍是 9.6e+299），万层之后是一条能一直爬的常规塔。
    /// </summary>
    static double MoChao(int n) => n > 9000 ? Math.Pow(1.11, Math.Min(n, 10000) - 9000) : 1.0;

    /// <summary>
    /// 怪物攻击每层增幅。**v0.18.1 由 1.05 提到 1.0515**。
    ///
    /// 旧值 1.05 与墨衣（1.06）之间每层漂移 0.95%，619 层累积 348 倍，
    /// 叠上基础比 50/6 之后，620 层的「怪血 / 怪攻」= 2944 倍、而玩家「攻击 / 怪攻」= 520 倍：
    /// 玩家一刀 = 怪物 520 次攻击，一条命要挨 2826 回合才力竭 ≈ **54 层**，
    /// 而且因为怪攻追不上墨衣，**越往深处反而越安全**（100 层 12.8 层 → 900 层 85.9 层），
    /// 力竭 / 休整 / 三连败回退在 400 层之后基本不触发；墨印也会在约 1200 层撞回 2% 地板。
    ///
    /// 提到 1.0515 之后，620 层怪攻 7.84e+13 → 1.91e+14（×2.4），一条命 54 → 10 层，
    /// 曲线改为单调向下（9.9 → 9.3 → 11.7 → 9.9 → 7.0），墨印归零节点推到 2000 层以后。
    /// 1 层完全不动（1.0515⁰ = 1）、10 层只高 1.4%，前期毫无感觉。
    ///
    /// 为什么不直接对齐到 1.06：玩家生命是 `1.06^墨衣等级`，620 层时墨衣 550 级，
    /// 与层数差 70 级 = 56 倍劣势；怪攻只要有 1.06，玩家 0.3 回合就被打死（现在需要 2826 回合）。
    /// 也不能靠抬基础值 6 → 20 顶替：那会让 1 层的一条命从 14 层掉到 1.6 层，前期直接卡死。
    /// </summary>
    public const double MobAtkExp = 1.0515;

    public static Big MobHpOf(int n) => Big.Clamp(Big.Pow(1.06, n - 1) * (50 * MoChao(n)));
    public static Big MobAtk(int n) => Big.Clamp(Big.Pow(MobAtkExp, n - 1) * (6 * MoChao(n)));
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

    /// <summary>
    /// 等级乘区：**只作用于攻击**，且 v0.19.0 起改为**平方根级**（`1 + 0.015√(级−1)`）。
    ///
    /// 原来是 `1 + 0.015×(级−1)`，99 级就是 ×2.49，配合本体攻的线性加成后，
    /// 玩家总攻击对等级是**二次**增长（0.015·级²）；而怪物血量是 1.06^层。
    /// 二次曲线在 90 层前一直跑赢指数，之后突然翻转 —— 于是「前 90 层零阻力、90 层撞墙」。
    ///
    /// 改平方根之后 99 级只有 ×1.149（几乎不涨），攻击增长基本只剩「本体攻 +1/级」这条线，
    /// 打怪回合数随层数单调递增 —— 这就是策划要的**越来越刮痧**：
    /// 前期 3~5 回合一只，72 层 9 回合，120 层 20 回合，靠强化墨刃才能把节奏拉回来。
    /// 之所以不直接砍系数，是因为砍系数会把前期一起砍死（前 10 层就变 8 回合，开局就闷）。
    /// </summary>
    Big LvMul => Big.Of(1 + 0.015 * Math.Sqrt(S.Level - 1));
    Big RootMul => Big.Pow(1.06, S.Root);

    // ---- 等级加点（v0.13.0）----
    // 等级不再只是乘区：每级直接给基础攻/血/防加一点「本体数值」。
    // 前 50 层（基础值就是全部）时这点加成是实打实的强度。真正的长线增长靠强化。
    //
    // v0.19.0：攻击的点数不动（1/级，但乘区被压成平方根，合计增长大幅放慢），
    // **生命 10 → 18、防御 0.5 → 0.9** 反向抬起来。这一升一降不是随手调的：
    // 攻击涨得慢 → 击杀回合数变多 → 挨打次数变多，血池必须跟着变厚，
    // 否则「刮痧」会变成「一碰就死」。实测 72 层（99 级）生命 1884 / 怪攻 208，
    // 一只小怪打 9 回合、一条命能撑 34 回合 ≈ 3~4 只，节奏是「磨」而不是「秒」。
    public const double LvAtkGain = 1.0;
    public const double LvHpGain = 18.0;
    public const double LvDefGain = 0.9;

    /// <summary>基础攻击 = 10 + 每级加点（未乘等级乘区/墨根，供面板拆解用）。</summary>
    public Big BaseAtk => Big.Clamp(Big.Of(10 + LvAtkGain * (S.Level - 1)));
    public Big BaseHp => Big.Clamp(Big.Of(100 + LvHpGain * (S.Level - 1)));
    public Big BaseDef => Big.Clamp(Big.Of(3 + LvDefGain * (S.Level - 1)));

    /// <summary>
    /// 「假如这一件已经到 lv 级」时的最终属性：**本体值 + 强化 +（攻击的）等级乘区 + 墨根乘区**。
    ///
    /// 面板的强化预览行必须走这三个方法。旧版面板自己写了 `Big.Pow(1.06, lv) * 10`，
    /// 只算了强化那一项 —— 同一块屏幕上强化页写「攻击 3.0B」、角色页写「攻击 35.0B」，
    /// 差的正是等级乘区（×11.65）；转生之后还会再差一个墨根乘区（墨根 50 时 ×214）。
    /// 这类「面板自己抄一遍公式」的错已经不是第一次了（暴击封顶也是同一个毛病）。
    ///
    /// **v0.18.0：等级乘区只留给攻击。** 见 HpIfRobe 上的说明。
    /// **v0.19.0：等级乘区改为平方根级**（见 LvMul），攻击对等级从二次增长降为近线性，
    /// 于是「每只怪要打的回合数」随层数单调上升 —— 越来越刮痧。强度的大头随之从
    /// 「自动到手的等级」搬回「玩家自己点的墨刃」。
    /// </summary>
    public Big AtkIfBlade(int lv) => Big.Clamp((Big.Pow(1.06, lv) * 10 + BaseAtk) * LvMul * RootMul);

    /// <summary>
    /// 生命 = (墨衣 **120** × 1.06^lv + 等级加点) × 墨根，**不吃等级乘区**（v0.18.0）。
    ///
    /// 为什么摘掉：墨衣每级 +6% 本身没问题（墨刃也是 +6%），问题是这 +6% 还额外乘了
    /// `LvMul = 1 + 0.015×(级−1)`，而怪物攻击 `6 × 1.05^层` **一点乘区都不吃**。
    /// 攻击那 20 倍是必需的——它要去跟「怪血 50 × 1.06^层」赛跑，没有它升级就没有意义；
    /// 生命的 20 倍没有任何对手来消耗，纯粹白送。实测 619 层存档：生命 7.09e+16、
    /// 怪攻 7.47e+13，整整 **949 倍**差三个数量级，于是
    ///   · 一条命要挨 **1.9 万回合**才力竭 ≈ 258 层，休整 / 自动回退 / 力竭率表从未触发过；
    ///   · 一级墨衣给的血 = **60 只怪的攻击力**，而一级墨刃只推进 1.1% 只怪血。
    /// 摘掉乘区后 949 → 47（一条命 ≈ 13 层），1 层与 50 层完全无感（那时 LvMul ≈ 1）。
    ///
    /// 基础值 80 → 120 是补偿：同时把防御的等级乘区也摘了（见 DefIfSeal），伤害变高，
    /// 血池要跟着抬一点，最终落在「一条命 10~20 层」。
    ///
    /// **v0.19.0：血池重新加厚（每级 10 → 18）。** 攻击被压成平方根后击杀回合数一路变多，
    /// 一条命要挨的打数跟着涨；血不够厚的话「刮痧」会退化成「力竭循环」。实测 72 层（99 级）
    /// 生命 1884、怪攻 208、一只小怪 9 回合，一条命撑得下 3~4 只 —— 磨得动，也不至于无脑。
    /// </summary>
    public Big HpIfRobe(int lv) => Big.Clamp((Big.Pow(1.06, lv) * 120 + BaseHp) * RootMul);

    /// <summary>
    /// 防御 = (墨印 4 × 1.06^lv + 等级加点) × 墨根，**同样不吃等级乘区**（v0.18.0）。
    ///
    /// 防御和生命是一个病：吃了 20 倍乘区之后，619 层存档的防御是怪攻的 **67 倍**，
    /// 软减伤算出来只有 0.44%，被 5% 地板整个盖住 —— 墨印从约 200 层起**每级收益为 0**，
    /// 而它是四件里第二贵的（1.15 倍）。摘掉乘区后「防/怪攻」回到 1.0~3.3，
    /// 掉血 4.8%~13.8%，每一级墨印都能看到掉血百分比下降（+1 级约 −0.3~0.6 个百分点）。
    /// </summary>
    public Big DefIfSeal(int lv) => Big.Clamp((Big.Pow(1.06, lv) * 4 + BaseDef) * RootMul);

    Big CalcAtk() => AtkIfBlade(S.EnhBlade);
    Big CalcHp() => HpIfRobe(S.EnhRobe);
    Big CalcDef() => DefIfSeal(S.EnhSeal);

    /// <summary>暴击率的基础值（不含墨眸，谁都有）。</summary>
    public const double CritRateBase = 0.02;

    /// <summary>墨眸每级给的暴击率。</summary>
    public const double CritRatePerEyeLv = 0.0015;

    /// <summary>
    /// **墨眸这一件**最多能给多少暴击率：30%（200 级喂满）。
    /// v0.16.0 由「墨眸一路喂到 60% 封顶」拆成两层：墨眸只负责一半，剩下 30 个点
    /// 留给以后的系统（新本命器 / 词条 / 转生天赋都行）。原来 387 级就把 60% 吃干，
    /// 这条线等于"一件通吃"，后来者没有位置可放。
    /// </summary>
    public const double CritRateEyeCap = 0.30;

    /// <summary>暴击率的**全局**硬上限：任何来源叠加都不许越过 60%。</summary>
    public const double CritRateCapTotal = 0.60;

    /// <summary>墨眸把暴击率喂满所需等级（200 × 0.15% = 30%）。</summary>
    public const int CritRateFullLv = 200;

    /// <summary>墨眸贡献的暴击率（自身封顶 30%，不含基础值）。</summary>
    public static double CritRateEyeOf(int eyeLv)
        => Math.Min(CritRateEyeCap, CritRatePerEyeLv * Math.Max(0, eyeLv));

    /// <summary>
    /// 当前总暴击率 = 基础 2% + 墨眸（封顶 30%）+ 额外来源（现在为 0），全局封 60%。
    /// **面板与实战共用这一份**：旧版面板自己写成 `0.02 + 0.0015×lv` 没封顶，
    /// 墨眸过 387 级后行内会显示 62%、66% 这种根本不可能出现的暴击率。
    /// </summary>
    public static double CritRateOf(int eyeLv) => CritRateTotal(eyeLv, 0);

    public static double CritRateTotal(int eyeLv, double extra)
        => Math.Min(CritRateCapTotal, CritRateBase + CritRateEyeOf(eyeLv) + Math.Max(0, extra));

    /// <summary>暴伤的基础加成（墨眸 0 级时就是 +50%）。</summary>
    public const double CritDmgBase = 0.5;

    /// <summary>
    /// 墨眸每级 +0.001 倍暴伤（+0.1%）。
    ///
    /// v0.16.1 由 +1%/级 降到 +0.1%/级，并且**取消封顶**（v0.16.0 的 +400% 硬顶作废）。
    /// 用户的判断是对的：封顶等于给这条线判死刑，喂到 350 级以后墨眸就再也买不动了，
    /// 「可以一直练」比「练到某个数就停」更像挂机游戏的成长感。
    /// 代价是每级必须很小 —— 0.001/级 意味着 1000 级才 +1.0 倍，
    /// 而成本是 ×1.07 的指数，后期每级买到的东西越来越少，这是**刻意**的：
    /// 暴伤乘在最终伤害外面，一旦让它涨得快，就又回到"一次暴击秒 boss"的二元战斗。
    /// </summary>
    public const double CritDmgPerLv = 0.001;

    /// <summary>
    /// 暴击伤害 = **+50% 基础，每级 +0.1%，无上限**。
    ///
    /// v0.13.0 曾改成 `0.5 × 1.06^墨眸等级`（指数），理由是"指数成本该买指数收益"。
    /// 那条判断只对了一半：墨刃的 1.06 乘在攻击上，而攻击要跟 `怪血 = 50 × 1.06^层`
    /// 赛跑；暴伤的 1.06 乘在**最终伤害外面**，等价于把墨刃的指数再来一遍 ——
    /// 实际伤害按 `1.06^(墨刃 + 墨眸)` 涨，而怪血只有 `1.06^层`。墨刃 535 + 墨眸 494
    /// 比层数 612 多出的 417 级，就是那 1.59 **万亿**倍暴伤：60% 的回合 1 击必杀，
    /// 40% 的回合磨二十几回合，中间没有第三种结果，防御/力竭/血量全部失去意义。
    ///
    /// v0.16.0 改成线性 + 封顶（+1%/级，+400% 到顶）—— 秒杀问题解决了，但"到顶即死线"；
    /// v0.16.1 按用户要求去掉封顶、把斜率压到 +0.1%/级：
    /// 200 级（暴击率喂满时）倍率 ×1.70、500 级 ×2.00、1000 级 ×2.50、10000 级 ×11.5。
    /// 期望倍率 = 1 + 32% × (倍率 − 1)，所以哪怕 10000 级也只有 ×4.4，永远不会变成秒杀。
    /// </summary>
    public static double CritDmgOf(int eyeLv)
        => CritDmgBase + CritDmgPerLv * Math.Max(0, eyeLv);

    public double CritRate => CritRateOf(S.EnhEye);
    public double CritDmg => CritDmgOf(S.EnhEye);

    // ---- 等级与经验（4.1，v0.13.0 重做，v0.19.0 重标定）----
    /// <summary>升级需求 = 「本轮最高层」单只经验 × 需要的只数。基础只数。</summary>
    public const double ExpKillsBase = 2.0;

    /// <summary>
    /// 需要只数随层数的增长系数：k = 2 + **1.6**√层（v0.19.0 由 0.58 上调）。
    ///
    /// 为什么必须上调：一层 10 只的经验当量 = 8 只小怪 + 精英 + 守层者 = 14 只当量（倍率固定后），
    /// 一级要 k 只 → **每层级数 = 14 / k**。0.58 时 k 从 2.6 涨到 6.9 才勉强把一层压到 2.7 级，
    /// 整条曲线于是永久停在「等级 ≈ 3 倍层数」：72 层必然 220 级，1000 层必然 1500 级，
    /// 等级完全由层数决定，玩家做什么都一样。实测删档重开零操作 70 分钟到 72 层 / 219 级。
    /// 提到 1.6 之后 k(72) = 15.6 → 一层 **0.9 级**，k(300) = 29.7、k(3000) = 89.6，
    /// 等级/层数落回 1.4 : 1（72 层 ≈ 99 级），且越深一层给的级数越少。
    /// </summary>
    public const double ExpKillsSlope = 1.6;

    /// <summary>
    /// 算需求用的层数：取**本轮到达过的最高层**，不是当前层。
    /// 用当前层的话，退回第 1 层可以「1 级 / 2.6 只怪」无限刷级——这条线必须只增不减。
    /// </summary>
    public int ExpLayer => Math.Max(1, Math.Max(S.Layer, S.MaxLayer));

    /// <summary>升一级大约要打几只同层小怪（精英 2×、守层者 4× 折算）。</summary>
    public double ExpKills => ExpKillsBase + ExpKillsSlope * Math.Sqrt(ExpLayer);

    /// <summary>
    /// 升级所需经验：第 1 层约 3.6 只小怪一级，72 层约 15.6 只（≈一层半），
    /// 3000 层约 90 只（≈九层）。**难度只增不减**，且每层给的级数随深度递减。
    /// </summary>
    public Big ExpNeed => Big.Clamp(MobExp(ExpLayer) * ExpKills);

    /// <summary>
    /// 精英的经验倍率（相对同层单只小怪）：**固定 2 倍**（v0.19.0 由「0.5×k」改为常数）。
    ///
    /// 原来按 k 缩放（`max(1.5, k×0.5)`）有个致命副作用：一层当量 = 8 + 0.5k + k = 8 + 1.5k，
    /// 除以 k 得 **每层级数 = 1.5 + 8/k ≥ 1.5**——数学上永远不可能低于一层 1.5 级，
    /// 无论把 k 抬到多大。这就是「等级永远是层数三倍」的地板，必须先把倍率改成固定值才拆得掉。
    /// </summary>
    public const double EliteExpMulConst = 2.0;

    /// <summary>守层者的经验倍率：**固定 4 倍**（v0.19.0，同样为了拆掉 1.5 级/层的地板）。</summary>
    public const double KeeperExpMulConst = 4.0;

    public double EliteExpMul => EliteExpMulConst;
    public double KeeperExpMul => KeeperExpMulConst;

    /// <summary>每层的小怪只数（第 10 只是守层者，第 5 只是精英）。</summary>
    public const int MiniPerLayer = 8;

    /// <summary>
    /// 从第 1 层一路打到第 <paramref name="maxLayer"/> 层、**按当前曲线应得的等级**（下限 1）。
    ///
    /// 一层给的经验当量 = 8 只小怪 + 1 只精英 + 1 只守层者
    /// = `MiniPerLayer + EliteExpMulConst + KeeperExpMulConst` = 14 只当量；
    /// 一级要 `k = 2 + 1.6√层` 只当量，所以这一层能升 `14 / k` 级。
    /// 逐层累加即可，不必逐只怪模拟。
    ///
    /// **v0.19.0 起倍率是常数**：旧版这里写 `max(1.5, k×0.5)` / `max(3, k)`，
    /// 让一层的级数恒等于 `1.5 + 8/k`，永远跌不到 1.5 以下，等级被锁死成层数的三倍。
    ///
    /// 唯一用途是**存档迁移**：把旧曲线（或测试档）攒下的虚高等级拉回这条线上。
    /// 它不是常驻上限——挂机反复刷层本来就该多拿等级，所以只在迁移时算一次。
    /// </summary>
    public static double LvCapAt(int maxLayer)
    {
        int n = Math.Clamp(maxLayer, 0, Limits.MaxLayer);
        double lv = 1.0;
        for (int L = 1; L <= n; L++)
        {
            double k = ExpKillsBase + ExpKillsSlope * Math.Sqrt(L);
            lv += (MiniPerLayer + EliteExpMulConst + KeeperExpMulConst) / k;
        }
        return lv;
    }


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
    /// 玩家这一回合挨多少伤害：**atk² / (atk + def×6)**，下限 atk × **2%**。
    ///
    /// v0.13.0 由「atk − def×0.5」（减法减伤，最低 1 血）改成这个软曲线。原来那套有两个死结：
    ///   · 防御一旦超过怪攻的 2 倍，伤害被 clamp 到 **1 血/回合**，血量上亿也打不完，
    ///     力竭 → 休整 → 自动回退这套机制在中期彻底作废；
    ///   · 怪攻只要涨过那个阈值，伤害立刻从「1」跳到「按亿计」，两回合把人打死 ——
    ///     中间没有任何过渡带。减法减伤遇到两边都在指数增长，跨阈值必然断崖。
    ///
    /// **v0.18.0 把系数 3 → 6、下限 5% → 2%**，配套「生命 / 防御都不吃等级乘区」：
    ///   · 摘掉乘区后「防/怪攻」落在 1.0~3.3，如果还保留 5% 下限，619 层算出来是 4.8%，
    ///     差一点点就撞地板，墨印的边际收益在高层又会归零；下限降到 2%（= 最多减伤 50 倍）
    ///     才留出「还能再练几十级」的空间。
    ///   · 系数 3 → 6 是把「防御=怪攻」时的减伤从 75% 提到 86%，否则中期（防/怪攻 ≈ 1）
    ///     每回合掉血 25%，一条命只剩 4 层，休整会变成主要玩法。
    ///
    /// 新公式的性质：def = atk 时受伤 ≈ atk/7（减伤 86%）；def 是 atk 的 3.3 倍时 ≈ atk/21（95%）；
    /// def → 0 时 ≈ atk（全吃），与旧公式一致；永远平滑，没有阈值。
    /// 保留 2% 下限是**故意**的：防御再高也总会掉一点血，力竭机制必须一直活着。
    /// </summary>
    public static Big TakenDamage(Big atk, Big def)
    {
        if (atk.IsZero) return Big.Zero;
        var soft = atk * atk / (atk + def * 6);
        var low = atk * 0.02;
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
    /// <summary>
    /// 掉落一袋所需敲击数（第七章：500 键 = 1 袋）。
    /// 沿革：100 → 300（v0.12.0，100 敲一袋太密）→ **500**（v0.15.0）。
    /// 300 档的问题不是"一袋多少钱"，而是**节奏太密**：一袋接一袋地掉，
    /// 玩家会把它当成第二条收入线，而不是顺手的一点补贴。
    /// </summary>
    public const int BagKeys = 500;

    /// <summary>
    /// 每日最多掉多少袋 = **40 袋 × 500 敲 = 20,000 敲**，正好等于每日礼包最高档的门槛。
    /// 到顶后继续敲不再掉袋（今日敲击数照涨，礼包门槛该到还是会到），次日日切自动恢复。
    /// 没有这条上限时，一天敲几万次键盘就能把金币收入顶掉一大截，
    /// 甚至不回战斗、光靠打字也养得起强化——那就等于第三条收入线了。
    /// </summary>
    public const int BagKeysPerDay = 40;

    /// <summary>
    /// 今日敲击的上限 = **40 袋 × 500 敲 = 20,000**，也就是每日礼包最高档的门槛。
    /// 面板上「今日敲击」的分母用它，而不是用「下一档还没到的门槛」——
    /// 分母跟着档位跳会让 4,800 / 5,000 看起来像「今天最多只能敲 5,000」。
    /// 到顶之后照旧计数，只是不再掉袋，界面上连分母一起去掉。
    /// </summary>
    public const int DailyKeyCap = BagKeys * BagKeysPerDay;

    /// <summary>
    /// 今日已经用掉多少个掉袋名额（0..40）。**由今日敲击数派生**，不读 <see cref="SaveDto.DailyBags"/>。
    ///
    /// 为什么要派生：掉袋本来就是「每 500 敲一袋」的确定节奏，袋数天然等于 `今日敲击 / 500`。
    /// 另存一个计数器会跟它分家——老存档升上来时 `DailyKeys` 有值而 `DailyBags` 是 0，
    /// 面板就会出现「今日敲击 4,800」配「金币袋 今日 0/40 袋」这种自相矛盾（v0.17.1 修）。
    /// 派生之后两边不可能再对不上，跨天/重开也都不需要额外同步。
    /// </summary>
    public int BagsToday => (int)Math.Min(S.DailyKeys / BagKeys, BagKeysPerDay);

    /// <summary>今日敲击数是否已用满掉袋名额（此时只在计数，不再掉袋）。</summary>
    public bool BagsTodayFull => S.DailyKeys >= DailyKeyCap;

    /// <summary>
    /// 一个金币袋折合多少只同层怪的金币（第七章）。
    /// 沿革：5.0 → 2.5（v0.12.2，打字收入过重）→ **1.25**（v0.15.0，再减半）。
    /// 两次都只砍袋值，不动品质倍率（×1 / ×2.5 / ×6）。
    /// </summary>
    public const double BagMobMul = 1.25;

    /// <summary>单袋金币（含等级乘区与品质倍率）。袋值与每日礼包共用同一把尺子。</summary>
    public Big BagValue(double mult)
        => Big.Clamp(MobGold(S.Layer) * BagMobMul * (1 + 0.015 * (S.Level - 1)) * mult);

    // ---- 每日敲击（4.x / 7.x）----
    /// <summary>每日礼包档位。索引即 <see cref="SaveDto.DailyMask"/> 的位序。</summary>
    public static readonly int[] DailyTiers = { 5000, 20000 };

    /// <summary>
    /// 日切。**本地日期**变了就把今日敲击、今日掉袋数与领取标记清空。
    /// 三个入口都要调：主循环（跨零点时窗口开着）、敲击回调（暂停时 Tick 不跑）、读档后。
    /// </summary>
    public void EnsureDaily()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (S.DailyDate == today) return;
        S.DailyDate = today;
        S.DailyKeys = 0;
        S.DailyBags = 0;
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

    /// <summary>
    /// 礼包金币：按当前层单只金币缩放，与金币袋同一把尺子（同样吃等级乘区）。
    /// **v0.15.0 起只有 5,000 档给金币**；20,000 档改成纯墨晶，返回 0。
    /// 两档都给金币时，低档那份纯粹是"顺手加成"，反而把两档的差异抹平了。
    /// </summary>
    public Big DailyGold(int tier)
        => tier == 0
           ? Big.Clamp(MobGold(S.Layer) * 60 * (1 + 0.015 * (S.Level - 1)))
           : Big.Zero;

    /// <summary>
    /// 礼包墨晶：随本轮最高层缓慢长，封顶——前期是真保底，后期不抢商店生意。
    /// **v0.15.0 起只有 20,000 档给墨晶**（5,000 档返回 0）：墨晶是强化材料、不吃等级乘区，
    /// 放到低档等于让"稍微敲几下"就能白拿素材，商店那条金币→墨晶的主链会被绕开。
    /// </summary>
    public int DailyInk(int tier)
        => tier == 0 ? 0 : Math.Clamp(30 + S.MaxLayer / 4, 30, 150);

    public bool ClaimDaily(int tier, bool silent = false)
    {
        if (!DailyReady(tier)) return false;
        var gold = DailyGold(tier);
        int ink = DailyInk(tier);
        if (gold.CompareTo(Big.Zero) > 0) AddGold(gold);
        if (ink > 0) AddInk(Big.Of(ink));
        S.DailyMask |= 1 << tier;
        if (!silent)
        {
            var parts = new List<string>();
            if (gold.CompareTo(Big.Zero) > 0) parts.Add($"金币 +{Fmt.Large(gold)}");
            if (ink > 0) parts.Add($"墨晶 +{ink}");
            Log.Write($"每日礼包（{DailyTiers[tier]:N0} 档）：{string.Join(" · ", parts)}");
        }
        Changed?.Invoke();
        return true;
    }

    // 注：这里原先还有一个 ClaimAll「一键全领」，会把每日礼包两档连同金币袋一次带走。
    // 按需求改成**每日礼包只能逐档手动领**，因此撤掉了这个批量入口——
    // 金币袋本身点一行就是全部入账（ClaimBags），礼包则必须玩家自己点档位行。

    public void OnKeyPress()
    {
        if (!S.ListenKeys) return;
        EnsureDaily();
        S.TotalKeys++;
        S.DailyKeys++;

        // 掉袋判据直接由今日敲击数派生（每满 500 敲一袋），不再另存一个 bagCounter：
        //   1) 累加器不落盘，重开一次就把零头（最多 499 敲）白扔了；
        //   2) 累加器和 DailyKeys 会分家 —— 老存档升上来时 DailyKeys 有值而 DailyBags 是 0，
        //      面板出现「今日敲击 4,800」配「金币袋 今日 0/40 袋」这种自相矛盾（v0.17.1 修）。
        // 派生之后「今日 N 袋」恒等于「今日敲击 / 500」，跨天、重开、老存档都不用额外同步。
        if (S.DailyKeys % BagKeys != 0) return;
        int slot = (int)(S.DailyKeys / BagKeys);      // 今天第几袋（从 1 起算）
        S.DailyBags = Math.Min(slot, BagKeysPerDay);  // 只作存档镜像，界面读 BagsToday

        // 每日掉袋上限：到顶后继续敲**不报错、不提示**，只是不再掉袋——
        // 今日敲击数照涨（每日礼包门槛该到还是会到），次日日切自动恢复。
        if (slot > BagKeysPerDay) return;

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
        // 只在当天刚好用满名额时记一条，之后不再刷屏
        if (slot == BagKeysPerDay)
            Log.Write($"今日金币袋已满 {BagKeysPerDay} 袋（{DailyKeyCap:N0} 敲），明日再掉");
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
    /// <summary>
    /// 四种本命器的单价系数（v0.16.0 按「对推进的紧要程度」重排，四件各不相同）：
    ///
    /// | 本命器 | 系数 | 干什么 | 为什么定这个价 |
    /// | --- | --- | --- | --- |
    /// | 墨衣（生命） | 0.70 | 扛打、拖长力竭前的续航 | 最便宜。血量只是"容错"，不直接换推进速度 |
    /// | 墨刃（攻击） | 1.00 | 直接决定几回合打死一只 | 基准价，1.00 是这套经济的锚点 |
    /// | 墨印（防御） | 1.15 | 减免伤，靠软曲线抗高层 | 跟墨刃同档、略贵一点（v0.16.0 由 0.70 提到 1.15：防御原来比血量还便宜，与"后期主要靠它扛"的设计相反） |
    /// | 墨眸（暴击） | 5.00 | 暴击率 + 暴伤，乘在最终伤害外 | 最贵，5 倍于墨刃。暴击是"白给一段乘区"，不该和线性属性同价 |
    /// </summary>
    public static double EnhFactor(int which) => which switch { 0 => 1.00, 1 => 0.70, 2 => 5.00, _ => 1.15 };

    /// <summary>
    /// 单件的等级上限，四件统一走 <see cref="Limits.MaxEnhance"/>。
    ///
    /// v0.16.0 曾把墨眸单独封在 350 级（因为暴伤 +400% 封顶后买不动了）；
    /// v0.16.1 暴伤取消封顶，墨眸回到与其它三件同一个上限 —— 暴击率 200 级后不再涨，
    /// 但暴伤每级 +0.1% 会一直涨，所以这条线永远有东西可买。
    /// </summary>
    public static int EnhCapOf(int which) => Limits.MaxEnhance;

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
        => EnhLevel(which) < EnhCapOf(which) && Ink.CompareTo(EnhCost(which, EnhLevel(which) + 1)) >= 0;

    public bool Enhance(int which)
    {
        int lv = EnhLevel(which);
        if (lv >= EnhCapOf(which)) return false;
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
        int capLv = EnhCapOf(which);
        if (lv >= capLv) return Big.Zero;
        if (lv + times > capLv) times = capLv - lv;
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
        int room = EnhCapOf(which) - lv;
        if (room <= 0) return 0;

        // 等比级数反解：ink ≥ c1·(r^n − 1)/(r − 1)
        double c1 = 2 * EnhFactor(which);
        double inkLog = Ink.Log10;
        double n;
        if (inkLog < 300)
        {
            double ink = Ink.ToDouble();
            n = Math.Log(1 + ink * EnhRateMinus1 / c1) / Math.Log(EnhRate);
        }
        else
        {
            // 墨晶超出 double 范围（上限抬到 1e+3000 后会在 3 万级左右出现）：
            // ToDouble 饱和会让 n 卡在 10,500 附近不再增长，这里改用对数直接估。
            // 1 + x ≈ x 带来的误差只有 log10 的常数项，实测两级以内，下面的校正循环会收干净。
            n = (inkLog + Math.Log10(EnhRateMinus1 / c1)) / Math.Log10(EnhRate);
        }
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
        int room = EnhCapOf(which) - EnhLevel(which);
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
        if (unitPrice <= 0 || Gold.IsZero) return Big.Zero;

        // 金币上限抬到 1.8e+3000 之后，ToDouble 会饱和成 double.MaxValue，
        // 再拿它除以单价算出来的 max 会停在 1e306 附近 —— 又变成「假的 max」。
        // 这里改用 Big 除法：指数相减本身不会溢出，量级一定对。
        var n = Gold / unitPrice;
        if (n.CompareTo(Big.Of(1)) < 0) return Big.Zero;

        // 小数量（≤1e15）向下取整，保证「买得起」严格成立；
        // 更大的数量保留尾数精度就行，整数部分已经超出显示精度。
        if (n.E <= 15) n = Big.Of(Math.Floor(n.ToDouble()));

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
        if (EnhLevel(which) >= EnhCapOf(which)) return Big.Zero;
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
            if (hits[i].CompareTo(Big.Zero) <= 0) continue;
            var amt = Big.Of(BoxTable[i].amount) * hits[i];
            if (BoxTable[i].isInk) { AddInk(amt); ink = ink + amt; }
            else { AddGold(amt); gold = gold + amt; }
        }

        Changed?.Invoke();

        if (count.CompareTo(Big.Of(2)) < 0)
        {
            // 单开保留原来的逐档文案（"末档"这种说法对一匣没意义）
            for (int i = 0; i < BoxTable.Length; i++)
                if (hits[i].CompareTo(Big.Zero) > 0)
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
    ///
    /// v0.17.0：返回值由 `double[]` 改成 `Big[]`。上限从 1.8e+303 抬到 1.8e+3000 之后，
    /// 金币到 1e+310 以上时 `count.ToDouble()` 会饱和成 1.8e308，命中次数按 double 算
    /// 就永远停在 5e307 附近 —— 玩家花的是 Big 精确扣款，拿回来的是饱和后的零头，直接亏穿。
    /// 现在只把**占比**留在 double 里（占比不受饱和影响），乘回总数走 Big。
    /// </summary>
    static Big[] RollBoxes(Big count)
    {
        int m = BoxTable.Length;
        var hits = new Big[m];

        if (count.CompareTo(Big.Of(2000)) <= 0)
        {
            int n = (int)Math.Round(count.ToDouble());
            if (n < 0) n = 0;
            var c = new int[m];
            for (int i = 0; i < n; i++)
            {
                double r = Rng.NextDouble(), acc = 0;
                for (int k = 0; k < m; k++)
                {
                    acc += BoxTable[k].p;
                    if (r <= acc) { c[k]++; break; }
                }
            }
            for (int k = 0; k < m; k++) hits[k] = Big.Of(c[k]);
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
        if (sum <= 0) { hits[0] = count; return hits; }

        // 先算占比、再用 Big 乘回总数：占比永远在 (0,1] 内，不吃 double 的饱和
        double others = 0;
        for (int k = 1; k < m; k++)
        {
            double ratio = v[k] / sum;
            hits[k] = count * ratio;
            others += ratio;
        }
        hits[0] = count * Math.Max(0, 1 - others);    // 零头补回第一档
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
        // 转生不再需要重置掉袋进度：掉袋判据由「今日敲击 % 500」派生（v0.17.1），
        // 今日敲击是当日统计、与转生无关；袋值本来就按掉落那一刻的层数现算。
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

        // 新档必须带上当前结构版本。`new SaveDto()` 的 Schema 是 0（那也是老档的特征），
        // 若不在这里标上，重置后在新手层（第 1 层）刷出高等级，一重启就会被当成旧档
        // 按 `LvCapAt(1) = 5` 压回 5 级。
        S.Schema = SaveSchema;

        // 运行期状态不在 SaveDto 里，得单独归零，否则界面上还挂着旧的血量/怪物/力竭
        HpMax = CalcHp();
        Hp = HpMax;
        MobIndex = 1;
        Resting = false;
        RestUntil = DateTime.MinValue;
        ConsecutiveLoss = 0;
        roundAccum = 0;
        // 掉袋进度不用单独归零：它由「今日敲击 % 500」派生（v0.17.1）
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
        MigrateSave();
    }

    /// <summary>当前存档结构版本。见 <see cref="SaveDto.Schema"/>。</summary>
    public const int SaveSchema = 3;

    /// <summary>
    /// 存档迁移：把旧档的语义拉到当前版本，跑完打上 <see cref="SaveSchema"/>。
    ///
    /// v1（v0.14.0）：**等级按当前曲线反算**。
    /// 旧档的升级需求是另一套公式（早期版本一只怪就能连升好几级），攒下来的等级
    /// 拿到新曲线面前是白送的乘区——同一个层数别人 940 级、旧档 1247 级，
    /// 凭空多一倍多的攻击。这里按 `LvCapAt(到达过的最高层)` 收掉超出的部分。
    /// 只收不放：等级低于曲线的档保持原样（那是自己的进度，不该被垫高）。
    /// 收完把当前经验压到本级 90% 以内，否则下一次 AddExp 立刻又顶回去。
    ///
    /// v2（v0.16.0）：墨眸按 350 级收口。**v0.16.1 作废**。
    /// 当时暴伤 +400% 封顶、350 级以上不产生属性，所以把超出的等级收平；
    /// 现在暴伤取消封顶（每级 +0.1% 一直涨），那些等级重新有价值，再收就是白扣。
    /// 已经被 v2 收过口的档原始等级无从恢复，这里不猜测也不重复收口；
    /// v3 相当于"撤回 v2"，没有字段变换，只是打标。
    /// 注意**不做全量重算**：墨刃/墨衣/墨印的存量等级是老价买的，老价买的就是老价买的，
    /// 新价格只对以后的每一级生效；否则改一次价就把玩家已经买到的东西也一起拿走。
    /// </summary>
    void MigrateSave()
    {
        if (S.Schema >= SaveSchema) return;

        if (S.Schema < 1)
        {
            int reached = Math.Max(1, Math.Max(S.Layer, S.MaxLayer));
            int cap = (int)Math.Floor(LvCapAt(reached));
            if (cap < 1) cap = 1;

            if (S.Level > cap)
            {
                int old = S.Level;
                S.Level = cap;
                var e = Exp;
                var lim = ExpNeed * 0.9;
                if (e.CompareTo(lim) > 0)
                {
                    e = lim;
                    S.ExpM = e.M; S.ExpE = e.E;
                }
                Log.Write($"存档迁移：等级 {old} → {cap}（按第 {reached} 层反算，旧曲线多出的部分已收走）");
            }
        }

        // v2 的墨眸收口已作废，这里不再收任何等级（见方法注释）。

        S.Schema = SaveSchema;
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
