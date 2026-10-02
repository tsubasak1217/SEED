// ============================================================
//  Program.cs — プロジェクト設定「描画の構成」「文字の描画」とパッケージ化「開発用のビルド」のオフスクリーン描画プローブ
//
//  【何をするか】
//  実物の ProjectSettingsWindow / PackagingWindow を **表示せずに** 組み立て、一時フォルダの設定ファイルを当てた
//  各状態を PNG へ書き出す。あわせて、コンボ・チェックを操作したときに設定へ何が書かれるかを表明する。
//    01_render_default   … render 節なし（既定 full。旗はすべて「構成のまま（有効）」）
//    02_render_ui        … render: { profile: ui, post: true, nope: 1 }（3D なしの注意・読まないキーの注意・post だけ有効）
//    03_render_ops       … 01 から ui を選び post を有効にした後（書き込み: profile=ui, post=true）
//    04_font_default     … font 節なし（MTSDF・ink trap。辺の色分けの欄が出る）
//    05_font_sdf         … SDF を選んだ後（辺の色分けの欄は隠れる。書き込み: distance_field=sdf）
//    06_pkg_windows_debug   … Windows・ビルド種別 Debug（開発用のビルドに自動でチェック）
//    07_pkg_windows_release … Release に切り替えてチェックを入れた後（上書き true・注意が出る）
//    08_pkg_android      … Android・開発用（チェックは押せない・入っている）
//  各状態の PNG は窓の大きさの「_window」と、右パネルを縦に伸ばして全部写した「_full」の 2 枚。
//
//  【なぜ窓を出さないのか】
//  エディタ（SEEDEditor.exe）は起動しない約束であり、利用者の画面にも何も出さない。設定ファイルは一時フォルダにだけ書く。
//
//  使い方:
//    dotnet run --project editor/tests/ProjectSettingsPreviewProbe -- --out <出力先>
//    （省略時は %TEMP%\seed_project_settings_preview）
// ============================================================

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SEEDEditor.Packaging;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Tests.ProjectSettingsPreview;

/// <summary>プローブのエントリポイント。</summary>
public static class Program
{
    // ── 描画の寸法（マジックナンバー回避）────────────────────

    /// <summary>プロジェクト設定ウィンドウの中身の幅（窓の既定の幅 860 から枠の分を引いた値）。</summary>
    private const int ProjectSettingsWidthPx = 844;

    /// <summary>パッケージ化ウィンドウの中身の幅（窓の既定の幅 800 から枠の分を引いた値）。</summary>
    private const int PackagingWidthPx = 784;

    /// <summary>窓の中身の高さ（どちらの窓も既定の高さ 560 から枠の分を引いた値）。</summary>
    private const int WindowHeightPx = 521;

    /// <summary>「_full」の画像で、右パネルの高さに足す余白（パネルの外側の余白・フッターなど）。</summary>
    private const int FullImageExtraHeightPx = 260;

    /// <summary>PNG の DPI（WPF の既定と同じ 96）。</summary>
    private const double RenderDpi = 96.0;

    /// <summary>Dispatcher のキューを空にするために回す回数。</summary>
    private const int DispatcherPumpCount = 8;

    /// <summary>出力先を指定するオプション名。</summary>
    private const string OptionOut = "--out";

    /// <summary>出力先を指定しなかったときの既定（一時フォルダの下。リポジトリに画像を作らない）。</summary>
    private const string DefaultOutDirName = "seed_project_settings_preview";

    /// <summary>プロジェクト設定ウィンドウの右パネルの入れ物の名前（XAML の x:Name）。</summary>
    private const string SettingsContentName = "SettingsContent";

    /// <summary>パッケージ化ウィンドウの設定ペインの名前（XAML の x:Name）。</summary>
    private const string SettingsPaneName = "SettingsPane";

    /// <summary>表明の失敗数。</summary>
    private static int _failures;

    /// <summary>各状態を順に描画して PNG へ書き出す。</summary>
    /// <param name="args">コマンドライン引数（--out &lt;出力先&gt;）。</param>
    /// <returns>終了コード（全部書けて表明が通ったら 0）。</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var outDir = ResolveOutDir(args);
        Directory.CreateDirectory(outDir);

        // App を生成すると App.xaml のリソース（共通ボタン書式・暗いコンボ）が載る。Run() は呼ばない。
        var app = new SEEDEditor.App();
        app.InitializeComponent();

        var projectRoot = Path.Combine(Path.GetTempPath(), "SEED_ProjectSettingsPreview_" + Guid.NewGuid().ToString("N"));
        var assets = Path.Combine(projectRoot, "assets");
        Directory.CreateDirectory(assets);
        try
        {
            Run("描画の構成", () => RenderProfileScenarios(outDir, assets));
            Run("文字の描画", () => FontFieldScenarios(outDir, assets));
            Run("開発用のビルド", () => DebugBuildMarkScenarios(outDir, assets));
        }
        finally
        {
            try { Directory.Delete(projectRoot, recursive: true); } catch { /* 後始末の失敗は無視 */ }
        }

        Console.WriteLine();
        Console.WriteLine($"出力先: {outDir}");
        Console.WriteLine(_failures == 0 ? "すべての表明が通りました" : $"表明の失敗 {_failures} 件");
        return _failures == 0 ? 0 : 1;
    }

    // ============================================================
    //  描画の構成（render 節）
    // ============================================================

    /// <summary>「描画の構成」パネルの状態と操作。</summary>
    /// <param name="outDir">出力先。</param>
    /// <param name="assets">一時プロジェクトのアセットフォルダ。</param>
    private static void RenderProfileScenarios(string outDir, string assets)
    {
        var settingsPath = Path.Combine(assets, "project_settings.json");

        // ── 01: 節なし（既定 full）──
        File.WriteAllText(settingsPath, "{ \"game_name\": \"Probe\" }");
        var window = OpenProjectSettings(assets, "render_profile");
        var panel = ProjectSettingsPanel(window);
        var profile = RowControl<ComboBox>(panel, "構成");
        var post = RowControl<ComboBox>(panel, "後処理");
        Expect(Tag(profile) as string == "full", "01: 節なしは既定の構成 full を選ぶ");
        Expect(post.SelectedIndex == 0 && ItemText(post, 0) == "構成のまま（有効）", $"01: 旗は「構成のまま（有効）」（{ItemText(post, 0)}）");
        Expect(Note(panel, "この設定では 3D のシーンを描きません").Visibility != Visibility.Visible, "01: full では 3D の注意を出さない");
        RenderWindow(outDir, "01_render_default", window, ProjectSettingsWidthPx, panel);

        // ── 03: 01 から操作（ui を選ぶ → post を有効 → 保存の中身を見る → full に戻す → post を構成のままへ）──
        SelectTag(profile, "ui");
        Expect(ItemText(post, 0) == "構成のまま（無効）", $"03: ui を選ぶと「構成のまま」が ui の値（無効）になる（{ItemText(post, 0)}）");
        Expect(post.SelectedIndex == 0 && (post.SelectedItem as ComboBoxItem)?.Content as string == "構成のまま（無効）",
            "03: 選択中の「構成のまま」の表示も新しい文言（閉じた箱の表示）");
        SelectTag(post, true);
        using (var saved = SaveAndRead(window.DataForVerification, settingsPath))
        {
            var render = saved.RootElement.GetProperty("render");
            Expect(render.GetProperty("profile").GetString() == "ui", "03: profile=ui を書く");
            Expect(render.GetProperty("post").ValueKind == JsonValueKind.True, "03: post=true を書く");
            Expect(!render.TryGetProperty("gi", out _), "03: 触っていない旗は書かない");
        }
        Expect(Note(panel, "この設定では 3D のシーンを描きません").Visibility == Visibility.Visible, "03: ui では 3D の注意を出す");
        Expect(Note(panel, "この設定で起動したときの実効").Text.Contains("後処理 有効"), "03: 実効の要約に後処理 有効");
        RenderWindow(outDir, "03_render_ops", window, ProjectSettingsWidthPx, panel);

        SelectTag(profile, "full");
        using (var saved = SaveAndRead(window.DataForVerification, settingsPath))
        {
            var render = saved.RootElement.GetProperty("render");
            Expect(!render.TryGetProperty("profile", out _), "03: 既定の構成（full）を選ぶと profile を書かない");
            Expect(render.GetProperty("post").ValueKind == JsonValueKind.True, "03: 旗の上書きは残る");
        }
        SelectTag(post, null);
        using (var saved = SaveAndRead(window.DataForVerification, settingsPath))
        {
            Expect(!saved.RootElement.TryGetProperty("render", out _), "03: すべて既定に戻すと render 節ごと消える");
        }
        window.Close();

        // ── 02: 節あり（ui・post 有効・ランタイムが読まないキー）──
        File.WriteAllText(settingsPath, "{ \"render\": { \"profile\": \"ui\", \"post\": true, \"nope\": 1 } }");
        window = OpenProjectSettings(assets, "render_profile");
        panel = ProjectSettingsPanel(window);
        Expect(Tag(RowControl<ComboBox>(panel, "構成")) as string == "ui", "02: ui を選んでいる");
        Expect(RowControl<ComboBox>(panel, "後処理").SelectedIndex == 1, "02: post は「有効」");
        Expect(Note(panel, "ランタイムが読まないキー").Text.Contains("nope=1"), "02: 読まないキーの注意を出す");
        RenderWindow(outDir, "02_render_ui", window, ProjectSettingsWidthPx, panel);
        using (var saved = SaveAndRead(window.DataForVerification, settingsPath))
        {
            Expect(saved.RootElement.GetProperty("render").GetProperty("nope").GetInt32() == 1, "02: 開いて保存しても読まないキーを消さない");
        }
        window.Close();
    }

    // ============================================================
    //  文字の描画（font 節）
    // ============================================================

    /// <summary>「文字の描画」パネルの状態と操作。</summary>
    /// <param name="outDir">出力先。</param>
    /// <param name="assets">一時プロジェクトのアセットフォルダ。</param>
    private static void FontFieldScenarios(string outDir, string assets)
    {
        var settingsPath = Path.Combine(assets, "project_settings.json");
        File.WriteAllText(settingsPath, "{ \"game_name\": \"Probe\" }");
        var window = OpenProjectSettings(assets, "font_field");
        var panel = ProjectSettingsPanel(window);
        var field = RowControl<ComboBox>(panel, "距離場");
        var coloring = RowControl<ComboBox>(panel, "辺の色分け");
        Expect(Tag(field) as string == FontFieldCatalog.DistanceFieldMtsdf, "04: 節なしは MTSDF");
        Expect(Tag(coloring) as string == FontFieldCatalog.ColoringInkTrap, "04: 節なしは ink trap");
        Expect(IsShown(coloring), "04: MTSDF では辺の色分けの欄を出す");
        RenderWindow(outDir, "04_font_default", window, ProjectSettingsWidthPx, panel);

        SelectTag(field, FontFieldCatalog.DistanceFieldSdf);
        Expect(!IsShown(coloring), "05: SDF を選ぶと辺の色分けの欄を隠す");
        using (var saved = SaveAndRead(window.DataForVerification, settingsPath))
        {
            var font = saved.RootElement.GetProperty("font");
            Expect(font.GetProperty("distance_field").GetString() == "sdf", "05: distance_field=sdf を書く");
            Expect(!font.TryGetProperty("msdf_coloring", out _), "05: 触っていない色分けは書かない");
        }
        RenderWindow(outDir, "05_font_sdf", window, ProjectSettingsWidthPx, panel);

        SelectTag(field, FontFieldCatalog.DistanceFieldMtsdf);
        using (var saved = SaveAndRead(window.DataForVerification, settingsPath))
        {
            Expect(!saved.RootElement.TryGetProperty("font", out _), "05: 既定（MTSDF）に戻すと font 節ごと消える");
        }
        window.Close();
    }

    // ============================================================
    //  パッケージ化の開発用のビルド
    // ============================================================

    /// <summary>パッケージ化ウィンドウの「開発用のビルド」の欄の状態と操作。</summary>
    /// <param name="outDir">出力先。</param>
    /// <param name="assets">一時プロジェクトのアセットフォルダ。</param>
    private static void DebugBuildMarkScenarios(string outDir, string assets)
    {
        var packagingPath = Path.Combine(assets, PackagingData.SettingsFileName);
        File.WriteAllText(packagingPath, "{ \"windows\": { \"build_type\": \"Debug\" }, \"android\": { \"variant\": \"Debug\" } }");

        var window = new PackagingWindow(assets);
        window.SelectPlatformForVerification(TargetPlatform.Windows);
        var pane = Named<FrameworkElement>(window, SettingsPaneName);
        var buildType = RowControl<ComboBox>(pane, "ビルド種別");
        var mark = RowControl<CheckBox>(pane, "開発用のビルド");
        Expect(mark.IsChecked == true && mark.IsEnabled, "06: Debug は自動で開発用のビルドにチェック（押せる）");
        Expect(Note(pane, "いまの決まり方").Text.Contains("自動"), "06: 決まり方は「自動」");
        RenderWindow(outDir, "06_pkg_windows_debug", window, PackagingWidthPx, pane);

        mark.IsChecked = false;
        Expect(window.DataForVerification.Windows.DebugBuildMark == false, "07: Debug で外すと上書き false");
        buildType.SelectedItem = "Release";
        Expect(window.DataForVerification.Windows.BuildType == BuildType.Release, "07: ビルド種別は Release");
        Expect(window.DataForVerification.Windows.DebugBuildMark is null && mark.IsChecked == false,
            "07: ビルド種別を選び直すと自動に戻る（Release は入れない）");
        mark.IsChecked = true;
        Expect(window.DataForVerification.Windows.DebugBuildMark == true, "07: Release で入れると上書き true");
        Expect(Note(pane, "ビルド種別が Release でも").Visibility == Visibility.Visible, "07: Release で入れると注意を出す");
        RenderWindow(outDir, "07_pkg_windows_release", window, PackagingWidthPx, pane);
        window.DataForVerification.SaveTo(packagingPath);
        Expect(File.ReadAllText(packagingPath).Contains("\"debug_build_mark\": true"), "07: packaging_settings.json に debug_build_mark: true");

        window.SelectPlatformForVerification(TargetPlatform.Android);
        pane = Named<FrameworkElement>(window, SettingsPaneName);
        var androidMark = RowControl<CheckBox>(pane, "開発用のビルド");
        Expect(androidMark.IsChecked == true && !androidMark.IsEnabled, "08: Android の開発用はチェック入り・押せない");
        RenderWindow(outDir, "08_pkg_android", window, PackagingWidthPx, pane);
        window.Close();
    }

    // ============================================================
    //  窓と部品
    // ============================================================

    /// <summary>プロジェクト設定ウィンドウを作り、小項目を選んだ状態にする（表示しない）。</summary>
    /// <param name="assets">アセットフォルダ。</param>
    /// <param name="subItemId">小項目の ID。</param>
    /// <returns>窓。</returns>
    private static ProjectSettingsWindow OpenProjectSettings(string assets, string subItemId)
    {
        var window = new ProjectSettingsWindow(assets);
        window.SelectSubItemForVerification(subItemId);
        Layout((FrameworkElement)window.Content, ProjectSettingsWidthPx, WindowHeightPx);
        return window;
    }

    /// <summary>プロジェクト設定ウィンドウの右パネル（選んだ小項目の中身）。</summary>
    /// <param name="window">窓。</param>
    /// <returns>右パネル。</returns>
    private static FrameworkElement ProjectSettingsPanel(ProjectSettingsWindow window) =>
        Named<ContentControl>(window, SettingsContentName).Content as FrameworkElement
        ?? throw new InvalidOperationException("右パネルが空です");

    /// <summary>名前付きの部品を引く（見つからなければ例外＝XAML の名前が変わった合図）。</summary>
    private static T Named<T>(FrameworkElement window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"{name} が見つかりません");

    /// <summary>
    /// 「ラベル＋入力欄」の行から入力欄を引く（ラベルの文字が一致する TextBlock を持つ Grid の、指定の型の子）。
    /// 見えている行を優先する（同じラベルの行がプラットフォームを切り替えた後に残ることは無いが、念のため）。
    /// </summary>
    private static T RowControl<T>(DependencyObject root, string label) where T : FrameworkElement
    {
        foreach (var grid in Descendants(root).OfType<Grid>())
        {
            var children = grid.Children.OfType<FrameworkElement>().ToList();
            if (children.OfType<TextBlock>().Any(t => t.Text == label) && children.OfType<T>().FirstOrDefault() is { } control)
            {
                return control;
            }
        }
        throw new InvalidOperationException($"行「{label}」の {typeof(T).Name} が見つかりません");
    }

    /// <summary>文が指定の書き出しで始まる TextBlock を引く（注意・説明の行）。</summary>
    private static TextBlock Note(DependencyObject root, string prefix) =>
        Descendants(root).OfType<TextBlock>().FirstOrDefault(t => t.Text.StartsWith(prefix, StringComparison.Ordinal)
                                                                  || t.Text.Contains(prefix, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"「{prefix}」の文が見つかりません");

    /// <summary>論理ツリーの子孫を辿る（コードで組んだ StackPanel / Grid / Border / ContentControl の中身）。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var grandChild in Descendants(child)) yield return grandChild;
        }
    }

    /// <summary>
    /// 部品と祖先がすべて見えているか（Collapsed の入れ物の中は見えない）。窓そのものは数えない
    /// （表示していない窓は Visibility が Collapsed のため。このプローブは窓を出さない）。
    /// </summary>
    private static bool IsShown(FrameworkElement element)
    {
        for (DependencyObject? node = element; node is not null and not Window; node = LogicalTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
    }

    /// <summary>コンボの選択中の項目の Tag。</summary>
    private static object? Tag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag;

    /// <summary>コンボの項目の文言。</summary>
    private static string? ItemText(ComboBox combo, int index) => (combo.Items[index] as ComboBoxItem)?.Content as string;

    /// <summary>Tag が一致する項目を選ぶ（利用者がドロップダウンから選んだのと同じ知らせが出る）。</summary>
    private static void SelectTag(ComboBox combo, object? tag)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (Equals((combo.Items[i] as ComboBoxItem)?.Tag, tag))
            {
                combo.SelectedIndex = i;
                Pump();
                return;
            }
        }
        throw new InvalidOperationException($"Tag {tag ?? "null"} の項目がありません");
    }

    /// <summary>編集中の設定を一時ファイルへ保存し、JSON として読み戻す（「保存して閉じる」と同じ書き方）。</summary>
    private static JsonDocument SaveAndRead(ProjectSettingsData data, string path)
    {
        data.SaveTo(path);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    // ============================================================
    //  描画
    // ============================================================

    /// <summary>
    /// 窓の大きさの画像（_window）と、右側を縦に伸ばして全部写した画像（_full）を書き出す。
    /// </summary>
    /// <param name="outDir">出力先。</param>
    /// <param name="name">ファイル名（拡張子なし）。</param>
    /// <param name="window">窓。</param>
    /// <param name="widthPx">中身の幅。</param>
    /// <param name="panel">右側のパネル（縦の長さを測る）。</param>
    private static void RenderWindow(string outDir, string name, Window window, int widthPx, FrameworkElement panel)
    {
        try
        {
            var content = (FrameworkElement)window.Content;
            Layout(content, widthPx, WindowHeightPx);
            Save(content, window.Background, widthPx, WindowHeightPx, Path.Combine(outDir, name + "_window.png"));

            var fullHeight = Math.Max(WindowHeightPx, (int)Math.Ceiling(panel.DesiredSize.Height) + FullImageExtraHeightPx);
            Layout(content, widthPx, fullHeight);
            Save(content, window.Background, widthPx, fullHeight, Path.Combine(outDir, name + "_full.png"));
            Layout(content, widthPx, WindowHeightPx);
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"    [FAIL] {name} の描画: {ex}");
        }
    }

    /// <summary>中身を PNG へ書き出す（窓の地色を後ろに敷く）。</summary>
    private static void Save(FrameworkElement content, Brush background, int widthPx, int heightPx, string file)
    {
        var bitmap = new RenderTargetBitmap(widthPx, heightPx, RenderDpi, RenderDpi, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(background, null, new Rect(0, 0, widthPx, heightPx));
        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(file)) encoder.Save(stream);
        Console.WriteLine($"    書き出しました: {file}");
    }

    /// <summary>中身を指定の大きさで測って並べ、Dispatcher を回す（テンプレートとバインドを解決させる）。</summary>
    private static void Layout(FrameworkElement content, int widthPx, int heightPx)
    {
        content.Measure(new Size(widthPx, heightPx));
        content.Arrange(new Rect(0, 0, widthPx, heightPx));
        content.UpdateLayout();
        Pump();
    }

    /// <summary>Dispatcher のキューを空にする。</summary>
    private static void Pump()
    {
        for (var i = 0; i < DispatcherPumpCount; i++)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    // ============================================================
    //  小道具
    // ============================================================

    /// <summary>1 つの塊を実行し、例外は失敗として数える。</summary>
    private static void Run(string title, Action body)
    {
        Console.WriteLine($"── {title} ──");
        try { body(); }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"    [FAIL] {title}: {ex}");
        }
    }

    /// <summary>表明（失敗は数えて最後に終了コードへ反映する）。</summary>
    private static void Expect(bool condition, string what)
    {
        Console.WriteLine((condition ? "    [ OK ] " : "    [FAIL] ") + what);
        if (!condition) _failures++;
    }

    /// <summary>出力先を決める（--out が無ければ一時フォルダの下）。</summary>
    private static string ResolveOutDir(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], OptionOut, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[i + 1]);
        return Path.Combine(Path.GetTempPath(), DefaultOutDirName);
    }
}
