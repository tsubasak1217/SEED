// ============================================================
//  DetachedProcess.cs — 呼び出し元から切り離した子プロセスを起動する（エミュレータ用。Windows の CreateProcessW）
//
//  【なぜ Process.Start を使わないか】
//  エミュレータ（emulator.exe → qemu）はエディタ・SeedAndroid が終わった後も動き続けるもの。.NET の Process.Start は
//  ハンドルを継承させる（bInheritHandles = TRUE）ので、呼び出し元の標準出力がパイプ（SeedAndroid をパイプへつないだとき等）だと
//  エミュレータがその書き口を握ったままになり、読み手はエミュレータが終わるまで EOF を受け取れない（終わらない）。
//  出力をパイプで受けると、今度は読み手がいなくなった後にエミュレータが書き込めなくなる。
//  そこで CreateProcessW を直接呼び、
//    - ハンドルを継承させない（bInheritHandles = FALSE）
//    - コンソールの窓を出さない（CREATE_NO_WINDOW。エミュレータ本体の画面は普通に出る。ログは見えないコンソールへ）
//    - Ctrl+C を受け取らない（CREATE_NEW_PROCESS_GROUP）
//    - 呼び出し元のジョブ（端末の窓を閉じると子も止めるジョブ）から外れる（CREATE_BREAKAWAY_FROM_JOB。
//      ジョブが許さなければ付けずに起動し直す）
//  で起動する。止めるのは呼び出し側の役目ではない（エミュレータは adb emu kill か窓を閉じて止める）。
//
//  Windows 専用（Android の道具が Windows 前提。docs/android.md §3）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SEEDEditor.Android.Processes;

/// <summary>切り離して起動した子プロセス（終わったか・終了コードを見るだけ。止めない）。</summary>
public interface IDetachedProcess : IDisposable
{
    /// <summary>プロセス ID。</summary>
    int Id { get; }

    /// <summary>終わったか。</summary>
    bool HasExited { get; }

    /// <summary>終了コード（まだ動いていれば null）。</summary>
    int? ExitCode { get; }
}

/// <summary>切り離して起動した子プロセス。</summary>
public sealed class DetachedProcess : IDetachedProcess
{
    // ── CreateProcessW の値（WinBase.h）──────────────────────────

    /// <summary>CREATE_NO_WINDOW: コンソールアプリをコンソールの窓なしで動かす。</summary>
    private const uint CreateNoWindow = 0x08000000;

    /// <summary>CREATE_NEW_PROCESS_GROUP: 新しいプロセスグループ（呼び出し元の Ctrl+C を受け取らない）。</summary>
    private const uint CreateNewProcessGroup = 0x00000200;

    /// <summary>CREATE_BREAKAWAY_FROM_JOB: 呼び出し元のジョブから外れる（ジョブが許すときだけ成功する）。</summary>
    private const uint CreateBreakawayFromJob = 0x01000000;

    /// <summary>ERROR_ACCESS_DENIED（ジョブが外れることを許さないとき CreateProcessW が返す）。</summary>
    private const int ErrorAccessDenied = 5;

    /// <summary>WAIT_OBJECT_0（プロセスが終わっている）。</summary>
    private const uint WaitObject0 = 0;

    /// <summary>待たずに状態だけを見る待ち時間（ミリ秒）。</summary>
    private const uint NoWaitMilliseconds = 0;

    /// <summary>プロセスのハンドル（終了の確認・終了コードの取得に使う。閉じてもプロセスは止まらない）。</summary>
    private readonly SafeProcessHandle _handle;

    /// <inheritdoc />
    public int Id { get; }

    /// <summary>ハンドルと ID から作る。</summary>
    private DetachedProcess(SafeProcessHandle handle, int id)
    {
        _handle = handle;
        Id = id;
    }

    /// <summary>
    /// 切り離して起動する。
    /// </summary>
    /// <param name="fileName">実行ファイル（絶対パス）。</param>
    /// <param name="arguments">引数。</param>
    /// <param name="workingDirectory">作業フォルダ（null なら呼び出し元と同じ）。</param>
    /// <returns>起動したプロセス。</returns>
    /// <exception cref="ChildProcessStartException">起動できなかったとき。</exception>
    public static DetachedProcess Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ChildProcessStartException(
                $"{fileName} を起動できません（切り離した起動は Windows だけに対応しています）。",
                new PlatformNotSupportedException());
        }

        var commandLine = WindowsCommandLine.Build(fileName, arguments);
        const uint baseFlags = CreateNoWindow | CreateNewProcessGroup;
        if (!TryCreate(fileName, commandLine, workingDirectory, baseFlags | CreateBreakawayFromJob, out var info, out var error)
            && !(error == ErrorAccessDenied && TryCreate(fileName, commandLine, workingDirectory, baseFlags, out info, out error)))
        {
            var cause = new Win32Exception(error);
            throw new ChildProcessStartException($"{fileName} を起動できません: {cause.Message}", cause);
        }

        // スレッドのハンドルは使わないのですぐ閉じる（プロセスのハンドルは終了の確認のために持つ）
        CloseHandle(info.Thread);
        return new DetachedProcess(new SafeProcessHandle(info.Process, ownsHandle: true), info.ProcessId);
    }

    /// <inheritdoc />
    public bool HasExited => WaitForSingleObject(_handle, NoWaitMilliseconds) == WaitObject0;

    /// <inheritdoc />
    public int? ExitCode => HasExited && GetExitCodeProcess(_handle, out var code) ? unchecked((int)code) : null;

    /// <summary>ハンドルを閉じる（プロセスは止めない）。</summary>
    public void Dispose() => _handle.Dispose();

    /// <summary>CreateProcessW を 1 回呼ぶ。</summary>
    private static bool TryCreate(
        string fileName, string commandLine, string? workingDirectory, uint flags, out ProcessInformation info, out int error)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        // lpCommandLine は書き換えられることがあるので書き込める領域で渡す
        var ok = CreateProcessW(fileName, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero,
            inheritHandles: false, flags, IntPtr.Zero, workingDirectory, ref startup, out info);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    // ── Win32 ────────────────────────────────────────────────

    /// <summary>STARTUPINFOW（既定のまま渡す。標準入出力も窓の表示も指定しない）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    /// <summary>PROCESS_INFORMATION。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint exitCode);
}
