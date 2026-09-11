using System;
using System.IO;

namespace RuntimeBuildConfigTests;

/// <summary>
/// テスト用の一時フォルダ。using で囲むと後始末まで含めて 1 行で書ける。
/// 実行環境（リポジトリ・利用者の設定フォルダ・実際の runtime/target）を
/// 一切汚さないため、このテストのファイル操作はすべてここの配下で行う。
/// </summary>
public sealed class TempDir : IDisposable
{
    /// <summary>一時フォルダの絶対パス。</summary>
    public string Path { get; }

    /// <summary>一時フォルダを新規作成する。</summary>
    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SEED_RuntimeBuildConfigTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>一時フォルダ配下のパスを組み立てる（作成はしない）。</summary>
    /// <param name="relative">相対パス。</param>
    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    /// <summary>一時フォルダ配下にフォルダを作り、その絶対パスを返す（多段可）。</summary>
    /// <param name="relative">相対パス。</param>
    public string CreateDirectory(string relative)
    {
        var dir = Combine(relative);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>一時フォルダ配下にファイルを書き、その絶対パスを返す（親フォルダも作る）。</summary>
    /// <param name="relative">相対パス。</param>
    /// <param name="content">書き込む内容。</param>
    public string WriteFile(string relative, string content)
    {
        var file = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }

    /// <summary>一時フォルダを削除する。</summary>
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* 後始末の失敗は無視 */ }
    }
}
