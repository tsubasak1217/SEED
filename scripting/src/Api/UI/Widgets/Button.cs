using System;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  Button.cs — ボタン（W2-4。docs/ui_components.md §3）
//
//  【プレハブ】templates/ui/prefabs/button.actor
//      Button（Sprite = 背景〈角丸〉・CanvasGesture〈タップ・長押し・押下の見た目・最小 48 dp〉・このスクリプト）
//      ├─ Label（Text。任意）
//      └─ Icon（Sprite。任意）
//  【状態】通常・押下（PressDown 〜 PressUp／PressCancel。W2-2 のアリーナがスクロールに負けた押下を取り消す）・
//  長押し中（LongPress 〜 離す）・無効（Interactable = false か Busy）。見た目は ButtonLooks.Resolve の 1 か所。
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

    /// <summary>種類（塗り・薄い塗り・枠・文字だけ）。</summary>
    [SerializeField(Label = "種類")]
    public ButtonVariant Variant = ButtonVariant.Filled;

    /// <summary>文字（空ならプレハブの Label のまま）。</summary>
    [SerializeField(Label = "文字")]
    public string Text = "";

    /// <summary>文字の大きさのトークン（既定 text.label。空ならプレハブの大きさのまま。丸いボタンの記号は text.title など）。</summary>
    [SerializeField(Label = "文字の大きさ")]
    public string LabelSize = UiTokens.TextLabel;

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

    /// <inheritdoc />
    public override bool IsEnabled => Interactable && !_busy;

    /// <summary>文字を変える。</summary>
    public void SetText(string text)
    {
        Text = text;
        if (TextOf(LabelChild) is { } label) label.Content = text;
    }

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        if (Text.Length > 0) SetText(Text);
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
    public override void OnGestureTap(GestureEvent e)
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
        var look = ButtonLooks.Resolve(Variant, IsPressed || IsLongPressing, !IsEnabled, Theme);
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
            if (LabelSize.Length > 0) label.FontSize = Theme.Number(LabelSize, label.FontSize);
        }
        if (SpriteOf(IconChild) is { } icon) icon.Color = look.Content;
    }
}
