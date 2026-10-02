using System;
using System.Text.Json;

namespace SEED.Localization;

// ============================================================
//  LocaleJson.cs — 多言語のデータファイル（index.json・<言語>.json）の JSON の共通の約束（純粋な計算）
//
//  【約束】（docs/localization.md §2）
//    - 手で書くファイルなので、コメント（// と /* */）と末尾のカンマを許す（SEED.UI のテーマの JSON と同じ）
//    - 先頭が "_" の鍵は説明（"_about" など）で、文字列としても設定としても読まない
//      （Wake or Pay の strings.ja.json の "_source" と同じ約束。移すときに書き換えが要らない）
//    - 入れ子のオブジェクトの鍵は "." でつないで 1 本のキーにする
//  index.json と言語の表の両方の読み込み（LocaleIndex・LocaleTable）がこの 1 か所の約束を使う。
// ============================================================

/// <summary>多言語のデータファイルの JSON の共通の約束。</summary>
public static class LocaleJson
{
    /// <summary>説明の鍵（読まない鍵）の接頭辞。</summary>
    public const string CommentPrefix = "_";

    /// <summary>入れ子の鍵をつなぐ区切り（"menu" と "start" → "menu.start"）。</summary>
    public const char KeySeparator = '.';

    /// <summary>JSON の読み方（手で書くファイルなのでコメントと末尾のカンマを許す）。</summary>
    public static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>説明の鍵か（先頭が "_"）。</summary>
    /// <param name="key">JSON のオブジェクトの鍵。</param>
    /// <returns>説明の鍵なら true（読まない）。</returns>
    public static bool IsComment(string key) => key.StartsWith(CommentPrefix, StringComparison.Ordinal);

    /// <summary>親の鍵と子の鍵をつなぐ（親が空なら子のまま）。</summary>
    /// <param name="prefix">親の鍵（根は空）。</param>
    /// <param name="key">子の鍵。</param>
    /// <returns>つないだ鍵。</returns>
    public static string Join(string prefix, string key) =>
        prefix.Length == 0 ? key : prefix + KeySeparator + key;
}
