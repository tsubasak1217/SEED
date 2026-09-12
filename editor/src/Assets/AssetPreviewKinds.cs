using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SEEDEditor.Assets;

/// <summary>
/// ファイルのタイルに「形式アイコンの代わりに出せるプレビュー」の種別。
/// </summary>
public enum AssetPreviewKind
{
    /// <summary>プレビューを持たない（常に形式アイコンのまま）。</summary>
    None,

    /// <summary>画像そのものを縮小したサムネイル。</summary>
    Image,

    /// <summary>フォントで描いたサンプル文字列のサムネイル。</summary>
    Font,
}

/// <summary>
/// 「拡張子 → どのプレビューを生成できるか」の唯一の対応表。
///
/// プロジェクトパネルのタイル生成は、この表の答えだけを見て
///   1. 画像サムネイルを非同期生成する
///   2. フォントサムネイルを遅延生成する
///   3. 何もしない（形式アイコンのまま）
/// を振り分ける。新しい形式へ対応するときは、パネル側のコードではなく
/// この表に 1 行足すこと（データドリブン）。
///
/// アイコン画像そのものの対応表は <c>Controls/FileTypeIcons.cs</c> が持つ。
/// あちらは「何を描くか」、こちらは「プレビューを作れるか」で役割が分かれている。
///
/// WPF に依存しない判定だけを置くこと
/// （editor/tests/ProjectPanelLogicTests が直接リンクして検証している）。
/// </summary>
public static class AssetPreviewKinds
{
    /// <summary>
    /// 画像サムネイル（実画像の縮小）を生成できる拡張子。
    /// WIC が読めない形式（.tga / .dds 等）も候補には含める。
    /// 実際に読めるかは生成側が試し、失敗したら形式アイコンのまま据え置く。
    /// </summary>
    private static readonly HashSet<string> ImageThumbnail = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tga", ".hdr", ".exr", ".webp",
    };

    /// <summary>
    /// フォントサムネイル（そのフォントで描いたサンプル文字列）を生成できる拡張子。
    /// .ttc（コレクション）は先頭フェイスで描く。
    /// </summary>
    private static readonly HashSet<string> FontThumbnail = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf", ".otf", ".ttc",
    };

    /// <summary>
    /// ピクセル寸法（幅×高さ）の取得を試す拡張子。
    ///
    /// ヘッダだけを読む軽い処理なので、WIC が標準で読めない形式（.tga / .dds / .exr 等）も
    /// 候補に入れておく。読めなければ取得側が「寸法なし」として扱い、キャプションは出さない。
    /// </summary>
    private static readonly HashSet<string> PixelSizeProbe = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico",
        ".tga", ".webp", ".dds", ".hdr", ".exr",
    };

    /// <summary>
    /// 拡張子に対応するプレビュー種別を返す。
    /// </summary>
    /// <param name="extension">先頭ドット付きの拡張子（大文字小文字は問わない）。null・空可。</param>
    public static AssetPreviewKind Of(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return AssetPreviewKind.None;
        if (ImageThumbnail.Contains(extension)) return AssetPreviewKind.Image;
        if (FontThumbnail.Contains(extension))  return AssetPreviewKind.Font;
        return AssetPreviewKind.None;
    }

    /// <summary>ファイルパスからプレビュー種別を返す（拡張子を取り出して <see cref="Of"/> を引く）。</summary>
    /// <param name="path">ファイルパス。null・空可。</param>
    public static AssetPreviewKind OfPath(string? path)
        => string.IsNullOrEmpty(path) ? AssetPreviewKind.None : Of(Path.GetExtension(path));

    /// <summary>画像サムネイルを生成できる拡張子か。</summary>
    /// <param name="extension">先頭ドット付きの拡張子。</param>
    public static bool SupportsImageThumbnail(string? extension)
        => !string.IsNullOrEmpty(extension) && ImageThumbnail.Contains(extension);

    /// <summary>フォントサムネイルを生成できる拡張子か。</summary>
    /// <param name="extension">先頭ドット付きの拡張子。</param>
    public static bool SupportsFontThumbnail(string? extension)
        => !string.IsNullOrEmpty(extension) && FontThumbnail.Contains(extension);

    /// <summary>ピクセル寸法の取得を試す価値がある拡張子か。</summary>
    /// <param name="extension">先頭ドット付きの拡張子。</param>
    public static bool SupportsPixelSize(string? extension)
        => !string.IsNullOrEmpty(extension) && PixelSizeProbe.Contains(extension);

    /// <summary>ピクセル寸法の取得を試す価値があるファイルパスか。</summary>
    /// <param name="path">ファイルパス。</param>
    public static bool SupportsPixelSizePath(string? path)
        => !string.IsNullOrEmpty(path) && SupportsPixelSize(Path.GetExtension(path));

    /// <summary>フォント拡張子の一覧（アイコン表など、他所から同じ集合を参照するため）。</summary>
    public static IReadOnlyCollection<string> FontExtensions => FontThumbnail.ToArray();

    /// <summary>画像サムネイル対象の拡張子一覧（アイコン表と共有するため）。</summary>
    public static IReadOnlyCollection<string> ImageThumbnailExtensions => ImageThumbnail.ToArray();
}
