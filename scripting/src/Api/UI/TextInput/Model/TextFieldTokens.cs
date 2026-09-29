namespace SEED.UI;

// ============================================================
//  TextFieldTokens.cs — 入力欄（W2-6b。SEED.UI.TextField）が読むテーマのトークンの名前（docs/ui_text_input.md §9・docs/ui_theme.md §8）
//
//  値の表（既定）は Theme/default_theme.json（color.selection・size.field_*・size.caret・size.composition_underline・
//  size.keyboard_gap・text.field・text.field_number・motion.caret_blink）。型と「使う部品」の表は UiTokenCatalog。
//  色・角丸・枠は共通のトークン（color.outline・color.primary・color.error・color.on_surface・color.on_surface_muted・radius.field・
//  size.border）も使う。値の出どころ: Material Design 3 の Outlined text field（高さ 56・左右の余白 16・フォーカスの枠 2・
//  文字 16）を目安に、Wake or Pay の数値の欄（枠線の角丸・約 112 dp・数字は中央で大きめ）に合わせて高さ 52・数字 24 にした。
//  カーソルの点滅 0.5 秒は Flutter の EditableText（_kCursorBlinkHalfPeriod 500ms）。
// ============================================================

/// <summary>入力欄のトークンの名前。</summary>
public static class TextFieldTokens
{
    /// <summary>選択の背景（主の色を薄く）。</summary>
    public const string ColorSelection = "color.selection";

    /// <summary>入力欄の高さ（プレハブの既定の大きさ。部品はレイアウトの大きさを優先する）。</summary>
    public const string SizeFieldHeight = "size.field_height";
    /// <summary>入力欄の左右の内側の余白。</summary>
    public const string SizeFieldPadding = "size.field_padding";
    /// <summary>フォーカス・エラーのときの枠の太さ。</summary>
    public const string SizeFieldFocusBorder = "size.field_focus_border";
    /// <summary>カーソルの太さ。</summary>
    public const string SizeCaret = "size.caret";
    /// <summary>変換中の文字の下線の太さ。</summary>
    public const string SizeCompositionUnderline = "size.composition_underline";
    /// <summary>キーボードを避けるとき、欄の下端とキーボードの上端の間に空ける余白。</summary>
    public const string SizeKeyboardGap = "size.keyboard_gap";

    /// <summary>文字の欄の文字の大きさ。</summary>
    public const string TextField = "text.field";
    /// <summary>数字の欄（数値の欄）の文字の大きさ。</summary>
    public const string TextFieldNumber = "text.field_number";

    /// <summary>カーソルの点滅の半周期（見える・見えないの各秒。0 以下で点滅しない）。</summary>
    public const string MotionCaretBlink = "motion.caret_blink";

    /// <summary>入力欄が読むトークン（既定のテーマに揃っているかをテストが確かめる）。</summary>
    public static readonly string[] All =
    {
        ColorSelection,
        SizeFieldHeight, SizeFieldPadding, SizeFieldFocusBorder, SizeCaret, SizeCompositionUnderline, SizeKeyboardGap,
        TextField, TextFieldNumber,
        MotionCaretBlink,
    };
}
