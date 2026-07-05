using System;
using System.IO;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion;

/// <summary>
/// スクリプト API リファレンス（docs/scripting_api.md）を読み込み、
/// インライン補完のシステムプロンプトへ注入するための正典テキストを提供する。
///
/// このファイルが「スクリプトから使える API を AI に教える」唯一の情報源。
/// API を追加したら docs/scripting_api.md を更新すれば、補完にも自動反映される。
/// 実行時に 1 度だけ読み込んでキャッシュする。
/// </summary>
public static class ScriptApiReference
{
    /// <summary>リファレンス Markdown のファイル名。</summary>
    private const string FileName = "scripting_api.md";

    /// <summary>リポジトリ内での相対パス（親ディレクトリを遡って探す際に使う）。</summary>
    private const string RepoRelativePath = "docs/scripting_api.md";

    /// <summary>プロンプトへ注入する最大文字数（過大なプロンプトによる遅延を防ぐ）。</summary>
    private const int MaxChars = 12000;

    // 読み込み結果のキャッシュ（null 未取得 / "" 見つからず）
    private static string? _cached;

    /// <summary>
    /// リファレンス本文を返す（見つからなければ空文字）。初回のみディスクから読み込む。
    /// </summary>
    public static string Load()
    {
        if (_cached is not null) return _cached;

        try
        {
            var path = Locate();
            if (path is null) { _cached = ""; return _cached; }

            var text = File.ReadAllText(path);
            if (text.Length > MaxChars) text = text[..MaxChars];
            _cached = text;
        }
        catch (Exception ex)
        {
            SEEDEditor.EditorLog.Write($"[インライン補完] APIリファレンス読み込み失敗: {ex.Message}");
            _cached = "";
        }
        return _cached;
    }

    /// <summary>
    /// リファレンス Markdown の場所を特定する。
    /// 1) 実行ディレクトリ直下（ビルドで出力コピーされた場合）
    /// 2) 実行ディレクトリから親を遡って docs/scripting_api.md を探す（開発時のリポジトリ構成）
    /// </summary>
    private static string? Locate()
    {
        // 1) 出力ディレクトリへコピーされた場合
        var baseDir = AppContext.BaseDirectory;
        var local = Path.Combine(baseDir, FileName);
        if (File.Exists(local)) return local;

        // 2) 親ディレクトリを遡ってリポジトリ内の docs/scripting_api.md を探す
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, RepoRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
