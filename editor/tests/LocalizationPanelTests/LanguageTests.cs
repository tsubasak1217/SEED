using SEED.Localization;
using SEEDEditor.Localization.Model;
using SpriteRigTests;

namespace LocalizationPanelTests;

// ============================================================
//  LanguageTests.cs — 言語の編集（追加・一覧から外す・既定・fallback・名前）と index.json の書き出しのテスト
// ============================================================

/// <summary>言語の編集のテスト。</summary>
internal static class LanguageTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("言語の追加: index.json の末尾に 1 行（code・name・fallback=null）・空の表を作る・全部の升目が未訳", () =>
        {
            var model = Models.Templates();
            int rows = model.BuildRows().Count;
            Check.True(model.AddLanguage("fr", "Français").Succeeded, "足した");
            Check.Equal("ja,en,fr", string.Join(",", model.Languages.Select(l => l.Code)), "列");
            string index = Models.Pending(model, "index.json")!;
            Check.True(index.Contains("    { \"code\": \"en\", \"name\": \"English\", \"fallback\": \"ja\", \"culture\": \"en-US\" },\n    { \"code\": \"fr\", \"name\": \"Français\", \"fallback\": null }\n  ]"), index);
            Check.Equal("{}\n", Models.Pending(model, "fr.json"), "空の表も保存で作る");
            Check.True(!model.Languages[2].FileExists, "まだファイルは無い");

            // fr の未訳: 普通のキーは全部、複数形は fr の規則（0 と 1 = one・ほか other）で書くべき形だけ
            var fr = model.BuildRows().Select(r => r.Cells[2]).ToList();
            Check.Equal(rows, fr.Count, "行の数は同じ");
            Check.True(fr.All(c => c.State is LocaleCellState.Absent or LocaleCellState.NotNeeded), "全部欠けか要らない");
            Check.Equal(LocaleCellState.NotNeeded, Models.Cell(model, "sample.items.zero", "fr").State, "fr の規則は zero を使わない");
            Check.Equal(LocaleCellState.Absent, Models.Cell(model, "sample.items.one", "fr").State, "fr は one を書く");

            var parsed = LocaleIndex.Parse(Models.Pending(model, "index.json"), "index.json");
            Check.Equal(0, parsed.Warnings.Count, "警告なしで読める: " + string.Join(" / ", parsed.Warnings));
            Check.Equal("fr", parsed.Languages[2].Code, "実行中も fr が読める");
        });

        h.Add("言語の追加: コードの書き方・重なり・index は断る（_ は - に書きそろえる）", () =>
        {
            var model = Models.Templates();
            Check.True(model.AddLanguage("JA", null).Message.Contains("既に"), "大文字小文字を区別せず重なり");
            Check.True(!model.AddLanguage("index", null).Succeeded, "index は使えない");
            Check.True(!model.AddLanguage("e n", null).Succeeded, "空白");
            Check.True(!model.AddLanguage("en.US", null).Succeeded, "ドット");
            Check.True(!model.AddLanguage("", null).Succeeded, "空");
            Check.True(model.AddLanguage("pt_BR", null).Succeeded, "pt_BR");
            Check.Equal("pt-BR", model.Languages.Last().Code, "書きそろえ");
            Check.Equal("pt-BR", model.Languages.Last().Name, "名前を省くとコード");
        });

        h.Add("言語の追加: 置き場に同じ名前の表が既にあれば読んで使う（一覧から外した言語を戻せる）", () =>
        {
            var files = new Dictionary<string, string>
            {
                ["index.json"] = "{ \"languages\": [ { \"code\": \"ja\" } ] }",
                ["ja.json"] = "{ \"a\": \"あ\" }",
                ["de.json"] = "{ \"a\": \"A (de)\" }",
            };
            var model = Models.FromTexts(files);
            Check.True(model.AddLanguage("de", "Deutsch").Succeeded, "足した");
            Check.Equal("A (de)", Models.Cell(model, "a", "de").Text, "既にある表を読む");
            Check.True(Models.Pending(model, "de.json") is null, "既にある表は書き直さない");
        });

        h.Add("言語を外す: index.json から消え、表のファイルは消さない・指していた fallback は null・既定なら先頭へ", () =>
        {
            var model = Models.Templates();
            Check.True(model.RemoveLanguage("ja").Succeeded, "ja を外した");
            Check.Equal("en", string.Join(",", model.Languages.Select(l => l.Code)), "残り");
            Check.Equal("en", model.DefaultCode, "既定は残りの先頭");
            Check.Equal(null, model.Languages[0].Fallback, "ja を指していた fallback は null");
            Check.Equal("index.json", string.Join(",", model.PendingWrites().Select(f => f.FileName)), "書くのは index.json だけ（ja.json は消さない）");
            string index = Models.Pending(model, "index.json")!;
            Check.True(index.Contains("\"default\": \"en\"") && index.Contains("{ \"code\": \"en\", \"name\": \"English\", \"fallback\": null, \"culture\": \"en-US\" }"), index);

            Check.True(model.RemoveLanguage("en").Succeeded, "最後の言語も外せる");
            Check.True(Models.Pending(model, "index.json")!.Contains("\"languages\": []"), "空の配列を残す");
            Check.True(!Models.Pending(model, "index.json")!.Contains("\"default\""), "既定の鍵も消える");
            Check.True(!model.RemoveLanguage("xx").Succeeded, "無い言語");
        });

        h.Add("既定・fallback・名前: index.json の該当の鍵だけが替わる。自分自身・無い言語は断る", () =>
        {
            var model = Models.Templates();
            Check.True(model.SetDefaultLanguage("en").Changed, "既定を en へ");
            Check.True(!model.SetDefaultLanguage("EN").Changed, "同じ");
            Check.True(model.SetFallback("en", null).Changed, "fallback を外す");
            Check.True(!model.SetFallback("en", "en").Succeeded, "自分自身");
            Check.True(!model.SetFallback("en", "xx").Succeeded, "無い言語");
            Check.True(model.SetFallback("ja", "en").Changed, "ja → en");
            Check.True(model.SetLanguageName("en", "英語").Changed, "名前");
            Check.True(!model.SetLanguageName("en", "  ").Succeeded, "空の名前");

            string index = Models.Pending(model, "index.json")!;
            Check.True(index.Contains("\"default\": \"en\""), "default");
            Check.True(index.Contains("{ \"code\": \"ja\", \"name\": \"日本語\", \"fallback\": \"en\", \"culture\": \"ja-JP\" }"), "ja の行");
            Check.True(index.Contains("{ \"code\": \"en\", \"name\": \"英語\", \"fallback\": null, \"culture\": \"en-US\" }"), "en の行");
            Check.Equal("ja,en", string.Join(",", model.FallbackChain("ja")), "探す順（ja → fallback の en。既定も en）");
            Check.True(index.StartsWith("{\n  \"_about\""), "説明の鍵は残る");
        });

        h.Add("index.json が無い置き場: 言語を足すと index.json を作り、既定もその言語になる", () =>
        {
            var model = Models.FromTexts(new Dictionary<string, string>());
            Check.True(!model.IndexExists && model.Languages.Count == 0, "空");
            Check.True(!model.AddKey("a").Succeeded, "言語が無いとキーは足せない");
            Check.True(model.AddLanguage("ja", "日本語").Succeeded, "足した");
            Check.Equal(
                "{\n  \"default\": \"ja\",\n  \"languages\": [\n    { \"code\": \"ja\", \"name\": \"日本語\", \"fallback\": null }\n  ]\n}\n",
                Models.Pending(model, "index.json"), "index.json");
            Check.True(model.AddKey("menu.start").Succeeded, "キーも足せる");
            Check.Equal("{\n  \"menu\": {\n    \"start\": null\n  }\n}\n", Models.Pending(model, "ja.json"), "ja.json");
        });
    }
}
