// ============================================================
//  QuietProcess.cs — ランタイムをフォーカスを奪わずに起動し、窓を画面の外へ退ける（Windows）
//
//  【なぜ自前で CreateProcess を呼ぶのか】
//  System.Diagnostics.Process は窓の出し方（STARTUPINFO.wShowWindow）に SW_SHOWNOACTIVATE を渡せない。
//  検証で使ってきた仕組み（tmp/w3_7/verify/common.py・tmp/lane2/quiet_launch.py）と同じく:
//    - STARTUPINFO の wShowWindow = SW_SHOWNOACTIVATE: 最初の ShowWindow（winit の set_visible(true)）が
//      「表示するが前面にしない・フォーカスを奪わない」になる
//    - CREATE_NO_WINDOW: debug の SEED.exe はコンソールのアプリなので、コンソールの窓を作らせない
//    - 標準出力・標準エラーはログのファイルへ（継承させるハンドルはそのファイルと NUL だけに絞る。
//      PROC_THREAD_ATTRIBUTE_HANDLE_LIST。呼び出し元のパイプなどを子に握らせない）
//    - STARTF_USEPOSITION: 窓を最初から画面の外（左上のさらに外）に作らせる（winit は位置を指定せず CW_USEDEFAULT で
//      作るので、その位置に STARTUPINFO の dwX・dwY が使われる）。一瞬でも画面に出さない
//  窓が出たら念のため SetWindowPos で Z 順の最背面・画面の外へ退ける（SWP_NOACTIVATE）。
//  さらに拡張スタイル WS_EX_NOACTIVATE を付け、窓が前面（アクティブ）にならないようにする。利用者が前面の窓を閉じると
//  Windows は次の窓をアクティブにするが、それがこの窓だとランタイムにフォーカスの出入りが届き、Play の窓が背面へ回った扱い
//  （platform.paused・フォーカスの無い間のフレームの間引き）になって、スクリプトが動かなくなることがあった（2026-10-02 に実測）。
//  窓の大きさは変えない（大きさの違う舞台は起動を分ける。ThumbnailGenerator。起動後に大きさを変えると、画素のルートの
//  キャンバスの自動の拡大が最初の大きさのまま残って絵が歪むため）。
//  画面の外の窓には OS が WM_PAINT を送らないが、環境変数 SEED_HEADLESS=1 でランタイムがフレームを回し続け、
//  スクリーンショットは GPU から読み戻すので写る（runtime の screenshot_ops.rs。エディタのヘッドレスと同じ）。
//  画面の外なのでマウスのカーソルが窓に乗らず、部品のホバーの見た目で絵が揺れることもない。
//
//  自分が起動したプロセスだけを扱う（他のプロセスの窓・プロセスには触れない）。
//
//  【後片付けの順（docs/reviews/2026-10-02_code_review.md #17）】
//  見張りのスレッドはプロセス ID を起動時に控えた値（_processId）で窓を探し、Process.Id を読まない
//  （以前は Dispose の後に Process.Id を読むと InvalidOperationException で道具ごと落ちる隙があった）。
//  Dispose は 中断の合図 → スレッドの Join（上限つき）→ Process の Dispose の順。待ちは中断の合図で起きるので Join はすぐ終わる。
// ============================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Runtime;

/// <summary>フォーカスを奪わずに起動したランタイムのプロセスと、その窓の操作。</summary>
public sealed class QuietProcess : IDisposable
{
    // ── Win32 の定数 ───────────────────────────────────

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const int STARTF_USEPOSITION = 0x00000004;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const short SW_SHOWNOACTIVATE = 4;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x00020002;
    private static readonly IntPtr HWND_BOTTOM = (IntPtr)1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000L;

    /// <summary>属性リストに入れる属性の数（継承させるハンドルの一覧だけ）。</summary>
    private const int AttributeCount = 1;

    /// <summary>子に継承させるハンドルの数（ログのファイルと NUL）。</summary>
    private const int InheritedHandleCount = 2;

    /// <summary>窓を退ける先（画面の左上のさらに外。仮想画面の座標の上限 ±32767 の内側）。</summary>
    private const int OffscreenX = -16000, OffscreenY = -16000;

    /// <summary>本体の窓とみなす最小の辺（winit が作る 16×16 などの補助の窓を除く）。</summary>
    private const int MainWindowMinSidePx = 64;

    /// <summary>窓を探す間隔（ミリ秒）。窓が見えてから退けるまでの間を短くする。</summary>
    private const int WindowPollIntervalMs = 15;

    /// <summary>窓が出た後も見張る間隔（ミリ秒）。窓が作り直されたら退け直す。</summary>
    private const int WindowWatchIntervalMs = 250;

    /// <summary>
    /// Dispose で見張りのスレッドが止まるのを待つ上限。待ちは中断の合図で起きるのでふつうはすぐ終わる。
    /// 止まったランタイムの窓へ SetWindowPos を送った直後などで戻らないときは、待たずに先へ進む（スレッドは背景なので道具の終了を妨げない）。
    /// </summary>
    private static readonly TimeSpan WatchStopTimeout = TimeSpan.FromSeconds(2);

    /// <summary>起動したプロセス。</summary>
    public Process Process { get; }

    /// <summary>起動したプロセスの ID（起動時に控える。見張りのスレッドは Process.Id を読まずにこれを使う）。</summary>
    private readonly int _processId;

    /// <summary>見張りのスレッド。</summary>
    private readonly Thread _watchThread;

    /// <summary>見張りの中断の合図。</summary>
    private readonly CancellationTokenSource _watchCancel = new();

    /// <summary>Dispose 済みか（0 = まだ・1 = 済み。2 回目の Dispose は何もしない）。</summary>
    private int _disposed;

    /// <summary>見張りのスレッドが動いているか（Dispose の後は false。診断・単体テスト用）。</summary>
    public bool IsWatching => _watchThread.IsAlive;

    /// <summary>退けた本体の窓（0 = まだ）。</summary>
    private IntPtr _mainWindow;

    /// <summary>退けた本体の窓（0 = まだ）。</summary>
    public IntPtr MainWindow => Interlocked.CompareExchange(ref _mainWindow, IntPtr.Zero, IntPtr.Zero);

    /// <summary>窓が出たときに前面がこのプロセスの窓だったか（フォーカスを奪った印。null = まだ窓が出ていない）。</summary>
    public bool? StoleForeground { get; private set; }

    /// <summary>出来事を伝える先（ログの行）。</summary>
    private readonly Action<string> _log;

    private QuietProcess(Process process, int processId, Action<string> log)
    {
        Process = process;
        _processId = processId;
        _log = log;
        _watchThread = new Thread(WatchWindows) { IsBackground = true, Name = "thumbnail-window-watch" };
        _watchThread.Start();
    }

    /// <summary>
    /// フォーカスを奪わずに起動する。環境変数は呼び出し元のプロセスのものを引き継ぐ（先に設定しておく）。
    /// </summary>
    /// <param name="exe">実行ファイル。</param>
    /// <param name="arguments">引数（1 つずつ。必要なら引用符で囲む）。</param>
    /// <param name="workingDirectory">作業フォルダ。</param>
    /// <param name="logPath">標準出力・標準エラーを書くファイル（作り直す）。</param>
    /// <param name="log">出来事を伝える先。</param>
    /// <returns>起動したプロセス。</returns>
    /// <exception cref="Win32Exception">起動できなかった。</exception>
    public static QuietProcess Start(string exe, IReadOnlyList<string> arguments, string workingDirectory, string logPath, Action<string> log)
    {
        // ── 子に継承させるハンドル（ログのファイルと、標準入力の NUL）──
        using var logFile = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var nul = new FileStream("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        SetInheritable(logFile.SafeFileHandle);
        SetInheritable(nul.SafeFileHandle);
        IntPtr logHandle = logFile.SafeFileHandle.DangerousGetHandle();
        IntPtr nulHandle = nul.SafeFileHandle.DangerousGetHandle();

        // ── 継承させるハンドルの一覧（属性リスト）──
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, AttributeCount, 0, ref size);
        IntPtr attributes = Marshal.AllocHGlobal(size);
        IntPtr handleList = Marshal.AllocHGlobal(IntPtr.Size * InheritedHandleCount);
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, AttributeCount, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
            Marshal.WriteIntPtr(handleList, 0, logHandle);
            Marshal.WriteIntPtr(handleList, IntPtr.Size, nulHandle);
            if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleList,
                    (IntPtr)(IntPtr.Size * InheritedHandleCount), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

            var startup = new STARTUPINFOEX();
            startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            startup.StartupInfo.dwFlags = STARTF_USESHOWWINDOW | STARTF_USEPOSITION | STARTF_USESTDHANDLES;
            startup.StartupInfo.wShowWindow = SW_SHOWNOACTIVATE;
            startup.StartupInfo.dwX = OffscreenX;
            startup.StartupInfo.dwY = OffscreenY;
            startup.StartupInfo.hStdInput = nulHandle;
            startup.StartupInfo.hStdOutput = logHandle;
            startup.StartupInfo.hStdError = logHandle;
            startup.lpAttributeList = attributes;

            var commandLine = new StringBuilder(Quote(exe));
            foreach (var a in arguments) commandLine.Append(' ').Append(Quote(a));

            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, bInheritHandles: true,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW, IntPtr.Zero, workingDirectory,
                    ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess: " + exe);

            try
            {
                // プロセスのハンドルを握ったまま Process を作る（その間に終わっても番号が使い回されない）
                var process = Process.GetProcessById(info.dwProcessId);
                return new QuietProcess(process, info.dwProcessId, log);
            }
            finally
            {
                CloseHandle(info.hThread);
                CloseHandle(info.hProcess);
            }
        }
        finally
        {
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(handleList);
        }
    }

    // ============================================================
    //  窓
    // ============================================================

    /// <summary>窓が出るのを待って画面の外・最背面へ退け、以後も見張る（別のスレッド）。</summary>
    private void WatchWindows()
    {
        var token = _watchCancel.Token;
        while (!token.IsCancellationRequested && !HasExited())
        {
            var main = FindMainWindow();
            if (main != IntPtr.Zero && main != MainWindow)
            {
                // 前面がこのプロセスの窓か（SW_SHOWNOACTIVATE が効いていれば違う）と、見えた位置（STARTF_USEPOSITION が
                // 効いていれば最初から画面の外）を、退ける前に見て残す
                StoleForeground ??= GetForegroundWindow() == main;
                GetWindowRect(main, out var found);
                // 前面（アクティブ）にならない窓にする（枠の大きさは変わらないので、描く大きさも変わらない）
                long exStyle = GetWindowLongPtrW(main, GWL_EXSTYLE).ToInt64();
                SetWindowLongPtrW(main, GWL_EXSTYLE, (IntPtr)(exStyle | WS_EX_NOACTIVATE));
                SetWindowPos(main, HWND_BOTTOM, OffscreenX, OffscreenY, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
                Interlocked.Exchange(ref _mainWindow, main);
                _log($"[thumbnails] 窓を画面の外へ退けました（見えた位置 {found.Left},{found.Top}・" +
                     $"前面を奪ったか: {(StoleForeground == true ? "はい" : "いいえ")}）");
            }
            // 間を置く（中断の合図で起きる。Dispose がすぐ Join できるように Thread.Sleep は使わない）
            if (token.WaitHandle.WaitOne(MainWindow == IntPtr.Zero ? WindowPollIntervalMs : WindowWatchIntervalMs)) break;
        }
    }

    /// <summary>このプロセスの見えている最上位の窓のうち、いちばん大きいもの（補助の小さな窓は除く）。</summary>
    private IntPtr FindMainWindow()
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        // Process.Id は Dispose の後に読むと例外になるので、起動時に控えた ID を使う
        int pid = _processId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != (uint)pid || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var r)) return true;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < MainWindowMinSidePx || h < MainWindowMinSidePx) return true;
            long area = (long)w * h;
            if (area > bestArea) { best = hwnd; bestArea = area; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>プロセスが終わったか（取れなければ終わったとみなす）。</summary>
    public bool HasExited()
    {
        try { return Process.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return true; }
    }

    /// <summary>
    /// 見張りを止める（プロセスには触れない）。中断の合図 → 見張りのスレッドの Join → Process の Dispose の順。2 回呼んでもよい。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _watchCancel.Cancel();
        // スレッドが止まってから Process を捨てる（止まる前に捨てると、スレッドが捨てた Process を触りうる）
        bool stopped = _watchThread.Join(WatchStopTimeout);
        Process.Dispose();
        // 合図はスレッドが止まったときだけ捨てる（止まりきらなかったスレッドが合図の待ちを読み続けても落ちないように）
        if (stopped) _watchCancel.Dispose();
    }

    // ============================================================
    //  小道具
    // ============================================================

    /// <summary>ハンドルを子へ継承できるようにする。</summary>
    private static void SetInheritable(SafeFileHandle handle)
    {
        if (!SetHandleInformation(handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation");
    }

    /// <summary>コマンドラインの 1 引数を引用符で囲む（MSVCRT の解釈の規則。空白・引用符が無ければそのまま）。</summary>
    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    // ============================================================
    //  Win32
    // ============================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
