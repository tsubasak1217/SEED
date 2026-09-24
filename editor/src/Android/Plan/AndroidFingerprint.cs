// ============================================================
//  AndroidFingerprint.cs — 工程の入力の指紋（ハッシュ）と、出力の同一性
//
//  【入力の指紋】
//  ファイルの「相対パス・大きさ・更新時刻」とパラメータ（ABI・プロファイル等）を並べた文字列の SHA-256。
//  中身そのものを読まないのは、アセット（数 GB になり得る）や .so（1 本 450 MB）を毎回読むと遅いため
//  （make・MSBuild と同じ考え方）。中身が変われば普通は大きさか更新時刻が変わる。変わったのに同じ大きさ・同じ時刻、
//  という稀な場合は --rebuild で作り直せる。小さな設定ファイル（dotnet_runtime.json）だけは中身で比べる。
//
//  【出力の同一性】
//  工程の出力（.so・pak の置き場・APK）の「大きさ・更新時刻」（フォルダは中身の一覧のハッシュ）。
//  記録と今が違えば、外で作り直された・消されたとみなして工程をやり直す（手で cargo ndk を叩いた等）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SEEDEditor.Android.Plan;

/// <summary>工程の入力の指紋を作る。</summary>
public sealed class AndroidFingerprintBuilder
{
    /// <summary>無いファイル・フォルダを表す値。</summary>
    public const string MissingMarker = "missing";

    /// <summary>集めた材料（1 行 1 項目）。</summary>
    private readonly StringBuilder _material = new();

    /// <summary>パラメータを足す。</summary>
    /// <param name="key">名前。</param>
    /// <param name="value">値（null は空）。</param>
    /// <returns>このインスタンス。</returns>
    public AndroidFingerprintBuilder AddValue(string key, string? value)
    {
        _material.Append("value|").Append(key).Append('|').Append(value ?? string.Empty).Append('\n');
        return this;
    }

    /// <summary>
    /// ファイルかフォルダ（サブフォルダごと）の「相対パス・大きさ・更新時刻」を足す。無ければ「無い」ことを足す。
    /// </summary>
    /// <param name="label">材料の名前（表示用の相対パス等。入力の並びが変わったことも指紋に出るように）。</param>
    /// <param name="path">ファイルかフォルダの絶対パス。</param>
    /// <param name="excludedDirectoryNames">辿らないフォルダ名（bin・obj・target 等の生成物）。</param>
    /// <returns>このインスタンス。</returns>
    public AndroidFingerprintBuilder AddTree(string label, string path, IReadOnlySet<string>? excludedDirectoryNames = null)
    {
        if (File.Exists(path))
        {
            AppendFile(label, string.Empty, new FileInfo(path));
        }
        else if (Directory.Exists(path))
        {
            foreach (var (relative, file) in EnumerateFiles(path, excludedDirectoryNames))
            {
                AppendFile(label, relative, file);
            }
        }
        else
        {
            _material.Append("tree|").Append(label).Append('|').Append(MissingMarker).Append('\n');
        }
        return this;
    }

    /// <summary>小さなファイルの中身そのもの（のハッシュ）を足す。無ければ「無い」ことを足す。</summary>
    /// <param name="label">材料の名前。</param>
    /// <param name="path">ファイルの絶対パス。</param>
    /// <returns>このインスタンス。</returns>
    public AndroidFingerprintBuilder AddFileContent(string label, string path)
    {
        var hash = File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : MissingMarker;
        _material.Append("content|").Append(label).Append('|').Append(hash).Append('\n');
        return this;
    }

    /// <summary>集めた材料の SHA-256（16 進の小文字）。</summary>
    /// <returns>指紋。</returns>
    public string Build() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_material.ToString()))).ToLowerInvariant();

    /// <summary>1 ファイルぶんの材料を足す。</summary>
    private void AppendFile(string label, string relative, FileInfo file)
    {
        _material.Append("file|").Append(label).Append('|').Append(relative).Append('|')
            .Append(file.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    /// <summary>
    /// フォルダの中のファイルを相対パスの順（序数比較）に列挙する（除外するフォルダ名は辿らない）。
    /// </summary>
    /// <param name="root">フォルダ。</param>
    /// <param name="excludedDirectoryNames">辿らないフォルダ名。</param>
    /// <returns>（/ 区切りの相対パス, ファイル）の並び。</returns>
    public static IEnumerable<(string Relative, FileInfo File)> EnumerateFiles(string root, IReadOnlySet<string>? excludedDirectoryNames)
    {
        var results = new List<(string, FileInfo)>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                if (item is DirectoryInfo sub)
                {
                    if (excludedDirectoryNames is null || !excludedDirectoryNames.Contains(sub.Name)) pending.Push(sub);
                }
                else if (item is FileInfo file)
                {
                    results.Add((Path.GetRelativePath(root, file.FullName).Replace(Path.DirectorySeparatorChar, '/'), file));
                }
            }
        }
        return results.OrderBy(entry => entry.Item1, StringComparer.Ordinal);
    }
}

/// <summary>工程の出力の同一性。</summary>
public static class AndroidOutputIdentity
{
    /// <summary>空のフォルダ・無いフォルダの同一性（中身が無い状態）。</summary>
    public const string Empty = "empty";

    /// <summary>ファイルの同一性（「大きさ:更新時刻」。無ければ <see cref="AndroidFingerprintBuilder.MissingMarker"/>）。</summary>
    /// <param name="path">ファイルの絶対パス。</param>
    /// <returns>同一性。</returns>
    public static string OfFile(string path)
    {
        var file = new FileInfo(path);
        return file.Exists
            ? $"{file.Length.ToString(CultureInfo.InvariantCulture)}:{file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}"
            : AndroidFingerprintBuilder.MissingMarker;
    }

    /// <summary>フォルダの同一性（中身の一覧のハッシュ。無い・空なら <see cref="Empty"/>）。</summary>
    /// <param name="path">フォルダの絶対パス。</param>
    /// <returns>同一性。</returns>
    public static string OfDirectory(string path)
    {
        if (!Directory.Exists(path)) return Empty;
        var files = AndroidFingerprintBuilder.EnumerateFiles(path, null).ToList();
        if (files.Count == 0) return Empty;
        var builder = new AndroidFingerprintBuilder();
        builder.AddTree("output", path);
        return builder.Build();
    }
}
