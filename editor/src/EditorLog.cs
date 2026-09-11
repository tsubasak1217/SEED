using System;
using System.IO;

namespace SEEDEditor;

/// <summary>デバッグ用ログ。ファイルへの追記と UI パネルへのリアルタイム通知を行う。</summary>
internal static class EditorLog
{
    private static readonly string LogPath = ResolveLogPath();

    /// <summary>
    /// ログファイルの絶対パス（editor/logs/SEEDEditor.log）。
    /// AI ツール（seed_log）が末尾 N 行を読み出すために公開する。
    /// ランタイムの stderr も "[STDERR] " 付きでこのファイルへ流れ込む。
    /// </summary>
    internal static string FilePath => LogPath;

    /// <summary>ファイル書き込みの排他ロック（任意スレッドから Write が呼ばれるため）。</summary>
    private static readonly object _fileLock = new();

    /// <summary>
    /// 追記用に開きっぱなしにするライタ。1 行ごとに開いて閉じる同期 I/O は
    /// 大量ログ時に呼び出しスレッド（ランタイム出力の読み取りスレッド等）を詰まらせ、
    /// パイプのバックプレッシャでゲーム本体まで遅くする。開きっぱなしにして
    /// open/close コストを排除する。FileShare.Read で他プロセスからの読み取りは許可する。
    /// </summary>
    private static StreamWriter? _writer = OpenWriter();

    /// <summary>
    /// ライタを開き直すまでの待ち時間。
    ///
    /// 「別のプロジェクトを開く」は新しいプロセスを起こしてから今のプロセスを閉じるため、
    /// 起動した瞬間は前のプロセスがまだログファイルを掴んでいる（FileShare.Read なので
    /// 書き込みで開けない）。そこで開けなかった場合だけ、次の Write で開き直しを試みる。
    /// 毎回試すと失敗時に I/O が重くなるので、この間隔を空ける。
    /// </summary>
    private static readonly TimeSpan ReopenInterval = TimeSpan.FromSeconds(1);

    /// <summary>最後に開き直しを試みた時刻（開けている間は使わない）。</summary>
    private static DateTime _lastReopenAttempt = DateTime.MinValue;

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

    /// <summary>
    /// ライタを開き直す（<see cref="ReopenInterval"/> の間隔を空けて 1 回だけ試す）。
    /// 呼び出し元で <see cref="_fileLock"/> を取っていること。
    /// </summary>
    private static void TryReopenWriter()
    {
        var now = DateTime.UtcNow;
        if (now - _lastReopenAttempt < ReopenInterval) return;
        _lastReopenAttempt = now;

        _writer = OpenWriter();
        // 開き直せたら、ファイル冒頭の見出しをここで書く（静的コンストラクタでは書けていない）。
        try { _writer?.WriteLine($"=== SEEDEditor started {DateTime.Now:HH:mm:ss.fff} ==="); }
        catch { /* ignore */ }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        System.Diagnostics.Debug.WriteLine("[SEEDEditor] " + message);
        // 開きっぱなしのライタへ追記（open/close を避けて高速化）。StreamWriter は
        // スレッドセーフではないためロックで直列化する。
        lock (_fileLock)
        {
            // 起動時に開けなかった場合（前のプロセスがまだ掴んでいる等）は開き直しを試みる。
            // これが無いと、プロセスを起こし直す「別のプロジェクトを開く」の直後に
            // 起動したエディタは、そのセッション中ずっとログを一切残せなくなる。
            if (_writer is null) TryReopenWriter();

            try { _writer?.WriteLine(line); } catch { /* ignore */ }
        }
        try { LogWritten?.Invoke(line); } catch { /* ignore */ }
    }
}
