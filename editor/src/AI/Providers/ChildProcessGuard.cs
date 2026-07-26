// ============================================================
//  ChildProcessGuard.cs — 子プロセスの強制終了保証（Windows Job Object）
//
//  エディタが Task Manager などで強制終了された場合でも、
//  起動した Gemini CLI / Claude Code などの子プロセスが
//  確実に終了するよう Windows ジョブオブジェクトに登録する。
//
//  JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE フラグを設定することで、
//  ジョブオブジェクトの最後のハンドル（親プロセスが保持）が
//  閉じられたタイミングですべての子プロセスが OS によって強制終了される。
// ============================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SEEDEditor.AI.Providers;

/// <summary>
/// 子プロセスを Windows ジョブオブジェクトに登録し、
/// 親プロセス（エディタ）が終了したときに子プロセスも確実に終了させる。
/// </summary>
internal static class ChildProcessGuard
{
    /// <summary>ジョブオブジェクトのハンドル（プロセス終了まで保持）。</summary>
    private static readonly IntPtr s_jobHandle;

    static ChildProcessGuard()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        s_jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (s_jobHandle == IntPtr.Zero) return;

        // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: ジョブハンドルが閉じられると
        // 登録済みのすべてのプロセスを OS が強制終了する
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000;

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(s_jobHandle, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)length);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// 指定プロセスをジョブオブジェクトに登録する。
    /// 登録後は親プロセスが強制終了されたときに子プロセスも自動終了する。
    /// </summary>
    public static void Track(Process process)
    {
        if (s_jobHandle == IntPtr.Zero) return;
        try { AssignProcessToJobObject(s_jobHandle, process.Handle); } catch { }
    }

    // ── Win32 API ────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    // ── 構造体 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// ジョブオブジェクトの基本制限情報を表す Win32 構造体。
    /// このクラスでは LimitFlags のみ使用し、JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE を設定する。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long    PerProcessUserTimeLimit;
        public long    PerJobUserTimeLimit;
        public uint    LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint    ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint    PriorityClass;
        public uint    SchedulingClass;
    }

    /// <summary>
    /// ジョブオブジェクトの I/O カウンタを表す Win32 構造体。
    /// JOBOBJECT_EXTENDED_LIMIT_INFORMATION のメモリレイアウトを満たすために存在し、
    /// このクラスでは値を参照しない。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary>
    /// ジョブオブジェクトの拡張制限情報を表す Win32 構造体。
    /// SetInformationJobObject に渡され、BasicLimitInformation.LimitFlags で
    /// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE を有効化するために使用する。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr     ProcessMemoryLimit;
        public UIntPtr     JobMemoryLimit;
        public UIntPtr     PeakProcessMemoryUsed;
        public UIntPtr     PeakJobMemoryUsed;
    }
}
