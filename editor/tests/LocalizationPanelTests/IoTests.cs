using System.Text;
using SEEDEditor.Assets;
using SEEDEditor.Localization.IO;
using SEEDEditor.Reload;
using SpriteRigTests;

namespace LocalizationPanelTests;

// ============================================================
//  IoTests.cs — ディスクの読み書き（LocaleFolderIO）・外部変更の控え（LocaleFolderSnapshot）・
//               見本の取り込み（LocaleTemplateInstaller）・置き場の場所（LocaleFolderLocator）のテスト
// ============================================================

/// <summary>ディスクとのやり取りのテスト。</summary>
internal static class IoTests
{
    /// <summary>UTF-8 の BOM。</summary>
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    /// <summary>テストを登録する。</summary>
    public static void Register(TestHarness h)
    {
        h.Add("保存: UTF-8・BOM 無し・最後に改行・変わったファイルだけ・旧版を <assets>/.backup へ退避", () =>
        {
            using var temp = new TempFolder();
            string folder = CopyTemplates(temp.Path);
            var jaBefore = File.ReadAllBytes(Path.Combine(folder, "ja.json"));

            var model = LocaleFolderIO.Load(folder);
            model.SetCell("ui.dialog.ok", "en", "Okay");
            var written = LocaleFolderIO.Save(folder, model, assetsRoot: temp.Path);

            Check.Equal("en.json", string.Join(",", written.Select(Path.GetFileName)), "en だけ書く");
            var bytes = File.ReadAllBytes(Path.Combine(folder, "en.json"));
            Check.True(!bytes.Take(Utf8Bom.Length).SequenceEqual(Utf8Bom), "BOM 無し");
            string text = Encoding.UTF8.GetString(bytes);
            Check.True(text.Contains("\"ok\": \"Okay\""), "中身");
            Check.True(text.EndsWith("\n", StringComparison.Ordinal) && text.TrimEnd('\r', '\n').EndsWith("}", StringComparison.Ordinal), "最後に改行 1 つ");
            Check.True(File.ReadAllBytes(Path.Combine(folder, "ja.json")).SequenceEqual(jaBefore), "ja.json は触らない");
            Check.True(Directory.GetFiles(Path.Combine(temp.Path, SafeFileWriter.BackupDirName, "locale")).Any(f => Path.GetFileName(f).StartsWith("en.", StringComparison.Ordinal)), "旧版の退避");
            Check.True(!model.IsDirty, "保存済み");

            // 読み直すと書いた値
            Check.Equal("Okay", Models.Cell(LocaleFolderIO.Load(folder), "ui.dialog.ok", "en").Text, "読み直し");
        });

        h.Add("読み込み: 無い・ある（BOM は落とす）を分け、フォルダが無くても空のモデル", () =>
        {
            using var temp = new TempFolder();
            string path = Path.Combine(temp.Path, "x.json");
            Check.True(!LocaleFolderIO.ReadFile(path).Exists, "無い");
            File.WriteAllBytes(path, Utf8Bom.Concat(Encoding.UTF8.GetBytes("{ \"a\": \"あ\" }")).ToArray());
            var content = LocaleFolderIO.ReadFile(path);
            Check.True(content.Exists && content.Text == "{ \"a\": \"あ\" }", "BOM を落として読む");

            var empty = LocaleFolderIO.Load(Path.Combine(temp.Path, "nothing"));
            Check.True(!empty.IndexExists && empty.Languages.Count == 0, "空のモデル");
        });

        h.Add("控え: 中身の変わった・足された・消えたファイルだけを返す（*.json だけ。一時ファイルは見ない）", () =>
        {
            using var temp = new TempFolder();
            File.WriteAllText(Path.Combine(temp.Path, "a.json"), "1");
            File.WriteAllText(Path.Combine(temp.Path, "b.json"), "2");
            var before = LocaleFolderSnapshot.Capture(temp.Path, FileContentHash.TryCompute);
            Check.Equal(0, LocaleFolderSnapshot.Capture(temp.Path, FileContentHash.TryCompute).ChangedSince(before).Count, "同じ中身");

            File.WriteAllText(Path.Combine(temp.Path, "a.json"), "1");          // 同じ中身の書き直し（時刻だけ）
            File.WriteAllText(Path.Combine(temp.Path, "b.json"), "22");         // 中身の変更
            File.WriteAllText(Path.Combine(temp.Path, "c.json"), "3");          // 追加
            File.WriteAllText(Path.Combine(temp.Path, "c.json.tmp"), "tmp");    // 一時ファイルは見ない
            var after = LocaleFolderSnapshot.Capture(temp.Path, FileContentHash.TryCompute);
            Check.Equal("b.json,c.json", string.Join(",", after.ChangedSince(before)), "変更と追加");

            File.Delete(Path.Combine(temp.Path, "b.json"));
            Check.Equal("b.json", string.Join(",", LocaleFolderSnapshot.Capture(temp.Path, FileContentHash.TryCompute).ChangedSince(after)), "削除");
            Check.Equal(0, LocaleFolderSnapshot.Capture(Path.Combine(temp.Path, "none"), FileContentHash.TryCompute).FileNames.Count, "無いフォルダは空");
        });

        h.Add("見本の取り込み: assets/locale へ index.json・ja.json・en.json が入り、2 度目は上書きしない", () =>
        {
            using var temp = new TempFolder();
            var first = LocaleTemplateInstaller.Install(RepoPaths.Templates, temp.Path);
            Check.True(first.Succeeded && first.CopiedCount == 3, first.Message);
            string folder = LocaleFolderLocator.DefaultFolder(temp.Path)!;
            Check.Equal(Path.Combine(temp.Path, "locale"), folder, "既定の置き場");
            Check.True(new[] { "index.json", "ja.json", "en.json" }.All(f => File.Exists(Path.Combine(folder, f))), "3 つ入る");

            File.WriteAllText(Path.Combine(folder, "ja.json"), "{ \"mine\": \"自分の\" }");
            var second = LocaleTemplateInstaller.Install(RepoPaths.Templates, temp.Path);
            Check.True(second.Succeeded && second.CopiedCount == 0, second.Message);
            Check.True(File.ReadAllText(Path.Combine(folder, "ja.json")).Contains("自分の"), "上書きしない");

            // locale のカテゴリが無いライブラリ（空のフォルダ）は失敗
            string emptyLibrary = Path.Combine(temp.Path, "empty_library");
            Directory.CreateDirectory(emptyLibrary);
            Check.True(!LocaleTemplateInstaller.Install(emptyLibrary, temp.Path).Succeeded, "locale の無いライブラリは失敗");
        });

        h.Add("場所: 表示名は assets:// の形（外なら絶対パス）・index.json と表のパス", () =>
        {
            string assets = Path.Combine(Path.GetTempPath(), "proj", "assets");
            Check.Equal("assets://locale", LocaleFolderLocator.DisplayName(assets, Path.Combine(assets, "locale")), "既定");
            Check.Equal("assets://story/locale", LocaleFolderLocator.DisplayName(assets, Path.Combine(assets, "story", "locale")), "別の置き場");
            string outside = Path.Combine(Path.GetTempPath(), "other", "locale");
            Check.Equal(outside, LocaleFolderLocator.DisplayName(assets, outside), "外");
            Check.Equal(Path.Combine(assets, "locale", "en.json"), LocaleFolderLocator.TablePath(Path.Combine(assets, "locale"), "en"), "表のパス");
        });
    }

    /// <summary>見本の表を一時フォルダの assets/locale と同じ形の locale/ へ写す。</summary>
    private static string CopyTemplates(string assetsRoot)
    {
        string folder = Path.Combine(assetsRoot, "locale");
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(RepoPaths.TemplateLocale))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        return folder;
    }
}
