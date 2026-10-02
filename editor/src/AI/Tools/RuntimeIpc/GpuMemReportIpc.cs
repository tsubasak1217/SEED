// ============================================================
//  GpuMemReportIpc.cs — GPU メモリの内訳（GPU_MEM_REPORT）の組み立てと応答の解釈
//
//  【ワイヤ形式】（正典はランタイム: runtime/src/engine/core/app_base/ipc.rs の IpcCommand::GpuMemReport と
//               app/gpu_mem_ops.rs。docs/rendering_profiles.md §4）
//    エディタ → ランタイム: GPU_MEM_REPORT（一時フォルダへ書く）/ GPU_MEM_REPORT:{書き出し先の絶対パス}
//    ランタイム → エディタ: GPU_MEM_REPORT_DONE:{書いた JSON のパス} / GPU_MEM_REPORT_ERROR:{理由}
//
//  計測そのものは**ランタイムの起動時に**有効にしておく必要がある（環境変数 SEED_GPU_MEM_LOG=1 か
//  起動引数 --gpu-mem-log）。MCP からは seed_launch(gpu_mem_log:true) で有効にできる
//  （SeedMcpServer/Launcher.cs → エディタの環境変数 → ランタイムが継ぐ）。
//
//  【書き出し先】
//  AI ツールはパスを外から受け取らず、エディタが OS の一時フォルダの下（スクリーンショットと同じ
//  seed_mcp/）に決める。外から任意のパスを渡せると、観測系の命令でプロジェクトのファイルを
//  上書きできてしまうため（docs/editor_mcp.md §7.2）。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/AiSafetyTests）がリンクして試すので WPF 型を使わない。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SEEDEditor.AI.Tools.RuntimeIpc;

/// <summary>
/// GPU_MEM_REPORT の命令の組み立てと応答の解釈。状態を持たない。
/// </summary>
public static class GpuMemReportIpc
{
    // ── ワイヤの頭（ランタイムの ipc.rs / gpu_mem_ops.rs と一致させる）──────

    /// <summary>命令（パスを付けないときはこれだけを送る）。</summary>
    public const string Command = "GPU_MEM_REPORT";

    /// <summary>命令とパスの区切り。</summary>
    public const string PathSeparator = ":";

    /// <summary>書けたときの応答の頭（後ろに JSON のパス）。</summary>
    public const string DonePrefix = "GPU_MEM_REPORT_DONE:";

    /// <summary>出せなかったときの応答の頭（後ろに理由）。</summary>
    public const string ErrorPrefix = "GPU_MEM_REPORT_ERROR:";

    /// <summary>待ち合わせで拾う応答の頭（どちらか一方が必ず 1 行届く）。</summary>
    public static IReadOnlyList<string> ReplyPrefixes { get; } = [DonePrefix, ErrorPrefix];

    // ── エディタが決める書き出し先 ───────────────────────────

    /// <summary>書き出し先のファイル名の頭（ランタイムの既定 seed_gpu_mem_ と同じ）。</summary>
    public const string FileNamePrefix = "seed_gpu_mem_";

    /// <summary>書き出し先のファイル名の時刻の書式（同じ秒に何度出しても衝突しないようミリ秒まで）。</summary>
    public const string FileTimeFormat = "yyyyMMdd_HHmmss_fff";

    /// <summary>書き出し先の拡張子。</summary>
    public const string FileExtension = ".json";

    /// <summary>1 行の命令に載せられない文字（IPC は 1 行 = 1 通）。</summary>
    private static readonly char[] LineBreaks = ['\r', '\n'];

    // ============================================================
    //  組み立て（エディタ → ランタイム）
    // ============================================================

    /// <summary>
    /// 命令の 1 行を組み立てる。
    /// </summary>
    /// <param name="outputPath">書き出し先の絶対パス（null・空ならランタイムの一時フォルダ）。</param>
    /// <returns>ランタイムへ送る 1 行。</returns>
    /// <exception cref="ArgumentException">パスが改行を含む（1 行の命令に載らない）とき。</exception>
    public static string BuildCommand(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return Command;
        if (outputPath.IndexOfAny(LineBreaks) >= 0)
            throw new ArgumentException("書き出し先のパスに改行は使えません", nameof(outputPath));
        return Command + PathSeparator + outputPath.Trim();
    }

    /// <summary>
    /// エディタが決める書き出し先（{一時フォルダの下の決まったフォルダ}/seed_gpu_mem_{時刻}.json）を作る。
    /// フォルダはまだ作らない（呼び出し側が作る。ランタイムの書き出しは親フォルダを作らないため）。
    /// </summary>
    /// <param name="directory">置き場のフォルダ（OS の一時フォルダの下のサブフォルダ）。</param>
    /// <param name="now">ファイル名に入れる時刻。</param>
    /// <returns>書き出し先の絶対パス。</returns>
    public static string MakeOutputPath(string directory, DateTime now) =>
        Path.GetFullPath(Path.Combine(
            directory,
            FileNamePrefix + now.ToString(FileTimeFormat, CultureInfo.InvariantCulture) + FileExtension));

    // ============================================================
    //  解釈（ランタイム → エディタ）
    // ============================================================

    /// <summary>
    /// 応答の 1 行を読む。
    /// </summary>
    /// <param name="line">届いた行。</param>
    /// <param name="ok">書けたか（DONE）。</param>
    /// <param name="payload">DONE なら JSON のパス、ERROR なら理由。</param>
    /// <returns>GPU_MEM_REPORT の応答として読めたら true。</returns>
    public static bool TryParseReply(string line, out bool ok, out string payload)
    {
        if (line.StartsWith(DonePrefix, StringComparison.Ordinal))
        {
            ok      = true;
            payload = line[DonePrefix.Length..].Trim();
            return true;
        }
        if (line.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            ok      = false;
            payload = line[ErrorPrefix.Length..];
            return true;
        }
        ok      = false;
        payload = "";
        return false;
    }
}
