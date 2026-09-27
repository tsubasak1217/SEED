namespace SEED.UI;

// ============================================================
//  SelectionLook.cs — 選択（セグメント・チップ・ラジオ）の項目の状態 → 見た目（W2-4。純粋な計算）
//
//  | 種類       | 選んだ                         | 選んでいない                      |
//  |------------|--------------------------------|-----------------------------------|
//  | セグメント | 選択の色で塗る・選択の文字の色 | 透明・面の文字の色                |
//  | チップ     | 選択の色で塗る・枠なし         | 透明・枠（outline）               |
//  | ラジオ     | 輪は主の色・中の点を出す       | 輪は控えめな色・点を出さない      |
//  押している間は状態の重ね色を重ね、無効は文字・枠を無効の色にする（選べない項目は灰色。roadmap §3.3）。
// ============================================================

/// <summary>選択の項目の種類。</summary>
public enum SelectionLookKind
{
    /// <summary>セグメント（横に並んだ 1 つ選ぶボタン）。</summary>
    Segment = 0,
    /// <summary>チップ（丸い札。複数選べる）。</summary>
    Chip = 1,
    /// <summary>ラジオ（丸い輪と点）。</summary>
    Radio = 2,
}

/// <summary>選択の項目の見た目。</summary>
/// <param name="Background">項目の塗り（ラジオでは輪の中の塗り）。</param>
/// <param name="Label">文字の色。</param>
/// <param name="Border">枠の色（ラジオでは輪の色）。</param>
/// <param name="BorderWidth">枠の太さ。</param>
/// <param name="CornerRadius">角丸（ラジオでは使わない）。</param>
/// <param name="Indicator">印の色（ラジオの点）。</param>
/// <param name="IndicatorVisible">印を出すか。</param>
public readonly record struct SelectionItemLook(
    Color Background, Color Label, Color Border, float BorderWidth, float CornerRadius, Color Indicator, bool IndicatorVisible);

/// <summary>選択の項目の状態 → 見た目。</summary>
public static class SelectionLooks
{
    /// <summary>
    /// 見た目を決める。
    /// </summary>
    /// <param name="kind">項目の種類。</param>
    /// <param name="selected">選んでいる。</param>
    /// <param name="pressed">押している。</param>
    /// <param name="disabled">選べない。</param>
    /// <param name="theme">テーマ。</param>
    public static SelectionItemLook Resolve(SelectionLookKind kind, bool selected, bool pressed, bool disabled, UiThemeData theme)
    {
        var transparent = UiColorMath.Transparent;
        var selectedFill = theme.Color(UiTokens.ColorSelected);
        var onSurface = theme.Color(UiTokens.ColorOnSurface);
        var outline = theme.Color(UiTokens.ColorOutline);
        float thin = theme.Number(UiTokens.SizeBorder);
        SelectionItemLook look = kind switch
        {
            SelectionLookKind.Segment => new SelectionItemLook(
                selected ? selectedFill : transparent,
                selected ? theme.Color(UiTokens.ColorOnSelected) : onSurface,
                outline, 0f, theme.Number(UiTokens.RadiusSegment), transparent, false),
            SelectionLookKind.Chip => new SelectionItemLook(
                selected ? selectedFill : transparent,
                selected ? theme.Color(UiTokens.ColorOnSelected) : onSurface,
                outline, selected ? 0f : thin, theme.Number(UiTokens.RadiusChip), transparent, false),
            _ => new SelectionItemLook(
                transparent, onSurface,
                selected ? theme.Color(UiTokens.ColorPrimary) : theme.Color(UiTokens.ColorOnSurfaceMuted),
                theme.Number(UiTokens.SizeCheckBorder), 0f, theme.Color(UiTokens.ColorPrimary), selected),
        };
        if (disabled)
        {
            var onDisabled = theme.Color(UiTokens.ColorOnDisabled);
            var fill = kind == SelectionLookKind.Radio || !selected ? transparent : theme.Color(UiTokens.ColorDisabled);
            return look with { Background = fill, Label = onDisabled, Border = onDisabled, Indicator = onDisabled };
        }
        if (pressed)
            look = look with
            {
                Background = UiColorMath.Over(look.Background, theme.Color(UiTokens.ColorStateLayer), theme.Number(UiTokens.OpacityPressed)),
            };
        return look;
    }
}
