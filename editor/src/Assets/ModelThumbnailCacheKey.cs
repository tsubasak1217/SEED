using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SEEDEditor.Assets;

/// <summary>
/// 3D モデルのサムネイル PNG が置かれる場所を決める、唯一のキャッシュキー計算。
///
/// <para>
/// プロジェクトパネルのモデルサムネイルは「エディタがキャッシュ PNG を探す →
/// 無ければランタイムへ生成を頼む → ランタイムが PNG を書く → エディタが表示する」
/// という往復で動く。<b>探す側（このクラス）と書く側（ランタイム）が同じ規則で
/// 同じパスを出さなければ、エディタは永遠にキャッシュを見つけられず、
/// 毎回ランタイムへ生成を頼み続ける</b>（表示は出るが、起動のたびに全部描き直しになる）。
/// </para>
///
/// <para>
/// そのため、ここは Rust 側 <c>runtime/src/engine/core/renderer/thumbnail/cache_key.rs</c> の
/// 逐語的な写しである。ハッシュに FNV-1a 64bit を使うのも同じ理由で、
/// .NET の <c>string.GetHashCode</c> も Rust の <c>DefaultHasher</c> も
/// 相手の言語で再現できないため使えない。FNV-1a は仕様が数行しかなく、
/// どの言語でもビット単位に同じ値を出せる（暗号強度は不要。必要なのは一致だけ）。
/// </para>
///
/// <para>
/// 同じ入力から同じ文字列・同じファイル名が出ることは、両言語の単体テストで固定してある
/// （Rust: cache_key.rs の <c>fixed_input_produces_the_agreed_file_name</c> /
///  C#: editor/tests/ProjectPanelLogicTests）。
/// <b>どちらかを変えるときは必ず両方のテストを同時に直すこと。</b>
/// </para>
///
/// <para>
/// WPF に依存しない計算だけを置くこと
/// （editor/tests/ProjectPanelLogicTests が直接リンクして検証している）。
/// </para>
/// </summary>
public static class ModelThumbnailCacheKey
{
    // ── キャッシュ場所の規約 ──────────────────────────────────

    /// <summary>
    /// プロジェクト直下のキャッシュフォルダ名。
    /// ランタイムの <c>asset_cache.rs</c> / <c>ProjectPaths.CACHE_DIR_NAME</c> と同じ値。
    /// </summary>
    public const string CacheDirName = "cache";

    /// <summary>キャッシュフォルダの下でサムネイル PNG を置くサブフォルダ名。</summary>
    public const string ThumbnailSubdirName = "thumbnails";

    /// <summary>生成されるサムネイルの拡張子（先頭ドット付き）。</summary>
    public const string ThumbnailExtension = ".png";

    // ── サイズの規約 ──────────────────────────────────────────

    /// <summary>
    /// サムネイル 1 辺の既定ピクセル数。
    ///
    /// <para>
    /// <b>この値の所有者はエディタ（＝サイズを選ぶ側）である。</b>
    /// ランタイムは受け取った値の範囲を検証するだけで、既定値を持たない
    /// （両方に既定値を置くと、片方だけ変えたときに静かにずれるため）。
    /// </para>
    ///
    /// <para>
    /// タイルの表示は 80px 前後なので、高 DPI 環境での拡大に耐える 128px を選んでいる。
    /// </para>
    /// </summary>
    public const int DefaultSizePx = 128;

    /// <summary>要求できる 1 辺の最小ピクセル数（ランタイム <c>MIN_SIZE_PX</c> と一致）。</summary>
    public const int MinSizePx = 16;

    /// <summary>要求できる 1 辺の最大ピクセル数（ランタイム <c>MAX_SIZE_PX</c> と一致）。</summary>
    public const int MaxSizePx = 512;

    // ── ハッシュの規約 ────────────────────────────────────────

    /// <summary>FNV-1a 64bit のオフセット基底値（仕様値）。</summary>
    private const ulong FnvOffsetBasis = 0xcbf29ce484222325UL;

    /// <summary>FNV-1a 64bit の素数（仕様値）。</summary>
    private const ulong FnvPrime = 0x00000100000001b3UL;

    /// <summary>アセット仮想パスの接頭辞。キー計算前に取り除く。</summary>
    private const string AssetsScheme = "assets://";

    /// <summary>キー文字列の各項目を区切る文字（パスに現れない文字を選んである）。</summary>
    private const char KeyFieldSeparator = '|';

    /// <summary>ハッシュを 16 桁の小文字 16 進で書き出す書式。</summary>
    private const string HashFormat = "x16";

    // ── 正規化とハッシュ ──────────────────────────────────────

    /// <summary>
    /// アセットパスを、Rust 側と完全に同じ結果になる形へ正規化する。
    ///
    /// <list type="number">
    ///   <item><description><c>assets://</c> 接頭辞を取り除く。</description></item>
    ///   <item><description>区切りを <c>/</c> に統一する。</description></item>
    ///   <item><description>先頭の余分な <c>/</c> を落とす。</description></item>
    ///   <item><description><b>ASCII 範囲だけ</b>小文字化する。</description></item>
    /// </list>
    ///
    /// <para>
    /// 小文字化を ASCII に限る理由: Windows のファイル名は大小を区別しないので
    /// 小文字化そのものは要るが、Unicode 全体の小文字化は言語ごとに結果が違う
    /// （例: <c>İ</c> U+0130 は .NET の <c>ToLowerInvariant</c> で 1 文字、
    ///  Rust の <c>to_lowercase</c> で 2 文字になる）。
    /// 日本語などの非 ASCII 文字はそもそも大小の区別を持たないため、
    /// ASCII に限れば「両言語で必ず一致」と「実用上の大小無視」を同時に満たせる。
    /// </para>
    /// </summary>
    /// <param name="path">アセット相対パスまたは <c>assets://</c> 仮想パス。null・空可。</param>
    /// <returns>正規化済みのパス文字列（null 入力なら空文字）。</returns>
    public static string NormalizeAssetPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        var text = path;
        if (text.StartsWith(AssetsScheme, StringComparison.Ordinal))
            text = text[AssetsScheme.Length..];

        var builder = new StringBuilder(text.Length);
        // 先頭のスラッシュ（区切り統一後に現れるものを含む）を読み飛ばすためのフラグ
        bool leading = true;
        foreach (var raw in text)
        {
            var c = raw == '\\' ? '/' : raw;
            if (leading)
            {
                if (c == '/') continue;
                leading = false;
            }
            // ASCII の大文字だけを小文字へ倒す（非 ASCII はそのまま）
            if (c is >= 'A' and <= 'Z') c = (char)(c + ('a' - 'A'));
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>
    /// FNV-1a 64bit ハッシュ。Rust 側と 1 ビットも違わないことが要件。
    /// </summary>
    /// <param name="bytes">ハッシュ対象のバイト列（キー文字列の UTF-8 表現）。</param>
    public static ulong Fnv1a64(ReadOnlySpan<byte> bytes)
    {
        var hash = FnvOffsetBasis;
        foreach (var b in bytes)
        {
            hash ^= b;
            // C# の ulong 乗算は既定でラップする（Rust の wrapping_mul と同じ挙動）
            unchecked { hash *= FnvPrime; }
        }
        return hash;
    }

    // ── キーとファイル名 ──────────────────────────────────────

    /// <summary>
    /// キャッシュキーの素になる正規化済み文字列を組み立てる。
    ///
    /// <para>
    /// 書式がそのままランタイムとの契約になる。変更するときは
    /// Rust の <c>ThumbnailCacheKey::to_key_string</c> と両方のテストを同時に直すこと。
    /// </para>
    /// </summary>
    /// <param name="assetPath">アセット相対パス（<c>assets://</c> 付きでも可）。</param>
    /// <param name="modifiedUnixSeconds">
    /// 元ファイルの最終更新時刻（Unix エポックからの秒）。
    /// ミリ秒以下を使わないのは、.NET（100ns 刻みの FILETIME 由来）と Rust で
    /// 端数の丸めが一致する保証がないため。秒までなら両者とも同じ整数になる。
    /// </param>
    /// <param name="fileSizeBytes">元ファイルのバイト数。</param>
    /// <param name="sizePx">要求するサムネイル 1 辺のピクセル数。</param>
    public static string BuildKeyString(
        string? assetPath, long modifiedUnixSeconds, long fileSizeBytes, int sizePx)
    {
        // 文化圏によって桁区切りが入らないよう、数値は必ず不変カルチャで書く
        return string.Concat(
            NormalizeAssetPath(assetPath),
            KeyFieldSeparator,
            modifiedUnixSeconds.ToString(CultureInfo.InvariantCulture),
            KeyFieldSeparator,
            fileSizeBytes.ToString(CultureInfo.InvariantCulture),
            KeyFieldSeparator,
            sizePx.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>キャッシュファイル名（拡張子込み）を求める。</summary>
    /// <param name="assetPath">アセット相対パス（<c>assets://</c> 付きでも可）。</param>
    /// <param name="modifiedUnixSeconds">元ファイルの最終更新時刻（Unix 秒）。</param>
    /// <param name="fileSizeBytes">元ファイルのバイト数。</param>
    /// <param name="sizePx">要求するサムネイル 1 辺のピクセル数。</param>
    public static string BuildFileName(
        string? assetPath, long modifiedUnixSeconds, long fileSizeBytes, int sizePx)
    {
        var key  = BuildKeyString(assetPath, modifiedUnixSeconds, fileSizeBytes, sizePx);
        var hash = Fnv1a64(Encoding.UTF8.GetBytes(key));
        return hash.ToString(HashFormat, CultureInfo.InvariantCulture) + ThumbnailExtension;
    }

    // ── パスの組み立て ────────────────────────────────────────

    /// <summary>
    /// アセットルートから、このプロジェクトのキャッシュフォルダ（<c>&lt;project&gt;/cache</c>）を求める。
    ///
    /// <para>
    /// ランタイムは <c>--assets-root</c> の<b>親フォルダ</b>から cache を導出する
    /// （<c>asset_cache.rs</c> の <c>cache_dir()</c>）。ここはその規則の写しで、
    /// <c>ProjectPaths.CacheDir</c> と同じ場所を指す。
    /// </para>
    /// </summary>
    /// <param name="assetsRoot">アセットルート（<c>&lt;project&gt;/assets</c>）の絶対パス。</param>
    /// <returns>キャッシュフォルダの絶対パス。求められないときは <c>null</c>。</returns>
    public static string? CacheDirForAssetsRoot(string? assetsRoot)
    {
        if (string.IsNullOrWhiteSpace(assetsRoot)) return null;
        try
        {
            // 末尾の区切り文字は先に落とす。
            // `Directory.GetParent(@"C:\a\assets\")` は親ではなく `C:\a\assets` 自身を返すため、
            // 付けたまま渡すと cache が assets の**中**に作られ、ランタイムの置き場とずれる
            // （Rust の `Path::parent()` は末尾区切りを無視するので、そちらと挙動を合わせる）。
            // ドライブルート（`C:\`）は TrimEndingDirectorySeparator が保護するので壊れない。
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetsRoot));
            var parent = Directory.GetParent(normalized);
            return parent == null ? null : Path.Combine(parent.FullName, CacheDirName);
        }
        catch
        {
            // 不正文字などでパスとして解釈できない場合はキャッシュを使わない
            return null;
        }
    }

    /// <summary>
    /// キャッシュフォルダとキーの材料から、サムネイル PNG の絶対パスを組み立てる。
    /// </summary>
    /// <param name="cacheDir">キャッシュフォルダ（<see cref="CacheDirForAssetsRoot"/> の戻り値）。</param>
    /// <param name="assetPath">アセット相対パス。</param>
    /// <param name="modifiedUnixSeconds">元ファイルの最終更新時刻（Unix 秒）。</param>
    /// <param name="fileSizeBytes">元ファイルのバイト数。</param>
    /// <param name="sizePx">要求するサムネイル 1 辺のピクセル数。</param>
    /// <returns>PNG の絶対パス。組み立てられないときは <c>null</c>。</returns>
    public static string? BuildPath(
        string? cacheDir, string? assetPath,
        long modifiedUnixSeconds, long fileSizeBytes, int sizePx)
    {
        if (string.IsNullOrWhiteSpace(cacheDir)) return null;
        var name = BuildFileName(assetPath, modifiedUnixSeconds, fileSizeBytes, sizePx);
        return Path.Combine(cacheDir, ThumbnailSubdirName, name);
    }

    /// <summary>
    /// 実ファイルのメタデータ（更新時刻・サイズ）を読んで、サムネイル PNG の絶対パスを求める。
    ///
    /// <para>
    /// ファイルが無い・読めない場合は <c>null</c>。呼び出し側はそれを
    /// 「サムネイルを出せないファイル」として扱い、形式アイコンのまま据え置く。
    /// </para>
    /// </summary>
    /// <param name="cacheDir">キャッシュフォルダ。</param>
    /// <param name="assetPath">アセット相対パス（ハッシュに入る論理パス）。</param>
    /// <param name="modelFilePath">実ファイルの絶対パス（メタデータの取得元）。</param>
    /// <param name="sizePx">要求するサムネイル 1 辺のピクセル数。</param>
    public static string? BuildPathForFile(
        string? cacheDir, string? assetPath, string? modelFilePath, int sizePx)
    {
        if (string.IsNullOrWhiteSpace(cacheDir) || string.IsNullOrWhiteSpace(modelFilePath))
            return null;

        try
        {
            var info = new FileInfo(modelFilePath);
            if (!info.Exists) return null;

            // Rust 側は SystemTime を Unix 秒へ落とす。こちらも UTC の Unix 秒で揃える。
            var modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            return BuildPath(cacheDir, assetPath, modified, info.Length, sizePx);
        }
        catch
        {
            // 権限・パス不正などで読めないファイルはサムネイル対象外にする
            return null;
        }
    }
}
