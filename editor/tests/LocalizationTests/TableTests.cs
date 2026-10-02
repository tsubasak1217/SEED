using SEED.Localization;
using SpriteRigTests;

namespace LocalizationTests;

// ============================================================
//  TableTests.cs — 言語の表（LocaleTable）の JSON の平たん化と引き方
// ============================================================

/// <summary>言語の表のテスト。</summary>
internal static class TableTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("表: 入れ子は . でつないだキーに平たくし、説明の鍵（_ で始まる）は読まない", () =>
        {
            var t = LocaleTable.Parse("""
                { "_about": "説明", "menu": { "_note": "説明", "start": "はじめる", "sub": { "deep": "深い" } }, "top": "上" }
                """, "t.json");
            Check.Equal("", t.Error, "壊れていない");
            Check.True(t.TryGetText("menu.start", out var start) && start == "はじめる", "menu.start");
            Check.True(t.TryGetText("menu.sub.deep", out var deep) && deep == "深い", "3 段の入れ子");
            Check.True(t.TryGetText("top", out var top) && top == "上", "最上位");
            Check.True(!t.ContainsKey("_about") && !t.ContainsKey("menu._note"), "説明の鍵は読まない");
            Check.Equal(3, t.Count, "キーの数");
            Check.Equal(0, t.Warnings.Count, "警告なし");
        });

        h.Add("表: 配列は番号のキー・数と真偽値は書いたままの文字・null は無いのと同じ", () =>
        {
            var t = LocaleTable.Parse("""
                { "days": ["日", "月"], "max": 10, "ratio": 1.5, "on": true, "off": false, "todo": null }
                """, "t.json");
            Check.True(t.TryGetText("days.0", out var d0) && d0 == "日", "days.0");
            Check.True(t.TryGetText("days.1", out var d1) && d1 == "月", "days.1");
            Check.True(t.TryGetText("max", out var max) && max == "10", "数");
            Check.True(t.TryGetText("ratio", out var ratio) && ratio == "1.5", "小数");
            Check.True(t.TryGetText("on", out var on) && on == "true", "真");
            Check.True(t.TryGetText("off", out var off) && off == "false", "偽");
            Check.True(!t.ContainsKey("todo"), "null は無いのと同じ（次の言語を探す）");
        });

        h.Add("表: 子がすべて複数形の種類ならまとまり。まとまりの名前を引くと other、子も普通に引ける", () =>
        {
            var t = LocaleTable.Parse("""
                { "coins": { "_note": "説明", "one": "{n} coin", "other": "{n} coins" } }
                """, "t.json");
            Check.True(t.IsPluralGroup("coins"), "まとまり");
            Check.True(t.ContainsKey("coins"), "まとまりの名前はキーとしてある");
            Check.True(t.TryGetText("coins", out var other) && other == "{n} coins", "まとまりの名前は other");
            Check.True(t.TryGetText("coins.one", out var one) && one == "{n} coin", "子も引ける");
        });

        h.Add("表: 複数形の種類でない子・オブジェクトの子があるとまとまりにしない（Wake or Pay の label・unit など）", () =>
        {
            var t = LocaleTable.Parse("""
                { "hostage": { "label": "コイン", "unit": "枚" }, "mixed": { "one": "1", "label": "x" }, "nested": { "one": { "a": "b" } } }
                """, "t.json");
            Check.True(!t.IsPluralGroup("hostage"), "label・unit はまとまりでない");
            Check.True(!t.IsPluralGroup("mixed"), "複数形の種類と混ざるとまとまりでない");
            Check.True(!t.IsPluralGroup("nested"), "値がオブジェクトならまとまりでない");
            Check.True(t.TryGetText("nested.one.a", out var a) && a == "b", "入れ子として読む");
        });

        h.Add("表: 重なったキーは警告して後のものを使う（\"a.b\" と {\"a\":{\"b\"}}）", () =>
        {
            var t = LocaleTable.Parse("""{ "a.b": "先", "a": { "b": "後" } }""", "t.json");
            Check.True(t.TryGetText("a.b", out var ab) && ab == "後", "後のもの");
            Check.Equal(1, t.Warnings.Count, "警告 1 つ");
            Check.True(t.Warnings[0].Contains("t.json"), "警告にどこのファイルかが入る");
        });

        h.Add("表: 空の鍵は警告して読まない", () =>
        {
            var t = LocaleTable.Parse("""{ "": "空", "ok": "x" }""", "t.json");
            Check.True(t.ContainsKey("ok") && t.Count == 1, "空の鍵は飛ばす");
            Check.Equal(1, t.Warnings.Count, "警告 1 つ");
        });

        h.Add("表: コメント（// と /* */）と末尾のカンマを許す", () =>
        {
            var t = LocaleTable.Parse("""
                {
                  // 行のコメント
                  "a": "x", /* 囲みのコメント */
                  "b": ["y", "z",],
                }
                """, "t.json");
            Check.Equal("", t.Error, "読める");
            Check.True(t.ContainsKey("a") && t.ContainsKey("b.1"), "中身");
        });

        h.Add("表: 壊れた JSON・最上位がオブジェクトでなければ Error で空（例外にしない）", () =>
        {
            var broken = LocaleTable.Parse("{ \"a\": ", "t.json");
            Check.True(!broken.IsValid && broken.Count == 0, "壊れた JSON");
            var array = LocaleTable.Parse("[\"a\"]", "t.json");
            Check.True(!array.IsValid && array.Count == 0, "最上位が配列");
            var empty = LocaleTable.Parse(null, "t.json");
            Check.True(!empty.IsValid, "null の本文");
        });

        h.Add("表: 複数形の引き方の順（0 は zero → 規則の形 → other → 普通のキー）", () =>
        {
            var t = LocaleTable.Parse("""
                { "items": { "zero": "なし", "one": "1 つ", "other": "{n} 個" }, "plain": "{n} 枚", "noother": { "one": "1" } }
                """, "t.json");
            Check.True(t.TryGetPlural("items", 0, PluralCategory.Other, out var zero) && zero == "なし", "0 は zero");
            Check.True(t.TryGetPlural("items", 1, PluralCategory.One, out var one) && one == "1 つ", "規則の形");
            Check.True(t.TryGetPlural("items", 5, PluralCategory.Few, out var few) && few == "{n} 個", "無い形は other");
            Check.True(t.TryGetPlural("plain", 3, PluralCategory.Other, out var plain) && plain == "{n} 枚", "普通のキー 1 文");
            Check.True(!t.TryGetPlural("noother", 2, PluralCategory.Other, out _), "other の無いまとまりで当たらなければ無い");
            Check.True(!t.TryGetPlural("nothing", 1, PluralCategory.One, out _), "無いキー");
        });

        h.Add("表: FromEntries（コードで表を組む）", () =>
        {
            var t = LocaleTable.FromEntries(
                new Dictionary<string, string> { ["x.one"] = "1", ["x.other"] = "n", ["y"] = "Y" },
                new[] { "x" });
            Check.True(t.IsPluralGroup("x") && t.TryGetText("x", out var x) && x == "n", "まとまり");
            Check.True(t.TryGetText("y", out var y) && y == "Y", "普通のキー");
        });
    }
}
