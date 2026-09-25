// ============================================================
//  AndroidLocalAssetReader.cs — 手元のアセットを「pak に入れるときと同じ形」で読み、中身の SHA-256 を取る（§23）
//
//  端末の APK の pak には、シーン・プレハブ等（PackagingRules.PathRewriteExtensions）が中の絶対パスを assets:// へ
//  書き換えた形で入っている。差し替えで送るアセットも同じ形にし（書き換えは Packaging/Pak/PakEntryContent が正典。
//  PakWriter と同じ手順）、比べる指紋もその形で取る（そうしないと、書き換える種類のアセットは毎回「違う」になる）。
//  書き換えに失敗したら PakWriter と同じく生のバイト列にする。書き換えない種類はファイルを流し読みして指紋だけ取り、
//  送るときもファイルから流す（大きなモデルをメモリへ読まない）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.IO;
using System.Security.Cryptography;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Packaging.Pak;

namespace SEEDEditor.Android.HotReload;

/// <summary>手元のアセットを pak に入れるときと同じ形で読む。</summary>
public static class AndroidLocalAssetReader
{
    /// <summary>
    /// アセット 1 件を読む。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス（書き換えの基準）。</param>
    /// <param name="relative">アセットルートからの相対パス。</param>
    /// <returns>中身と指紋。</returns>
    /// <exception cref="IOException">読めない。</exception>
    /// <exception cref="UnauthorizedAccessException">権限が無い。</exception>
    public static AndroidLocalAssetContent Read(string assetsRoot, string relative)
    {
        var absolute = AssetPathUtil.ToAbsolute(assetsRoot, relative);
        if (PakEntryContent.NeedsRewrite(relative))
        {
            byte[] bytes;
            try
            {
                bytes = PakEntryContent.ReadRewritten(assetsRoot, relative);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // PakWriter と同じく、書き換えられなければ生のバイト列を使う
                bytes = File.ReadAllBytes(absolute);
            }
            return new AndroidLocalAssetContent(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, bytes, null);
        }

        using var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var digest = Convert.ToHexString(SHA256.HashData(stream));
        return new AndroidLocalAssetContent(digest, stream.Length, null, absolute);
    }
}
