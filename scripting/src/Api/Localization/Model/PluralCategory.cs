using System;

namespace SEED.Localization;

// ============================================================
//  PluralCategory.cs — 複数形の形の種類（CLDR の zero / one / two / few / many / other。純粋）
//
//  言語の表では、複数形のキーを子の鍵に分けて書く:
//    "coins": { "one": "{n} coin", "other": "{n} coins" }
//  子の鍵の名前（"one" など）と列挙子の対応は、この 1 か所（Names）で決める。
// ============================================================

/// <summary>複数形の形の種類（CLDR の複数形の種類）。</summary>
public enum PluralCategory
{
    /// <summary>0 のときの形（アラビア語の 0 など。どの言語でも n = 0 なら「zero」の子を先に探す）。</summary>
    Zero,
    /// <summary>1 つの形（英語の 1、ロシア語の 1・21・31 … など）。</summary>
    One,
    /// <summary>2 つの形（アラビア語・ヘブライ語の 2）。</summary>
    Two,
    /// <summary>少数の形（ロシア語の 2〜4・22〜24 …、チェコ語の 2〜4 など）。</summary>
    Few,
    /// <summary>多数の形（ロシア語の 5〜20・25〜30 …、ポーランド語の 5〜21 … など）。</summary>
    Many,
    /// <summary>そのほかの形（すべての言語にある。日本語・中国語・韓国語はこれだけ）。</summary>
    Other,
}

/// <summary>複数形の種類と言語の表の子の鍵の名前の対応。</summary>
public static class PluralCategoryNames
{
    /// <summary>種類 → 子の鍵の名前（列挙の順）。</summary>
    private static readonly string[] Names = { "zero", "one", "two", "few", "many", "other" };

    /// <summary>種類の子の鍵の名前（"one" など）。</summary>
    /// <param name="category">種類。</param>
    /// <returns>子の鍵の名前。</returns>
    public static string ToKey(PluralCategory category) => Names[(int)category];

    /// <summary>子の鍵の名前が複数形の種類の名前か（大文字小文字を区別する）。</summary>
    /// <param name="key">子の鍵。</param>
    /// <returns>複数形の種類の名前なら true。</returns>
    public static bool IsCategoryKey(string key) => Array.IndexOf(Names, key) >= 0;
}
