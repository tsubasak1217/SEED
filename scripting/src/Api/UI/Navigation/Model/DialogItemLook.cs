using System;

namespace SEED.UI;

// ============================================================
//  DialogItemLook.cs — 選択肢の一覧のダイアログの 1 行（DialogItem）の見た目と置き場（2026-10-02。純粋な計算）
//
//  行は札の幅いっぱい（押した重ね色が札の左右の端まで。Flutter の SimpleDialogOption の InkWell と同じ）で、中身は左右に
//  size.dialog_padding（24）の余白を取る（題と文字の左端がそろう）。アイコン欄（一覧のどれかにアイコンがあるとき）は
//  size.icon の大きさで行の高さの真ん中、文字はアイコン欄 ＋ size.icon_gap の右から、行の高さの真ん中に 1 行で置く。
//  | 状態   | 背景                         | 文字                 | アイコン（指定の色が無いとき） |
//  |--------|------------------------------|----------------------|--------------------------------|
//  | ふつう | 透明                         | color.on_surface     | color.on_surface_muted         |
//  | 危険   | 透明                         | color.error          | color.error                    |
//  | 押下   | color.state_layer を opacity.pressed で重ねる | （同じ）             | （同じ）                       |
//  | 選べない | 透明（押下を出さない）      | color.on_disabled    | color.on_disabled              |
// ============================================================

/// <summary>選択肢の 1 行の見た目。</summary>
/// <param name="Background">行の背景（押下の重ね色。ふだんは透明）。</param>
/// <param name="Label">文字の色。</param>
/// <param name="IconColor">アイコンの既定の色（アイコンに色の指定が無いとき。図形のアイコン）。</param>
/// <param name="IconFade">アイコンの濃さ（選べない行は opacity.disabled。画像のアイコンも薄くする）。</param>
public readonly record struct DialogItemLook(Color Background, Color Label, Color IconColor, float IconFade);

/// <summary>選択肢の 1 行の中身の置き場。</summary>
/// <param name="IconX">アイコンの左端（行の左端から）。</param>
/// <param name="LabelX">文字の枠の左端。</param>
/// <param name="LabelWidth">文字の枠の幅（右の余白まで。0 未満にしない）。</param>
/// <param name="LabelHeight">文字の枠の高さ（行の高さ。文字は縦の真ん中）。</param>
public readonly record struct DialogItemPlacement(float IconX, float LabelX, float LabelWidth, float LabelHeight);

/// <summary>選択肢の 1 行の見た目と置き場の計算。</summary>
public static class DialogItemLooks
{
    /// <summary>半分。</summary>
    private const float Half = 0.5f;
    /// <summary>薄めない濃さ。</summary>
    private const float NoFade = 1f;

    /// <summary>
    /// 見た目を決める。
    /// </summary>
    /// <param name="kind">種類（危険なら color.error）。</param>
    /// <param name="pressed">押している。</param>
    /// <param name="disabled">選べない（押下の見た目も出さない）。</param>
    /// <param name="theme">テーマ。</param>
    public static DialogItemLook Resolve(DialogButtonKind kind, bool pressed, bool disabled, UiThemeData theme)
    {
        var transparent = UiColorMath.Transparent;
        if (disabled)
        {
            var onDisabled = theme.Color(UiTokens.ColorOnDisabled);
            return new DialogItemLook(transparent, onDisabled, onDisabled, theme.Number(UiTokens.OpacityDisabled, NoFade));
        }
        bool danger = kind == DialogButtonKind.Danger;
        var label = theme.Color(danger ? UiTokens.ColorError : UiTokens.ColorOnSurface);
        var icon = theme.Color(danger ? UiTokens.ColorError : UiTokens.ColorOnSurfaceMuted);
        var background = pressed
            ? UiColorMath.Over(transparent, theme.Color(UiTokens.ColorStateLayer), theme.Number(UiTokens.OpacityPressed))
            : transparent;
        return new DialogItemLook(background, label, icon, NoFade);
    }

    /// <summary>
    /// 中身の置き場を決める。
    /// </summary>
    /// <param name="rowWidth">行の幅（札の幅）。</param>
    /// <param name="rowHeight">行の高さ（size.dialog_item_height）。</param>
    /// <param name="padding">左右の余白（size.dialog_padding）。</param>
    /// <param name="iconColumn">アイコン欄を取るか（一覧のどれかにアイコンがある）。</param>
    /// <param name="columnWidth">アイコン欄の幅（size.icon。どの行も同じ幅にして文字の左端をそろえる）。</param>
    /// <param name="iconGap">アイコンと文字の間（size.icon_gap）。</param>
    public static DialogItemPlacement Place(float rowWidth, float rowHeight, float padding, bool iconColumn, float columnWidth, float iconGap)
    {
        float w = NonNegative(rowWidth), h = NonNegative(rowHeight), pad = NonNegative(padding);
        float labelX = iconColumn ? pad + NonNegative(columnWidth) + NonNegative(iconGap) : pad;
        return new DialogItemPlacement(pad, labelX, Math.Max(0f, w - labelX - pad), h);
    }

    /// <summary>行の高さの真ん中に置く物の上端（物が行より高ければ負）。</summary>
    /// <param name="rowHeight">行の高さ。</param>
    /// <param name="size">物の高さ（アイコンの大きさ）。</param>
    public static float CenterTop(float rowHeight, float size) => (NonNegative(rowHeight) - NonNegative(size)) * Half;

    /// <summary>負・有限でない値を 0 にする。</summary>
    private static float NonNegative(float value) => float.IsFinite(value) && value > 0f ? value : 0f;
}
