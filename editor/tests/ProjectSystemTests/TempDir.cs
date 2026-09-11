using System;
using System.IO;

namespace ProjectSystemTests;

/// <summary>
/// テスト用の一時フォルダ。using で囲むと後始末まで含めて 1 行で書ける。
/// 実行環境（リポジトリ・利用者の設定フォルダ）を一切汚さないため、
/// このテストのファイル操作はすべてここの配下で行う。
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
            "SEED_ProjectSystemTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>一時フォルダ直下にサブフォルダを作り、その絶対パスを返す。</summary>
    /// <param name="name">サブフォルダ名。</param>
    public string CreateSubDirectory(string name)
    {
        var dir = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>一時フォルダ配下のパスを組み立てる（作成はしない）。</summary>
    /// <param name="relative">相対パス。</param>
    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    /// <summary>一時フォルダを削除する。</summary>
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* 後始末の失敗は無視 */ }
    }
}
