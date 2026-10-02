using System;
using System.IO;
using System.Security.Cryptography;

namespace SEEDEditor.Reload;

/// <summary>
/// 自動再読込が「内容が本当に変わったか」を判定するためのファイル内容のハッシュ。
///
/// シーンの自動再読込（SceneAutoReloader）とプレハブの自動再読込（PrefabAutoReloader）が共有する。
/// ファイル監視のイベントはタイムスタンプだけの更新（touch・ウイルススキャン）や
/// 同じ書き込みの重複でも届くので、内容の SHA-256 を比べて no-op を落とす。
/// </summary>
public static class FileContentHash
{
    /// <summary>
    /// ファイル内容の SHA-256 を 16 進文字列で返す。
    /// 読めない（書き込み中でロックされている・消えた・権限が無い）場合は null。
    /// 書き込み中のファイルも読めるよう、共有モードは ReadWrite | Delete で開く。
    /// </summary>
    /// <param name="path">ファイルの絶対パス。</param>
    /// <returns>ハッシュ（大文字の 16 進）。読めなければ null。</returns>
    public static string? TryCompute(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (Exception)
        {
            // ファイルが無い / ロック中 / 権限不足。呼び出し側が再試行または中止を決める。
            return null;
        }
    }
}
