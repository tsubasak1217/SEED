using System;
using System.IO;

namespace InlineCompletionTests;

/// <summary>
/// リポジトリの実ファイル（docs/scripting_api.md・editor/config/inline_completion_reference.json）を
/// 実行ファイルの場所から親を遡って探す（エディタの ScriptApiReference.Locate と同じ戦略）。
/// </summary>
public static class RepoFiles
{
    /// <summary>リファレンスのリポジトリ相対パス。</summary>
    public const string ScriptingApiRelative = "docs/scripting_api.md";

    /// <summary>選び方の設定のリポジトリ相対パス。</summary>
    public const string ReferenceSettingsRelative = "editor/config/inline_completion_reference.json";

    /// <summary>リポジトリ相対パスのファイルを探す。</summary>
    /// <param name="relative">リポジトリ相対パス（/ 区切り）。</param>
    /// <returns>絶対パス（見つからなければ例外）。</returns>
    public static string Find(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"リポジトリの {relative} が見つかりません（起点 {AppContext.BaseDirectory}）");
    }
}
