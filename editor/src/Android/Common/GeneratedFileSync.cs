// ============================================================
//  GeneratedFileSync.cs — SeedAndroid の生成物を Gradle の置き場へ同期する共通処理
//
//  【役割】
//  ビルドのたびに生成するファイル（ランチャーのアイコン app/src/seedIcon/res・W1-2 のプラットフォーム機能の断片
//  app/src/seedFeatures）を置き場へ反映する。
//    WriteChanged … 中身が違う（か無い）ファイルだけを書く。中身が同じファイルは書かない（更新時刻を変えないので、
//                   Gradle の差分のビルドと APK の工程の指紋〈大きさと更新時刻〉を邪魔しない）
//    RemoveStale  … 一覧に無いファイル（前の版の生成物）を消し、空になったフォルダも消す
//  使う側: Icons/LauncherIconStager・Platform/AndroidPlatformFeatureStager。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SEEDEditor.Android;

/// <summary>生成物の置き場への同期。</summary>
public static class GeneratedFileSync
{
    /// <summary>
    /// 中身が違うファイルだけを書く。
    /// </summary>
    /// <param name="rootDir">置き場。</param>
    /// <param name="files">/ 区切りの相対パス → 中身。</param>
    /// <returns>書いた数と、中身が同じで書かなかった数。</returns>
    public static (int Written, int Unchanged) WriteChanged(string rootDir, IReadOnlyDictionary<string, byte[]> files)
    {
        var written = 0;
        var unchanged = 0;
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(rootDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            {
                unchanged++;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            written++;
        }
        return (written, unchanged);
    }

    /// <summary>
    /// 一覧に無いファイルを消し、空になったフォルダも消す（置き場そのものは残す）。
    /// </summary>
    /// <param name="rootDir">置き場（無ければ何もしない）。</param>
    /// <param name="keep">残すファイルの / 区切りの相対パス。</param>
    /// <returns>消したファイルの数。</returns>
    public static int RemoveStale(string rootDir, IEnumerable<string> keep)
    {
        if (!Directory.Exists(rootDir)) return 0;
        var keepSet = new HashSet<string>(keep.Select(k => k.Replace('/', Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories).ToList())
        {
            if (keepSet.Contains(Path.GetRelativePath(rootDir, file))) continue;
            File.Delete(file);
            removed++;
        }
        foreach (var dir in Directory.EnumerateDirectories(rootDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        return removed;
    }
}
