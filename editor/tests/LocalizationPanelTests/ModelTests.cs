using SEED.Localization;
using SEEDEditor.Localization.Model;
using SpriteRigTests;

namespace LocalizationPanelTests;

// ============================================================
//  ModelTests.cs — 文字列表のモデルの組み立て（行と列・並び・未訳の数え方・説明・読み取り専用）のテスト
// ============================================================

/// <summary>表のモデルの組み立てのテスト。</summary>
internal static class ModelTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("組み立て: 見本の表は列 ja・en（既定 ja）、行は ja の並び、未訳 0（複数形の要らない升目は数えない）", () =>
        {
            var model = Models.Templates();
            Check.Equal("ja,en", string.Join(",", model.Languages.Select(l => l.Code)), "列");
            Check.Equal("ja", model.DefaultCode, "既定");
            Check.True(model.Languages[0].IsDefault && !model.Languages[1].IsDefault, "既定の印");
            Check.Equal("ja", model.Languages[1].Fallback, "en の fallback");

            var rows = model.BuildRows();
            Check.Equal("ui.dialog.ok", rows[0].Key, "先頭の行");
            Check.Equal(0, model.CountMissing(), "未訳: " + string.Join(", ", rows.Where(r => r.HasMissing).Select(r => r.Key)));
            Check.True(!model.IsDirty && model.PendingWrites().Count == 0, "読んだだけでは未保存にならない");
            Check.Equal(0, model.Warnings.Count, "警告: " + string.Join(" / ", model.Warnings));
        });

        h.Add("組み立て: 後の言語にしか無いキーは、その言語で直前にあるキーの後ろに並ぶ", () =>
        {
            var rows = Models.Templates().BuildRows().Select(r => r.Key).ToList();
            // ja は sample.coins（1 文）、en は sample.coins.one / other（形）。en の形は en の並びで直前の sample.money の後ろ
            int money = rows.IndexOf("sample.money");
            Check.Equal("sample.coins.one", rows[money + 1], "money の次");
            Check.Equal("sample.coins.other", rows[money + 2], "その次");
            // en にだけある items.one は items.zero の後ろ
            Check.Equal(rows.IndexOf("sample.items.zero") + 1, rows.IndexOf("sample.items.one"), "items.one の位置");
        });

        h.Add("複数形: 形の行と 1 文の行の種類・要らない升目の理由（1 文で足りる / 規則で使わない / 形で書いてある）", () =>
        {
            var model = Models.Templates();
            var coinsOne = Models.Row(model, "sample.coins.one");
            Check.Equal(LocaleRowKind.PluralForm, coinsOne.Kind, "形の行");
            Check.Equal("sample.coins", coinsOne.LogicalKey, "論理のキー");
            Check.Equal(PluralCategory.One, coinsOne.Category, "形");
            Check.Equal(LocaleCellState.NotNeeded, coinsOne.Cells[0].State, "ja は 1 文で足りる");
            Check.True(coinsOne.Cells[0].Note.Contains("1 文"), coinsOne.Cells[0].Note);
            Check.Equal(LocaleCellState.Translated, coinsOne.Cells[1].State, "en は文あり");

            var coins = Models.Row(model, "sample.coins");
            Check.Equal(LocaleRowKind.PluralPlain, coins.Kind, "1 文の行");
            Check.Equal(LocaleCellState.NotNeeded, coins.Cells[1].State, "en は形で書いてある");

            var itemsOne = Models.Cell(model, "sample.items.one", "ja");
            Check.Equal(LocaleCellState.NotNeeded, itemsOne.State, "ja の規則は one を使わない");
            Check.True(itemsOne.Note.Contains("規則"), itemsOne.Note);
        });

        h.Add("未訳: 空の文字列・null・欠けを数え、状態を分ける（説明のツールチップも分ける）", () =>
        {
            var model = Models.FromTexts(new Dictionary<string, string>
            {
                ["index.json"] = "{ \"default\": \"ja\", \"languages\": [ { \"code\": \"ja\" }, { \"code\": \"en\", \"fallback\": \"ja\" } ] }",
                ["ja.json"] = "{ \"a\": \"あ\", \"b\": \"い\", \"c\": \"う\", \"d\": \"え\" }",
                ["en.json"] = "{ \"a\": \"A\", \"b\": \"\", \"c\": null }",
            });
            Check.Equal(LocaleCellState.Translated, Models.Cell(model, "a", "en").State, "a");
            Check.Equal(LocaleCellState.Empty, Models.Cell(model, "b", "en").State, "b は空");
            Check.Equal(LocaleCellState.Untranslated, Models.Cell(model, "c", "en").State, "c は null");
            Check.Equal(LocaleCellState.Absent, Models.Cell(model, "d", "en").State, "d は欠け");
            Check.Equal(3, model.CountMissing(), "未訳 3");
            Check.True(Models.Cell(model, "d", "en").Note.Contains("表にありません"), "欠けの説明");
            Check.True(Models.Row(model, "d").HasMissing && !Models.Row(model, "a").HasMissing, "行の未訳");
        });

        h.Add("絞り込み: 検索はキーと文（大文字小文字を区別しない）・未訳だけ", () =>
        {
            var model = Models.Templates();
            var rows = model.BuildRows();
            Check.True(LocaleRowFilter.Matches(Models.Row(model, "ui.dialog.cancel"), "CANCEL", false), "キーで当たる");
            Check.True(LocaleRowFilter.Matches(Models.Row(model, "ui.dialog.cancel"), "キャンセル", false), "文で当たる");
            Check.True(!LocaleRowFilter.Matches(Models.Row(model, "ui.dialog.ok"), "キャンセル", false), "当たらない");
            Check.Equal(0, rows.Count(r => LocaleRowFilter.Matches(r, null, missingOnly: true)), "未訳だけ（見本は 0）");
            Check.Equal(rows.Count, rows.Count(r => LocaleRowFilter.Matches(r, "  ", false)), "空白だけは絞らない");
        });

        h.Add("説明: 選んだキーを包むオブジェクトの _about を既定の言語の表から近い順に", () =>
        {
            var comments = Models.Templates().CommentsFor("ui.dialog.ok");
            Check.Equal(2, comments.Count, "ui の _about と根の _about");
            Check.Equal("ui._about", comments[0].Location, "近い順");
            Check.True(comments[0].Text.Contains("SEED.UI"), comments[0].Text);
        });

        h.Add("読み取り専用: 壊れた言語の表は列だけ読み取り専用（編集も保存もしない）、壊れた index.json は全体", () =>
        {
            var model = Models.FromTexts(new Dictionary<string, string>
            {
                ["index.json"] = "{ \"languages\": [ { \"code\": \"ja\" }, { \"code\": \"en\" } ] }",
                ["ja.json"] = "{ \"a\": \"あ\" }",
                ["en.json"] = "{ \"a\": ",
            });
            Check.True(model.Languages[1].IsReadOnly && !model.Languages[0].IsReadOnly, "en だけ読み取り専用");
            Check.True(!model.SetCell("a", "en", "A").Succeeded, "en は編集できない");
            Check.True(model.SetCell("a", "ja", "い").Succeeded, "ja は編集できる");
            Check.Equal("ja.json", string.Join(",", model.PendingWrites().Select(f => f.FileName)), "en は書かない");
            Check.True(model.Warnings.Any(w => w.Contains("en.json")), "警告に出る");

            var brokenIndex = Models.FromTexts(new Dictionary<string, string> { ["index.json"] = "{" });
            Check.True(brokenIndex.IsReadOnly, "index.json が壊れていれば全体が読み取り専用");
            Check.True(!brokenIndex.AddKey("a").Succeeded && brokenIndex.PendingWrites().Count == 0, "何もしない");
        });

        h.Add("読み込み: 対になっていないサロゲートのエスケープ（JsonDocument が ArgumentException を投げる）でも落ちず、その列は読み取り専用", () =>
        {
            var model = Models.FromTexts(new Dictionary<string, string>
            {
                ["index.json"] = "{ \"languages\": [ { \"code\": \"ja\" }, { \"code\": \"en\" } ] }",
                ["ja.json"] = "{ \"a\": \"あ\" }",
                ["en.json"] = "{ \"a\": \"\\ud800\" }",
            });
            Check.True(model.Languages[1].IsReadOnly, "en は読み取り専用");
            Check.True(!model.Languages[0].IsReadOnly, "ja は読める");
            Check.Equal(LocaleCellState.Absent, Models.Cell(model, "a", "en").State, "en は空の表として見せる");
        });

        h.Add("読み込み: 開けない表（読めないファイル）は無いものと区別して読み取り専用", () =>
        {
            var model = LocaleTableModel.Load(
                LocaleFileContent.Read("{ \"languages\": [ { \"code\": \"ja\" } ] }"),
                _ => LocaleFileContent.Unreadable("使用中"));
            Check.True(model.Languages[0].IsReadOnly, "読み取り専用");
            Check.True(model.Languages[0].LoadError.Contains("使用中"), model.Languages[0].LoadError);
        });
    }
}
