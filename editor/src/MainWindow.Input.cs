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

        // (2) テキスト編集コントロールにフォーカスが「無い」こと。
        //     【重要な前提】埋め込みランタイムの子 HWND はクリックしてもキーボード
        //     フォーカスを取らない（取らないからこそ本フックで転送している）。そのため
        //     「ビューポートにフォーカスがあるか」を正の条件にすると恒久的に偽になり、
        //     カメラキーが一切効かなくなる（実際に起きたリグレッション）。
        //     正しいゲートは負の条件 —— スクリプトエディタ(AvalonEdit)・インスペクタ等の
        //     テキスト入力にフォーカスがある間だけ転送を止め、それ以外（＝前面判定を
        //     通過済みのエディタ操作中）は転送を許可する。
        bool viewportFocused = !IsTextInputFocused();

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

        // ── モーダルトランスフォーム（Blender 風 G/R/S）とツールホットキー ──────
        //
        // 【なぜエディタ側で拾うか】
        // 埋め込み Edit モードではランタイムの子 HWND がキーボードフォーカスを
        // 持たない（Play 時だけ SetFocus する）。そのため winit の KeyboardInput が
        // 届かず、キー入力はこのフックが拾って IPC で転送するしかない。
        //
        // 【キーの割り当て】
        //   G / R / S       … モーダル移動 / 回転 / 拡縮の開始
        //   X / Y / Z       … モーダル中の軸拘束（押すたび ワールド→ローカル→解除）
        //   Enter / Esc     … モーダルの確定 / 取消（左右クリックでも同じ）
        //   Q / W / E / T   … ツール切替（選択 / 移動 / 回転 / 拡縮）
        //                     拡縮が T なのは R をモーダル回転へ明け渡したため。
        if (isDown && !_ctrlHeld && viewportFocused
            && _runtimeManager?.State == EditorState.Edit)
        {
            if (_modalTransformActive)
            {
                // モーダル進行中: 軸拘束と確定/取消だけを受け付ける。
                switch (vk)
                {
                    case 0x58: _runtimeManager?.SendToRuntime("MODAL:AXIS:X"); return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                    case 0x59: _runtimeManager?.SendToRuntime("MODAL:AXIS:Y"); return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                    case 0x5A: _runtimeManager?.SendToRuntime("MODAL:AXIS:Z"); return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                    case 0x0D: // Enter
                        _runtimeManager?.SendToRuntime("MODAL:CONFIRM");
                        _modalTransformActive = false;
                        return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                    case 0x1B: // Esc（削除ダイアログより優先する）
                        _runtimeManager?.SendToRuntime("MODAL:CANCEL");
                        _modalTransformActive = false;
                        return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
                // それ以外のキーはモーダル中は無効。ただしカメラキー（WASDQE/Shift）の
                // DOWN/UP 対応が崩れないよう、下の転送処理には素通しする。
            }
            else
            {
                // モーダル開始キー。実際に開始できたかはランタイムが
                // MODAL_STATE で返してくるので、ここでは先行して立てておく。
                string? begin = vk switch
                {
                    0x47 => "MODAL:BEGIN:MOVE",   // G
                    0x52 => "MODAL:BEGIN:ROTATE", // R
                    0x53 => "MODAL:BEGIN:SCALE",  // S
                    _    => null,
                };
                if (begin is not null)
                {
                    _modalTransformActive = true;
                    _runtimeManager?.SendToRuntime(begin);
                    // G / R は他用途がないのでここで打ち切る。
                    // S はカメラ後退キーでもあるため、下の転送処理へ素通しする。
                    if (vk != 0x53) return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
                else
                {
                    // ツール切替ホットキー。RMB でのカメラ操作中は
                    // ランタイム側が無視するので、ここでは条件を持たない。
                    string? tool = vk switch
                    {
                        0x51 => "SELECT", // Q
                        0x57 => "MOVE",   // W
                        0x45 => "ROTATE", // E
                        0x54 => "SCALE",  // T
                        _    => null,
                    };
                    if (tool is not null)
                    {
                        _runtimeManager?.SendToRuntime($"TOOL_HOTKEY:{tool}");
                        // T はカメラキーではないのでここで打ち切る。
                        // Q / W / E はカメラ上下・前後キーでもあるため素通しする。
                        if (vk == 0x54) return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                    }
                }
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
    /// <summary>
    /// テキスト入力コントロールに WPF キーボードフォーカスがあるかを判定する。
    /// true の間はシーン操作系ショートカット・カメラキーをランタイムへ転送しない
    /// （スクリプトエディタでの Ctrl+C/V や WASD 入力がシーンを操作しないようにする）。
    ///
    /// 【設計メモ】埋め込みランタイムの子 HWND はキーボードフォーカスを取らないため、
    /// 「ビューポートにフォーカスがあるか」という正の条件は成立し得ない。
    /// テキスト編集中だけ止める負の条件でゲートするのが本フックの正しい形。
    /// </summary>
    private static bool IsTextInputFocused()
    {
        var focused = Keyboard.FocusedElement;
        if (focused is null) return false;
        // 標準のテキスト入力（TextBox/RichTextBox 系・パスワード欄）
        if (focused is System.Windows.Controls.Primitives.TextBoxBase) return true;
        if (focused is System.Windows.Controls.PasswordBox) return true;
        // スクリプトエディタ（AvalonEdit）の入力面は TextArea という独自要素のため型名で判定する
        var typeName = focused.GetType().FullName ?? string.Empty;
        return typeName.Contains("AvalonEdit", StringComparison.Ordinal);
    }

    /// <summary>
    /// 埋め込みランタイム（ビューポート内の子 HWND）がクリックされたときに、
    /// テキスト入力（スクリプトエディタ／インスペクタ等）に残っている WPF の
    /// キーボードフォーカスを外す。
    ///
    /// 【なぜ必要か】埋め込みランタイムは別プロセスの子 HWND であり、そこへのクリックは
    /// WPF の入力ルートを一切通らない。そのため直前までスクリプトエディタ(AvalonEdit)に
    /// フォーカスがあると <see cref="Keyboard.FocusedElement"/> はその TextArea のまま残る。
    /// すると <see cref="IsTextInputFocused"/> が true を返し続け、
    /// 低レベルキーボードフックはカメラキー(WASD 等)をランタイムへ転送しない一方、
    /// キー自体は WPF 経由でスクリプトエディタへ届いて文字入力になってしまう
    /// （＝ビューポートをクリックしたのにカメラが動かず、代わりに文字が打たれる不具合）。
    /// クリックを検知できるのは <see cref="Viewport.ViewportHost"/> が拾う
    /// WM_PARENTNOTIFY のみなので、それを契機にここでフォーカスを能動的に解除する。
    ///
    /// 【フォーカスの外し先】どこにも移さず null にする。
    /// <see cref="IsTextInputFocused"/> は FocusedElement が null なら false を返すため、
    /// これでカメラキー転送が復活する。またフォーカスを失った AvalonEdit は
    /// LostKeyboardFocus を受けてキャレット表示を消す。
    /// ビューポートの子 HWND 自体は WPF 管理外でありフォーカス先にできない
    /// （FocusedElement が null という状態こそが「ビューポート操作中」の正常形）。
    ///
    /// テキスト入力にフォーカスが無い場合は何もしない（他パネルの選択状態を壊さないため）。
    /// UI スレッドから呼ぶこと。
    /// </summary>
    private void ClearTextInputFocusForViewportClick()
    {
        if (!IsTextInputFocused()) return;

        // 論理フォーカスも解除する。キーボードフォーカスだけを消すと、
        // ウィンドウ再アクティブ化やパネル切り替えの際に FocusScope の記憶から
        // テキスト入力へフォーカスが戻り、同じ症状が再発するため。
        if (Keyboard.FocusedElement is DependencyObject focused)
        {
            var scope = FocusManager.GetFocusScope(focused);
            if (scope is not null) FocusManager.SetFocusedElement(scope, null);
        }

        // WPF のキーボードフォーカスを外す（FocusedElement = null）。
        Keyboard.ClearFocus();

        EditorLog.Write("ClearTextInputFocusForViewportClick — ビューポートクリックによりテキスト入力のフォーカスを解除");
    }

    private bool IsCamInputActive()
    {
        var state = _runtimeManager?.State;
        return state == EditorState.Edit || state == EditorState.Pause;
    }
}
