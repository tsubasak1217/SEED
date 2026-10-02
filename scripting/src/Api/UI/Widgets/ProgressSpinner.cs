using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  ProgressSpinner.cs — 不定の進捗（回る弧・スピナー。2026-10-02。docs/ui_components.md §6）
//
//  【プレハブ】templates/ui/prefabs/progress_spinner.actor
//      ProgressSpinner（Sprite = 弧〈形 = 弧・端を丸く〉・このスクリプト）
//  終わりの分からない待ち（通信・購入・復元）に出す。値の輪（ProgressRing）と違い値を持たず、弧が伸び縮みしながら回り続ける
//  （Flutter の CircularProgressIndicator の不定の動き。式は SpinnerMotion）。色は color.primary（無効は opacity.disabled で薄く・止める）。
//  大きさ（弧の外側の直径）と太さは部品ごとに指定でき（Size・Thickness）、0 以下ならテーマの size.spinner・size.spinner_thickness。
//  【描き続け】回っている間（Spinning かつ 押せる かつ 自分と祖先が表示 かつ 画面と重なる）は毎フレーム弧を書き、少しだけ描き続けを頼む
//  （Redraw.KeepAlive。隠した・止めた・スクロールで画面の外へ出たら次のフレームから頼まない＝render_policy: on_demand で描画が止まる）。
//  親の切り抜き（CanvasClip）で見えないだけの所にあるときは見分けられず回り続ける（docs/ui_components.md §10）。
//  形と塗りの弧（SpriteShapeKind.Arc。W2-4 の SDF）で描くので、毎フレーム SEED.Draw を呼ばない。
// ============================================================

/// <summary>不定の進捗（回る弧）。</summary>
public sealed class ProgressSpinner : UiWidget
{
    /// <summary>
    /// 描き続けを頼む長さ（秒）。回っている間は毎フレーム延ばすので、隠した・止めた後はこの時間で描画が止まる
    /// （60 fps の 6 フレームぶん。フレームが 1 つ遅れても途切れない長さ）。
    /// </summary>
    private const float KeepAliveSeconds = 0.1f;
    /// <summary>薄めない濃さ。</summary>
    private const float NoFade = 1f;

    /// <summary>大きさ（弧の外側の直径。キャンバスの単位。0 以下 = テーマの size.spinner）。</summary>
    [SerializeField(Label = "大きさ")]
    public float Size;

    /// <summary>弧の太さ（キャンバスの単位。0 以下 = テーマの size.spinner_thickness）。</summary>
    [SerializeField(Label = "太さ")]
    public float Thickness;

    /// <summary>回すか（false で今の姿のまま止める）。</summary>
    [SerializeField(Label = "回す")]
    public bool Spinning = true;

    /// <summary>回し始めてからの秒（実時間。止めている間・見えていない間は進めない）。</summary>
    private double _elapsed;

    /// <summary>今の大きさ（部品の指定か、テーマの size.spinner）。</summary>
    public float ResolvedSize => UiSizeOverride.Resolve(Size, Theme.Number(UiTokens.SizeSpinner));

    /// <summary>今の太さ（部品の指定か、テーマの size.spinner_thickness）。</summary>
    public float ResolvedThickness => UiSizeOverride.Resolve(Thickness, Theme.Number(UiTokens.SizeSpinnerThickness));

    /// <summary>回す・止める。</summary>
    /// <param name="spinning">回すか。</param>
    public void SetSpinning(bool spinning)
    {
        if (Spinning == spinning) return;
        Spinning = spinning;
        Refresh();
    }

    /// <summary>大きさを変える（0 以下 = テーマの size.spinner）。</summary>
    /// <param name="size">弧の外側の直径（キャンバスの単位）。</param>
    public void SetSize(float size)
    {
        if (Size == size) return;
        Size = size;
        Refresh();
    }

    /// <summary>太さを変える（0 以下 = テーマの size.spinner_thickness）。</summary>
    /// <param name="thickness">弧の太さ（キャンバスの単位）。</param>
    public void SetThickness(float thickness)
    {
        if (Thickness == thickness) return;
        Thickness = thickness;
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        // 回すのは「回す・押せる・自分と祖先が表示・画面と重なる」の間だけ（見えていない間・スクロールで画面の外へ出た間は
        // 描き続けを頼まない。画面へ戻ればスクロールの描画のフレームで再び回り出す）
        if (!Spinning || !IsEnabled || !UiVisibility.IsShownInHierarchy(gameObject) || !UiVisibility.IsOnScreen(gameObject)) return;
        // 実時間で進める（ゲームの時間の倍率に依らない。1 フレームが重くても動きは途切れず、そのぶん進んで見えるだけ）
        float step = Time.UnscaledDeltaTime;
        if (float.IsFinite(step) && step > 0f) _elapsed += step;
        ApplyArc();
        Redraw.KeepAlive(KeepAliveSeconds);
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        if (SpriteOf() is not { } arc) return;
        float size = ResolvedSize;
        float fade = IsEnabled ? NoFade : Theme.Number(UiTokens.OpacityDisabled);
        arc.Shape = SpriteShapeKind.Arc;
        arc.Size = new Vector2(size, size);
        arc.ArcThickness = ResolvedThickness;
        arc.ArcRoundCaps = true;
        arc.Color = UiColorMath.FadeAlpha(Theme.Color(UiTokens.ColorPrimary), fade);
        ApplyArc();
    }

    /// <summary>今の時刻の弧（始まりと角度）を当てる。</summary>
    private void ApplyArc()
    {
        if (SpriteOf() is not { } arc) return;
        var shape = SpinnerMotion.At(_elapsed, Theme.Number(UiTokens.MotionSpinnerCycle), Theme.Number(UiTokens.MotionSpinnerRotation));
        arc.ArcStart = shape.StartDegrees;
        arc.ArcSweep = shape.SweepDegrees;
    }
}
