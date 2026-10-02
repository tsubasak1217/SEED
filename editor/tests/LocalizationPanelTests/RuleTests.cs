using SEEDEditor.Assets;
using SpriteRigTests;

namespace LocalizationPanelTests;

// ============================================================
//  RuleTests.cs — ダブルクリックで専用のパネルへ回す規則（PanelOpenRuleCatalog・editor/config/panel_open_rules.json）のテスト
// ============================================================

/// <summary>パネルで開く規則のテスト。</summary>
internal static class RuleTests
{
    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("規則: locale フォルダの .json だけが文字列表のパネル（組み込み既定と editor/config の JSON が同じ）", () =>
        {
            var fromFile = PanelOpenRuleCatalog.LoadFromDir(RepoPaths.EditorConfig);
            Check.Equal(0, fromFile.Warnings.Count, "警告: " + string.Join(" / ", fromFile.Warnings));
            foreach (var catalog in new[] { PanelOpenRuleCatalog.BuiltIn(), fromFile })
            {
                string assets = Path.Combine("C:", "proj", "assets");
                Check.Equal("localization", catalog.FindPanelFor(Path.Combine(assets, "locale", "en.json")), "assets/locale/en.json");
                Check.Equal("localization", catalog.FindPanelFor(Path.Combine(assets, "locale", "index.json")), "index.json");
                Check.Equal("localization", catalog.FindPanelFor(Path.Combine(assets, "story", "Locale", "ja.JSON")), "大文字小文字を区別しない");
                Check.Equal(null, catalog.FindPanelFor(Path.Combine(assets, "data", "en.json")), "ほかのフォルダの .json");
                Check.Equal(null, catalog.FindPanelFor(Path.Combine(assets, "locale", "notes.txt")), "locale の .txt");
                Check.Equal(null, catalog.FindPanelFor(Path.Combine(assets, "locale", "sub", "en.json")), "直上のフォルダだけを見る");
                Check.Equal(null, catalog.FindPanelFor(""), "空");
            }
        });

        h.Add("規則: 壊れた・無いファイルは組み込み既定（警告つき）・空の規則は尊重・だめな規則だけ捨てる", () =>
        {
            using var temp = new TempFolder();
            string path = Path.Combine(temp.Path, PanelOpenRuleCatalog.FileName);

            var missing = PanelOpenRuleCatalog.Load(path);
            Check.True(missing.Warnings.Count == 1 && missing.Rules.Count == 1, "無いファイル");

            File.WriteAllText(path, "{ broken");
            var broken = PanelOpenRuleCatalog.Load(path);
            Check.True(broken.Warnings.Count == 1 && broken.Rules.Count == 1, "壊れたファイル");

            File.WriteAllText(path, "{ \"format_version\": 1, \"rules\": [] }");
            Check.Equal(0, PanelOpenRuleCatalog.Load(path).Rules.Count, "空の規則（パネルへ回さない設定）");

            File.WriteAllText(path, "{ \"format_version\": 1, \"rules\": [ { \"panel\": \"\", \"parent_folder\": \"x\", \"extensions\": [\".a\"] }, { \"panel\": \"p\", \"parent_folder\": \"text\", \"extensions\": [\"txt\"] } ] }");
            var partial = PanelOpenRuleCatalog.Load(path);
            Check.True(partial.Rules.Count == 1 && partial.Warnings.Count == 1, "だめな規則だけ捨てる");
            Check.Equal("p", partial.FindPanelFor(Path.Combine(temp.Path, "text", "a.txt")), "ドットの無い拡張子も読める");
        });
    }
}
