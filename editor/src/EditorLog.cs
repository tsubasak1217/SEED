using System;
using System.IO;

namespace SEEDEditor;

/// <summary>デバッグ用ログ。ファイルへの追記と UI パネルへのリアルタイム通知を行う。</summary>
internal static class EditorLog
{
    private static readonly string LogPath = ResolveLogPath();

    /// <summary>ファイル書き込みの排他ロック（任意スレッドから Write が呼ばれるため）。</summary>
    private static readonly object _fileLock = new();

    /// <summary>
    /// 追記用に開きっぱなしにするライタ。1 行ごとに開いて閉じる同期 I/O は
    /// 大量ログ時に呼び出しスレッド（ランタイム出力の読み取りスレッド等）を詰まらせ、
    /// パイプのバックプレッシャでゲーム本体まで遅くする。開きっぱなしにして
    /// open/close コストを排除する。FileShare.Read で他プロセスからの読み取りは許可する。
    /// </summary>
    private static readonly StreamWriter? _writer = OpenWriter();

    /// <summary>新しいログ行が追加されたときに発火する（任意スレッドから呼ばれる）。</summary>
    public static event Action<string>? LogWritten;

    private static StreamWriter? OpenWriter()
    {
        try
        {
            // FileMode.Create: 起動時にファイルをリセット。FileShare.Read: 読み取り併用可。
            var fs = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            return new StreamWriter(fs) { AutoFlush = true };
        }
        catch { return null; }
    }

    private static string ResolveLogPath()
    {
        // bin/Debug/net9.0-windows/ から 3階層上が editor/
        var editorDir = Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\"));
        var logsDir = Path.Combine(editorDir, "logs");
        Directory.CreateDirectory(logsDir);
        return Path.Combine(logsDir, "SEEDEditor.log");
    }

    static EditorLog()
    {
        // 起動見出し（ファイルは OpenWriter の FileMode.Create で既にリセット済み）。
        lock (_fileLock)
        {
            try { _writer?.WriteLine($"=== SEEDEditor started {DateTime.Now:HH:mm:ss.fff} ==="); }
            catch { /* ignore */ }
        }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        System.Diagnostics.Debug.WriteLine("[SEEDEditor] " + message);
        // 開きっぱなしのライタへ追記（open/close を避けて高速化）。StreamWriter は
        // スレッドセーフではないためロックで直列化する。
        lock (_fileLock)
        {
            try { _writer?.WriteLine(line); } catch { /* ignore */ }
        }
        try { LogWritten?.Invoke(line); } catch { /* ignore */ }
    }
}
