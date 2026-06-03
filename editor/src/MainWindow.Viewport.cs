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

    /// カーソルがビューポート ContainerHwnd 矩形内にあるかを物理ピクセル座標で確認する。
    public bool IsMouseOverViewportHwnd()
    {
        if (_viewportHost == null) return false;
        GetCursorPos(out var cursor);
        GetWindowRect(_viewportHost.ContainerHwnd, out var rect);
        return cursor.X >= rect.Left && cursor.X <= rect.Right
            && cursor.Y >= rect.Top  && cursor.Y <= rect.Bottom;
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

    // ── WndProc：ウィンドウドラッグ監視 ──────────────────────────

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
        return nint.Zero;
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
