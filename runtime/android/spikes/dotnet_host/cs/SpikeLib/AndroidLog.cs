// =============================================================================
// AndroidLog.cs
// liblog.so の __android_log_write を関数ポインタで直接呼び、logcat へ出力する。
// アプリプロセスでは stdout/stderr が捨てられるため、段階B ではこの経路（またはホスト経由）が必要になる。
// =============================================================================
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SpikeLib;

/// <summary>logcat 出力の薄いラッパー。</summary>
internal static unsafe class AndroidLog
{
    /// <summary>Android のログライブラリ名。</summary>
    private const string LibraryName = "liblog.so";

    /// <summary>書き込み関数のシンボル名。int __android_log_write(int prio, const char* tag, const char* text)</summary>
    private const string WriteSymbol = "__android_log_write";

    /// <summary>ANDROID_LOG_INFO の値（android/log.h）。</summary>
    public const int PriorityInfo = 4;

    /// <summary>logcat のタグ。</summary>
    public const string Tag = "SEEDSpike";

    /// <summary>
    /// logcat へ 1 行書く。
    /// </summary>
    /// <returns>(成功したか, 詳細)</returns>
    public static (bool ok, string detail) Write(int priority, string message)
    {
        if (!NativeLibrary.TryLoad(LibraryName, out IntPtr handle))
        {
            return (false, $"{LibraryName} を読み込めない");
        }
        if (!NativeLibrary.TryGetExport(handle, WriteSymbol, out IntPtr fn))
        {
            return (false, $"{WriteSymbol} が見つからない");
        }
        // C 文字列として渡すため NUL 終端を付ける
        byte[] tag = Encoding.UTF8.GetBytes(Tag + "\0");
        byte[] text = Encoding.UTF8.GetBytes(message + "\0");
        fixed (byte* pTag = tag)
        fixed (byte* pText = text)
        {
            var write = (delegate* unmanaged[Cdecl]<int, byte*, byte*, int>)fn;
            int rc = write(priority, pTag, pText);
            return (rc > 0, $"rc={rc} tag={Tag}");
        }
    }
}
