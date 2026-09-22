// ============================================================
//  EngineFolderLocator.cs — 「SEED 本体のフォルダー」がどこかを決める
//
//  【何のためか】
//  タスクバーのジャンプリストの「SEED のフォルダーを開く」（2026-09-23 の要望）で開く先を決める。
//  「SEED」の項目自体は Windows が自動で出すもので、右クリックの中身（開く／管理者として実行／
//  ピン留め／プロパティ）はアプリから変えられない。そこでジャンプリスト本体へタスク項目として足す。
//
//  【どこを「本体」と見なすか】
//   ① 開発配置: exe は editor/bin/<Cfg>/<tfm>/ に出る。利用者が「SEED 本体」と呼ぶのは
//      リポジトリのフォルダ（SEED/。editor/ runtime/ docs/ が並ぶ場所）なので、そこを開く。
//      見分け方は EditorPaths と同じ「3 階層上に editor/config がある」。そのさらに 1 つ上がリポジトリ。
//   ② 配布配置: exe の隣に config/ がある。本体＝exe のフォルダ。
//   ③ どちらでもない: exe のフォルダ（少なくとも実行ファイルの場所は開ける）。
//
//  【依存】
//  WPF 非依存の純粋ロジック（判定は関数注入で、コンソールのテストから検証できる）。
// ============================================================
using System;
using System.IO;

namespace SEEDEditor.Project;

/// <summary>
/// 「SEED 本体のフォルダー」の場所を決める。
/// </summary>
public static class EngineFolderLocator
{
    /// <summary>
    /// 実行ファイルの置き場からエディタルート（editor/）まで遡る階層数。
    /// 開発ビルドは editor/bin/&lt;Cfg&gt;/&lt;tfm&gt;/ に出るため 3。EditorPaths の規約と同じ。
    /// </summary>
    private const int EDITOR_ROOT_LEVELS_UP = 3;

    /// <summary>エディタルートの目印になるフォルダ名（editor/config）。</summary>
    private const string CONFIG_DIR_NAME = "config";

    /// <summary>
    /// 本体のフォルダーを決める。
    /// </summary>
    /// <param name="exeDirectory">実行ファイルのあるフォルダの絶対パス。</param>
    /// <param name="directoryExists">フォルダの実在判定（テストから差し替えられるよう注入）。</param>
    /// <returns>開くべきフォルダの絶対パス。実行ファイルのフォルダが空なら空文字。</returns>
    public static string Resolve(string? exeDirectory, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(exeDirectory)) return string.Empty;

        string exeDir;
        try { exeDir = Path.GetFullPath(exeDirectory); }
        catch (Exception) { return exeDirectory; }

        // ① 開発配置: 3 階層上に editor/config があれば、その親（リポジトリ）が本体。
        var editorRoot = Ancestor(exeDir, EDITOR_ROOT_LEVELS_UP);
        if (editorRoot is not null && directoryExists(Path.Combine(editorRoot, CONFIG_DIR_NAME)))
        {
            var repoRoot = Path.GetDirectoryName(editorRoot);
            if (!string.IsNullOrEmpty(repoRoot) && directoryExists(repoRoot)) return repoRoot;
            return editorRoot;
        }

        // ② 配布配置・③ それ以外: exe のフォルダ。
        return exeDir;
    }

    /// <summary>指定の階層数だけ親へ遡ったフォルダ（途中でルートに達したら null）。</summary>
    /// <param name="directory">起点のフォルダ。</param>
    /// <param name="levels">遡る階層数。</param>
    private static string? Ancestor(string directory, int levels)
    {
        var current = directory;
        for (var i = 0; i < levels; i++)
        {
            current = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(current)) return null;
        }
        return current;
    }
}
