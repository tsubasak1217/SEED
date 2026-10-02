using SEED.Localization;
using SpriteRigTests;

namespace LocalizationTests;

// ============================================================
//  PluralTests.cs — 複数形の規則（PluralRules。CLDR の整数の規則の簡略版）
// ============================================================

/// <summary>複数形の規則のテスト。</summary>
internal static class PluralTests
{
    /// <summary>言語 code で数の並びがすべて expected の形になることを確かめる。</summary>
    private static void Expect(string code, PluralCategory expected, params long[] counts)
    {
        foreach (long n in counts)
            Check.Equal(expected, PluralRules.Select(code, n), $"{code} の {n}");
    }

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("複数形: 日本語・中国語・韓国語は other だけ", () =>
        {
            Expect("ja", PluralCategory.Other, 0, 1, 2, 100);
            Expect("zh-Hant", PluralCategory.Other, 1);
            Expect("ko", PluralCategory.Other, 1);
        });

        h.Add("複数形: 英語・ドイツ語（と表に無い言語）は 1 だけ one", () =>
        {
            Expect("en", PluralCategory.One, 1);
            Expect("en", PluralCategory.Other, 0, 2, 11, 21, 101);
            Expect("de", PluralCategory.One, 1);
            Expect("xx", PluralCategory.Other, 0, 2);
            Check.Equal(PluralRuleKind.OneOther, PluralRules.RuleFor(null), "コードなしは one / other");
        });

        h.Add("複数形: フランス語・ブラジルのポルトガル語は 0 と 1 が one、ポルトガルのポルトガル語は 1 だけ", () =>
        {
            Expect("fr", PluralCategory.One, 0, 1);
            Expect("fr", PluralCategory.Other, 2, 100);
            Expect("pt", PluralCategory.One, 0, 1);
            Expect("pt-BR", PluralCategory.One, 0);
            Expect("pt-PT", PluralCategory.Other, 0);
            Expect("pt_PT", PluralCategory.One, 1);
        });

        h.Add("複数形: ロシア語・ウクライナ語（one / few / many）", () =>
        {
            Expect("ru", PluralCategory.One, 1, 21, 101, 1001);
            Expect("ru", PluralCategory.Few, 2, 3, 4, 22, 104);
            Expect("ru", PluralCategory.Many, 0, 5, 11, 12, 14, 25, 111, 112);
            Expect("uk", PluralCategory.Few, 23);
        });

        h.Add("複数形: ポーランド語（1 だけ one・21 は many）", () =>
        {
            Expect("pl", PluralCategory.One, 1);
            Expect("pl", PluralCategory.Few, 2, 3, 4, 22, 24);
            Expect("pl", PluralCategory.Many, 0, 5, 12, 14, 21, 25, 112);
        });

        h.Add("複数形: チェコ語（one / few / other）・ヘブライ語（one / two / other）", () =>
        {
            Expect("cs", PluralCategory.One, 1);
            Expect("cs", PluralCategory.Few, 2, 4);
            Expect("cs", PluralCategory.Other, 0, 5, 22);
            Expect("he", PluralCategory.One, 1);
            Expect("he", PluralCategory.Two, 2);
            Expect("he", PluralCategory.Other, 0, 3, 10);
        });

        h.Add("複数形: アラビア語（zero / one / two / few / many / other）", () =>
        {
            Expect("ar", PluralCategory.Zero, 0);
            Expect("ar", PluralCategory.One, 1);
            Expect("ar", PluralCategory.Two, 2);
            Expect("ar", PluralCategory.Few, 3, 10, 103, 110);
            Expect("ar", PluralCategory.Many, 11, 99, 111);
            Expect("ar", PluralCategory.Other, 100, 101, 102);
        });

        h.Add("複数形: 負の数は絶対値で判定・long.MinValue もあふれない", () =>
        {
            Expect("en", PluralCategory.One, -1);
            Expect("ru", PluralCategory.One, -21);
            Expect("ru", PluralCategory.Many, -5);
            Expect("en", PluralCategory.Other, long.MinValue);
            Expect("ru", PluralCategory.Many, long.MinValue);   // 絶対値 9223372036854775808 の末尾は 8 → many
        });

        h.Add("複数形: 種類と子の鍵の名前", () =>
        {
            Check.Equal("zero", PluralCategoryNames.ToKey(PluralCategory.Zero), "zero");
            Check.Equal("other", PluralCategoryNames.ToKey(PluralCategory.Other), "other");
            Check.True(PluralCategoryNames.IsCategoryKey("few") && !PluralCategoryNames.IsCategoryKey("Few"), "大文字小文字を区別");
        });
    }
}
