// ============================================================
//  Program.cs — 文字列表（ローカライズ）パネルのオフスクリーン描画プローブ
//
//  【何をするか】
//  実物の LocalizationPanel（XAML）を **表示せずに** 組み立て、一時フォルダへ写した見本の表（templates/locale に
//  未訳の多い fr を 1 つ足したもの）を開いた各状態を PNG へ書き出す。
//    01_table          … 開いた直後（行を 1 つ選んで説明の帯も出す）
//    02_missing_only   … 「未訳だけ表示」
//    03_search         … 検索欄に「dialog」
//    04_dirty          … 升目を 1 つ書き換えた（タブ名に * ・保存が押せる）
//    05_missing_folder … assets/locale が無いプロジェクト（「見本から作る」の案内）
//  あわせて、状態ごとに列・行の数・要約・未保存の印・案内の出し分けを標準出力へ出して表明する。
//
//  【なぜパネルを出さないのか】
//  エディタ（SEEDEditor.exe）は起動しない約束であり、利用者の画面にも何も出さない。
//  パネルを測って並べ、RenderTargetBitmap で描く（TemplateActorPickerPreviewProbe と同じ流儀）。
//
//  使い方:
//    dotnet run --project editor/tests/LocalizationPanelPreviewProbe -- --out <出力先>
// ============================================================

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SEEDEditor.Panels;
using SEEDEditor.Panels.Localization;

namespace SEEDEditor.Tests.LocalizationPanelPreview;

/// <summary>プローブのエントリポイント。</summary>
public static class Program
{
    // ── 描画の寸法（マジックナンバー回避）────────────────────

    /// <summary>描画するパネルの幅（px。下段のドッキングを横いっぱいに広げた想定）。</summary>
    private const int PanelWidthPx = 1180;

    /// <summary>描画するパネルの高さ（px）。</summary>
    private const int PanelHeightPx = 520;

    /// <summary>PNG の DPI（WPF の既定と同じ 96）。</summary>
    private const double RenderDpi = 96.0;

    /// <summary>Dispatcher のキューを空にするために回す回数。</summary>
    private const int DispatcherPumpCount = 8;

    /// <summary>出力先を指定するオプション名。</summary>
    private const string OPTION_OUT = "--out";

    /// <summary>出力先を指定しなかったときの既定（一時フォルダの下。リポジトリには書かない）。</summary>
    private const string DEFAULT_OUT_DIR = "localization_panel_preview";

    /// <summary>リポジトリの templates/ を探すとき、実行ファイルから何階層まで遡るか。</summary>
    private const int RepositorySearchDepth = 10;

    /// <summary>見本に足す言語（未訳の升目を見せるため）。</summary>
    private const string ExtraIndexJson =
        "{\n" +
        "  \"default\": \"ja\",\n" +
        "  \"languages\": [\n" +
        "    { \"code\": \"ja\", \"name\": \"日本語\", \"fallback\": null, \"culture\": \"ja-JP\" },\n" +
        "    { \"code\": \"en\", \"name\": \"English\", \"fallback\": \"ja\", \"culture\": \"en-US\" },\n" +
        "    { \"code\": \"fr\", \"name\": \"Français\", \"fallback\": \"en\" }\n" +
        "  ]\n" +
        "}\n";

    /// <summary>足す言語の表（訳しかけ。null と空の文字列も入れる）。</summary>
    private const string FrenchJson =
        "{\n" +
        "  \"ui\": {\n" +
        "    \"dialog\": {\n" +
        "      \"ok\": \"OK\",\n" +
        "      \"cancel\": \"Annuler\",\n" +
        "      \"yes\": null,\n" +
        "      \"no\": \"\"\n" +
        "    }\n" +
        "  }\n" +
        "}\n";

    /// <summary>表明の失敗数。</summary>
    private static int _failures;

    /// <summary>
    /// 各状態を順に描画して PNG へ書き出す。
    /// </summary>
    /// <param name="args">コマンドライン引数（--out &lt;出力先&gt;）。</param>
    /// <returns>終了コード（全部書けて表明が通ったら 0）。</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var outDir = ResolveOutDir(args);
        Directory.CreateDirectory(outDir);
        var templates = FindTemplateLocale();
        if (templates is null)
        {
            Console.WriteLine("  [FAIL] リポジトリの templates/locale が見つかりません");
            return 1;
        }

        // App を生成すると App.xaml のリソース（共通ボタン書式）が載る。Run() は呼ばない。
        var app = new SEEDEditor.App();
        app.InitializeComponent();

        // ── 一時フォルダへ見本を写し、未訳の多い fr を足す（リポジトリのファイルには触らない）──
        string work = Path.Combine(Path.GetTempPath(), "seed_l10n_probe_" + Guid.NewGuid().ToString("N"));
        string assets = Path.Combine(work, "assets");
        string locale = Path.Combine(assets, "locale");
        Directory.CreateDirectory(locale);
        foreach (var file in Directory.GetFiles(templates)) File.Copy(file, Path.Combine(locale, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(locale, "index.json"), ExtraIndexJson);
        File.WriteAllText(Path.Combine(locale, "fr.json"), FrenchJson);

        try
        {
            var panel = new LocalizationPanel();
            string? lastTitle = null;
            panel.TitleChanged += title => lastTitle = title;
            panel.SetAssetsPath(assets);
            Layout(panel);

            int allRows = RowCount(panel);
            Render(outDir, "01_table", panel, () => SelectRow(panel, 0), () =>
            {
                Expect(Find<DataGrid>(panel, "Table").Columns.Count == 5, "01: 列はキー・種類・ja・en・fr");
                Expect(Find<TextBlock>(panel, "TxtSummary").Text.StartsWith("未訳 ", StringComparison.Ordinal), "01: 未訳の要約が出る");
                Expect(Find<Border>(panel, "MissingView").Visibility != Visibility.Visible, "01: 置き場の案内は出ない");
                Expect(Find<TextBlock>(panel, "TxtDetails").Text.Contains("_about"), "01: 選んだ行の説明（_about）が出る");
                Expect(!Find<Button>(panel, "BtnSave").IsEnabled, "01: 変更が無いので保存は押せない");
            });

            Render(outDir, "02_missing_only", panel, () => Find<CheckBox>(panel, "ChkMissingOnly").IsChecked = true, () =>
            {
                int shown = RowCount(panel);
                Expect(shown > 0 && shown < allRows, $"02: 未訳の行だけ（{shown} / {allRows} 行）");
            });

            Render(outDir, "03_search", panel, () =>
            {
                Find<CheckBox>(panel, "ChkMissingOnly").IsChecked = false;
                Find<TextBox>(panel, "TxtSearch").Text = "dialog";
            }, () =>
            {
                Expect(RowCount(panel) == 9, $"03: dialog のキーだけ（{RowCount(panel)} 行）");
            });

            Render(outDir, "04_dirty", panel, () =>
            {
                Find<TextBox>(panel, "TxtSearch").Text = string.Empty;
                var row = Rows(panel).First(r => r.Key == "ui.dialog.yes");
                row.Cells[2].Text = "Oui";   // fr の升目を書き換える（DataGrid の編集の確定と同じ setter）
            }, () =>
            {
                Expect(panel.HasUnsavedChanges, "04: 未保存になる");
                Expect(lastTitle is not null && lastTitle.EndsWith("*", StringComparison.Ordinal), $"04: タブ名に *（{lastTitle}）");
                Expect(Find<Button>(panel, "BtnSave").IsEnabled, "04: 保存が押せる");
                Expect(!Rows(panel).First(r => r.Key == "ui.dialog.yes").Cells[2].IsMissing, "04: 書いた升目は未訳でなくなる");
            });
            panel.StopWatching();

            // ── 置き場の無いプロジェクト ──
            var empty = new LocalizationPanel();
            string emptyAssets = Path.Combine(work, "empty_assets");
            Directory.CreateDirectory(emptyAssets);
            empty.SetAssetsPath(emptyAssets);
            Render(outDir, "05_missing_folder", empty, () => { }, () =>
            {
                Expect(Find<Border>(empty, "MissingView").Visibility == Visibility.Visible, "05: 「見本から作る」の案内が出る");
                Expect(Find<DataGrid>(empty, "Table").Visibility != Visibility.Visible, "05: 表は出ない");
            });
            empty.StopWatching();
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* 後片付けの失敗は結果に関係しない */ }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "すべての表明が通りました" : $"表明の失敗 {_failures} 件");
        return _failures == 0 ? 0 : 1;
    }

    // ============================================================
    //  描画
    // ============================================================

    /// <summary>状態を作って PNG へ書き出し、表明する。</summary>
    private static void Render(string outDir, string name, LocalizationPanel panel, Action prepare, Action verify)
    {
        try
        {
            prepare();
            Layout(panel);

            var bitmap = new RenderTargetBitmap(PanelWidthPx, PanelHeightPx, RenderDpi, RenderDpi, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var file = Path.Combine(outDir, name + ".png");
            using (var stream = File.Create(file)) encoder.Save(stream);

            Console.WriteLine($"  書き出しました: {file}");
            Console.WriteLine($"    行 {RowCount(panel)} / 要約「{Find<TextBlock>(panel, "TxtSummary").Text}」 / 題「{panel.Title}」");
            verify();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"  [FAIL] {name}: {ex}");
        }
    }

    /// <summary>パネルを決まった大きさで測って並べ、Dispatcher を回す（テンプレート・バインド・行の生成を解決させる）。</summary>
    private static void Layout(FrameworkElement panel)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            panel.Measure(new Size(PanelWidthPx, PanelHeightPx));
            panel.Arrange(new Rect(0, 0, PanelWidthPx, PanelHeightPx));
            panel.UpdateLayout();
            Pump();
        }
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

    /// <summary>名前付きの部品を引く（見つからなければ例外＝XAML の名前が変わった合図）。</summary>
    private static T Find<T>(LocalizationPanel panel, string name) where T : class =>
        panel.FindName(name) as T ?? throw new InvalidOperationException($"{name} が見つかりません");

    /// <summary>表に出ている行。</summary>
    private static LocaleGridRow[] Rows(LocalizationPanel panel) =>
        (Find<DataGrid>(panel, "Table").ItemsSource as IEnumerable)?.OfType<LocaleGridRow>().ToArray() ?? Array.Empty<LocaleGridRow>();

    /// <summary>表に出ている行の数。</summary>
    private static int RowCount(LocalizationPanel panel) => Rows(panel).Length;

    /// <summary>行を選ぶ。</summary>
    private static void SelectRow(LocalizationPanel panel, int index)
    {
        var grid = Find<DataGrid>(panel, "Table");
        var rows = Rows(panel);
        if (index < rows.Length) grid.SelectedItem = rows[index];
    }

    /// <summary>表明（失敗は数えて最後に終了コードへ反映する）。</summary>
    private static void Expect(bool condition, string what)
    {
        Console.WriteLine((condition ? "    [ OK ] " : "    [FAIL] ") + what);
        if (!condition) _failures++;
    }

    /// <summary>--out の値（無ければ一時フォルダの下）。</summary>
    private static string ResolveOutDir(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], OPTION_OUT, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[i + 1]);
        return Path.Combine(Path.GetTempPath(), DEFAULT_OUT_DIR);
    }

    /// <summary>実行ファイルから遡って、リポジトリの templates/locale を探す。</summary>
    private static string? FindTemplateLocale()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < RepositorySearchDepth && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "templates", "locale");
            if (File.Exists(Path.Combine(candidate, "index.json"))) return candidate;
        }
        return null;
    }
}
