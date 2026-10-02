namespace SEED.UI;

// ============================================================
//  ButtonLook.cs — ボタンの状態 → 見た目（W2-4。純粋な計算）
//
//  状態（押下・無効）と種類（塗り・薄い塗り・枠・文字だけ）と色の役割（主の色・危険）から、背景・中身（文字・アイコン）・
//  枠の色と太さ・角丸を 1 か所で決める。値はすべてテーマのトークン（UiTokens）から取る。部品（Button）はこの結果をプレハブの
//  スプライト・文字へ当てるだけ。規則の表は docs/ui_components.md §6。
//  【色の役割（2026-10-02）】ButtonTone.Danger（「削除」「破棄して戻る」）は、主の色（color.primary・color.on_primary）の代わりに
//  エラーの色（color.error・color.on_error）を使う: Filled = エラーの塗りと on_error の文字、Outlined = エラーの枠と文字、Text = エラーの文字。
//  Tonal は Material 3 の errorContainer に当たるトークンが無いので Filled と同じ（エラーの塗り）にする（docs/ui_components.md §6）。
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

/// <summary>ボタンの色の役割（2026-10-02）。</summary>
public enum ButtonTone
{
    /// <summary>主の色（既定。color.primary・color.on_primary）。</summary>
    Primary = 0,
    /// <summary>危険（削除・破棄など取り消せない操作。color.error・color.on_error）。</summary>
    Danger = 1,
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
    /// 見た目を決める（色の役割は主の色）。
    /// </summary>
    /// <param name="variant">種類。</param>
    /// <param name="pressed">押している（押下の見た目。長押しの間も）。</param>
    /// <param name="disabled">無効（押せない・処理中）。</param>
    /// <param name="theme">テーマ。</param>
    public static ButtonLook Resolve(ButtonVariant variant, bool pressed, bool disabled, UiThemeData theme)
        => Resolve(variant, ButtonTone.Primary, pressed, disabled, theme);

    /// <summary>
    /// 見た目を決める（2026-10-02: 色の役割つき）。
    /// </summary>
    /// <param name="variant">種類。</param>
    /// <param name="tone">色の役割（主の色・危険）。</param>
    /// <param name="pressed">押している（押下の見た目。長押しの間も）。</param>
    /// <param name="disabled">無効（押せない・処理中）。無効は色の役割に依らず同じ灰色。</param>
    /// <param name="theme">テーマ。</param>
    public static ButtonLook Resolve(ButtonVariant variant, ButtonTone tone, bool pressed, bool disabled, UiThemeData theme)
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
        bool danger = tone == ButtonTone.Danger;
        // 色の役割の「強い色」と「その上の文字の色」（主の色かエラーの色）
        var accent = theme.Color(danger ? UiTokens.ColorError : UiTokens.ColorPrimary);
        var onAccent = theme.Color(danger ? UiTokens.ColorOnError : UiTokens.ColorOnPrimary);
        var (background, content) = variant switch
        {
            ButtonVariant.Filled => (accent, onAccent),
            // 薄い塗り: 主の色は選択の色。危険は errorContainer のトークンが無いので塗りと同じ
            ButtonVariant.Tonal => danger ? (accent, onAccent) : (theme.Color(UiTokens.ColorSelected), theme.Color(UiTokens.ColorOnSelected)),
            _ => (transparent, accent),
        };
        if (pressed)
            background = UiColorMath.Over(background, theme.Color(UiTokens.ColorStateLayer), theme.Number(UiTokens.OpacityPressed));
        // 枠: 主の色の枠のボタンは outline、危険はエラーの色（文字と同じ色の枠で危険を示す）
        var border = danger ? accent : theme.Color(UiTokens.ColorOutline);
        return new ButtonLook(background, content, border, borderWidth, radius);
    }
}
