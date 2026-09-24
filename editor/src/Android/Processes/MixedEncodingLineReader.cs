// ============================================================
//  MixedEncodingLineReader.cs — 子プロセスの出力を 1 行ずつ文字列にする（UTF-8 と ANSI の混在に耐える）
//
//  【なぜ要るのか】
//  Android のビルドで起動する道具は出力の文字コードがまちまち:
//    adb（logcat のエンジンの日本語ログ）・cargo … UTF-8
//    Gradle（Java）・dotnet の子プロセス … 出力がパイプのときは Windows の ANSI コードページ（日本語環境は CP932）になり得る
//  従来の build_and_run.ps1 は adb の UTF-8 をコンソールのコードページで読んでログを化けさせていた（docs/backlog.md）。
//  ここでは行ごとに「厳密な UTF-8 として読めるか」を試し、読めなければ ANSI コードページで読む。
//  日本語の CP932 のバイト列が偶然 UTF-8 として正しい並びになることはまず無いので、この判定で取り違えない。
//
//  行の区切りは \n・\r\n・\r（単独の \r も進捗表示の区切りとして 1 行に数える）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Android.Processes;

/// <summary>子プロセスの出力（バイト列）を行ごとに文字列にする。</summary>
public static class MixedEncodingLineReader
{
    /// <summary>1 回の読み取りの大きさ（バイト）。</summary>
    private const int ReadBufferSize = 16 * 1024;

    /// <summary>行の区切り（改行）。</summary>
    private const byte LineFeed = (byte)'\n';

    /// <summary>行の区切り（復帰。\r\n の \r か、単独の \r）。</summary>
    private const byte CarriageReturn = (byte)'\r';

    /// <summary>厳密な UTF-8（不正なバイト列で例外を投げる。BOM は付けない）。</summary>
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>UTF-8 として読めない行に使う文字コード（Windows の ANSI コードページ。取れなければ Latin-1）。</summary>
    private static readonly Encoding FallbackEncoding = CreateFallbackEncoding();

    /// <summary>
    /// ストリームを終わりまで読み、1 行ごとに <paramref name="onLine"/> を呼ぶ（行末の改行は含めない）。
    /// </summary>
    /// <param name="stream">子プロセスの標準出力・標準エラー。</param>
    /// <param name="onLine">1 行ごとに呼ばれる。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    public static async Task ReadLinesAsync(Stream stream, Action<string> onLine, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferSize];
        var line = new ArrayBufferWriter<byte>();
        var previousWasCarriageReturn = false;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var chunk = new ReadOnlySpan<byte>(buffer, 0, read);
            while (!chunk.IsEmpty)
            {
                // 次の区切り（\r か \n）までを溜めに足す
                var separator = chunk.IndexOfAny(CarriageReturn, LineFeed);
                if (separator < 0)
                {
                    line.Write(chunk);
                    previousWasCarriageReturn = false;
                    break;
                }
                if (separator > 0)
                {
                    line.Write(chunk[..separator]);
                    previousWasCarriageReturn = false;
                }

                if (chunk[separator] == LineFeed)
                {
                    // \r\n の \n は、直前の \r で行を出し終えているので何もしない
                    if (!previousWasCarriageReturn) EmitLine(line, onLine);
                    previousWasCarriageReturn = false;
                }
                else
                {
                    EmitLine(line, onLine);
                    previousWasCarriageReturn = true;
                }
                chunk = chunk[(separator + 1)..];
            }
        }
        // 改行で終わらない最後の行
        if (line.WrittenCount > 0) EmitLine(line, onLine);
    }

    /// <summary>
    /// バイト列を文字列にする（厳密な UTF-8 で読めればそれ、読めなければ ANSI コードページ）。
    /// </summary>
    /// <param name="bytes">1 行ぶんのバイト列（改行を含まない）。</param>
    /// <returns>文字列。</returns>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return FallbackEncoding.GetString(bytes);
        }
    }

    /// <summary>溜めた 1 行を文字列にして渡し、溜めを空にする。</summary>
    /// <param name="line">溜めた行のバイト列。</param>
    /// <param name="onLine">1 行ごとに呼ばれる。</param>
    private static void EmitLine(ArrayBufferWriter<byte> line, Action<string> onLine)
    {
        var text = Decode(line.WrittenSpan);
        line.Clear();
        onLine(text);
    }

    /// <summary>UTF-8 として読めない行に使う文字コードを作る。</summary>
    /// <returns>Windows なら ANSI コードページ、それ以外（・取れないとき）は Latin-1。</returns>
    private static Encoding CreateFallbackEncoding()
    {
        if (!OperatingSystem.IsWindows()) return Encoding.Latin1;
        try
        {
            // .NET は既定で UTF-8・ASCII・Latin-1 ほかしか持たないので、コードページの表から引く
            return CodePagesEncodingProvider.Instance.GetEncoding((int)GetACP()) ?? Encoding.Latin1;
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Windows の ANSI コードページの番号（日本語環境は 932）。</summary>
    /// <returns>コードページ番号。</returns>
    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
