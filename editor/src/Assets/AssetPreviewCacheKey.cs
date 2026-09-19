using System;
using System.Globalization;
using System.IO;

namespace SEEDEditor.Assets;

/// <summary>
/// プレビュー（画像サムネイル・フォントサムネイル・画像寸法）のキャッシュキーを組み立てる。
///
/// キーは「パス＋最終更新時刻＋サイズ＋派生条件」で作る。
///   - パス     : 別ファイルを取り違えないため。Windows は大文字小文字を区別しないので小文字へ畳む
///   - 更新時刻 : ファイルを差し替えたら古いプレビューを使い回さないため
///   - サイズ   : 更新時刻の分解能（FAT 系で 2 秒）で取りこぼす差し替えを拾うため
///   - 派生条件 : 同じファイルでも出力が変わる条件（サムネイルの一辺など）を混ぜるため
///
/// WPF に依存しない組み立てだけを置くこと
/// （editor/tests/ProjectPanelLogicTests が直接リンクして検証している）。
/// </summary>
public static class AssetPreviewCacheKey
{
    /// <summary>キーの各要素をつなぐ区切り文字（パスに現れない文字を使う）。</summary>
    private const char FieldSeparator = '|';

    /// <summary>ファイル情報が取れなかったときに使う代替値。</summary>
    private const string UnknownField = "?";

    /// <summary>
    /// 値を明示してキーを作る（テストしやすいよう I/O を含まない形）。
    /// </summary>
    /// <param name="path">対象ファイルのパス。</param>
    /// <param name="lastWriteUtc">最終更新時刻（UTC）。</param>
    /// <param name="lengthBytes">ファイルサイズ（バイト）。</param>
    /// <param name="variant">同じファイルでも出力が変わる条件（例: サムネイルの一辺）。空可。</param>
    /// <returns>キャッシュ辞書のキー文字列。</returns>
    public static string Build(string path, DateTime lastWriteUtc, long lengthBytes, string variant = "")
    {
        var normalizedPath = (path ?? string.Empty).ToLowerInvariant();
        var ticks          = lastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        var length         = lengthBytes.ToString(CultureInfo.InvariantCulture);
        return string.Join(FieldSeparator, normalizedPath, ticks, length, variant);
    }

    /// <summary>
    /// 実ファイルから情報を読んでキーを作る。
    /// ファイルが無い・情報が読めない場合も「取れなかった」ことを表すキーを返す
    /// （例外にせず、生成側で失敗として扱えるようにする）。
    /// </summary>
    /// <param name="path">対象ファイルの絶対パス。</param>
    /// <param name="variant">同じファイルでも出力が変わる条件。空可。</param>
    /// <returns>キャッシュ辞書のキー文字列。</returns>
    public static string BuildFromFile(string path, string variant = "")
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) return Build(path, info.LastWriteTimeUtc, info.Length, variant);
        }
        catch
        {
            // アクセス権が無い等。下の「不明」キーで扱う。
        }

        var normalizedPath = (path ?? string.Empty).ToLowerInvariant();
        return string.Join(FieldSeparator, normalizedPath, UnknownField, UnknownField, variant);
    }

    // ── ファイル名化 ────────────────────────────────────────────

    /// <summary>FNV-1a 64bit のオフセット基底値（仕様値）。</summary>
    private const ulong FnvOffsetBasis = 0xcbf29ce484222325UL;

    /// <summary>FNV-1a 64bit の素数（仕様値）。</summary>
    private const ulong FnvPrime = 0x00000100000001b3UL;

    /// <summary>ハッシュを 16 桁の小文字 16 進で書き出す書式。</summary>
    private const string HashFormat = "x16";

    /// <summary>
    /// キー文字列を、そのままファイル名に使える 16 桁の 16 進へ畳む。
    ///
    /// <para>
    /// キーにはパスがそのまま入っているため、ファイル名にできない文字
    /// （<c>:</c> <c>\</c> など）やパス長の上限（260 文字）に当たる。
    /// ハッシュにすれば長さが固定になり、日本語パスでも安全に扱える。
    /// 衝突しても「別ファイルのサムネイルが出る」だけで壊れないので、
    /// 暗号強度は要らない（必要なのは同じ入力から同じ名前が出ることだけ）。
    /// </para>
    ///
    /// <para>
    /// 3D モデルのサムネイル（<see cref="ModelThumbnailCacheKey"/>）は
    /// 同じ FNV-1a を自前で持っている。あちらは <b>Rust 側と 1 ビットも違ってはいけない
    /// 取り決めの写し</b>で、他所の都合で式が変わると両言語の対応が静かに壊れるため、
    /// 意図的に独立させてある（こちらはエディタ内で完結する用途）。
    /// </para>
    /// </summary>
    /// <param name="key"><see cref="Build"/> などが作ったキー文字列。</param>
    /// <returns>16 桁の小文字 16 進。</returns>
    public static string ToFileNameHash(string? key)
    {
        ulong hash = FnvOffsetBasis;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(key ?? string.Empty))
        {
            hash ^= b;
            hash *= FnvPrime;
        }
        return hash.ToString(HashFormat, CultureInfo.InvariantCulture);
    }
}
