// ============================================================
//  MainWindow.Input.cs — キーボードフックと Play 時クランプ
//
//  担当:
//   - グローバルキーボードフック（低レベル）
//     Ctrl+Z/Y/S/C/X/V、ESC/Del、カメラキー転送
//   - Play 時カーソルクランプ（IPC 経由で Rust 側が毎フレーム適用）
//   - ランタイム HWND 確定後の初期化
// ============================================================

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SEEDEditor.Runtime;
using static SEEDEditor.Native.NativeInterop;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── Play 時カーソルクランプ（IPC 経由で Rust 側が毎フレーム適用）──────

    private void OnRuntimeHwndAvailable(nint hwnd)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 最大化起動などでコンテナサイズと Runtime ウィンドウサイズがズレる場合に補正
            if (_runtimeManager?.State == EditorState.Edit)
                _runtimeManager.ResizeRuntimeToContainer();

            if (_clampInPlay && !_isDragging && _runtimeManager?.State == EditorState.Play)
                ApplyPlayClamp();

            SyncViewportSettings();

            // 起動後、最初に Edit ランタイムが準備できたら前回のシーンを復元する（一度だけ）。
            if (!_initialSceneLoaded && _runtimeManager?.State == EditorState.Edit)
            {
                _initialSceneLoaded = true;
                TryLoadLastScene();
            }

            // FIRST_FRAME が届かない場合のフォールバック（リリースビルドの Runtime 等）。
            // READY 受信から 3 秒経ってもオーバーレイが残っていれば強制的に閉じる。
            Task.Delay(3000).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (ViewportLoadingOverlay.Visibility != Visibility.Collapsed)
                    ViewportLoadingOverlay.Visibility = Visibility.Collapsed;
            }));
        });
    }

    /// <summary>
    /// ランタイムが最初の実フレームを描画したときに呼ばれる（デバッグビルドのみ）。
    /// このタイミングで起動中オーバーレイを非表示にする。
    /// </summary>
    private void OnFirstFrameReady()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ViewportLoadingOverlay.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>
    /// Rust ランタイムへ PLAY_CLAMP:1 を送信する。
    /// Rust 側が毎フレーム ClipCursor を再適用するため C# 側タイマーは不要。
    /// </summary>
    private void ApplyPlayClamp()
    {
        _runtimeManager?.SendToRuntime("PLAY_CLAMP:1");
    }

    private void ReleasePlayClamp()
    {
        _runtimeManager?.SendToRuntime("PLAY_CLAMP:0");
    }

    // ── グローバルキーボードフック ────────────────────────────────

    private void InstallKeyboardHook()
    {
        _llKeyProc = LLKeyboardCallback;
        var hMod = GetModuleHandle(null);
        _llKeyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _llKeyProc, hMod, 0);
        EditorLog.Write($"InstallKeyboardHook — hook=0x{_llKeyHook:X}");
    }

    private void UninstallKeyboardHook()
    {
        if (_llKeyHook != 0)
        {
            UnhookWindowsHookEx(_llKeyHook);
            _llKeyHook = 0;
        }
    }

    private nint LLKeyboardCallback(int nCode, nint wParam, nint lParam)
    {
        // ── 転送ゲート（不具合1対策）────────────────────────────────
        // グローバルフックはシステム全体のキーを拾うため、以下を満たさない限り
        // ランタイムへは一切転送しない:
        //   (1) 前面ウィンドウがエディタ本体 or ランタイム子プロセスに属すること。
        //       他アプリが前面のときはここで抜けるため、裏で複製やカメラ移動が
        //       起きなくなる（従来は「|| IsCamInputActive()」でフォーカス不問に
        //       入っていたのが原因だった）。
        //   埋め込み Play 中はランタイム子が OS 前面フォーカスを持つため許可対象に含める
        //   （ゲーム入力は OS 経由で子 HWND へ直接届き、下の各 State ガードで
        //     Play 中はシーン操作/カメラ転送を行わないため、ゲーム操作は阻害しない）。
        if (nCode < 0 || !IsEditorOrRuntimeForeground())
            return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);

        var kb     = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var vk     = kb.vkCode;
        bool isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
        bool isUp   = wParam == WM_KEYUP   || wParam == WM_SYSKEYUP;

        // (2) シーンビューポート（ランタイム埋め込みパネル）にフォーカスがあるか。
        //     スクリプトエディタ/インスペクタ等の WPF コントロールにフォーカスがある
        //     間は false になり、シーン操作・カメラ移動をランタイムへ転送しない。
        bool viewportFocused = IsSceneViewportFocused();

        // Ctrl キー追跡 + Rust へ転送。
        // 追跡（_ctrlHeld）はフォーカスに関係なく常に行い、下の Ctrl+C 等の分岐で使う。
        // ランタイムへの CTRL_DOWN/UP 転送は「ビューポートにフォーカスがあり、かつ
        // カメラ入力が有効（Edit/Pause）」のときだけ行う。Play 中は転送しない
        // （ゲーム入力は OS 経由で届くため二重転送を避ける）。
        if (vk == 0x11 || vk == 0xA2 || vk == 0xA3)
        {
            if (isDown && !_ctrlHeld)
            {
                if (viewportFocused && IsCamInputActive())
                {
                    _runtimeManager?.SendToRuntime("CTRL_DOWN");
                    _ctrlFwd = true;
                }
            }
            else if (isUp && _ctrlHeld)
            {
                // 転送済みの DOWN には必ず UP を送る（フォーカスが外れていてもスタック防止）。
                if (_ctrlFwd)
                {
                    _runtimeManager?.SendToRuntime("CTRL_UP");
                    _ctrlFwd = false;
                }
            }
            _ctrlHeld = isDown;
        }
        // Ctrl+Z / Ctrl+Y / Ctrl+S / Ctrl+C / Ctrl+X / Ctrl+V（Edit モードのみ）
        else if (isDown && _ctrlHeld && _runtimeManager?.State == EditorState.Edit)
        {
            if (vk == 0x5A) // Z
            {
                // Undo はシーンビューポートにフォーカスがあるときだけランタイムへ転送する。
                // スクリプトエディタ等の編集中はエディタ側の Undo に委ね、シーンを巻き戻さない。
                if (viewportFocused)
                {
                    // terrain モード中は地形専用の undo スタック（TERRAIN_UNDO）を使う。
                    // 通常モードのアクター編集 undo（UNDO）とはランタイム側で別管理のため分岐する。
                    _runtimeManager?.SendToRuntime(_terrainMode ? "TERRAIN_UNDO" : "UNDO");
                }
                return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
            }
            else if (vk == 0x59) // Y
            {
                if (viewportFocused)
                    _runtimeManager?.SendToRuntime(_terrainMode ? "TERRAIN_REDO" : "REDO");
                return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
            }
            else if (vk == 0x53) // S
            {
                // 保存はフォーカス位置に関係なくグローバルに効かせる（従来通り）。
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                Dispatcher.BeginInvoke(() =>
                {
                    // スクリプトエディタがアクティブなときはシーン保存を行わない。
                    // スクリプト保存（Ctrl+S=編集中 / Ctrl+Shift+S=全て）は
                    // パネル側の OnPanelKeyDown が実施するため、ここでは何もしない
                    // （二重保存を避ける）。
                    if (PanelScriptEditor.IsActiveForSave) return;
                    if (shift) ShowSaveAsDialog();
                    else       DoQuickSave();
                });
                return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
            }
            else if (vk == 0x43) // C
            {
                Dispatcher.BeginInvoke(() => {
                    // プロジェクトパネルにフォーカスがあればアセットのコピー。
                    if (PanelProject.IsKeyboardFocusWithin) { PanelProject.HandleCopy(); return; }
                    // それ以外は、シーンビューポートにフォーカスがあるときだけ
                    // ランタイムへ複製コピーを転送する。スクリプトエディタ/インスペクタ等の
                    // テキスト編集中はコントロール既定のコピーに委ね、シーンを複製しない。
                    if (viewportFocused) _runtimeManager?.SendToRuntime("COPY");
                });
                return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
            }
            else if (vk == 0x58) // X
            {
                Dispatcher.BeginInvoke(() => {
                    if (PanelProject.IsKeyboardFocusWithin) PanelProject.HandleCut();
                    // ビューポート/その他のテキスト編集ではランタイムへ何も送らない（切り取りは未対応）。
                });
                return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
            }
            else if (vk == 0x56) // V
            {
                Dispatcher.BeginInvoke(() => {
                    if (PanelProject.IsKeyboardFocusWithin) { PanelProject.HandlePaste(); return; }
                    // シーンビューポートにフォーカスがあるときだけランタイムへ貼り付けを転送する。
                    if (viewportFocused) _runtimeManager?.SendToRuntime("PASTE");
                });
                return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
            }
        }

        // ESC / Del → 選択インスタンス削除（ダイアログあり）。
        // シーンビューポートにフォーカスがあるときだけ。テキスト編集中の Del/ESC で
        // シーンオブジェクトが消えないようにする。
        if (isDown && (vk == 0x1B || vk == 0x2E) && !_ctrlHeld
            && _runtimeManager?.State == EditorState.Edit
            && viewportFocused
            && !_deleteDialogOpen)
        {
            Dispatcher.BeginInvoke(TryDeleteSelected);
            return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
        }

        if (VkKeyMap.TryGetValue(vk, out var keyName))
        {
            // カメラ移動キーは「カメラ入力有効（Edit/Pause）かつビューポートにフォーカス」
            // のときだけ DOWN を転送する。スクリプトエディタ等での WASD 入力で
            // カメラが動かないようにする。
            if (isDown && IsCamInputActive() && viewportFocused && _pressedVks.Add(vk))
            {
                _runtimeManager?.SendToRuntime($"CAM_KEY_DOWN:{keyName}");
            }
            else if (isUp && _pressedVks.Remove(vk))
            {
                // CAM_KEY_DOWN を送信済みのキーは、状態に関わらず必ず UP を送る。
                // 従来は IsCamInputActive() が false になった後の UP を送信せずに
                // 捨てていたため、ランタイム側でキーがスタックし
                // 「RMB だけでカメラが移動する」「軸スナップが効かない」原因になっていた。
                _runtimeManager?.SendToRuntime($"CAM_KEY_UP:{keyName}");
            }
        }
        return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 前面ウィンドウがエディタ本体プロセス、またはランタイム（子ビューポート）プロセスに
    /// 属しているかを返す。どちらでもない（他アプリが前面）の場合は false。
    /// 埋め込み Play/Pause 中はランタイム子が OS 前面になるため、ランタイム PID も許可する。
    /// </summary>
    private bool IsEditorOrRuntimeForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == 0) return false;
        GetWindowThreadProcessId(fg, out var fgPid);
        if (fgPid == (uint)Environment.ProcessId) return true;
        // ランタイム子プロセス（埋め込みビューポート/埋め込み Play）が前面のケース。
        var rtPid = _runtimeManager?.CurrentProcessId;
        return rtPid.HasValue && fgPid == (uint)rtPid.Value;
    }

    /// <summary>
    /// シーンビューポート（ランタイムを埋め込む HwndHost）に入力フォーカスがあるかを返す。
    ///
    /// WPF はウィンドウ全体で 1 つの HwndSource（HWND）を共有するため、スクリプトエディタや
    /// インスペクタ等の WPF コントロールにフォーカスがあるときは Keyboard.FocusedElement が
    /// その要素を返す。一方、埋め込みランタイムは WPF 管理外の独立した子 HWND であり、
    /// そこへ OS フォーカスが移ると WPF 側の Keyboard.FocusedElement は null になる
    /// （＝ WPF はどのコントロールにもフォーカスが無いと認識する）。
    /// また AvalonDock がビューポートのドキュメントをアクティブ化した際は HwndHost 自身
    /// （_viewportHost）が FocusedElement になり得る。この 2 ケースをビューポートフォーカスとみなす。
    ///
    /// 本メソッドは低レベルキーボードフックのコールバック（エディタ UI スレッド上で実行される）
    /// から同期的に呼ばれる前提。
    /// </summary>
    private bool IsSceneViewportFocused()
    {
        var focused = Keyboard.FocusedElement;
        // WPF コントロールにフォーカスが無い = ランタイム子 HWND 等、WPF 管理外へフォーカスが
        // 移っている状態。呼び出し側で前面をエディタ/ランタイムに限定済みのため、これは
        // シーンビューポートにフォーカスがあるとみなす。
        if (focused is null) return true;
        // HwndHost（ビューポートホスト）自身がフォーカス要素のケース。
        if (_viewportHost != null && ReferenceEquals(focused, _viewportHost)) return true;
        // それ以外（スクリプトエディタ/インスペクタ/プロジェクト等の WPF コントロール）。
        return false;
    }

    private bool IsCamInputActive()
    {
        var state = _runtimeManager?.State;
        return state == EditorState.Edit || state == EditorState.Pause;
    }
}
