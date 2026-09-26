// ============================================================
//  AndroidPlatformFeatureFiles.cs — 生成するマニフェストの断片のファイル一式（中身と指紋。W1-2）
//
//  【役割】
//  AndroidPlatformManifestWriter.Render が作り、AndroidPlatformFeatureStager が app/src/seedFeatures/ へ置く。
//  同じ設定からは同じバイト列になるので、中身の SHA-256（<see cref="Digest"/>）を APK（Gradle）の工程の指紋の材料にする
//  （機能を変えたら APK を作り直し、変えていなければ飛ばす。Plan/AndroidStepFingerprints.Gradle）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SEEDEditor.Android.Platform;

/// <summary>生成するファイル一式。</summary>
public sealed class AndroidPlatformFeatureFiles
{
    /// <summary>マニフェストの断片の置き場からの相対パス。</summary>
    public const string ManifestRelativePath = "AndroidManifest.xml";

    /// <summary>リソース（seed_system_bars_visible）の置き場からの相対パス（/ 区切り）。</summary>
    public const string ValuesRelativePath = "res/values/seed_platform.xml";

    /// <summary>指紋の材料の区切り（パスと中身の間。パスに現れない文字）。</summary>
    private const char DigestSeparator = '\0';

    /// <summary>ファイル（/ 区切りの相対パス → 中身。相対パスの序数順）。</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    /// <summary>中身の SHA-256（16 進の小文字。相対パスと中身の全部から）。</summary>
    public string Digest { get; }

    /// <summary>ファイル一式を作る。</summary>
    /// <param name="files">相対パス → 中身。</param>
    public AndroidPlatformFeatureFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        Files = new SortedDictionary<string, byte[]>(files.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal);
        Digest = ComputeDigest(Files);
    }

    /// <summary>マニフェストの断片の中身（UTF-8 の文字列。テスト・ログ用）。</summary>
    public string ManifestText => Encoding.UTF8.GetString(Files[ManifestRelativePath]);

    /// <summary>リソースの中身（UTF-8 の文字列。テスト・ログ用）。</summary>
    public string ValuesText => Encoding.UTF8.GetString(Files[ValuesRelativePath]);

    /// <summary>相対パスと中身から SHA-256 を作る。</summary>
    private static string ComputeDigest(IReadOnlyDictionary<string, byte[]> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relative, content) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative + DigestSeparator));
            hash.AppendData(content);
            hash.AppendData(Encoding.UTF8.GetBytes(DigestSeparator.ToString()));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
