// ============================================================
//  TemplateActorListItem.cs — テンプレートアクタの窓の一覧の 1 行（表示モデル）
//
//  【役割】
//  TemplateActorEntry（データ）を、一覧の 1 行の見た目に要る値へ直すだけの型。
//  サムネイルの枠・名前・説明・カテゴリと 2D/3D の別・ツールチップを持つ。
//  サムネイルが無いときは Thumbnail が null になり、窓は頭文字の板を出す。
// ============================================================

using System.Windows;
using System.Windows.Media;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// 一覧の 1 行の表示モデル。
/// </summary>
public sealed class TemplateActorListItem
{
    /// <summary>元のエントリ。</summary>
    public TemplateActorEntry Entry { get; }

    /// <summary>サムネイル画像（無ければ null）。</summary>
    public ImageSource? Thumbnail { get; }

    /// <summary>表示名。</summary>
    public string Name => Entry.Name;

    /// <summary>説明文。</summary>
    public string Description => Entry.Description;

    /// <summary>頭文字の板に置く文字。</summary>
    public string Initial => Entry.Initial;

    /// <summary>補足の行（カテゴリと 2D/3D の別）。</summary>
    public string Detail => $"{Entry.CategoryDisplay}　・　{(Entry.Is2D ? "2D" : "3D")}";

    /// <summary>頭文字の板を見せるか（画像があるときは隠す）。</summary>
    public Visibility PlaceholderVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>サムネイルの枠のツールチップ（画像が無いときは置き場所の規約を教える）。</summary>
    public string ThumbnailHint { get; }

    /// <summary>行のツールチップ（説明の全文とテンプレートの場所）。</summary>
    public string RowToolTip => $"{Entry.Description}\n\nテンプレート: {Entry.TemplateRelPath}";

    /// <summary>
    /// 表示モデルを作る（サムネイルはここで読む）。
    /// </summary>
    /// <param name="entry">元のエントリ。</param>
    /// <param name="libraryRoot">ライブラリルートの絶対パス（サムネイルの場所の解決に使う）。</param>
    public TemplateActorListItem(TemplateActorEntry entry, string libraryRoot)
    {
        Entry = entry;
        var thumbnailPath = AssetPathUtil.ToAbsolute(libraryRoot, entry.ThumbnailRelPath);
        Thumbnail = TemplateActorThumbnails.Load(thumbnailPath);
        ThumbnailHint = Thumbnail is not null
            ? $"サムネイル: {entry.ThumbnailRelPath}"
            : $"サムネイル未設定。templates/{entry.ThumbnailRelPath} に " +
              $"{TemplateActorCatalogFormat.RecommendedThumbnailSizePx}×{TemplateActorCatalogFormat.RecommendedThumbnailSizePx} の PNG を置くと表示されます";
    }

    /// <summary>このテンプレートの見本の画像が実在するか（窓の件数表示用）。</summary>
    public bool HasThumbnail => Thumbnail is not null;
}
