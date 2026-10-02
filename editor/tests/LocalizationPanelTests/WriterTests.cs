using SEED.Localization;
using SpriteRigTests;
using static SEED.Localization.LocaleJsonWriter;

namespace LocalizationPanelTests;

// ============================================================
//  WriterTests.cs — LocaleJsonWriter（平たくした項目 → 入れ子の JSON）のテスト
// ============================================================

/// <summary>書き出しのテスト。</summary>
internal static class WriterTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("書き出し: 入れ子・並び（最初に出てきた順）・null・字句・インデント 2・最後に改行", () =>
        {
            string json = Write(new[]
            {
                Entry.Text(new[] { "_about" }, "説明"),
                Entry.Text(new[] { "menu", "start" }, "はじめる"),
                Entry.Text(new[] { "greeting" }, "こんにちは"),
                Entry.Untranslated(new[] { "menu", "quit" }),   // 後から来ても menu の中へ入る
                new Entry(new[] { "count" }, EntryKind.Raw, "10"),
                new Entry(new[] { "flag" }, EntryKind.Raw, "true"),
            });
            string expected =
                "{\n" +
                "  \"_about\": \"説明\",\n" +
                "  \"menu\": {\n" +
                "    \"start\": \"はじめる\",\n" +
                "    \"quit\": null\n" +
                "  },\n" +
                "  \"greeting\": \"こんにちは\",\n" +
                "  \"count\": 10,\n" +
                "  \"flag\": true\n" +
                "}\n";
            Check.Equal(expected, json, "書き出し");
        });

        h.Add("書き出し: 0,1,2… が抜けなく並ぶまとまりは配列（値だけなら 1 行）、番号が飛んだらオブジェクト", () =>
        {
            string json = Write(new[]
            {
                Entry.Text(new[] { "week", "0" }, "日"),
                Entry.Text(new[] { "week", "1" }, "月"),
                Entry.Untranslated(new[] { "week", "2" }),
                Entry.Text(new[] { "gap", "0" }, "a"),
                Entry.Text(new[] { "gap", "2" }, "c"),
            });
            string expected =
                "{\n" +
                "  \"week\": [\"日\", \"月\", null],\n" +
                "  \"gap\": {\n" +
                "    \"0\": \"a\",\n" +
                "    \"2\": \"c\"\n" +
                "  }\n" +
                "}\n";
            Check.Equal(expected, json, "配列");
        });

        h.Add("書き出し: 配列の中の値だけのオブジェクトは 1 行（index.json の書き方）", () =>
        {
            string json = Write(new[]
            {
                Entry.Text(new[] { "default" }, "ja"),
                Entry.Text(new[] { "languages", "0", "code" }, "ja"),
                Entry.Untranslated(new[] { "languages", "0", "fallback" }),
                Entry.Text(new[] { "languages", "1", "code" }, "en"),
                Entry.Text(new[] { "languages", "1", "fallback" }, "ja"),
            });
            string expected =
                "{\n" +
                "  \"default\": \"ja\",\n" +
                "  \"languages\": [\n" +
                "    { \"code\": \"ja\", \"fallback\": null },\n" +
                "    { \"code\": \"en\", \"fallback\": \"ja\" }\n" +
                "  ]\n" +
                "}\n";
            Check.Equal(expected, json, "index の書き方");
        });

        h.Add("書き出し: エスケープは要るものだけ（\" \\ 改行 タブ 制御文字 対でないサロゲート）。日本語・絵文字はそのまま", () =>
        {
            string text = "a\"b\\c\nd\te\u0001f" + "😀" + "\uD800" + "日本";
            string json = Write(new[] { Entry.Text(new[] { "k" }, text) });
            Check.Equal("{\n  \"k\": \"a\\\"b\\\\c\\nd\\te\\u0001f😀\\ud800日本\"\n}\n", json, "エスケープ");

            // 読み直すと元の文字（対でないサロゲートは .NET の JSON が文字列として読めないので往復は外す）
            string valid = "a\"b\\c\nd\te\u0001f" + "😀" + "日本";
            var table = LocaleTable.Parse(Write(new[] { Entry.Text(new[] { "k" }, valid) }), "t");
            Check.True(table.TryGetText("k", out var back), "読める");
            Check.Equal(valid, back, "往復");
        });

        h.Add("書き出し: 改行とインデントの幅を変えられる・項目が無ければ {}", () =>
        {
            string json = Write(new[] { Entry.Text(new[] { "a", "b" }, "x") }, new Options { NewLine = "\r\n", IndentSize = 4 });
            Check.Equal("{\r\n    \"a\": {\r\n        \"b\": \"x\"\r\n    }\r\n}\r\n", json, "CRLF と 4");
            Check.Equal("{}\n", Write(Array.Empty<Entry>()), "空");
        });

        h.Add("書き出し: 鍵 1 つの道筋は平たいまま（{\"menu.start\": …}）。SplitKey は Join の逆", () =>
        {
            string json = Write(new[] { Entry.Text(new[] { "menu.start" }, "x") });
            Check.Equal("{\n  \"menu.start\": \"x\"\n}\n", json, "平たい鍵");
            Check.Equal("a|b|c", string.Join("|", SplitKey(LocaleJson.Join(LocaleJson.Join("a", "b"), "c"))), "SplitKey");
        });

        h.Add("書き出し: 空の道筋・同じ道筋・値の下の子・まとまりへの値は ArgumentException", () =>
        {
            ExpectThrows(() => Write(new[] { Entry.Text(Array.Empty<string>(), "x") }), "空の道筋");
            ExpectThrows(() => Write(new[] { Entry.Text(new[] { "a" }, "x"), Entry.Text(new[] { "a" }, "y") }), "同じ道筋");
            ExpectThrows(() => Write(new[] { Entry.Text(new[] { "a" }, "x"), Entry.Text(new[] { "a", "b" }, "y") }), "値の下の子");
            ExpectThrows(() => Write(new[] { Entry.Text(new[] { "a", "b" }, "x"), Entry.Text(new[] { "a" }, "y") }), "まとまりへの値");
        });
    }

    /// <summary>ArgumentException が投げられることを表明する。</summary>
    private static void ExpectThrows(Action action, string what)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new AssertionException($"{what}: ArgumentException が投げられませんでした");
    }
}
