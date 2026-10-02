using SEED.Localization;
using SEEDEditor.Localization.Json;
using SpriteRigTests;

namespace LocalizationPanelTests;

// ============================================================
//  DocumentTests.cs — LocaleJsonDocument（形を保つ読み込みと書き戻し）のテスト
// ============================================================

/// <summary>形を保つ読み込みのテスト。</summary>
internal static class DocumentTests
{
    /// <summary>見本の表のファイル。</summary>
    private static readonly string[] TemplateFiles = { "index.json", "ja.json", "en.json" };

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("往復: templates/locale の index.json・ja.json・en.json は読んで書き戻すと元のファイルと同じ文字の並び", () =>
        {
            foreach (var file in TemplateFiles)
            {
                string text = File.ReadAllText(Path.Combine(RepoPaths.TemplateLocale, file));
                var document = LocaleJsonDocument.Parse(text);
                Check.True(document.IsValid, $"{file}: {document.Error}");
                Check.Equal(text, document.Write(), $"{file} の往復");
            }
        });

        h.Add("往復: 改行は元のファイルに合わせる（CRLF のファイルは CRLF・LF は LF）", () =>
        {
            string lf = "{\n  \"a\": \"x\"\n}\n";
            string crlf = lf.Replace("\n", "\r\n");
            Check.Equal(lf, LocaleJsonDocument.Parse(lf).Write(), "LF");
            Check.Equal(crlf, LocaleJsonDocument.Parse(crlf).Write(), "CRLF");
        });

        h.Add("読み込み: null・数・真偽値・空のまとまり・配列の null・説明の鍵を項目として残し、そのまま書き戻す", () =>
        {
            string text =
                "{\n" +
                "  \"_about\": \"説明\",\n" +
                "  \"todo\": null,\n" +
                "  \"count\": 10,\n" +
                "  \"ratio\": 1.5,\n" +
                "  \"flag\": false,\n" +
                "  \"empty\": {},\n" +
                "  \"none\": [],\n" +
                "  \"week\": [\"日\", null, \"火\"],\n" +
                "  \"menu\": {\n" +
                "    \"_comment\": \"メニュー\",\n" +
                "    \"start\": \"はじめる\"\n" +
                "  }\n" +
                "}\n";
            var document = LocaleJsonDocument.Parse(text);
            Check.Equal(text, document.Write(), "書き戻し");

            // キーの項目（表の行）は説明・空のまとまりを除いたもの
            var keys = Enumerable.Range(0, document.Count).Where(document.IsKeyEntry).Select(document.FlatKeyAt).ToList();
            Check.Equal("todo,count,ratio,flag,week.0,week.1,week.2,menu.start", string.Join(",", keys), "キーの項目");
            Check.True(document.IsCommentEntry(document.FindPath(new[] { "menu", "_comment" })), "説明の項目");
            Check.True(document.IsPlaceholder(document.FindPath(new[] { "empty" })), "空のまとまり");
        });

        h.Add("読み込み: コメントと末尾のカンマを許す（書き戻しでは消える）・同じ道筋は後の値（位置は先）", () =>
        {
            string text = "{\n  // コメント\n  \"a\": \"1\",\n  \"b\": \"2\",\n  \"a\": \"3\",\n}\n";
            var document = LocaleJsonDocument.Parse(text);
            Check.True(document.IsValid, document.Error);
            Check.Equal("{\n  \"a\": \"3\",\n  \"b\": \"2\"\n}\n", document.Write(), "書き戻し");
        });

        h.Add("読み込み: 壊れた JSON・最上位が配列は Error（項目 0）", () =>
        {
            var broken = LocaleJsonDocument.Parse("{ \"a\": ");
            Check.True(!broken.IsValid && broken.Count == 0, "壊れた JSON");
            var array = LocaleJsonDocument.Parse("[1, 2]");
            Check.True(!array.IsValid, "最上位が配列");
        });

        h.Add("キー: 平たい書き方のファイルは新しいキーも平たいまま・入れ子のファイルは . で分ける", () =>
        {
            var flat = LocaleJsonDocument.Parse("{ \"menu.start\": \"x\", \"menu.quit\": \"y\" }");
            Check.True(flat.UsesFlatKeys, "平たい書き方");
            Check.Equal("menu.help", string.Join("|", flat.PathForNewKey("menu.help")), "平たいまま");
            Check.True(flat.FindKey("menu.quit") == 1, "平たいキーで引ける");

            var nested = LocaleJsonDocument.Parse("{ \"menu\": { \"start\": \"x\" } }");
            Check.True(!nested.UsesFlatKeys, "入れ子の書き方");
            Check.Equal("menu|help", string.Join("|", nested.PathForNewKey("menu.help")), "分ける");
        });

        h.Add("ぶつかり: 値の下・まとまりの位置・同じ道筋には置けない。空のまとまりの下には置ける", () =>
        {
            var document = LocaleJsonDocument.Parse("{ \"a\": \"x\", \"b\": { \"c\": \"y\" }, \"e\": {} }");
            Check.True(!document.CanPlaceLeaf(new[] { "a", "z" }, null, out var underLeaf), "値の下");
            Check.True(underLeaf.Contains("「a」"), underLeaf);
            Check.True(!document.CanPlaceLeaf(new[] { "b" }, null, out _), "まとまりの位置");
            Check.True(!document.CanPlaceLeaf(new[] { "b", "c" }, null, out _), "同じ道筋");
            Check.True(document.CanPlaceLeaf(new[] { "b", "d" }, null, out _), "まとまりの中の新しい鍵");
            Check.True(document.CanPlaceLeaf(new[] { "e", "f" }, null, out _), "空のまとまりの下");
            Check.True(document.CanPlaceLeaf(new[] { "a" }, new HashSet<int> { 0 }, out _), "自分自身を外せば置ける");

            // 空のまとまりは置く前に消す
            document.RemovePlaceholdersAlong(new[] { "e", "f" });
            document.Append(LocaleJsonWriter.Entry.Text(new[] { "e", "f" }, "z"));
            Check.Equal("{\n  \"a\": \"x\",\n  \"b\": {\n    \"c\": \"y\"\n  },\n  \"e\": {\n    \"f\": \"z\"\n  }\n}\n", document.Write(), "空のまとまりの中へ");
        });

        h.Add("説明: キーを包むオブジェクトの _ の鍵を近い順に集め、別のキー専用の説明（_greeting）は外す", () =>
        {
            var document = LocaleJsonDocument.Parse(
                "{ \"_about\": \"根\", \"ui\": { \"_about\": \"UI\", \"_back\": \"戻るの説明\", \"ok\": \"OK\", \"back\": \"戻る\" } }");
            var forOk = document.CommentsFor(new[] { "ui", "ok" });
            Check.Equal("ui._about=UI|_about=根", string.Join("|", forOk.Select(c => c.Location + "=" + c.Text)), "ok の説明");
            var forBack = document.CommentsFor(new[] { "ui", "back" });
            Check.Equal("ui._about=UI|ui._back=戻るの説明|_about=根", string.Join("|", forBack.Select(c => c.Location + "=" + c.Text)), "back の説明");
        });
    }
}
