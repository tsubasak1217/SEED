namespace SEED.UI;

// ============================================================
//  SliderNav.cs — スライダ（SEED.UI.Slider）の方向キーのアダプタ（2026-10-03。L3-6）
//
//  左右 = 値を 1 段階ずつ増減（Step。0 = 連続なら範囲の ContinuousStepFraction ずつ）。左右はいつも受ける（端でもフォーカスを移さない
//  ＝ Unity の横の Slider と同じ）。上下はフォーカスを移す。決定は何もしない。値は Slider.SetValue（範囲・段階へ寄せ、変われば ValueChanged）。
// ============================================================

/// <summary>スライダのアダプタ。</summary>
internal sealed class SliderNav : WidgetNav<Slider>
{
    /// <summary>連続（Step = 0）のスライダの 1 回の増減（範囲に対する割合。1/20 = 5%）。</summary>
    public const float ContinuousStepFraction = 0.05f;

    /// <summary>スライダに被せる。</summary>
    /// <param name="slider">スライダ。</param>
    public SliderNav(Slider slider) : base(slider) { }

    /// <inheritdoc />
    public override NavAxis AdjustAxis => NavAxis.Horizontal;

    /// <inheritdoc />
    public override void OnNavSubmit() { }

    /// <inheritdoc />
    public override bool OnNavAdjust(int direction)
    {
        if (!Widget.IsEnabled) return true;
        Widget.SetValue(Widget.Value + direction * StepOf(Widget.Min, Widget.Max, Widget.Step));
        return true;
    }

    /// <summary>1 回の増減の幅（段階があればそれ、連続なら範囲 × ContinuousStepFraction）。</summary>
    /// <param name="min">最小。</param>
    /// <param name="max">最大。</param>
    /// <param name="step">段階（0 = 連続）。</param>
    public static float StepOf(float min, float max, float step)
        => step > 0f ? step : System.MathF.Abs(max - min) * ContinuousStepFraction;
}
