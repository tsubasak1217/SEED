// ============================================================
//  LauncherIconStager.cs — ランチャーのアイコンの生成物を Gradle の置き場（app/src/seedIcon/res/）へ置く（段階D）
//
//  【流れ】（APK の工程の Gradle の前に呼ぶ。Steps/GradleBuildStep）
//    アイコンの設定あり … 元の PNG を読む（PngDecoder）→ 生成物を作る（LauncherIconGenerator）→
//                         中身が違うファイルだけを書き、一覧に無いファイルは消す
//    アイコンの設定なし … 置き場ごと消す（前のプロジェクトのアイコンを残さない。Gradle へは seed.launcherIcon を渡さない）
//  中身が同じファイルは書かない（更新時刻を変えない）ので、アイコンを変えていなければ Gradle の差分のビルドを邪魔しない。
//  PNG の書き出しは同じ画像から常に同じバイト列になる（PngEncoder）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.Android.Icons;

/// <summary>置いた結果。</summary>
/// <param name="Generated">アイコンを生成したか（false ならシステムの既定のアイコン）。</param>
/// <param name="Written">書いたファイルの数（中身が変わった・新しい）。</param>
/// <param name="Unchanged">中身が同じで書かなかったファイルの数。</param>
/// <param name="Removed">消したファイルの数（一覧に無い古い生成物）。</param>
/// <param name="SourceSize">元の画像の大きさ（生成しなかったら null）。</param>
/// <param name="Warnings">警告。</param>
public sealed record LauncherIconStageResult(
    bool Generated, int Written, int Unchanged, int Removed, (int Width, int Height)? SourceSize, IReadOnlyList<string> Warnings)
{
    /// <summary>ログ用の一行。</summary>
    /// <returns>説明。</returns>
    public string Describe() => Generated
        ? $"アイコンを生成しました（元 {SourceSize!.Value.Width}x{SourceSize.Value.Height}・書いた {Written}・同じ {Unchanged}・消した {Removed}）"
        : Removed > 0 ? $"アイコンの設定が無いため、前の生成物を消しました（{Removed} ファイル。システムの既定のアイコン）"
        : "アイコンの設定がありません（システムの既定のアイコン）";
}

/// <summary>アイコンの生成物を置く。</summary>
public static class LauncherIconStager
{
    /// <summary>
    /// 生成の作り方の版（大きさ・合成の仕方を変えたら上げる。APK の指紋の材料）。
    /// </summary>
    public const int GeneratorRevision = 1;

    /// <summary>
    /// 生成物を置く（設定が無ければ置き場を消す）。
    /// </summary>
    /// <param name="resDir">置き場（app/src/seedIcon/res）。</param>
    /// <param name="source">アイコンの元（無ければ null）。</param>
    /// <returns>結果。</returns>
    /// <exception cref="AndroidPipelineException">元の PNG を読めないとき。</exception>
    public static LauncherIconStageResult Stage(string resDir, LauncherIconSource? source)
    {
        if (source is null)
        {
            var removed = CountFiles(resDir);
            if (Directory.Exists(resDir)) Directory.Delete(resDir, recursive: true);
            return new LauncherIconStageResult(false, 0, 0, removed, null, Array.Empty<string>());
        }

        RgbaImage image;
        try
        {
            image = PngDecoder.Decode(File.ReadAllBytes(source.IconPath));
        }
        catch (Exception ex) when (ex is PngFormatException or IOException or UnauthorizedAccessException)
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest,
                $"アイコンの PNG（{source.IconPath}）を読めません: {ex.Message}");
        }

        var output = LauncherIconGenerator.Generate(image, source.Background);
        var (written, unchanged) = WriteChanged(resDir, output.Files);
        var removedStale = RemoveStale(resDir, output.Files.Keys);
        return new LauncherIconStageResult(true, written, unchanged, removedStale, (image.Width, image.Height), output.Warnings);
    }

    /// <summary>中身が違うファイルだけを書く。</summary>
    private static (int Written, int Unchanged) WriteChanged(string resDir, IReadOnlyDictionary<string, byte[]> files)
    {
        var written = 0;
        var unchanged = 0;
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(resDir, relative.Replace('/', Path.DirectorySeparatorChar));
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

    /// <summary>一覧に無いファイル（前の版の生成物）を消し、空になったフォルダも消す。</summary>
    private static int RemoveStale(string resDir, IEnumerable<string> keep)
    {
        if (!Directory.Exists(resDir)) return 0;
        var keepSet = new HashSet<string>(keep.Select(k => k.Replace('/', Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(resDir, "*", SearchOption.AllDirectories).ToList())
        {
            if (keepSet.Contains(Path.GetRelativePath(resDir, file))) continue;
            File.Delete(file);
            removed++;
        }
        foreach (var dir in Directory.EnumerateDirectories(resDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        return removed;
    }

    /// <summary>フォルダの中のファイルの数（無ければ 0）。</summary>
    private static int CountFiles(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count() : 0;
}
