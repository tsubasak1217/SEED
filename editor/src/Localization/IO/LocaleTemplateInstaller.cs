// ============================================================
//  LocaleTemplateInstaller.cs — 見本の表（templates/locale）をプロジェクトの assets/locale へ取り込む
//
//  文字列表のパネルの「作る」ボタンの中身。テンプレートの取り込み（editor/src/Templates の TemplateLibrary・
//  TemplateImporter。ファイル → テンプレートをインポート と同じ道）で locale のカテゴリを丸ごと選んで取り込む。
//  ライブラリはアセットの根と同じ形なので、locale/index.json・ja.json・en.json がそのまま assets/locale/ へ入る
//  （docs/localization.md §10。LocalizationTests が同じ道を確かめている）。既にあるファイルは上書きしない。
// ============================================================

using System;
using System.Linq;
using SEEDEditor.Templates;

namespace SEEDEditor.Localization.IO;

/// <summary>見本の表の取り込みの結果。</summary>
/// <param name="Succeeded">取り込めたか（1 つも写せなかった・カテゴリが無いなら false）。</param>
/// <param name="CopiedCount">写したファイルの数。</param>
/// <param name="Message">結果の説明（失敗なら理由）。</param>
public readonly record struct LocaleTemplateInstallResult(bool Succeeded, int CopiedCount, string Message);

/// <summary>見本の表（templates/locale）をプロジェクトへ取り込む。</summary>
public static class LocaleTemplateInstaller
{
    /// <summary>テンプレートのライブラリの多言語のカテゴリ（フォルダ名）。</summary>
    public const string TemplateCategory = "locale";

    /// <summary>
    /// 見本の表を取り込む（既にあるファイルは飛ばす）。
    /// </summary>
    /// <param name="libraryRoot">テンプレートのライブラリ（TemplateLibraryLocator.Resolve）。</param>
    /// <param name="assetsRoot">プロジェクトのアセットのフォルダ。</param>
    /// <returns>結果。</returns>
    public static LocaleTemplateInstallResult Install(string libraryRoot, string assetsRoot)
    {
        var library = TemplateLibrary.Load(libraryRoot);
        var category = library.Categories.FirstOrDefault(
            c => string.Equals(c.FolderName, TemplateCategory, StringComparison.OrdinalIgnoreCase));
        if (category is null || category.Entries.Count == 0)
            return new LocaleTemplateInstallResult(false, 0, $"テンプレートのライブラリに {TemplateCategory} がありません: {libraryRoot}");

        var plan = TemplateImporter.CreatePlan(libraryRoot, assetsRoot, category.Entries.Select(e => e.RelPath).ToList());
        var result = TemplateImporter.Execute(plan, TemplateImportConflictPolicy.Skip);
        if (result.Failures.Count > 0)
        {
            string failures = string.Join(" / ", result.Failures.Select(f => $"{f.RelPath}: {f.Message}"));
            return new LocaleTemplateInstallResult(result.CopiedCount > 0, result.CopiedCount, $"写せなかったファイルがあります: {failures}");
        }
        return new LocaleTemplateInstallResult(true, result.CopiedCount,
            $"見本の表を {result.CopiedCount} ファイル取り込みました（既にあった {result.SkippedCount} ファイルはそのまま）");
    }
}
