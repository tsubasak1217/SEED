using System;
using System.IO;

namespace SEEDEditor.Runtime;

/// <summary>
/// runtime/src/ 以下の .rs / .toml / .lock を監視し、
/// ビルドが必要かどうかを IsDirty で管理する。
///
/// 初期化時に mtime 比較で「ビルド済みかどうか」を判定する。
/// 以降はファイルシステムイベントで IsDirty を更新する。
/// </summary>
public sealed class RuntimeSourceWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private volatile bool _dirty;

    public bool IsDirty => _dirty;

    /// <summary>監視対象ファイルが変更されたときに発火する。</summary>
    public event Action? Changed;

    /// <param name="runtimeSourceDir">runtime/ ディレクトリ（Cargo.toml の親）</param>
    /// <param name="exePath">起動対象の exe フルパス（mtime 比較に使う）</param>
    public RuntimeSourceWatcher(string runtimeSourceDir, string exePath)
    {
        _dirty = CheckDirty(runtimeSourceDir, exePath);

        _watcher = new FileSystemWatcher(runtimeSourceDir)
        {
            Filter                = "*.*",
            IncludeSubdirectories = true,
            NotifyFilter          = NotifyFilters.LastWrite
                                  | NotifyFilters.FileName
                                  | NotifyFilters.DirectoryName,
        };

        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Deleted += OnFsEvent;
        _watcher.Renamed += (_, e) =>
        {
            if (IsRelevant(e.Name) || IsRelevant(e.OldName))
                MarkDirty();
        };

        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>ビルド成功後に呼ぶ。次回変更があるまで IsDirty = false になる。</summary>
    public void MarkClean() => _dirty = false;

    // ─── 内部ヘルパー ────────────────────────────────────────────

    private void OnFsEvent(object _, FileSystemEventArgs e)
    {
        if (IsRelevant(e.Name)) MarkDirty();
    }

    private void MarkDirty()
    {
        _dirty = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// 拡張子が .rs / .toml / .lock のファイルのみ対象とする。
    /// target/ 配下は除外（ビルド成果物が変化してもトリガーしない）。
    /// </summary>
    private static bool IsRelevant(string? name)
    {
        if (name is null) return false;
        if (name.StartsWith("target", StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(name);
        return ext is ".rs" or ".toml" or ".lock";
    }

    /// <summary>
    /// exe が存在しないか、いずれかのソースファイルが exe より新しければ true（要ビルド）を返す。
    /// 判定理由をログに出力する。
    /// </summary>
    private static bool CheckDirty(string sourceDir, string exePath)
    {
        if (!File.Exists(exePath))
        {
            EditorLog.Write($"[Watcher] exe が存在しない → ビルド必要  path={exePath}");
            return true;
        }

        var exeTime = File.GetLastWriteTimeUtc(exePath);
        EditorLog.Write($"[Watcher] exe mtime={exeTime:u}  path={exePath}");

        // src/**/*.rs
        var srcDir = Path.Combine(sourceDir, "src");
        if (!Directory.Exists(srcDir))
        {
            EditorLog.Write($"[Watcher] src/ ディレクトリが存在しない → ビルド必要  path={srcDir}");
            return true;
        }

        foreach (var f in Directory.EnumerateFiles(srcDir, "*.rs", SearchOption.AllDirectories))
        {
            var ft = File.GetLastWriteTimeUtc(f);
            if (ft > exeTime)
            {
                EditorLog.Write($"[Watcher] 変更検出 → ビルド必要  file={f}  mtime={ft:u}");
                return true;
            }
        }

        // Cargo.toml / Cargo.lock
        foreach (var f in Directory.EnumerateFiles(sourceDir, "Cargo.*", SearchOption.TopDirectoryOnly))
        {
            var ft = File.GetLastWriteTimeUtc(f);
            if (ft > exeTime)
            {
                EditorLog.Write($"[Watcher] 変更検出 → ビルド必要  file={f}  mtime={ft:u}");
                return true;
            }
        }

        EditorLog.Write("[Watcher] すべてのソースが exe より古い → ビルドスキップ");
        return false;
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}
