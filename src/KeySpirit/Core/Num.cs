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

    /// <summary>转成普通 double；超出范围时返回设计上限。</summary>
    public double ToDouble()
    {
        if (IsZero) return 0;
        if (E > 303) return 1.8e303;
        if (E < -300) return 0;
        return M * Math.Pow(10, E);
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
    public static readonly Big Max = new Big(1.8, 303);
    public const int MaxLayer = 10000;
    public const int MaxSoul = 10000;      // 墨根
    public const int MaxEnhance = 10000;   // 强化等级
}

public static class Fmt
{
    static readonly string[] Suffix = { "", "K", "M", "B", "T" };

    public static string Large(Big v)
    {
        if (v.IsZero) return "0";

        // 1e303 以上直接按尾数+指数输出（ToDouble 会被夹，不能用来算位数）
        if (v.E >= 300)
        {
            double mm = Math.Floor(v.M * 10) / 10;
            if (mm < 1) mm = Math.Floor(v.M * 100) / 10;
            return mm.ToString("0.0") + "e+" + (v.E + (mm < 1 ? -1 : 0));
        }

        double lg = v.Log10;
        if (lg < 3) return Math.Floor(v.ToDouble()).ToString("0");
        if (lg < 15)
        {
            int g = (int)Math.Floor(lg / 3);           // 1..4
            double scaled = v.M * Math.Pow(10, v.E - g * 3);
            return scaled.ToString("0.0") + Suffix[g];
        }
        // 1e15 以上：科学记数法，1 位小数（向下取整）
        int e = v.E;
        double m = Math.Floor(v.M * 10) / 10;
        if (m < 1) { e -= 1; m = Math.Floor(v.M * 100) / 10; }
        return m.ToString("0.0") + "e+" + e;
    }

    /// <summary>小数值显示：<1000 保留 1 位小数（属性对比用），其余走大数规则。</summary>
    public static string Small(Big v)
    {
        double d = v.ToDouble();
        if (System.Math.Abs(d) < 1000) return d.ToString("0.#");
        return Large(v);
    }

    public static string Pct(double v, int digits = 1) => (v * 100).ToString("0." + new string('0', digits)) + "%";

    public static string Time(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m{t.Seconds:00}s";
        return $"{Math.Max(0, (int)t.TotalSeconds)}s";
    }
}
