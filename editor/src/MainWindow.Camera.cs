// ============================================================
//  MainWindow.Camera.cs — ビューポートオプションとカメラ制御
//
//  担当:
//   - ビューポートオプションポップアップ（FOV・Far・グリッド・速度など）
//   - スライダー横の数値入力フィールド
//   - カメラ Transform 手動入力
//   - カメラ状態受信と UI 同期
//   - 全カメラキーのリリース
//   - エディタ状態変化への UI 反応（ボタン・ラベル・FPS など）
// ============================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── Viewport オプション ──────────────────────────────────────

    /// <summary>ビューポート設定の初期化完了フラグ。false 中はイベントハンドラを無視する。</summary>
    private bool _updatingControls = false;

    /// <summary>ポップアップをアイコン右上に配置するコールバック。</summary>
    public System.Windows.Controls.Primitives.CustomPopupPlacement[]
        OnViewportPopupPlacement(Size popupSize, Size targetSize, Point offset)
    {
        // ボタン右端・ボタン下端にポップアップ底面を揃えて上方向へ展開
        double x = targetSize.Width + 4;
        double y = targetSize.Height - popupSize.Height;
        return [new(new Point(x, y), System.Windows.Controls.Primitives.PopupPrimaryAxis.Vertical)];
    }

    private void OnViewportOptions(object sender, RoutedEventArgs e)
    {
        bool opening = !ViewportOptionsPopup.IsOpen;
        ViewportOptionsPopup.IsOpen = opening;
        if (opening && _runtimeManager?.State == EditorState.Edit)
            _runtimeManager.SendToRuntime("GET_CAM_STATE");
    }

    private void OnFovChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_viewportSettingsInitialized || _updatingControls) return;
        var v = (int)SldFov.Value;
        _updatingControls = true;
        TbFovInput.Text = v.ToString();
        _updatingControls = false;
        _runtimeManager?.SendToRuntime($"VIEWPORT_FOV:{v}");
    }

    private void OnFarChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_viewportSettingsInitialized || _updatingControls) return;
        var v = (int)SldFar.Value;
        _updatingControls = true;
        TbFarInput.Text = v.ToString();
        _updatingControls = false;
        _runtimeManager?.SendToRuntime($"VIEWPORT_FAR:{v}");
    }

    private void OnShowGridChanged(object sender, RoutedEventArgs e)
    {
        if (!_viewportSettingsInitialized) return;
        _runtimeManager?.SendToRuntime($"SHOW_GRID:{(ChkShowGrid.IsChecked == true ? "1" : "0")}");
    }

    private void OnShowAxisGizmoChanged(object sender, RoutedEventArgs e)
    {
        if (!_viewportSettingsInitialized) return;
        _runtimeManager?.SendToRuntime($"SHOW_AXIS_GIZMO:{(ChkShowAxisGizmo.IsChecked == true ? "1" : "0")}");
    }

    /// キャンバス表示モード切り替え（スクリーンスペース / ワールドスペース）
    private void OnCanvasScreenSpaceChanged(object sender, RoutedEventArgs e)
    {
        if (!_viewportSettingsInitialized) return;
        _runtimeManager?.SendToRuntime($"CANVAS_SS_OVERLAY:{(ChkCanvasScreenSpace.IsChecked == true ? "1" : "0")}");
    }

    private void OnCamSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_viewportSettingsInitialized || _updatingControls) return;
        var v = Math.Round(SldCamSpeed.Value, 2);
        _updatingControls = true;
        TbSpeedInput.Text = $"{v:F1}";
        _updatingControls = false;
        _runtimeManager?.SendToRuntime($"CAM_SPEED:{v.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    // ── スライダー横の数値入力 ────────────────────────────────────

    private void OnSliderNumKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            ApplySliderTextInput(tb);
            e.Handled = true;
        }
    }

    private void OnSliderNumLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) ApplySliderTextInput(tb);
    }

    private void ApplySliderTextInput(TextBox tb)
    {
        if (_updatingControls || !_viewportSettingsInitialized) return;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        if (!double.TryParse(tb.Text, System.Globalization.NumberStyles.Float, ic, out var v)) return;

        if (tb == TbFovInput)
        {
            v = Math.Clamp(v, SldFov.Minimum, SldFov.Maximum);
            var vi = (int)v;
            _updatingControls = true;
            SldFov.Value = vi;
            tb.Text = vi.ToString();
            _updatingControls = false;
            _runtimeManager?.SendToRuntime($"VIEWPORT_FOV:{vi}");
        }
        else if (tb == TbFarInput)
        {
            v = Math.Clamp(v, SldFar.Minimum, SldFar.Maximum);
            var vi = (int)v;
            _updatingControls = true;
            SldFar.Value = vi;
            tb.Text = vi.ToString();
            _updatingControls = false;
            _runtimeManager?.SendToRuntime($"VIEWPORT_FAR:{vi}");
        }
        else if (tb == TbSpeedInput)
        {
            v = Math.Clamp(v, SldCamSpeed.Minimum, SldCamSpeed.Maximum);
            _updatingControls = true;
            SldCamSpeed.Value = v;
            tb.Text = $"{v:F1}";
            _updatingControls = false;
            _runtimeManager?.SendToRuntime($"CAM_SPEED:{v.ToString(ic)}");
        }
    }

    // ── カメラ Transform 入力 ─────────────────────────────────────

    private void OnCamFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitCamTransform();
    }

    private void OnCamFieldLostFocus(object sender, RoutedEventArgs e)
    {
        CommitCamTransform();
    }

    private void CommitCamTransform()
    {
        if (!_viewportSettingsInitialized) return;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var ns = System.Globalization.NumberStyles.Float;
        if (!float.TryParse(TbCamPx.Text,  ns, ic, out float px)) return;
        if (!float.TryParse(TbCamPy.Text,  ns, ic, out float py)) return;
        if (!float.TryParse(TbCamPz.Text,  ns, ic, out float pz)) return;
        if (!float.TryParse(TbCamEuX.Text, ns, ic, out float ex)) return;
        if (!float.TryParse(TbCamEuY.Text, ns, ic, out float ey)) return;
        if (!float.TryParse(TbCamEuZ.Text, ns, ic, out float ez)) return;
        _runtimeManager?.SendToRuntime(
            $"CAM_TRANSFORM:{px.ToString(ic)},{py.ToString(ic)},{pz.ToString(ic)},{ex.ToString(ic)},{ey.ToString(ic)},{ez.ToString(ic)}");
    }

    // ── カメラ状態受信 ────────────────────────────────────────────

    private void OnCameraStateReceived(string payload)
    {
        // CAM_STATE:{px},{py},{pz},{euler_x},{euler_y},{euler_z},{fov_deg},{far},{speed}
        var parts = payload.Split(',');
        if (parts.Length < 9) return;
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var ns = System.Globalization.NumberStyles.Float;
        if (!float.TryParse(parts[0], ns, ic, out float px))    return;
        if (!float.TryParse(parts[1], ns, ic, out float py))    return;
        if (!float.TryParse(parts[2], ns, ic, out float pz))    return;
        if (!float.TryParse(parts[3], ns, ic, out float ex))    return;
        if (!float.TryParse(parts[4], ns, ic, out float ey))    return;
        if (!float.TryParse(parts[5], ns, ic, out float ez))    return;
        if (!float.TryParse(parts[6], ns, ic, out float fov))   return;
        if (!float.TryParse(parts[7], ns, ic, out float far))   return;
        if (!float.TryParse(parts[8], ns, ic, out float speed)) return;

        Dispatcher.InvokeAsync(() =>
        {
            bool prev = _viewportSettingsInitialized;
            _viewportSettingsInitialized = false;
            _updatingControls = true;
            TbCamPx.Text  = Fmt(px);
            TbCamPy.Text  = Fmt(py);
            TbCamPz.Text  = Fmt(pz);
            TbCamEuX.Text = Fmt(ex);
            TbCamEuY.Text = Fmt(ey);
            TbCamEuZ.Text = Fmt(ez);
            var fovC   = Math.Clamp(fov,   SldFov.Minimum,      SldFov.Maximum);
            var farC   = Math.Clamp(far,   SldFar.Minimum,      SldFar.Maximum);
            var spdC   = Math.Clamp(speed, SldCamSpeed.Minimum, SldCamSpeed.Maximum);
            SldFov.Value      = fovC;   TbFovInput.Text   = ((int)fovC).ToString();
            SldFar.Value      = farC;   TbFarInput.Text   = ((int)farC).ToString();
            SldCamSpeed.Value = spdC;   TbSpeedInput.Text = $"{spdC:F1}";
            _updatingControls = false;
            _viewportSettingsInitialized = prev;
        });
    }

    private static string Fmt(float v) =>
        v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    private void OnResetViewportSettings(object sender, RoutedEventArgs e)
    {
        _updatingControls = true;
        SldFov.Value      = 45;   TbFovInput.Text   = "45";
        SldFar.Value      = 1000; TbFarInput.Text   = "1000";
        SldCamSpeed.Value = 5;    TbSpeedInput.Text = "5.0";
        _updatingControls = false;
        ChkShowGrid.IsChecked          = true;
        ChkShowAxisGizmo.IsChecked     = true;
        ChkCanvasScreenSpace.IsChecked = false;
        if (_viewportSettingsInitialized)
        {
            _runtimeManager?.SendToRuntime("VIEWPORT_FOV:45");
            _runtimeManager?.SendToRuntime("VIEWPORT_FAR:1000");
            _runtimeManager?.SendToRuntime("CAM_SPEED:5");
            _runtimeManager?.SendToRuntime("CANVAS_SS_OVERLAY:0");
        }
        TbCamPx.Text = "0"; TbCamPy.Text = "2"; TbCamPz.Text = "-10";
        TbCamEuX.Text = "0"; TbCamEuY.Text = "0"; TbCamEuZ.Text = "0";
        CommitCamTransform();
    }

    private void SyncViewportSettings()
    {
        _viewportSettingsInitialized = true;
        var fov = (int)SldFov.Value;
        TbFovInput.Text = fov.ToString();
        _runtimeManager?.SendToRuntime($"VIEWPORT_FOV:{fov}");
        var far = (int)SldFar.Value;
        TbFarInput.Text = far.ToString();
        _runtimeManager?.SendToRuntime($"VIEWPORT_FAR:{far}");
        _runtimeManager?.SendToRuntime($"SHOW_GRID:{(ChkShowGrid.IsChecked == true ? "1" : "0")}");
        _runtimeManager?.SendToRuntime($"SHOW_AXIS_GIZMO:{(ChkShowAxisGizmo.IsChecked == true ? "1" : "0")}");
        var spd = Math.Round(SldCamSpeed.Value, 2);
        TbSpeedInput.Text = $"{spd:F1}";
        _runtimeManager?.SendToRuntime($"CAM_SPEED:{spd.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        // 編集時物理設定は Edit runtime にのみ送信する（Play runtime へ送ると物理スレッドが停止する）
        if (_runtimeManager?.State == EditorState.Edit)
        {
            // 現在のシーンタブ（3Dシーン/2Dシーン）のビューモードを同期する。
            // 起動時・ランタイム再接続時・Play→Edit 復帰時のいずれもここを通るため、
            // ランタイムのビューモードが常にタブ UI と一致する。
            SendCurrentEditView();
            _runtimeManager?.SendToRuntime(
                $"SET_EDIT_PHYSICS:{(_editPhysicsEnabled ? 1 : 0)},{(_editPhysicsWithRigidbody ? 1 : 0)}");
            _runtimeManager?.SendToRuntime(
                $"SET_EDIT_PHYSICS_2D:{(_editPhysics2dEnabled ? 1 : 0)},{(_editPhysics2dWithRigidbody ? 1 : 0)}");
        }
        // コライダー描画は Play/Edit 両方で送信する
        _runtimeManager?.SendToRuntime($"SET_PLAY_COLLIDER_DRAW:{(_playColliderDraw ? 1 : 0)}");
    }

    private void ReleaseAllCamKeys()
    {
        foreach (var vk in _pressedVks)
        {
            if (VkKeyMap.TryGetValue(vk, out var keyName))
                _runtimeManager?.SendToRuntime($"CAM_KEY_UP:{keyName}");
        }
        _pressedVks.Clear();
    }

    // ── 状態変化への UI 反応 ───────────────────────────────────────

    private void OnStateChanged(EditorState state)
    {
        EditorLog.Write($"OnStateChanged — {state}");
        Dispatcher.BeginInvoke(() =>
        {
            ApplyUiState(state);
            // Play へ遷移したら、スクリプトデバッグが有効な場合に
            // 新しい Play プロセスへ自動アタッチする（スクリプトは Play 中のみ動くため）。
            if (state == EditorState.Play)
                TryAutoAttachDebuggerOnPlay();
        });
    }

    private void ApplyUiState(EditorState state)
    {
        if (state != EditorState.Edit && state != EditorState.Pause)
            _pressedVks.Clear();

        // Play 移行時はクランプ適用、それ以外は解除
        // RuntimeHwnd が 0 の場合は OnRuntimeHwndAvailable でリトライされる
        if (state == EditorState.Play && _clampInPlay && !_isDragging)
            ApplyPlayClamp();
        else
            ReleasePlayClamp();

        EditorLog.Write($"ApplyUiState — {state}");

        // シーン/アクタータブバーは Edit モードのみ操作可能にする
        // （Play 中はランタイムが EDIT_VIEW を無視するため UI 側も無効化する）
        ActorTabBar.IsEnabled = state == EditorState.Edit;

        switch (state)
        {
            case EditorState.Edit:
                _pressedVks.Clear();
                BtnPlayPause.IsEnabled   = true;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = false;
                LblState.Text            = "● EDIT";
                LblState.Foreground      = Brushes.LightGreen;
                ViewportDocumentContent.Visibility = Visibility.Visible;
                TxtViewportStatus.Text             = "";
                ViewportLoadingOverlay.Visibility  = Visibility.Visible;
                Activate();
                break;

            case EditorState.Play:
                BtnPlayPause.IsEnabled   = true;
                BtnPlayPause.Background  = _brushPause;
                ImgPlayPause.Source      = _imgPause;
                BtnStop.IsEnabled        = true;
                LblState.Text            = "▶ PLAY";
                LblState.Foreground      = Brushes.LightSkyBlue;
                ViewportDocumentContent.Visibility = Visibility.Hidden;
                ViewportLoadingOverlay.Visibility  = Visibility.Collapsed;
                break;

            case EditorState.Pause:
                BtnPlayPause.IsEnabled   = true;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = true;
                LblState.Text            = "⏸ PAUSE";
                LblState.Foreground      = Brushes.Orange;
                ViewportDocumentContent.Visibility = Visibility.Visible;
                ViewportLoadingOverlay.Visibility  = Visibility.Collapsed;
                break;

            case EditorState.Building:
                BtnPlayPause.IsEnabled   = false;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = false;
                LblState.Text            = "⚙ BUILDING...";
                LblState.Foreground      = Brushes.Yellow;
                TxtViewportStatus.Text            = "ビルド中...";
                ViewportLoadingOverlay.Visibility = Visibility.Visible;
                break;

            case EditorState.Idle:
                BtnPlayPause.IsEnabled   = false;
                BtnPlayPause.Background  = _brushPlay;
                ImgPlayPause.Source      = _imgPlay;
                BtnStop.IsEnabled        = false;
                LblState.Text            = "○ IDLE";
                LblState.Foreground      = Brushes.Gray;
                TxtViewportStatus.Text            = "再起動中...";
                ViewportLoadingOverlay.Visibility = Visibility.Visible;
                break;
        }

        // FPS 表示: Edit / Pause / Play のときのみ表示（Idle / Building では非表示）
        var showFps = state == EditorState.Edit
                   || state == EditorState.Pause
                   || state == EditorState.Play;
        TxtFps.Visibility = showFps ? Visibility.Visible : Visibility.Collapsed;
        if (!showFps) TxtFps.Text = "";
    }

    /// <summary>ランタイムからFPS通知を受け取ったときにUI上の表示を更新する。</summary>
    private void OnFpsReceived(float fps)
    {
        // IPC コールバックはバックグラウンドスレッドから来るため Dispatcher 経由で更新する
        Dispatcher.BeginInvoke(() =>
        {
            TxtFps.Text = $"FPS: {fps:F1}";
        });
    }
}
