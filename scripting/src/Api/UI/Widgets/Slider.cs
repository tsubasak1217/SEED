using System;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  Slider.cs — スライダ（W2-4。docs/ui_components.md §6）
//
//  【プレハブ】templates/ui/prefabs/slider.actor
//      Slider（Sprite = 当たりの帯〈透明〉・CanvasGesture〈タップ・横のドラッグ・押下の見た目なし〉・このスクリプト）
//      ├─ Track（Sprite = 溝〈角丸〉。左端と長さがつまみの動く範囲）
//      ├─ Fill（Sprite = 値までの塗り。幅はスクリプトが決める）
//      └─ Thumb（Sprite = つまみ〈楕円〉。位置と大きさはスクリプトが決める）
//  【操作】ドラッグ（W2-2 のアリーナ。横だけ・slop 8 dp）とタップでその位置の値へ。値は範囲（Min〜Max）と段階（Step。0 = 連続）へ
//  寄せる（ValueMath.ValueAt）。縦の一覧の中では最初の指の向きで一覧のスクロールかスライダかが決まる。
//  数値欄と一緒に使うときは ValueChanged と NumberField.ValueChanged を互いにつなぐ（同じ値は知らせない＝往復しない）。
//  【溝の長さ（2026-10-02）】溝の左右の余白はプレハブの Track の位置から読み、溝の長さは部品のレイアウトの幅に合わせる
//  （SliderGeometry。コンテナの fill_width・Stretch・flex で伸ばした・画面の幅が変わったときも、レイアウトの大きさが変わるたびに置き直す）。
//  以前は開始時のプレハブの幅で固定だった（Wake or Pay の W3-1 (1)。プロジェクトは FullWidthSlider で回避）。伸ばしていない部品は従来と同じ。
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
    /// <summary>両側（左右・上下の余白の数）。</summary>
    private const float BothSides = 2f;

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

    /// <summary>溝の長さ（つまみの動く範囲。キャンバスの単位。レイアウトの幅に合わせた値）。</summary>
    public float TrackLength => _track.Length;

    /// <summary>プレハブの寸法（開始時に読む。溝の左右の余白と高さの割合の基準）。</summary>
    private SliderBase _base;
    /// <summary>今の溝の置き場（左端・長さ・中心の高さ）。</summary>
    private SliderTrack _track;
    /// <summary>溝を合わせたレイアウトの大きさの見張り。</summary>
    private LayoutSizeWatch _layoutSize;

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
        // プレハブの寸法: 部品の大きさ（当たりの帯の Sprite。無ければ溝を左右・上下の同じ余白で囲んだ大きさ）・溝の左端と長さ・溝の中心の高さ
        if (SpriteOf(TrackChild) is { } track && TransformOf(TrackChild) is { } ct)
        {
            var size = SpriteOf() is { } bg
                ? bg.Size
                : new Vector2(ct.Position.x * BothSides + track.Width, ct.Position.y * BothSides + track.Height);
            _base = new SliderBase(size.x, size.y, ct.Position.x, track.Width, ct.Position.y + track.Height * Half);
        }
        _track = SliderGeometry.FromBase(_base);
        Value = ValueMath.Snap(Value, Min, Max, Step);
    }

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        // レイアウトの大きさ（前のフレームの描画の値）が変わったら溝を置き直す
        if (gameObject.GetComponent<CanvasTransform>() is not { HasLayout: true } ct) return;
        var size = ct.LayoutSize;
        if (!_layoutSize.Update(size)) return;
        var track = SliderGeometry.Resolve(_base, size.x, size.y);
        if (track == _track) return;
        _track = track;
        Refresh();
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
        SetValue(ValueMath.ValueAt(e.LocalPosition.x, _track.X, _track.Length, Min, Max, Step));
        if (IsDragging) Refresh();
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        bool disabled = !IsEnabled;
        float fade = disabled ? Theme.Number(UiTokens.OpacityDisabled) : 1f;
        float t = ValueMath.Fraction(Value, Min, Max);
        float trackThickness = Theme.Number(UiTokens.SizeSliderTrack);
        float top = _track.CenterY - trackThickness * Half;
        if (SpriteOf(TrackChild) is { } track)
        {
            track.Color = UiColorMath.FadeAlpha(Theme.Color(UiTokens.ColorSurfaceVariant), fade);
            // 溝の長さはレイアウトの幅に合わせた値（伸ばしていない部品はプレハブの長さのまま）
            track.Size = new Vector2(_track.Length, trackThickness);
            track.CornerRadius = trackThickness * Half;
        }
        if (TransformOf(TrackChild) is { } trackCt)
            trackCt.Position = new Vector2(_track.X, top);
        var active = disabled ? Theme.Color(UiTokens.ColorOnDisabled) : Theme.Color(UiTokens.ColorPrimary);
        if (SpriteOf(FillChild) is { } fill)
        {
            fill.Color = active;
            fill.Size = new Vector2(_track.Length * t, trackThickness);
            fill.CornerRadius = trackThickness * Half;
        }
        if (TransformOf(FillChild) is { } fillCt)
            fillCt.Position = new Vector2(_track.X, top);
        float thumb = Theme.Number(IsDragging ? UiTokens.SizeSliderThumbPressed : UiTokens.SizeSliderThumb);
        if (SpriteOf(ThumbChild) is { } th)
        {
            th.Shape = SpriteShapeKind.Ellipse;
            th.Color = active;
            th.Size = new Vector2(thumb, thumb);
        }
        if (TransformOf(ThumbChild) is { } thumbCt)
            thumbCt.Position = new Vector2(_track.X + _track.Length * t - thumb * Half, _track.CenterY - thumb * Half);
    }
}
