using System;
using System.IO;

namespace AndroidPipelineTests;

/// <summary>
/// テスト用の一時フォルダ（using で囲むと後始末まで 1 行で書ける）。
/// リポジトリ・利用者の設定フォルダを汚さないため、このテストのファイル操作はすべてここの配下で行う。
/// </summary>
public sealed class TempDir : IDisposable
{
    /// <summary>一時フォルダの絶対パス。</summary>
    public string Path { get; }

    /// <summary>一時フォルダを新規作成する。</summary>
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SEED_AndroidPipelineTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>一時フォルダ配下のパスを組み立てる（作成はしない）。</summary>
    /// <param name="relative">相対パス（/ 区切り可）。</param>
    /// <returns>絶対パス。</returns>
    public string Combine(string relative) =>
        System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>ファイルを書く（フォルダも作る）。</summary>
    /// <param name="relative">相対パス。</param>
    /// <param name="content">中身。</param>
    /// <returns>絶対パス。</returns>
    public string WriteFile(string relative, string content)
    {
        var path = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>一時フォルダを削除する。</summary>
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* 後始末の失敗は無視 */ }
    }
}
