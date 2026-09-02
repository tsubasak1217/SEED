using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SEEDEditor.Placement.Patterns;
using SEEDEditor.Runtime;

namespace SEEDEditor.Placement;

/// <summary>
/// ロジック配置ダイアログ。
///
/// <para>
/// 円形・グリッド・直線・ランダムのパターンを指定し、
/// 「追加」でランタイムへ <c>LOGIC_PLACE</c> を 1 発送る。
/// 実際の点列生成・地形接地・アクタ生成はすべてランタイムが行い、
/// 本ダイアログは<b>パラメータ編集と俯瞰プレビューだけ</b>を担う。
/// </para>
///
/// <para>
/// プレビューは <see cref="PlacementGenerator"/>（Rust 実装の写し）で描く。
/// IPC 往復を挟むとパラメータ操作の即時性が出せないための二重実装であり、
/// 一致は双方のユニットテストが既知ベクタで固定している。
/// </para>
///
/// <para>
/// 入力欄はパターンごとに表示を切り替える（無関係なパラメータは出さない）。
/// パラメータの前回値は <see cref="EditorPreferences"/> に記憶し、
/// 次に開いたときの初期値にする。
/// </para>
/// </summary>
public partial class LogicPlacementWindow : Window
{
    // ── プレビュー描画の定数（マジックナンバー禁止）─────────────

    /// <summary>プレビューの点マーカーの直径 [px]。</summary>
    private const double PreviewDotSize = 6.0;

    /// <summary>プレビューの内側余白 [px]（点が枠に張り付かないようにする）。</summary>
    private const double PreviewPadding = 18.0;

    /// <summary>プレビューで向きを示す線分の長さ [px]。</summary>
    private const double PreviewYawLineLength = 10.0;

    /// <summary>点が 1 個・または全点が同一位置のときに使う既定のワールド幅 [m]。</summary>
    private const double PreviewFallbackExtent = 1.0;

    /// <summary>基準点（原点）マーカーの十字の腕の長さ [px]。</summary>
    private const double PreviewOriginCrossArm = 6.0;

    /// <summary>プレビューに描画する点数の上限（描画コストの頭打ち）。</summary>
    private const int PreviewMaxDots = 1024;

    // ── 色 ───────────────────────────────────────────────────

    /// <summary>点マーカーの色。</summary>
    private static readonly Brush DotBrush = new SolidColorBrush(Color.FromRgb(0x6C, 0xB6, 0xFF));
    /// <summary>向き線の色。</summary>
    private static readonly Brush YawBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
    /// <summary>基準点マーカーの色。</summary>
    private static readonly Brush OriginBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>呼び出し元の文脈（2D/3D・親アクタ・制御点モード）。</summary>
    private readonly LogicPlacementContext _ctx;

    /// <summary>コマンド送信先。null なら送信せずに閉じるだけ（テスト・未接続時）。</summary>
    private readonly RuntimeManager? _runtime;

    /// <summary>編集中のパターン指定。入力欄の変更で随時更新される。</summary>
    private PlacementSpec _spec;

    /// <summary>UI の初期化中は入力イベントでプレビューを更新しないためのガード。</summary>
    private bool _loading = true;

    /// <summary>配置元アクタファイル（assets 相対の仮想パス）。空アクタなら null。</summary>
    private string? _sourcePath;

    /// <summary>直近のプレビュー生成結果（「追加」時の件数表示に使う）。</summary>
    private PlacementResult _preview = new();

    /// <summary>パターン選択コンボの項目（表示名と値の対応）。</summary>
    private static readonly (PlacementPattern Value, string Label)[] PatternChoices =
    {
        (PlacementPattern.Circle, "円形／円弧"),
        (PlacementPattern.Grid,   "グリッド"),
        (PlacementPattern.Line,   "直線"),
        (PlacementPattern.Random, "ランダム散布"),
    };

    /// <summary>
    /// ダイアログを構築する。
    /// </summary>
    /// <param name="ctx">呼び出し元の文脈。</param>
    /// <param name="runtime">コマンド送信先（null 可）。</param>
    public LogicPlacementWindow(LogicPlacementContext ctx, RuntimeManager? runtime)
    {
        InitializeComponent();
        _ctx     = ctx;
        _runtime = runtime;
        // 前回値を初期値にする（無ければ既定値）。複製して持つので、
        // キャンセルしても保存済みの値は壊れない。
        _spec = EditorPreferences.Instance.LogicPlacement?.Clone() ?? new PlacementSpec();

        Title = ctx.IsControlPointMode
            ? "ロジック配置 — 制御点を追加"
            : ctx.Is2D ? "ロジック配置（2D アクタ）" : "ロジック配置（3D アクタ）";

        foreach (var (_, label) in PatternChoices) CmbPattern.Items.Add(label);

        ApplyContextVisibility();
        LoadSpecIntoUi();
        HookInputEvents();
        _loading = false;
        UpdatePreview();
    }

    // ════════════════════════════════════════════════════════════
    //  UI 構成
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 呼び出し元の文脈に応じて、意味を持たない入力欄を隠す。
    ///
    /// - 制御点モード: 「配置元」「地形接地」は無関係なので非表示
    ///   （制御点はアクタ相対の座標データであり、実体も地形も持たない）。
    ///   「基準点」は**残す**。制御点もアクタ配置と同じくビューポートの
    ///   カーソル位置を基準に置くようになったため、説明が必要になる。
    /// - 2D: 段（Y 方向）と地形接地は存在しないので非表示。
    /// </summary>
    private void ApplyContextVisibility()
    {
        bool cp = _ctx.IsControlPointMode;
        SourceGroup.Visibility = cp ? Visibility.Collapsed : Visibility.Visible;
        BaseGroup.Visibility   = Visibility.Visible;
        if (cp)
        {
            TxtBaseHint.Text = "「配置」を押すとビューポートが配置モードになります。"
                             + "カーソル位置（メッシュ・地形の表面。何も無ければカメラ前方）を"
                             + "対象アクタのローカル座標へ変換した点を基準に、"
                             + "点列が末尾へ追加されます。左クリックで確定・右クリック / Esc で取消します。";
        }

        // 地形接地は「3D の実アクタ配置」のときだけ意味を持つ。
        ChkGround.Visibility = (!cp && !_ctx.Is2D) ? Visibility.Visible : Visibility.Collapsed;

        // 2D は段（Y 方向）を持たない。
        var layerVis = _ctx.Is2D ? Visibility.Collapsed : Visibility.Visible;
        LblGridLayers.Visibility   = layerVis;
        TxtGridLayers.Visibility   = layerVis;
        LblGridSpacingY.Visibility = layerVis;
        TxtGridSpacingY.Visibility = layerVis;

        // 2D では平面の第 2 軸はキャンバスの Y なので、ラベルを合わせる。
        if (_ctx.Is2D)
        {
            LblGridSpacingZ.Text = "間隔Y";
            LblAreaSizeZ.Text    = "幅Y";
            LblAnchorY.Text      = "アンカーY";
            TxtAnchorHint.Text   = "アンカーは 0〜1。(0,0) がパターンの左上、(1,1) が右下、"
                                 + "(0.5,0.5) が中心をカーソル位置（基準点）に合わせます。";
            TxtBaseHint.Text     = "「配置」を押すとビューポートが配置モードになります。"
                                 + "カーソルのキャンバス位置にプレビューが追従し、"
                                 + "左クリックで確定・右クリック / Esc で取消します。";
        }
    }

    /// <summary>パターン種別に応じて、該当セクションだけを表示する。</summary>
    private void ApplyPatternVisibility()
    {
        Visibility Vis(PlacementPattern p) => _spec.Pattern == p ? Visibility.Visible : Visibility.Collapsed;
        CircleGroup.Visibility = Vis(PlacementPattern.Circle);
        GridGroup.Visibility   = Vis(PlacementPattern.Grid);
        LineGroup.Visibility   = Vis(PlacementPattern.Line);
        RandomGroup.Visibility = Vis(PlacementPattern.Random);
        ApplyAreaVisibility();
    }

    /// <summary>ランダム散布の範囲形状（円／矩形）に応じて入力欄を切り替える。</summary>
    private void ApplyAreaVisibility()
    {
        bool circle = RadioAreaCircle.IsChecked == true;
        AreaCircleRow.Visibility = circle ? Visibility.Visible : Visibility.Collapsed;
        AreaRectRow.Visibility   = circle ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>配置元の選択に応じてファイル参照行の有効・無効を切り替える。</summary>
    private void ApplySourceVisibility()
    {
        bool useFile = RadioSourceFile.IsChecked == true;
        SourcePathRow.IsEnabled = useFile;
        SourcePathRow.Opacity   = useFile ? 1.0 : 0.5;
    }

    /// <summary>_spec の内容を入力欄へ流し込む（ダイアログを開いた直後に 1 回）。</summary>
    private void LoadSpecIntoUi()
    {
        int patternIdx = Array.FindIndex(PatternChoices, c => c.Value == _spec.Pattern);
        CmbPattern.SelectedIndex = patternIdx >= 0 ? patternIdx : 0;

        TxtCircleCount.Text  = Num(_spec.Count);
        TxtCircleRadius.Text = Num(_spec.Radius);
        TxtCircleStart.Text  = Num(_spec.StartAngle);
        TxtCircleSpan.Text   = Num(_spec.AngleSpan);
        ChkFaceCenter.IsChecked = _spec.FaceCenter;

        TxtGridRows.Text     = Num(_spec.Rows);
        TxtGridCols.Text     = Num(_spec.Cols);
        TxtGridLayers.Text   = Num(_spec.Layers);
        TxtGridSpacingX.Text = Num(_spec.SpacingX);
        TxtGridSpacingZ.Text = Num(_spec.SpacingZ);
        TxtGridSpacingY.Text = Num(_spec.SpacingY);
        TxtAnchorX.Text = Num(PlacementSpec.ClampAnchor(_spec.AnchorX));
        TxtAnchorY.Text = Num(PlacementSpec.ClampAnchor(_spec.AnchorY));
        ChkGridChecker.IsChecked = _spec.CheckerOffset;

        TxtLineCount.Text   = Num(_spec.Count);
        TxtLineAngle.Text   = Num(_spec.LineAngle);
        TxtLineSpacing.Text = Num(_spec.LineSpacing);
        TxtLineAnchor.Text  = Num(PlacementSpec.ClampAnchor(_spec.AnchorX));

        RadioAreaCircle.IsChecked = _spec.AreaCircle;
        RadioAreaRect.IsChecked   = !_spec.AreaCircle;
        TxtAreaRadius.Text        = Num(_spec.AreaRadius);
        TxtAreaSizeX.Text         = Num(_spec.AreaSizeX);
        TxtAreaSizeZ.Text         = Num(_spec.AreaSizeZ);
        TxtRandomCount.Text       = Num(_spec.Count);
        TxtRandomMinSpacing.Text  = Num(_spec.MinSpacing);
        TxtScaleVariance.Text     = Num(_spec.ScaleVariance);
        ChkRandomRotation.IsChecked = _spec.RandomRotation;

        TxtJitterPos.Text = Num(_spec.JitterPos);
        TxtJitterRot.Text = Num(_spec.JitterRot);
        TxtSeed.Text      = _spec.Seed.ToString(CultureInfo.InvariantCulture);
        ChkFaceForward.IsChecked = _spec.FaceForward;
        ChkGround.IsChecked      = EditorPreferences.Instance.LogicPlacementGround;

        ApplyPatternVisibility();
        ApplySourceVisibility();
    }

    /// <summary>数値を入力欄用の文字列にする（不変カルチャ・不要な小数を出さない）。</summary>
    private static string Num(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>整数を入力欄用の文字列にする。</summary>
    private static string Num(uint v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// すべての入力欄に「変更されたら再生成」を結線する。
    ///
    /// XAML 側で個別に結線するとハンドラ名が 20 個以上になり、
    /// 追加漏れが静かなバグ（プレビューが更新されない）になるため、
    /// ここで一括して繋ぐ。
    /// </summary>
    private void HookInputEvents()
    {
        var boxes = new[]
        {
            TxtCircleCount, TxtCircleRadius, TxtCircleStart, TxtCircleSpan,
            TxtGridRows, TxtGridCols, TxtGridLayers,
            TxtGridSpacingX, TxtGridSpacingZ, TxtGridSpacingY,
            TxtAnchorX, TxtAnchorY,
            TxtLineCount, TxtLineAngle, TxtLineSpacing, TxtLineAnchor,
            TxtAreaRadius, TxtAreaSizeX, TxtAreaSizeZ,
            TxtRandomCount, TxtRandomMinSpacing, TxtScaleVariance,
            TxtJitterPos, TxtJitterRot, TxtSeed,
        };
        foreach (var b in boxes) b.TextChanged += (_, _) => UpdatePreview();

        var checks = new[]
        {
            ChkFaceCenter, ChkGridChecker,
            ChkRandomRotation, ChkFaceForward, ChkGround,
        };
        foreach (var c in checks)
        {
            c.Checked   += (_, _) => UpdatePreview();
            c.Unchecked += (_, _) => UpdatePreview();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  入力の読み取り
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 入力欄を float として読む。空・不正な入力は既定値を返す
    /// （入力途中で例外を出したりプレビューを消したりしないため）。
    /// </summary>
    private static float ReadFloat(TextBox box, float fallback)
        => float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>入力欄を uint として読む（負値・不正は既定値）。</summary>
    private static uint ReadUInt(TextBox box, uint fallback)
        => uint.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>入力欄を ulong（シード）として読む。</summary>
    private static ulong ReadUInt64(TextBox box, ulong fallback)
        => ulong.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>
    /// 現在の入力欄の内容を <see cref="_spec"/> へ取り込む。
    ///
    /// 「個数」はパターンごとに別の入力欄を持つ（円・直線・ランダム）ので、
    /// 表示中のパターンの欄だけを読む（隠れている欄の値で上書きしない）。
    /// 同様に「中心揃え」もグリッドと直線で別チェックボックスを共有する。
    /// </summary>
    private void CollectSpecFromUi()
    {
        int idx = CmbPattern.SelectedIndex;
        _spec.Pattern = idx >= 0 && idx < PatternChoices.Length ? PatternChoices[idx].Value : PlacementPattern.Circle;

        _spec.Radius     = ReadFloat(TxtCircleRadius, _spec.Radius);
        _spec.StartAngle = ReadFloat(TxtCircleStart,  _spec.StartAngle);
        _spec.AngleSpan  = ReadFloat(TxtCircleSpan,   _spec.AngleSpan);
        _spec.FaceCenter = ChkFaceCenter.IsChecked == true;

        _spec.Rows      = ReadUInt(TxtGridRows,   _spec.Rows);
        _spec.Cols      = ReadUInt(TxtGridCols,   _spec.Cols);
        // 2D は段を持たないので常に 1 段。
        _spec.Layers    = _ctx.Is2D ? 1u : ReadUInt(TxtGridLayers, _spec.Layers);
        _spec.SpacingX  = ReadFloat(TxtGridSpacingX, _spec.SpacingX);
        _spec.SpacingZ  = ReadFloat(TxtGridSpacingZ, _spec.SpacingZ);
        _spec.SpacingY  = ReadFloat(TxtGridSpacingY, _spec.SpacingY);
        _spec.CheckerOffset = ChkGridChecker.IsChecked == true;

        _spec.LineAngle   = ReadFloat(TxtLineAngle,   _spec.LineAngle);
        _spec.LineSpacing = ReadFloat(TxtLineSpacing, _spec.LineSpacing);

        _spec.AreaCircle     = RadioAreaCircle.IsChecked == true;
        _spec.AreaRadius     = ReadFloat(TxtAreaRadius, _spec.AreaRadius);
        _spec.AreaSizeX      = ReadFloat(TxtAreaSizeX,  _spec.AreaSizeX);
        _spec.AreaSizeZ      = ReadFloat(TxtAreaSizeZ,  _spec.AreaSizeZ);
        _spec.MinSpacing     = ReadFloat(TxtRandomMinSpacing, _spec.MinSpacing);
        _spec.ScaleVariance  = ReadFloat(TxtScaleVariance,    _spec.ScaleVariance);
        _spec.RandomRotation = ChkRandomRotation.IsChecked == true;

        // パターンごとに持つ「個数」「中心揃え」は表示中の欄から読む。
        _spec.Count = _spec.Pattern switch
        {
            PlacementPattern.Line   => ReadUInt(TxtLineCount,   _spec.Count),
            PlacementPattern.Random => ReadUInt(TxtRandomCount, _spec.Count),
            _                       => ReadUInt(TxtCircleCount, _spec.Count),
        };
        // 基準位置アンカーもパターンごとに別の入力欄を持つ（グリッドは XY 2 軸、
        // 直線は線に沿った 1 軸）。表示中のパターンの欄だけを読む。
        if (_spec.Pattern == PlacementPattern.Line)
        {
            _spec.AnchorX = PlacementSpec.ClampAnchor(ReadFloat(TxtLineAnchor, _spec.AnchorX));
        }
        else
        {
            _spec.AnchorX = PlacementSpec.ClampAnchor(ReadFloat(TxtAnchorX, _spec.AnchorX));
            _spec.AnchorY = PlacementSpec.ClampAnchor(ReadFloat(TxtAnchorY, _spec.AnchorY));
        }

        _spec.JitterPos   = ReadFloat(TxtJitterPos, _spec.JitterPos);
        _spec.JitterRot   = ReadFloat(TxtJitterRot, _spec.JitterRot);
        _spec.Seed        = ReadUInt64(TxtSeed, _spec.Seed);
        _spec.FaceForward = ChkFaceForward.IsChecked == true;
    }

    // ════════════════════════════════════════════════════════════
    //  プレビュー
    // ════════════════════════════════════════════════════════════

    /// <summary>入力欄を読み直し、俯瞰プレビューと件数表示を更新する。</summary>
    private void UpdatePreview()
    {
        if (_loading) return;
        CollectSpecFromUi();
        _preview = PlacementGenerator.Generate(_spec);
        DrawPreview(_preview);
        UpdateSummary(_preview);
    }

    /// <summary>件数・警告の表示を更新し、「追加」ボタンの可否を決める。</summary>
    private void UpdateSummary(PlacementResult result)
    {
        int n = result.Points.Count;
        TxtSummary.Text = $"生成される数: {n} 個";
        BtnAdd.IsEnabled = n > 0;

        var warnings = new List<string>();
        if (result.Warning is not null) warnings.Add(result.Warning);

        // 制御点モードでは上限（残り容量）を事前に知らせる。
        if (_ctx.IsControlPointMode && _ctx.RemainingControlPointCapacity >= 0
            && n > _ctx.RemainingControlPointCapacity)
        {
            warnings.Add($"制御点の残り容量は {_ctx.RemainingControlPointCapacity} 点です。"
                       + $"超過する {n - _ctx.RemainingControlPointCapacity} 点は追加されません。");
        }

        TxtWarning.Text = string.Join("\n", warnings);
        TxtWarning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 生成点を真上から見た図として描く。
    ///
    /// 画面の右が +X、画面の上が +Z（＝地図と同じ見え方）。
    /// 全点を含む正方形の範囲を求め、縦横比を崩さずに枠へ収める。
    /// </summary>
    private void DrawPreview(PlacementResult result)
    {
        PreviewCanvas.Children.Clear();
        double w = PreviewCanvas.ActualWidth;
        double h = PreviewCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || result.Points.Count == 0) return;

        // ── 範囲を求める（原点も必ず含めて、基準点との関係が分かるようにする）──
        double minX = 0, maxX = 0, minZ = 0, maxZ = 0;
        foreach (var p in result.Points)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
        }
        double extentX = Math.Max(maxX - minX, PreviewFallbackExtent);
        double extentZ = Math.Max(maxZ - minZ, PreviewFallbackExtent);
        // 縦横比を保つため、大きいほうの寸法で一律に縮尺を決める。
        double scale = Math.Min((w - PreviewPadding * 2) / extentX, (h - PreviewPadding * 2) / extentZ);
        double cx = (minX + maxX) * 0.5;
        double cz = (minZ + maxZ) * 0.5;

        // ワールド (x, z) → キャンバス (px, py)。Z は上向きにするため符号を反転する。
        Point ToScreen(double x, double z) => new(
            w * 0.5 + (x - cx) * scale,
            h * 0.5 - (z - cz) * scale);

        // ── 基準点（原点）マーカー ──
        var origin = ToScreen(0, 0);
        AddLine(origin.X - PreviewOriginCrossArm, origin.Y, origin.X + PreviewOriginCrossArm, origin.Y, OriginBrush, 1);
        AddLine(origin.X, origin.Y - PreviewOriginCrossArm, origin.X, origin.Y + PreviewOriginCrossArm, OriginBrush, 1);

        // ── 点マーカー（上限まで）──
        int drawn = 0;
        foreach (var p in result.Points)
        {
            if (drawn++ >= PreviewMaxDots) break;
            var s = ToScreen(p.X, p.Z);

            // 向きが設定されている点は短い線分で示す（ヨー 0 = +Z = 画面上）。
            if (p.Yaw != 0f)
            {
                double rad = p.Yaw * Math.PI / 180.0;
                double dx = Math.Sin(rad);
                double dz = Math.Cos(rad);
                AddLine(s.X, s.Y, s.X + dx * PreviewYawLineLength, s.Y - dz * PreviewYawLineLength, YawBrush, 1);
            }

            var dot = new Ellipse
            {
                Width  = PreviewDotSize,
                Height = PreviewDotSize,
                Fill   = DotBrush,
            };
            Canvas.SetLeft(dot, s.X - PreviewDotSize * 0.5);
            Canvas.SetTop(dot,  s.Y - PreviewDotSize * 0.5);
            PreviewCanvas.Children.Add(dot);
        }
    }

    /// <summary>プレビュー用の線分を 1 本追加する。</summary>
    private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        PreviewCanvas.Children.Add(new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = brush, StrokeThickness = thickness,
        });
    }

    // ════════════════════════════════════════════════════════════
    //  イベントハンドラ
    // ════════════════════════════════════════════════════════════

    /// <summary>パターン選択が変わったとき: 表示欄を切り替えてプレビューを更新する。</summary>
    private void OnPatternChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        CollectSpecFromUi();
        ApplyPatternVisibility();
        UpdatePreview();
    }

    /// <summary>ランダム散布の範囲形状が変わったとき。</summary>
    private void OnAreaKindChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyAreaVisibility();
        UpdatePreview();
    }

    /// <summary>配置元（空アクタ／アクタファイル）が変わったとき。</summary>
    private void OnSourceKindChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplySourceVisibility();
    }

    /// <summary>
    /// 配置元アクタファイルを選ぶ。
    ///
    /// 2D 配置なら .actor2d、3D 配置なら .actor だけを候補にする
    /// （種別違いのファイルを選べてしまうと、生成後に必ず破綻するため）。
    /// 選択したパスは assets ルート基準の仮想パスへ変換して保持する。
    /// </summary>
    private void OnBrowseSourceFile(object sender, RoutedEventArgs e)
    {
        string ext    = _ctx.Is2D ? ".actor2d" : ".actor";
        string filter = _ctx.Is2D ? "2D アクタファイル|*.actor2d" : "アクタファイル|*.actor";
        var root = MainWindow.AssetsPath;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title            = $"配置元アクタファイル（{ext}）を選択",
            Filter           = filter,
            InitialDirectory = Directory.Exists(root) ? root : Environment.CurrentDirectory,
        };
        if (dlg.ShowDialog(this) != true) return;

        _sourcePath = VirtualPath.ToVirtual(dlg.FileName, root);
        TxtSourcePath.Text = _sourcePath;
        RadioSourceFile.IsChecked = true;
        ApplySourceVisibility();
    }

    /// <summary>
    /// アンカーのプリセットボタン（左上 / 中央 / 右下）。
    ///
    /// 値は XAML の <c>Tag</c> に "x,y" 形式で持たせてある
    /// （ボタンごとにハンドラを増やさず、増減を XAML 側だけで完結させるため）。
    /// </summary>
    private void OnAnchorPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var parts = tag.Split(',');
        if (parts.Length != 2) return;
        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ax)) return;
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ay)) return;
        TxtAnchorX.Text = Num(PlacementSpec.ClampAnchor(ax));
        TxtAnchorY.Text = Num(PlacementSpec.ClampAnchor(ay));
        UpdatePreview();
    }

    /// <summary>シードを引き直す（同じパラメータで別の散らばりを見るための操作）。</summary>
    private void OnRandomizeSeed(object sender, RoutedEventArgs e)
    {
        // ビットパターンを保ったまま u64 として読み替える（Rust 側 seed は u64）。
        ulong seed = unchecked((ulong)Random.Shared.NextInt64());
        TxtSeed.Text = seed.ToString(CultureInfo.InvariantCulture);
        UpdatePreview();
    }

    /// <summary>キャンバスの大きさが変わったら描き直す（縮尺が変わるため）。</summary>
    private void OnPreviewCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_loading) return;
        DrawPreview(_preview);
    }

    /// <summary>Esc で閉じる（ダイアログの共通作法）。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }

    /// <summary>「キャンセル」。何も送らずに閉じる。</summary>
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 「配置」。ランタイムへ指定を 1 発送り、パラメータを記憶して閉じる。
    ///
    /// <para>
    /// 配置対象（新規アクタ／制御点）によらず <c>LOGIC_PLACE_BEGIN</c> を送って
    /// <b>配置モード</b>へ入れる。ダイアログはここで閉じ、以降はビューポート上で
    /// 「カーソル追従プレビュー → 左クリックで確定 / 右クリック・Esc で取消」となる。
    /// 制御点の場合はカーソルのワールド着弾点を対象アクタのローカル座標へ変換した点が
    /// 基準点になる（変換はランタイム側が行う）。
    /// </para>
    /// </summary>
    private void OnAdd(object sender, RoutedEventArgs e)
    {
        CollectSpecFromUi();

        var req = new LogicPlaceRequest
        {
            Target = _ctx.IsControlPointMode
                ? LogicPlaceRequest.TargetControlPoints
                : LogicPlaceRequest.TargetActors,
            Is2D       = _ctx.Is2D,
            ParentDfs  = _ctx.ParentDfs,
            GroupName  = BuildGroupName(),
            NamePrefix = _spec.PatternDisplayName,
            SourcePath = (!_ctx.IsControlPointMode && RadioSourceFile.IsChecked == true) ? _sourcePath : null,
            Ground     = !_ctx.IsControlPointMode && !_ctx.Is2D && ChkGround.IsChecked == true,
            ActorDfsId = _ctx.ActorDfsId,
            SlotIdx    = _ctx.SlotIdx,
            Spec       = _spec,
        };
        // ランタイムを配置モードへ入れ、ビューポートのカーソル位置で確定させる。
        _runtime?.SendToRuntime(req.ToBeginIpcCommand());

        // 次回の初期値として記憶する（パラメータを毎回入れ直さずに済むように）。
        EditorPreferences.Instance.LogicPlacement = _spec.Clone();
        EditorPreferences.Instance.LogicPlacementGround = ChkGround.IsChecked == true;
        EditorPreferences.Save();

        DialogResult = true;
    }

    /// <summary>
    /// 生成するグループフォルダ名を決める。
    /// 呼び出し元が命名規則（重複回避）を持っていればそれに委ねる。
    /// </summary>
    private string BuildGroupName()
    {
        var baseName = _spec.PatternDisplayName;
        return _ctx.MakeUniqueGroupName?.Invoke(baseName) ?? baseName;
    }
}
