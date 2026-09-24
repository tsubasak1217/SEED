// ============================================================
//  LogcatLineParser.cs — logcat の 1 行（-v threadtime）を「重要度・タグ・本文」に分ける（純粋な処理）
//
//  【形】（中核の LogcatStep は adb logcat -v threadtime で流す）
//    09-25 14:03:12.345  1234  1250 I SEED    : [SEED INIT] adapter=…
//    日付  時刻           PID   TID  重要度 タグ（右を空白で詰める）: 本文
//  重要度は V / D / I / W / E / F / A（S は出力されない）。
//  「--------- beginning of main」のような区切りの行など、形に合わない行は null（呼び出し側がそのまま出す）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System.Text.RegularExpressions;

namespace SEEDEditor.AndroidRun;

/// <summary>logcat の 1 行を分けたもの。</summary>
/// <param name="Time">端末の時刻（"MM-dd HH:mm:ss.fff"）。</param>
/// <param name="ProcessId">プロセス ID。</param>
/// <param name="Level">重要度の 1 文字（V / D / I / W / E / F / A）。</param>
/// <param name="Tag">タグ（前後の空白を落としたもの）。</param>
/// <param name="Message">本文。</param>
public sealed record LogcatLine(string Time, int ProcessId, char Level, string Tag, string Message)
{
    /// <summary>エラー以上（E / F / A）か。</summary>
    public bool IsError => Level is 'E' or 'F' or 'A';

    /// <summary>警告（W）か。</summary>
    public bool IsWarning => Level == 'W';
}

/// <summary>logcat の 1 行の解釈。</summary>
public static partial class LogcatLineParser
{
    /// <summary>
    /// -v threadtime の 1 行。タグは「: 」の手前まで（タグの後ろの詰め物の空白は落とす）。本文は空でもよい。
    /// </summary>
    [GeneratedRegex(
        @"^(?<date>\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2}\.\d{3})\s+(?<pid>\d+)\s+(?<tid>\d+)\s+(?<level>[VDIWEFA])\s+(?<tag>.*?)\s*:(?: (?<message>.*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ThreadTimeLine();

    /// <summary>
    /// 1 行を分ける（形に合わなければ null）。
    /// </summary>
    /// <param name="line">logcat の 1 行。</param>
    /// <returns>分けたもの。</returns>
    public static LogcatLine? Parse(string line)
    {
        var match = ThreadTimeLine().Match(line.TrimEnd('\r'));
        if (!match.Success) return null;
        // PID は表示に使うだけなので、桁あふれ等で読めなければ 0 にする（行そのものは捨てない）
        var processId = int.TryParse(
            match.Groups["pid"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)
            ? pid : 0;
        return new LogcatLine(
            $"{match.Groups["date"].Value} {match.Groups["time"].Value}",
            processId,
            match.Groups["level"].Value[0],
            match.Groups["tag"].Value.Trim(),
            match.Groups["message"].Success ? match.Groups["message"].Value : string.Empty);
    }
}
