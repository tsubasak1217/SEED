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
        // IsEditorForeground() が false になるケース:
        //   Pause中に埋め込みPlayウィンドウがフォーカスを持つと
        //   GetForegroundWindow() がランタイムPIDを返すため false になる。
        // IsCamInputActive() が true の場合（Edit/Pause）はカメラキー転送を
        // 行う必要があるため、フォーカス不問でブロックに入る。
        // 内部の Ctrl+Z 等は別途 State == Edit で保護されているので問題なし。
        if (nCode >= 0 && (IsEditorForeground() || IsCamInputActive()))
        {
            var kb     = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vk     = kb.vkCode;
            bool isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool isUp   = wParam == WM_KEYUP   || wParam == WM_SYSKEYUP;

            // Ctrl キー追跡 + Rust へ転送
            if (vk == 0x11 || vk == 0xA2 || vk == 0xA3)
            {
                if (isDown && !_ctrlHeld)
                    _runtimeManager?.SendToRuntime("CTRL_DOWN");
                else if (isUp && _ctrlHeld)
                    _runtimeManager?.SendToRuntime("CTRL_UP");
                _ctrlHeld = isDown;
            }
            // Ctrl+Z / Ctrl+Y / Ctrl+S → IPC 経由で転送（Edit モードのみ）
            else if (isDown && _ctrlHeld && _runtimeManager?.State == EditorState.Edit)
            {
                if (vk == 0x5A) // Z
                {
                    _runtimeManager?.SendToRuntime("UNDO");
                    return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
                else if (vk == 0x59) // Y
                {
                    _runtimeManager?.SendToRuntime("REDO");
                    return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
                else if (vk == 0x53) // S
                {
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
                        if (FocusManager.GetFocusedElement(this) is System.Windows.Controls.TextBox) return;
                        if (PanelProject.IsKeyboardFocusWithin) PanelProject.HandleCopy();
                        else _runtimeManager?.SendToRuntime("COPY");
                    });
                    return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
                else if (vk == 0x58) // X
                {
                    Dispatcher.BeginInvoke(() => {
                        if (FocusManager.GetFocusedElement(this) is System.Windows.Controls.TextBox) return;
                        if (PanelProject.IsKeyboardFocusWithin) PanelProject.HandleCut();
                    });
                    return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
                else if (vk == 0x56) // V
                {
                    Dispatcher.BeginInvoke(() => {
                        if (FocusManager.GetFocusedElement(this) is System.Windows.Controls.TextBox) return;
                        if (PanelProject.IsKeyboardFocusWithin) PanelProject.HandlePaste();
                        else _runtimeManager?.SendToRuntime("PASTE");
                    });
                    return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
                }
            }

            // ESC / Del → 選択インスタンス削除（ダイアログあり）
            if (isDown && (vk == 0x1B || vk == 0x2E) && !_ctrlHeld
                && _runtimeManager?.State == EditorState.Edit
                && !_deleteDialogOpen)
            {
                Dispatcher.BeginInvoke(TryDeleteSelected);
                return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
            }

            if (VkKeyMap.TryGetValue(vk, out var keyName))
            {
                if (isDown && IsCamInputActive() && _pressedVks.Add(vk))
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
        }
        return CallNextHookEx(_llKeyHook, nCode, wParam, lParam);
    }

    private static bool IsEditorForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == 0) return false;
        GetWindowThreadProcessId(fg, out var fgPid);
        return fgPid == (uint)Environment.ProcessId;
    }

    private bool IsCamInputActive()
    {
        var state = _runtimeManager?.State;
        return state == EditorState.Edit || state == EditorState.Pause;
    }
}
