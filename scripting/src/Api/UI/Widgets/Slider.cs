using System;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  Slider.cs — スライダ（W2-4。docs/ui_components.md §3）
//
//  【プレハブ】templates/ui/prefabs/slider.actor
//      Slider（Sprite = 当たりの帯〈透明〉・CanvasGesture〈タップ・横のドラッグ・押下の見た目なし〉・このスクリプト）
//      ├─ Track（Sprite = 溝〈角丸〉。左端と長さがつまみの動く範囲）
//      ├─ Fill（Sprite = 値までの塗り。幅はスクリプトが決める）
//      └─ Thumb（Sprite = つまみ〈楕円〉。位置と大きさはスクリプトが決める）
//  【操作】ドラッグ（W2-2 のアリーナ。横だけ・slop 8 dp）とタップでその位置の値へ。値は範囲（Min〜Max）と段階（Step。0 = 連続）へ
//  寄せる（ValueMath.ValueAt）。縦の一覧の中では最初の指の向きで一覧のスクロールかスライダかが決まる。
//  数値欄と一緒に使うときは ValueChanged と NumberField.ValueChanged を互いにつなぐ（同じ値は知らせない＝往復しない）。
// ============================================================

/// <summary>スライダ。</summary>
public sealed class Slider : UiWidget
{
    /// <summary>子の溝の名前。</summary>
    private const string TrackChild = "Track";
    /// <summary>子の塗りの名前。</summary>
    private const string FillChild = "Fill";
    /// <summary>子のつまみの名前。</summary>
    private const string ThumbChild = "Thumb";
    /// <summary>半分。</summary>
    private const float Half = 0.5f;

    /// <summary>最小値。</summary>
    [SerializeField(Label = "最小")]
    public float Min;
    /// <summary>最大値。</summary>
    [SerializeField(Label = "最大")]
    public float Max = 1f;
    /// <summary>段階（0 = 連続）。</summary>
    [SerializeField(Label = "段階")]
    public float Step;
    /// <summary>値。</summary>
    [SerializeField(Label = "値")]
    public float Value;

    /// <summary>値が変わった（新しい値）。</summary>
    public event Action<Slider, float>? ValueChanged;

    /// <summary>指で動かしている。</summary>
    public bool IsDragging { get; private set; }

    /// <summary>溝の左端の x と長さ・溝の中心の y（プレハブの Track から）。</summary>
    private float _trackX, _trackLength, _trackCenterY;

    /// <summary>値を変える（範囲・段階へ寄せる。同じ値なら知らせない）。</summary>
    public void SetValue(float value, bool notify = true)
    {
        float v = ValueMath.Snap(value, Min, Max, Step);
        if (ValueMath.NearlyEqual(v, Value)) return;
        Value = v;
        Refresh();
        if (notify) ValueChanged?.Invoke(this, v);
    }

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        if (SpriteOf(TrackChild) is { } track && TransformOf(TrackChild) is { } ct)
        {
            _trackX = ct.Position.x;
            _trackLength = track.Width;
            _trackCenterY = ct.Position.y + track.Height * Half;
        }
        Value = ValueMath.Snap(Value, Min, Max, Step);
    }

    /// <inheritdoc />
    public override void OnGestureTap(GestureEvent e) => MoveTo(e);

    /// <inheritdoc />
    public override void OnGestureDragStart(GestureEvent e)
    {
        if (!IsEnabled) return;
        IsDragging = true;
        MoveTo(e);
    }

    /// <inheritdoc />
    public override void OnGestureDragUpdate(GestureEvent e) => MoveTo(e);

    /// <inheritdoc />
    public override void OnGestureDragEnd(GestureEvent e)
    {
        IsDragging = false;
        Refresh();
    }

    /// <summary>指の位置の値へ動かす。</summary>
    private void MoveTo(GestureEvent e)
    {
        if (!IsEnabled) return;
        SetValue(ValueMath.ValueAt(e.LocalPosition.x, _trackX, _trackLength, Min, Max, Step));
        if (IsDragging) Refresh();
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        bool disabled = !IsEnabled;
        float fade = disabled ? Theme.Number(UiTokens.OpacityDisabled) : 1f;
        float t = ValueMath.Fraction(Value, Min, Max);
        float trackThickness = Theme.Number(UiTokens.SizeSliderTrack);
        if (SpriteOf(TrackChild) is { } track)
        {
            track.Color = UiColorMath.FadeAlpha(Theme.Color(UiTokens.ColorSurfaceVariant), fade);
            track.Height = trackThickness;
            track.CornerRadius = trackThickness * Half;
        }
        if (TransformOf(TrackChild) is { } trackCt)
            trackCt.Position = new Vector2(_trackX, _trackCenterY - trackThickness * Half);
        var active = disabled ? Theme.Color(UiTokens.ColorOnDisabled) : Theme.Color(UiTokens.ColorPrimary);
        if (SpriteOf(FillChild) is { } fill)
        {
            fill.Color = active;
            fill.Size = new Vector2(_trackLength * t, trackThickness);
            fill.CornerRadius = trackThickness * Half;
        }
        if (TransformOf(FillChild) is { } fillCt)
            fillCt.Position = new Vector2(_trackX, _trackCenterY - trackThickness * Half);
        float thumb = Theme.Number(IsDragging ? UiTokens.SizeSliderThumbPressed : UiTokens.SizeSliderThumb);
        if (SpriteOf(ThumbChild) is { } th)
        {
            th.Shape = SpriteShapeKind.Ellipse;
            th.Color = active;
            th.Size = new Vector2(thumb, thumb);
        }
        if (TransformOf(ThumbChild) is { } thumbCt)
            thumbCt.Position = new Vector2(_trackX + _trackLength * t - thumb * Half, _trackCenterY - thumb * Half);
    }
}
