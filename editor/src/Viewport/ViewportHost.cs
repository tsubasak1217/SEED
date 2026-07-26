using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SEEDEditor.Viewport;

/// <summary>
/// Runtime ウィンドウを埋め込む Win32 コンテナウィンドウを提供する HwndHost。
/// プロセス管理は行わない（RuntimeManager が担当する）。
///
/// BuildWindowCore でコンテナ HWND を作成し、ContainerCreated イベントを発火する。
/// RuntimeManager はこの HWND に対して SetParent で子ウィンドウを埋め込む。
/// </summary>
public sealed class ViewportHost : HwndHost
{
    private const int WS_CHILD        = 0x40000000;
    private const int WS_VISIBLE      = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WM_SIZE         = 0x0005;
    private const int WM_ERASEBKGND   = 0x0014;
    private const int BLACK_BRUSH     = 4;

    // WM_PARENTNOTIFY: 子ウィンドウ（埋め込みランタイム HWND）上でマウスボタンが
    // 押されたとき、システムが親（このコンテナ HWND）へ送るメッセージ。
    // 子 HWND への直接入力は WPF の入力ルートを通らないため、AvalonDock は
    // ビューポートパネルのアクティブ化を検知できない。これを補うために使用する。
    private const int WM_PARENTNOTIFY = 0x0210;
    private const int WM_LBUTTONDOWN  = 0x0201;
    private const int WM_RBUTTONDOWN  = 0x0204;
    private const int WM_MBUTTONDOWN  = 0x0207;
    private const int WM_XBUTTONDOWN  = 0x020B;

    private IntPtr _containerHwnd;

    public IntPtr ContainerHwnd => _containerHwnd;

    /// <summary>コンテナ HWND が生成されたときに発火する。</summary>
    public event EventHandler? ContainerCreated;

    /// <summary>
    /// 埋め込みランタイム HWND（子ウィンドウ）上でマウスボタンが押されたときに発火する。
    /// ビューポートパネルの手動アクティブ化に使用する（WM_PARENTNOTIFY を契機とする）。
    /// </summary>
    public event EventHandler? ViewportPointerPressed;

    // ── HwndHost 実装 ─────────────────────────────────────────

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _containerHwnd = NativeMethods.CreateWindowEx(
            0, "Static", "",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0,
            (int)ActualWidth  > 0 ? (int)ActualWidth  : 800,
            (int)ActualHeight > 0 ? (int)ActualHeight : 600,
            hwndParent.Handle,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_containerHwnd == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create container window.");

        ContainerCreated?.Invoke(this, EventArgs.Empty);
        return new HandleRef(this, _containerHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        NativeMethods.DestroyWindow(hwnd.Handle);
    }

    /// <summary>コンテナリサイズ時に子ウィンドウを追従させる。</summary>
    protected override IntPtr WndProc(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_ERASEBKGND)
        {
            var rect = new NativeMethods.RECT();
            NativeMethods.GetClientRect(hwnd, ref rect);
            NativeMethods.FillRect(wParam, ref rect, NativeMethods.GetStockObject(BLACK_BRUSH));
            handled = true;
            return (IntPtr)1;
        }
        if (msg == WM_SIZE)
        {
            int w = NativeMethods.LoWord(lParam);
            int h = NativeMethods.HiWord(lParam);
            NativeMethods.EnumChildWindows(hwnd, (child, _) =>
            {
                NativeMethods.MoveWindow(child, 0, 0, w, h, repaint: true);
                return true;
            }, IntPtr.Zero);
        }
        // 子ウィンドウ（ランタイム HWND）へのマウスボタン押下を検知してパネルアクティブ化を通知する。
        // wParam の下位ワードにイベント種別（WM_LBUTTONDOWN 等）が入る。
        // 通知のみを行い、handled は false のまま既定処理へ流すため、ランタイム操作は一切阻害しない。
        if (msg == WM_PARENTNOTIFY)
        {
            int evt = NativeMethods.LoWord(wParam);
            if (evt == WM_LBUTTONDOWN || evt == WM_RBUTTONDOWN
             || evt == WM_MBUTTONDOWN || evt == WM_XBUTTONDOWN)
            {
                ViewportPointerPressed?.Invoke(this, EventArgs.Empty);
            }
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    // ── Win32 P/Invoke ────────────────────────────────────────

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(
            int exStyle, string className, string windowName, int style,
            int x, int y, int w, int h,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        internal static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);

        /// <summary>Win32 矩形構造体。</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        internal static extern bool GetClientRect(IntPtr hwnd, ref RECT rect);

        [DllImport("user32.dll")]
        internal static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr hbr);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr GetStockObject(int fnObject);

        internal static int LoWord(IntPtr lParam) => (int)(lParam.ToInt64() & 0xFFFF);
        internal static int HiWord(IntPtr lParam) => (int)((lParam.ToInt64() >> 16) & 0xFFFF);
    }
}
