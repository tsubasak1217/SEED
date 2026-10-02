using System;
using System.IO;

namespace SEEDEditor.Reload;

/// <summary>
/// プレハブの外部変更の監視（<see cref="PrefabAutoReloader"/>）が「拾うべきファイルか」を決める純粋な判定。
///
/// 【拾うもの】アセットルート配下の <c>.actor</c> / <c>.actor2d</c>（拡張子は大文字小文字を問わない）。
///
/// 【拾わないもの】
///   - 世代バックアップ <c>.backup/</c> の中（ランタイムの safe_write・エディタの SafeFileWriter が
///     保存の直前に旧版を複製する。拾うと「保存のたびに古い版が外部変更として届く」）
///   - ビルド生成物・VCS の内部（<c>obj</c> / <c>bin</c> / <c>.git</c> / <c>.vs</c> / <c>node_modules</c>。
///     スクリプトの監視 ScriptAutoReloader と同じ一覧）
///   - 原子的な保存の一時ファイル <c>X.actor.tmp</c>（拡張子が <c>.tmp</c> なので拡張子の判定で落ちる。
///     名前の変更 <c>X.actor.tmp → X.actor</c> は新しい名前で拾う）
///
/// WPF・ファイルシステムに触れない（文字列だけで判定する。テスト: editor/tests/AutoReloadPolicyTests）。
/// </summary>
public static class PrefabWatchPaths
{
    /// <summary>3D プレハブの拡張子。</summary>
    public const string Actor3DExtension = ".actor";

    /// <summary>2D プレハブの拡張子。</summary>
    public const string Actor2DExtension = ".actor2d";

    /// <summary>FileSystemWatcher のフィルタ（拡張子ごとに 1 つ）。</summary>
    public static readonly string[] WatchFilters = { "*" + Actor3DExtension, "*" + Actor2DExtension };

    /// <summary>
    /// 中のファイルを拾わないディレクトリ名（パスのどの段にあっても除外する。大文字小文字を問わない）。
    /// <c>.backup</c> はランタイムの <c>safe_write::BACKUP_DIR_NAME</c> と
    /// エディタの <c>SafeFileWriter.BackupDirName</c> と同じ名前（世代バックアップの置き場）。
    /// </summary>
    private static readonly string[] IgnoredDirectories = { ".backup", "obj", "bin", ".git", ".vs", "node_modules" };

    /// <summary>
    /// 監視対象のプレハブファイルか。
    /// </summary>
    /// <param name="fullPath">ファイル監視が知らせた絶対パス（null / 空は false）。</param>
    /// <param name="assetsRoot">アセットルートの絶対パス（この配下の相対部分だけを除外ディレクトリの判定に使う）。</param>
    /// <returns>拾うべきなら true。</returns>
    public static bool IsPrefabFile(string? fullPath, string assetsRoot)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        // 1. 拡張子（最後の拡張子が .actor / .actor2d のものだけ。X.actor.tmp・X.actor~ は落ちる）
        var extension = Path.GetExtension(fullPath);
        if (!string.Equals(extension, Actor3DExtension, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, Actor2DExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 2. 除外ディレクトリ（アセットルートより下の段だけを見る。ルート自体の名前に bin などが含まれても落とさない）
        var relative = RelativeUnderRoot(fullPath, assetsRoot);
        var segments = relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        // 最後の段はファイル名なので、ディレクトリの段（最後以外）だけを調べる
        for (int i = 0; i < segments.Length - 1; i++)
        {
            foreach (var ignored in IgnoredDirectories)
            {
                if (string.Equals(segments[i], ignored, StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// アセットルートより下の相対部分を返す（ルートの外ならパス全体）。区切りは '/' と '\\' のどちらでもよい。
    /// </summary>
    /// <param name="fullPath">絶対パス。</param>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <returns>相対部分（先頭の区切りは残ってもよい。呼び出し側が空の段を捨てる）。</returns>
    private static string RelativeUnderRoot(string fullPath, string assetsRoot)
    {
        if (string.IsNullOrEmpty(assetsRoot)) return fullPath;
        var normalizedPath = fullPath.Replace('\\', '/');
        var normalizedRoot = assetsRoot.Replace('\\', '/').TrimEnd('/');
        // ルートそのものの名前の途中で切らないよう、ルートの直後が区切りであることを確かめる
        if (normalizedPath.Length > normalizedRoot.Length
            && normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && normalizedPath[normalizedRoot.Length] == '/')
        {
            return normalizedPath[normalizedRoot.Length..];
        }
        return normalizedPath;
    }
}
