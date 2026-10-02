using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SEED.Localization;

// ============================================================
//  LocaleFormatter.cs — 文の差し込み（{name}・{0}・{name:書式}・{{ と }}。純粋な計算）
//
//  【規則】（docs/localization.md §4）
//    {name}         … 渡した名前の値（大文字小文字を区別する）
//    {0}            … 渡した順の 0 番目の値（名前つきで渡した値も順で引ける）
//    {name:N0}      … 値が IFormattable（数・日付）なら「:」の後ろを .NET の書式として今の言語の文化で書く（読めない書式は書式なし）
//    {{ と }}       … 波かっこそのもの（Wake or Pay の StringTable と同じ書き方）
//    それ以外の {…} … そのまま残す。Text の差し込みスロットの記法（{num}・{num.3}・{string}・{image}・{color}〜{/color}）を
//                    壊さないため。名前の無い {}・渡していない名前・閉じていない { も残す
//  同じ名前が 2 つあれば先のものを使う（L10n.Plural は利用者の値を先に、数 n を後ろに並べる＝利用者の n が勝つ）。
//  null の値は空文字。
// ============================================================

/// <summary>文の差し込み。</summary>
public static class LocaleFormatter
{
    /// <summary>差し込みの始まり。</summary>
    private const char Open = '{';
    /// <summary>差し込みの終わり。</summary>
    private const char Close = '}';
    /// <summary>名前と書式の区切り。</summary>
    private const char FormatSeparator = ':';
    /// <summary>波かっこの二重（{{ / }}）の長さ。</summary>
    private const int EscapedBraceLength = 2;
    /// <summary>組み立てるときに文の長さへ足しておく余裕（差し込みで伸びる分）。</summary>
    private const int ExtraCapacity = 16;

    /// <summary>波かっこ（これが無い文は組み立てずにそのまま返す）。</summary>
    private static readonly char[] Braces = { Open, Close };

    /// <summary>名前つきの値を差し込む。</summary>
    /// <param name="template">文（差し込み前）。</param>
    /// <param name="args">名前と値の組（無ければ空）。</param>
    /// <param name="provider">数・日付の書式の文化（null なら今のスレッドの文化）。</param>
    /// <returns>差し込んだ文。</returns>
    public static string Format(string? template, ReadOnlySpan<(string Name, object? Value)> args, IFormatProvider? provider)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        if (template.IndexOfAny(Braces) < 0) return template;

        var sb = new StringBuilder(template.Length + ExtraCapacity);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];

            // ── 波かっこの二重は波かっこそのもの ──
            if ((c == Open || c == Close) && i + 1 < template.Length && template[i + 1] == c)
            {
                sb.Append(c);
                i += EscapedBraceLength;
                continue;
            }

            // ── {…} を探して置き換える（中に { を含むもの・閉じていないものは差し込みとみなさない）──
            if (c == Open)
            {
                int close = template.IndexOf(Close, i + 1);
                int nextOpen = template.IndexOf(Open, i + 1);
                if (close > i && (nextOpen < 0 || nextOpen > close))
                {
                    string body = template.Substring(i + 1, close - i - 1);
                    if (TryResolve(body, args, provider, out var replacement)) sb.Append(replacement);
                    else sb.Append(template, i, close - i + 1);   // 知らない差し込みはそのまま残す
                    i = close + 1;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 文が使う差し込みの名前（{name}・{name:書式}・{0} の name / 0。二重の波かっこは数えない）。
    /// 言語の表どうしで差し込みがそろっているかを確かめる道具（テスト・翻訳の確認）に使う。
    /// </summary>
    /// <param name="template">文。</param>
    /// <returns>名前（現れた順・重なりなし）。</returns>
    public static IReadOnlyList<string> Placeholders(string? template)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(template)) return names;
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if ((c == Open || c == Close) && i + 1 < template.Length && template[i + 1] == c)
            {
                i += EscapedBraceLength;
                continue;
            }
            if (c == Open)
            {
                int close = template.IndexOf(Close, i + 1);
                int nextOpen = template.IndexOf(Open, i + 1);
                if (close > i && (nextOpen < 0 || nextOpen > close))
                {
                    string name = NameOf(template.Substring(i + 1, close - i - 1));
                    if (name.Length > 0 && !names.Contains(name)) names.Add(name);
                    i = close + 1;
                    continue;
                }
            }
            i++;
        }
        return names;
    }

    /// <summary>差し込みの中身（name か name:書式）を値の文字へ置き換える。</summary>
    private static bool TryResolve(string body, ReadOnlySpan<(string Name, object? Value)> args, IFormatProvider? provider, out string replacement)
    {
        replacement = string.Empty;
        string name = NameOf(body);
        if (name.Length == 0) return false;
        if (!TryFindValue(name, args, out var value)) return false;
        int separator = body.IndexOf(FormatSeparator);
        string? format = separator < 0 ? null : body.Substring(separator + 1);
        replacement = Render(value, format, provider);
        return true;
    }

    /// <summary>差し込みの中身の名前の部分（「:」より前）。</summary>
    private static string NameOf(string body)
    {
        int separator = body.IndexOf(FormatSeparator);
        return separator < 0 ? body : body.Substring(0, separator);
    }

    /// <summary>名前（先に名前で、無ければ順の番号で）から値を探す。</summary>
    private static bool TryFindValue(string name, ReadOnlySpan<(string Name, object? Value)> args, out object? value)
    {
        foreach (var (argName, argValue) in args)
        {
            if (string.Equals(argName, name, StringComparison.Ordinal))
            {
                value = argValue;
                return true;
            }
        }
        if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < args.Length)
        {
            value = args[index].Value;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>
    /// 値を文字にする（IFormattable は書式と文化で。読めない書式は書式なしに戻す。文化の暦で表せない日付〈グレゴリオ暦でない文化の
    /// 範囲の外〉は不変文化で書く。2026-10-03。2 回目のレビュー #33。以前は ArgumentOutOfRangeException が呼び手へ飛んだ）。
    /// </summary>
    private static string Render(object? value, string? format, IFormatProvider? provider)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case string text:
                return text;
            case IFormattable formattable:
                string? effective = string.IsNullOrEmpty(format) ? null : format;
                try
                {
                    return formattable.ToString(effective, provider);
                }
                catch (FormatException)
                {
                    return RenderPlain(formattable, provider);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return RenderInvariant(formattable, effective);
                }
            default:
                return value.ToString() ?? string.Empty;
        }
    }

    /// <summary>書式なしで書く（文化の暦で表せない日付なら不変文化で）。</summary>
    private static string RenderPlain(IFormattable formattable, IFormatProvider? provider)
    {
        try
        {
            return formattable.ToString(null, provider);
        }
        catch (ArgumentOutOfRangeException)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>不変文化（グレゴリオ暦）で書く（読めない書式は書式なし）。</summary>
    private static string RenderInvariant(IFormattable formattable, string? format)
    {
        try
        {
            return formattable.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        }
    }
}
