// ============================================================
//  TemplateLibrary.cs — テンプレートライブラリの走査
//
//  【役割】
//  ライブラリルート（templates/）を 1 度だけ舐めて
//  「カテゴリ（トップレベルフォルダ）→ エントリ（その直下の子）」の一覧を作る。
//  コピーもインポートもここでは行わない（決定と実行を分ける）。
//
//  【ライブラリの形】
//  ライブラリのフォルダ構成は**アセットルートと同じ形**である。
//    templates/shaders/toon.wgsl  →  <ProjectAssets>/shaders/toon.wgsl
//  つまり "templates/" という接頭辞はコピー先に付かない。ライブラリ内のシーンが
//  assets://shaders/toon.wgsl のようにルート相対で参照しているのもこのためで、
//  ライブラリのルートをアセットルートとみなせば参照がそのまま解決できる
//  （依存の閉包計算に AssetCollector をそのまま使える理由。TemplateImporter を参照）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace SEEDEditor.Templates;

/// <summary>
/// テンプレートライブラリを走査して、カテゴリとエントリの一覧を提供する。
/// </summary>
public sealed class TemplateLibrary
{
    /// <summary>
    /// 走査から除外する名前の接頭辞。
    /// OS やツールが作る隠しファイル・隠しフォルダ（.DS_Store / .git / .backup など）は
    /// テンプレートとして選ばせる意味が無いので、一覧に出さない。
    /// </summary>
    private const char HiddenNamePrefix = '.';

    /// <summary>ライブラリルートの絶対パス。</summary>
    public string Root { get; }

    /// <summary>カテゴリ一覧（<see cref="TemplateCategoryNames"/> の並び順 → 未知は名前順）。</summary>
    public IReadOnlyList<TemplateCategory> Categories { get; }

    /// <summary>エントリが 1 件も無ければ true（ライブラリが空 / 場所違いの合図）。</summary>
    public bool IsEmpty
    {
        get
        {
            foreach (var c in Categories) if (c.Entries.Count > 0) return false;
            return true;
        }
    }

    /// <summary>走査結果からライブラリを構築する（生成は <see cref="Load"/> から行う）。</summary>
    /// <param name="root">ライブラリルートの絶対パス。</param>
    /// <param name="categories">カテゴリ一覧。</param>
    private TemplateLibrary(string root, IReadOnlyList<TemplateCategory> categories)
    {
        Root       = root;
        Categories = categories;
    }

    // ============================================================
    //  走査
    // ============================================================

    /// <summary>
    /// ライブラリルートを走査して一覧を作る。
    /// </summary>
    /// <param name="root">ライブラリルートの絶対パス。</param>
    /// <returns>
    /// 走査結果。ルートが存在しない場合はカテゴリ 0 件のライブラリを返す
    /// （例外にしないのは、UI 側が「ライブラリが見つかりません」を出せれば十分なため）。
    /// </returns>
    public static TemplateLibrary Load(string root)
    {
        var fullRoot   = Path.GetFullPath(root);
        var categories = new List<TemplateCategory>();

        if (Directory.Exists(fullRoot))
        {
            // ── トップレベルフォルダ = カテゴリ ──────────────────
            foreach (var categoryDir in Directory.EnumerateDirectories(fullRoot))
            {
                var folderName = Path.GetFileName(categoryDir);
                if (IsHidden(folderName)) continue;

                var entries = ScanEntries(categoryDir, folderName);
                categories.Add(new TemplateCategory(
                    folderName,
                    TemplateCategoryNames.GetDisplayName(folderName),
                    entries));
            }

            // ── 表示順を決める（既知カテゴリ優先 → 未知はフォルダ名順） ──
            categories.Sort((a, b) =>
            {
                int byTable = TemplateCategoryNames.GetSortOrder(a.FolderName)
                    .CompareTo(TemplateCategoryNames.GetSortOrder(b.FolderName));
                return byTable != 0
                    ? byTable
                    : string.Compare(a.FolderName, b.FolderName, StringComparison.OrdinalIgnoreCase);
            });
        }

        return new TemplateLibrary(fullRoot, categories);
    }

    /// <summary>
    /// カテゴリフォルダ直下の子（ファイル / フォルダ）をエントリとして列挙する。
    /// </summary>
    /// <param name="categoryDir">カテゴリフォルダの絶対パス。</param>
    /// <param name="folderName">カテゴリのフォルダ名（相対パスの先頭要素になる）。</param>
    /// <returns>表示名の昇順に並べたエントリ一覧。</returns>
    private static List<TemplateEntry> ScanEntries(string categoryDir, string folderName)
    {
        var entries = new List<TemplateEntry>();

        // ── フォルダエントリ（配下すべてで 1 単位） ────────────
        foreach (var dir in Directory.EnumerateDirectories(categoryDir))
        {
            var name = Path.GetFileName(dir);
            if (IsHidden(name)) continue;

            var (count, bytes) = MeasureFolder(dir);
            entries.Add(new TemplateEntry(
                DisplayName: name,
                RelPath:     folderName + "/" + name,
                IsFolder:    true,
                FileCount:   count,
                SizeBytes:   bytes));
        }

        // ── ファイルエントリ ───────────────────────────────────
        foreach (var file in Directory.EnumerateFiles(categoryDir))
        {
            var name = Path.GetFileName(file);
            if (IsHidden(name)) continue;

            long size;
            try { size = new FileInfo(file).Length; }
            catch { size = 0; }     // 読めないファイルもサイズ 0 で一覧には出す

            entries.Add(new TemplateEntry(
                DisplayName: name,
                RelPath:     folderName + "/" + name,
                IsFolder:    false,
                FileCount:   1,
                SizeBytes:   size));
        }

        // フォルダとファイルを混ぜたまま名前順に並べる
        // （fonts/Digital と fonts/misc.ttf のどちらも「1 つのテンプレート」なので、
        //   種別で二分するより名前で探せる方が速い）
        entries.Sort((a, b) =>
            string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    /// <summary>フォルダ配下のファイル数と合計バイト数を測る。</summary>
    /// <param name="dir">測るフォルダの絶対パス。</param>
    /// <returns>(ファイル数, 合計バイト数)。</returns>
    private static (int Count, long Bytes) MeasureFolder(string dir)
    {
        int  count = 0;
        long bytes = 0;
        try
        {
            foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                count++;
                bytes += fi.Length;
            }
        }
        catch
        {
            // アクセスできないフォルダは測れた分だけを返す（一覧表示は続ける）
        }
        return (count, bytes);
    }

    /// <summary>隠し扱いの名前（先頭がドット）かを判定する。</summary>
    /// <param name="name">ファイル名またはフォルダ名。</param>
    /// <returns>隠し扱いなら true。</returns>
    private static bool IsHidden(string name) =>
        name.Length == 0 || name[0] == HiddenNamePrefix;

    // ============================================================
    //  参照
    // ============================================================

    /// <summary>
    /// 相対パスからエントリを引く（UI の選択状態を相対パスで保持するため）。
    /// </summary>
    /// <param name="relPath">ライブラリルート相対パス。</param>
    /// <param name="entry">見つかったエントリ。</param>
    /// <returns>見つかれば true。</returns>
    public bool TryGetEntry(string relPath, out TemplateEntry entry)
    {
        foreach (var category in Categories)
            foreach (var e in category.Entries)
                if (string.Equals(e.RelPath, relPath, StringComparison.OrdinalIgnoreCase))
                {
                    entry = e;
                    return true;
                }

        entry = default;
        return false;
    }
}
