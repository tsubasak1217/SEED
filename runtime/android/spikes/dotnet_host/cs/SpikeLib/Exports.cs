// =============================================================================
// Exports.cs
// Rust(spike_host) から hostfxr の get_function_with_unmanaged_callers_only で
// 関数ポインタとして取得・呼び出しされるエクスポート関数群。
// =============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SpikeLib;

/// <summary>
/// ネイティブ（Rust）へ公開する関数群。すべて cdecl の UnmanagedCallersOnly。
/// ネイティブ境界をマネージド例外が越えるとプロセスが即死するため、
/// 各関数は内部で必ず例外を捕捉して戻り値で失敗を返す。
/// </summary>
public static unsafe class Exports
{
    /// <summary>ProbeHardwareException の kind: null 参照（SIGSEGV → NullReferenceException）。</summary>
    public const int ProbeKindNullReference = 0;

    /// <summary>ProbeHardwareException の kind: 整数 0 除算（x64 は SIGFPE → DivideByZeroException）。</summary>
    public const int ProbeKindDivideByZero = 1;

    /// <summary>ProbeHardwareException の戻り値: 期待した例外を捕捉できた。</summary>
    public const int ProbeResultCaught = 1;

    /// <summary>ProbeHardwareException の戻り値: 例外が発生しなかった。</summary>
    public const int ProbeResultNoException = 0;

    /// <summary>ProbeHardwareException の戻り値: 期待と異なる例外を捕捉した。</summary>
    public const int ProbeResultUnexpected = -1;

    /// <summary>最小の疎通確認。a + b を返すだけ。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Add(int a, int b) => a + b;

    /// <summary>
    /// 全チェックを実行し、結果の UTF-8 テキストを buf に書き込む。
    /// </summary>
    /// <param name="buf">書き込み先（Rust 側が確保）</param>
    /// <param name="cap">buf の容量（バイト）</param>
    /// <returns>書き込んだバイト数。容量不足なら必要バイト数の負値。</returns>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int RunChecks(byte* buf, int cap)
    {
        try
        {
            string report = Checks.RunAll();
            return Utf8Buffer.Write(report, buf, cap);
        }
        catch (Exception ex)
        {
            // RunAll 自体は各チェックの例外を握るので、ここに来るのは想定外の致命的失敗のみ
            return Utf8Buffer.Write("FATAL\t" + ex, buf, cap);
        }
    }

    /// <summary>
    /// UTF-8 往復確認。Rust から受け取った UTF-8 を decode し、
    /// 「echo(コードポイント数):元の文字列」を UTF-8 で書き戻す。
    /// </summary>
    /// <returns>書き込んだバイト数。容量不足なら必要バイト数の負値。</returns>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int EchoUtf8(byte* src, int srcLen, byte* dst, int dstCap)
    {
        try
        {
            string text = Encoding.UTF8.GetString(src, srcLen);
            // サロゲートペア（絵文字など）を 1 文字と数えるため Rune 単位で数える
            int runeCount = 0;
            foreach (var _ in text.EnumerateRunes()) runeCount++;
            return Utf8Buffer.Write($"echo({runeCount}):{text}", dst, dstCap);
        }
        catch (Exception ex)
        {
            return Utf8Buffer.Write("FATAL\t" + ex, dst, dstCap);
        }
    }

    /// <summary>
    /// ハードウェア例外（シグナル）がマネージド例外へ変換されるかを確認する。
    /// Rust の sigaltstack が小さいとシグナルハンドラ内でプロセスごと落ちる可能性があるため、
    /// RunChecks とは分離して最後に呼ぶ。
    /// </summary>
    /// <param name="kind">ProbeKind* のいずれか</param>
    /// <returns>ProbeResult* のいずれか</returns>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ProbeHardwareException(int kind)
    {
        try
        {
            if (kind == ProbeKindNullReference)
            {
                HardwareFault.ReadNullField();
            }
            else
            {
                HardwareFault.DivideByZero();
            }
            return ProbeResultNoException;
        }
        catch (NullReferenceException) when (kind == ProbeKindNullReference)
        {
            return ProbeResultCaught;
        }
        catch (DivideByZeroException) when (kind == ProbeKindDivideByZero)
        {
            return ProbeResultCaught;
        }
        catch (Exception)
        {
            return ProbeResultUnexpected;
        }
    }
}

/// <summary>
/// ハードウェア例外を確実に発生させるための補助。
/// JIT に明示的な null チェックや定数畳み込みをさせないよう、値は static フィールド経由で渡し、インライン化も禁止する。
/// </summary>
internal static class HardwareFault
{
    /// <summary>フィールドを持つだけの参照型（null のままにしておく）。</summary>
    private sealed class Holder
    {
        public int Value;
    }

    /// <summary>常に null。JIT が null と見抜けないよう static に置く。</summary>
    private static Holder? s_nullHolder;

    /// <summary>常に 0。除数として使う。</summary>
    private static int s_zero;

    /// <summary>null 参照のフィールド読み取り（メモリアクセス違反 → SIGSEGV）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ReadNullField() => s_nullHolder!.Value;

    /// <summary>整数の 0 除算（x64 では idiv が SIGFPE を出す。arm64 は JIT が明示チェックを入れる）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int DivideByZero() => 100 / s_zero;
}

/// <summary>UTF-8 文字列をネイティブのバッファへ書き込む共通処理。</summary>
internal static unsafe class Utf8Buffer
{
    /// <summary>
    /// text を UTF-8 で dst に書き込む（NUL 終端はしない）。
    /// </summary>
    /// <returns>書き込んだバイト数。容量不足なら必要バイト数の負値。</returns>
    public static int Write(string text, byte* dst, int cap)
    {
        int need = Encoding.UTF8.GetByteCount(text);
        if (dst == null || need > cap) return -need;
        fixed (char* p = text)
        {
            return Encoding.UTF8.GetBytes(p, text.Length, dst, cap);
        }
    }
}
