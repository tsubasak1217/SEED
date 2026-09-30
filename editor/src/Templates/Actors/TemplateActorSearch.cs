// ============================================================
//  TemplateActorSearch.cs — テンプレートアクタの検索（絞り込み）
//
//  【役割】
//  検索欄の文字とカテゴリの選択から、一覧に出すテンプレートアクタを決める純粋な処理。
//  画面（TemplateActorPickerWindow）は入力のたびにここを呼ぶだけ。WPF に依存しないので
//  単体テスト（editor/tests/TemplateImportTests）で検算できる。
//
//  【一致の規則】
//  - 部分一致。対象は 表示名・説明・検索語（tags）・テンプレートのパス・カテゴリ名。
//  - 空白（半角・全角）で区切った語は**すべて**含むものだけを残す（AND）。
//  - 大文字と小文字、全角と半角（ＡＢＣ／ABC、ｶﾀｶﾅ／カタカナ）を区別しない
//    （Unicode の互換正規化 NFKC → 小文字化）。
//  - カタカナとひらがなも区別しない（「ぼたん」で「ボタン」が引ける）。
//  - カテゴリを選んでいるときは、そのカテゴリ（1 段目を選んだらその下の 2 段目も含む）の中だけ。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// テンプレートアクタの絞り込み。状態を持たない静的ユーティリティ。
/// </summary>
public static class TemplateActorSearch
{
    /// <summary>カタカナ（ァ〜ヶ）の先頭の符号位置。</summary>
    private const char KatakanaFirst = 'ァ';

    /// <summary>カタカナ（ァ〜ヶ）の末尾の符号位置。ひらがなに対応する字がある範囲の終わり。</summary>
    private const char KatakanaLast = 'ヶ';

    /// <summary>カタカナからひらがなへの符号位置の差（ア U+30A2 → あ U+3042）。</summary>
    private const int KatakanaToHiraganaOffset = 0x60;

    /// <summary>検索語の区切り（NFKC で全角空白は半角空白になるので、半角の空白類だけでよい）。</summary>
    private static readonly char[] TermSeparators = [' ', '\t', '\r', '\n'];

    /// <summary>
    /// 比較用に文字列を正規化する（NFKC → 小文字 → カタカナをひらがなへ）。
    /// </summary>
    /// <param name="text">元の文字列。</param>
    /// <returns>正規化した文字列（null は空文字）。</returns>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var folded = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();

        // カタカナ → ひらがな（長音「ー」や記号は範囲外なのでそのまま）
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
            sb.Append(c is >= KatakanaFirst and <= KatakanaLast ? (char)(c - KatakanaToHiraganaOffset) : c);
        return sb.ToString();
    }

    /// <summary>
    /// 検索欄の文字を、正規化した語の並びに分ける。
    /// </summary>
    /// <param name="query">検索欄の文字。</param>
    /// <returns>正規化済みの語（空なら 0 件＝絞り込みなし）。</returns>
    public static IReadOnlyList<string> SplitTerms(string? query) =>
        Normalize(query).Split(TermSeparators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// エントリの検索対象の文字（表示名・説明・検索語・パス・カテゴリ）を正規化して 1 本にする。
    /// </summary>
    /// <param name="entry">対象のエントリ。</param>
    /// <returns>正規化済みの検索対象（語の間は改行で区切り、語をまたいだ誤一致を防ぐ）。</returns>
    public static string BuildHaystack(TemplateActorEntry entry)
    {
        var parts = new List<string>
        {
            entry.Name,
            entry.Description,
            entry.TemplateRelPath,
            entry.CategoryDisplay,
        };
        parts.AddRange(entry.Tags);
        return Normalize(string.Join('\n', parts));
    }

    /// <summary>
    /// エントリが検索語をすべて含むかを判定する。
    /// </summary>
    /// <param name="haystack"><see cref="BuildHaystack"/> の結果。</param>
    /// <param name="terms"><see cref="SplitTerms"/> の結果。0 件なら常に true。</param>
    /// <returns>すべての語を含めば true。</returns>
    public static bool Matches(string haystack, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
            if (!haystack.Contains(term, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// エントリが選択中のカテゴリに入るかを判定する。
    /// </summary>
    /// <param name="entry">対象のエントリ。</param>
    /// <param name="categoryKey">選択中のカテゴリのキー。null（「すべて」）なら常に true。</param>
    /// <returns>カテゴリそのもの、またはその下の段に入っていれば true。</returns>
    public static bool InCategory(TemplateActorEntry entry, string? categoryKey)
    {
        if (string.IsNullOrEmpty(categoryKey)) return true;
        if (string.Equals(entry.CategoryKey, categoryKey, StringComparison.Ordinal)) return true;
        // 1 段目を選んでいるときは、その下の 2 段目のエントリも含める
        return entry.CategoryKey.StartsWith(
            categoryKey + TemplateActorCatalogFormat.CategorySeparator, StringComparison.Ordinal);
    }

    /// <summary>
    /// 検索欄の文字とカテゴリの選択でエントリを絞り込む（並びは元のまま）。
    /// </summary>
    /// <param name="entries">カタログのエントリ。</param>
    /// <param name="query">検索欄の文字。</param>
    /// <param name="categoryKey">選択中のカテゴリ（null なら「すべて」）。</param>
    /// <returns>一致したエントリ。</returns>
    public static List<TemplateActorEntry> Filter(
        IEnumerable<TemplateActorEntry> entries, string? query, string? categoryKey)
    {
        var terms = SplitTerms(query);
        return entries
            .Where(e => InCategory(e, categoryKey) && Matches(BuildHaystack(e), terms))
            .ToList();
    }
}
