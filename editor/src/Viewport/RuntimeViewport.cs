using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SEEDEditor.Viewport
{
    /// <summary>
    /// Rust ランタイムウィンドウを WPF レイアウト内に埋め込む HwndHost 実装。
    ///
    /// 動作フロー:
    ///   1. BuildWindowCore でコンテナ用 Win32 ウィンドウを生成する
    ///   2. コンテナの HWND を引数として Rust プロセスを起動する
    ///   3. Rust が with_parent_window でコンテナの子ウィンドウを生成する
    ///   4. WM_SIZE をフックしてコンテナリサイズ時に子ウィンドウを追従させる
    /// </summary>
    public class RuntimeViewport : HwndHost
    {
        // ── 定数 ────────────────────────────────────────────────
        private const int WS_CHILD       = 0x40000000;
        private const int WS_VISIBLE     = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WM_SIZE        = 0x0005;

        // ── フィールド ───────────────────────────────────────────
        private readonly string _runtimeExePath;
        private Process?        _runtimeProcess;
        private IntPtr          _containerHwnd;

        public IntPtr ContainerHwnd => _containerHwnd;
        public bool   IsRunning     => _runtimeProcess is { HasExited: false };

        // ── コンストラクタ ────────────────────────────────────────
        public RuntimeViewport(string runtimeExePath)
        {
            _runtimeExePath = runtimeExePath;
        }

        // ── HwndHost 実装 ─────────────────────────────────────────

        /// <summary>
        /// WPF のレイアウトエンジンからコンテナ HWND の生成を要求される。
        /// ここでコンテナ Win32 ウィンドウを生成し、Rust プロセスを起動する。
        /// </summary>
        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            // コンテナ用 Win32 ウィンドウを WPF パネルの子として生成する。
            // このウィンドウを Rust に渡し、Rust がさらにその子として描画ウィンドウを作る。
            _containerHwnd = NativeMethods.CreateWindowEx(
                0,
                "Static", "",
                WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
                0, 0,
                (int)ActualWidth > 0  ? (int)ActualWidth  : 800,
                (int)ActualHeight > 0 ? (int)ActualHeight : 600,
                hwndParent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_containerHwnd == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create container window.");

            // Rust ランタイムを起動する。
            // コンテナ HWND を --parent-hwnd 引数として渡す。
            LaunchRuntime(_containerHwnd);

            return new HandleRef(this, _containerHwnd);
        }

        /// <summary>HwndHost が破棄されるときにコンテナウィンドウを破棄する。</summary>
        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            StopRuntime();
            NativeMethods.DestroyWindow(hwnd.Handle);
        }

        /// <summary>
        /// コンテナウィンドウの WndProc をフックする。
        /// WM_SIZE を受け取ったとき、子ウィンドウ（Rust ウィンドウ）を同じサイズにリサイズする。
        /// </summary>
        protected override IntPtr WndProc(
            IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SIZE)
            {
                int width  = NativeMethods.LoWord(lParam);
                int height = NativeMethods.HiWord(lParam);
                ResizeChildren(hwnd, width, height);
            }
            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        // ── 公開メソッド ──────────────────────────────────────────

        public void StopRuntime()
        {
            if (_runtimeProcess is { HasExited: false })
            {
                _runtimeProcess.Kill();
                _runtimeProcess.Dispose();
                _runtimeProcess = null;
            }
        }

        // ── プライベートメソッド ──────────────────────────────────

        private void LaunchRuntime(IntPtr parentHwnd)
        {
            var psi = new ProcessStartInfo
            {
                FileName        = _runtimeExePath,
                Arguments       = $"--parent-hwnd={parentHwnd}",
                UseShellExecute = false,
            };

            _runtimeProcess = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start runtime process.");

            _runtimeProcess.EnableRaisingEvents = true;
            _runtimeProcess.Exited += (_, _) => _runtimeProcess = null;
        }

        /// <summary>コンテナの直接の子ウィンドウをすべて指定サイズにリサイズする。</summary>
        private static void ResizeChildren(IntPtr parentHwnd, int width, int height)
        {
            NativeMethods.EnumChildWindows(parentHwnd, (childHwnd, _) =>
            {
                NativeMethods.MoveWindow(childHwnd, 0, 0, width, height, repaint: true);
                return true; // 列挙を継続
            }, IntPtr.Zero);
        }
    }

    // ── Win32 P/Invoke ────────────────────────────────────────────
    internal static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(
            int    dwExStyle,
            string lpClassName,
            string lpWindowName,
            int    dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll")]
        internal static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool MoveWindow(
            IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool repaint);

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool EnumChildWindows(
            IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        internal static int LoWord(IntPtr lParam) => (int)(lParam.ToInt64() & 0xFFFF);
        internal static int HiWord(IntPtr lParam) => (int)((lParam.ToInt64() >> 16) & 0xFFFF);
    }
}
