// ============================================================
//  LocaleKeyRules.cs — 文字列表のキーの書き方の決まり（純粋な計算）
//
//  【決まり】（docs/localization.md §2.2・§15）
//    - 前後の空白は落とす。中の空白・制御文字は使えない（キーは名前。"menu.start" のように書く）
//    - "." で区切った鍵はどれも空にできない（"a..b"・".a"・"a." は空の鍵になり、実行中に読まれない）
//    - "_" で始まる鍵は説明（_about）として読み飛ばされるので使えない
//  区切り（"."）と説明の印（"_"）は SEED.Localization の LocaleJson の定数をそのまま使う（二重に持たない）。
// ============================================================

using System;
using SEED.Localization;

namespace SEEDEditor.Localization.Model;

/// <summary>文字列表のキーの書き方の決まり。</summary>
public static class LocaleKeyRules
{
    /// <summary>
    /// キーを確かめる（前後の空白を落としたものを返す）。
    /// </summary>
    /// <param name="key">利用者が書いたキー。</param>
    /// <param name="normalized">前後の空白を落としたキー（だめなときも入れる）。</param>
    /// <param name="reason">使えない理由（使えるなら空）。</param>
    /// <returns>使えるなら true。</returns>
    public static bool TryValidate(string? key, out string normalized, out string reason)
    {
        normalized = (key ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            reason = "キーを入力してください";
            return false;
        }

        foreach (char c in normalized)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                reason = "キーに空白・制御文字は使えません";
                return false;
            }
        }

        foreach (var segment in LocaleJsonWriter.SplitKey(normalized))
        {
            if (segment.Length == 0)
            {
                reason = $"「{LocaleJson.KeySeparator}」の前後には名前が要ります（空の鍵は読まれません）";
                return false;
            }
            if (LocaleJson.IsComment(segment))
            {
                reason = $"「{LocaleJson.CommentPrefix}」で始まる鍵は説明として読み飛ばされるので、キーには使えません";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// キーを「まとまりの名前 + 複数形の形」に分ける（"coins.one" → "coins" と One）。
    /// 最後の鍵が複数形の形の名前（zero〜other）でなければ false。
    /// </summary>
    /// <param name="key">平たいキー。</param>
    /// <param name="group">まとまりの名前。</param>
    /// <param name="category">形。</param>
    /// <returns>分けられたら true。</returns>
    public static bool TrySplitPluralForm(string key, out string group, out PluralCategory category)
    {
        group = string.Empty;
        category = PluralCategory.Other;
        int separator = key.LastIndexOf(LocaleJson.KeySeparator);
        if (separator <= 0 || separator == key.Length - 1) return false;

        string tail = key.Substring(separator + 1);
        if (!TryParseCategory(tail, out category)) return false;
        group = key.Substring(0, separator);
        return true;
    }

    /// <summary>複数形の形の名前（"one" など）を種類にする（大文字小文字を区別する。PluralCategoryNames と同じ）。</summary>
    /// <param name="name">形の名前。</param>
    /// <param name="category">種類。</param>
    /// <returns>形の名前なら true。</returns>
    public static bool TryParseCategory(string name, out PluralCategory category)
    {
        foreach (var candidate in Enum.GetValues<PluralCategory>())
        {
            if (string.Equals(PluralCategoryNames.ToKey(candidate), name, StringComparison.Ordinal))
            {
                category = candidate;
                return true;
            }
        }
        category = PluralCategory.Other;
        return false;
    }

    /// <summary>まとまりの名前と形から形のキーを作る（"coins" と One → "coins.one"）。</summary>
    /// <param name="group">まとまりの名前。</param>
    /// <param name="category">形。</param>
    /// <returns>形のキー。</returns>
    public static string FormKey(string group, PluralCategory category) =>
        LocaleJson.Join(group, PluralCategoryNames.ToKey(category));
}
