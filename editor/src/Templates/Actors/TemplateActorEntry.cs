// ============================================================
//  TemplateActorEntry.cs — テンプレートアクタ 1 件とカテゴリ（値オブジェクト）
//
//  【役割】
//  カタログ（template_actors.json）を読んだ結果を運ぶだけの型。
//  読み込み（TemplateActorCatalog）・検索（TemplateActorSearch）・追加（TemplateActorInstaller）・
//  表示（TemplateActorPickerWindow）がこの形だけを介してやり取りする。
//
//  パスはすべて**ライブラリルート相対**（'/' 区切り）に揃えてある。
//  カタログの中ではカタログのフォルダ基準で書くが、読み込み時に "ui/" のような
//  フォルダ名を前に付けて変換する（ライブラリはアセットルートと同じ構成なので、
//  この相対パスがそのままプロジェクトのアセットルート相対パスにもなる）。
// ============================================================

using System.Collections.Generic;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// テンプレートアクタ 1 件（カタログの entries の 1 要素を解決したもの）。
/// </summary>
/// <param name="Name">表示名（例「ボタン」）。</param>
/// <param name="Description">一覧に出す説明文。</param>
/// <param name="CategoryKey">カテゴリのキー（'/' 区切り。例 "UI/基本"）。</param>
/// <param name="CategorySegments">カテゴリの段（例 ["UI", "基本"]）。1〜2 段。</param>
/// <param name="Tags">検索語（日本語と英語）。</param>
/// <param name="TemplateRelPath">テンプレート（.actor / .actor2d）のライブラリ相対パス。</param>
/// <param name="ThumbnailRelPath">
/// サムネイル画像のライブラリ相対パス。<see cref="ThumbnailExplicit"/> が false のときは
/// 規約から組み立てた「あるはずの場所」で、実在は保証しない（無ければ頭文字の板を出す）。
/// </param>
/// <param name="ThumbnailExplicit">カタログの thumbnail 欄で明示されたパスなら true。</param>
/// <param name="RequiredRelPaths">実行時に読むので一緒にコピーするファイルのライブラリ相対パス。</param>
/// <param name="Is2D">ルートが 2D アクタ（actor_kind = Actor2D）なら true。</param>
/// <param name="CatalogRelPath">このエントリを書いたカタログのライブラリ相対パス（警告の出どころ表示用）。</param>
public sealed record TemplateActorEntry(
    string Name,
    string Description,
    string CategoryKey,
    IReadOnlyList<string> CategorySegments,
    IReadOnlyList<string> Tags,
    string TemplateRelPath,
    string ThumbnailRelPath,
    bool ThumbnailExplicit,
    IReadOnlyList<string> RequiredRelPaths,
    bool Is2D,
    string CatalogRelPath)
{
    /// <summary>画面に出すカテゴリの表記（例「UI › 基本」）。</summary>
    public string CategoryDisplay =>
        string.Join(TemplateActorCatalogFormat.CategoryDisplaySeparator, CategorySegments);

    /// <summary>
    /// サムネイルが無いときに灰色の板へ置く頭文字（表示名の先頭の 1 文字）。
    /// サロゲートペア（絵文字や一部の漢字）を途中で切らないよう、文字要素単位で取る。
    /// </summary>
    public string Initial
    {
        get
        {
            if (string.IsNullOrEmpty(Name)) return "?";
            var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(Name);
            return enumerator.MoveNext() ? (string)enumerator.Current : "?";
        }
    }
}

/// <summary>
/// テンプレートアクタのカテゴリ 1 件（左の木の 1 行）。
/// </summary>
/// <param name="Key">カテゴリのキー（例 "UI" / "UI/基本"）。</param>
/// <param name="Name">この段の名前（例「基本」）。</param>
/// <param name="ParentKey">親カテゴリのキー（1 段目なら null）。</param>
/// <param name="Depth">段の深さ（1 段目 = 1、2 段目 = 2）。</param>
public sealed record TemplateActorCategory(
    string Key,
    string Name,
    string? ParentKey,
    int Depth);
