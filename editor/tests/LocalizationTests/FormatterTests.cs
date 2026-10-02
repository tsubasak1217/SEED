using System.Globalization;
using SEED.Localization;
using SpriteRigTests;

namespace LocalizationTests;

// ============================================================
//  FormatterTests.cs — 差し込み（LocaleFormatter）の規則
// ============================================================

/// <summary>差し込みのテスト。</summary>
internal static class FormatterTests
{
    /// <summary>不変文化。</summary>
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>名前つきの値で差し込む近道。</summary>
    private static string F(string template, params (string Name, object? Value)[] args) =>
        LocaleFormatter.Format(template, args, Inv);

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("差し込み: {name} と {0}（名前つきの値も順で引ける）", () =>
        {
            Check.Equal("こんにちは、カニ さん", F("こんにちは、{name} さん", ("name", "カニ")), "{name}");
            Check.Equal("1 と 2", F("{0} と {1}", ("a", 1), ("b", 2)), "{0}{1}");
            Check.Equal("X=X", F("{x}={0}", ("x", "X")), "名前つきの値を順でも引ける");
        });

        h.Add("差し込み: {name:書式} は文化で書く・読めない書式は書式なし", () =>
        {
            Check.Equal("1,234,567 円", F("{amount:N0} 円", ("amount", 1234567)), "N0（不変文化）");
            Check.Equal("1.234.567", LocaleFormatter.Format("{a:N0}", new (string, object?)[] { ("a", 1234567) }, CultureInfo.GetCultureInfo("de-DE")), "N0（de-DE）");
            Check.Equal("5", F("{v:Q}", ("v", 5)), "読めない書式");
            Check.Equal("abc", F("{s:N0}", ("s", "abc")), "文字列に書式は効かない");
        });

        h.Add("差し込み: {{ と }} は波かっこそのもの", () =>
        {
            Check.Equal("{name} は {x}", F("{{name}} は {{x}}", ("name", "N"), ("x", "X")), "二重の波かっこ");
            Check.Equal("a } b { c", F("a }} b {{ c"), "片方だけ");
        });

        h.Add("差し込み: 渡していない名前・Text の差し込みスロットの記法・{} はそのまま残す", () =>
        {
            Check.Equal("所持金 {num} 円 {num:2} {num.3} {string} {image}", F("所持金 {num} 円 {num:2} {num.3} {string} {image}", ("name", "x")), "Text の記法");
            Check.Equal("{color}注意{/color}", F("{color}注意{/color}", ("name", "x")), "色の記法");
            Check.Equal("{} {missing}", F("{} {missing}", ("name", "x")), "空・知らない名前");
            Check.Equal("{0}", F("{0}"), "値が無ければ {0} も残す");
        });

        h.Add("差し込み: 閉じていない {・中に { を含む {…}", () =>
        {
            Check.Equal("a {b", F("a {b", ("b", "B")), "閉じていない");
            Check.Equal("{aX}", F("{a{b}}", ("b", "X")), "内側だけ差し込む");
        });

        h.Add("差し込み: null は空・同じ名前は先のもの・波かっこの無い文はそのまま（同じもの）", () =>
        {
            Check.Equal("[]", F("[{v}]", ("v", null)), "null");
            Check.Equal("先", F("{v}", ("v", "先"), ("v", "後")), "先が勝つ");
            const string plain = "波かっこなし";
            Check.True(ReferenceEquals(plain, F(plain, ("v", 1))), "組み立てずに返す");
            Check.Equal("", F(""), "空");
            Check.Equal("", LocaleFormatter.Format(null, default, Inv), "null の文");
        });

        h.Add("差し込み: 使っている名前の一覧（Placeholders。二重の波かっこは数えない・重なりなし）", () =>
        {
            var names = LocaleFormatter.Placeholders("{a} {b:N0} {0} {{c}} {a} {color}x{/color}");
            Check.Equal("a,b,0,color,/color", string.Join(",", names), "名前");
            Check.Equal(0, LocaleFormatter.Placeholders("なし").Count, "無し");
        });
    }
}
