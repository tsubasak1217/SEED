namespace SEED.UI;

// ============================================================
//  ButtonLook.cs — ボタンの状態 → 見た目（W2-4。純粋な計算）
//
//  状態（押下・無効）と種類（塗り・薄い塗り・枠・文字だけ）から、背景・中身（文字・アイコン）・枠の色と太さ・角丸を
//  1 か所で決める。値はすべてテーマのトークン（UiTokens）から取る。部品（Button）はこの結果をプレハブの
//  スプライト・文字へ当てるだけ。規則の表は docs/ui_components.md §3。
// ============================================================

/// <summary>ボタンの種類。</summary>
public enum ButtonVariant
{
    /// <summary>主の色で塗る（既定）。</summary>
    Filled = 0,
    /// <summary>薄い塗り（選択の色）。</summary>
    Tonal = 1,
    /// <summary>枠だけ。</summary>
    Outlined = 2,
    /// <summary>文字だけ。</summary>
    Text = 3,
}

/// <summary>ボタンの見た目。</summary>
/// <param name="Background">背景の色（透明もある）。</param>
/// <param name="Content">文字・アイコンの色。</param>
/// <param name="Border">枠の色。</param>
/// <param name="BorderWidth">枠の太さ（0 = 枠なし）。</param>
/// <param name="CornerRadius">角丸の半径。</param>
public readonly record struct ButtonLook(Color Background, Color Content, Color Border, float BorderWidth, float CornerRadius);

/// <summary>ボタンの状態 → 見た目。</summary>
public static class ButtonLooks
{
    /// <summary>
    /// 見た目を決める。
    /// </summary>
    /// <param name="variant">種類。</param>
    /// <param name="pressed">押している（押下の見た目。長押しの間も）。</param>
    /// <param name="disabled">無効（押せない・処理中）。</param>
    /// <param name="theme">テーマ。</param>
    public static ButtonLook Resolve(ButtonVariant variant, bool pressed, bool disabled, UiThemeData theme)
    {
        var radius = theme.Number(UiTokens.RadiusButton);
        var borderWidth = variant == ButtonVariant.Outlined ? theme.Number(UiTokens.SizeBorder) : 0f;
        var transparent = UiColorMath.Transparent;
        if (disabled)
        {
            // 無効: 塗りは無効の色、枠と文字は無効の文字の色（押下の見た目は出さない）
            var onDisabled = theme.Color(UiTokens.ColorOnDisabled);
            var fill = variant is ButtonVariant.Filled or ButtonVariant.Tonal ? theme.Color(UiTokens.ColorDisabled) : transparent;
            return new ButtonLook(fill, onDisabled, theme.Color(UiTokens.ColorDisabled), borderWidth, radius);
        }
        var (background, content) = variant switch
        {
            ButtonVariant.Filled => (theme.Color(UiTokens.ColorPrimary), theme.Color(UiTokens.ColorOnPrimary)),
            ButtonVariant.Tonal => (theme.Color(UiTokens.ColorSelected), theme.Color(UiTokens.ColorOnSelected)),
            _ => (transparent, theme.Color(UiTokens.ColorPrimary)),
        };
        if (pressed)
            background = UiColorMath.Over(background, theme.Color(UiTokens.ColorStateLayer), theme.Number(UiTokens.OpacityPressed));
        return new ButtonLook(background, content, theme.Color(UiTokens.ColorOutline), borderWidth, radius);
    }
}
