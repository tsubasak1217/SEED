using System;
using System.Globalization;

namespace SEED.UI;

// ============================================================
//  NumberText.cs — 数字の欄の文字 ↔ 値（W2-6b。純粋な計算。editor/tests/UiComponentsTests で検算）
//
//  Wake or Pay の数値の欄（猶予・スヌーズ・ペナルティ・上限金額。アプリ仕様 §3.3「数値でない入力は無視・範囲外はクランプ」）の決まり:
//    - 数字として読めない（空・桁あふれ）文字は値にしない（前の値のまま）
//    - 読めた値は範囲へ収める（範囲の外は端の値）
//  欄の文字そのものは打っている間は変えない（打ち途中の「1」を最小値 10 へ書き換えない）。フォーカスが外れた・完了したときに
//  収めた値の文字へ直す（TextField の NormalizeOnCommit）。スライダとの双方向は「欄にフォーカスが無いときだけ外の値で上書き」
//  （TextField.SetTextUnlessFocused）。
// ============================================================

/// <summary>数字の欄の文字 ↔ 値。</summary>
public static class NumberText
{
    /// <summary>
    /// 文字を整数として読み、範囲へ収める（読めなければ false）。
    /// </summary>
    /// <param name="text">欄の文字（数字だけの欄なので 0〜9 の並び。前後の空白は無視）。</param>
    /// <param name="min">最小値。</param>
    /// <param name="max">最大値（min より小さければ入れ替える）。</param>
    /// <param name="value">収めた値。</param>
    public static bool TryParseClamped(string? text, long min, long max, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
        {
            // 桁あふれ（数字だけが長く並んだ）は最大へ収める。数字でない文字があれば読めない
            if (!IsAllDigits(text.Trim())) return false;
            parsed = long.MaxValue;
        }
        long low = Math.Min(min, max);
        long high = Math.Max(min, max);
        value = Math.Clamp(parsed, low, high);
        return true;
    }

    /// <summary>値の文字（欄へ書き戻す形。桁区切りなし）。</summary>
    public static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>すべて 0〜9 か（空は false）。</summary>
    private static bool IsAllDigits(string text)
    {
        if (text.Length == 0) return false;
        foreach (char ch in text)
            if (ch is < '0' or > '9') return false;
        return true;
    }
}
