using System.Globalization;

namespace SEED.UI;

// ============================================================
//  UiTextSize.cs — 部品の文字の大きさの指定（トークンの名前か数）→ 大きさ（2026-10-02。純粋な計算）
//
//  部品の「文字の大きさ」の欄（Button.LabelSize・SelectionGroup〈ChipGroup・RadioGroup・SegmentedControl〉.LabelSize）は、
//  テーマのトークンの名前（既定 text.label。アプリ独自の app.text.chip なども）か、数そのもの（"18"。キャンバスの単位）を書ける。
//    空            … 大きさを変えない（プレハブの大きさのまま）
//    トークンの名前 … テーマの値（テーマに無ければ数として読み、それも駄目なら変えない）
//    数            … その大きさ（0 以下・有限でない値は変えない）
//  以前は ChipGroup・RadioGroup の文字が text.label に固定で、部品ごとに変えられなかった（Wake or Pay の W3-1 (2)）。
// ============================================================

/// <summary>部品の文字の大きさの指定の読み方。</summary>
public static class UiTextSize
{
    /// <summary>
    /// 大きさを決める（決まらなければ false＝変えない）。
    /// </summary>
    /// <param name="theme">テーマ。</param>
    /// <param name="spec">指定（トークンの名前か数。空なら変えない）。</param>
    /// <param name="size">決まった大きさ。</param>
    public static bool TryResolve(UiThemeData theme, string? spec, out float size)
    {
        size = 0f;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        string s = spec.Trim();
        // テーマのトークン（SEED のトークン・アプリ独自の app.*）
        if (theme.TryNumber(s, out var token))
        {
            size = token;
            return float.IsFinite(size) && size > 0f;
        }
        // 数そのもの（インバリアントの書式。"18"・"17.5"）
        if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number) && number > 0f)
        {
            size = number;
            return true;
        }
        return false;
    }

    /// <summary>大きさを決める（決まらなければ <paramref name="fallback"/>）。</summary>
    /// <param name="theme">テーマ。</param>
    /// <param name="spec">指定（トークンの名前か数）。</param>
    /// <param name="fallback">決まらないときの大きさ（今の大きさ）。</param>
    public static float Resolve(UiThemeData theme, string? spec, float fallback)
        => TryResolve(theme, spec, out var size) ? size : fallback;
}
