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

    /// <summary>
    /// 3D モデルをランタイムにオフスクリーン描画させたサムネイル。
    ///
    /// <para>
    /// 他の 2 種と違い、生成にランタイム（wgpu）が要る。エディタ単独では描けないので
    /// 「キャッシュ PNG があれば出す／無ければランタイムへ頼む／ランタイムが居なければ
    /// 形式アイコンのまま」という段階的な扱いになる（<c>ProjectPanel</c> 側）。
    /// </para>
    /// </summary>
    Model,

    /// <summary>
    /// 音声を復号して描いた波形サムネイル（列ごとの最小・最大＝ピーク）。
    ///
    /// <para>
    /// 生成はエディタ内（NAudio で復号 → WPF で描画）で完結する。
    /// 長い BGM でも全サンプルをメモリへ載せないよう、読みながら逐次ダウンサンプルする
    /// （計算は <c>Assets/WaveformPeaks.cs</c>、描画とキャッシュは
    ///  <c>Controls/WaveformThumbnailRenderer.cs</c>）。
    /// </para>
    /// </summary>
    AudioWaveform,
}

/// <summary>
/// 「拡張子 → どのプレビューを生成できるか」の唯一の対応表。
///
/// プロジェクトパネルのタイル生成は、この表の答えだけを見て
///   1. 画像サムネイルを非同期生成する
///   2. フォントサムネイルを遅延生成する
///   3. モデルサムネイルをランタイムへ要求する（キャッシュにあればそれを出す）
///   4. 何もしない（形式アイコンのまま）
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
    /// モデルサムネイル（ランタイムによるオフスクリーン描画）を生成できる拡張子。
    ///
    /// <para>
    /// ランタイムのモデルローダ（<c>runtime/src/engine/core/loader/mod.rs</c> の
    /// <c>load_model</c>）が実際に読める形式と一致させること。
    /// <c>.fbx</c> はローダが明示的に非対応なのでここには入れない
    /// （入れるとタイルごとに必ず失敗応答が返る）。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ModelThumbnail = new(StringComparer.OrdinalIgnoreCase)
    {
        ".glb", ".gltf", ".obj",
    };

    /// <summary>
    /// 波形サムネイルを生成でき、かつ「音声ファイル」として扱う拡張子。
    ///
    /// <para>
    /// <b>エディタ全体で音声かどうかを判定する唯一の集合。</b>
    /// 無音カット（<c>Audio/AudioSilenceTrimmer.IsAudioFile</c>）も
    /// タイルの試聴ボタン（<c>Panels/ProjectPanel.AudioPreview.cs</c>）も
    /// 関連付けで開く判定（<see cref="ShellOpenCatalog"/> の音声分類）も、
    /// それぞれで拡張子を並べ直さずここを引く。
    /// </para>
    ///
    /// <para>
    /// 実際に復号できるかは形式と環境による（<c>.ogg</c> は既定では開けないことが多い）。
    /// 可否は生成・再生側が実際に開いて判断し、失敗しても形式アイコンのまま据え置く
    /// ＝ここは「音声として扱う候補」であって「必ず鳴る形式」ではない。
    /// </para>
    ///
    /// <para>
    /// なお <c>Controls/AudioDictionaryCatalog.AudioExtensions</c> は別物として残している。
    /// あちらは「AudioComponent の音声パス欄が受け付ける並び」で、
    /// ランタイム側の定数との一致を <c>editor/tests/AudioDictionaryTests</c> が
    /// 順序込みで固定しているため、こちらの集合（順序を持たない）とは役割が違う。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> AudioWaveform = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".ogg", ".flac",
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
        if (ModelThumbnail.Contains(extension)) return AssetPreviewKind.Model;
        if (AudioWaveform.Contains(extension))  return AssetPreviewKind.AudioWaveform;
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

    /// <summary>モデルサムネイル（ランタイム描画）を生成できる拡張子か。</summary>
    /// <param name="extension">先頭ドット付きの拡張子。</param>
    public static bool SupportsModelThumbnail(string? extension)
        => !string.IsNullOrEmpty(extension) && ModelThumbnail.Contains(extension);

    /// <summary>音声ファイルとして扱う拡張子か（波形サムネイル・試聴ボタンの対象）。</summary>
    /// <param name="extension">先頭ドット付きの拡張子。</param>
    public static bool IsAudioExtension(string? extension)
        => !string.IsNullOrEmpty(extension) && AudioWaveform.Contains(extension);

    /// <summary>音声ファイルか（拡張子だけで判定する。中身は見ない）。</summary>
    /// <param name="path">ファイルパス。null・空可。</param>
    public static bool IsAudioPath(string? path)
        => !string.IsNullOrEmpty(path) && IsAudioExtension(Path.GetExtension(path));

    /// <summary>画像ファイルとして扱う拡張子か（サムネイル対象 ∪ 寸法取得対象）。</summary>
    /// <remarks>
    /// サムネイルを描けるか（<see cref="SupportsImageThumbnail"/>）より広い。
    /// 「この形式を画像ビューアへ渡してよいか」の判定に使う（<see cref="ShellOpenCatalog"/>）。
    /// </remarks>
    /// <param name="extension">先頭ドット付きの拡張子。</param>
    public static bool IsImageExtension(string? extension)
        => !string.IsNullOrEmpty(extension)
           && (ImageThumbnail.Contains(extension) || PixelSizeProbe.Contains(extension));

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

    /// <summary>モデルサムネイル対象の拡張子一覧（アイコン表と共有するため）。</summary>
    public static IReadOnlyCollection<string> ModelThumbnailExtensions => ModelThumbnail.ToArray();

    /// <summary>音声拡張子の一覧（アイコン表・関連付け表と共有するため）。</summary>
    public static IReadOnlyCollection<string> AudioExtensions => AudioWaveform.ToArray();

    /// <summary>画像拡張子の一覧（関連付け表と共有するため。サムネイル対象 ∪ 寸法取得対象）。</summary>
    public static IReadOnlyCollection<string> ImageExtensions
        => ImageThumbnail.Union(PixelSizeProbe, StringComparer.OrdinalIgnoreCase).ToArray();
}
