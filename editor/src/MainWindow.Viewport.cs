// ============================================================
//  MainWindow.Viewport.cs — ビューポート関連処理
//
//  担当:
//   - ビューポートへのアクター D&D（WPF フォールバック + Win32 OLE）
//   - WndProc によるウィンドウドラッグ監視
//   - ビューポートコンテキストメニュー
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SEEDEditor.Native;
using SEEDEditor.Runtime;
using static SEEDEditor.Native.NativeInterop;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── ビューポート D&D ──────────────────────────────────────────

    /// Window 全体のドラッグオーバー（フォールバック）。
    /// HwndHost 上では WPF DragOver が発火しないため、ビューポート周囲の WPF 領域（リサイズグリッパー等）でのみ動作する。
    /// ビューポート直上のドロップは ViewportOleDropTarget が処理する。
    private void OnViewportDragOver(object sender, DragEventArgs e)
    {
        var paths = GetActorPathsFromWpf(e.Data).ToList();
        e.Effects = (paths.Any() && IsNearViewport()) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// Window 全体のドロップ（フォールバック）。ビューポート周囲の WPF 領域からのドロップを処理する。
    private void OnViewportDrop(object sender, DragEventArgs e)
    {
        if (!IsNearViewport()) return;
        var paths = GetActorPathsFromWpf(e.Data).ToList();
        if (!paths.Any()) return;

        var (localX, localY) = GetViewportLocalCursorPos();
        HandleViewportDrop(paths, localX, localY);
    }

    /// カーソルがビューポート HWND 矩形の近傍（40px マージン）にあるかを確認する。
    /// WPF DragOver は HwndHost 内では発火しないため、周囲の WPF 領域（リサイズグリッパー等）を許容する。
    private bool IsNearViewport()
    {
        const int Margin = 40;
        if (_viewportHost == null) return false;
        GetCursorPos(out var cursor);
        GetWindowRect(_viewportHost.ContainerHwnd, out var rect);
        return cursor.X >= rect.Left  - Margin && cursor.X <= rect.Right  + Margin
            && cursor.Y >= rect.Top   - Margin && cursor.Y <= rect.Bottom + Margin;
    }

    /// DoDragDrop が DragDropEffects.None で返った場合（HwndHost 上へのドロップ）に
    /// カーソル位置とビューポート HWND 矩形を比較してドロップを転送する。
    public void TryDropActorsAtCursor(string[] paths)
    {
        if (!IsMouseOverViewportHwnd()) return;

        var actorPaths = paths
            .Where(p => IsActorFile(p))
            .ToList();
        if (actorPaths.Count == 0) return;

        var (localX, localY) = GetViewportLocalCursorPos();
        HandleViewportDrop(actorPaths, localX, localY);
    }

    /// カーソルがビューポート ContainerHwnd 上に「実際に」あるかを確認する。
    ///
    /// 矩形判定だけだと Z オーダーを無視するため、プロジェクト設定ウィンドウ等が
    /// ビューポートの手前に重なっている場合でも true になり、そのウィンドウへの
    /// ドロップがビューポートに吸われてしまう。矩形判定に加えて WindowFromPoint で
    /// カーソル直下の最前面ウィンドウを取得し、親を辿ってビューポートコンテナに
    /// 到達できる場合のみ true を返す（前面に他ウィンドウがあれば到達しない）。
    public bool IsMouseOverViewportHwnd()
    {
        if (_viewportHost == null) return false;
        GetCursorPos(out var cursor);
        GetWindowRect(_viewportHost.ContainerHwnd, out var rect);
        var inRect = cursor.X >= rect.Left && cursor.X <= rect.Right
                  && cursor.Y >= rect.Top  && cursor.Y <= rect.Bottom;
        if (!inRect) return false;

        // カーソル直下のウィンドウから親を辿り、ビューポートコンテナ（またはその子で
        // ある埋め込みランタイムウィンドウ）に属しているかを確認する
        var hwnd = WindowFromPoint(cursor);
        while (hwnd != 0)
        {
            if (hwnd == _viewportHost.ContainerHwnd) return true;
            hwnd = GetParent(hwnd);
        }
        return false;
    }

    /// カーソルのビューポートローカル座標（物理ピクセル）を返す。
    private (uint x, uint y) GetViewportLocalCursorPos()
    {
        GetCursorPos(out var cursor);
        GetWindowRect(_viewportHost!.ContainerHwnd, out var rect);
        var localX = (uint)Math.Clamp(cursor.X - rect.Left, 0, rect.Right  - rect.Left - 1);
        var localY = (uint)Math.Clamp(cursor.Y - rect.Top,  0, rect.Bottom - rect.Top  - 1);
        return (localX, localY);
    }

    /// <summary>
    /// ドラッグ中のカーソル位置をランタイムに送信する。
    /// GiveFeedback コールバックからカーソルがビューポート上にいる間呼び出す。
    /// </summary>
    internal void SendActorDragHover()
    {
        var (localX, localY) = GetViewportLocalCursorPos();
        _runtimeManager?.SendToRuntime($"DRAG_HOVER:{localX},{localY}");
    }

    /// <summary>ドラッグがビューポートから離れた、またはドラッグ終了時に呼び出す。</summary>
    internal void SendActorDragHoverEnd()
    {
        _runtimeManager?.SendToRuntime("DRAG_HOVER_END");
    }

    /// ドラッグ中にビューポート上をハイライトする透明オーバーレイを表示 / 非表示にする。
    /// オーバーレイは OnContainerCreated で事前生成済み・WS_EX_TRANSPARENT 付き。
    public void SetViewportDragHighlight(bool active)
    {
        if (_vpDragOverlay == null) return;

        if (active && _viewportHost != null)
        {
            // ContainerHwnd の物理ピクセル座標を WPF 論理座標に変換して配置する
            GetWindowRect(_viewportHost.ContainerHwnd, out var rect);
            var topLeft  = PointFromScreen(new Point(rect.Left,  rect.Top));
            var botRight = PointFromScreen(new Point(rect.Right, rect.Bottom));

            _vpDragOverlay.Left   = topLeft.X;
            _vpDragOverlay.Top    = topLeft.Y;
            _vpDragOverlay.Width  = botRight.X - topLeft.X;
            _vpDragOverlay.Height = botRight.Y - topLeft.Y;

            if (!_vpDragOverlay.IsVisible)
                _vpDragOverlay.Show();
        }
        else
        {
            _vpDragOverlay.Hide();
        }
    }

    /// ドロップされたアクターパスをランタイムに送信する共通処理。
    /// WPF フォールバックパスと Win32 OLE DropTarget パスの両方から呼ばれる。
    internal void HandleViewportDrop(IReadOnlyList<string> paths, uint viewportX, uint viewportY)
    {
        var state = _runtimeManager?.State;
        EditorLog.Write($"[Drop] HandleViewportDrop paths={paths.Count} pos=({viewportX},{viewportY}) state={state}");
        if (state != EditorState.Edit && state != EditorState.Pause) return;

        foreach (var path in paths)
        {
            var msg = $"DROP_ACTOR:{path},{viewportX},{viewportY}";
            EditorLog.Write($"[Drop] Sending: {msg}");
            _runtimeManager?.SendToRuntime(msg);
        }
    }

    /// WPF IDataObject から .actor パス一覧を取得する。
    private static IEnumerable<string> GetActorPathsFromWpf(IDataObject data)
    {
        if (data.GetDataPresent("SEEDProjectPaths"))
        {
            var paths = data.GetData("SEEDProjectPaths") as string[];
            if (paths != null)
                return paths.Where(p => IsActorFile(p));
        }
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null)
                return paths.Where(p => IsActorFile(p));
        }
        return Enumerable.Empty<string>();
    }

    /// <summary>.actor / .actor2d いずれかの拡張子を持つパスかを返す。</summary>
    private static bool IsActorFile(string path)
        => path.EndsWith(".actor",   StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".actor2d", StringComparison.OrdinalIgnoreCase);

    // ── Win32 OLE DropTarget（ビューポート HWND への直接ドロップ）──

    /// ContainerHwnd に登録する Win32 OLE DropTarget。
    /// WPF DragDrop はイン-プロセスで動作するため、pDataObj を System.Windows.IDataObject にキャストできる。
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ViewportOleDropTarget : IOleDropTarget
    {
        private const uint DROPEFFECT_NONE = 0;
        private const uint DROPEFFECT_COPY = 1;
        private const int  S_OK            = 0;

        private readonly MainWindow _owner;
        /// <summary>現在ドラッグ中の .actor パスが存在するかどうか。</summary>
        private bool _isDraggingActors = false;

        public ViewportOleDropTarget(MainWindow owner) => _owner = owner;

        public int DragEnter(object pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            var actorPaths = GetActorPaths(pDataObj).ToList();
            _isDraggingActors = actorPaths.Any();
            pdwEffect = _isDraggingActors ? DROPEFFECT_COPY : DROPEFFECT_NONE;
            EditorLog.Write($"[OLE] DragEnter: isDraggingActors={_isDraggingActors} actorCount={actorPaths.Count} pt=({pt.X},{pt.Y})");
            if (_isDraggingActors)
                SendHover(pt);
            return S_OK;
        }

        public int DragOver(uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            // エフェクトを維持しつつホバー位置をランタイムに通知する。
            // pdwEffect を明示的に設定しないと OLE が DROPEFFECT_NONE と解釈することがある。
            if (_isDraggingActors)
            {
                pdwEffect = DROPEFFECT_COPY;
                SendHover(pt);
            }
            else
            {
                pdwEffect = DROPEFFECT_NONE;
                EditorLog.Write("[OLE] DragOver: _isDraggingActors=false, skipping hover");
            }
            return S_OK;
        }

        public int DragLeave()
        {
            // ドラッグ離脱: プレビュー球体を消す
            if (_isDraggingActors)
            {
                _isDraggingActors = false;
                _owner._runtimeManager?.SendToRuntime("DRAG_HOVER_END");
            }
            return S_OK;
        }

        public int Drop(object pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            _isDraggingActors = false;
            var paths = GetActorPaths(pDataObj).ToList();
            if (paths.Count == 0) { pdwEffect = DROPEFFECT_NONE; return S_OK; }

            // スクリーン座標をビューポートローカル座標に変換する
            GetWindowRect(_owner._viewportHost!.ContainerHwnd, out var vpRect);
            var localX = (uint)Math.Max(0, pt.X - vpRect.Left);
            var localY = (uint)Math.Max(0, pt.Y - vpRect.Top);

            _owner.Dispatcher.BeginInvoke(() => _owner.HandleViewportDrop(paths, localX, localY));
            pdwEffect = DROPEFFECT_COPY;
            return S_OK;
        }

        /// <summary>スクリーン座標をビューポートローカル座標に変換して DRAG_HOVER を送信する。</summary>
        private void SendHover(POINT pt)
        {
            if (_owner._viewportHost == null) return;
            GetWindowRect(_owner._viewportHost.ContainerHwnd, out var vpRect);
            var localX = (uint)Math.Max(0, pt.X - vpRect.Left);
            var localY = (uint)Math.Max(0, pt.Y - vpRect.Top);
            _owner._runtimeManager?.SendToRuntime($"DRAG_HOVER:{localX},{localY}");
        }

        /// イン-プロセス WPF ドラッグの場合、pDataObj は System.Windows.IDataObject にキャストできる。
        private static IEnumerable<string> GetActorPaths(object pDataObj)
        {
            if (pDataObj is not System.Windows.IDataObject data) return Enumerable.Empty<string>();
            if (data.GetDataPresent("SEEDProjectPaths"))
            {
                var paths = data.GetData("SEEDProjectPaths") as string[];
                if (paths != null)
                    return paths.Where(p => IsActorFile(p));
            }
            return Enumerable.Empty<string>();
        }
    }

    // ── WndProc：ウィンドウドラッグ監視・横ホイールスクロール ─────

    /// <summary>横ホイール（タッチパッドの横スワイプ等）の Windows メッセージ。
    /// WPF は縦の MouseWheel しか標準処理しないため WndProc で自前処理する。</summary>
    private const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>横ホイール 1 ノッチ（delta=120）あたりの水平スクロール量（px）。</summary>
    private const double HorizontalScrollPixelsPerNotch = 48.0;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_MOVE)
        {
            // タイトルバードラッグ開始 → クランプ一時解除
            _isDragging = true;
            ReleasePlayClamp();
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            // ドラッグ終了 → 必要ならクランプ再適用
            _isDragging = false;
            if (_clampInPlay && _runtimeManager?.State == EditorState.Play)
                ApplyPlayClamp();
        }
        else if (msg == WM_MOUSEHWHEEL)
        {
            // 横ホイール入力: マウス直下のスクロール可能要素へ水平スクロールを配送する
            //（WPF は WM_MOUSEHWHEEL を UI イベントへ変換しないため、ここで処理しないと
            //  タッチパッドの横スワイプが全パネルで無反応になる）
            handled = HandleHorizontalWheel(wParam);
        }
        return nint.Zero;
    }

    // ── タッチパッド縦スクロールの減衰 ────────────────────────────

    /// <summary>
    /// タッチパッド（精密スクロール）の縦スクロール減衰係数。
    /// 精密タッチパッドは小刻みな delta を高頻度で送るため、WPF 既定の
    /// 「1 ノッチ = 3 行」換算だと体感が強すぎる。この係数で弱める。
    /// </summary>
    private const double TouchpadVerticalScrollScale = 0.35;

    /// <summary>ホイール 1 ノッチの delta 値（Windows 標準）。</summary>
    private const int WheelDeltaPerNotch = 120;

    /// <summary>ピクセルスクロール時の 1 ノッチあたりの移動量（px。WPF 既定相当）。</summary>
    private const double VerticalScrollPixelsPerNotch = 48.0;

    /// <summary>
    /// ウィンドウ全体の PreviewMouseWheel: タッチパッドの精密スクロールだけを減衰する。
    ///
    /// 物理マウスホイールは delta が ±120 の倍数で届くのに対し、精密タッチパッドは
    /// 端数の小さい delta を高頻度で送る。この違いで入力元を判別し、タッチパッド入力のみ
    /// TouchpadVerticalScrollScale を掛けて手動スクロールへ置き換える（マウスは従来どおり）。
    /// </summary>
    private void OnGlobalPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 120 の倍数 = 物理ホイール（または非精密ドライバ）→ 既定処理に任せる
        if (e.Delta % WheelDeltaPerNotch == 0) return;

        double scaled = e.Delta * TouchpadVerticalScrollScale;
        if (ApplyVerticalScroll(scaled)) e.Handled = true;
    }

    /// <summary>
    /// マウス直下の要素からビジュアルツリーを遡り、最初に見つかったスクロール可能要素へ
    /// 減衰済み delta による縦スクロールを適用する。処理できた場合 true。
    /// </summary>
    private bool ApplyVerticalScroll(double scaledDelta)
    {
        var el = Mouse.DirectlyOver as DependencyObject;
        while (el is not null)
        {
            // AvalonEdit のエディタ: ピクセル単位の縦オフセット API を使う
            if (el is ICSharpCode.AvalonEdit.TextEditor editor)
            {
                double px = scaledDelta / WheelDeltaPerNotch * VerticalScrollPixelsPerNotch;
                editor.ScrollToVerticalOffset(Math.Max(0, editor.VerticalOffset - px));
                return true;
            }
            if (el is ScrollViewer sv && sv.ScrollableHeight > 0)
            {
                // 論理スクロール（CanContentScroll: 行/アイテム単位）とピクセルスクロールで単位を変える
                double amount = sv.CanContentScroll
                    ? scaledDelta / WheelDeltaPerNotch * SystemParameters.WheelScrollLines
                    : scaledDelta / WheelDeltaPerNotch * VerticalScrollPixelsPerNotch;
                sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset - amount, 0, sv.ScrollableHeight));
                return true;
            }
            el = System.Windows.Media.VisualTreeHelper.GetParent(el);
        }
        return false;
    }

    /// <summary>
    /// WM_MOUSEHWHEEL を処理する: マウスカーソル直下の要素からビジュアルツリーを遡り、
    /// 最初に見つかったスクロール可能要素（AvalonEdit の TextEditor または ScrollViewer）へ
    /// 水平スクロールを適用する。処理できた場合 true。
    /// </summary>
    private bool HandleHorizontalWheel(nint wParam)
    {
        // wParam の上位 16bit が符号付き delta（正 = 右方向。1 ノッチ = 120）
        short delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
        if (delta == 0) return false;
        double amount = delta / 120.0 * HorizontalScrollPixelsPerNotch;

        // マウス直下の要素を取得し、ビジュアルツリーを遡ってスクロール対象を探す
        var el = Mouse.DirectlyOver as DependencyObject;
        while (el is not null)
        {
            // AvalonEdit のエディタ: 専用の水平オフセット API を使う
            if (el is ICSharpCode.AvalonEdit.TextEditor editor)
            {
                editor.ScrollToHorizontalOffset(Math.Max(0, editor.HorizontalOffset + amount));
                return true;
            }
            // 一般の ScrollViewer（出力パネル・ヒエラルキー等）
            if (el is ScrollViewer sv && sv.ScrollableWidth > 0)
            {
                sv.ScrollToHorizontalOffset(Math.Clamp(sv.HorizontalOffset + amount, 0, sv.ScrollableWidth));
                return true;
            }
            el = System.Windows.Media.VisualTreeHelper.GetParent(el);
        }
        return false;
    }

    // ── ビューポートコンテキストメニュー (C) ─────────────────────

    private void OnViewportContextMenuRequested()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_runtimeManager?.State != EditorState.Edit) return;

            var selectedIds = PanelHierarchy.GetSelectedNonGroupIds();
            bool hasSelection = selectedIds.Count > 0;

            var menu = new ContextMenu();

            if (hasSelection)
            {
                AddViewportMenuItem(menu, "コピー", "Ctrl+C", () => _runtimeManager?.SendToRuntime("COPY"));
                AddViewportMenuItem(menu, "削除",   "Del / Esc",    () =>
                {
                    var ids = PanelHierarchy.GetSelectedNonGroupIds();
                    if (ids.Count > 0)
                        _runtimeManager?.SendToRuntime($"DELETE_RECURSIVE:{string.Join(",", ids)}");
                });
                menu.Items.Add(new Separator());
                AddViewportMenuItem(menu, "アクタファイル化", null,
                    () => PanelHierarchy.ShowExportActorDialog());
                menu.Items.Add(new Separator());
            }
            // ── アクタを追加 サブメニュー ──────────────────────
            var addActorMenu = new MenuItem { Header = "アクタを追加" };

            var add3DItem = new MenuItem { Header = "3Dアクタ" };
            add3DItem.Click += (_, _) =>
            {
                PanelHierarchy.PrepareRenameAfterAdd();
                _runtimeManager?.SendToRuntime("ADD_ACTOR:0,-1");
            };
            addActorMenu.Items.Add(add3DItem);

            var add2DItem = new MenuItem { Header = "2Dアクタ" };
            add2DItem.Click += (_, _) =>
            {
                PanelHierarchy.PrepareRenameAfterAdd();
                _runtimeManager?.SendToRuntime("ADD_ACTOR_2D:0,-1");
            };
            addActorMenu.Items.Add(add2DItem);

            menu.Items.Add(addActorMenu);
            menu.Items.Add(new Separator());
            AddViewportMenuItem(menu, "ペースト", "Ctrl+V", () => _runtimeManager?.SendToRuntime("PASTE"));

            menu.PlacementTarget = ViewportDocumentContent;
            menu.Placement       = PlacementMode.MousePoint;
            menu.IsOpen          = true;
        });
    }

    private static void AddViewportMenuItem(ContextMenu menu, string header, string? gesture, Action action)
    {
        var item = new MenuItem { Header = header };
        if (gesture is not null) item.InputGestureText = gesture;
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
