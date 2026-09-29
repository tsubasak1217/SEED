using System.Text;

namespace SEED.UI;

// ============================================================
//  TextMarkupEscape.cs — 利用者の打った文字を Text.Content へそのまま描かせるための記法の逃がし（W2-6b。純粋な計算）
//
//  SEED の Text は本文の記法（[icon:名前]・[img:パス]・{スロット}）を解いて描く（runtime の font/inline/markup.rs・slot_markup.rs）。
//  入力欄の本文は利用者の打った文字そのものなので、記法の入口の「[」と「{」の前にバックスラッシュを置いて逃がす
//  （バックスラッシュは直後が [ か { のときだけ意味を持つので、ほかの文字の前のバックスラッシュはそのまま描かれる）。
//  逃がした本文を描いた見た目は元の文字の並びと同じなので、カーソルの位置は元の文字で測る（SEED.TextMeasure）。
// ============================================================

/// <summary>本文の記法を逃がす。</summary>
public static class TextMarkupEscape
{
    /// <summary>逃がしの文字（バックスラッシュ）。</summary>
    private const char EscapeChar = '\\';
    /// <summary>画像・アイコンの記法の入口。</summary>
    private const char ImageOpen = '[';
    /// <summary>スロットの記法の入口。</summary>
    private const char SlotOpen = '{';

    /// <summary>記法の入口を逃がした本文（入口が無ければそのまま返す）。</summary>
    public static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOfAny(new[] { ImageOpen, SlotOpen }) < 0) return text ?? string.Empty;
        var builder = new StringBuilder(text.Length + text.Length / 4);
        foreach (char ch in text)
        {
            if (ch == ImageOpen || ch == SlotOpen) builder.Append(EscapeChar);
            builder.Append(ch);
        }
        return builder.ToString();
    }
}
