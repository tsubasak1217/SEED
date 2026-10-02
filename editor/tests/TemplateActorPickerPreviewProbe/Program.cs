// ============================================================
//  Program.cs — テンプレートアクタの窓のオフスクリーン描画プローブ
//
//  【何をするか】
//  実物の TemplateActorPickerWindow（XAML）を **表示せずに** 組み立て、同梱のカタログ
//  （templates/*/template_actors.json）を当てた各状態を PNG へ書き出す。
//    01_all            … 「すべて」・先頭を選択（追加先 = ルート）。2026-10-02 から同梱のカタログの全件に見本の画像
//                        （templates/*/thumbnails/。editor/tools/SeedTemplateThumbnails が作る）があるので、
//                        画像入りの一覧になり、全件に画像があることも表明する（足したエントリの撮り忘れに気付ける）
//    02_search         … 検索欄に「ぼたん」（かなの区別なしで「ボタン」が引ける）
//    03_category       … 左の木で「UI › 入力」を選択
//    04_reject         … 追加先が Canvas の無い 3D の子で、2D の部品を選択（「追加」が押せない）
//    05_empty          … 当たらない検索（空の案内）
//    06_thumbnail      … 見本の画像を 1 枚だけ置いた仮のライブラリ（画像の枠に絵が入る）
//  あわせて、状態ごとに「追加」の押せる／押せない・状態の行・一覧の件数を標準出力へ出して表明する。
//
//  【なぜ窓を出さないのか】
//  エディタ（SEEDEditor.exe）は起動しない約束であり、利用者の画面にも何も出さない。
//  窓の Content（ルートの Grid）は表示前はどの親の見た目にも入っていないので、
//  そのまま測って描ける。暗黙のスタイル（一覧・木の行）は論理ツリーの親＝窓のリソースから引かれる。
//
//  【画像はリポジトリに作らない】
//  06 の見本の画像は、ライブラリの UI 部分を一時フォルダへ写した「仮のライブラリ」にだけ描く。
//
//  使い方:
//    dotnet run --project editor/tests/TemplateActorPickerPreviewProbe -- --out <出力先>
// ============================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SEEDEditor.Templates;
using SEEDEditor.Templates.Actors;

namespace SEEDEditor.Tests.TemplateActorPickerPreview;

/// <summary>プローブのエントリポイント。</summary>
public static class Program
{
    // ── 描画の寸法（マジックナンバー回避）────────────────────

    /// <summary>描画する窓の中身の幅（px。窓の既定の幅から枠の分を引いた値）。</summary>
    private const int ContentWidthPx = 784;

    /// <summary>描画する窓の中身の高さ（px）。</summary>
    private const int ContentHeightPx = 521;

    /// <summary>PNG の DPI（WPF の既定と同じ 96）。</summary>
    private const double RenderDpi = 96.0;

    /// <summary>Dispatcher のキューを空にするために回す回数。</summary>
    private const int DispatcherPumpCount = 8;

    /// <summary>仮のライブラリに描く見本の画像の一辺（規約の推奨 192）。</summary>
    private const int ThumbnailSizePx = TemplateActorCatalogFormat.RecommendedThumbnailSizePx;

    /// <summary>見本の画像の角丸の半径（px）。</summary>
    private const double ThumbnailCornerRadius = 32;

    /// <summary>見本の画像に書く文字の大きさ（px）。</summary>
    private const double ThumbnailLabelSize = 44;

    /// <summary>見本の画像の文字の 1 DIP あたりの画素数（96 DPI で描くので 1）。</summary>
    private const double PixelsPerDip = 1.0;

    /// <summary>見本の画像の地色（プローブの絵なのでテーマ色と関係ない）。</summary>
    private static readonly Color ThumbnailFill = Color.FromRgb(0x33, 0x1B, 0xFF);

    /// <summary>出力先を指定するオプション名。</summary>
    private const string OPTION_OUT = "--out";

    /// <summary>出力先を指定しなかったときの既定（実行ファイルの隣）。</summary>
    private const string DEFAULT_OUT_DIR = "template_actor_picker_preview";

    /// <summary>ライブラリを探すとき、実行ファイルから何階層まで遡るか。</summary>
    private const int RepositorySearchDepth = 10;

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

        var libraryRoot = FindLibrary();
        if (libraryRoot is null)
        {
            Console.WriteLine("  [FAIL] リポジトリの templates/（カタログ付き）が見つかりません");
            return 1;
        }

        // App を生成すると App.xaml のリソース（共通ボタン書式・ツリーの三角）が載る。Run() は呼ばない。
        var app = new SEEDEditor.App();
        app.InitializeComponent();

        var catalog = TemplateActorCatalog.Load(libraryRoot);
        Console.WriteLine($"カタログ: {catalog.Entries.Count} 件 / カテゴリ {catalog.Categories.Count} / 警告 {catalog.Warnings.Count}");

        var root    = new TemplateActorTarget();
        var plain3D = new TemplateActorTarget { ParentDfs = 4, ParentName = "Player", ParentIs2D = false };

        // ── 01〜05: 同梱のライブラリで状態を作る ─────────────────
        Render(outDir, "01_all", catalog, root, w => { },
               w => Expect(Find<TextBlock>(w, "LblStatus").Text.Contains($"見本の画像あり {catalog.Entries.Count} 件"),
                           "01: 同梱のカタログの全件に見本の画像がある（無ければ SeedTemplateThumbnails で撮る）"));
        Render(outDir, "02_search", catalog, root, w => Find<TextBox>(w, "TxtSearch").Text = "ぼたん",
               w => Expect(ListCount(w) >= 2, "02: 「ぼたん」でボタンと丸ボタンが引ける"));
        Render(outDir, "03_category", catalog, root, w => SelectCategory(w, "UI/入力"),
               w => Expect(ListCount(w) == catalog.Entries.Count(e => e.CategoryKey == "UI/入力"), "03: 入力のカテゴリだけが出る"));
        Render(outDir, "04_reject", catalog, plain3D, w => Find<ListBox>(w, "ListActors").SelectedIndex = 0,
               w =>
               {
                   Expect(!Find<Button>(w, "BtnAdd").IsEnabled, "04: Canvas の無い 3D の子に 2D は「追加」が押せない");
                   Expect(Find<TextBlock>(w, "LblStatus").Text.Contains("Canvas"), "04: 状態の行に理由が出る");
               });
        Render(outDir, "05_empty", catalog, root, w => Find<TextBox>(w, "TxtSearch").Text = "zzzz-no-such",
               w => Expect(Find<TextBlock>(w, "LblEmpty").Visibility == Visibility.Visible, "05: 当たらないときの案内が出る"));

        // ── 06: 見本の画像を 1 枚置いた仮のライブラリ ───────────────
        var tempLibrary = BuildTemporaryLibraryWithThumbnail(libraryRoot, outDir);
        var tempCatalog = TemplateActorCatalog.Load(tempLibrary);
        Render(outDir, "06_thumbnail", tempCatalog, root, w => { },
               w => Expect(Find<TextBlock>(w, "LblStatus").Text.Contains("見本の画像あり 1 件"), "06: 置いた画像が数えられる"));

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "すべての表明が通りました" : $"表明の失敗 {_failures} 件");
        return _failures == 0 ? 0 : 1;
    }

    // ============================================================
    //  描画
    // ============================================================

    /// <summary>
    /// 窓を組み立て、状態を作って PNG へ書き出す。
    /// </summary>
    /// <param name="outDir">出力先。</param>
    /// <param name="name">ファイル名（拡張子なし）。</param>
    /// <param name="catalog">当てるカタログ。</param>
    /// <param name="target">追加先。</param>
    /// <param name="prepare">状態を作る処理。</param>
    /// <param name="verify">描いた後の表明（省略可）。</param>
    private static void Render(
        string outDir, string name, TemplateActorCatalog catalog, TemplateActorTarget target,
        Action<TemplateActorPickerWindow> prepare, Action<TemplateActorPickerWindow>? verify = null)
    {
        try
        {
            var window = TemplateActorPickerWindow.CreateForVerification(FakeContext(catalog.LibraryRoot), target);
            window.ApplyCatalogForVerification(catalog);

            var content = (FrameworkElement)window.Content;
            Layout(content);
            prepare(window);
            Layout(content);

            var bitmap = new RenderTargetBitmap(ContentWidthPx, ContentHeightPx, RenderDpi, RenderDpi, PixelFormats.Pbgra32);
            // 窓の地色は窓自身が塗るので、描く前に中身の後ろへ同じ色を敷く
            var background = new DrawingVisual();
            using (var dc = background.RenderOpen())
                dc.DrawRectangle(window.Background, null, new Rect(0, 0, ContentWidthPx, ContentHeightPx));
            bitmap.Render(background);
            bitmap.Render(content);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var file = Path.Combine(outDir, name + ".png");
            using (var stream = File.Create(file)) encoder.Save(stream);

            Console.WriteLine($"  書き出しました: {file}");
            Console.WriteLine($"    一覧 {ListCount(window)} 件 / 追加 {(Find<Button>(window, "BtnAdd").IsEnabled ? "押せる" : "押せない")} / " +
                              $"状態「{Find<TextBlock>(window, "LblStatus").Text}」");
            verify?.Invoke(window);
            window.Close();
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"  [FAIL] {name}: {ex}");
        }
    }

    /// <summary>中身を窓の大きさで測って並べ、Dispatcher を回す（テンプレートとバインドを解決させる）。</summary>
    private static void Layout(FrameworkElement content)
    {
        content.Measure(new Size(ContentWidthPx, ContentHeightPx));
        content.Arrange(new Rect(0, 0, ContentWidthPx, ContentHeightPx));
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
    //  状態を作る小道具
    // ============================================================

    /// <summary>名前付きの部品を引く（見つからなければ例外＝XAML の名前が変わった合図）。</summary>
    private static T Find<T>(TemplateActorPickerWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"{name} が見つかりません");

    /// <summary>一覧の件数。</summary>
    private static int ListCount(TemplateActorPickerWindow window) =>
        Find<ListBox>(window, "ListActors").Items.Count;

    /// <summary>左の木でカテゴリを選ぶ（表示モデルの IsSelected を立てると木の選択が動く）。</summary>
    private static void SelectCategory(TemplateActorPickerWindow window, string key)
    {
        var tree = Find<TreeView>(window, "TreeCategories");
        foreach (var item in Flatten(tree.ItemsSource))
            if (item.Key == key) item.IsSelected = true;
    }

    /// <summary>左の木の行を 2 段まで平らにする。</summary>
    private static IEnumerable<TemplateActorCategoryItem> Flatten(IEnumerable? items)
    {
        foreach (var item in items?.OfType<TemplateActorCategoryItem>() ?? [])
        {
            yield return item;
            foreach (var child in item.Children) yield return child;
        }
    }

    /// <summary>表明（失敗は数えて最後に終了コードへ反映する）。</summary>
    private static void Expect(bool condition, string what)
    {
        Console.WriteLine((condition ? "    [ OK ] " : "    [FAIL] ") + what);
        if (!condition) _failures++;
    }

    /// <summary>ランタイムにもプロジェクトにも触れない偽物の外部機能。</summary>
    private static TemplateActorPickerContext FakeContext(string libraryRoot) => new()
    {
        LibraryRoot    = libraryRoot,
        AssetsRoot     = () => Path.GetTempPath(),
        RefreshTarget  = t => new TemplateActorTargetRefresh(t, null),
        ReadOnlyReason = () => null,
        SendToRuntime  = command => Console.WriteLine($"    （送信せず）{command}"),
        FilesCopied    = _ => { },
        Log            = line => Console.WriteLine($"    log: {line}"),
    };

    // ============================================================
    //  仮のライブラリ（見本の画像 1 枚）
    // ============================================================

    /// <summary>
    /// ライブラリの UI 部分（カタログと prefabs）を出力先の下へ写し、ボタンの見本の画像を 1 枚描く。
    /// リポジトリの templates/ には何も書かない。
    /// </summary>
    /// <returns>仮のライブラリのルート。</returns>
    private static string BuildTemporaryLibraryWithThumbnail(string libraryRoot, string outDir)
    {
        var tempRoot = Path.Combine(outDir, "temp_library");
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);

        var srcUi = Path.Combine(libraryRoot, "ui");
        var dstUi = Path.Combine(tempRoot, "ui");
        Directory.CreateDirectory(Path.Combine(dstUi, "prefabs"));
        File.Copy(Path.Combine(srcUi, TemplateLibraryMetadata.TemplateActorCatalogFileName),
                  Path.Combine(dstUi, TemplateLibraryMetadata.TemplateActorCatalogFileName));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(srcUi, "prefabs")))
            File.Copy(file, Path.Combine(dstUi, "prefabs", Path.GetFileName(file)));

        var thumbDir = Path.Combine(dstUi, TemplateLibraryMetadata.ThumbnailFolderName);
        Directory.CreateDirectory(thumbDir);
        WriteSampleThumbnail(Path.Combine(thumbDir, "button" + TemplateActorCatalogFormat.ThumbnailExtension));
        return tempRoot;
    }

    /// <summary>角丸の青い板に「BTN」と書いた 192×192 の PNG を描く（見本の画像の代わり）。</summary>
    private static void WriteSampleThumbnail(string path)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(ThumbnailFill), null,
                new Rect(0, 0, ThumbnailSizePx, ThumbnailSizePx), ThumbnailCornerRadius, ThumbnailCornerRadius);
            var text = new FormattedText("BTN", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), ThumbnailLabelSize, Brushes.White, PixelsPerDip);
            dc.DrawText(text, new Point((ThumbnailSizePx - text.Width) / 2, (ThumbnailSizePx - text.Height) / 2));
        }
        var bitmap = new RenderTargetBitmap(ThumbnailSizePx, ThumbnailSizePx, RenderDpi, RenderDpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ============================================================
    //  場所
    // ============================================================

    /// <summary>--out の値を解決する（無ければ実行ファイルの隣）。</summary>
    private static string ResolveOutDir(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], OPTION_OUT, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[i + 1]);
        return Path.GetFullPath(DEFAULT_OUT_DIR);
    }

    /// <summary>実行ファイルから遡って、リポジトリの templates/（カタログ付き）を探す。</summary>
    private static string? FindLibrary()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < RepositorySearchDepth && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, TemplateLibraryLocator.LibraryFolderName);
            if (File.Exists(Path.Combine(candidate, "ui", TemplateLibraryMetadata.TemplateActorCatalogFileName)))
                return candidate;
        }
        return null;
    }
}
