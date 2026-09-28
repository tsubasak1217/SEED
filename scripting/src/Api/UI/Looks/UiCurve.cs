using System;

namespace SEED.UI;

// ============================================================
//  UiCurve.cs — 動きの曲線（3 次ベジェ。CSS の cubic-bezier(x1, y1, x2, y2)。W2-7。純粋な計算）
//
//  時間の割合 t（0〜1）→ 進み具合（0〜1）。端点は (0, 0) と (1, 1)、制御点は (x1, y1)・(x2, y2)。
//  x（時間）の媒介変数を二分法で解き、y（進み具合）を返す（SwipeMath.Ease と同じ解き方。x の制御点は 0〜1 に収める）。
//  画面の出入り・覆い・ダイアログ・トーストの曲線はテーマのトークン（例 motion.push_curve の .x1・.y1・.x2・.y2）で
//  差し替えられる（データ駆動。docs/ui_navigation.md §7）。
// ============================================================

/// <summary>動きの曲線（3 次ベジェ）。</summary>
public readonly struct UiCurve : IEquatable<UiCurve>
{
    /// <summary>二分法の最大の回数（2^-24 の幅まで絞れる）。</summary>
    private const int MaxBisectionSteps = 24;
    /// <summary>二分法を打ち切る時間の誤差。</summary>
    private const float CubicErrorBound = 1e-5f;
    /// <summary>曲線のトークンの成分の接尾辞（x1）。</summary>
    public const string SuffixX1 = ".x1";
    /// <summary>曲線のトークンの成分の接尾辞（y1）。</summary>
    public const string SuffixY1 = ".y1";
    /// <summary>曲線のトークンの成分の接尾辞（x2）。</summary>
    public const string SuffixX2 = ".x2";
    /// <summary>曲線のトークンの成分の接尾辞（y2）。</summary>
    public const string SuffixY2 = ".y2";

    /// <summary>1 つ目の制御点の x（時間。0〜1）。</summary>
    public float X1 { get; }
    /// <summary>1 つ目の制御点の y（進み具合。範囲の外も可＝行き過ぎる曲線）。</summary>
    public float Y1 { get; }
    /// <summary>2 つ目の制御点の x（時間。0〜1）。</summary>
    public float X2 { get; }
    /// <summary>2 つ目の制御点の y。</summary>
    public float Y2 { get; }

    /// <summary>制御点から作る（x は 0〜1 へ収める。有限でない値は直線の値）。</summary>
    public UiCurve(float x1, float y1, float x2, float y2)
    {
        X1 = Math.Clamp(float.IsFinite(x1) ? x1 : 0f, 0f, 1f);
        Y1 = float.IsFinite(y1) ? y1 : 0f;
        X2 = Math.Clamp(float.IsFinite(x2) ? x2 : 1f, 0f, 1f);
        Y2 = float.IsFinite(y2) ? y2 : 1f;
    }

    /// <summary>直線（等速）。</summary>
    public static UiCurve Linear => new(0f, 0f, 1f, 1f);
    /// <summary>Material の fastOutSlowIn（標準の動き。SwipeMath.Ease と同じ）。</summary>
    public static UiCurve FastOutSlowIn => new(0.4f, 0f, 0.2f, 1f);
    /// <summary>Material 3 の standard（画面の出入り）。</summary>
    public static UiCurve Standard => new(0.2f, 0f, 0f, 1f);
    /// <summary>減速（入ってくる動き）。</summary>
    public static UiCurve Decelerate => new(0f, 0f, 0f, 1f);
    /// <summary>Flutter の Curves.easeOut（覆いの 220ms）。</summary>
    public static UiCurve EaseOut => new(0f, 0f, 0.58f, 1f);
    /// <summary>Flutter の Curves.easeInOut。</summary>
    public static UiCurve EaseInOut => new(0.42f, 0f, 0.58f, 1f);

    /// <summary>
    /// 時間の割合 → 進み具合。t ≤ 0 は 0、t ≥ 1 は 1（NaN も 0）。
    /// </summary>
    /// <param name="t">時間の割合（0〜1）。</param>
    public float Evaluate(float t)
    {
        if (!(t > 0f)) return 0f;
        if (t >= 1f) return 1f;
        float start = 0f, end = 1f, mid = t;
        for (int i = 0; i < MaxBisectionSteps; i++)
        {
            mid = (start + end) / 2f;
            float estimate = Component(X1, X2, mid);
            if (MathF.Abs(t - estimate) < CubicErrorBound) break;
            if (estimate < t) start = mid; else end = mid;
        }
        return Component(Y1, Y2, mid);
    }

    /// <summary>3 次ベジェの 1 成分（端点 0 と 1、制御点 a・b、媒介変数 m）。</summary>
    private static float Component(float a, float b, float m)
        => 3f * a * (1f - m) * (1f - m) * m + 3f * b * (1f - m) * m * m + m * m * m;

    /// <summary>
    /// テーマから曲線を引く（トークン + ".x1" / ".y1" / ".x2" / ".y2" の 4 つ。1 つでも欠けていれば <paramref name="fallback"/>）。
    /// </summary>
    /// <param name="theme">テーマ。</param>
    /// <param name="token">曲線のトークン（例 motion.push_curve）。</param>
    /// <param name="fallback">引けないときの曲線。</param>
    public static UiCurve FromTheme(UiThemeData theme, string token, UiCurve fallback)
    {
        if (theme.TryNumber(token + SuffixX1, out var x1)
            && theme.TryNumber(token + SuffixY1, out var y1)
            && theme.TryNumber(token + SuffixX2, out var x2)
            && theme.TryNumber(token + SuffixY2, out var y2))
        {
            return new UiCurve(x1, y1, x2, y2);
        }
        return fallback;
    }

    /// <summary>曲線のトークンの 4 つの成分の名前（既定のテーマの検査用）。</summary>
    public static string[] ComponentTokens(string token)
        => new[] { token + SuffixX1, token + SuffixY1, token + SuffixX2, token + SuffixY2 };

    /// <inheritdoc />
    public bool Equals(UiCurve other) => X1 == other.X1 && Y1 == other.Y1 && X2 == other.X2 && Y2 == other.Y2;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is UiCurve c && Equals(c);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(X1, Y1, X2, Y2);

    /// <inheritdoc />
    public override string ToString() => $"cubic-bezier({X1}, {Y1}, {X2}, {Y2})";
}
