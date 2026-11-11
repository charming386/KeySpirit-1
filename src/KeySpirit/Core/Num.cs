using System;

namespace KeySpirit.Core;

/// <summary>
/// 尾数 + 指数量值（对应设计稿 13.4 的 BigDouble）。不可变。
/// 上限不是类型约束：设计上限由 Core.Limits 主动夹取（1.8e+303）。
/// </summary>
public readonly struct Big : IComparable<Big>
{
    public readonly double M;   // mantissa, [1,10) 或 0
    public readonly int E;      // base-10 exponent

    public Big(double m, int e)
    {
        if (double.IsNaN(m) || double.IsInfinity(m))
        {
            M = 1.8; E = 303;   // 兜底：任何非法值都收到设计上限
            return;
        }
        if (m == 0) { M = 0; E = 0; return; }
        // normalize into [1,10)
        int shift = (int)Math.Floor(Math.Log10(Math.Abs(m)));
        double mm = m / Math.Pow(10, shift);
        int ee = e + shift;
        if (mm >= 10) { mm /= 10; ee += 1; }
        if (mm < 1) { mm *= 10; ee -= 1; }
        M = mm; E = ee;
    }

    public static readonly Big Zero = new Big(0, 1);
    public bool IsZero => M == 0;

    public static Big Of(double v) => new Big(v, 0);

    public static Big Pow(double baseValue, int n)
    {
        if (n == 0) return Of(1);
        double lg = Math.Log10(baseValue) * n;
        int e = (int)Math.Floor(lg);
        double m = Math.Pow(10, lg - e);
        return new Big(m, e);
    }

    public double Log10 => IsZero ? double.NegativeInfinity : Math.Log10(M) + E;

    /// <summary>
    /// 转成普通 double；**存不下时饱和到 <see cref="double.MaxValue"/>**，不返回假的 1.8e303。
    ///
    /// v0.17.0 上限从 1.8e+303 抬到 1.8e+3000 之后，这个函数只用于「比值 / 进度条 / 展示」，
    /// 不能再拿它做精确运算 —— 两个都饱和的值相除会恒等于 1。
    /// 需要比例的地方用 <see cref="Ratio"/>,需要精确数量的地方用 Big 运算。
    /// E = 307 是能安全乘出 double 的最后一档（M × 1e307 最大 9.99e307 &lt; 1.8e308）。
    /// </summary>
    public double ToDouble()
    {
        if (IsZero) return 0;
        if (E > 307) return double.MaxValue;
        if (E < -300) return 0;
        return M * Math.Pow(10, E);
    }

    /// <summary>
    /// a / b 的比例，**两个数都超出 double 范围时仍然正确**。
    /// 只靠指数差就能判断量级：差 1 以上直接给 0 或 1（视觉上本来就分辨不出），
    /// 同阶才回到尾数相除，精度够进度条用。
    /// </summary>
    public static double Ratio(Big a, Big b)
    {
        if (b.IsZero || a.IsZero) return 0;
        int d = a.E - b.E;
        // 差 3 个数量级以上，比例已经 >1000 或 <1/1000，直接给端点（进度条分辨不出）
        if (d > 2) return 1;
        if (d < -6) return 0;
        return Math.Clamp(a.M / b.M * Math.Pow(10, d), 0, 1);
    }

    public static Big operator +(Big a, Big b)
    {
        if (a.IsZero) return b;
        if (b.IsZero) return a;
        int hi = Math.Max(a.E, b.E), lo = Math.Min(a.E, b.E);
        if (hi - lo > 17) return a.E >= b.E ? a : b;
        double m = (a.E >= b.E ? a.M : b.M) + (a.E >= b.E ? b.M : a.M) / Math.Pow(10, hi - lo);
        return new Big(m, hi);
    }

    public static Big operator *(Big a, Big b)
    {
        if (a.IsZero || b.IsZero) return Zero;
        return new Big(a.M * b.M, a.E + b.E);
    }

    public static Big operator -(Big a, Big b)
    {
        if (b.IsZero) return a;
        if (a.CompareTo(b) <= 0) return Zero;
        double m = a.M - b.M * Math.Pow(10, b.E - a.E);
        if (m <= 0) return Zero;
        return new Big(m, a.E);
    }

    public static Big operator *(Big a, double k) => a * Of(k);
    public static Big operator *(double k, Big a) => a * Of(k);
    public static Big operator /(Big a, double k) => new Big(a.M / k, a.E);

    /// <summary>Big / Big：尾数相除、指数相减，由构造函数负责归一化。防御的软减伤公式要用。</summary>
    public static Big operator /(Big a, Big b) => b.IsZero ? Zero : new Big(a.M / b.M, a.E - b.E);

    public static bool operator <(Big a, Big b) => a.CompareTo(b) < 0;
    public static bool operator >(Big a, Big b) => a.CompareTo(b) > 0;
    public static bool operator <=(Big a, Big b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Big a, Big b) => a.CompareTo(b) >= 0;

    public int CompareTo(Big other)
    {
        if (IsZero && other.IsZero) return 0;
        if (IsZero) return -1;
        if (other.IsZero) return 1;
        if (E != other.E) return E.CompareTo(other.E);
        return M.CompareTo(other.M);
    }

    public static Big Min(Big a, Big b) => a.CompareTo(b) <= 0 ? a : b;

    /// <summary>设计上限 1.8e+303（13.3 封顶表）。</summary>
    public static Big Clamp(Big v) => v.CompareTo(Limits.Max) > 0 ? Limits.Max : v;

    public override string ToString() => Fmt.Large(this);
}

public static class Limits
{
    /// <summary>
    /// 数值上限。v0.17.0 由 **1.8e+303 扩到 1.8e+3000**。
    ///
    /// 原来 303 是照着 double 的 1.8e308 留了一点边际定的，代价是**把另外三个上限一起锁死**：
    /// 强化成本 `×1.07^n` 在 10,312 级就顶到 1.8e+303、怪血 `1.06^层` 在 11,975 层顶到同一个数 ——
    /// 所以 `MaxLayer / MaxSoul / MaxEnhance` 只能写 10,000，再高就溢出。
    /// 玩家在 617 层时看到「10,000 上限」，其实是一道**被大数实现逼出来的墙**，不是设计意图。
    ///
    /// 扩到 1e+3000 之后（`Big.E` 本来就是 int，能存到 21 亿，这里只用了千分之一）：
    ///   · 强化成本 1.07^n 到 102,097 级才触顶
    ///   · 强化属性 1.06^n 到 118,550 级才触顶
    ///   · 怪血 1.06^层 到 118,551 层才触顶
    /// 于是三个上限可以一起放到 **100,000** —— 是原来数字的十倍，
    /// 而真正的墙变成 `×1.07` 的指数成本：玩家永远攒不出 10 万级那个量级的墨晶。
    /// 这比写死一个「10,000」更像「没上限」。
    /// </summary>
    public static readonly Big Max = new Big(1.8, 3000);

    /// <summary>层数上限。10 万层时怪血 5.0e+2513，离 1.8e+3000 还有 487 个数量级。</summary>
    public const int MaxLayer = 100000;

    /// <summary>墨根上限（转生累计）。正常玩法最多 0.35×层数，10 万层也只有 3.5 万。</summary>
    public const int MaxSoul = 100000;

    /// <summary>本命器强化上限。四件统一，墨眸不再单列 —— 它是线性属性，没有被 1.06 的墙管。</summary>
    public const int MaxEnhance = 100000;
}

public static class Fmt
{
    static readonly string[] Suffix = { "", "K", "M", "B", "T" };

    /// <summary>
    /// 科学记数法保留的小数位数。
    /// 位数是**按调用处传**的，不是一个全局常量 —— 这是 v0.18.3 修的：
    ///
    ///   · 主列 / 托盘 / 日志走默认的 **1 位**。主列只有 232 宽，血条 + 「生命 9.9e+15 / 9.9e+15」
    ///     本来就挤，再多两个字符血条就要被压短甚至换行；
    ///   · **详情页**（角色 / 战斗）空间够，传 <see cref="DetailDigits"/> 用 **3 位**：
    ///     `1.06^(墨衣等级)` 每级只涨 6%，1 位小数会把 9.94e+15 和 9.99e+15 印成同一个
    ///     「9.9e+15」，连点十几级数字一动不动，玩家会以为强化没生效。3 位能分辨 0.1%，
    ///     够看出每级涨幅。
    ///
    /// v0.18.2 的错误做法是把全局常量改成 3，结果**主列血条旁的数字也一起变长了**。
    /// 这里的默认值必须是 1，任何「看起来更精确」的想法都不该动它。
    ///
    /// 向下取整（Floor）是**故意**的：显示值永远不超过真实值，
    /// 否则「显示 1.0e+15、实际 0.999e+15」会让玩家对着面板凑不够成本。
    /// </summary>
    public const int DetailDigits = 3;

    static string Sci(double mantissa, int exp, int digits)
    {
        // 尾数下限保护：M 名义上在 [1,10)，浮点误差可能算出 9.999999 这种
        int e = exp;
        double scale = Math.Pow(10, digits);
        double m = Math.Floor(mantissa * scale) / scale;
        if (m < 1) { e -= 1; m = Math.Floor(mantissa * scale * 10) / scale; }
        return m.ToString("0." + new string('0', digits)) + "e+" + e;
    }

    /// <summary>
    /// 大数显示。**digits 默认 1**（主列血条旁、托盘提示、日志都走这个），
    /// 只有详情页传 <see cref="DetailDigits"/>。
    /// K/M/B/T 段始终 1 位小数 —— 那是短写法，不是科学记数法，跟着变会撑宽又没意义。
    /// </summary>
    public static string Large(Big v, int digits = 1)
    {
        if (v.IsZero) return "0";

        // 1e303 以上直接按尾数+指数输出（ToDouble 会被夹，不能用来算位数）
        if (v.E >= 300) return Sci(v.M, v.E, digits);

        double lg = v.Log10;
        if (lg < 3) return Math.Floor(v.ToDouble()).ToString("0");
        if (lg < 15)
        {
            int g = (int)Math.Floor(lg / 3);           // 1..4
            double scaled = v.M * Math.Pow(10, v.E - g * 3);
            return scaled.ToString("0.0") + Suffix[g];
        }
        // 1e15 以上：科学记数法，位数由 digits 定（默认 1，详情页 3）
        return Sci(v.M, v.E, digits);
    }

    /// <summary>小数值显示：<1000 保留 1 位小数（属性对比用），其余走大数规则。</summary>
    public static string Small(Big v, int digits = 1)
    {
        double d = v.ToDouble();
        if (System.Math.Abs(d) < 1000) return d.ToString("0.#");
        return Large(v, digits);
    }

    public static string Pct(double v, int digits = 1) => (v * 100).ToString("0." + new string('0', digits)) + "%";

    public static string Time(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m{t.Seconds:00}s";
        return $"{Math.Max(0, (int)t.TotalSeconds)}s";
    }
}
