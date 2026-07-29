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
    /// 転送した（DROP_ACTOR を送信した）場合は true を返す
    /// （シーンタブのドラッグ仮切替の確定/復帰判定に使用する）。
    public bool TryDropActorsAtCursor(string[] paths)
    {
        if (!IsMouseOverViewportHwnd()) return false;

        var actorPaths = paths
            .Where(p => IsActorFile(p))
            .ToList();
        if (actorPaths.Count == 0) return false;

        var (localX, localY) = GetViewportLocalCursorPos();
        HandleViewportDrop(actorPaths, localX, localY);
        return true;
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
    /// カーソルがビューポート HWND 上にある場合に、そのビューポートローカル座標
    /// （物理ピクセル）を取得する。ビューポート外なら false を返し x/y は 0 になる。
    /// </summary>
    /// <remarks>
    /// インスペクタの「制御点を追加」ボタンからのドラッグ＆ドロップ転送に使う。
    /// ビューポートは HwndHost のため WPF の Drop が発火せず、DoDragDrop は
    /// DragDropEffects.None を返す。その戻り値を見た呼び出し側がこのメソッドで
    /// ドロップ位置を取得し、ADD_CONTROL_POINT_AT_SCREEN としてランタイムへ転送する。
    /// </remarks>
    public bool TryGetViewportCursorPos(out uint x, out uint y)
    {
        x = 0; y = 0;
        if (!IsMouseOverViewportHwnd()) return false;
        (x, y) = GetViewportLocalCursorPos();
        return true;
    }

    // ── 制御点（ControlPoint）の追加 D&D ────────────────────────
    //
    // 「＋ 制御点を追加」ボタンをビューポートへ D&D する操作の受け皿。
    //
    // 【なぜ OLE ドロップターゲット経由なのか（不具合の経緯）】
    // ビューポートの ContainerHwnd は OnContainerCreated で RegisterDragDrop 済みであり、
    // ドラッグ中の折衝はすべて ViewportOleDropTarget が行う。従来の実装は
    // 「HwndHost 上には OLE ターゲットが無いので DoDragDrop は None を返す」という前提で、
    // DoDragDrop から戻った**後**に GetCursorPos でドロップ位置を復元していた。
    // しかし実際には ViewportOleDropTarget が .actor 以外のペイロードを
    // DROPEFFECT_NONE で明示的に拒否するため IDropTarget::Drop が呼ばれず、
    // かつ復帰後のカーソル位置は「マウスを離した位置」である保証が無い
    //（OLE ループ終了までにカーソルが動いていれば別座標になる）。
    // そこで制御点ペイロードも OLE ターゲットに正式に受理させ、
    // Drop の引数として渡ってくる**確定したドロップ座標**を使う方式へ変更した。

    /// <summary>制御点追加 D&D の識別に使う DataObject フォーマット名（C# 内部のみで完結する識別子）。</summary>
    internal const string ControlPointDragFormat = "SEEDControlPointAdd";

    /// <summary>ドラッグ中の配置予定マーカー要求（CONTROL_POINT_DRAG_HOVER）の最小送信間隔（ミリ秒）。</summary>
    /// <remarks>
    /// 約 30Hz。ランタイム側の解決は ID バッファ読み戻し（1 フレーム 1 回）を消費するため、
    /// 毎マウスメッセージ送るとピック等と競合する。人間の目には 30Hz で十分追従して見える。
    /// </remarks>
    private const int ControlPointHoverIntervalMs = 33;

    /// <summary>
    /// 現在ドラッグ中の制御点追加操作の対象（アクター DFS ID, スロット添字）。
    /// ドラッグ外では null。ドロップ座標は OLE ターゲット側が持つので座標は含めない。
    /// </summary>
    private (int actorId, int slotIdx)? _controlPointDragTarget;

    /// <summary>OLE ドロップターゲットが ADD_CONTROL_POINT_AT_SCREEN を送信済みかどうか。</summary>
    private bool _controlPointDropSent;

    /// <summary>
    /// 制御点追加 D&D の開始を登録する（InspectorPanel が DoDragDrop の直前に呼ぶ）。
    /// </summary>
    internal void BeginControlPointDrag(int actorId, int slotIdx)
    {
        _controlPointDragTarget = (actorId, slotIdx);
        _controlPointDropSent   = false;
        EditorLog.Write($"[CtrlPoint] ドラッグ開始 actor={actorId} slot={slotIdx}");
    }

    /// <summary>
    /// 制御点追加 D&D の終了を登録する（InspectorPanel が DoDragDrop の直後に呼ぶ）。
    /// 戻り値は「OLE 経路でドロップを送信済みか」で、false なら呼び出し側が
    /// カーソル位置からのフォールバック送信を行う。
    /// </summary>
    internal bool EndControlPointDrag()
    {
        bool sent = _controlPointDropSent;
        _controlPointDragTarget = null;
        _controlPointDropSent   = false;
        // マーカーが残らないよう、どの終わり方でも必ず END を送る（多重送信は無害）。
        _runtimeManager?.SendToRuntime("CONTROL_POINT_DRAG_END");
        EditorLog.Write($"[CtrlPoint] ドラッグ終了 oleDropSent={sent}");
        return sent;
    }

    /// <summary>
    /// 制御点の追加をランタイムへ依頼する（ビューポートローカル座標・物理ピクセル）。
    /// ヒット判定とワールド座標算出はランタイムの責務（C# はワールド座標を扱わない）。
    /// </summary>
    private void SendAddControlPointAtScreen(int actorId, int slotIdx, uint x, uint y)
    {
        var msg = $"ADD_CONTROL_POINT_AT_SCREEN:{actorId},{slotIdx},{x},{y}";
        EditorLog.Write($"[CtrlPoint] 送信: {msg}");
        _runtimeManager?.SendToRuntime(msg);
    }

    /// <summary>
    /// OLE 経路でドロップを取り逃がした場合のフォールバック送信
    /// （InspectorPanel が DoDragDrop 復帰後に呼ぶ）。
    /// カーソルがビューポート上に無ければ何もしない。
    /// </summary>
    internal void TryAddControlPointAtCursor(int actorId, int slotIdx)
    {
        if (!TryGetViewportCursorPos(out var vx, out var vy))
        {
            EditorLog.Write("[CtrlPoint] フォールバック: カーソルがビューポート外のため送信しない");
            return;
        }
        EditorLog.Write("[CtrlPoint] フォールバック経路で送信（OLE ドロップ未発生）");
        SendAddControlPointAtScreen(actorId, slotIdx, vx, vy);
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

    /// <summary>ビューポートへドロップ可能なファイル拡張子（ドット付き・小文字）。</summary>
    /// <remarks>
    /// アクターファイル（.actor / .actor2d）に加え、スプライト生成用の画像拡張子を含む。
    /// 画像は 2D コンテキストで SpriteComponent 付き Actor2D として生成される
    /// （3D コンテキストへドロップされた画像は Rust 側で無視する）。
    /// 対応画像は runtime 側 DROPPABLE_IMAGE_EXTS / Cargo.toml の image feature と一致させること。
    /// </remarks>
    private static readonly string[] DroppableExtensions =
    {
        ".actor", ".actor2d",
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".webp",
    };

    /// <summary>ビューポートへドロップ可能なファイル（アクター or 画像）かを返す。</summary>
    private static bool IsActorFile(string path)
        => DroppableExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

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
        /// <summary>
        /// 現在のドラッグが「制御点の追加」かどうか。
        /// アクタードラッグとは**排他**で、こちらが true の間はアクタースポーン用の
        /// DRAG_HOVER（プレビュー球）を一切送らない（余計な球が出るのを防ぐ）。
        /// </summary>
        private bool _isDraggingControlPoint = false;
        /// <summary>制御点ホバー通知を最後に送った時刻（間引き用）。</summary>
        private DateTime _lastControlPointHoverAt = DateTime.MinValue;

        public ViewportOleDropTarget(MainWindow owner) => _owner = owner;

        public int DragEnter(object pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            // 制御点ドラッグを先に判定する（アクターより優先。両立はしない）。
            _isDraggingControlPoint = IsControlPointDrag(pDataObj);
            if (_isDraggingControlPoint)
            {
                _isDraggingActors = false;
                pdwEffect = DROPEFFECT_COPY;
                EditorLog.Write($"[OLE] DragEnter: 制御点ドラッグ pt=({pt.X},{pt.Y})");
                SendControlPointHover(pt, force: true);
                return S_OK;
            }

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
            if (_isDraggingControlPoint)
            {
                pdwEffect = DROPEFFECT_COPY;
                SendControlPointHover(pt, force: false);
            }
            else if (_isDraggingActors)
            {
                pdwEffect = DROPEFFECT_COPY;
                SendHover(pt);
            }
            else
            {
                pdwEffect = DROPEFFECT_NONE;
            }
            return S_OK;
        }

        public int DragLeave()
        {
            // ドラッグ離脱: プレビュー（球体 / 配置予定マーカー）を消す
            if (_isDraggingControlPoint)
            {
                _isDraggingControlPoint = false;
                _owner._runtimeManager?.SendToRuntime("CONTROL_POINT_DRAG_END");
            }
            if (_isDraggingActors)
            {
                _isDraggingActors = false;
                _owner._runtimeManager?.SendToRuntime("DRAG_HOVER_END");
            }
            return S_OK;
        }

        public int Drop(object pDataObj, uint grfKeyState, POINT pt, ref uint pdwEffect)
        {
            // ── 制御点の追加ドロップ ──
            if (_isDraggingControlPoint || IsControlPointDrag(pDataObj))
            {
                _isDraggingControlPoint = false;
                var target = _owner._controlPointDragTarget;
                // ドラッグ開始情報が無い（＝対象アクターが分からない）場合は何もしない。
                if (target is not { } t)
                {
                    EditorLog.Write("[OLE] Drop: 制御点ドラッグだが対象未登録のため破棄");
                    pdwEffect = DROPEFFECT_NONE;
                    return S_OK;
                }
                var (cpX, cpY) = ToViewportLocal(pt);
                _owner._controlPointDropSent = true;
                // 送信は UI スレッドへ回さず即時に行う。OLE の Drop から戻った直後に
                // InspectorPanel 側の後処理が走るため、そこで「送信済み」が見えている必要がある。
                _owner._runtimeManager?.SendToRuntime("CONTROL_POINT_DRAG_END");
                _owner.SendAddControlPointAtScreen(t.actorId, t.slotIdx, cpX, cpY);
                pdwEffect = DROPEFFECT_COPY;
                return S_OK;
            }

            // ── アクター / 画像のドロップ ──
            _isDraggingActors = false;
            var paths = GetActorPaths(pDataObj).ToList();
            if (paths.Count == 0) { pdwEffect = DROPEFFECT_NONE; return S_OK; }

            var (localX, localY) = ToViewportLocal(pt);

            _owner.Dispatcher.BeginInvoke(() => _owner.HandleViewportDrop(paths, localX, localY));
            pdwEffect = DROPEFFECT_COPY;
            return S_OK;
        }

        /// <summary>スクリーン座標（物理ピクセル）をビューポートローカル座標へ変換する。</summary>
        private (uint x, uint y) ToViewportLocal(POINT pt)
        {
            GetWindowRect(_owner._viewportHost!.ContainerHwnd, out var vpRect);
            return ((uint)Math.Max(0, pt.X - vpRect.Left), (uint)Math.Max(0, pt.Y - vpRect.Top));
        }

        /// <summary>ドラッグ中のペイロードが「制御点の追加」かどうかを判定する。</summary>
        private static bool IsControlPointDrag(object pDataObj)
            => pDataObj is System.Windows.IDataObject data
            && data.GetDataPresent(ControlPointDragFormat);

        /// <summary>
        /// 配置予定マーカー用のホバー座標を送信する（`ControlPointHoverIntervalMs` で間引く）。
        /// </summary>
        /// <param name="force">間引きを無視して必ず送るか（DragEnter 直後に使う）。</param>
        private void SendControlPointHover(POINT pt, bool force)
        {
            if (_owner._viewportHost == null) return;
            var now = DateTime.UtcNow;
            if (!force && (now - _lastControlPointHoverAt).TotalMilliseconds < ControlPointHoverIntervalMs)
                return;
            _lastControlPointHoverAt = now;

            var (x, y) = ToViewportLocal(pt);
            _owner._runtimeManager?.SendToRuntime($"CONTROL_POINT_DRAG_HOVER:{x},{y}");
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
    // タッチパッドスクロール（縦の減衰・横スワイプ）は EditorScrollBehavior が
    // 全ウィンドウ共通で処理するため、ここでは扱わない。

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
