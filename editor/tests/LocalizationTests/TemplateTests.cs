using SEED.Localization;
using SEEDEditor.Templates;
using SpriteRigTests;

namespace LocalizationTests;

// ============================================================
//  TemplateTests.cs — templates/locale（見本の表）と、テンプレートの取り込み（editor/src/Templates）で assets/locale へ入ること
//
//  1. templates/locale の index.json・ja.json・en.json が警告なしで読め、ja と en でキー（複数形のまとまりは名前）と
//     差し込みの名前がそろっていること
//  2. 本体（LocaleCatalog）で templates をそのままアセットの根として読み、言語の切り替え・複数形・書式が見本どおりに出ること
//  3. TemplateLibrary が locale をカテゴリとして見つけ、TemplateImporter の計画が "locale/…"（templates/ を付けない）へ
//     コピーし、コピー先を本体で読めること
// ============================================================

/// <summary>見本の表とテンプレートの取り込みのテスト。</summary>
internal static class TemplateTests
{
    /// <summary>テンプレートのカテゴリ（フォルダ）の名前。</summary>
    private const string LocaleCategory = "locale";

    /// <summary>見本の表のファイル。</summary>
    private static readonly string[] LocaleFiles = { "index.json", "ja.json", "en.json" };

    /// <summary>確かめに使う日付（2026-10-02 は金曜日）。</summary>
    private static readonly DateTime Sample = new(2026, 10, 2);

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("見本: templates/locale の index.json・ja.json・en.json は警告なしで読める", () =>
        {
            string dir = Path.Combine(RepoPaths.Templates, LocaleCategory);
            var index = LocaleIndex.Parse(File.ReadAllText(Path.Combine(dir, "index.json")), "index.json");
            Check.Equal("", index.Error, "index.json");
            Check.Equal(0, index.Warnings.Count, "index.json の警告: " + string.Join(" / ", index.Warnings));
            Check.Equal("ja", index.DefaultCode, "既定は ja");
            Check.Equal("ja,en", string.Join(",", index.Languages.Select(l => l.Code)), "言語");
            foreach (var language in index.Languages)
            {
                var table = LoadTable(language.Code);
                Check.Equal("", table.Error, $"{language.Code}.json");
                Check.Equal(0, table.Warnings.Count, $"{language.Code}.json の警告: " + string.Join(" / ", table.Warnings));
            }
        });

        h.Add("見本: ja と en でキー（複数形のまとまりは名前）と差し込みの名前がそろう", () =>
        {
            var ja = LogicalEntries(LoadTable("ja"));
            var en = LogicalEntries(LoadTable("en"));
            var onlyJa = ja.Keys.Except(en.Keys).ToList();
            var onlyEn = en.Keys.Except(ja.Keys).ToList();
            Check.True(onlyJa.Count == 0, "ja だけのキー: " + string.Join(", ", onlyJa));
            Check.True(onlyEn.Count == 0, "en だけのキー: " + string.Join(", ", onlyEn));
            foreach (var (key, names) in ja)
            {
                Check.True(names.SetEquals(en[key]),
                    $"{key} の差し込み: ja {{{string.Join(",", names)}}} / en {{{string.Join(",", en[key])}}}");
            }
        });

        h.Add("見本: 本体で読むと切り替え・複数形・書式・曜日が見本どおり（欠けなし）", () =>
        {
            var store = new MemoryLocaleStore();
            var log = new WarningLog();
            var c = new LocaleCatalog(new FolderLocaleSource(RepoPaths.Templates), store, log.Add, () => null);
            Check.Equal("ja", c.Language, "既定の言語");
            Check.Equal("キャンセル", c.Get("ui.dialog.cancel", None), "ja");
            Check.Equal("コイン 1 枚", c.Plural("sample.coins", 1, None), "ja の複数形（1 文）");
            Check.Equal("アイテムはありません", c.Plural("sample.items", 0, None), "ja の zero");
            Check.Equal("10月2日", c.FormatDate(Sample, c.Get("format.date", None)), "ja の日付の書式");
            Check.Equal("金", c.Get($"ui.weekday_short.{(int)Sample.DayOfWeek}", None), "ja の曜日");
            Check.Equal("所持金 1,200 円", c.Get("sample.money", new (string, object?)[] { ("amount", 1200) }), "ja の差し込み");

            Check.Equal(LocaleSwitchResult.Changed, c.SetLanguage("en"), "en へ");
            Check.Equal("Cancel", c.Get("ui.dialog.cancel", None), "en");
            Check.Equal("1 coin", c.Plural("sample.coins", 1, None), "en one");
            Check.Equal("5 coins", c.Plural("sample.coins", 5, None), "en other");
            Check.Equal("No items", c.Plural("sample.items", 0, None), "en zero");
            Check.Equal("Oct 2", c.FormatDate(Sample, c.Get("format.date", None)), "en の日付の書式");
            Check.Equal("Fri", c.Get($"ui.weekday_short.{(int)Sample.DayOfWeek}", None), "en の曜日");
            Check.Equal("Hello, Kani", c.Get("sample.greeting", new (string, object?)[] { ("name", "Kani") }), "en の差し込み");
            Check.Equal("en", store.GetString(LocaleCatalog.LanguageSaveKey), "保存");
            Check.Equal(0, c.MissingKeys.Count, "欠けなし: " + string.Join(", ", c.MissingKeys));
            Check.Equal(0, log.Messages.Count, "警告なし: " + string.Join(" / ", log.Messages));
        });

        h.Add("取り込み: テンプレートのライブラリが locale をカテゴリとして見つけ、assets/locale へコピーし、本体で読める", () =>
        {
            var library = TemplateLibrary.Load(RepoPaths.Templates);
            var category = library.Categories.FirstOrDefault(c => c.FolderName == LocaleCategory);
            Check.True(category is not null, "locale のカテゴリ");
            var entries = category!.Entries.Select(e => e.RelPath).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var expected = LocaleFiles.Select(f => $"{LocaleCategory}/{f}").OrderBy(p => p, StringComparer.Ordinal).ToList();
            Check.Equal(string.Join(",", expected), string.Join(",", entries), "エントリはファイル 3 つ");

            string assetsRoot = Path.Combine(Path.GetTempPath(), "seed_locale_import_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(assetsRoot);
            try
            {
                var plan = TemplateImporter.CreatePlan(RepoPaths.Templates, assetsRoot, entries);
                var planned = plan.Items.Select(i => i.RelPath).OrderBy(p => p, StringComparer.Ordinal).ToList();
                Check.Equal(string.Join(",", expected), string.Join(",", planned), "計画は locale/ のファイル 3 つ（templates/ を付けない）");
                Check.Equal(0, plan.MissingReferences.Count, "足りない参照なし");

                var result = TemplateImporter.Execute(plan, TemplateImportConflictPolicy.Skip);
                Check.Equal(LocaleFiles.Length, result.CopiedCount, "3 つコピー");
                foreach (var file in LocaleFiles)
                    Check.True(File.Exists(Path.Combine(assetsRoot, LocaleCategory, file)), $"assets/locale/{file}");

                var c = new LocaleCatalog(new FolderLocaleSource(assetsRoot), new MemoryLocaleStore(), null, () => "en-GB");
                Check.Equal("en", c.Language, "取り込んだ表を読む（端末 en-GB → en）");
                Check.Equal("OK", c.Get("ui.dialog.ok", None), "引ける");
                Check.Equal(2, c.Languages.Count, "2 言語");
            }
            finally
            {
                Directory.Delete(assetsRoot, recursive: true);
            }
        });
    }

    /// <summary>差し込みなし。</summary>
    private static ReadOnlySpan<(string Name, object? Value)> None => ReadOnlySpan<(string Name, object? Value)>.Empty;

    /// <summary>見本の言語の表を読む。</summary>
    private static LocaleTable LoadTable(string code)
    {
        string path = Path.Combine(RepoPaths.Templates, LocaleCategory, code + LocalePaths.TableExtension);
        return LocaleTable.Parse(File.ReadAllText(path), path);
    }

    /// <summary>
    /// 表の「論理のキー」（普通のキーと複数形のまとまりの名前。まとまりの子は含めない）→ 差し込みの名前の集まり
    /// （まとまりは全部の形の和）。
    /// </summary>
    private static Dictionary<string, HashSet<string>> LogicalEntries(LocaleTable table)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (key, text) in table.Entries)
        {
            int dot = key.LastIndexOf(LocaleJson.KeySeparator);
            string logical = dot > 0 && table.IsPluralGroup(key.Substring(0, dot)) && PluralCategoryNames.IsCategoryKey(key.Substring(dot + 1))
                ? key.Substring(0, dot)
                : key;
            if (!result.TryGetValue(logical, out var names)) result[logical] = names = new HashSet<string>(StringComparer.Ordinal);
            names.UnionWith(LocaleFormatter.Placeholders(text));
        }
        return result;
    }
}
