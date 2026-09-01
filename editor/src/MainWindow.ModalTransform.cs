// ============================================================
//  MainWindow.ModalTransform.cs — モーダルトランスフォーム用グローバルマウス追跡
//
//  担当:
//   - モーダル（Blender 風 G/R/S）進行フラグの一元管理
//   - モーダル中だけ有効な低レベルマウスフック（WH_MOUSE_LL）
//     ・カーソル座標をビューポートのクライアント座標へ変換して
//       `MODAL:CURSOR:{x},{y}` でランタイムへ転送する
//     ・左クリック＝確定 / 右クリック＝取消（エディタ UI へは伝播させない）
//   - フォーカス喪失（Alt+Tab 等）時の安全側取消
//
//  【なぜ必要か】
//  OS はマウスイベントをカーソル直下のウィンドウにしか配送しない。
//  シーンパネル（ランタイムの子ウィンドウ）から少しでもカーソルが出ると
//  ランタイムには WM_MOUSEMOVE が届かず、モーダルの更新が止まってしまう。
//  Blender ではウィンドウ外でも変形が続くため、エディタ側でグローバルに
//  カーソルを追跡して IPC で送り込む。
//
//  【座標系】
//  フックが返すのは画面物理座標。ランタイム子ウィンドウのクライアント原点
//  （ClientToScreen で取得）を引き、winit の CursorMoved と同じ
//  「クライアント座標（物理ピクセル）」へ変換して送る。
//  ウィンドウ外を表す負値・幅/高さ超えの値はクランプせずそのまま送る
//  （ランタイム側のレイ生成は線形変換なので素直に外挿される）。
// ============================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static SEEDEditor.Native.NativeInterop;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── 定数 ──────────────────────────────────────────────────

    /// <summary>
    /// カーソル座標の最小送信間隔（ミリ秒）。
    ///
    /// 高ポーリングレートのマウスは 1ms 未満の間隔で移動イベントを出すため、
    /// そのまま送るとランタイムのフレームより高頻度になり IPC が詰まる。
    /// 移動量は「前回座標との差分」を累積する方式なので、途中の点を間引いても
    /// 合計は変わらない（望遠鏡和として保存される）。
    /// </summary>
    private const int ModalCursorThrottleMs = 4;

    // ── フィールド ────────────────────────────────────────────

    /// <summary>モーダル中のグローバルカーソル追跡用フックのコールバック（GC 回収防止のため保持）。</summary>
    private LowLevelMouseProc? _modalMouseProc;

    /// <summary>モーダル中のグローバルカーソル追跡用フックのハンドル（0 = 未設置）。</summary>
    private nint _modalMouseHook;

    /// <summary>
    /// 「飲み込むべきボタン UP メッセージ」（0 = なし）。
    ///
    /// 確定/取消はボタン DOWN で行い、その DOWN を飲み込む。対応する UP を
    /// 素通しすると、エディタ UI 側が DOWN 無しの UP を受け取って
    /// 不整合（ドラッグ状態の取り残し等）を起こしうるため、対の UP も飲み込む。
    /// この値が残っている間はモーダル終了後もフックを解除しない。
    /// </summary>
    private int _modalPendingUpMsg;

    /// <summary>直近に送信したカーソル座標（同一座標の重複送信を避けるため）。</summary>
    private int _modalLastSentX;
    private int _modalLastSentY;

    /// <summary>1 度でも座標を送ったか（初回は重複判定をスキップする）。</summary>
    private bool _modalCursorSent;

    /// <summary>送信間引き用のストップウォッチ。</summary>
    private readonly Stopwatch _modalCursorThrottle = new();

    // ============================================================
    //  進行フラグとフックの一元管理
    // ============================================================

    /// <summary>
    /// モーダルトランスフォームの進行フラグを設定し、
    /// グローバルマウスフックの設置/解除を対称に行う。
    ///
    /// キーフック（UI スレッド）と IPC 受信（バックグラウンドスレッド）の
    /// 両方から呼ばれる。フラグ自体は volatile なので即時反映し、
    /// フックの設置/解除だけ UI スレッドへマーシャルする
    /// （WH_MOUSE_LL のコールバックはフックを設置したスレッドの
    ///   メッセージループ上で呼ばれるため、必ず UI スレッドで設置する）。
    /// </summary>
    private void SetModalTransformActive(bool active)
    {
        _modalTransformActive = active;
        if (Dispatcher.CheckAccess()) ApplyModalMouseHookState();
        else                          Dispatcher.BeginInvoke(ApplyModalMouseHookState);
    }

    /// <summary>
    /// 現在の進行フラグに合わせてフックの設置状態を揃える（UI スレッド専用）。
    ///
    /// 終了時でも「対の ボタン UP を飲み込む」ためにフックを残す場合がある。
    /// </summary>
    private void ApplyModalMouseHookState()
    {
        if (_modalTransformActive) InstallModalMouseHook();
        else if (_modalPendingUpMsg == 0) UninstallModalMouseHook();
    }

    /// <summary>
    /// グローバルカーソル追跡用の低レベルマウスフックを設置する（多重設置は無視）。
    /// </summary>
    private void InstallModalMouseHook()
    {
        if (_modalMouseHook != 0) return;
        _modalMouseProc = ModalMouseCallback;
        var hMod = GetModuleHandle(null);
        _modalMouseHook = SetWindowsHookExMouse(WH_MOUSE_LL, _modalMouseProc, hMod, 0);
        // 新しいモーダルの開始。前回の送信履歴と間引きタイマーを初期化する。
        _modalCursorSent = false;
        _modalCursorThrottle.Reset();
        EditorLog.Write($"InstallModalMouseHook — hook=0x{_modalMouseHook:X}");
    }

    /// <summary>
    /// グローバルカーソル追跡用フックを解除する（未設置なら何もしない）。
    /// モーダル終了・フォーカス喪失・ウィンドウ終了・例外時のいずれからも呼べる。
    /// </summary>
    private void UninstallModalMouseHook()
    {
        if (_modalMouseHook == 0) return;
        UnhookWindowsHookEx(_modalMouseHook);
        _modalMouseHook = 0;
        _modalMouseProc = null;
        _modalPendingUpMsg = 0;
        _modalCursorThrottle.Reset();
    }

    // ============================================================
    //  フックコールバック
    // ============================================================

    /// <summary>
    /// モーダル中の低レベルマウスフック。
    ///
    ///  - WM_MOUSEMOVE : 座標を転送する。**イベントは飲み込まない**
    ///    （WH_MOUSE_LL で移動を握りつぶすと OS のカーソル移動そのものが止まる）。
    ///  - 左ボタン DOWN: 確定（Blender 準拠でどこをクリックしても確定）。
    ///    エディタ UI へは伝播させないために飲み込む。
    ///  - 右ボタン DOWN: 取消。同じく飲み込む。
    ///  - ホイール     : モーダル中のズームはピボット投影がずれるため飲み込む。
    ///
    /// コールバックはネイティブから呼ばれるため、例外を外へ投げてはならない。
    /// 例外時はモーダルを取消してフックを解除する（安全側）。
    /// </summary>
    private nint ModalMouseCallback(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode < 0) return CallNextHookEx(_modalMouseHook, nCode, wParam, lParam);

            int message = (int)wParam;

            // ── 終了直後: 対のボタン UP だけを飲み込んでフックを解除する ──
            if (!_modalTransformActive)
            {
                if (_modalPendingUpMsg != 0 && message == _modalPendingUpMsg)
                {
                    _modalPendingUpMsg = 0;
                    UninstallModalMouseHook();
                    return 1;
                }
                // 対の UP が来ないまま別のイベントが来た場合も確実に解除する
                // （UP を取りこぼしてフックが残り続けるのを防ぐ）。
                _modalPendingUpMsg = 0;
                UninstallModalMouseHook();
                return CallNextHookEx(_modalMouseHook, nCode, wParam, lParam);
            }

            // ── フォーカスが他アプリへ移った（Alt+Tab 等）──────────
            // 変形を続ける根拠が無くなるので安全側へ倒して取消する。
            // クリックは飲み込まない（他アプリの操作を奪わない）。
            if (!IsEditorOrRuntimeForeground())
            {
                CancelModalTransformExternally();
                return CallNextHookEx(_modalMouseHook, nCode, wParam, lParam);
            }

            switch (message)
            {
                case WM_MOUSEMOVE:
                {
                    var msll = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    SendModalCursor(msll.pt, force: false);
                    // 素通し（飲み込むとカーソルが動かなくなる）
                    break;
                }

                case WM_LBUTTONDOWN:
                    // Blender 準拠: モーダル中の左クリックはどこでも確定。
                    // そのクリックはエディタ UI へは渡さない（誤操作防止）。
                    EndModalByMouse("MODAL:CONFIRM", WM_LBUTTONUP);
                    return 1;

                case WM_RBUTTONDOWN:
                    EndModalByMouse("MODAL:CANCEL", WM_RBUTTONUP);
                    return 1;

                case WM_MBUTTONDOWN:
                case WM_MBUTTONUP:
                    // 中ボタン（カメラパン）はモーダルと排他。押下・解放とも
                    // 握りつぶすだけでモーダルは継続する
                    // （Blender も中ボタンでは確定しない）。
                    return 1;

                case WM_MOUSEWHEEL:
                    // ズームするとピボットのスクリーン投影がずれ、回転/拡縮の
                    // 基準が壊れる。ランタイム側でも無視しているので、ここで捨てる。
                    return 1;
            }

            return CallNextHookEx(_modalMouseHook, nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            // ネイティブへ例外を漏らさない。安全側に倒してモーダルを畳む。
            EditorLog.Write($"ModalMouseCallback — 例外のためモーダルを取消: {ex}");
            try { _runtimeManager?.SendToRuntime("MODAL:CANCEL"); } catch { /* 送信失敗は無視 */ }
            _modalTransformActive = false;
            _modalPendingUpMsg = 0;
            UninstallModalMouseHook();
            return 0;
        }
    }

    /// <summary>
    /// マウスボタンでモーダルを終了する（確定 or 取消）。
    ///
    /// 間引きで最後の移動を送り損ねている可能性があるため、
    /// 終了コマンドの前に最新座標を必ず 1 度送ってから確定させる。
    /// </summary>
    /// <param name="command">送信する IPC コマンド（MODAL:CONFIRM / MODAL:CANCEL）。</param>
    /// <param name="expectedUpMessage">対で飲み込むボタン UP のメッセージ ID。</param>
    private void EndModalByMouse(string command, int expectedUpMessage)
    {
        if (GetCursorPos(out var pt)) SendModalCursor(pt, force: true);
        _runtimeManager?.SendToRuntime(command);
        // DOWN を飲み込んだので、対の UP も飲み込む（それまでフックは残す）。
        _modalPendingUpMsg = expectedUpMessage;
        SetModalTransformActive(false);
    }

    /// <summary>
    /// フック/フォーカス喪失など、エディタ側の都合でモーダルを取消す。
    /// </summary>
    private void CancelModalTransformExternally()
    {
        if (!_modalTransformActive) return;
        _runtimeManager?.SendToRuntime("MODAL:CANCEL");
        SetModalTransformActive(false);
    }

    // ============================================================
    //  座標の転送
    // ============================================================

    /// <summary>
    /// 画面座標のカーソル位置をビューポートのクライアント座標へ変換して転送する。
    ///
    /// ウィンドウ外（負値・幅/高さ超え）でもクランプせずそのまま送る。
    /// </summary>
    /// <param name="screenPt">画面物理座標のカーソル位置。</param>
    /// <param name="force">true なら間引きを無視して必ず送る（確定直前など）。</param>
    private void SendModalCursor(POINT screenPt, bool force)
    {
        if (_runtimeManager == null) return;
        if (!force
            && _modalCursorThrottle.IsRunning
            && _modalCursorThrottle.ElapsedMilliseconds < ModalCursorThrottleMs)
            return;

        if (!TryToViewportClient(screenPt, out int x, out int y)) return;

        // 同一座標の重複送信は無意味（差分ゼロ）なので捨てる。
        if (_modalCursorSent && x == _modalLastSentX && y == _modalLastSentY) return;

        _modalLastSentX = x;
        _modalLastSentY = y;
        _modalCursorSent = true;
        _modalCursorThrottle.Restart();
        _runtimeManager.SendToRuntime($"MODAL:CURSOR:{x},{y}");
    }

    /// <summary>
    /// 画面物理座標をランタイム子ウィンドウのクライアント座標へ変換する。
    ///
    /// ランタイムの子 HWND は埋め込みコンテナのクライアント原点 (0,0) に
    /// 配置される（RuntimeManager.ResizeRuntimeToContainer）。
    /// ここでは子 HWND のクライアント原点を直接求めるため、
    /// winit の CursorMoved と完全に同じ座標系になる。
    /// 子 HWND がまだ無い場合はコンテナで代用する。
    /// </summary>
    private bool TryToViewportClient(POINT screenPt, out int x, out int y)
    {
        x = 0;
        y = 0;
        nint hwnd = _runtimeManager?.RuntimeHwnd ?? 0;
        if (hwnd == 0) hwnd = _viewportHost?.ContainerHwnd ?? 0;
        if (hwnd == 0) return false;

        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) return false;

        x = screenPt.X - origin.X;
        y = screenPt.Y - origin.Y;
        return true;
    }

    // ============================================================
    //  フォーカス喪失（Alt+Tab 等）
    // ============================================================

    /// <summary>
    /// エディタウィンドウが非アクティブになったときにモーダルを取消す。
    ///
    /// モーダル中は「マウス移動＝変形」であり、他アプリを触っている間に
    /// 変形が進むのは危険なため、安全側（取消＝開始時の姿勢へ復元）に倒す。
    ///
    /// ただし埋め込みランタイムの子ウィンドウが OS フォアグラウンドを取った
    /// ケース（埋め込み Play/Pause）では非アクティブ化してもエディタ操作の
    /// 続きなので取消さない（<see cref="IsEditorOrRuntimeForeground"/> で除外）。
    /// </summary>
    private void OnWindowDeactivatedCancelModal(object? sender, EventArgs e)
    {
        if (!_modalTransformActive) return;
        if (IsEditorOrRuntimeForeground()) return;
        CancelModalTransformExternally();
    }
}
