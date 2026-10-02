// ============================================================
//  RuntimeLog.cs — ランタイムの標準出力（ログのファイル）を追いかけて読む
//
//  【役割】
//  QuietProcess がランタイムの標準出力・標準エラーをファイルへ書かせるので、それを後ろから少しずつ読み、
//  行に分けて返す。舞台のスクリプトの合図（[THUMB] ready / failed）と、件ごとの注意すべき行
//  （読めない画像・スクリプトの例外など）をここから拾う。
//  文字コードは UTF-8（Rust の出力も、ランタイムに載った .NET の出力も UTF-8）。
// ============================================================

using System.Text;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Runtime;

/// <summary>ログのファイルを後ろから読む。1 つのスレッドから使う。</summary>
public sealed class RuntimeLog
{
    /// <summary>1 回に読む上限（バイト）。</summary>
    private const int ReadChunkBytes = 64 * 1024;

    /// <summary>待つ間にファイルを見直す間隔（ミリ秒）。</summary>
    private const int PollIntervalMs = 50;

    /// <summary>ログのファイルの絶対パス。</summary>
    public string Path { get; }

    /// <summary>次に読む位置（バイト）。</summary>
    private long _offset;

    /// <summary>行の途中で読み終えた残り（次に読んだ分の頭につなぐ）。</summary>
    private readonly StringBuilder _partial = new();

    /// <summary>UTF-8 の復号器（文字の途中で切れたバイトを次へ持ち越す）。</summary>
    private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();

    /// <summary>
    /// ログのファイルを指定して作る（まだ無くてもよい）。
    /// </summary>
    /// <param name="path">ログのファイルの絶対パス。</param>
    public RuntimeLog(string path) => Path = path;

    /// <summary>
    /// 前に読んだ後に増えた行を返す（行の途中は次回へ持ち越す）。
    /// </summary>
    /// <returns>増えた行（前後の空白を落とした本文）。</returns>
    public IReadOnlyList<string> ReadNewLines()
    {
        var lines = new List<string>();
        if (!File.Exists(Path)) return lines;
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= _offset) return lines;
        stream.Seek(_offset, SeekOrigin.Begin);
        var bytes = new byte[ReadChunkBytes];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(ReadChunkBytes)];
        int read;
        while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
        {
            _offset += read;
            int count = _decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            for (int i = 0; i < count; i++)
            {
                char c = chars[i];
                if (c == '\n')
                {
                    lines.Add(_partial.ToString().Trim());
                    _partial.Clear();
                }
                else _partial.Append(c);
            }
        }
        return lines;
    }

    /// <summary>読んだがまだ渡していない行（合図の行より後に同じ回で読んだ行を次の待ちへ残す）。</summary>
    private readonly Queue<string> _pending = new();

    /// <summary>
    /// 条件に合う行が出るまで待つ。待つ間に読んだ行（合った行まで）はすべて <paramref name="seen"/> へ積む。
    /// 合った行より後に読んだ行は捨てずに次の待ちへ残す。
    /// </summary>
    /// <param name="match">待つ行の条件。</param>
    /// <param name="timeout">待つ上限。</param>
    /// <param name="seen">読んだ行の積み先。</param>
    /// <param name="keepWaiting">待ち続けてよいか（ランタイムが生きているか等。偽なら諦める）。</param>
    /// <returns>合った行（時間切れ・諦めたら null）。</returns>
    public string? WaitFor(Func<string, bool> match, TimeSpan timeout, List<string> seen, Func<bool> keepWaiting)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            foreach (var line in ReadNewLines()) _pending.Enqueue(line);
            while (_pending.Count > 0)
            {
                var line = _pending.Dequeue();
                seen.Add(line);
                if (match(line)) return line;
            }
            if (DateTime.UtcNow >= deadline || !keepWaiting()) return null;
            Thread.Sleep(PollIntervalMs);
        }
    }

    /// <summary>
    /// いまある行（残っていた行と、新しく増えた行）をすべて渡す。
    /// </summary>
    /// <returns>行。</returns>
    public IReadOnlyList<string> Drain()
    {
        foreach (var line in ReadNewLines()) _pending.Enqueue(line);
        var all = _pending.ToList();
        _pending.Clear();
        return all;
    }
}
