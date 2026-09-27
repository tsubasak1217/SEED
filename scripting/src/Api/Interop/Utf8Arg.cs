using System;
using System.Buffers;
using System.Text;

namespace SEED;

/// <summary>
/// FFI（Rust の ScriptHostApi）へ渡す文字列の UTF-8 バイト列の入れ物。
///
/// <para><b>なぜ要るか（W1-S）</b><br/>
/// 以前は各 FFI 呼び出しが <c>stackalloc byte[文字列の長さ]</c> で、長さの上限なしにスタックへ確保していた。
/// 数百 KB〜MB の JSON を 1 キーに入れる <c>SaveData.SetString</c> ではスタックが溢れ、.NET のスタックオーバーフローは
/// 捕まえられないのでプロセスごと落ちる。この入れ物は、短い文字列（<see cref="StackLimitBytes"/> 以下と保証できるもの）だけを
/// 呼び出し元のスタックに置き、長い文字列は <see cref="ArrayPool{T}.Shared"/> から借りたヒープの配列に置く。
/// </para>
///
/// <para><b>使い方</b>（スタックの確保は呼び出し元の関数の中でしかできないので、大きさだけをこの型が決める）</para>
/// <code>
/// using var kb = new Utf8Arg(key, stackalloc byte[Utf8Arg.StackBytesFor(key)]);
/// fixed (byte* kp = kb.Bytes)
///     return _api.SaveCtl(kind, kp, kb.Length) != 0;
/// </code>
/// <para>
/// 空の文字列（と null）は長さ 0 のバイト列になり、<c>fixed</c> のポインタは null になる（従来の <c>stackalloc byte[0]</c> と同じ。
/// Rust 側の <c>str_from</c> は長さ 0 を空文字として扱う）。借りた配列は <c>Dispose</c>（<c>using</c>）でプールへ返す。
/// </para>
/// </summary>
internal readonly ref struct Utf8Arg
{
    /// <summary>
    /// 呼び出し元のスタックに置く上限（バイト）。これを超えうる文字列は ArrayPool の配列を借りる。
    /// 1 回の FFI 呼び出しで文字列を 3 つまで渡すので、スタックの使用は 1 呼び出しあたり高々数 KB に収まる。
    /// </summary>
    internal const int StackLimitBytes = 1024;

    /// <summary>
    /// UTF-8 で 1 文字（UTF-16 の 1 単位）が最大何バイトになるか。
    /// 大きな文字列で <see cref="Encoding.GetMaxByteCount"/> の掛け算が溢れて例外になる前に、文字数で先に振り分けるために使う。
    /// </summary>
    private const int MaxUtf8BytesPerChar = 3;

    /// <summary>ArrayPool から借りた配列（スタックに置いたときは null）。</summary>
    private readonly byte[]? _rented;

    /// <summary>文字列の UTF-8 バイト列（スタックか、借りた配列の先頭部分）。</summary>
    private readonly Span<byte> _bytes;

    /// <summary>
    /// 呼び出し元が <c>stackalloc</c> する大きさ（バイト）を返す。
    /// UTF-8 の最大長が <see cref="StackLimitBytes"/> 以下と保証できる文字列だけその最大長、超えうるなら 0（ヒープを使う）。
    /// 文字数だけで決めるので O(1)（長い文字列を数え直さない）。
    /// </summary>
    /// <param name="text">渡す文字列（null は空文字とみなす）。</param>
    internal static int StackBytesFor(string? text)
    {
        int chars = text?.Length ?? 0;
        if (chars >= StackLimitBytes / MaxUtf8BytesPerChar) return 0;
        int max = Encoding.UTF8.GetMaxByteCount(chars);
        return max <= StackLimitBytes ? max : 0;
    }

    /// <summary>
    /// 文字列を UTF-8 にする。<paramref name="stackBuffer"/> に必ず収まるならそこへ、収まらなければ ArrayPool の配列へ書く。
    /// </summary>
    /// <param name="text">渡す文字列（null は空文字とみなす）。</param>
    /// <param name="stackBuffer">呼び出し元が <c>stackalloc byte[StackBytesFor(text)]</c> で確保した領域（0 バイトならヒープを使う）。</param>
    internal Utf8Arg(string? text, Span<byte> stackBuffer)
    {
        text ??= string.Empty;
        if (text.Length == 0)
        {
            // 空文字は長さ 0（ポインタは null になる。Rust 側は長さで判断する）
            _rented = null;
            _bytes = default;
            return;
        }
        if (text.Length < StackLimitBytes / MaxUtf8BytesPerChar
            && Encoding.UTF8.GetMaxByteCount(text.Length) <= stackBuffer.Length)
        {
            // 最大長が収まると保証できるので、数えずに 1 回で書く
            _rented = null;
            _bytes = stackBuffer[..Encoding.UTF8.GetBytes(text, stackBuffer)];
            return;
        }
        // 長い文字列: ちょうどの長さを数えてプールから借りる（Rent は要求以上の長さの配列を返す）
        int count = Encoding.UTF8.GetByteCount(text);
        _rented = ArrayPool<byte>.Shared.Rent(count);
        _bytes = _rented.AsSpan(0, Encoding.UTF8.GetBytes(text, _rented));
    }

    /// <summary>UTF-8 のバイト列（<c>fixed</c> でポインタにして FFI へ渡す）。</summary>
    internal Span<byte> Bytes => _bytes;

    /// <summary>UTF-8 のバイト数（FFI の長さの引数）。</summary>
    internal int Length => _bytes.Length;

    /// <summary>借りた配列をプールへ返す（<c>using</c> で 1 回だけ呼ばれる）。</summary>
    public void Dispose()
    {
        if (_rented is not null) ArrayPool<byte>.Shared.Return(_rented);
    }
}
