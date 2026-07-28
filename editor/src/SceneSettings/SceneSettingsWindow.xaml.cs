// ============================================================
//  SceneSettingsWindow.xaml.cs — シーン設定ウィンドウ
//
//  ビューポート左下の歯車ボタンから開く、シーン単位の設定ウィンドウ。
//  旧「ビューポートオプション」ポップアップを置き換えるもので、
//  プロジェクト設定ウィンドウ（ProjectSettingsWindow）と同じ
//  「左カテゴリツリー + 右設定パネル」構造・配色を踏襲する。
//
//  設計方針:
//   - 変更は即時反映（保存ボタンは持たない）。閉じるはタイトルバーの ×。
//   - このウィンドウはランタイムへ IPC を送らない。値を SceneSettingsData へ書き、
//     SettingChanged / ShadingAssetChanged コールバックで MainWindow へ通知するだけに留める。
//     理由: IPC 文字列の組み立てと送信タイミング（Edit/Play 判定・再同期）を MainWindow へ
//     一元化しておかないと、送信箇所が分散して整合が取れなくなるため。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SEEDEditor.Panels;

namespace SEEDEditor.SceneSettings;

/// <summary>
/// シーン設定ウィンドウ本体。
/// カテゴリ（デバッグカメラ / レンダリング / 物理）ごとに設定パネルを切り替えて表示する。
/// </summary>
public partial class SceneSettingsWindow : Window, ISceneSettingsPanelHost
{
    // ── P/Invoke ─────────────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    /// <summary>ダークタイトルバー属性 ID。</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    // ── カテゴリ定義 ─────────────────────────────────────────

    /// <summary>小項目（右パネルに表示する設定パネル 1 枚）の定義。</summary>
    private record SubItem(string Id, string Label);

    /// <summary>大項目の定義。SubItems リストで対応する小項目を管理する。</summary>
    private record Category(string Id, string Label, List<SubItem> SubItems);

    /// <summary>デバッグカメラ設定パネルの小項目 ID。</summary>
    private const string SubItemDebugCamera = "debug_camera";
    /// <summary>レンダリング設定パネルの小項目 ID。</summary>
    private const string SubItemRendering = "rendering";
    /// <summary>物理設定パネルの小項目 ID。</summary>
    private const string SubItemPhysics = "physics";

    /// <summary>
    /// カテゴリ定義テーブル。
    /// 「1 カテゴリ = 1 パネル」とし、パネル末尾の「デフォルトに戻す」が
    /// そのカテゴリだけを既定値へ戻す構成にしている。
    /// </summary>
    private static readonly List<Category> Categories = new()
    {
        new("cat_debug_camera", "デバッグカメラ", new()
        {
            new(SubItemDebugCamera, "ビュー設定"),
        }),
        new("cat_rendering", "レンダリング", new()
        {
            new(SubItemRendering, "描画設定"),
        }),
        new("cat_physics", "物理", new()
        {
            new(SubItemPhysics, "編集時物理"),
        }),
    };

    // ── ブラシ定数（ProjectSettingsWindow と同一配色）──────────

    private static readonly SolidColorBrush BrushSelected   = new(Color.FromRgb(0x09, 0x4D, 0x80));
    private static readonly SolidColorBrush BrushHover      = new(Color.FromRgb(0x30, 0x30, 0x32));
    private static readonly SolidColorBrush BrushCatHover   = new(Color.FromRgb(0x2E, 0x2E, 0x30));
    private static readonly SolidColorBrush BrushCategoryFg = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush BrushSubItemFg  = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly SolidColorBrush BrushTransp     = Brushes.Transparent;

    // ── 選択肢テーブル（Tag は IPC 送信値そのもの）────────────

    /// <summary>透明描画方式の選択肢。</summary>
    private static readonly (string Label, string Tag)[] TransparencyItems =
    {
        ("距離ソート", "sort"),
        ("WBOIT",     "wboit"),
    };

    /// <summary>影の方式の選択肢。</summary>
    private static readonly (string Label, string Tag)[] ShadowItems =
    {
        ("シャドウマップ", "shadowmap"),
        ("レイトレ",       "rt"),
    };

    /// <summary>GI の方式の選択肢。</summary>
    private static readonly (string Label, string Tag)[] GiItems =
    {
        ("なし（環境光）",   "flat"),
        ("SSGI",             "ssgi"),
        ("レイトレ（DDGI）", "rt"),
    };

    /// <summary>反射の方式の選択肢。</summary>
    private static readonly (string Label, string Tag)[] ReflectionItems =
    {
        ("なし",     "off"),
        ("SSR",      "ssr"),
        ("レイトレ", "rt"),
    };

    /// <summary>AO の方式の選択肢。</summary>
    private static readonly (string Label, string Tag)[] AoItems =
    {
        ("なし",     "off"),
        ("SSAO",     "ssao"),
        ("レイトレ", "rt"),
    };

    /// <summary>半透明の方式の選択肢。</summary>
    private static readonly (string Label, string Tag)[] TranslucencyItems =
    {
        ("ラスタ（従来）",             "raster"),
        ("レイトレ（色付き影＋屈折）", "rt"),
    };

    /// <summary>
    /// シーンビュー表示モードの選択肢。
    /// エディタのシーンビュー（デバッグカメラ）だけに効き、ゲームカメラのプレビュー小窓・
    /// Play の見た目には影響しない（ランタイム側でゲート）。
    /// 「G-Buffer: 〜」は Deferred の G-Buffer を生値のまま表示するデバッグ用で、
    /// ランタイムは Deferred を維持したままライティングパスだけを可視化パスへ差し替える。
    /// Tag 文字列はランタイムの view_mode.rs の GBUFFER_DEBUG_CHANNEL_TABLE と 1:1 で一致させること。
    /// </summary>
    private static readonly (string Label, string Tag)[] ViewModeItems =
    {
        ("ライティングON",           "lit"),
        ("ライティングOFF",          "unlit"),
        ("ワイヤーフレーム",         "wireframe"),
        ("G-Buffer: ベースカラー",   "gbuffer_base_color"),
        ("G-Buffer: オクルージョン", "gbuffer_occlusion"),
        ("G-Buffer: 法線",           "gbuffer_normal"),
        ("G-Buffer: ラフネス",       "gbuffer_roughness"),
        ("G-Buffer: メタリック",     "gbuffer_metallic"),
        ("G-Buffer: 透過",           "gbuffer_transmission"),
        ("G-Buffer: エミッシブ",     "gbuffer_emissive"),
        ("G-Buffer: 深度",           "gbuffer_depth"),
        ("G-Buffer: 速度",           "gbuffer_velocity"),
        ("G-Buffer: レンダータグ",   "gbuffer_render_tag"),
        ("G-Buffer: ユーザーデータ", "gbuffer_user_data"),
    };

    // ── スライダーの範囲定数（旧ビューポートオプションと同値）──

    private const double FovMin = 10,  FovMax = 120;
    private const double FarMin = 100, FarMax = 5000;
    private const double CamSpeedMin = 0.1, CamSpeedMax = 100;
    /// <summary>各種強度スライダー（ブルーム / GI / 反射 / AO）の範囲。</summary>
    private const double IntensityMin = 0, IntensityMax = 2;
    /// <summary>環境光強度スライダーの範囲。</summary>
    private const double AmbientIntensityMin = 0, AmbientIntensityMax = 1;

    /// <summary>シーン既定シェーディングアセットとして受け付ける拡張子。</summary>
    private static readonly string[] ShadingAssetExtensions = { ".wgsl" };

    // ── 状態フィールド ────────────────────────────────────────

    /// <summary>編集対象のシーン設定データ（MainWindow と共有する参照）。</summary>
    private SceneSettingsData _data;

    /// <summary>アセットディレクトリ（仮想パス変換とファイルダイアログの初期位置に使う）。</summary>
    private readonly string _assetsPath;

    /// <summary>シーン既定シェーディングアセットの仮想パス（未設定なら null）。</summary>
    private string? _shadingAssetPath;

    /// <summary>シーンビュー表示モード（セッション限り・非永続）。</summary>
    private string _viewMode;

    /// <summary>デバッグカメラの位置（X, Y, Z）。シーン設定ではなく CAM_TRANSFORM で扱う。</summary>
    private float[] _cameraPosition;

    /// <summary>デバッグカメラの回転（オイラー角 X, Y, Z）。</summary>
    private float[] _cameraEuler;

    /// <summary>現在展開中の大項目 ID セット（初期状態は全展開）。</summary>
    private readonly HashSet<string> _expandedCategories = new();

    /// <summary>現在選択中の小項目 ID。</summary>
    private string _selectedSubItemId = SubItemDebugCamera;

    /// <summary>現在ハイライト表示中の小項目 Border。</summary>
    private Border? _selectedBorder;

    /// <summary>現在表示中パネルの「表示を現在値へ戻す」処理のリスト。</summary>
    private readonly List<Action> _refreshActions = new();

    /// <summary>プログラムから表示を更新中なら true（この間はコールバックを発火しない）。</summary>
    private bool _suppressed;

    /// <summary>「屈折の逐次グラブ」チェックボックス（有効/無効を他の設定に応じて切り替える）。</summary>
    private CheckBox? _refractSeqGrabCheck;

    /// <summary>RigidBody サブオプションのコンテナ（親チェックが ON のときだけ表示する）。</summary>
    private FrameworkElement? _rigidbodyOptionPanel;

    // ── 変更通知コールバック ──────────────────────────────────

    /// <summary>設定が変更されたときに発火する（MainWindow が対応する IPC を送る）。</summary>
    public Action<SceneSettingsChangeKind>? SettingChanged;

    /// <summary>
    /// シーン既定シェーディングアセットが変更されたときに発火する。
    /// 引数は仮想パス（assets://…）。解除時は空文字列。
    /// この項目だけは .scene のトップレベル "shading_asset" に保存されるため、
    /// SET_SCENE_SETTINGS ではなく SET_SCENE_SHADING_ASSET で送る。
    /// </summary>
    public Action<string>? ShadingAssetChanged;

    // ── ISceneSettingsPanelHost 実装 ─────────────────────────

    /// <inheritdoc/>
    bool ISceneSettingsPanelHost.Suppressed => _suppressed;

    /// <inheritdoc/>
    void ISceneSettingsPanelHost.RegisterRefresh(Action refresh) => _refreshActions.Add(refresh);

    /// <inheritdoc/>
    Style? ISceneSettingsPanelHost.TextBoxStyle => Resources["SettingTextBox"] as Style;

    // ── 公開プロパティ ────────────────────────────────────────

    /// <summary>シーンビュー表示モード（"lit" / "unlit" / "wireframe" / "gbuffer_*"）。</summary>
    public string ViewMode => _viewMode;

    /// <summary>デバッグカメラ位置（X, Y, Z）。</summary>
    public float[] CameraPosition => _cameraPosition;

    /// <summary>デバッグカメラ回転（オイラー角 X, Y, Z）。</summary>
    public float[] CameraEuler => _cameraEuler;

    // ── コンストラクタ ────────────────────────────────────────

    /// <summary>
    /// シーン設定ウィンドウを生成する。
    /// </summary>
    /// <param name="data">編集対象のシーン設定（MainWindow と共有する参照をそのまま渡す）。</param>
    /// <param name="assetsPath">アセットディレクトリの絶対パス。</param>
    /// <param name="shadingAssetPath">シーン既定シェーディングアセットの仮想パス（未設定なら null）。</param>
    /// <param name="viewMode">現在のシーンビュー表示モード。</param>
    /// <param name="cameraPosition">デバッグカメラ位置（X, Y, Z）。</param>
    /// <param name="cameraEuler">デバッグカメラ回転（オイラー角 X, Y, Z）。</param>
    public SceneSettingsWindow(
        SceneSettingsData data, string assetsPath, string? shadingAssetPath,
        string viewMode, float[] cameraPosition, float[] cameraEuler)
    {
        InitializeComponent();
        _data             = data;
        _assetsPath       = assetsPath;
        _shadingAssetPath = shadingAssetPath;
        _viewMode         = viewMode;
        _cameraPosition   = (float[])cameraPosition.Clone();
        _cameraEuler      = (float[])cameraEuler.Clone();

        // カテゴリ数が少ないため既定で全展開する
        foreach (var category in Categories) _expandedCategories.Add(category.Id);
    }

    // ── ウィンドウ初期化 ─────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // ダークタイトルバーを適用する
        var helper = new WindowInteropHelper(this);
        int dark = 1;
        DwmSetWindowAttribute(helper.Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        BuildCategoryPanel();
        SelectSubItem(_selectedSubItemId);
    }

    // ── 外部（MainWindow）からの表示更新 ─────────────────────

    /// <summary>
    /// 編集対象データを差し替える（シーンのロード時に呼ぶ）。
    /// 表示中パネルを現在値で作り直す。
    /// </summary>
    public void SetData(SceneSettingsData data, string? shadingAssetPath, float[] cameraPosition, float[] cameraEuler)
    {
        _data             = data;
        _shadingAssetPath = shadingAssetPath;
        _cameraPosition   = (float[])cameraPosition.Clone();
        _cameraEuler      = (float[])cameraEuler.Clone();
        SelectSubItem(_selectedSubItemId);
    }

    /// <summary>
    /// ランタイムから受信したカメラ状態（CAM_STATE）を表示へ反映する。
    /// 表示更新のみで、コールバックは発火しない（送り返しのループを防ぐ）。
    /// </summary>
    public void UpdateCameraTransform(float[] position, float[] euler)
    {
        _cameraPosition = (float[])position.Clone();
        _cameraEuler    = (float[])euler.Clone();
        RefreshDisplay();
    }

    /// <summary>
    /// 表示中パネルの全コントロールを現在のデータ値へ同期する。
    /// 抑制フラグを立てて実行するため、変更コールバックは発火しない。
    /// </summary>
    public void RefreshDisplay()
    {
        _suppressed = true;
        try
        {
            foreach (var refresh in _refreshActions) refresh();
        }
        finally
        {
            _suppressed = false;
        }
    }

    // ── 左パネル: カテゴリツリー構築 ────────────────────────

    /// <summary>
    /// 左パネルのカテゴリツリーを再構築する。
    /// Categories をデータソースとして大項目ヘッダーと小項目行を動的生成する。
    /// </summary>
    private void BuildCategoryPanel()
    {
        CategoryPanel.Children.Clear();
        _selectedBorder = null;

        foreach (var category in Categories)
        {
            bool expanded = _expandedCategories.Contains(category.Id);
            CategoryPanel.Children.Add(BuildCategoryHeader(category.Id, category.Label, expanded));

            // 折りたたみ中は小項目を表示しない
            if (!expanded) continue;

            foreach (var sub in category.SubItems)
            {
                bool isSelected = sub.Id == _selectedSubItemId;
                var subBorder   = BuildSubItemRow(sub.Id, sub.Label, isSelected);
                CategoryPanel.Children.Add(subBorder);
                if (isSelected) _selectedBorder = subBorder;
            }
        }
    }

    /// <summary>大項目ヘッダー Border を生成する。クリックで展開/折りたたみを切り替える。</summary>
    private Border BuildCategoryHeader(string categoryId, string label, bool expanded)
    {
        var border = new Border
        {
            Padding    = new Thickness(12, 7, 8, 7),
            Cursor     = Cursors.Hand,
            Background = BrushTransp,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            // ▼: 展開中, ▶: 折りたたみ中
            Text              = expanded ? "▼" : "▶",
            Foreground        = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
            FontSize          = 8,
            Width             = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text              = label,
            Foreground        = BrushCategoryFg,
            FontSize          = 12,
            FontWeight        = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        border.Child = content;

        border.MouseEnter += (_, _) => border.Background = BrushCatHover;
        border.MouseLeave += (_, _) => border.Background = BrushTransp;
        border.MouseLeftButtonDown += (_, _) =>
        {
            if (!_expandedCategories.Remove(categoryId))
                _expandedCategories.Add(categoryId);
            BuildCategoryPanel();
        };

        return border;
    }

    /// <summary>小項目行 Border を生成する。クリックで対応する設定パネルを表示する。</summary>
    private Border BuildSubItemRow(string subItemId, string label, bool isSelected)
    {
        var border = new Border
        {
            Padding    = new Thickness(30, 5, 8, 5),
            Cursor     = Cursors.Hand,
            Background = isSelected ? BrushSelected : BrushTransp,
        };
        border.Child = new TextBlock
        {
            Text       = label,
            Foreground = BrushSubItemFg,
            FontSize   = 12,
        };

        border.MouseEnter += (_, _) =>
        {
            if (border != _selectedBorder) border.Background = BrushHover;
        };
        border.MouseLeave += (_, _) =>
        {
            if (border != _selectedBorder) border.Background = BrushTransp;
        };
        border.MouseLeftButtonDown += (_, _) => SelectSubItem(subItemId);

        return border;
    }

    // ── 右パネル: 設定コンテンツ切り替え ────────────────────

    /// <summary>
    /// 指定した小項目を選択状態にし、右パネルに対応する設定 UI を構築して表示する。
    /// 変更は即時反映のため、パネル切り替え時に値を収集する必要はない。
    /// </summary>
    private void SelectSubItem(string subItemId)
    {
        _selectedSubItemId = subItemId;

        // 前パネルの表示更新処理は破棄する（コントロールごと作り直すため）
        _refreshActions.Clear();
        _refractSeqGrabCheck  = null;
        _rigidbodyOptionPanel = null;

        BuildCategoryPanel();

        SettingsContent.Content = subItemId switch
        {
            SubItemRendering => BuildRenderingPanel(),
            SubItemPhysics   => BuildPhysicsPanel(),
            _                => BuildDebugCameraPanel(),
        };
    }

    // ── デバッグカメラ設定パネル ──────────────────────────────

    /// <summary>デバッグカメラ位置の既定値（X, Y, Z）。</summary>
    private static readonly float[] DefaultCameraPosition = { 0f, 2f, -10f };
    /// <summary>デバッグカメラ回転の既定値（オイラー角 X, Y, Z）。</summary>
    private static readonly float[] DefaultCameraEuler = { 0f, 0f, 0f };

    /// <summary>
    /// 「デバッグカメラ」カテゴリのパネルを構築する。
    /// 画角・描画距離・移動速度・投影方式・表示補助・カメラ Transform を扱う。
    /// </summary>
    private UIElement BuildDebugCameraPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(SceneSettingsControls.PanelHeader(
            "デバッグカメラ",
            "シーンビュー（Edit モードのデバッグカメラ）の設定です。\n" +
            "設定はシーンごとに .scene ファイルへ保存されます。"));

        panel.Children.Add(SceneSettingsControls.SliderRow(
            this, "画角（FOV）", FovMin, FovMax, integer: true,
            "デバッグカメラの垂直画角（度）。",
            () => _data.DebugCamera.Fov,
            v  => _data.DebugCamera.Fov = v,
            () => SettingChanged?.Invoke(SceneSettingsChangeKind.Fov)));

        panel.Children.Add(SceneSettingsControls.SliderRow(
            this, "描画距離（Far）", FarMin, FarMax, integer: true,
            "デバッグカメラの far クリップ距離。遠景が切れる場合は大きくします。",
            () => _data.DebugCamera.Far,
            v  => _data.DebugCamera.Far = v,
            () => SettingChanged?.Invoke(SceneSettingsChangeKind.Far)));

        panel.Children.Add(SceneSettingsControls.SliderRow(
            this, "カメラ速度", CamSpeedMin, CamSpeedMax, integer: false,
            "右ドラッグ + WASD でのデバッグカメラ移動速度。",
            () => _data.DebugCamera.Speed,
            v  => _data.DebugCamera.Speed = v,
            () => SettingChanged?.Invoke(SceneSettingsChangeKind.CameraSpeed)));

        panel.Children.Add(SceneSettingsControls.Check(
            this, "2D（正射投影）",
            "透視投影と正射投影を切り替えます。視点は維持したまま 0.3 秒かけて補間されます。\n" +
            "タブバー右端の「2D」ボタンと同じ設定です。",
            () => _data.DebugCamera.Ortho2d,
            v  => _data.DebugCamera.Ortho2d = v,
            () => SettingChanged?.Invoke(SceneSettingsChangeKind.Ortho2d)));

        panel.Children.Add(SceneSettingsControls.Check(
            this, "グリッド表示", "シーンビューの地面グリッドを表示します。",
            () => _data.DebugCamera.ShowGrid,
            v  => _data.DebugCamera.ShowGrid = v,
            () => SettingChanged?.Invoke(SceneSettingsChangeKind.ShowGrid)));

        panel.Children.Add(SceneSettingsControls.Check(
            this, "軸ガイド表示", "画面隅の XYZ 軸ギズモを表示します。",
            () => _data.DebugCamera.ShowAxisGizmo,
            v  => _data.DebugCamera.ShowAxisGizmo = v,
            () => SettingChanged?.Invoke(SceneSettingsChangeKind.ShowAxisGizmo)));

        // ── カメラ Transform（位置・回転）──
        panel.Children.Add(SceneSettingsControls.SectionHeader("カメラ Transform"));
        panel.Children.Add(SceneSettingsControls.HintText(
            "値はシーンビューのカメラ操作に追従して更新されます。入力して Enter で移動します。"));

        panel.Children.Add(SceneSettingsControls.Vector3Row(
            this, "位置", new[] { "X", "Y", "Z" }, rotationColor: false,
            () => _cameraPosition,
            v  => _cameraPosition = v,
            () => SettingChanged?.Invoke(SceneSettingsChangeKind.CameraTransform)));

        panel.Children.Add(SceneSettingsControls.Vector3Row(
            this, "回転", new[] { "X°", "Y°", "Z°" }, rotationColor: true,
            () => _cameraEuler,
            v  => _cameraEuler = v,
            () => SettingChanged?.Invoke(SceneSettingsChangeKind.CameraTransform)));

        // ── デフォルトに戻す（このカテゴリのみ）──
        panel.Children.Add(SceneSettingsControls.ResetButton(() =>
        {
            _data.DebugCamera.ResetToDefault();
            _cameraPosition = (float[])DefaultCameraPosition.Clone();
            _cameraEuler    = (float[])DefaultCameraEuler.Clone();
            RefreshDisplay();
            SettingChanged?.Invoke(SceneSettingsChangeKind.DebugCameraAll);
        }));

        return panel;
    }

    // ── レンダリング設定パネル ────────────────────────────────

    /// <summary>
    /// 「レンダリング」カテゴリのパネルを構築する。
    /// ポストプロセス・機能マトリクス・環境光・透明描画・シーンビュー表示モード・
    /// シーン既定シェーダーを扱う。
    /// </summary>
    private UIElement BuildRenderingPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(SceneSettingsControls.PanelHeader(
            "レンダリング",
            "このシーンの描画設定です（シーンごとに .scene ファイルへ保存されます）。\n" +
            "シーンビュー表示モードだけはデバッグ用途のため保存されません。"));

        // ── ポストプロセス ──
        panel.Children.Add(SceneSettingsControls.SectionHeader("ポストプロセス"));

        panel.Children.Add(SceneSettingsControls.Check(
            this, "ブルーム", "明るい部分を滲ませるブルームを有効にします。",
            () => _data.Rendering.Bloom,
            v  => _data.Rendering.Bloom = v,
            NotifyRendering));

        panel.Children.Add(SceneSettingsControls.SliderRow(
            this, "ブルーム強度", IntensityMin, IntensityMax, integer: false, null,
            () => _data.Rendering.BloomIntensity,
            v  => _data.Rendering.BloomIntensity = v,
            NotifyRendering));

        panel.Children.Add(SceneSettingsControls.Check(
            this, "FXAA（アンチエイリアス）", "ポストプロセスによる簡易アンチエイリアス。",
            () => _data.Rendering.Fxaa,
            v  => _data.Rendering.Fxaa = v,
            NotifyRendering));

        panel.Children.Add(SceneSettingsControls.Check(
            this, "Deferred レンダリング",
            "G-Buffer + フルスクリーン・ライティング方式。\n" +
            "OFF で従来のフォワード経路へ完全フォールバックします（A/B パリティ検証用）。",
            () => _data.Rendering.Deferred,
            v  => _data.Rendering.Deferred = v,
            NotifyRendering));

        // 屈折の逐次グラブ:「距離ソート」かつ「半透明=レイトレ」のときのみ意味を持つ。
        // それ以外の組み合わせでは UpdateRefractSeqGrabEnabled でグレーアウトする。
        _refractSeqGrabCheck = SceneSettingsControls.Check(
            this, "屈折の逐次グラブ（ガラス越しのガラスを映す・重い）",
            "半透明=レイトレ かつ 透明描画=距離ソート のときのみ有効。\n" +
            "ガラスを1つ描くたびに屈折背景を再生成するため重い処理です。",
            () => _data.Rendering.RefractSequentialGrab,
            v  => _data.Rendering.RefractSequentialGrab = v,
            NotifyRendering);
        panel.Children.Add(_refractSeqGrabCheck);

        panel.Children.Add(SceneSettingsControls.SliderRow(
            this, "GI 強度", IntensityMin, IntensityMax, integer: false,
            "間接光（GI）の強度倍率。GI の方式は下の「レンダリング機能」で選びます。",
            () => _data.Rendering.GiIntensity,
            v  => _data.Rendering.GiIntensity = v,
            NotifyRendering));

        panel.Children.Add(SceneSettingsControls.SliderRow(
            this, "反射強度", IntensityMin, IntensityMax, integer: false,
            "反射（SSR / RT）の強度倍率。反射の方式は下の「レンダリング機能」で選びます。",
            () => _data.Rendering.ReflectionIntensity,
            v  => _data.Rendering.ReflectionIntensity = v,
            NotifyRendering));

        panel.Children.Add(SceneSettingsControls.SliderRow(
            this, "AO 強度", IntensityMin, IntensityMax, integer: false,
            "アンビエントオクルージョンの強度倍率。AO の方式は下の「レンダリング機能」で選びます。",
            () => _data.Rendering.AoIntensity,
            v  => _data.Rendering.AoIntensity = v,
            NotifyRendering));

        // ── レンダリング機能マトリクス ──
        panel.Children.Add(SceneSettingsControls.SectionHeader("レンダリング機能"));
        panel.Children.Add(SceneSettingsControls.HintText(
            "影 / GI / 反射 / AO / 半透明をそれぞれ独立して切り替えます。\n" +
            "RT が使えない GPU ではランタイム側が自動的に代替方式へ降格します（[SEED FEATURES] ログ参照）。\n" +
            "「未実装」の方式を選んだ場合も従来動作へフォールバックします。"));

        panel.Children.Add(SceneSettingsControls.ComboRow(
            this, "影", ShadowItems, null,
            () => _data.Rendering.Features.Shadow,
            v  => _data.Rendering.Features.Shadow = v,
            NotifyRendering, out _));

        panel.Children.Add(SceneSettingsControls.ComboRow(
            this, "GI", GiItems, null,
            () => _data.Rendering.Features.Gi,
            v  => _data.Rendering.Features.Gi = v,
            NotifyRendering, out _));

        panel.Children.Add(SceneSettingsControls.ComboRow(
            this, "反射", ReflectionItems, null,
            () => _data.Rendering.Features.Reflection,
            v  => _data.Rendering.Features.Reflection = v,
            NotifyRendering, out _));

        panel.Children.Add(SceneSettingsControls.ComboRow(
            this, "AO", AoItems, null,
            () => _data.Rendering.Features.Ao,
            v  => _data.Rendering.Features.Ao = v,
            NotifyRendering, out _));

        // 半透明の方式は屈折の逐次グラブの有効条件に含まれるため、変更時に再評価する
        panel.Children.Add(SceneSettingsControls.ComboRow(
            this, "半透明", TranslucencyItems,
            "ラスタ内部の合成方式は下の「透明描画」で選びます。",
            () => _data.Rendering.Features.Translucency,
            v  => _data.Rendering.Features.Translucency = v,
            () => { NotifyRendering(); UpdateRefractSeqGrabEnabled(); }, out _));

        // ── 環境光 ──
        panel.Children.Add(SceneSettingsControls.SectionHeader("環境光"));
        panel.Children.Add(SceneSettingsControls.HintText(
            "全ライトの強度が 0 でも見える底上げ光です。強度 0 で完全な暗闇になります。"));

        panel.Children.Add(SceneSettingsControls.ColorRow(
            this, this, "色", "クリックでカラーピッカーを開きます（リニア RGB）。",
            () => _data.Rendering.AmbientColor,
            v  => _data.Rendering.AmbientColor = v,
            () => SettingChanged?.Invoke(SceneSettingsChangeKind.Ambient)));

        panel.Children.Add(SceneSettingsControls.SliderRow(
            this, "強度", AmbientIntensityMin, AmbientIntensityMax, integer: false, null,
            () => _data.Rendering.AmbientIntensity,
            v  => _data.Rendering.AmbientIntensity = v,
            () => SettingChanged?.Invoke(SceneSettingsChangeKind.Ambient)));

        // ── 透明描画 ──
        panel.Children.Add(SceneSettingsControls.SectionHeader("透明描画"));
        panel.Children.Add(SceneSettingsControls.HintText(
            "距離ソート: 半透明オブジェクトをカメラからの距離順に描画します（既定）。\n" +
            "WBOIT: Weighted Blended Order-Independent Transparency（描画順に依存しない合成）。"));

        panel.Children.Add(SceneSettingsControls.ComboRow(
            this, "方式", TransparencyItems, null,
            () => _data.Rendering.Transparency,
            v  => _data.Rendering.Transparency = v,
            () => { NotifyRendering(); UpdateRefractSeqGrabEnabled(); }, out _));

        // ── シーンビュー表示モード（セッション限り・非永続）──
        panel.Children.Add(SceneSettingsControls.SectionHeader("シーンビュー表示モード"));
        panel.Children.Add(SceneSettingsControls.HintText(
            "シーンビュー（デバッグカメラ）の表示モードを切り替えます。\n" +
            "ゲームカメラのプレビュー小窓・Play の見た目には影響しません。\n" +
            "「G-Buffer: 〜」は Deferred の G-Buffer を生値のまま表示するデバッグ用です。\n" +
            "この設定はシーンには保存されません（毎回「ライティングON」で開始します）。"));

        panel.Children.Add(SceneSettingsControls.ComboRow(
            this, "表示モード", ViewModeItems, null,
            () => _viewMode,
            v  => _viewMode = v,
            () => SettingChanged?.Invoke(SceneSettingsChangeKind.ViewMode), out _));

        // ── シーンのシェーダー（.scene のトップレベル shading_asset）──
        panel.Children.Add(SceneSettingsControls.SectionHeader("シーンのシェーダー"));
        panel.Children.Add(SceneSettingsControls.HintText(
            "このシーンの既定シェーディングアセット（WGSL）です。\n" +
            "カメラ側の Shading が未設定のときのフォールバック先になります。\n" +
            "未設定の場合は組み込み標準 PBR を使用します。"));
        panel.Children.Add(BuildShadingAssetRow());

        // ── デフォルトに戻す（このカテゴリのみ）──
        panel.Children.Add(SceneSettingsControls.ResetButton(() =>
        {
            _data.Rendering.ResetToDefault();
            // 表示モードもこのカテゴリの項目なので既定（ライティングON）へ戻す。
            // シーンのシェーダーはシーンのアセット参照であり「設定の既定値」ではないため、
            // 誤って参照を失わないようリセット対象に含めない（× ボタンで明示的に解除する）。
            _viewMode = ViewModeItems[0].Tag;
            RefreshDisplay();
            UpdateRefractSeqGrabEnabled();
            SettingChanged?.Invoke(SceneSettingsChangeKind.RenderingAll);
        }));

        // 復元した透明描画 / 半透明の組み合わせで屈折の逐次グラブの可否を確定する
        UpdateRefractSeqGrabEnabled();

        return panel;
    }

    /// <summary>レンダリング設定（SET_POST_FX 対象）の変更を通知する。</summary>
    private void NotifyRendering() => SettingChanged?.Invoke(SceneSettingsChangeKind.Rendering);

    /// <summary>
    /// 「屈折の逐次グラブ」チェックボックスの有効/無効を、透明描画方式と半透明モードの
    /// 組み合わせから確定する。このオプションは「距離ソート」かつ「半透明=レイトレ」の
    /// ときにのみ意味を持つ（ガラスを 1 つ描くたびに屈折背景を再取得し、ガラス越しの
    /// 別ガラスを映すため）。それ以外の組み合わせではグレーアウトして操作を禁止する。
    /// </summary>
    private void UpdateRefractSeqGrabEnabled()
    {
        if (_refractSeqGrabCheck == null) return;
        _refractSeqGrabCheck.IsEnabled =
            _data.Rendering.Transparency == "sort" && _data.Rendering.Features.Translucency == "rt";
    }

    /// <summary>
    /// シーン既定シェーディングアセットのファイル参照行を構築する。
    /// ProjectPanel からの .wgsl ドラッグ＆ドロップ・参照ボタン・クリアボタンに対応する
    /// （InspectorPanel のカメラ Shading 欄と同じ FileRefBuilder の流儀）。
    /// </summary>
    private UIElement BuildShadingAssetRow()
    {
        return FileRefBuilder.Build(
            "シェーダ", _shadingAssetPath,
            ShadingAssetExtensions,
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title            = "シーン既定のシェーディングアセット（WGSL）を選択",
                    Filter           = "WGSL シェーダ|*.wgsl|すべてのファイル|*.*",
                    InitialDirectory = Directory.Exists(_assetsPath) ? _assetsPath : null,
                };
                return dlg.ShowDialog(this) == true ? dlg.FileName : null;
            },
            path =>
            {
                // 絶対パスを仮想パスへ変換してから通知する（ランタイムの asset_fs 規約）
                _shadingAssetPath = VirtualPath.ToVirtual(path, _assetsPath);
                ShadingAssetChanged?.Invoke(_shadingAssetPath);
                SelectSubItem(_selectedSubItemId);   // 表示パスを更新するためパネルを作り直す
            },
            () =>
            {
                // 空文字列を送るとランタイム側で未設定（None）へ戻る
                _shadingAssetPath = null;
                ShadingAssetChanged?.Invoke(string.Empty);
                SelectSubItem(_selectedSubItemId);
            });
    }

    // ── 物理設定パネル ────────────────────────────────────────

    /// <summary>
    /// 「物理」カテゴリのパネルを構築する。
    /// 編集時物理と、その配下の RigidBody サブオプションを扱う。
    /// </summary>
    private UIElement BuildPhysicsPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(SceneSettingsControls.PanelHeader(
            "編集時物理",
            "Edit モード中に物理シミュレーションを走らせる設定です（2D/3D 共通）。\n" +
            "設定はシーンごとに .scene ファイルへ保存されます。"));

        panel.Children.Add(SceneSettingsControls.Check(
            this, "編集時の物理シミュレーション（2D/3D）",
            "Edit モードでもコライダーの押し戻しを働かせます。",
            () => _data.Physics.EditPhysics,
            v  => _data.Physics.EditPhysics = v,
            () =>
            {
                // 親を OFF にしたら子（RigidBody）も必ず OFF にする
                if (!_data.Physics.EditPhysics) _data.Physics.EditPhysicsRigidbody = false;
                UpdateRigidbodyOptionVisibility();
                RefreshDisplay();
                SettingChanged?.Invoke(SceneSettingsChangeKind.Physics);
            }));

        // RigidBody サブオプション（親が ON のときだけ表示する）
        var rigidbodyPanel = new StackPanel();
        rigidbodyPanel.Children.Add(SceneSettingsControls.Check(
            this, "RigidBody（重力・ダイナミクスを有効にする）",
            "有効にすると編集時物理がタイムラインモードになり、\n" +
            "ビューポート下部に再生バーが表示されます。",
            () => _data.Physics.EditPhysicsRigidbody,
            v  => _data.Physics.EditPhysicsRigidbody = v,
            () => SettingChanged?.Invoke(SceneSettingsChangeKind.Physics),
            SceneSettingsControls.SubOptionIndent));
        _rigidbodyOptionPanel = rigidbodyPanel;
        panel.Children.Add(rigidbodyPanel);
        UpdateRigidbodyOptionVisibility();

        // 表示更新時にもサブオプションの表示状態を追従させる
        ((ISceneSettingsPanelHost)this).RegisterRefresh(UpdateRigidbodyOptionVisibility);

        panel.Children.Add(SceneSettingsControls.ResetButton(() =>
        {
            _data.Physics.ResetToDefault();
            RefreshDisplay();
            SettingChanged?.Invoke(SceneSettingsChangeKind.PhysicsAll);
        }));

        return panel;
    }

    /// <summary>RigidBody サブオプションの表示/非表示を親チェックの状態から更新する。</summary>
    private void UpdateRigidbodyOptionVisibility()
    {
        if (_rigidbodyOptionPanel == null) return;
        _rigidbodyOptionPanel.Visibility = _data.Physics.EditPhysics
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
