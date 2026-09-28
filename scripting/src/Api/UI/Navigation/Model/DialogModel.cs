using System;
using System.Collections.Generic;
using System.Globalization;

namespace SEED.UI;

// ============================================================
//  DialogModel.cs — ダイアログの中身・ボタン・結果の決め方（W2-7。純粋な計算）
//
//  - ボタンは 1〜3 つ: 中立（左端）・いいえ（Negative）・はい（Positive。右端）の順（Material 3 の並び。文字が空のボタンは出さない。
//    どれも空なら「OK」の Positive を 1 つ）
//  - 幕（背景を暗くする面）のタップ: DismissOnScrimTap なら Dismissed で閉じる、そうでなければ何もしない
//  - 戻る: CancelableByBack なら Dismissed で閉じる。そうでなくても戻るは受ける（後ろの画面へ回さない。確認を必ず答えさせる）
//  - 結果は 1 回だけ（DialogResultLatch。ボタンの連打・閉じる動きの途中の戻るで 2 度出さない）
//  - 本文の高さ: 文字の寸法を測る API（Text.Measure）は W2-6c なので、文字の数から行の数を見積もる（DialogLayout）
// ============================================================

/// <summary>ダイアログの結果。</summary>
public enum DialogResult
{
    /// <summary>はい・OK（右端のボタン）。</summary>
    Positive = 0,
    /// <summary>いいえ・やめる。</summary>
    Negative = 1,
    /// <summary>中立（左端のボタン）。</summary>
    Neutral = 2,
    /// <summary>ボタンを押さずに閉じた（幕のタップ・戻る・スクリプトからの Dismiss）。</summary>
    Dismissed = 3,
}

/// <summary>ダイアログの中身と振る舞い。</summary>
public sealed class DialogOptions
{
    /// <summary>既定の Positive の文字（どのボタンも指定しなかったとき）。</summary>
    public const string DefaultPositiveText = "OK";

    /// <summary>題（空なら出さない）。</summary>
    public string Title { get; init; } = string.Empty;
    /// <summary>本文。</summary>
    public string Message { get; init; } = string.Empty;
    /// <summary>はいのボタンの文字（空なら出さない）。</summary>
    public string PositiveText { get; init; } = string.Empty;
    /// <summary>いいえのボタンの文字（空なら出さない）。</summary>
    public string NegativeText { get; init; } = string.Empty;
    /// <summary>中立のボタンの文字（空なら出さない）。</summary>
    public string NeutralText { get; init; } = string.Empty;
    /// <summary>幕のタップで閉じるか（Dismissed）。</summary>
    public bool DismissOnScrimTap { get; init; } = true;
    /// <summary>戻るで閉じるか（Dismissed）。false でも戻るは受ける（後ろへ回さない）。</summary>
    public bool CancelableByBack { get; init; } = true;
}

/// <summary>ダイアログの決め方。</summary>
public static class DialogModel
{
    /// <summary>
    /// 出すボタンの結果（左から右の順）。どれも空なら Positive だけ。
    /// </summary>
    public static IReadOnlyList<DialogResult> Buttons(DialogOptions options)
    {
        var list = new List<DialogResult>();
        if (options.NeutralText.Length > 0) list.Add(DialogResult.Neutral);
        if (options.NegativeText.Length > 0) list.Add(DialogResult.Negative);
        if (options.PositiveText.Length > 0 || list.Count == 0) list.Add(DialogResult.Positive);
        return list;
    }

    /// <summary>ボタンの文字（Positive が空で出すときは既定の「OK」）。</summary>
    public static string ButtonText(DialogOptions options, DialogResult button) => button switch
    {
        DialogResult.Neutral => options.NeutralText,
        DialogResult.Negative => options.NegativeText,
        DialogResult.Positive => options.PositiveText.Length > 0 ? options.PositiveText : DialogOptions.DefaultPositiveText,
        _ => string.Empty,
    };

    /// <summary>幕のタップの結果（閉じないなら null）。</summary>
    public static DialogResult? OnScrimTap(DialogOptions options) => options.DismissOnScrimTap ? DialogResult.Dismissed : null;

    /// <summary>
    /// 戻るの結果。戻るは常に受ける（Consumed = true）。閉じるなら Result に Dismissed。
    /// </summary>
    public static (bool Consumed, DialogResult? Result) OnBack(DialogOptions options)
        => (true, options.CancelableByBack ? DialogResult.Dismissed : null);
}

/// <summary>結果を 1 回だけ受ける留め金。</summary>
public sealed class DialogResultLatch
{
    /// <summary>決まった結果（まだなら null）。</summary>
    public DialogResult? Result { get; private set; }

    /// <summary>決まったか。</summary>
    public bool IsCompleted => Result.HasValue;

    /// <summary>結果を決める（最初の 1 回だけ true）。</summary>
    public bool TryComplete(DialogResult result)
    {
        if (Result.HasValue) return false;
        Result = result;
        return true;
    }
}

/// <summary>ダイアログの文字の大きさの見積もり（Text.Measure〈W2-6c〉までの仮）。</summary>
public static class DialogLayout
{
    /// <summary>全角の文字の幅（文字の大きさに対する割合）。</summary>
    private const float WideCharEm = 1.0f;
    /// <summary>半角の文字の幅（文字の大きさに対する割合。M PLUS Rounded 1c の英数字の平均に近い値）。</summary>
    private const float NarrowCharEm = 0.55f;
    /// <summary>行の高さ（文字の大きさに対する割合。SEED の Text の既定の行間 1.0 に上下の余白を足した見積もり）。</summary>
    public const float LineHeightEm = 1.4f;
    /// <summary>半角とみなす符号位置の上限（ASCII と半角カナの手前まで）。</summary>
    private const int NarrowCodePointMax = 0x7F;
    /// <summary>半角カナの範囲（U+FF61〜U+FF9F）。</summary>
    private const int HalfWidthKanaFirst = 0xFF61;
    /// <summary>半角カナの範囲の終わり。</summary>
    private const int HalfWidthKanaLast = 0xFF9F;

    /// <summary>
    /// 本文の行の数の見積もり（改行と、枠の幅での折り返し。1 文字単位で折る＝SEED の日本語の折り返しと同じ考え方）。
    /// </summary>
    /// <param name="text">本文。</param>
    /// <param name="fontSize">文字の大きさ。</param>
    /// <param name="boxWidth">枠の幅（文字の大きさと同じ単位）。</param>
    public static int EstimateLines(string text, float fontSize, float boxWidth)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (!(fontSize > 0f) || !(boxWidth > 0f)) return 1;
        int lines = 1;
        float x = 0f;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            string element = (string)e.Current;
            if (element == "\n" || element == "\r\n")
            {
                lines++;
                x = 0f;
                continue;
            }
            if (element == "\r") continue;
            float w = CharWidthEm(element) * fontSize;
            if (x > 0f && x + w > boxWidth)
            {
                lines++;
                x = 0f;
            }
            x += w;
        }
        return lines;
    }

    /// <summary>本文の高さの見積もり（行の数 × 行の高さ）。</summary>
    public static float EstimateHeight(string text, float fontSize, float boxWidth)
        => EstimateLines(text, fontSize, boxWidth) * fontSize * LineHeightEm;

    /// <summary>1 行の文字の幅の見積もり（ボタンの幅を文字に合わせる）。</summary>
    public static float EstimateWidth(string text, float fontSize)
    {
        if (string.IsNullOrEmpty(text) || !(fontSize > 0f)) return 0f;
        float width = 0f;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) width += CharWidthEm((string)e.Current) * fontSize;
        return width;
    }

    /// <summary>1 文字（書記素）の幅（文字の大きさに対する割合）。</summary>
    private static float CharWidthEm(string element)
    {
        // 壊れたサロゲートは全角とみなす（例外にしない）
        if (!System.Text.Rune.TryGetRuneAt(element, 0, out var rune)) return WideCharEm;
        int cp = rune.Value;
        bool narrow = cp <= NarrowCodePointMax || (cp >= HalfWidthKanaFirst && cp <= HalfWidthKanaLast);
        return narrow ? NarrowCharEm : WideCharEm;
    }
}
