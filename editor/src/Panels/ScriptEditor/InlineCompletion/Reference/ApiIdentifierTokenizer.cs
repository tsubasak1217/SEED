// ============================================================
//  ApiIdentifierTokenizer.cs — 識別子（英数字の語）の切り出しと比べ方（文書側・編集中のファイル側で共用）
//
//  【役割】
//  リファレンスの節の「語」と、編集中のファイルの「語」を同じ規則で切り出す。
//  規則が両側で食い違うと一致しなくなるので、切り出し・語の比べ方・捨てる語を 1 か所に置く。
//
//  【規則】
//  - 語 = ASCII の英字か _ で始まり、英数字と _ が続く並び（C# の識別子の ASCII 部分）。
//    "L10n.Get" は "L10n" と "Get" の 2 語。"Vector3" は 1 語。
//  - 比べるときは大文字小文字を区別しない（変数 screenStack も型 ScreenStack の手がかりにする）。
//  - 捨てる語: 1 文字の語（x・y・i など）と C# のキーワード（public・float・var など。
//    キーワードは小文字の綴りだけを捨てる＝Get や Set のような API 名は残す）。
//
//  【WPF 非依存】editor/tests/InlineCompletionTests がこのフォルダを丸ごとリンクする。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

/// <summary>
/// 識別子の切り出し・比べ方・捨てる語の規則。
/// </summary>
public static class ApiIdentifierTokenizer
{
    /// <summary>語として数える最短の文字数（"UI" は残し、x・y・i などの 1 文字は捨てる）。</summary>
    public const int MinWordLength = 2;

    /// <summary>識別子の並び（ASCII の英字か _ で始まる）。正規表現の部品として他のクラスも使う。</summary>
    public const string IdentifierPatternText = "[A-Za-z_][A-Za-z0-9_]*";

    /// <summary>語どうしの比べ方（大文字小文字を区別しない）。辞書・集合・整列はすべてこれを使う。</summary>
    public static readonly StringComparer WordComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>識別子を 1 回の走査で拾う正規表現。</summary>
    private static readonly Regex IdentifierPattern =
        new(IdentifierPatternText, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// C# のキーワード（予約語と文脈キーワード）。どのファイル・どの節にも現れて手がかりにならない。
    /// 綴りどおり（小文字）だけを捨てる（Ordinal）。"Get" や "Value" のような API 名は残す。
    /// </summary>
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        // 予約語
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
        // 文脈キーワード（よく出るもの）
        "add", "and", "async", "await", "dynamic", "get", "global", "init", "nameof", "not",
        "or", "partial", "record", "remove", "required", "set", "value", "var", "when", "where",
        "with", "yield",
    };

    /// <summary>C# のキーワードか（綴りどおりに比べる）。</summary>
    /// <param name="word">語。</param>
    /// <returns>キーワードなら true。</returns>
    public static bool IsKeyword(string word) => CSharpKeywords.Contains(word);

    /// <summary>語として数えるか（短すぎず、キーワードでない）。</summary>
    /// <param name="word">語。</param>
    /// <returns>数えるなら true。</returns>
    public static bool IsCandidate(string word) => word.Length >= MinWordLength && !IsKeyword(word);

    /// <summary>識別子の 1 文字目になれる文字か。</summary>
    /// <param name="c">文字。</param>
    /// <returns>なれるなら true。</returns>
    public static bool IsIdentifierStart(char c) => c == '_' || (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z');

    /// <summary>識別子の 2 文字目以降になれる文字か。</summary>
    /// <param name="c">文字。</param>
    /// <returns>なれるなら true。</returns>
    public static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || (c is >= '0' and <= '9');

    /// <summary>
    /// テキストから語を切り出して集合へ足す（捨てる語は足さない）。
    /// </summary>
    /// <param name="text">対象のテキスト（見出し 1 行・本文など）。</param>
    /// <param name="into">足し込む先の集合（<see cref="WordComparer"/> で作ったもの）。</param>
    public static void AddWords(string text, ISet<string> into)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (Match match in IdentifierPattern.Matches(text))
        {
            if (IsCandidate(match.Value)) into.Add(match.Value);
        }
    }

    /// <summary>テキストの語の集合を作る。</summary>
    /// <param name="text">対象のテキスト。</param>
    /// <returns>語の集合（大文字小文字を区別しない）。</returns>
    public static HashSet<string> WordsOf(string text)
    {
        var set = new HashSet<string>(WordComparer);
        AddWords(text, set);
        return set;
    }
}
