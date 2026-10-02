// ============================================================
//  LocaleCodeRules.cs — 言語のコードの書き方の決まり（純粋な計算）
//
//  【決まり】（docs/localization.md §2.1・§15）
//    - 書きそろえは LocaleIndex.Normalize と同じ（前後の空白を落とし "_" を "-" に。大文字小文字はそのまま）
//    - 英数字を "-" でつないだもの（BCP 47 の形: "ja"・"en-US"・"zh-Hant"）。表のファイル名 <code>.json になるので
//      ファイル名に使えない文字・"." は使えない
//    - "index" は使えない（表のファイル名が言語の一覧 index.json とぶつかる）
// ============================================================

using System;
using SEED.Localization;

namespace SEEDEditor.Localization.Model;

/// <summary>言語のコードの書き方の決まり。</summary>
public static class LocaleCodeRules
{
    /// <summary>コードの部分の区切り（BCP 47 の "-"）。</summary>
    private const char SubtagSeparator = '-';

    /// <summary>言語のコードにできない名前（表のファイル名が index.json とぶつかる）。</summary>
    private static readonly string ReservedCode =
        LocalePaths.IndexFileName.Substring(0, LocalePaths.IndexFileName.Length - LocalePaths.TableExtension.Length);

    /// <summary>
    /// 言語のコードを確かめる（書きそろえたものを返す）。
    /// </summary>
    /// <param name="code">利用者が書いたコード。</param>
    /// <param name="normalized">書きそろえたコード（だめなときも入れる）。</param>
    /// <param name="reason">使えない理由（使えるなら空）。</param>
    /// <returns>使えるなら true。</returns>
    public static bool TryValidate(string? code, out string normalized, out string reason)
    {
        normalized = LocaleIndex.Normalize(code ?? string.Empty);
        if (normalized.Length == 0)
        {
            reason = "言語のコードを入力してください（ja・en・pt-BR など）";
            return false;
        }

        foreach (var part in normalized.Split(SubtagSeparator))
        {
            if (part.Length == 0 || !IsAsciiLettersOrDigits(part))
            {
                reason = "言語のコードは英数字を「-」でつないだ形で書きます（ja・en-US・zh-Hant など）";
                return false;
            }
        }

        if (string.Equals(normalized, ReservedCode, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"「{ReservedCode}」は言語の一覧（{LocalePaths.IndexFileName}）とぶつかるので使えません";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>ASCII の英数字だけか。</summary>
    private static bool IsAsciiLettersOrDigits(string text)
    {
        foreach (char c in text)
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        return true;
    }
}
