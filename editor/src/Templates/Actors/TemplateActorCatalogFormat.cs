// ============================================================
//  TemplateActorCatalogFormat.cs — テンプレートアクタのカタログ形式の定数
//
//  【役割】
//  カタログ（templates/<フォルダ>/template_actors.json）の欄名・区切り・上限・
//  サムネイルの規約をここ 1 か所に集める。読み込み（TemplateActorCatalog）・
//  表示（TemplateActorPickerWindow）・文書（docs/template_library.md §9）が同じ値を指す。
//
//  【カタログの形（format_version 1）】
//    {
//      "format_version": 1,
//      "sort_order": 10,                       … フォルダ間の並び（小さいほど先）
//      "entries": [
//        {
//          "path": "prefabs/button.actor",     … カタログのフォルダからの相対パス
//          "name": "ボタン",                    … 表示名
//          "description": "…",                 … 一覧に出す説明
//          "category": "UI/基本",               … 2 段までのカテゴリ（'/' 区切り）
//          "tags": ["button", "ボタン"],        … 検索語（日本語と英語）
//          "thumbnail": "",                    … サムネイル（相対パス。空なら既定の探し方）
//          "requires": ["prefabs/x.actor"]     … 実行時に読むので一緒にコピーするファイル
//        }
//      ]
//    }
// ============================================================

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// テンプレートアクタのカタログ形式（欄名・規約）の定数表。
/// </summary>
public static class TemplateActorCatalogFormat
{
    // ============================================================
    //  版
    // ============================================================

    /// <summary>このエディタが読めるカタログの版。これより新しい版のカタログは読まずに警告する。</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>版の欄名（新しく足す形式の流儀 <c>format_version</c> に合わせる）。</summary>
    public const string FormatVersionKey = "format_version";

    // ============================================================
    //  トップレベルの欄
    // ============================================================

    /// <summary>フォルダ間の並び順の欄名（整数。小さいほど先に出る）。</summary>
    public const string SortOrderKey = "sort_order";

    /// <summary><see cref="SortOrderKey"/> が無いカタログの並び順（既知のカタログより後ろに回す）。</summary>
    public const int DefaultSortOrder = 1000;

    /// <summary>エントリ配列の欄名。</summary>
    public const string EntriesKey = "entries";

    // ============================================================
    //  エントリの欄
    // ============================================================

    /// <summary>テンプレート（.actor / .actor2d）の相対パス（カタログのフォルダ基準）。必須。</summary>
    public const string PathKey = "path";

    /// <summary>表示名。空ならファイル名（拡張子なし）を使う。</summary>
    public const string NameKey = "name";

    /// <summary>一覧に出す説明文。</summary>
    public const string DescriptionKey = "description";

    /// <summary>カテゴリ（'/' 区切りで <see cref="MaxCategoryDepth"/> 段まで）。</summary>
    public const string CategoryKey = "category";

    /// <summary>検索語の配列。</summary>
    public const string TagsKey = "tags";

    /// <summary>サムネイル画像の相対パス（カタログのフォルダ基準）。空なら既定の探し方。</summary>
    public const string ThumbnailKey = "thumbnail";

    /// <summary>
    /// 実行時にスクリプトが読むため、テンプレートの JSON に現れなくても一緒にコピーする
    /// ファイルの相対パス（カタログのフォルダ基準）の配列。
    /// 例: 画面のスタック（SEED.UI.ScreenStack）は既定で prefabs/screen_frame.actor を読む。
    /// </summary>
    public const string RequiresKey = "requires";

    // ============================================================
    //  カテゴリ
    // ============================================================

    /// <summary>カテゴリの段の区切り（データ上の表記）。</summary>
    public const char CategorySeparator = '/';

    /// <summary>カテゴリの段の区切り（画面に出す表記）。</summary>
    public const string CategoryDisplaySeparator = " › ";

    /// <summary>カテゴリの最大段数（左の木を 2 段で収めるため）。超えた段は最後の段へまとめる。</summary>
    public const int MaxCategoryDepth = 2;

    /// <summary>カテゴリが空のエントリを入れるカテゴリ名。</summary>
    public const string UncategorizedName = "その他";

    // ============================================================
    //  サムネイルの規約
    // ============================================================

    /// <summary>
    /// サムネイルの既定の拡張子。<c>thumbnail</c> 欄が空のとき
    /// <c>&lt;カタログのフォルダ&gt;/thumbnails/&lt;テンプレートのファイル名（拡張子なし）&gt;.png</c> を探す。
    /// </summary>
    public const string ThumbnailExtension = ".png";

    /// <summary>サムネイル画像の推奨の一辺（px。正方形）。一覧では縮めて表示する。</summary>
    public const int RecommendedThumbnailSizePx = 192;

    // ============================================================
    //  テンプレートの種別
    // ============================================================

    /// <summary>アクタの種別の欄名（.actor のルート。無ければ 3D）。</summary>
    public const string ActorKindKey = "actor_kind";

    /// <summary>2D アクタを表す <see cref="ActorKindKey"/> の値（ランタイムの ActorKind::Actor2D の直列化名）。</summary>
    public const string ActorKind2D = "Actor2D";

    /// <summary>テンプレートとして受け付ける拡張子（小文字）。</summary>
    public static readonly string[] TemplateExtensions = [".actor", ".actor2d"];
}
