using System;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  Button.cs — ボタン（W2-4。docs/ui_components.md §6）
//
//  【プレハブ】templates/ui/prefabs/button.actor
//      Button（Sprite = 背景〈角丸〉・CanvasGesture〈タップ・長押し・押下の見た目・最小 48 dp〉・このスクリプト）
//      ├─ Label（Text。任意）
//      └─ Icon（Sprite。任意）
//  【状態】通常・押下（PressDown 〜 PressUp／PressCancel。W2-2 のアリーナがスクロールに負けた押下を取り消す）・
//  長押し中（LongPress 〜 離す）・無効（Interactable = false か Busy）。見た目は ButtonLooks.Resolve の 1 か所。
//  【色の役割（2026-10-02）】Tone = Danger で「削除」「破棄して戻る」などをエラーの色（color.error・color.on_error）で描く。
//  【文字の枠（2026-10-02）】FitLabel（既定 true）なら、ボタンのレイアウトの大きさが変わるたびに文字の枠をボタンの大きさに合わせる
//  （プレハブでの「文字の枠 − ボタンの大きさ」の差を保つ。ButtonLabelFit）。コンテナが幅いっぱいに伸ばしたボタンでも、真ん中寄せの文字が
//  ボタンの真ん中に来る（以前は枠がプレハブの幅のまま左に寄った。Wake or Pay の W3-3 (2)）。伸ばしていないボタンは今までと同じ。
//  【二重押しの防止】Busy（処理中）の間は押せない（roadmap §3.3「処理中は無効」）。
//  【触感】タップで SEED.Platform.Haptics.Tap（Android の端末だけ。Haptic = false で止める）。
// ============================================================

/// <summary>ボタン。</summary>
public sealed class Button : UiWidget
{
    /// <summary>子の文字の名前。</summary>
    private const string LabelChild = "Label";
    /// <summary>子のアイコンの名前。</summary>
    private const string IconChild = "Icon";
    /// <summary>大きさが変わったとみなす差（キャンバスの単位。浮動小数の丸めの差では合わせ直さない）。</summary>
    private const float SizeEpsilon = 0.01f;

    /// <summary>種類（塗り・薄い塗り・枠・文字だけ）。</summary>
    [SerializeField(Label = "種類")]
    public ButtonVariant Variant = ButtonVariant.Filled;

    /// <summary>色の役割（主の色・危険。2026-10-02）。</summary>
    [SerializeField(Label = "色の役割")]
    public ButtonTone Tone = ButtonTone.Primary;

    /// <summary>文字（空ならプレハブの Label のまま）。</summary>
    [SerializeField(Label = "文字")]
    public string Text = "";

    /// <summary>
    /// 文字の大きさ（既定 text.label。テーマのトークンの名前か数〈"18"〉。空ならプレハブの大きさのまま。丸いボタンの記号は text.title など。
    /// 読み方は UiTextSize）。
    /// </summary>
    [SerializeField(Label = "文字の大きさ")]
    public string LabelSize = UiTokens.TextLabel;

    /// <summary>文字の枠をボタンの大きさに合わせるか（2026-10-02。既定 true。幅いっぱいのボタンでも文字が真ん中に来る）。</summary>
    [SerializeField(Label = "文字の枠を大きさに合わせる")]
    public bool FitLabel = true;

    /// <summary>タップで端末を震わせるか（Android の端末だけ）。</summary>
    [SerializeField(Label = "触感")]
    public bool Haptic = true;

    /// <summary>押された（タップ）。</summary>
    public event Action<Button>? Clicked;
    /// <summary>長押しされた（押し続けて 500ms）。</summary>
    public event Action<Button>? LongPressed;
    /// <summary>押下が終わった（離した・取り消された。長押しの連続を止める合図）。</summary>
    public event Action<Button>? Released;

    /// <summary>押している（押下の見た目）。</summary>
    public bool IsPressed { get; private set; }
    /// <summary>長押しの後、まだ押している。</summary>
    public bool IsLongPressing { get; private set; }

    /// <summary>処理中（true の間は押せない＝二重押しの防止）。</summary>
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            Refresh();
        }
    }
    private bool _busy;

    /// <summary>開始時のボタンの大きさ（Sprite。文字の枠を合わせる基準）。</summary>
    private Vector2 _baseSize;
    /// <summary>開始時の文字の枠（Text.BoxWidth・BoxHeight。0 の軸は枠なし）。</summary>
    private Vector2 _baseBox;
    /// <summary>文字の枠を合わせられるか（文字の子とボタンの Sprite がある）。</summary>
    private bool _canFitLabel;
    /// <summary>文字の枠を合わせたレイアウトの大きさの見張り。</summary>
    private LayoutSizeWatch _layoutSize;

    /// <inheritdoc />
    public override bool IsEnabled => Interactable && !_busy;

    /// <summary>文字を変える。</summary>
    public void SetText(string text)
    {
        Text = text;
        if (TextOf(LabelChild) is { } label) label.Content = text;
    }

    /// <summary>色の役割を変える（2026-10-02。見た目も変える）。</summary>
    public void SetTone(ButtonTone tone)
    {
        if (Tone == tone) return;
        Tone = tone;
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        if (Text.Length > 0) SetText(Text);
        // 文字の枠を合わせる基準（プレハブの大きさと文字の枠。差を保ってボタンの大きさへ合わせる）
        if (SpriteOf() is { } bg && TextOf(LabelChild) is { } label)
        {
            _baseSize = bg.Size;
            _baseBox = new Vector2(label.BoxWidth, label.BoxHeight);
            _canFitLabel = true;
        }
    }

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt) => FitLabelBox();

    /// <summary>
    /// レイアウトの大きさ（前のフレームの描画の値）が変わったら、文字の枠をボタンの大きさに合わせる（ButtonLabelFit。2026-10-02）。
    /// </summary>
    private void FitLabelBox()
    {
        if (!FitLabel || !_canFitLabel) return;
        if (gameObject.GetComponent<CanvasTransform>() is not { HasLayout: true } ct) return;
        var size = ct.LayoutSize;
        if (!_layoutSize.Update(size, SizeEpsilon)) return;
        if (TextOf(LabelChild) is not { } label) return;
        var box = ButtonLabelFit.BoxSize(size, _baseSize, _baseBox);
        bool changed = false;
        if (MathF.Abs(label.BoxWidth - box.x) > SizeEpsilon) { label.BoxWidth = box.x; changed = true; }
        if (MathF.Abs(label.BoxHeight - box.y) > SizeEpsilon) { label.BoxHeight = box.y; changed = true; }
        if (changed) Redraw.Request();
    }

    /// <inheritdoc />
    public override void OnGesturePressDown(GestureEvent e)
    {
        if (!IsEnabled) return;
        IsPressed = true;
        Refresh();
    }

    /// <inheritdoc />
    public override void OnGesturePressCancel(GestureEvent e) => EndPress();

    /// <inheritdoc />
    public override void OnGesturePressUp(GestureEvent e) => EndPress();

    /// <inheritdoc />
    public override void OnGestureTap(GestureEvent e) => PerformClick();

    /// <summary>
    /// 押す（指のタップと同じ: 押せるなら触感と Clicked。2026-10-03。方向キー・パッドの決定〈UiNavigation の ButtonNav〉もここを通る）。
    /// </summary>
    internal void PerformClick()
    {
        if (!IsEnabled) return;
        if (Haptic) SEED.Platform.Haptics.Tap();
        Clicked?.Invoke(this);
    }

    /// <inheritdoc />
    public override void OnGestureLongPress(GestureEvent e)
    {
        if (!IsEnabled) return;
        IsLongPressing = true;
        LongPressed?.Invoke(this);
    }

    /// <summary>押下を終える（見た目を戻して Released を知らせる）。</summary>
    private void EndPress()
    {
        bool wasPressed = IsPressed || IsLongPressing;
        IsPressed = false;
        IsLongPressing = false;
        Refresh();
        if (wasPressed) Released?.Invoke(this);
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        var look = ButtonLooks.Resolve(Variant, Tone, IsPressed || IsLongPressing, !IsEnabled, Theme);
        if (SpriteOf() is { } bg)
        {
            bg.Color = look.Background;
            bg.CornerRadius = look.CornerRadius;
            bg.BorderWidth = look.BorderWidth;
            bg.BorderColor = look.Border;
        }
        if (TextOf(LabelChild) is { } label)
        {
            label.Color = look.Content;
            // 大きさ（LabelSize が空なら変えない。トークンか数）・書体・太さ（W2-9）
            UiTextStyle.Apply(label, Theme, LabelSize);
        }
        if (SpriteOf(IconChild) is { } icon) icon.Color = look.Content;
    }
}
