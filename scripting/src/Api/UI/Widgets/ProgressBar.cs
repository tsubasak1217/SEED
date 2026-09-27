using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  ProgressBar.cs — 進捗の棒（横棒。経験値の棒など。W2-4。docs/ui_components.md §3）
//
//  【プレハブ】templates/ui/prefabs/progress_bar.actor
//      ProgressBar（Sprite = 溝〈角丸〉・このスクリプト）
//      ├─ Fill（Sprite = 値までの塗り〈角丸〉。幅はスクリプトが決める）
//      └─ Label（Text。任意。中に「Lv 3」などを出す。文字は使う側が決める）
//  値（0..1）が変わると塗りの幅が motion.medium 秒で伸び縮みする（Animate = false ならすぐ）。動いている間は描き続けを頼む。
// ============================================================

/// <summary>進捗の棒。</summary>
public sealed class ProgressBar : UiWidget
{
    /// <summary>子の塗りの名前。</summary>
    private const string FillChild = "Fill";
    /// <summary>半分。</summary>
    private const float Half = 0.5f;
    /// <summary>描き続けを頼むときの余裕（秒）。</summary>
    private const float KeepAliveSlack = 0.05f;

    /// <summary>値（0..1）。</summary>
    [SerializeField(Label = "値")]
    public float Value;

    /// <summary>値の変化を動かすか。</summary>
    [SerializeField(Label = "動かす")]
    public bool Animate = true;

    /// <summary>表示している値の動き。</summary>
    private UiTween _shown;
    /// <summary>溝の大きさ（プレハブの Sprite）。</summary>
    private Vector2 _trackSize;

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
        _trackSize = SpriteOf() is { } track ? track.Size : Vector2.Zero;
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
        float radius = Theme.Number(UiTokens.RadiusProgress);
        if (SpriteOf() is { } track)
        {
            track.Color = UiColorMath.FadeAlpha(Theme.Color(UiTokens.ColorSurfaceVariant), fade);
            track.CornerRadius = radius;
        }
        if (SpriteOf(FillChild) is { } fill)
        {
            float width = ValueMath.BarFillWidth(_shown.Value, _trackSize.x);
            fill.Color = UiColorMath.FadeAlpha(Theme.Color(UiTokens.ColorPrimary), fade);
            fill.Size = new Vector2(width, _trackSize.y);
            // 塗りが角丸の直径より短いときは半径を縮める（細い棒が楕円に潰れない）
            fill.CornerRadius = System.MathF.Min(radius, width * Half);
            gameObject.FindChild(FillChild).Visible = width > 0f;
        }
    }
}
