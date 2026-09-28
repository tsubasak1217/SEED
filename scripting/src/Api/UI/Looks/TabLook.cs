namespace SEED.UI;

// ============================================================
//  TabLook.cs — 下のタブの項目の状態 → 見た目（W2-7。純粋な計算）
//
//  | 状態       | 選択の印（丸い帯）              | アイコン・文字          |
//  |------------|---------------------------------|-------------------------|
//  | 選んだ     | color.selected で出す           | color.on_surface        |
//  | 選んでいない | 出さない                      | color.on_surface_muted  |
//  | 押している | 印の所に押下の重ね色を重ねて出す | 状態のまま              |
//  | 無効       | 出さない                        | color.on_disabled       |
//  印の大きさ・角丸は size.tab_indicator_width・size.tab_indicator_height・radius.tab_indicator（Material 3 のナビゲーションバー）。
// ============================================================

/// <summary>タブの項目の見た目。</summary>
/// <param name="Indicator">選択の印の色。</param>
/// <param name="IndicatorVisible">印を出すか。</param>
/// <param name="Content">アイコン・文字の色。</param>
/// <param name="IndicatorSize">印の大きさ。</param>
/// <param name="IndicatorRadius">印の角丸。</param>
public readonly record struct TabItemLook(Color Indicator, bool IndicatorVisible, Color Content, Vector2 IndicatorSize, float IndicatorRadius);

/// <summary>タブの項目の状態 → 見た目。</summary>
public static class TabLooks
{
    /// <summary>見た目を決める。</summary>
    public static TabItemLook Resolve(bool selected, bool pressed, bool disabled, UiThemeData theme)
    {
        var size = new Vector2(theme.Number(NavTokens.SizeTabIndicatorWidth), theme.Number(NavTokens.SizeTabIndicatorHeight));
        float radius = theme.Number(NavTokens.RadiusTabIndicator);
        if (disabled)
            return new TabItemLook(UiColorMath.Transparent, false, theme.Color(UiTokens.ColorOnDisabled), size, radius);
        var baseIndicator = selected ? theme.Color(UiTokens.ColorSelected) : UiColorMath.Transparent;
        var indicator = pressed
            ? UiColorMath.Over(baseIndicator, theme.Color(UiTokens.ColorStateLayer), theme.Number(UiTokens.OpacityPressed))
            : baseIndicator;
        var content = selected ? theme.Color(UiTokens.ColorOnSurface) : theme.Color(UiTokens.ColorOnSurfaceMuted);
        return new TabItemLook(indicator, selected || pressed, content, size, radius);
    }
}
