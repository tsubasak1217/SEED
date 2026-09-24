// ============================================================
//  ApkFile.cs — Gradle が作った APK の SHA-256（インストールを飛ばしてよいかの判断に使う）
//
//  APK は 60〜110 MB あるので、前回の記録（大きさ:更新時刻）と同じならその時の SHA-256 を使い回す。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.IO;
using System.Security.Cryptography;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.State;

namespace SEEDEditor.Android.Steps;

/// <summary>APK の SHA-256。</summary>
public static class ApkFile
{
    /// <summary>
    /// APK の SHA-256（16 進の小文字）。前回の記録と同じ APK なら記録の値を使う。APK が無ければ null。
    /// </summary>
    /// <param name="apkPath">APK のパス。</param>
    /// <param name="stamp">前回作った APK の記録（無ければ null）。</param>
    /// <returns>SHA-256。</returns>
    public static string? Sha256(string apkPath, AndroidApkStamp? stamp)
    {
        var identity = AndroidOutputIdentity.OfFile(apkPath);
        if (identity == AndroidFingerprintBuilder.MissingMarker) return null;
        if (stamp is not null && stamp.Identity == identity) return stamp.Sha256;
        return ComputeSha256(apkPath);
    }

    /// <summary>ファイルの SHA-256 を計算する（16 進の小文字）。</summary>
    /// <param name="path">ファイル。</param>
    /// <returns>SHA-256。</returns>
    public static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
