using System;
using System.IO;

namespace SEEDEditor;

/// <summary>デバッグ用ログ。%TEMP%\SEEDEditor.log に追記する。</summary>
internal static class EditorLog
{
    private static readonly string LogPath = ResolveLogPath();

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
        // 起動時にファイルをリセット
        File.WriteAllText(LogPath, $"=== SEEDEditor started {DateTime.Now:HH:mm:ss.fff} ===\n");
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        System.Diagnostics.Debug.WriteLine("[SEEDEditor] " + message);
        try { File.AppendAllText(LogPath, line + "\n"); } catch { /* ignore */ }
    }
}
