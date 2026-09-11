// ============================================================
//  TemplateEntry.cs — テンプレートライブラリの一覧データ（値オブジェクト）
//
//  【役割】
//  ライブラリを走査した結果（カテゴリとエントリ）を保持するだけの型。
//  走査（TemplateLibrary）・インポート（TemplateImporter）・表示（TemplateImportWindow）が
//  この形だけを介してやり取りする。
//
//  【エントリの粒度】
//  「カテゴリフォルダの直下の子」1 つが 1 エントリ。ファイルでもフォルダでもよい。
//    scenes/physicsTest.scene … ファイルエントリ
//    fonts/Digital            … フォルダエントリ（配下すべてが 1 単位）
//  この粒度にしているのは、フォント・地形のように「フォルダ 1 式でようやく使える」
//  テンプレートと、シーン・シェーダのように 1 ファイルで完結するテンプレートが
//  ライブラリの中に混在しているため。
// ============================================================

using System.Collections.Generic;

namespace SEEDEditor.Templates;

/// <summary>
/// テンプレートライブラリのエントリ 1 件（カテゴリフォルダ直下の子 1 つ）。
/// </summary>
/// <param name="DisplayName">UI に出す名前（ファイル名 / フォルダ名そのもの）。</param>
/// <param name="RelPath">
/// ライブラリルートからの相対パス（'/' 区切り。例 "scenes/physicsTest.scene"）。
/// ライブラリはアセットルートと同じ構成なので、これがそのままコピー先の相対パスになる。
/// </param>
/// <param name="IsFolder">フォルダエントリなら true。</param>
/// <param name="FileCount">エントリが含むファイル数（ファイルエントリは 1）。</param>
/// <param name="SizeBytes">エントリが含むファイルの合計バイト数（依存は含まない概算）。</param>
public readonly record struct TemplateEntry(
    string DisplayName,
    string RelPath,
    bool IsFolder,
    int FileCount,
    long SizeBytes);

/// <summary>
/// テンプレートライブラリのカテゴリ 1 件（ライブラリ直下のトップレベルフォルダ）。
/// </summary>
/// <param name="FolderName">フォルダ名（例 "scenes"）。相対パスの先頭要素でもある。</param>
/// <param name="DisplayName">UI に出す表示名（<see cref="TemplateCategoryNames"/> による）。</param>
/// <param name="Entries">このカテゴリのエントリ（表示名の昇順）。</param>
public sealed record TemplateCategory(
    string FolderName,
    string DisplayName,
    IReadOnlyList<TemplateEntry> Entries)
{
    /// <summary>カテゴリに含まれる全エントリのファイル数合計。</summary>
    public int TotalFileCount
    {
        get
        {
            int total = 0;
            foreach (var e in Entries) total += e.FileCount;
            return total;
        }
    }

    /// <summary>カテゴリに含まれる全エントリのバイト数合計。</summary>
    public long TotalSizeBytes
    {
        get
        {
            long total = 0;
            foreach (var e in Entries) total += e.SizeBytes;
            return total;
        }
    }
}
