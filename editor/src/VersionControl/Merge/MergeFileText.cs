// ============================================================
//  MergeFileText.cs — 競合したファイルの読み書き（BOM とバイナリの扱い）
//
//  【なぜ File.ReadAllText を直接使わないのか】
//  1. BOM … `File.ReadAllText` は BOM を黙って捨てる。捨てたまま書き戻すと
//     BOM が消え、バージョン管理上は「ファイル全体が変わった」ことになる。
//  2. バイナリ … 競合するのはテキストだけとは限らない（.png / .fbx など）。
//     UTF-8 として読むと文字化けした「それらしいテキスト」になってしまい、
//     マージエディタが開けてしまう。NUL バイトの有無で先に弾く。
//  3. 巨大ファイル … 行差分は O(n×m) の表を作る。数百 MB のファイルを
//     読み込んでから諦めるのでは遅すぎるので、読む前に大きさで弾く。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.IO;
using System.Text;

namespace SEEDEditor.VersionControl.Merge;

/// <summary>
/// ファイルを読んだ結果（不変）。
/// </summary>
/// <param name="Succeeded">テキストとして読めたか。</param>
/// <param name="Text">中身（読めなかったときは空文字）。</param>
/// <param name="HasUtf8Bom">UTF-8 BOM が付いていたか。</param>
/// <param name="Reason">読めなかった理由（読めたときは空文字）。</param>
public readonly record struct MergeFileReadResult(
    bool Succeeded, string Text, bool HasUtf8Bom, string Reason);

/// <summary>
/// 競合したファイルの読み書き。
/// </summary>
public static class MergeFileText
{
    /// <summary>
    /// マージエディタで開けるファイルの上限 [バイト]。
    /// 行差分の表がマスあたり 4 バイト要るため、これ以上は現実的に扱えない。
    /// </summary>
    public const int MAX_READ_BYTES = 16 * 1024 * 1024;

    /// <summary>バイナリ判定のために先頭から調べるバイト数。</summary>
    public const int BINARY_PROBE_BYTES = 8 * 1024;

    /// <summary>UTF-8 BOM のバイト列。</summary>
    private static readonly byte[] UTF8_BOM = { 0xEF, 0xBB, 0xBF };

    /// <summary>
    /// ファイルをテキストとして読む。
    /// バイナリ・大きすぎる・読めないときは理由つきで失敗を返す（例外は投げない）。
    /// </summary>
    /// <param name="absolutePath">読むファイルの絶対パス。</param>
    public static MergeFileReadResult Read(string absolutePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
                return Failure(VersionControlMessages.MERGE_FILE_NOT_FOUND);

            var info = new FileInfo(absolutePath);
            if (info.Length > MAX_READ_BYTES)
            {
                return Failure(string.Format(
                    VersionControlMessages.MERGE_FILE_TOO_LARGE_FORMAT,
                    MAX_READ_BYTES / (1024 * 1024)));
            }

            var bytes = File.ReadAllBytes(absolutePath);
            if (LooksBinary(bytes)) return Failure(VersionControlMessages.MERGE_FILE_BINARY);

            var hasBom = StartsWithUtf8Bom(bytes);
            var start  = hasBom ? UTF8_BOM.Length : 0;
            var text   = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetString(bytes, start, bytes.Length - start);

            return new MergeFileReadResult(true, text, hasBom, string.Empty);
        }
        catch (Exception ex)
        {
            // 読めない理由（権限・排他）はそのまま見せた方が原因に辿り着ける。
            return Failure(ex.Message);
        }
    }

    /// <summary>
    /// テキストをファイルへ書き戻す。BOM の有無は元に合わせる。
    ///
    /// <para>
    /// 改行は <paramref name="text"/> に含まれているものをそのまま使う
    /// （<see cref="MergeTextLines"/> が行ごとの改行を保っている）。
    /// ここで <c>WriteAllText</c> の既定に任せると改行が変換されてしまう。
    /// </para>
    /// </summary>
    /// <param name="absolutePath">書き込み先の絶対パス。</param>
    /// <param name="text">書き込む中身（BOM は含めない）。</param>
    /// <param name="withUtf8Bom">UTF-8 BOM を付けるか。</param>
    public static void Write(string absolutePath, string text, bool withUtf8Bom)
        => File.WriteAllText(
            absolutePath, text,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: withUtf8Bom));

    /// <summary>
    /// いまファイルに UTF-8 BOM が付いているかを調べる（書き戻す前の確認用）。
    /// ファイルが無い・読めないときは偽。
    /// </summary>
    /// <param name="absolutePath">調べるファイルの絶対パス。</param>
    public static bool HasUtf8Bom(string absolutePath)
    {
        try
        {
            if (!File.Exists(absolutePath)) return false;

            using var stream = File.OpenRead(absolutePath);
            var head  = new byte[UTF8_BOM.Length];
            var count = stream.Read(head, 0, head.Length);
            return count == UTF8_BOM.Length && StartsWithUtf8Bom(head);
        }
        catch (Exception)
        {
            // 判定できないなら「BOM 無し」へ倒す（付け足す方が害が大きい）。
            return false;
        }
    }

    /// <summary>先頭が UTF-8 BOM か。</summary>
    /// <param name="bytes">調べるバイト列。</param>
    private static bool StartsWithUtf8Bom(byte[] bytes)
    {
        if (bytes.Length < UTF8_BOM.Length) return false;

        for (var i = 0; i < UTF8_BOM.Length; i++)
        {
            if (bytes[i] != UTF8_BOM[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// バイナリらしいか（先頭に NUL バイトがあるか）。
    /// テキストファイルに NUL は現れない、という広く使われている判定。
    /// </summary>
    /// <param name="bytes">調べるバイト列。</param>
    private static bool LooksBinary(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, BINARY_PROBE_BYTES);
        for (var i = 0; i < limit; i++)
        {
            if (bytes[i] == 0) return true;
        }
        return false;
    }

    /// <summary>理由つきの失敗を作る。</summary>
    /// <param name="reason">読めなかった理由。</param>
    private static MergeFileReadResult Failure(string reason)
        => new(false, string.Empty, false, reason);
}
