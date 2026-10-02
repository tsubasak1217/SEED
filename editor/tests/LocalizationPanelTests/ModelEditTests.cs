using SEED.Localization;
using SEEDEditor.Localization.Model;
using SpriteRigTests;

namespace LocalizationPanelTests;

// ============================================================
//  ModelEditTests.cs — 文字列表のモデルの編集（升目・キーの追加/改名/削除・複数形の形）と保存で書く中身のテスト
// ============================================================

/// <summary>表のモデルの編集のテスト。</summary>
internal static class ModelEditTests
{
    /// <summary>小さな 2 言語の表（ja / en）。</summary>
    private static Dictionary<string, string> SmallFiles() => new()
    {
        ["index.json"] =
            "{\n" +
            "  \"default\": \"ja\",\n" +
            "  \"languages\": [\n" +
            "    { \"code\": \"ja\", \"name\": \"日本語\", \"fallback\": null },\n" +
            "    { \"code\": \"en\", \"name\": \"English\", \"fallback\": \"ja\" }\n" +
            "  ]\n" +
            "}\n",
        ["ja.json"] =
            "{\n" +
            "  \"_about\": \"日本語\",\n" +
            "  \"menu\": {\n" +
            "    \"start\": \"はじめる\",\n" +
            "    \"help\": \"たすけて\",\n" +
            "    \"quit\": \"おわる\"\n" +
            "  },\n" +
            "  \"score\": \"スコア {0}\"\n" +
            "}\n",
        ["en.json"] =
            "{\n" +
            "  \"menu\": {\n" +
            "    \"start\": \"Start\",\n" +
            "    \"quit\": \"Quit\"\n" +
            "  },\n" +
            "  \"score\": \"Score {0}\"\n" +
            "}\n",
    };

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("升目: 書き換えると変わった表だけが保存の対象・同じ値に戻すと未保存も消える", () =>
        {
            var model = Models.FromTexts(SmallFiles());
            Check.True(model.SetCell("menu.start", "en", "Begin").Changed, "変わった");
            Check.True(model.IsDirty, "未保存");
            Check.Equal("en.json", string.Join(",", model.PendingWrites().Select(f => f.FileName)), "en だけ");
            Check.True(Models.Pending(model, "en.json")!.Contains("\"start\": \"Begin\""), "中身");

            Check.True(!model.SetCell("menu.start", "en", "Begin").Changed, "同じ値は変えない");
            Check.True(model.SetCell("menu.start", "en", "Start").Changed, "元へ戻す");
            Check.True(!model.IsDirty && model.PendingWrites().Count == 0, "元の中身に戻れば未保存は消える");
        });

        h.Add("升目: 空にすると null（未訳）・表に無いキーに書くと、ほかの言語の並びの位置へ入る", () =>
        {
            var model = Models.FromTexts(SmallFiles());
            Check.True(model.SetCell("menu.quit", "en", "").Changed, "空にした");
            Check.Equal(LocaleCellState.Untranslated, Models.Cell(model, "menu.quit", "en").State, "null になる");
            Check.True(Models.Pending(model, "en.json")!.Contains("\"quit\": null"), "null で書く");

            // en に無い menu.help は start の後ろ（ja の並び）へ入る
            Check.Equal(LocaleCellState.Absent, Models.Cell(model, "menu.help", "en").State, "前は欠け");
            Check.True(!model.SetCell("menu.help", "en", "").Changed, "欠けのまま空なら何も足さない");
            Check.True(model.SetCell("menu.help", "en", "Help").Changed, "足した");
            string en = Models.Pending(model, "en.json")!;
            Check.Equal(
                "{\n  \"menu\": {\n    \"start\": \"Start\",\n    \"help\": \"Help\",\n    \"quit\": null\n  },\n  \"score\": \"Score {0}\"\n}\n",
                en, "en の中身");
        });

        h.Add("キーの追加: 全部の言語の末尾に null で入り、行も末尾・未訳に数える", () =>
        {
            var model = Models.FromTexts(SmallFiles());
            int missingBefore = model.CountMissing();
            Check.True(model.AddKey("  title.main  ").Succeeded, "足した（前後の空白は落とす）");
            Check.Equal("title.main", model.BuildRows().Last().Key, "行の末尾");
            Check.Equal(missingBefore + 2, model.CountMissing(), "ja・en の 2 升が未訳");
            Check.True(Models.Pending(model, "ja.json")!.EndsWith("  \"score\": \"スコア {0}\",\n  \"title\": {\n    \"main\": null\n  }\n}\n"), "ja の末尾");
            Check.True(Models.Pending(model, "en.json")!.Contains("\"main\": null"), "en にも");
        });

        h.Add("キーの追加: 重なり・値の下・まとまりの位置・書き方の誤りは断り、何も変えない", () =>
        {
            var model = Models.FromTexts(SmallFiles());
            Check.True(model.AddKey("score").Message.Contains("既に"), "重なり");
            Check.True(model.AddKey("score.extra").Message.Contains("「score」"), "値の下");
            Check.True(model.AddKey("menu").Message.Contains("下にキー"), "まとまりの位置");
            Check.True(!model.AddKey("a..b").Succeeded, "空の鍵");
            Check.True(!model.AddKey("_hidden").Succeeded, "説明の鍵");
            Check.True(!model.AddKey("a b").Succeeded, "空白");
            Check.True(!model.AddKey("").Succeeded, "空");
            Check.True(!model.IsDirty, "何も変わっていない");
        });

        h.Add("名前の変更: 表の中の位置はそのまま・全部の言語・既にある名前は断る", () =>
        {
            var model = Models.FromTexts(SmallFiles());
            Check.True(model.RenameKey("menu.start", "menu.begin").Succeeded, "替えた");
            Check.Equal("menu.begin", model.BuildRows()[0].Key, "行の位置はそのまま");
            Check.True(Models.Pending(model, "ja.json")!.Contains("\"begin\": \"はじめる\",\n    \"help\""), "ja の位置");
            Check.True(Models.Pending(model, "en.json")!.Contains("\"begin\": \"Start\",\n    \"quit\""), "en の位置");
            Check.True(model.RenameKey("menu.begin", "score").Message.Contains("既に"), "既にある名前");
            Check.True(model.RenameKey("menu.begin", "menu.begin").Succeeded && !model.RenameKey("menu.begin", "menu.begin").Changed, "同じ名前");

            // 別のオブジェクトへ移すと、そのオブジェクトが（最初の項目の位置に）できる
            Check.True(model.RenameKey("score", "hud.score").Succeeded, "入れ子へ");
            Check.True(Models.Pending(model, "en.json")!.Contains("\"hud\": {\n    \"score\": \"Score {0}\"\n  }"), "hud ができる");
        });

        h.Add("名前の変更: 複数形のまとまりは形ごと替わる", () =>
        {
            var model = Models.Templates();
            Check.True(model.RenameKey("sample.items", "sample.things").Succeeded, "替えた");
            var keys = model.BuildRows().Select(r => r.Key).ToList();
            Check.True(keys.Contains("sample.things.zero") && keys.Contains("sample.things.one") && keys.Contains("sample.things.other"), "形も替わる");
            Check.True(!keys.Any(k => k.StartsWith("sample.items", StringComparison.Ordinal)), "古い名前は残らない");
            Check.Equal(0, model.CountMissing(), "未訳は増えない");
        });

        h.Add("削除: 全部の言語の表から消える・行も消える", () =>
        {
            var model = Models.FromTexts(SmallFiles());
            Check.True(model.DeleteKeys(new[] { "menu.help", "score" }).Changed, "消した");
            var keys = model.BuildRows().Select(r => r.Key).ToList();
            Check.Equal("menu.start,menu.quit", string.Join(",", keys), "残り");
            Check.Equal("{\n  \"menu\": {\n    \"start\": \"Start\",\n    \"quit\": \"Quit\"\n  }\n}\n", Models.Pending(model, "en.json"), "en");
            Check.True(!model.DeleteKeys(new[] { "nothing" }).Changed, "無いキー");
        });

        h.Add("複数形の形の追加: まだまとまりでないキーは、全言語で 1 文を other へ移してまとまりにする", () =>
        {
            var model = Models.FromTexts(SmallFiles());
            Check.True(model.AddPluralForm("score", PluralCategory.One).Changed, "足した");
            Check.True(Models.Pending(model, "ja.json")!.Contains("\"score\": {\n    \"other\": \"スコア {0}\",\n    \"one\": null\n  }"), "ja");
            Check.True(Models.Pending(model, "en.json")!.Contains("\"score\": {\n    \"other\": \"Score {0}\",\n    \"one\": null\n  }"), "en");
            Check.True(model.PluralGroups().Contains("score"), "まとまりになった（LocaleTable の判定）");
            Check.Equal(LocaleCellState.Untranslated, Models.Cell(model, "score.one", "en").State, "en の one は書くべき");
            Check.Equal(LocaleCellState.NotNeeded, Models.Cell(model, "score.one", "ja").State, "ja の one は要らない");
        });

        h.Add("複数形の形の追加: まとまりがあれば 1 文で足りる言語は触らず、ほかの言語に形を足す", () =>
        {
            var model = Models.Templates();
            Check.True(model.AddPluralForm("sample.coins", PluralCategory.Few).Changed, "足した");
            Check.True(Models.Pending(model, "ja.json") is null, "ja（1 文）は触らない");
            Check.True(Models.Pending(model, "en.json")!.Contains("\"one\": \"{n} coin\",\n      \"other\": \"{n} coins\",\n      \"few\": null"), "en の形の末尾へ");
            Check.True(!model.AddPluralForm("sample.coins", PluralCategory.Few).Changed, "2 度目は何もしない");
            Check.Equal(0, model.CountMissing(), "en の規則は few を使わない（未訳に数えない）");
        });

        h.Add("保存: 書いた中身を LocaleTable で読むと編集が効いている（往復）", () =>
        {
            var model = Models.Templates();
            model.SetCell("ui.dialog.ok", "en", "Okay");
            model.AddKey("menu.title");
            model.SetCell("menu.title", "ja", "タイトル");
            var ja = LocaleTable.Parse(Models.Pending(model, "ja.json"), "ja.json");
            var en = LocaleTable.Parse(Models.Pending(model, "en.json"), "en.json");
            Check.True(ja.IsValid && en.IsValid && ja.Warnings.Count == 0 && en.Warnings.Count == 0, "警告なしで読める");
            Check.True(en.TryGetText("ui.dialog.ok", out var ok) && ok == "Okay", "en の書き換え");
            Check.True(ja.TryGetText("menu.title", out var title) && title == "タイトル", "ja の足したキー");
            Check.True(!en.ContainsKey("menu.title"), "en の足したキーは null（未訳＝次の言語から引く）");
            Check.True(ja.TryGetText("ui.weekday_short.5", out var friday) && friday == "金", "配列は配列のまま読める");
            Check.True(Models.Pending(model, "ja.json")!.Contains("\"weekday_short\": [\"日\", \"月\", \"火\", \"水\", \"木\", \"金\", \"土\"]"), "配列は 1 行のまま");

            model.MarkSaved();
            Check.True(!model.IsDirty && model.PendingWrites().Count == 0, "保存したら未保存は消える");
        });
    }
}
