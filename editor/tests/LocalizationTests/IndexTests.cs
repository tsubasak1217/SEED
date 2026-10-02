using SEED.Localization;
using SpriteRigTests;

namespace LocalizationTests;

// ============================================================
//  IndexTests.cs — 言語の一覧（LocaleIndex）の読み込み・言語の引き当て・探す順
// ============================================================

/// <summary>言語の一覧のテスト。</summary>
internal static class IndexTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("一覧: 言語・既定・fallback・culture（省略はコード）・name（省略はコード）を読む", () =>
        {
            var index = LocaleIndex.Parse("""
                { "_about": "説明", "default": "ja", "languages": [
                  { "code": "ja", "name": "日本語", "fallback": null, "culture": "ja-JP" },
                  { "code": "en", "fallback": "ja" } ] }
                """, "index.json");
            Check.Equal("", index.Error, "読める");
            Check.Equal(0, index.Warnings.Count, "警告なし");
            Check.Equal(2, index.Languages.Count, "言語の数");
            Check.Equal("ja", index.DefaultCode, "既定");
            Check.Equal("日本語", index.Languages[0].Name, "名前");
            Check.Equal("ja-JP", index.Languages[0].CultureName, "culture");
            Check.Equal(null, index.Languages[0].Fallback, "fallback null");
            Check.Equal("en", index.Languages[1].Name, "名前の省略はコード");
            Check.Equal("en", index.Languages[1].CultureName, "culture の省略はコード");
            Check.Equal("ja", index.Languages[1].Fallback, "fallback");
        });

        h.Add("一覧: default の省略は先頭・知らない default は警告して先頭", () =>
        {
            var omitted = LocaleIndex.Parse("""{ "languages": [ { "code": "en" }, { "code": "ja" } ] }""", "i");
            Check.Equal("en", omitted.DefaultCode, "省略は先頭");
            var unknown = LocaleIndex.Parse("""{ "default": "fr", "languages": [ { "code": "en" } ] }""", "i");
            Check.Equal("en", unknown.DefaultCode, "知らない default は先頭");
            Check.Equal(1, unknown.Warnings.Count, "警告");
        });

        h.Add("一覧: 知らない言語を指す fallback は警告して既定の言語へ直接落とす", () =>
        {
            var index = LocaleIndex.Parse("""{ "languages": [ { "code": "ja" }, { "code": "en", "fallback": "de" } ] }""", "i");
            Check.Equal(null, index.Find("en")!.Fallback, "fallback を捨てる");
            Check.Equal(1, index.Warnings.Count, "警告");
        });

        h.Add("一覧: 重なったコード・コードの無い要素・オブジェクトでない要素・知らない鍵は警告して飛ばす", () =>
        {
            var index = LocaleIndex.Parse("""
                { "languages": [ { "code": "ja", "name": "先" }, { "code": "JA", "name": "後" }, { "name": "コード無し" }, "en",
                  { "code": "en", "flag": "🇺🇸" } ], "extra": 1 }
                """, "i");
            Check.Equal(2, index.Languages.Count, "ja と en");
            Check.Equal("先", index.Find("ja")!.Name, "先のものを使う");
            Check.Equal(5, index.Warnings.Count, "重なり・コード無し・文字列の要素・知らない鍵 flag・知らない鍵 extra");
        });

        h.Add("一覧: \"pt_BR\" は \"pt-BR\" に書きそろえる", () =>
        {
            var index = LocaleIndex.Parse("""{ "languages": [ { "code": "pt_BR", "fallback": "en" }, { "code": "en" } ] }""", "i");
            Check.Equal("pt-BR", index.Languages[0].Code, "書きそろえ");
            Check.Equal("en", index.Languages[0].Fallback, "fallback");
        });

        h.Add("一覧: 壊れた JSON・最上位がオブジェクトでない・空なら言語 0 件（例外にしない）", () =>
        {
            Check.True(!LocaleIndex.Parse("{", "i").IsValid, "壊れた JSON");
            Check.True(!LocaleIndex.Parse("[]", "i").IsValid, "配列");
            var empty = LocaleIndex.Parse("{}", "i");
            Check.True(empty.IsValid && empty.IsEmpty && empty.DefaultCode == "", "言語 0 件");
            Check.Equal(0, empty.BuildChain("ja").Count, "探す順も空");
        });

        h.Add("一覧: 言語の引き当て（大文字小文字・_ と -・地域を落とす・同じ言語の別の地域・当たらなければ null）", () =>
        {
            var index = LocaleIndex.Parse("""
                { "languages": [ { "code": "ja" }, { "code": "en-US" }, { "code": "zh-Hant" }, { "code": "pt" } ] }
                """, "i");
            Check.Equal("ja", index.Match("JA"), "大文字小文字");
            Check.Equal("ja", index.Match("ja-JP"), "地域を落とす");
            Check.Equal("ja", index.Match("ja_JP"), "_ も区切り");
            Check.Equal("en-US", index.Match("en-us"), "そのまま");
            Check.Equal("en-US", index.Match("en-GB"), "同じ言語の別の地域");
            Check.Equal("en-US", index.Match("en"), "言語だけ");
            Check.Equal("zh-Hant", index.Match("zh-Hant-TW"), "後ろから落とす");
            Check.Equal("pt", index.Match("pt-BR"), "pt-BR → pt");
            Check.Equal(null, index.Match("fr-FR"), "無い言語");
            Check.Equal(null, index.Match(null), "null");
            Check.Equal(null, index.Match("  "), "空白");
        });

        h.Add("一覧: 探す順（fallback の連鎖 → 既定・輪・自分を指す・知らない言語）", () =>
        {
            var index = LocaleIndex.Parse("""
                { "default": "ja", "languages": [
                  { "code": "ja" }, { "code": "en", "fallback": "ja" }, { "code": "pt", "fallback": "en" }, { "code": "pt-BR", "fallback": "pt" },
                  { "code": "a", "fallback": "b" }, { "code": "b", "fallback": "a" }, { "code": "self", "fallback": "self" } ] }
                """, "i");
            Check.Equal("pt-BR,pt,en,ja", string.Join(",", index.BuildChain("pt-BR")), "連鎖");
            Check.Equal("ja", string.Join(",", index.BuildChain("ja")), "既定だけ");
            Check.Equal("a,b,ja", string.Join(",", index.BuildChain("a")), "輪で止まる");
            Check.Equal("self,ja", string.Join(",", index.BuildChain("self")), "自分を指す");
            Check.Equal("ja", string.Join(",", index.BuildChain("xx")), "知らない言語は既定だけ");
        });
    }
}
