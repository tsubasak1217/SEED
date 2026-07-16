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

    // ── ポストプロセス（Bloom / FXAA / 透明描画） ───────────────────────────

    /// <summary>ブルーム強度のデフォルト値。見た目を変えない後方互換のため 0.6 とする。</summary>
    private const double DefaultBloomIntensity = 0.6;
    /// <summary>GI 強度のデフォルト値（Phase RT-GI）。SET_POST_FX の "gi_intensity" フィールドに使う。</summary>
    private const double DefaultGiIntensity = 1.0;

    /// <summary>透明描画方式のデフォルト値（距離ソート）。SET_POST_FX の "transparency" フィールドに使う。</summary>
    private const string DefaultTransparencyMode = "sort";

    /// <summary>シーンビュー表示モードのデフォルト値（ライティングON）。SET_POST_FX の "view_mode" フィールドに使う。</summary>
    private const string DefaultViewMode = "lit";

    /// <summary>
    /// ポストプロセス設定（Bloom 有効/強度・FXAA 有効・透明描画方式）をまとめて 1 つの JSON にして
    /// ランタイムへ送信する共通処理。CheckBox・Slider・ComboBox いずれの変更イベントからも
    /// この関数を呼び出すことで、送信フォーマット（SET_POST_FX:{json}）を一箇所に集約する。
    /// </summary>
    private void SendPostFx()
    {
        if (!_viewportSettingsInitialized) return;

        // XAML 初期化中に ValueChanged/Checked/SelectionChanged が発火した場合に備え、
        // 各コントロールの null チェックを行いデフォルト値へフォールバックする。
        bool bloom = ChkBloom?.IsChecked == true;
        bool fxaa  = ChkFxaa?.IsChecked == true;
        double intensity = SldBloomIntensity?.Value ?? DefaultBloomIntensity;
        // CmbTransparency の選択アイテムの Tag（"sort" / "wboit"）を読み取る。未選択・null の場合は既定の距離ソート。
        string transparency = (CmbTransparency?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string
                               ?? DefaultTransparencyMode;
        // GPU メッシュレットカリング（第1弾）。null（未初期化）時は既定 true。
        bool meshletCull = ChkMeshletCull?.IsChecked != false;
        // Deferred レンダリング（G-Buffer + フルスクリーン・ライティング）。null（未初期化）時は既定 true。
        bool deferred = ChkDeferred?.IsChecked != false;
        // シーンビュー表示モード（"lit" / "unlit" / "wireframe"）。未選択・null 時は既定の "lit"。
        string viewMode = (CmbViewMode?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string
                          ?? DefaultViewMode;
        // GI 強度（DDGI の数値パラメータ）。null（未初期化）時は既定 1.0。
        double giIntensity = SldGiIntensity?.Value ?? DefaultGiIntensity;
        // レンダリング機能マトリクス（影/GI/反射/AO/半透明）。各コンボの Tag（小文字モード文字列）を読む。
        // 未初期化・null 時は現状維持の既定へフォールバックする（GI のみ現状維持のため既定 "rt"）。
        // 「（未実装）」の ssr/ssao/rt を選んでもランタイム側で従来動作へフォールバックする。
        string shadow       = (CmbShadow?.SelectedItem       as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "shadowmap";
        string giMode       = (CmbGi?.SelectedItem           as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "rt";
        string reflection   = (CmbReflection?.SelectedItem   as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "off";
        string ao           = (CmbAo?.SelectedItem           as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "off";
        string translucency = (CmbTranslucency?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "raster";

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        // 新キー "features"（機能マトリクス）。旧キー gi_enabled は features.gi へ移行したため送らない。
        string features = $"\"features\":{{\"shadow\":\"{shadow}\",\"gi\":\"{giMode}\",\"reflection\":\"{reflection}\",\"ao\":\"{ao}\",\"translucency\":\"{translucency}\"}}";
        string json = $"{{\"bloom\":{(bloom ? "true" : "false")},\"fxaa\":{(fxaa ? "true" : "false")},\"bloom_intensity\":{intensity.ToString(ci)},\"transparency\":\"{transparency}\",\"meshlet_cull\":{(meshletCull ? "true" : "false")},\"deferred\":{(deferred ? "true" : "false")},\"view_mode\":\"{viewMode}\",\"gi_intensity\":{giIntensity.ToString(ci)},{features}}}";
        _runtimeManager?.SendToRuntime($"SET_POST_FX:{json}");
    }

    /// <summary>ChkBloom / ChkFxaa の Checked/Unchecked から呼ばれる共通ハンドラ。</summary>
    private void OnPostFxChanged(object sender, RoutedEventArgs e)
    {
        SendPostFx();
    }

    /// <summary>SldBloomIntensity の ValueChanged から呼ばれる薄いハンドラ（Slider は戻り値の型が異なるため分離）。</summary>
    private void OnPostFxSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls) return;
        SendPostFx();
    }

    /// <summary>CmbTransparency の SelectionChanged から呼ばれるハンドラ。透明描画方式の変更をランタイムへ送信する。</summary>
    private void OnTransparencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls) return;
        SendPostFx();
    }

    /// <summary>CmbViewMode の SelectionChanged から呼ばれるハンドラ。シーンビュー表示モードの変更をランタイムへ送信する。</summary>
    private void OnViewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls) return;
        SendPostFx();
    }

    /// <summary>
    /// レンダリング機能コンボ（影/GI/反射/AO/半透明）の SelectionChanged 共通ハンドラ。
    /// いずれのモード変更も SendPostFx で "features" として一括送信する。
    /// </summary>
    private void OnRenderFeatureChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls) return;
        SendPostFx();
    }

    // ── 環境光（アンビエント） ───────────────────────────────────

    /// <summary>環境光の色（リニア RGB）。既定は白。ランタイム側の DEFAULT_AMBIENT_COLOR と一致させる。</summary>
    private float _ambientR = 1f, _ambientG = 1f, _ambientB = 1f;

    /// <summary>
    /// 環境光（アンビエント）の色・強度をランタイムへ送信する（SET_AMBIENT:{r},{g},{b},{intensity}）。
    /// 色はリニア RGB。強度 0 で完全な暗闇になる。UI 変更時のみ送信し、起動時は
    /// project_settings.json のランタイム側読込値を尊重する（ここからは自動送信しない）。
    /// </summary>
    private void SendAmbient()
    {
        if (!_viewportSettingsInitialized) return;
        double intensity = SldAmbientIntensity?.Value ?? 0.05;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        _runtimeManager?.SendToRuntime(
            $"SET_AMBIENT:{_ambientR.ToString(ci)},{_ambientG.ToString(ci)},{_ambientB.ToString(ci)},{intensity.ToString(ci)}");
    }

    /// <summary>環境光カラースウォッチのクリック: カラーピッカーを開いて色を選び、ランタイムへ送信する。</summary>
    private void OnAmbientColorClicked(object sender, MouseButtonEventArgs e)
    {
        // ライト色と同様にアルファは持たない（a=1 固定）。入出力はリニア RGB。
        var result = ColorPickerWindow.ShowDialog(Window.GetWindow(this), _ambientR, _ambientG, _ambientB, 1f);
        if (result is null) return;
        (_ambientR, _ambientG, _ambientB, _) = result.Value;
        UpdateAmbientSwatch();
        SendAmbient();
    }

    /// <summary>SldAmbientIntensity の ValueChanged から呼ばれるハンドラ（環境光強度）。</summary>
    private void OnAmbientSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls) return;
        SendAmbient();
    }

    /// <summary>環境光カラースウォッチの背景を現在のリニア色から更新する（リニア→sRGB 表示）。</summary>
    private void UpdateAmbientSwatch()
    {
        if (AmbientColorSwatch == null) return;
        AmbientColorSwatch.Background = new SolidColorBrush(Color.FromRgb(
            LinearToSrgbByte(_ambientR), LinearToSrgbByte(_ambientG), LinearToSrgbByte(_ambientB)));
    }

    /// <summary>リニア RGB 成分（0..1）を sRGB の 8bit 値へ変換する（スウォッチ表示用）。</summary>
    private static byte LinearToSrgbByte(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        // sRGB 伝達関数（IEC 61966-2-1）。ColorPickerWindow の LinearToSrgb と同一。
        double s = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(s * 255.0), 0, 255);
    }

    // ── デバッグカメラ 2D（正射投影）トグル ────────────────────────

    /// <summary>デバッグカメラが 2D（正射投影）モードなら true。</summary>
    private bool _editorCam2D = false;

    /// <summary>
    /// エディタのデバッグカメラの投影方式（2D＝正射 / 3D＝透視）を設定する。
    /// タブバー右端の「2D」ボタンとビューポート設定のチェックボックスの両方から
    /// 呼ばれる共通処理。視点は維持したまま、ランタイム側で 0.3 秒かけて補間される。
    /// </summary>
    private void SetEditorCam2D(bool on)
    {
        _editorCam2D = on;
        _runtimeManager?.SendToRuntime($"EDITOR_CAM_ORTHO:{(on ? "1" : "0")}");

        // UI を同期する（チェックボックス変更イベントの再帰送信は _updatingControls で抑制）
        _updatingControls = true;
        ChkEditorCamOrtho.IsChecked = on;
        _updatingControls = false;
        Update2DCamToggleVisual();
    }

    /// <summary>タブバー右端「2D」ボタンの見た目をトグル状態に合わせて更新する。</summary>
    private void Update2DCamToggleVisual()
    {
        if (Btn2DCamToggle == null) return;
        if (_editorCam2D)
        {
            // アクティブ: タブのアクセントと同じオレンジで強調する
            Btn2DCamToggle.Background  = new SolidColorBrush(Color.FromRgb(0xE8, 0x78, 0x20));
            Btn2DCamToggle.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x78, 0x20));
            Btn2DCamToggle.Foreground  = Brushes.White;
        }
        else
        {
            Btn2DCamToggle.Background  = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
            Btn2DCamToggle.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            Btn2DCamToggle.Foreground  = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        }
    }

    /// <summary>タブバー右端「2D」ボタン: 押すたびに 2D⇄3D をトグルする。</summary>
    private void On2DCamToggleClicked(object sender, RoutedEventArgs e)
        => SetEditorCam2D(!_editorCam2D);

    /// <summary>
    /// ビューポート設定の「2D（正射投影）」チェックボックス変更ハンドラ。
    /// </summary>
    private void OnEditorCamOrthoChanged(object sender, RoutedEventArgs e)
    {
        if (!_viewportSettingsInitialized || _updatingControls) return;
        SetEditorCam2D(ChkEditorCamOrtho.IsChecked == true);
    }

    // ── ギズモ座標系（World / Local）トグル ────────────────────────

    /// <summary>
    /// ギズモ（移動/回転/スケール）の座標系が Local（選択アクターのローカル軸）なら true。
    /// デフォルトは false（World＝ワールド軸整列、従来の挙動）。
    /// ギズモの真の状態はランタイム（Rust 側 App::gizmo_space）が保持するため、
    /// この変数はあくまで UI 表示・IPC 送信トリガー用のミラーである。
    /// </summary>
    private bool _gizmoLocalSpace = false;

    /// <summary>
    /// ギズモ座標系を設定し、ランタイムへ GIZMO_SPACE:WORLD / GIZMO_SPACE:LOCAL を送信する。
    /// タブバーの World/Local トグルボタンから呼ばれる。
    /// </summary>
    private void SetGizmoSpace(bool local)
    {
        _gizmoLocalSpace = local;
        _runtimeManager?.SendToRuntime(local ? "GIZMO_SPACE:LOCAL" : "GIZMO_SPACE:WORLD");
        UpdateGizmoSpaceToggleVisual();
    }

    /// <summary>タブバー「World/Local」ボタンの見た目・ラベルをトグル状態に合わせて更新する。</summary>
    private void UpdateGizmoSpaceToggleVisual()
    {
        if (BtnGizmoSpaceToggle == null) return;
        if (_gizmoLocalSpace)
        {
            // アクティブ（Local）: Btn2DCamToggle と同じオレンジで強調する
            BtnGizmoSpaceToggle.Content     = "Local";
            BtnGizmoSpaceToggle.Background  = new SolidColorBrush(Color.FromRgb(0xE8, 0x78, 0x20));
            BtnGizmoSpaceToggle.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x78, 0x20));
            BtnGizmoSpaceToggle.Foreground  = Brushes.White;
        }
        else
        {
            BtnGizmoSpaceToggle.Content     = "World";
            BtnGizmoSpaceToggle.Background  = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
            BtnGizmoSpaceToggle.BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            BtnGizmoSpaceToggle.Foreground  = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        }
    }

    /// <summary>タブバー「World/Local」ボタン: 押すたびに World⇄Local をトグルする。</summary>
    private void OnGizmoSpaceToggleClicked(object sender, RoutedEventArgs e)
        => SetGizmoSpace(!_gizmoLocalSpace);

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
        // ポストプロセス設定も既定値（Bloom/FXAA 無効・強度 0.6・透明描画は距離ソート）へリセットする
        _updatingControls = true;
        ChkBloom.IsChecked           = false;
        ChkFxaa.IsChecked            = false;
        SldBloomIntensity.Value      = DefaultBloomIntensity;
        if (CmbTransparency != null) CmbTransparency.SelectedIndex = 0;
        // メッシュレットカリング（第1弾）は既定 true（有効）へリセットする。
        if (ChkMeshletCull != null) ChkMeshletCull.IsChecked = true;
        // シーンビュー表示モードは既定「ライティングON」（index 0）へリセットする。
        if (CmbViewMode != null) CmbViewMode.SelectedIndex = 0;
        // レンダリング機能マトリクスを既定（現状維持）へリセットする。
        //   影=シャドウマップ / GI=DDGI（現状維持のため index 1）/ 反射=なし / AO=なし / 半透明=ラスタ。
        if (CmbShadow != null)       CmbShadow.SelectedIndex = 0;
        if (CmbGi != null)           CmbGi.SelectedIndex = 1;
        if (CmbReflection != null)   CmbReflection.SelectedIndex = 0;
        if (CmbAo != null)           CmbAo.SelectedIndex = 0;
        if (CmbTranslucency != null) CmbTranslucency.SelectedIndex = 0;
        if (SldGiIntensity != null)  SldGiIntensity.Value = DefaultGiIntensity;
        _updatingControls = false;
        if (_viewportSettingsInitialized)
        {
            _runtimeManager?.SendToRuntime("VIEWPORT_FOV:45");
            _runtimeManager?.SendToRuntime("VIEWPORT_FAR:1000");
            _runtimeManager?.SendToRuntime("CAM_SPEED:5");
            _runtimeManager?.SendToRuntime("CANVAS_SS_OVERLAY:0");
            SendPostFx();
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
        // ポストプロセス設定（Bloom/FXAA）を再同期する
        SendPostFx();
        // 編集時物理設定は Edit runtime にのみ送信する（Play runtime へ送ると物理スレッドが停止する）
        if (_runtimeManager?.State == EditorState.Edit)
        {
            // 現在のシーンタブ（ワールド/ビューポート）のビューモードを同期する。
            // 起動時・ランタイム再接続時・Play→Edit 復帰時のいずれもここを通るため、
            // ランタイムのビューモードが常にタブ UI と一致する。
            SendCurrentEditView();
            // Edit ランタイムのカメラ移動キー状態を強制リセットする。
            // Play 切替中に届かなかった CAM_KEY_UP でキーがスタックしていると
            // 「RMB 押下だけでカメラが移動」「軸スナップが即キャンセル」になるため、
            // Edit 復帰・再同期のたびにクリーンな状態から始める。
            _runtimeManager?.SendToRuntime("CAM_KEYS_CLEAR");
            // 編集時物理は 2D/3D 統合コマンド 1 本で再同期する（2D/3D 常に同値）。
            _runtimeManager?.SendToRuntime(
                $"SET_EDIT_PHYSICS_ALL:{(_editPhysicsEnabled ? 1 : 0)},{(_editPhysicsWithRigidbody ? 1 : 0)}");
            // ギズモ座標系（World/Local）もランタイム再接続時に再同期する
            // （ランタイム側は新規プロセスごとに GizmoSpace::World で初期化されるため）。
            _runtimeManager?.SendToRuntime(_gizmoLocalSpace ? "GIZMO_SPACE:LOCAL" : "GIZMO_SPACE:WORLD");
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
        // Edit/Pause 以外へ遷移するときは、押下中キーの CAM_KEY_UP を送ってから
        // クリアする（Pause 中に押したキーが Play 再開でスタックするのを防ぐ）。
        // bare Clear だと以降の実 KeyUp が _pressedVks に無いため UP が送信されない。
        if (state != EditorState.Edit && state != EditorState.Pause)
            ReleaseAllCamKeys();

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
