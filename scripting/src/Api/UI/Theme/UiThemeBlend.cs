namespace SEED.UI;

// ============================================================
//  UiThemeBlend.cs — 2 つのテーマの表の間の表（切り替えの色の補間。W2-9。docs/ui_theme.md §5。純粋な計算）
//
//  色だけを補間する（sRGB の成分ごと。UiColorMath.LerpSrgb）。数・文字列・名前・明暗は行き先の値（切り替えの最初から）:
//  大きさ・角丸・文字の大きさまで補間すると、並び（レイアウト）が毎フレーム動いて文字の折り返しなどが揺れるため。
//  行き先にだけある色は行き先の色、元にだけある色は捨てる（行き先の表の名前の集合と同じ）。
// ============================================================

/// <summary>2 つのテーマの表の間の表。</summary>
public static class UiThemeBlend
{
    /// <summary>
    /// <paramref name="from"/> から <paramref name="to"/> への途中の表（t は 0..1。0 = 元の色・1 = 行き先そのもの）。
    /// </summary>
    public static UiThemeData Blend(UiThemeData from, UiThemeData to, float t)
    {
        if (t >= 1f) return to;
        var table = UiThemeTable.From(to);
        foreach (var token in to.ColorTokens)
        {
            if (!from.TryColor(token, out var a)) continue;
            table.Set(token, UiThemeValue.FromColor(UiColorMath.LerpSrgb(a, to.Color(token), t)));
        }
        return table.Freeze(to.Name, to.Brightness, to.Warnings);
    }
}
