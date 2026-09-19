// ============================================================
//  WaveformThumbnailCacheKey.cs — 波形サムネイル PNG の置き場とファイル名
//
//  【役割】
//  「この音声ファイルの、この大きさの波形サムネイルは、どこに置かれるか」を
//  決める唯一の計算。生成側（WaveformThumbnailRenderer）も探す側（ProjectPanel）も
//  ここを引くので、書いた場所と探す場所がずれることがない。
//
//  【置き場を cache/editor/ にする理由】
//  波形サムネイルはエディタが表示のためだけに作る「利用者別の状態」で、
//  ゲームの実行にも配布物にも不要。視点サイドカー（cache/editor/view/）・
//  VCS の状態（cache/editor/vcs/）・シーンロック（cache/editor/scene_locks/）と
//  同じ区画へ置き、バージョン管理の対象外にする。
//  3D モデルのサムネイル（cache/thumbnails/）と別なのは、あちらが
//  ランタイム（Rust）と共有する取り決めで、場所も名前も勝手に変えられないため。
//
//  【キーの材料】
//  パス＋最終更新時刻＋サイズ＋出力の大きさ。画像・フォントのプレビューと同じ考え方
//  （Assets/AssetPreviewCacheKey.cs）。音声を差し替えたら波形も作り直される。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ProjectPanelLogicTests）から直接リンクするため、
//  WPF 型・NAudio へ依存しない。
// ============================================================

using System;
using System.Globalization;
using System.IO;

namespace SEEDEditor.Assets;

/// <summary>
/// 波形サムネイル PNG のキャッシュ位置を決める計算（状態を持たない静的クラス）。
/// </summary>
public static class WaveformThumbnailCacheKey
{
    // ── キャッシュ場所の規約 ──────────────────────────────────

    /// <summary>
    /// プロジェクト直下のキャッシュフォルダ名。
    /// <c>Project/ProjectPaths.CACHE_DIR_NAME</c> と同じ値。
    /// このファイルは単体テストへ単独でリンクするため <c>ProjectPaths</c> に依存できず
    /// 値を写している（<c>Scene/SceneLock.cs</c> と同じ事情）。**変えるときは両方そろえること**。
    /// </summary>
    public const string CacheDirName = "cache";

    /// <summary>キャッシュのうちエディタ用の区画（視点サイドカー・VCS の状態と同じ階層）。</summary>
    public const string EditorDirName = "editor";

    /// <summary>波形サムネイルを置くサブフォルダ名。</summary>
    public const string WaveformSubdirName = "waveforms";

    /// <summary>生成されるサムネイルの拡張子（先頭ドット付き）。</summary>
    public const string ThumbnailExtension = ".png";

    // ── サイズの規約 ──────────────────────────────────────────

    /// <summary>
    /// 波形サムネイルの既定の横ピクセル数。
    ///
    /// <para>
    /// タイルの表示は 80px 前後なので、高 DPI 環境での拡大に耐える 128px を選んでいる
    /// （3D モデルのサムネイル <c>ModelThumbnailCacheKey.DefaultSizePx</c> と同じ考え方）。
    /// 横ピクセル数はそのまま「波形の列数」になる。
    /// </para>
    /// </summary>
    public const int DefaultWidthPx = 128;

    /// <summary>
    /// 波形サムネイルの既定の縦ピクセル数。
    ///
    /// <para>
    /// 音声に「正方形である必然性」は無いが、タイルのアイコン枠が正方形なので
    /// 同じ縦横にしておくと他の形式と並べたときに大きさが揃う。
    /// </para>
    /// </summary>
    public const int DefaultHeightPx = 128;

    /// <summary>要求できる最小ピクセル数（縦横とも）。</summary>
    public const int MinSizePx = 8;

    /// <summary>要求できる最大ピクセル数（縦横とも）。</summary>
    public const int MaxSizePx = 1024;

    // ── キーの組み立て ────────────────────────────────────────

    /// <summary>出力の大きさをキーへ混ぜるときの書式（{0}=幅 {1}=高さ）。</summary>
    private const string VariantFormat = "wave{0}x{1}";

    /// <summary>
    /// 出力の大きさを表す「派生条件」の文字列を作る。
    /// 同じ音声でも大きさが違えば別のサムネイルになるため、キーに混ぜる必要がある。
    /// </summary>
    /// <param name="widthPx">出力の横ピクセル数。</param>
    /// <param name="heightPx">出力の縦ピクセル数。</param>
    public static string BuildVariant(int widthPx, int heightPx)
        => string.Format(CultureInfo.InvariantCulture, VariantFormat, widthPx, heightPx);

    /// <summary>
    /// 値を明示してキャッシュのファイル名を作る（I/O を含まない形。テストしやすい）。
    /// </summary>
    /// <param name="audioPath">音声ファイルのパス。</param>
    /// <param name="lastWriteUtc">音声ファイルの最終更新時刻（UTC）。</param>
    /// <param name="lengthBytes">音声ファイルのバイト数。</param>
    /// <param name="widthPx">出力の横ピクセル数。</param>
    /// <param name="heightPx">出力の縦ピクセル数。</param>
    /// <returns>ハッシュ 16 桁 + ".png" のファイル名。</returns>
    public static string BuildFileName(
        string? audioPath, DateTime lastWriteUtc, long lengthBytes, int widthPx, int heightPx)
    {
        var key = AssetPreviewCacheKey.Build(
            audioPath ?? string.Empty, lastWriteUtc, lengthBytes, BuildVariant(widthPx, heightPx));
        return AssetPreviewCacheKey.ToFileNameHash(key) + ThumbnailExtension;
    }

    // ── パスの組み立て ────────────────────────────────────────

    /// <summary>
    /// プロジェクトルートから、波形サムネイルの置き場
    /// （<c>&lt;project&gt;/cache/editor/waveforms</c>）を求める。
    /// </summary>
    /// <param name="projectRoot">プロジェクトルートの絶対パス。空なら null を返す。</param>
    /// <returns>置き場の絶対パス。求められないときは <c>null</c>。</returns>
    public static string? CacheDirForProjectRoot(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return null;
        try
        {
            var root = Path.GetFullPath(projectRoot);
            return Path.Combine(root, CacheDirName, EditorDirName, WaveformSubdirName);
        }
        catch
        {
            // 不正文字などでパスとして解釈できない場合はキャッシュを使わない。
            return null;
        }
    }

    /// <summary>
    /// 実ファイルの情報を読んで、キャッシュ PNG の絶対パスを求める。
    /// </summary>
    /// <param name="projectRoot">プロジェクトルートの絶対パス。</param>
    /// <param name="audioPath">音声ファイルの絶対パス。</param>
    /// <param name="widthPx">出力の横ピクセル数。</param>
    /// <param name="heightPx">出力の縦ピクセル数。</param>
    /// <returns>
    /// キャッシュ PNG の絶対パス。プロジェクトルートが分からない、
    /// または音声ファイルの情報が読めないときは <c>null</c>
    /// （＝キャッシュを使わず、その場で生成して捨てる）。
    /// </returns>
    public static string? PathForFile(
        string? projectRoot, string? audioPath, int widthPx, int heightPx)
    {
        var dir = CacheDirForProjectRoot(projectRoot);
        if (dir is null || string.IsNullOrWhiteSpace(audioPath)) return null;

        try
        {
            var info = new FileInfo(audioPath);
            if (!info.Exists) return null;
            return Path.Combine(
                dir, BuildFileName(audioPath, info.LastWriteTimeUtc, info.Length, widthPx, heightPx));
        }
        catch
        {
            // アクセス権が無い等。キャッシュ無しとして扱う。
            return null;
        }
    }

    /// <summary>
    /// 要求された大きさを受理できる範囲へ丸める。
    /// </summary>
    /// <param name="sizePx">要求値。</param>
    /// <returns><see cref="MinSizePx"/>〜<see cref="MaxSizePx"/> に収めた値。</returns>
    public static int ClampSize(int sizePx)
        => sizePx < MinSizePx ? MinSizePx : (sizePx > MaxSizePx ? MaxSizePx : sizePx);
}
