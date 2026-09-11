using System;

/// <summary>
/// 音価の足し算にだけ使う<b>既約の有理数</b>（分子 / 分母。分母は必ず 1 以上）。
///
/// ビートパターンは「1/4 ＋ 1/6 ＋ …」のように分母の違う音価を足し合わせるので、
/// float で積むと「合計がちょうど 1 小節か」を等号で判定できない
/// （0.9999999 や 1.0000001 になる）。そこで<b>合計の検証だけは有理数で厳密に</b>行い、
/// 実際の再生位置を求めるときにだけ <see cref="ToDouble"/> で実数へ落とす。
///
/// <b>単一責任</b>: 約分つきの加算と比較だけを持ち、音楽的な意味は一切知らない。
/// </summary>
public readonly struct Fraction : IEquatable<Fraction>
{
    /// <summary>分母として許す最小値（0 除算の番人）。</summary>
    private const long MinDenominator = 1;

    /// <summary>ゼロ（0/1）。加算の初期値に使う。</summary>
    public static readonly Fraction Zero = new Fraction(0, 1);

    /// <summary>分子（符号はこちらが持つ）。</summary>
    public long Numerator { get; }

    /// <summary>分母（必ず 1 以上）。</summary>
    public long Denominator { get; }

    /// <summary>
    /// 分子・分母から既約の有理数を作る【生成の唯一の入口】。
    /// 分母が 0 以下のときは 1 として扱う（呼び出し側の検証漏れで落ちないため）。
    /// </summary>
    /// <param name="numerator">分子。</param>
    /// <param name="denominator">分母（0 以下は 1 に丸める）。</param>
    public Fraction(long numerator, long denominator)
    {
        long den = denominator < MinDenominator ? MinDenominator : denominator;
        long divisor = Gcd(Math.Abs(numerator), den);
        Numerator = numerator / divisor;
        Denominator = den / divisor;
    }

    /// <summary>この有理数に <paramref name="other"/> を足した値（既約）。</summary>
    /// <param name="other">足す値。</param>
    public Fraction Add(Fraction other)
        => new Fraction(Numerator * other.Denominator + other.Numerator * Denominator,
                        Denominator * other.Denominator);

    /// <summary>整数（分母 1 の値）になっているか。＝ちょうど小節の切れ目に来ているか。</summary>
    public bool IsInteger => Denominator == MinDenominator;

    /// <summary>整数値としての値（<see cref="IsInteger"/> が false のときは切り捨て）。</summary>
    public long IntegerValue => Numerator / Denominator;

    /// <summary>0 より大きいか。</summary>
    public bool IsPositive => Numerator > 0;

    /// <summary>実数（double）へ落とす【再生位置を求める唯一の変換点】。</summary>
    public double ToDouble() => (double)Numerator / Denominator;

    /// <summary>値が等しいか（既約なので分子・分母の一致でよい）。</summary>
    /// <param name="other">比較する値。</param>
    public bool Equals(Fraction other)
        => Numerator == other.Numerator && Denominator == other.Denominator;

    /// <summary>値が等しいか（object 版）。</summary>
    /// <param name="obj">比較する値。</param>
    public override bool Equals(object? obj) => obj is Fraction f && Equals(f);

    /// <summary>ハッシュ値（辞書のキーに使えるようにするための実装）。</summary>
    public override int GetHashCode() => Numerator.GetHashCode() ^ Denominator.GetHashCode();

    /// <summary>"3/4" 形式の文字列（エラーメッセージ用）。</summary>
    public override string ToString() => $"{Numerator}/{Denominator}";

    /// <summary>最大公約数（0 が混ざっても 1 以上を返す番人つき）。</summary>
    /// <param name="a">値 A。</param>
    /// <param name="b">値 B。</param>
    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            long t = a % b;
            a = b;
            b = t;
        }
        return a > 0 ? a : 1;
    }
}
