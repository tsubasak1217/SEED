using System;

namespace SEED.UI;

// ============================================================
//  SpinnerMotion.cs — 不定の進捗（ProgressSpinner。回る弧）の時刻 → 弧の始まりと角度（2026-10-02。純粋な計算）
//
//  Flutter の CircularProgressIndicator（値なし＝不定）と同じ動き（2026-10-02 に flutter/flutter master の
//  packages/flutter/lib/src/material/progress_indicator.dart を取得して式と定数を確かめた）:
//    u = 弧の周期（motion.spinner_cycle = 1333 ms）の中の進み（のこぎり波 0 → 1）
//    r = 回転の周期（motion.spinner_rotation = 2222 ms）の中の進み（のこぎり波 0 → 1）
//    頭 head = fastOutSlowIn(Interval(0.0, 0.5)(u))   … 周期の前半で弧の先が 270 度ぶん進む
//    尾 tail = fastOutSlowIn(Interval(0.5, 1.0)(u))   … 後半で弧の後ろが追いつく
//    始まり = −90 度 ＋ tail × 270 度 ＋ r × 360 度 ＋ u × 90 度
//    角度   = max(head × 270 度 − tail × 270 度, ε)   （ε = 0.001 ラジアン。0 で弧が消えないように）
//  周期の境目（u: 1 → 0）では始まりがちょうど 360 度戻り角度は ε のままなので、見た目は途切れない。
//  SEED の弧（SpriteShapeKind.Arc）の角度は度で、0 = +X・時計回りが正（Flutter の canvas.drawArc と同じ向き）。
// ============================================================

/// <summary>弧（始まりと角度。度）。</summary>
/// <param name="StartDegrees">始まりの角度（度。0 = +X・時計回りが正。−90 = 真上）。</param>
/// <param name="SweepDegrees">弧の角度（度。正 = 時計回り）。</param>
public readonly record struct SpinnerArc(float StartDegrees, float SweepDegrees);

/// <summary>不定の進捗の時刻 → 弧。</summary>
public static class SpinnerMotion
{
    /// <summary>弧の始まりの基準（真上。Flutter の _startAngle = −π/2）。</summary>
    public const float BaseStartDegrees = -90f;
    /// <summary>頭・尾が動く角度（Flutter の 3/2 π = 270 度）。</summary>
    public const float ArcTravelDegrees = 270f;
    /// <summary>弧の周期で足す回転（Flutter の offsetValue × 0.5 π = 90 度）。</summary>
    public const float CycleOffsetDegrees = 90f;
    /// <summary>1 回転（度）。</summary>
    public const float FullTurnDegrees = 360f;
    /// <summary>頭が動く区間の終わり（周期の割合。Flutter の Interval(0.0, 0.5)）。</summary>
    public const float HeadEnd = 0.5f;
    /// <summary>尾が動く区間の始まり（周期の割合。Flutter の Interval(0.5, 1.0)）。</summary>
    public const float TailStart = 0.5f;
    /// <summary>Flutter の弧の角度の下限（_epsilon。ラジアン。0 で弧が消えないように）。</summary>
    private const float MinSweepRadians = 0.001f;
    /// <summary>1 ラジアンの度。</summary>
    private const float DegreesPerRadian = 180f / MathF.PI;
    /// <summary>弧の角度の下限（度。Flutter の _epsilon = 0.001 ラジアン）。</summary>
    public static readonly float MinSweepDegrees = MinSweepRadians * DegreesPerRadian;

    /// <summary>頭・尾の動きの曲線（Flutter の Curves.fastOutSlowIn）。</summary>
    private static readonly UiCurve Ease = UiCurve.FastOutSlowIn;

    /// <summary>
    /// 時刻の弧。
    /// </summary>
    /// <param name="seconds">回し始めてからの秒（負・有限でない値は 0）。</param>
    /// <param name="cycleSeconds">弧が伸びて縮む 1 周期（秒。motion.spinner_cycle。0 以下・有限でない値は動かさない＝最初の姿）。</param>
    /// <param name="rotationSeconds">全体が 1 回転する時間（秒。motion.spinner_rotation。同上）。</param>
    public static SpinnerArc At(double seconds, float cycleSeconds, float rotationSeconds)
    {
        double t = double.IsFinite(seconds) && seconds > 0d ? seconds : 0d;
        float u = Phase(t, cycleSeconds);
        float r = Phase(t, rotationSeconds);
        float head = Interval(u, 0f, HeadEnd);
        float tail = Interval(u, TailStart, 1f);
        float start = BaseStartDegrees + tail * ArcTravelDegrees + r * FullTurnDegrees + u * CycleOffsetDegrees;
        float sweep = MathF.Max(head * ArcTravelDegrees - tail * ArcTravelDegrees, MinSweepDegrees);
        return new SpinnerArc(NormalizeDegrees(start), sweep);
    }

    /// <summary>角度を 0 以上 360 未満へ（同じ向き。弧の SDF は角度を一周で畳むので見た目は変わらない。値を小さく保つだけ）。</summary>
    /// <param name="degrees">角度（度）。</param>
    public static float NormalizeDegrees(float degrees)
    {
        if (!float.IsFinite(degrees)) return 0f;
        float wrapped = degrees % FullTurnDegrees;
        if (wrapped < 0f) wrapped += FullTurnDegrees;
        // ごく小さな負の値に 360 を足すと丸めで 360 ちょうどになることがある（0 と同じ向き）
        return wrapped >= FullTurnDegrees ? 0f : wrapped;
    }

    /// <summary>のこぎり波（周期の中の進み 0..1）。周期が 0 以下・有限でなければ 0。</summary>
    /// <param name="seconds">秒（0 以上）。</param>
    /// <param name="period">周期（秒）。</param>
    public static float Phase(double seconds, float period)
    {
        if (!(float.IsFinite(period) && period > 0f)) return 0f;
        double cycles = seconds / period;
        return (float)(cycles - Math.Floor(cycles));
    }

    /// <summary>区間の中の進みに曲線を通す（Flutter の Interval: 区間の前は 0・後は 1、端はそのまま）。</summary>
    /// <param name="u">周期の中の進み（0..1）。</param>
    /// <param name="begin">区間の始まり。</param>
    /// <param name="end">区間の終わり。</param>
    private static float Interval(float u, float begin, float end)
    {
        float x = Math.Clamp((u - begin) / (end - begin), 0f, 1f);
        if (x <= 0f || x >= 1f) return x;
        return Ease.Evaluate(x);
    }
}
