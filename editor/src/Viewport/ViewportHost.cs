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

    private IntPtr _containerHwnd;

    public IntPtr ContainerHwnd => _containerHwnd;

    /// <summary>コンテナ HWND が生成されたときに発火する。</summary>
    public event EventHandler? ContainerCreated;

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

        internal static int LoWord(IntPtr lParam) => (int)(lParam.ToInt64() & 0xFFFF);
        internal static int HiWord(IntPtr lParam) => (int)((lParam.ToInt64() >> 16) & 0xFFFF);
    }
}
