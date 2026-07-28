// ============================================================
//  MainWindow.Physics.cs — ツールモードと編集時物理シミュレーション
//
//  担当:
//   - ツールモード切り替え（Select/Move/Rotate/Scale）
//   - 実行時コライダー描画
//   - 編集時 3D/2D 物理シミュレーション
//   - 物理タイムライン（再生・停止・シーク・TrackFill）
//   - カーソルクランプ設定
// ============================================================

using System;
using System.Windows;
using System.Windows.Input;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── ツールモード ──────────────────────────────────────────────

    private void OnToolSelect(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("TOOL:SELECT");

    private void OnToolMove(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("TOOL:MOVE");

    private void OnToolRotate(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("TOOL:ROTATE");

    private void OnToolScale(object sender, RoutedEventArgs e)
        => _runtimeManager?.SendToRuntime("TOOL:SCALE");

    private void OnClampCursorChanged(object sender, RoutedEventArgs e)
    {
        _clampInPlay = ChkClampCursor.IsChecked == true;

        if (_runtimeManager?.State == EditorState.Play)
        {
            if (_clampInPlay && !_isDragging)
                ApplyPlayClamp();
            else
                ReleasePlayClamp();
        }
    }

    // ── 実行時コライダー描画 ────────────────────────────────────────

    /// <summary>
    /// "実行時コライダー描画" チェックボックスが変更されたときに呼ばれる。
    /// フラグを更新し、IPC コマンドで Runtime に通知する。
    /// </summary>
    private void OnPlayColliderDrawChanged(object sender, RoutedEventArgs e)
    {
        _playColliderDraw = ChkPlayColliderDraw.IsChecked == true;
        _runtimeManager?.SendToRuntime($"SET_PLAY_COLLIDER_DRAW:{(_playColliderDraw ? 1 : 0)}");
    }

    // ── 編集時の物理シミュレーション ────────────────────────────────

    /// <summary>
    /// シーン設定（_sceneSettings.Physics）の編集時物理フラグを、MainWindow が保持する
    /// ミラーフィールドへ写す。2D/3D は常に同値のため 2D 用フラグも同じ値にする。
    ///
    /// 親（編集時物理）が OFF のときは子（RigidBody）も必ず OFF に落とす。
    /// この規則は Rust 側の is_edit_physics_pushback_mode() と整合させるために必須。
    /// </summary>
    private void MirrorEditPhysicsFlags()
    {
        _editPhysicsEnabled       = _sceneSettings.Physics.EditPhysics;
        _editPhysicsWithRigidbody = _editPhysicsEnabled && _sceneSettings.Physics.EditPhysicsRigidbody;
        // 設定側にも「親 OFF なら子 OFF」を反映して食い違いを残さない
        _sceneSettings.Physics.EditPhysicsRigidbody = _editPhysicsWithRigidbody;

        // 2D フラグを 3D と同値へミラーする（常に一致させる）
        _editPhysics2dEnabled       = _editPhysicsEnabled;
        _editPhysics2dWithRigidbody = _editPhysicsWithRigidbody;
    }

    /// <summary>
    /// シーン設定の編集時物理をエディタ状態・ランタイムへ反映する。
    /// シーン設定ウィンドウでの変更通知（SceneSettingsChangeKind.Physics）から呼ばれる。
    /// タイムライン（再生バー）は RigidBody 有効時（タイムラインモード）のみ表示する。
    /// </summary>
    private void ApplyEditPhysicsFromSettings()
    {
        MirrorEditPhysicsFlags();
        RefreshPhysicsTimelineVisibility();
        SendEditPhysicsAll();
    }

    /// <summary>
    /// 編集時物理の統合コマンド SET_EDIT_PHYSICS_ALL を送信する。
    /// 2D/3D は常に同値のため 3D 用フラグを代表値として使う。
    /// </summary>
    private void SendEditPhysicsAll()
    {
        _runtimeManager?.SendToRuntime(
            $"SET_EDIT_PHYSICS_ALL:{(_editPhysicsEnabled ? 1 : 0)},{(_editPhysicsWithRigidbody ? 1 : 0)}");
    }

    /// <summary>
    /// タイムラインパネルの表示状態を、編集時物理の現在のチェック状態から再計算して適用する。
    ///
    /// Rust 側の is_edit_physics_pushback_mode() と同じ規則（RigidBody 有効なら
    /// タイムラインモード）に合わせる。2D/3D は同値のため 3D 用フラグで判定する。
    /// </summary>
    private void RefreshPhysicsTimelineVisibility()
    {
        bool timeline = _editPhysicsEnabled && _editPhysicsWithRigidbody;
        ShowPhysicsTimeline(timeline);
    }

    // ── 編集時物理タイムライン ─────────────────────────────────────────────────

    /// <summary>タイムラインパネルの表示/非表示を切り替える。</summary>
    private void ShowPhysicsTimeline(bool show)
    {
        PhysicsTimelineRow.Height = show
            ? new GridLength(36)
            : new GridLength(0);
        PhysicsTimelinePanel.Visibility = show
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!show)
        {
            // 非表示時にスライダーをリセットする。
            // Maximum の「縮小しない」方針を採っているため、
            // 物理を無効→再有効にした際に古い Maximum が残らないようにここでリセットする。
            _suppressSliderEvents = true;
            SldPhysicsTimeline.Maximum = 0;
            SldPhysicsTimeline.Value   = 0;
            _suppressSliderEvents = false;
        }
    }

    /// <summary>Rust から EDIT_PHYSICS_STATE を受信したときにUIを更新する。</summary>
    private void OnEditPhysicsStateReceived(string payload)
    {
        // フォーマット: "paused,at_latest,current_frame,total_frames,time_sec"
        Dispatcher.InvokeAsync(() =>
        {
            var parts = payload.Split(',');
            if (parts.Length < 5) return;

            bool paused = parts[0] == "1";
            // at_latest は parts[1]
            if (!int.TryParse(parts[2], out int curFrame))    return;
            if (!int.TryParse(parts[3], out int totalFrames)) return;
            if (!double.TryParse(parts[4],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double timeSec)) return;

            // タイムライン初期化直後（シーン切り替え・物理再有効化・フレーム0リスタート）を検知して
            // Maximum をリセットする。
            // ・init_physics_timeline / apply_frame 直後: paused=1, total=1, cur=0, time=0
            // ・フレーム0から再スタート直後: paused=0, total=1, cur=0, time=0
            // paused を条件に含めると再スタート直後のリセットが行われないため除外する。
            bool isTimelineInit = totalFrames == 1 && curFrame == 0
                                  && Math.Abs(timeSec) < 1e-6;
            if (isTimelineInit)
            {
                _suppressSliderEvents = true;
                SldPhysicsTimeline.Maximum = 0;
                SldPhysicsTimeline.Value   = 0;
                _suppressSliderEvents = false;
            }

            _physicsTimelinePaused = paused;
            _physicsCurrentFrame   = curFrame;
            _physicsTotalFrames    = totalFrames;
            _physicsTimeSec        = timeSec;

            UpdatePhysicsTimelineUI();
        });
    }

    /// <summary>シークバーと時間テキストをRustの状態に合わせて更新する。</summary>
    private void UpdatePhysicsTimelineUI()
    {
        // スライダーのMaximumとValueをプログラムから更新（ValueChangedを抑制）
        _suppressSliderEvents = true;

        // Maximum は常に増加方向のみ更新する（再生中・停止中を問わず縮小しない）。
        // ・停止時の縮小を防ぐことで Thumb の右端瞬間移動を回避する。
        // ・再生中も更新することで初回再生時（Maximum=0 から始まる場合）に
        //   TrackFill の Width が常に 0 になる問題を解消する。
        // Maximum のリセットは ShowPhysicsTimeline(false) 時と
        // タイムライン初期化検知（OnEditPhysicsStateReceived）時に行う。
        {
            double newMax = Math.Max(0, _physicsTotalFrames - 1);
            if (newMax > SldPhysicsTimeline.Maximum)
                SldPhysicsTimeline.Maximum = newMax;
        }
        double maxVal = SldPhysicsTimeline.Maximum;
        SldPhysicsTimeline.Value = Math.Min(_physicsCurrentFrame, maxVal);

        _suppressSliderEvents = false;

        // TrackFill（進捗バー）の幅を更新する
        UpdatePhysicsTrackFill();

        // 再生ボタンのテキスト（再生中:■、停止中:▶）
        TxtPhysicsPlayBtn.Text = _physicsTimelinePaused ? "▶" : "■";
        // 再生中は水色、停止中は白
        var timerColor = _physicsTimelinePaused
            ? System.Windows.Media.Color.FromRgb(0xAA, 0xAA, 0xAA)
            : System.Windows.Media.Color.FromRgb(0x44, 0xCC, 0xFF);
        TxtPhysicsTimer.Foreground = new System.Windows.Media.SolidColorBrush(timerColor);

        // 現在時刻
        TxtPhysicsTimer.Text = $"{_physicsTimeSec:F2}s";

        // 総フレーム・総時間（最終フレームの時間は Rust 側から来ないため frames で代用）
        TxtPhysicsTotalTime.Text = $"f{_physicsCurrentFrame} / f{Math.Max(0, _physicsTotalFrames - 1)}";
    }

    /// <summary>TrackFill（進捗バー）の Width を Value/Maximum の比率で更新する。</summary>
    private void UpdatePhysicsTrackFill()
    {
        // Loaded タイミングで取得に失敗している場合に再取得を試みる
        if (_physicsTrackFill == null)
        {
            SldPhysicsTimeline.ApplyTemplate();
            _physicsTrackFill = SldPhysicsTimeline.Template
                .FindName("TrackFill", SldPhysicsTimeline) as System.Windows.Controls.Border;
        }
        if (_physicsTrackFill == null) return;
        double max = SldPhysicsTimeline.Maximum;
        if (max <= 0 || SldPhysicsTimeline.ActualWidth <= 0)
        {
            _physicsTrackFill.Width = 0;
            return;
        }
        double ratio = SldPhysicsTimeline.Value / max;
        // Thumb 幅（12px）を除いたトラック幅で計算し、左端のThumb余白（6px）を加算
        double trackWidth = Math.Max(0, SldPhysicsTimeline.ActualWidth - 12);
        _physicsTrackFill.Width = ratio * trackWidth + 6; // +6 = Thumb中心までオフセット
    }

    /// <summary>再生/停止ボタン（▶/■）クリック。</summary>
    private void OnPhysicsTimerClick(object sender, MouseButtonEventArgs e)
    {
        // UpdatePhysicsTimelineUI で Maximum は常に増加方向のみ更新するため、
        // ここでは特別な処理は不要。
        _runtimeManager?.SendToRuntime("EDIT_PHYSICS_PLAY_PAUSE");
        e.Handled = true;
    }

    /// <summary>適用ボタン（✓）クリック — 現在フレームをフレーム 0 として確定する。</summary>
    private void OnPhysicsApplyClick(object sender, MouseButtonEventArgs e)
    {
        _runtimeManager?.SendToRuntime("EDIT_PHYSICS_APPLY_FRAME");
        e.Handled = true;
    }

    /// <summary>
    /// シークバーの ValueChanged イベント。
    /// マウス操作中（クリック・ドラッグ共通）のみリアルタイムでシークを送信する。
    /// </summary>
    private void OnPhysicsSliderValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        // プログラムからの変更（UpdatePhysicsTimelineUI）は無視する
        if (_suppressSliderEvents) return;
        // マウス操作中のみシーク送信（RepeatButtonのLargeChangeクリックも含む）
        if (!_sliderInteracting) return;

        int frame = (int)SldPhysicsTimeline.Value;
        _runtimeManager?.SendToRuntime($"EDIT_PHYSICS_SEEK:{frame}");
    }

    /// <summary>シークバーのイベントをバインドする。</summary>
    private void BindPhysicsStepButtons()
    {
        // ── Loaded: TrackFill（進捗バー）への参照を取得する ──────────────────
        // ControlTemplate 内の要素は Template.FindName() でアクセスする
        SldPhysicsTimeline.Loaded += (s, e) =>
        {
            SldPhysicsTimeline.ApplyTemplate();
            _physicsTrackFill = SldPhysicsTimeline.Template
                .FindName("TrackFill", SldPhysicsTimeline) as System.Windows.Controls.Border;
            UpdatePhysicsTrackFill();
        };
        // サイズ変更時に TrackFill の幅を再計算する
        SldPhysicsTimeline.SizeChanged += (s, e) => UpdatePhysicsTrackFill();

        // ── PreviewMouseLeftButtonDown ────────────────────────────────────────
        // クリック位置からフレームを直接計算してシーク（Thumbドラッグ・トラッククリック共通）。
        // 再生中にクリックされた場合は Rust 側が自動で一時停止してシークするため、
        // ここでは「再生中だったか」を記録するだけでよい（別途 PLAY_PAUSE を送る必要はない）。
        SldPhysicsTimeline.PreviewMouseLeftButtonDown += (s, e) =>
        {
            _sliderInteracting = true;
            // 再生中だった場合: マウスリリース時に再開できるようフラグを立てる
            _physicsWasPlayingBeforeSeek = !_physicsTimelinePaused;
            SeekSliderByMousePosition(e.GetPosition(SldPhysicsTimeline));
            SldPhysicsTimeline.CaptureMouse();
            e.Handled = true;
        };

        // ── MouseMove ────────────────────────────────────────────────────────
        // ドラッグ中のリアルタイムシーク（マウスキャプチャ中のみ）
        SldPhysicsTimeline.MouseMove += (s, e) =>
        {
            if (!_sliderInteracting) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;
            SeekSliderByMousePosition(e.GetPosition(SldPhysicsTimeline));
        };

        // ── PreviewMouseLeftButtonUp ──────────────────────────────────────────
        // マウスリリース時に最終シークを確定してキャプチャ解放。
        // シーク開始前に再生中だった場合は PLAY_PAUSE で再開する。
        SldPhysicsTimeline.PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (!_sliderInteracting) return;
            _sliderInteracting = false;
            SldPhysicsTimeline.ReleaseMouseCapture();
            int frame = (int)SldPhysicsTimeline.Value;
            _runtimeManager?.SendToRuntime($"EDIT_PHYSICS_SEEK:{frame}");

            // 再生中にシークを開始していた場合: リリース時に再生を再開する
            if (_physicsWasPlayingBeforeSeek)
            {
                _physicsWasPlayingBeforeSeek = false;
                _runtimeManager?.SendToRuntime("EDIT_PHYSICS_PLAY_PAUSE");
            }
        };
    }

    /// <summary>
    /// マウス座標からスライダーのフレームを計算してシークする。
    /// Thumb 幅（6px 余白）を考慮してクリック位置を正規化する。
    /// </summary>
    private void SeekSliderByMousePosition(Point pos)
    {
        const double thumbHalfWidth = 6.0; // Thumb の Width=12 の半分
        double trackWidth = SldPhysicsTimeline.ActualWidth - 2 * thumbHalfWidth;
        if (trackWidth <= 0) return;

        double ratio = Math.Clamp((pos.X - thumbHalfWidth) / trackWidth, 0.0, 1.0);
        int frame = (int)Math.Round(ratio * SldPhysicsTimeline.Maximum);

        _suppressSliderEvents = true;
        SldPhysicsTimeline.Value = frame;
        _suppressSliderEvents = false;

        _runtimeManager?.SendToRuntime($"EDIT_PHYSICS_SEEK:{frame}");
    }
}
