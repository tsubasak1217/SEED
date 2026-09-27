using System;

namespace SEED.UI;

// ============================================================
//  SwipeMath.cs — 行のスワイプの操作の計算（ずらし量・離したときに開くか・動きの曲線。W2-3）
//
//  エンジンの API に触れない純粋な計算（editor/tests/UiListViewTests から単体で試せる）。
//
//  【開く・閉じるの規則】（Android の ItemTouchHelper に倣う。2026-09-28 に androidx のソースで値を確かめた）
//    - 離したときの横の速さが「逃げの速さ」（既定 120 dp/秒 = item_touch_helper_swipe_escape_velocity）以上なら、
//      速さの向きで決める（開く向きなら開く・逆なら閉じる）
//    - それより遅ければ、ずらした量が開いた量の「閾値」（既定 0.5 = ItemTouchHelper.getSwipeThreshold）以上なら開く
//  【ずらし量】閉じた 0 から開いた量（右側の操作なら負＝左へずらす）までに収める（端より先へは引けない）。
//  【動き】開く・閉じるは 250ms（ItemTouchHelper.DEFAULT_SWIPE_ANIMATION_DURATION）、曲線は Material の
//  fastOutSlowIn = Cubic(0.4, 0.0, 0.2, 1.0)（Flutter の Curves.fastOutSlowIn と同じ）。
// ============================================================

/// <summary>行のスワイプの操作の計算。</summary>
public static class SwipeMath
{
    /// <summary>開くとみなすずらし量の割合の既定（ItemTouchHelper.getSwipeThreshold = 0.5）。</summary>
    public const float DefaultOpenThreshold = 0.5f;
    /// <summary>速さで決める閾値の既定（dp/秒。androidx の item_touch_helper_swipe_escape_velocity = 120dp）。</summary>
    public const float DefaultEscapeVelocityDp = 120f;
    /// <summary>開く・閉じるの動きの時間の既定（秒。ItemTouchHelper.DEFAULT_SWIPE_ANIMATION_DURATION = 250ms）。</summary>
    public const float DefaultSettleSeconds = 0.25f;

    /// <summary>動きの曲線の制御点（Material fastOutSlowIn = Flutter Curves.fastOutSlowIn）。</summary>
    private static readonly float[] SettleCurve = { 0.4f, 0.0f, 0.2f, 1.0f };
    /// <summary>3 次ベジェの逆算の許容誤差（Flutter Cubic._cubicErrorBound）。</summary>
    private const float CubicErrorBound = 0.001f;
    /// <summary>二分法の上限の回数（無限の繰り返しの保護）。</summary>
    private const int MaxBisectionSteps = 64;

    /// <summary>ドラッグの移動を当てた後のずらし量（0 と開いた量の間へ収める）。</summary>
    /// <param name="offset">今のずらし量。</param>
    /// <param name="delta">指の横の移動（キャンバスの単位）。</param>
    /// <param name="openOffset">開いたときのずらし量（右側の操作なら負）。</param>
    public static float ApplyDrag(float offset, float delta, float openOffset)
    {
        float next = offset + (float.IsFinite(delta) ? delta : 0f);
        float lo = MathF.Min(0f, openOffset);
        float hi = MathF.Max(0f, openOffset);
        return Math.Clamp(next, lo, hi);
    }

    /// <summary>離したときに開くか（冒頭の規則）。</summary>
    /// <param name="offset">今のずらし量。</param>
    /// <param name="velocityDp">離したときの横の速さ（dp/秒。右向きが正）。</param>
    /// <param name="openOffset">開いたときのずらし量（右側の操作なら負）。</param>
    /// <param name="threshold">開くとみなすずらし量の割合（0〜1）。</param>
    /// <param name="escapeVelocityDp">速さで決める閾値（dp/秒）。</param>
    public static bool ShouldOpen(float offset, float velocityDp, float openOffset, float threshold, float escapeVelocityDp)
    {
        if (openOffset == 0f) return false;
        float direction = MathF.Sign(openOffset);
        if (float.IsFinite(velocityDp) && MathF.Abs(velocityDp) >= escapeVelocityDp)
        {
            return MathF.Sign(velocityDp) == direction;
        }
        return MathF.Sign(offset) == direction && MathF.Abs(offset) >= Math.Clamp(threshold, 0f, 1f) * MathF.Abs(openOffset);
    }

    /// <summary>動きの曲線（fastOutSlowIn）。t は時間の割合（0〜1）。</summary>
    public static float Ease(float t)
    {
        if (!(t > 0f)) return 0f;
        if (t >= 1f) return 1f;
        float a = SettleCurve[0], b = SettleCurve[1], c = SettleCurve[2], d = SettleCurve[3];
        float start = 0f, end = 1f, mid = 0.5f;
        for (int i = 0; i < MaxBisectionSteps; i++)
        {
            mid = (start + end) / 2f;
            float estimate = Evaluate(a, c, mid);
            if (MathF.Abs(t - estimate) < CubicErrorBound) break;
            if (estimate < t) start = mid; else end = mid;
        }
        return Evaluate(b, d, mid);
    }

    /// <summary>3 次ベジェの 1 成分（端点 0 と 1、制御点 a・b、媒介変数 m）。</summary>
    private static float Evaluate(float a, float b, float m)
        => 3f * a * (1f - m) * (1f - m) * m + 3f * b * (1f - m) * m * m + m * m * m;
}
