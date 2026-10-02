using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  ProgressRing.cs — 進捗の輪（長押し・振るの進み。W2-4。docs/ui_components.md §3）
//
//  【プレハブ】templates/ui/prefabs/progress_ring.actor
//      ProgressRing（Sprite = 溝〈形 = 弧・一周〉・このスクリプト）
//      ├─ Arc（Sprite = 値までの弧〈形 = 弧・端を丸く〉。角度 = 値 × 360 度。真上から時計回り）
//      └─ Label（Text。任意。中央の文字は使う側が決める）
//  形と塗りの弧（SpriteShapeKind.Arc。W2-4 の SDF）で描くので、毎フレーム SEED.Draw を呼ばなくてよい
//  （値が変わったときだけ欄を書く）。値の変化は motion.medium 秒で動く（Animate = false ならすぐ）。
// ============================================================

/// <summary>進捗の輪。</summary>
public sealed class ProgressRing : UiWidget
{
    /// <summary>子の弧の名前。</summary>
    private const string ArcChild = "Arc";
    /// <summary>描き続けを頼むときの余裕（秒）。</summary>
    private const float KeepAliveSlack = 0.05f;

    /// <summary>値（0..1）。</summary>
    [SerializeField(Label = "値")]
    public float Value;

    /// <summary>値の変化を動かすか。</summary>
    [SerializeField(Label = "動かす")]
    public bool Animate = true;

    /// <summary>
    /// 輪の太さ（キャンバスの単位。0 以下 = テーマの size.ring_thickness。2026-10-02）。以前はテーマのトークンだけで決まり、
    /// 部品ごとに変えられなかった（Wake or Pay の W3-2 (5)。起床確認の輪と同じトークンを共有していた）。
    /// </summary>
    [SerializeField(Label = "太さ")]
    public float Thickness;

    /// <summary>表示している値の動き。</summary>
    private UiTween _shown;

    /// <summary>輪の太さを変える（0 以下 = テーマの size.ring_thickness。見た目も変える）。</summary>
    /// <param name="thickness">太さ（キャンバスの単位）。</param>
    public void SetThickness(float thickness)
    {
        if (Thickness == thickness) return;
        Thickness = thickness;
        Refresh();
    }

    /// <summary>今の輪の太さ（部品の指定か、テーマの size.ring_thickness）。</summary>
    public float ResolvedThickness => UiSizeOverride.Resolve(Thickness, Theme.Number(UiTokens.SizeRingThickness));

    /// <summary>値を変える（0..1 へ収める）。</summary>
    public void SetValue(float value)
    {
        float v = ValueMath.Progress(value);
        if (ValueMath.NearlyEqual(v, Value) && !_shown.IsRunning) return;
        Value = v;
        if (Animate)
        {
            float duration = Theme.Number(UiTokens.MotionMedium);
            _shown.Retarget(v, duration);
            Redraw.KeepAlive(duration + KeepAliveSlack);
        }
        else
        {
            _shown.Jump(v);
        }
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        Value = ValueMath.Progress(Value);
        _shown = UiTween.At(Value);
    }

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        if (!_shown.IsRunning) return;
        _shown.Advance(dt);
        ApplyLook();
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        float fade = IsEnabled ? 1f : Theme.Number(UiTokens.OpacityDisabled);
        // 太さは部品の指定（0 以下ならテーマの size.ring_thickness。2026-10-02）
        float thickness = ResolvedThickness;
        if (SpriteOf() is { } track)
        {
            track.Shape = SpriteShapeKind.Arc;
            track.ArcSweep = ValueMath.FullTurnDegrees;
            track.ArcThickness = thickness;
            track.Color = UiColorMath.FadeAlpha(Theme.Color(UiTokens.ColorSurfaceVariant), fade);
        }
        if (SpriteOf(ArcChild) is { } arc)
        {
            float sweep = ValueMath.RingSweepDegrees(_shown.Value);
            arc.Shape = SpriteShapeKind.Arc;
            arc.ArcSweep = sweep;
            arc.ArcThickness = thickness;
            arc.ArcRoundCaps = true;
            arc.Color = UiColorMath.FadeAlpha(Theme.Color(UiTokens.ColorPrimary), fade);
            gameObject.FindChild(ArcChild).Visible = sweep > 0f;
        }
    }
}
