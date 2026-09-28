using System;

namespace SEED.UI;

// ============================================================
//  UiThemeTransition.cs — テーマの切り替えの色の補間の進み具合（W2-9。docs/ui_theme.md §5。純粋な計算）
//
//  元の表・行き先の表・時間・曲線を持ち、経った時間から途中の表（UiThemeBlend）を作る。時間が 0 以下なら最初から行き先。
//  切り替えの途中でまた切り替えたら、途中の表を元にして新しく始める（色が跳ばない。UiTheme が行う）。
// ============================================================

/// <summary>テーマの切り替えの色の補間。</summary>
public sealed class UiThemeTransition
{
    /// <summary>元の表。</summary>
    public UiThemeData From { get; }
    /// <summary>行き先の表。</summary>
    public UiThemeData To { get; }
    /// <summary>補間の時間（秒）。</summary>
    public float Duration { get; }
    /// <summary>補間の曲線。</summary>
    public UiCurve Curve { get; }
    /// <summary>経った時間（秒）。</summary>
    public float Elapsed { get; private set; }

    /// <summary>作る（時間は有限の 0 以上へ収める）。</summary>
    public UiThemeTransition(UiThemeData from, UiThemeData to, float duration, UiCurve curve)
    {
        From = from;
        To = to;
        Duration = float.IsFinite(duration) ? MathF.Max(0f, duration) : 0f;
        Curve = curve;
    }

    /// <summary>終わったか。</summary>
    public bool IsDone => Elapsed >= Duration;

    /// <summary>進み具合（曲線を通した 0..1）。</summary>
    public float Progress => Duration <= 0f ? 1f : Curve.Evaluate(Math.Clamp(Elapsed / Duration, 0f, 1f));

    /// <summary>今の途中の表。</summary>
    public UiThemeData Current => IsDone ? To : UiThemeBlend.Blend(From, To, Progress);

    /// <summary>時間を進めて、途中の表を返す（負・非有限の dt は 0）。</summary>
    public UiThemeData Step(float dt)
    {
        if (float.IsFinite(dt) && dt > 0f) Elapsed = MathF.Min(Duration, Elapsed + dt);
        return Current;
    }
}
