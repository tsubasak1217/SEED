// ============================================================
//  TemplateLibraryMetadata.cs — テンプレートライブラリの「中身ではない」ファイル名の表
//
//  【役割】
//  templates/<カテゴリ>/ の直下には、テンプレートそのもの（シーン・プレハブ・テクスチャ…）の
//  ほかに、ライブラリ自身を説明するためのファイルを置く:
//    template_actors.json … テンプレートアクタのカタログ（docs/template_library.md §9）
//    thumbnails/          … テンプレートアクタの見本のサムネイル画像
//  これらはプロジェクトへコピーする「テンプレート」ではないので、
//  インポート画面（TemplateLibrary の走査）には出さない。
//
//  名前をここ 1 か所に置き、走査の除外（TemplateLibrary）と
//  カタログの読み込み（Actors/TemplateActorCatalog）の両方がこの表を見る。
//  名前を変えるときはここだけを直せばよい。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Templates;

/// <summary>
/// テンプレートライブラリのメタデータ（テンプレートではない付帯ファイル）の名前を持つ静的な表。
/// </summary>
public static class TemplateLibraryMetadata
{
    /// <summary>
    /// テンプレートアクタのカタログのファイル名。
    /// ライブラリのトップレベルフォルダ（templates/ui/ など）の直下に 1 つ置ける。
    /// </summary>
    public const string TemplateActorCatalogFileName = "template_actors.json";

    /// <summary>
    /// テンプレートアクタのサムネイル画像を置くフォルダ名。
    /// カタログと同じトップレベルフォルダの直下に置く（templates/ui/thumbnails/）。
    /// </summary>
    public const string ThumbnailFolderName = "thumbnails";

    /// <summary>
    /// インポート画面の一覧に出さない名前（カテゴリフォルダ直下の子の名前と照合する）。
    /// 大文字小文字は区別しない（Windows のファイル名の扱いに合わせる）。
    /// </summary>
    private static readonly HashSet<string> HiddenEntryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            TemplateActorCatalogFileName,
            ThumbnailFolderName,
        };

    /// <summary>
    /// カテゴリフォルダ直下の子が、ライブラリのメタデータ（インポート対象外）かを判定する。
    /// </summary>
    /// <param name="entryName">ファイル名またはフォルダ名（パスではなく名前だけ）。</param>
    /// <returns>メタデータならインポートの一覧に出さないので true。</returns>
    public static bool IsMetadataEntry(string entryName) => HiddenEntryNames.Contains(entryName);
}
