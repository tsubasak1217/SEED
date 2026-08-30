using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Controls;
using SEEDEditor.Panels.SpriteRig;
using SEEDEditor.Panels.SpriteRig.IO;
using SEEDEditor.Panels.SpriteRig.Mesh;
using SEEDEditor.Panels.SpriteRig.Model;

namespace SEEDEditor.Panels;

/// <summary>
/// スプライトリグ（<c>.sprite_mesh</c> のメッシュ作成・編集）パネル。
///
/// Spine / Unity Skinning Editor 相当の作業のうち Phase B1a が扱う範囲
/// ―― 画像の取り込み、アルファからの自動メッシュ化、手作業でのポリゴン／頂点編集、
/// <c>.sprite_mesh</c> の保存 ―― をここで完結させる。ランタイム（Rust）は使わず、
/// 描画も編集も純 WPF で行う。
///
/// 画像 1 枚ぶんの編集状態は <see cref="SpriteRigDocument"/> が持ち、
/// 複数タブを <see cref="SpriteRigDocumentSet"/> が束ねる。
/// このクラスの責務は「その集合を WPF の TabControl とツールバーへ写すこと」だけで、
/// 編集ロジックそのものは持たない（単体テスト可能性を保つため）。
///
/// Phase B1b（ボーン配置・ウェイトペイント）は
/// <see cref="SpriteRigEditMode"/> の切替と <see cref="SpriteRigMesh.Bones"/> /
/// <see cref="SpriteRigMesh.Weights"/> のデータ構造だけ先に用意してある。
/// </summary>
public partial class SpriteRigPanel : UserControl
{
    /// <summary>プロジェクトパネルがドラッグ＆ドロップで渡してくるデータ形式名。</summary>
    private const string ProjectPathsDataFormat = "SEEDProjectPaths";

    /// <summary>タブ見出しのアイコン一辺（px）。</summary>
    private const double TabIconSize = 12.0;

    /// <summary>タブ見出しの閉じるボタン一辺（px）。</summary>
    private const double TabCloseIconSize = 9.0;

    /// <summary>開いているドキュメント（タブ）の集合。</summary>
    private readonly SpriteRigDocumentSet _documents = new();

    /// <summary>アセットルート（ファイルダイアログの初期ディレクトリに使う）。</summary>
    private string _assetsPath = string.Empty;

    /// <summary>UI からの変更通知を一時的に無視するためのガード（初期化・同期中）。</summary>
    private bool _suppressUiEvents;

    /// <summary>タブ構成やアクティブタブが変わったときに発火する（アンカーのタイトル更新用）。</summary>
    public event Action<string>? TitleChanged;

    /// <summary><c>.sprite_mesh</c> を保存したときに発火する（保存パスを渡す）。</summary>
    public event Action<string>? MeshSaved;

    /// <summary>パネルを初期化する。</summary>
    public SpriteRigPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPanelPreviewKeyDown;
        UpdateUiForActiveDocument();
    }

    /// <summary>
    /// アセットルートを設定する（ファイルダイアログの初期位置に使う）。
    /// </summary>
    /// <param name="assetsPath">アセットルートの絶対パス。</param>
    public void SetAssetsPath(string assetsPath) => _assetsPath = assetsPath;

    // ============================================================
    //  外部から開く（プロジェクトパネル・メニュー）
    // ============================================================

    /// <summary>
    /// 画像を新しいタブで開く。既に同じ画像の未保存タブがあればそれをアクティブにする。
    /// <b>編集中の他タブは決して破棄されない。</b>
    /// </summary>
    /// <param name="imagePath">画像の絶対パス。</param>
    public void OpenImage(string imagePath)
    {
        // 同名の .sprite_mesh が既にあるなら、そちらを開いて続きから編集できるようにする
        string siblingMesh = Path.ChangeExtension(imagePath, SpriteMeshFile.Extension);
        if (File.Exists(siblingMesh))
        {
            OpenSpriteMesh(siblingMesh);
            return;
        }

        try
        {
            var image = SpriteImageLoader.Load(imagePath);
            var document = new SpriteRigDocument(imagePath, image);
            ActivateDocument(_documents.AddOrActivate(document));
        }
        catch (Exception ex)
        {
            ShowError($"画像を開けませんでした:\n{imagePath}\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// 既存の <c>.sprite_mesh</c> を開いて再編集する。
    /// 紐づく画像は <c>texture</c> フィールド → 同名画像 → ユーザー選択の順で解決する。
    /// </summary>
    /// <param name="meshPath">.sprite_mesh の絶対パス。</param>
    public void OpenSpriteMesh(string meshPath)
    {
        // 既に開いていればタブを切り替えるだけ（読み直して編集内容を失わない）
        var already = _documents.FindByMeshPath(meshPath);
        if (already != null)
        {
            ActivateDocument(_documents.AddOrActivate(already));
            return;
        }

        try
        {
            var loaded = SpriteMeshFile.Load(meshPath);
            string? imagePath = loaded.TextureHint ?? FindSiblingImage(meshPath) ?? AskForImage(meshPath);
            if (imagePath == null) return;   // ユーザーがキャンセルした

            var image = SpriteImageLoader.Load(imagePath);
            var document = SpriteRigDocument.FromExistingMesh(imagePath, image, meshPath, loaded.Mesh);
            ActivateDocument(_documents.AddOrActivate(document));
        }
        catch (Exception ex)
        {
            ShowError($".sprite_mesh を開けませんでした:\n{meshPath}\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// <c>.sprite_mesh</c> と同じ場所・同じ名前の画像を探す。
    /// </summary>
    /// <param name="meshPath">.sprite_mesh のパス。</param>
    /// <returns>見つかった画像の絶対パス。無ければ null。</returns>
    private static string? FindSiblingImage(string meshPath)
    {
        string directory = Path.GetDirectoryName(meshPath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(meshPath);
        if (string.IsNullOrEmpty(directory)) return null;

        foreach (string candidate in Directory.EnumerateFiles(directory, baseName + ".*"))
        {
            if (SpriteImageLoader.IsSupportedImagePath(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// 画像を特定できなかったとき、ユーザーに選ばせる。
    /// </summary>
    /// <param name="meshPath">対象の .sprite_mesh（初期ディレクトリに使う）。</param>
    /// <returns>選ばれた画像パス。キャンセルなら null。</returns>
    private string? AskForImage(string meshPath)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"'{Path.GetFileName(meshPath)}' に対応する画像を選択してください",
            Filter = SpriteImageLoader.OpenDialogFilter,
            InitialDirectory = Path.GetDirectoryName(meshPath) ?? _assetsPath,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    // ============================================================
    //  タブ管理
    // ============================================================

    /// <summary>
    /// ドキュメントに対応するタブを作る（無ければ生成する）。
    /// </summary>
    /// <param name="document">対象ドキュメント。</param>
    private TabItem EnsureTab(SpriteRigDocument document)
    {
        var existing = FindTab(document);
        if (existing != null) return existing;

        var canvas = new SpriteRigCanvas { Document = document };
        canvas.DocumentModified += OnCanvasDocumentModified;

        var tab = new TabItem
        {
            Tag = document,
            Content = canvas,
            Header = BuildTabHeader(document),
        };
        TabsDocuments.Items.Add(tab);

        // レイアウト確定後に画像全体が見えるようズームを合わせる
        canvas.Loaded += (_, _) => canvas.ZoomToFit();
        return tab;
    }

    /// <summary>
    /// タブ見出し（アイコン + 名前 + 閉じるボタン）を組み立てる。
    /// </summary>
    /// <param name="document">対象ドキュメント。</param>
    private FrameworkElement BuildTabHeader(SpriteRigDocument document)
    {
        var closeButton = new Button
        {
            Content = AppIcon.Create("Icon.Close", TabCloseIconSize),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0.0),
            Padding = new Thickness(3.0, 0.0, 0.0, 0.0),
            Cursor = Cursors.Hand,
            ToolTip = "このタブを閉じる",
            Tag = document,
        };
        closeButton.Click += OnCloseTabClicked;

        var title = new TextBlock
        {
            Text = document.TabTitle,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4.0, 0.0, 2.0, 0.0),
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Tag = title };
        header.Children.Add(AppIcon.Create("Icon.Panel.SpriteRig", TabIconSize));
        header.Children.Add(title);
        header.Children.Add(closeButton);
        return header;
    }

    /// <summary>ドキュメントに対応するタブを探す（無ければ null）。</summary>
    private TabItem? FindTab(SpriteRigDocument document)
        => TabsDocuments.Items.OfType<TabItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, document));

    /// <summary>指定ドキュメントをアクティブなタブにして UI を同期する。</summary>
    private void ActivateDocument(SpriteRigDocument document)
    {
        _documents.Activate(document);
        var tab = EnsureTab(document);
        TabsDocuments.SelectedItem = tab;
        UpdateUiForActiveDocument();
    }

    /// <summary>閉じるボタン。未保存なら保存を確認する。</summary>
    private void OnCloseTabClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: SpriteRigDocument document }) return;
        CloseDocument(document);
    }

    /// <summary>
    /// ドキュメントを閉じる。未保存なら「保存する / しない / キャンセル」を確認する。
    /// </summary>
    /// <param name="document">閉じるドキュメント。</param>
    /// <returns>実際に閉じた場合 true（キャンセルされたら false）。</returns>
    private bool CloseDocument(SpriteRigDocument document)
    {
        if (document.IsDirty)
        {
            var answer = MessageBox.Show(
                $"'{document.DisplayName}' の変更が保存されていません。保存しますか？",
                "スプライトリグ", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes && !TrySave(document, askForPath: false)) return false;
        }

        var tab = FindTab(document);
        if (tab != null) TabsDocuments.Items.Remove(tab);
        _documents.Close(document);

        var active = _documents.Active;
        if (active != null) TabsDocuments.SelectedItem = FindTab(active);
        UpdateUiForActiveDocument();
        return true;
    }

    /// <summary>タブ選択が変わったときにアクティブドキュメントを同期する。</summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 内側のコントロール（ComboBox 等）から浮上してきたイベントは無視する
        if (!ReferenceEquals(e.OriginalSource, TabsDocuments)) return;

        if (TabsDocuments.SelectedItem is TabItem { Tag: SpriteRigDocument document })
            _documents.Activate(document);
        UpdateUiForActiveDocument();
    }

    // ============================================================
    //  UI 同期
    // ============================================================

    /// <summary>キャンバス側でメッシュが変わったときの通知。</summary>
    private void OnCanvasDocumentModified() => UpdateUiForActiveDocument();

    /// <summary>
    /// アクティブドキュメントの状態を、ツールバー・左パネル・タブ見出しへ反映する。
    /// </summary>
    private void UpdateUiForActiveDocument()
    {
        var document = _documents.Active;
        bool hasDocument = document != null;

        TbEmptyHint.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        TabsDocuments.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;

        BtnSave.IsEnabled = hasDocument;
        BtnSaveAs.IsEnabled = hasDocument;
        BtnZoomFit.IsEnabled = hasDocument;
        BtnAutoMesh.IsEnabled = hasDocument;
        BtnRetriangulate.IsEnabled = hasDocument;
        BtnClearMesh.IsEnabled = hasDocument;
        BtnUndo.IsEnabled = document?.History.CanUndo == true;
        BtnRedo.IsEnabled = document?.History.CanRedo == true;

        if (document == null)
        {
            TbStatus.Text = string.Empty;
            TbMeshInfo.Text = "—";
            TitleChanged?.Invoke("スプライトリグ");
            return;
        }

        // ── スライダー・トグルをドキュメントの設定へ合わせる（イベント再入は抑止する）──
        _suppressUiEvents = true;
        SldAlphaThreshold.Value = document.AutoMeshOptions.AlphaThreshold;
        SldSimplifyTolerance.Value = document.AutoMeshOptions.SimplifyTolerance;
        SldInteriorSpacing.Value = document.AutoMeshOptions.InteriorSpacing;
        SldMinIslandArea.Value = document.AutoMeshOptions.MinIslandArea;
        TogglePixelGrid.IsChecked = document.ShowPixelGrid;
        CmbEditMode.SelectedIndex = (int)document.EditMode;
        SyncToolToggles(document.Tool);
        _suppressUiEvents = false;

        UpdateSliderLabels(document);
        UpdateModeHint(document);
        UpdateTabHeader(document);

        TbStatus.Text = $"{Path.GetFileName(document.ImagePath)}  "
                      + $"{document.Image.Width}x{document.Image.Height}px";
        TbMeshInfo.Text =
            $"輪郭: {document.Mesh.Polygons.Count} 本\n"
          + $"内部点: {document.Mesh.InteriorPoints.Count}\n"
          + $"頂点: {document.Mesh.Vertices.Count}\n"
          + $"三角形: {document.Mesh.TriangleCount}\n"
          + $"保存先: {(document.MeshPath == null ? "（未保存）" : Path.GetFileName(document.MeshPath))}";

        TitleChanged?.Invoke(document.IsDirty ? "スプライトリグ *" : "スプライトリグ");
    }

    /// <summary>タブ見出しの名前（未保存マーク付き）を更新する。</summary>
    private void UpdateTabHeader(SpriteRigDocument document)
    {
        var tab = FindTab(document);
        if (tab?.Header is StackPanel { Tag: TextBlock title }) title.Text = document.TabTitle;
    }

    /// <summary>スライダー横の数値表示を更新する。</summary>
    private void UpdateSliderLabels(SpriteRigDocument document)
    {
        var options = document.AutoMeshOptions;
        TbAlphaThreshold.Text = options.AlphaThreshold.ToString(CultureInfo.InvariantCulture);
        TbSimplifyTolerance.Text = options.SimplifyTolerance.ToString("0.0", CultureInfo.InvariantCulture);
        TbInteriorSpacing.Text = options.InteriorSpacing.ToString("0", CultureInfo.InvariantCulture);
        TbMinIslandArea.Text = options.MinIslandArea.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>ボーン／ウェイトモードが未実装であることの案内を出し分ける。</summary>
    private void UpdateModeHint(SpriteRigDocument document)
    {
        bool isMeshMode = document.EditMode == SpriteRigEditMode.Mesh;
        TbModeHint.Visibility = isMeshMode ? Visibility.Collapsed : Visibility.Visible;
        TbModeHint.Text = isMeshMode
            ? string.Empty
            : "このモードの編集操作は Phase B1b で実装します（現在は表示のみ）。";
    }

    /// <summary>ツール選択トグルの状態を現在のツールへ合わせる。</summary>
    private void SyncToolToggles(SpriteRigMeshTool tool)
    {
        ToolSelect.IsChecked = tool == SpriteRigMeshTool.Select;
        ToolDrawPolygon.IsChecked = tool == SpriteRigMeshTool.DrawPolygon;
        ToolAddVertex.IsChecked = tool == SpriteRigMeshTool.AddVertex;
        ToolMoveVertex.IsChecked = tool == SpriteRigMeshTool.MoveVertex;
        ToolDeleteVertex.IsChecked = tool == SpriteRigMeshTool.DeleteVertex;
    }

    /// <summary>アクティブタブのキャンバスを返す（無ければ null）。</summary>
    private SpriteRigCanvas? ActiveCanvas
        => (TabsDocuments.SelectedItem as TabItem)?.Content as SpriteRigCanvas;

    // ============================================================
    //  ツールバー操作
    // ============================================================

    /// <summary>「画像を開く」ボタン。</summary>
    private void OnOpenImage(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "スプライトリグを作成する画像を選択",
            Filter = SpriteImageLoader.OpenDialogFilter,
            InitialDirectory = _assetsPath,
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;

        foreach (string path in dialog.FileNames) OpenImage(path);
    }

    /// <summary>「メッシュを開く」ボタン。</summary>
    private void OnOpenMesh(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "編集する .sprite_mesh を選択",
            Filter = $"スプライトメッシュ|*{SpriteMeshFile.Extension}|すべてのファイル|*.*",
            InitialDirectory = _assetsPath,
        };
        if (dialog.ShowDialog() == true) OpenSpriteMesh(dialog.FileName);
    }

    /// <summary>「保存」ボタン。</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_documents.Active is { } document) TrySave(document, askForPath: false);
    }

    /// <summary>「名前を付けて保存」ボタン。</summary>
    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_documents.Active is { } document) TrySave(document, askForPath: true);
    }

    /// <summary>
    /// ドキュメントを保存する。
    /// </summary>
    /// <param name="document">保存対象。</param>
    /// <param name="askForPath">true なら保存先ダイアログを出す。</param>
    /// <returns>保存できた場合 true。</returns>
    private bool TrySave(SpriteRigDocument document, bool askForPath)
    {
        // 「保存」は保存先が決まっていればそのまま、未保存なら画像と同名の既定パスへ書く。
        // 「名前を付けて保存」のときだけダイアログを出す。
        string target;
        if (!askForPath)
        {
            target = document.MeshPath ?? document.DefaultMeshPath;
        }
        else
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = ".sprite_mesh の保存先",
                Filter = $"スプライトメッシュ|*{SpriteMeshFile.Extension}",
                FileName = Path.GetFileName(document.DefaultMeshPath),
                InitialDirectory = Path.GetDirectoryName(document.DefaultMeshPath) ?? _assetsPath,
                DefaultExt = SpriteMeshFile.Extension,
            };
            if (dialog.ShowDialog() != true) return false;
            target = dialog.FileName;
        }

        try
        {
            string saved = document.Save(target);
            MeshSaved?.Invoke(saved);
            UpdateUiForActiveDocument();
            return true;
        }
        catch (Exception ex)
        {
            ShowError($".sprite_mesh を保存できませんでした:\n{ex.Message}");
            return false;
        }
    }

    /// <summary>「元に戻す」ボタン。</summary>
    private void OnUndo(object sender, RoutedEventArgs e) => PerformUndo();

    /// <summary>「やり直す」ボタン。</summary>
    private void OnRedo(object sender, RoutedEventArgs e) => PerformRedo();

    /// <summary>アクティブドキュメントの Undo を実行する。</summary>
    private void PerformUndo()
    {
        if (_documents.Active?.Undo() != true) return;
        ActiveCanvas?.Refresh();
        UpdateUiForActiveDocument();
    }

    /// <summary>アクティブドキュメントの Redo を実行する。</summary>
    private void PerformRedo()
    {
        if (_documents.Active?.Redo() != true) return;
        ActiveCanvas?.Refresh();
        UpdateUiForActiveDocument();
    }

    /// <summary>ピクセルグリッド表示のトグル。</summary>
    private void OnTogglePixelGrid(object sender, RoutedEventArgs e)
    {
        if (_documents.Active is not { } document) return;
        document.ShowPixelGrid = TogglePixelGrid.IsChecked == true;
        ActiveCanvas?.Refresh();
    }

    /// <summary>「全体表示」ボタン。</summary>
    private void OnZoomFit(object sender, RoutedEventArgs e) => ActiveCanvas?.ZoomToFit();

    /// <summary>編集モード（メッシュ / ボーン / ウェイト）の切り替え。</summary>
    private void OnEditModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _documents.Active is not { } document) return;

        document.EditMode = (SpriteRigEditMode)Math.Max(0, CmbEditMode.SelectedIndex);
        UpdateModeHint(document);
        ActiveCanvas?.Refresh();
    }

    /// <summary>ツール選択トグルの共通ハンドラ。</summary>
    private void OnToolSelected(object sender, RoutedEventArgs e)
    {
        if (_documents.Active is not { } document)
        {
            SyncToolToggles(SpriteRigMeshTool.Select);
            return;
        }

        // 押されたボタンからツールを決める（同じボタンの再クリックでは解除させない）
        SpriteRigMeshTool tool =
            ReferenceEquals(sender, ToolDrawPolygon) ? SpriteRigMeshTool.DrawPolygon :
            ReferenceEquals(sender, ToolAddVertex) ? SpriteRigMeshTool.AddVertex :
            ReferenceEquals(sender, ToolMoveVertex) ? SpriteRigMeshTool.MoveVertex :
            ReferenceEquals(sender, ToolDeleteVertex) ? SpriteRigMeshTool.DeleteVertex :
            SpriteRigMeshTool.Select;

        // ツールを変えたら作図途中のポリゴンは破棄する（半端な状態を残さない）
        if (document.Tool != tool) document.CancelPendingPolygon();
        document.Tool = tool;
        SyncToolToggles(tool);
        ActiveCanvas?.Refresh();
    }

    /// <summary>自動メッシュ化パラメータのスライダー変更。</summary>
    private void OnAutoMeshParameterChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || _documents.Active is not { } document) return;

        var options = document.AutoMeshOptions;
        options.AlphaThreshold = (int)Math.Round(SldAlphaThreshold.Value);
        options.SimplifyTolerance = SldSimplifyTolerance.Value;
        options.InteriorSpacing = SldInteriorSpacing.Value;
        options.MinIslandArea = SldMinIslandArea.Value;
        UpdateSliderLabels(document);
    }

    /// <summary>「自動メッシュ生成」ボタン。</summary>
    private void OnAutoMesh(object sender, RoutedEventArgs e)
    {
        if (_documents.Active is not { } document) return;

        // 既存のジオメトリを置き換えるので、消えて困る手作業があるなら確認する
        if (document.Mesh.HasGeometry)
        {
            var answer = MessageBox.Show(
                "現在のメッシュを破棄して自動生成し直します。よろしいですか？（Ctrl+Z で戻せます）",
                "スプライトリグ", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            document.ApplyAutoMesh();
        }
        catch (Exception ex)
        {
            ShowError($"自動メッシュ生成に失敗しました:\n{ex.Message}");
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        ActiveCanvas?.Refresh();
        UpdateUiForActiveDocument();
    }

    /// <summary>「再三角分割」ボタン。</summary>
    private void OnRetriangulate(object sender, RoutedEventArgs e)
    {
        if (_documents.Active is not { } document) return;
        document.Retriangulate();
        ActiveCanvas?.Refresh();
        UpdateUiForActiveDocument();
    }

    /// <summary>「メッシュを消去」ボタン。</summary>
    private void OnClearMesh(object sender, RoutedEventArgs e)
    {
        if (_documents.Active is not { } document) return;
        document.ClearGeometry();
        ActiveCanvas?.Refresh();
        UpdateUiForActiveDocument();
    }

    // ============================================================
    //  キーボード・ドラッグ＆ドロップ
    // ============================================================

    /// <summary>
    /// パネル内での Ctrl+Z / Ctrl+Y を、このパネル専用の Undo スタックへ流す。
    /// エディタ全体（シーン）の Undo とは独立している。
    /// </summary>
    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        if (e.Key == Key.Z)
        {
            PerformUndo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            PerformRedo();
            e.Handled = true;
        }
    }

    /// <summary>ドラッグ中のカーソル効果を決める（対応形式のときだけコピー扱い）。</summary>
    private void OnPanelDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ExtractDroppedPaths(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>画像 / .sprite_mesh のドロップを受け取り、タブとして開く。</summary>
    private void OnPanelDrop(object sender, DragEventArgs e)
    {
        foreach (string path in ExtractDroppedPaths(e.Data))
        {
            if (Path.GetExtension(path).Equals(SpriteMeshFile.Extension, StringComparison.OrdinalIgnoreCase))
                OpenSpriteMesh(path);
            else
                OpenImage(path);
        }
        e.Handled = true;
    }

    /// <summary>
    /// ドロップデータから、このパネルが開けるファイルパスだけを取り出す。
    /// プロジェクトパネル形式（<see cref="ProjectPathsDataFormat"/>）と
    /// エクスプローラ形式（FileDrop）の両方に対応する。
    /// </summary>
    /// <param name="data">ドロップされたデータ。</param>
    private static List<string> ExtractDroppedPaths(IDataObject data)
    {
        var candidates = new List<string>();
        if (data.GetDataPresent(ProjectPathsDataFormat) &&
            data.GetData(ProjectPathsDataFormat) is string[] projectPaths)
        {
            candidates.AddRange(projectPaths);
        }
        else if (data.GetDataPresent(DataFormats.FileDrop) &&
                 data.GetData(DataFormats.FileDrop) is string[] filePaths)
        {
            candidates.AddRange(filePaths);
        }

        var accepted = new List<string>(candidates.Count);
        foreach (string path in candidates)
        {
            if (SpriteImageLoader.IsSupportedImagePath(path) ||
                Path.GetExtension(path).Equals(SpriteMeshFile.Extension, StringComparison.OrdinalIgnoreCase))
            {
                accepted.Add(path);
            }
        }
        return accepted;
    }

    /// <summary>エラーメッセージを表示する。</summary>
    /// <param name="message">表示する本文。</param>
    private static void ShowError(string message)
        => MessageBox.Show(message, "スプライトリグ", MessageBoxButton.OK, MessageBoxImage.Error);
}
