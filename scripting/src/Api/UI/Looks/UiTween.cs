using System;

namespace SEED.UI;

// ============================================================
//  UiTween.cs — 部品の小さな動き（スイッチのつまみ・進捗の伸び）の時間の進め方（W2-4。純粋な計算）
//
//  始まりの値 → 終わりの値を duration 秒で、Material の fastOutSlowIn（SwipeMath.Ease と同じ曲線）で動かす。
//  動きの途中で行き先が変わったら、今の値から新しい行き先へ動き直す（途中から戻っても跳ばない）。
//  部品は動いている間 SEED.Redraw を呼んで描画を止めさせない（W2-10a の約束。redraw_policy.md）。
// ============================================================

/// <summary>値の動き（1 本）。</summary>
public struct UiTween
{
    /// <summary>始まりの値。</summary>
    public float From;
    /// <summary>行き先の値。</summary>
    public float To;
    /// <summary>始まってからの時間（秒）。</summary>
    public float Elapsed;
    /// <summary>動きの長さ（秒。0 以下はすぐ行き先）。</summary>
    public float Duration;

    /// <summary>止まっている値で作る。</summary>
    public static UiTween At(float value) => new() { From = value, To = value, Elapsed = 0f, Duration = 0f };

    /// <summary>今の値。</summary>
    public readonly float Value
    {
        get
        {
            if (Duration <= 0f || Elapsed >= Duration) return To;
            float t = Math.Clamp(Elapsed / Duration, 0f, 1f);
            return From + (To - From) * SwipeMath.Ease(t);
        }
    }

    /// <summary>動いている途中か。</summary>
    public readonly bool IsRunning => Duration > 0f && Elapsed < Duration;

    /// <summary>行き先を変えて動き始める（今の値から）。行き先が同じなら何もしない。</summary>
    /// <param name="to">行き先。</param>
    /// <param name="duration">長さ（秒）。</param>
    public void Retarget(float to, float duration)
    {
        if (to == To && (IsRunning || Value == to)) return;
        From = Value;
        To = to;
        Elapsed = 0f;
        Duration = Math.Max(0f, duration);
    }

    /// <summary>すぐ行き先へ（動かさない）。</summary>
    public void Jump(float to)
    {
        From = To = to;
        Elapsed = 0f;
        Duration = 0f;
    }

    /// <summary>時間を進める。</summary>
    /// <returns>進めた後の値。</returns>
    public float Advance(float dt)
    {
        if (IsRunning) Elapsed += Math.Max(0f, dt);
        return Value;
    }

    /// <summary>残りの時間（秒。止まっていれば 0）。</summary>
    public readonly float Remaining => IsRunning ? Duration - Elapsed : 0f;
}
