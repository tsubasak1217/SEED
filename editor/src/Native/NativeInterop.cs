// ============================================================
//  Native/NativeInterop.cs — Win32 / COM P/Invoke 定義の集約
//
//  MainWindow や他クラスで使用する Win32 API・COM インターフェース・
//  構造体・定数をすべてここに集約する。
//  呼び出し元では `using static SEEDEditor.Native.NativeInterop;` を
//  追加することで完全修飾なしに利用できる。
// ============================================================

using System.Runtime.InteropServices;

namespace SEEDEditor.Native;

/// <summary>Win32 API / COM P/Invoke 定義の集約クラス。</summary>
internal static class NativeInterop
{
    // ── デリゲート ────────────────────────────────────────────

    /// <summary>低レベルキーボードフックのコールバック型。</summary>
    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    // ── 構造体 ────────────────────────────────────────────────

    /// <summary>低レベルキーボードフックの情報構造体。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint  vkCode;
        public uint  scanCode;
        public uint  flags;
        public uint  time;
        public nint  dwExtraInfo;
    }

    /// <summary>Win32 矩形構造体。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Win32 座標構造体。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    // ── Win32 定数 ────────────────────────────────────────────

    /// <summary>拡張ウィンドウスタイルのインデックス。</summary>
    public const int GWL_EXSTYLE = -20;

    /// <summary>マウスクリックを透過させる拡張スタイル。</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    // DWM 属性定数
    /// <summary>ダークモードのタイトルバーを有効にする DWM 属性 (Windows 10 20H1+)。</summary>
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    /// <summary>タイトルバー色を設定する DWM 属性 (Windows 11+)。</summary>
    public const int DWMWA_CAPTION_COLOR           = 35;
    /// <summary>ウィンドウ枠色を設定する DWM 属性 (Windows 11+)。</summary>
    public const int DWMWA_BORDER_COLOR            = 34;

    // フックタイプ
    /// <summary>低レベルキーボードフックの識別子。</summary>
    public const int WH_KEYBOARD_LL = 13;

    // ウィンドウメッセージ
    public const int WM_KEYDOWN     = 0x0100;
    public const int WM_KEYUP       = 0x0101;
    public const int WM_SYSKEYDOWN  = 0x0104;
    public const int WM_SYSKEYUP    = 0x0105;
    public const int WM_SYSCOMMAND  = 0x0112;
    public const int WM_EXITSIZEMOVE = 0x0232;

    // WM_SYSCOMMAND のサブコマンド
    /// <summary>ウィンドウ移動を示すシステムコマンド。</summary>
    public const int SC_MOVE = 0xF010;

    // ── user32.dll ────────────────────────────────────────────

    /// <summary>現在のカーソル座標を取得する。</summary>
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);

    /// <summary>ウィンドウの拡張スタイルなどを取得する。</summary>
    [DllImport("user32.dll")] public static extern int  GetWindowLong(nint hWnd, int nIndex);

    /// <summary>ウィンドウの拡張スタイルなどを設定する。</summary>
    [DllImport("user32.dll")] public static extern int  SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    /// <summary>低レベルキーボードフックをインストールする。</summary>
    [DllImport("user32.dll")] public static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    /// <summary>インストール済みフックを削除する。</summary>
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hhk);

    /// <summary>次のフックに処理を渡す。</summary>
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    /// <summary>フォアグラウンドウィンドウのハンドルを返す。</summary>
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();

    /// <summary>ウィンドウを作成したプロセスの ID を取得する。</summary>
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    /// <summary>ウィンドウの画面座標での矩形を取得する。</summary>
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    /// <summary>ウィンドウのクライアント座標を画面座標へ変換する（原点取得に使用）。</summary>
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    /// <summary>指定ウィンドウを Z オーダーの最前面へ移動する。</summary>
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(nint hWnd);

    /// <summary>指定ウィンドウをフォアグラウンド（前面・アクティブ）にする。</summary>
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hWnd);

    /// <summary>
    /// ウィンドウの位置・サイズ・Z オーダーを変更する。
    /// SWP_ASYNCWINDOWPOS を付けると対象スレッドへ非同期でポストするため、
    /// 対象ウィンドウが応答不能（デバッガで凍結中など）でも呼び出し側はブロックしない。
    /// </summary>
    [DllImport("user32.dll")] public static extern bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    // SetWindowPos 用の定数
    public static readonly nint HWND_TOP     = 0;
    public const uint SWP_NOSIZE         = 0x0001;
    public const uint SWP_NOMOVE         = 0x0002;
    public const uint SWP_NOACTIVATE     = 0x0010;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    /// <summary>マウスカーソルを指定した矩形内に閉じ込める。</summary>
    [DllImport("user32.dll")] public static extern bool ClipCursor(ref RECT lpRect);

    /// <summary>マウスカーソルの閉じ込めを解除する（null 渡し用）。</summary>
    [DllImport("user32.dll")] public static extern bool ClipCursor(nint lpRect);

    // ── kernel32.dll ─────────────────────────────────────────

    /// <summary>指定モジュールのハンドルを返す。</summary>
    [DllImport("kernel32.dll")] public static extern nint GetModuleHandle(string? lpModuleName);

    // ── ole32.dll ─────────────────────────────────────────────

    /// <summary>ウィンドウを OLE ドロップターゲットとして登録する。</summary>
    [DllImport("ole32.dll")] public static extern int RegisterDragDrop(
        nint hwnd, [MarshalAs(UnmanagedType.Interface)] IOleDropTarget dropTarget);

    /// <summary>OLE ドロップターゲット登録を解除する。</summary>
    [DllImport("ole32.dll")] public static extern int RevokeDragDrop(nint hwnd);

    // ── dwmapi.dll ────────────────────────────────────────────

    /// <summary>DWM ウィンドウ属性を設定する（ダークモード・タイトルバー色等）。</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    // ── COM インターフェース ────────────────────────────────────

    /// <summary>
    /// ContainerHwnd を Win32 OLE DropTarget として登録するための COM インターフェース。
    /// WPF DragOver/Drop は HwndHost 上では発火しないため、ここで直接受け取る。
    /// </summary>
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleDropTarget
    {
        [PreserveSig] int DragEnter(
            [MarshalAs(UnmanagedType.Interface)] object pDataObj,
            uint grfKeyState, POINT pt, ref uint pdwEffect);
        [PreserveSig] int DragOver(uint grfKeyState, POINT pt, ref uint pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(
            [MarshalAs(UnmanagedType.Interface)] object pDataObj,
            uint grfKeyState, POINT pt, ref uint pdwEffect);
    }
}
