namespace SEED.UI;

// ============================================================
//  TextFieldLooks.cs — 入力欄の状態 → 見た目（W2-6b。純粋な計算。docs/ui_text_input.md §9）
//
//  状態は 通常・フォーカス・無効・エラー（roadmap §3.3 の文字入力の行）。Material Design 3 の Outlined text field にそろえた:
//    - 枠: 通常 = color.outline・size.border、フォーカス = color.primary・size.field_focus_border、エラー = color.error・太い枠、
//          無効 = color.disabled・size.border
//    - 塗り: 既定は無し（Outlined。Wake or Pay の数値の欄と同じ）、Filled のときだけ color.surface_variant
//    - 文字 = color.on_surface（無効は color.on_disabled）、例の文 = color.on_surface_muted
//    - カーソル = color.primary（エラーは color.error）、選択 = color.selection、変換中の下線 = color.on_surface
//  角丸は radius.field。
// ============================================================

/// <summary>入力欄の見た目を決める状態。</summary>
/// <param name="Focused">フォーカスがある。</param>
/// <param name="Disabled">無効。</param>
/// <param name="Error">エラー（利用者の入力の誤りを見せる）。</param>
/// <param name="Filled">塗りのある種類（既定は枠だけ）。</param>
public readonly record struct TextFieldLookState(bool Focused, bool Disabled, bool Error, bool Filled);

/// <summary>入力欄の見た目。</summary>
public readonly record struct TextFieldLook(
    Color Background,
    Color Border,
    float BorderWidth,
    float CornerRadius,
    Color Text,
    Color Placeholder,
    Color Caret,
    Color Selection,
    Color Underline);

/// <summary>入力欄の状態 → 見た目。</summary>
public static class TextFieldLooks
{
    /// <summary>状態とテーマから見た目を決める。</summary>
    public static TextFieldLook Resolve(TextFieldLookState state, UiThemeData theme)
    {
        float thin = theme.Number(UiTokens.SizeBorder);
        float thick = theme.Number(TextFieldTokens.SizeFieldFocusBorder);
        var (border, width) = state switch
        {
            { Disabled: true } => (theme.Color(UiTokens.ColorDisabled), thin),
            { Error: true } => (theme.Color(UiTokens.ColorError), thick),
            { Focused: true } => (theme.Color(UiTokens.ColorPrimary), thick),
            _ => (theme.Color(UiTokens.ColorOutline), thin),
        };
        var background = state.Filled && !state.Disabled ? theme.Color(UiTokens.ColorSurfaceVariant) : UiColorMath.Transparent;
        var text = state.Disabled ? theme.Color(UiTokens.ColorOnDisabled) : theme.Color(UiTokens.ColorOnSurface);
        var placeholder = state.Disabled ? theme.Color(UiTokens.ColorOnDisabled) : theme.Color(UiTokens.ColorOnSurfaceMuted);
        var caret = state.Error ? theme.Color(UiTokens.ColorError) : theme.Color(UiTokens.ColorPrimary);
        return new TextFieldLook(
            background, border, width, theme.Number(UiTokens.RadiusField),
            text, placeholder, caret, theme.Color(TextFieldTokens.ColorSelection), theme.Color(UiTokens.ColorOnSurface));
    }
}
