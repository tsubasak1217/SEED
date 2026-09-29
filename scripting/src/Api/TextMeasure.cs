using System;

namespace SEED;

// ============================================================
//  TextMeasure.cs — 1 行の文字の寸法（W2-6b。入力欄のカーソルの位置。W2-6c の Text.Measure の芽。docs/ui_text_input.md §7.3）
//
//  描画と同じ送り幅（書体の送り幅 × 大きさ。カーニングなし）で測る。記法（[icon:…]・{…}）は解かずに文字そのものを測る。
//  書体は Text.FontPath と同じ assets:// のパス（空 = 組み込みの書体）、大きさは Text.FontSize と同じキャンバスの単位。
//  複数行・折り返し・枠の大きさは W2-6c の Text.Measure で足す。
// ============================================================

/// <summary>1 行の文字の寸法（描画と同じ送り幅）。</summary>
public static class TextMeasure
{
    /// <summary>1 行の幅（キャンバスの単位。測れなければ 0）。</summary>
    /// <param name="text">本文（改行は 1 文字として送り幅を足すだけ。1 行の本文に使う）。</param>
    /// <param name="fontPath">書体（Text.FontPath と同じ。空 = 組み込み）。</param>
    /// <param name="fontSize">大きさ（Text.FontSize と同じ）。</param>
    public static float LineWidth(string text, string fontPath, float fontSize)
    {
        Span<float> output = stackalloc float[1];
        return ScriptHost.TextMeasureCall(ScriptHost.TextMeasureOpLineWidth, fontPath, fontSize, text, output) > 0 ? output[0] : 0f;
    }

    /// <summary>
    /// カーソルの位置の並び（長さ = text.Length + 1。i 番目 = 添字 i の文字の前の端の x。先頭は 0）。
    /// サロゲートの組の間の添字は文字の頭と同じ x。測れなければ全部 0。
    /// </summary>
    public static float[] CaretOffsets(string text, string fontPath, float fontSize)
    {
        text ??= string.Empty;
        var stops = new float[text.Length + 1];
        CaretOffsets(text, fontPath, fontSize, stops);
        return stops;
    }

    /// <summary>カーソルの位置の並びを <paramref name="stops"/>（長さ text.Length + 1 以上）へ書く。書けたら true。</summary>
    public static bool CaretOffsets(string text, string fontPath, float fontSize, Span<float> stops)
    {
        int needed = ScriptHost.TextMeasureCall(ScriptHost.TextMeasureOpCaretStops, fontPath, fontSize, text, stops);
        return needed > 0 && needed <= stops.Length;
    }

    /// <summary>書体の縦の寸法（アセント・ディセント。どちらも正。大きさと同じ単位）。測れなければ (0, 0)。</summary>
    public static (float Ascent, float Descent) Metrics(string fontPath, float fontSize)
    {
        Span<float> output = stackalloc float[2];
        return ScriptHost.TextMeasureCall(ScriptHost.TextMeasureOpMetrics, fontPath, fontSize, string.Empty, output) == 2
            ? (output[0], output[1])
            : (0f, 0f);
    }
}
