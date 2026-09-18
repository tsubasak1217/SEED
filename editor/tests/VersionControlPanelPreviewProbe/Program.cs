// ============================================================
//  Program.cs — バージョン管理パネルのオフスクリーン描画プローブ
//
//  【何をするか】
//  実物の VersionControlPanel（XAML）を **ウィンドウを出さずに** 組み立て、
//  偽のプロバイダで作った各状態を PNG へ書き出す。
//    1. changes  … 変更あり（フォルダー階層のツリー）
//    2. conflicts… 競合あり（競合の節が最上部に出る）
//    3. locks    … ロックあり（ロックの節を開いた状態）
//    4. history  … 履歴あり（履歴の節を開いた状態）
//    5. clean    … 変更なし
//    6. unavailable … バージョン管理下に無いプロジェクト
//
//  【なぜウィンドウを出さないのか】
//  エディタ（SEEDEditor.exe）は起動しない約束であり、CI でも動かしたい。
//  Loaded は「表示されたとき」に飛ぶ routed event なので、ここでは
//  手で発火させて初期化の経路をそのまま通す。
//
//  【Lore には触れない】
//  VersionControlService.UseProviderForVerification に偽物を据えるため、
//  ネイティブの lorelib.dll もサーバも一切呼ばれない。
//
//  使い方:
//    dotnet run --project editor/tests/VersionControlPanelPreviewProbe -- --out <出力先>
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SEEDEditor.Panels;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.Tests.VersionControlPanelPreview;

/// <summary>
/// プローブのエントリポイント。
/// </summary>
public static class Program
{
    // ── 描画の寸法（マジックナンバー回避）────────────────────

    /// <summary>描画するパネルの幅（px）。ドッキングした既定の横幅に近い値。</summary>
    private const int PanelWidthPx = 460;

    /// <summary>描画するパネルの高さ（px）。</summary>
    private const int PanelHeightPx = 780;

    /// <summary>PNG の DPI（WPF の既定と同じ 96）。</summary>
    private const double RenderDpi = 96.0;

    /// <summary>Dispatcher のキューを空にするために回す回数。</summary>
    private const int DispatcherPumpCount = 8;

    /// <summary>ダイアログの組み立て確認で渡す計測幅（px）。表示はしないので目安でよい。</summary>
    private const double DialogProbeWidthPx = 420;

    /// <summary>ダイアログの組み立て確認で渡す計測高さ（px）。</summary>
    private const double DialogProbeHeightPx = 340;

    /// <summary>出力先を指定するオプション名。</summary>
    private const string OPTION_OUT = "--out";

    /// <summary>出力先を指定しなかったときの既定（実行ファイルの隣）。</summary>
    private const string DEFAULT_OUT_DIR = "vcs_panel_preview";

    /// <summary>
    /// 各状態を順に描画して PNG へ書き出す。
    /// </summary>
    /// <param name="args">コマンドライン引数（--out &lt;出力先&gt;）。</param>
    /// <returns>終了コード（全部書けたら 0）。</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var outDir = ResolveOutDir(args);
        Directory.CreateDirectory(outDir);

        // App を生成すると App.xaml のリソース（共通ボタン書式・アイコン辞書・
        // ツリーの三角）が Application.Current.Resources に載る。Run() は呼ばない。
        // リソースの探索先は ModuleInitializer で本体アセンブリへ向け直してある。
        var app = new SEEDEditor.App();
        app.InitializeComponent();

        var written = 0;
        foreach (var (name, scenario) in Scenarios())
        {
            try
            {
                var file = Path.Combine(outDir, $"{name}.png");
                Render(scenario, file);
                Console.WriteLine($"  書き出しました: {file}");
                written++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [FAIL] {name}: {ex}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{written} 件の PNG を {outDir} に書き出しました。");

        // 「その他 …」メニューはポップアップなので PNG には写らない。
        // 項目が揃っていて文言が入っていることを別に確かめる。
        var menuOk = VerifyMoreMenuItems();

        // ダイアログはモーダルなので描画はしない。**組み立てが通るか**だけを確かめる
        // （共通スタイルのキー間違い・リソース未解決は、ここで初めて例外になる）。
        var dialogsOk = VerifyDialogsLoad();

        return written > 0 && menuOk && dialogsOk ? 0 : 1;
    }

    /// <summary>
    /// ヘッダーの「その他 …」メニューの項目が揃っていて、文言が入っていることを確かめる。
    ///
    /// <para>
    /// メニューはポップアップなのでオフスクリーン描画に写らない。
    /// 文言は <c>VersionControlMessages</c> からコードで差し込んでいるので、
    /// 差し込み漏れがあると**空の項目**として黙って出る。
    /// </para>
    /// </summary>
    /// <returns>期待どおりなら真。</returns>
    private static bool VerifyMoreMenuItems()
    {
        // 期待する項目名（XAML の x:Name）。増減したらここも直す。
        var expected = new[]
        {
            "MenuShowWorkingCopy",
            "MenuReleaseAllLocks",
            "MenuMergeBranch",
            "MenuArchiveBranch",
            "MenuAccounts",
        };

        VersionControlService.UseProviderForVerification(
            new FakeVersionControlProvider(WORKING_COPY, CleanStatus()));

        try
        {
            var panel = new VersionControlPanel { Width = PanelWidthPx, Height = PanelHeightPx };
            panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Pump();

            var ok = true;
            foreach (var name in expected)
            {
                if (panel.FindName(name) is not MenuItem item)
                {
                    Console.WriteLine($"  [FAIL] メニュー項目が見つかりません: {name}");
                    ok = false;
                    continue;
                }

                var header = item.Header as string;
                if (string.IsNullOrWhiteSpace(header))
                {
                    Console.WriteLine($"  [FAIL] メニュー項目の文言が空です: {name}");
                    ok = false;
                    continue;
                }

                Console.WriteLine($"  メニュー項目: {name} = 「{header}」");
            }

            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] メニューの確認で例外: {ex}");
            return false;
        }
        finally
        {
            VersionControlService.UseProviderForVerification(null);
        }
    }

    /// <summary>
    /// パネルから開くダイアログが例外なく組み立てられることを確かめる（表示はしない）。
    ///
    /// <para>
    /// XAML の <c>StaticResource</c> は綴りを間違えてもビルドが通り、
    /// 実際に開くまで壊れていることが分からない。ここで 1 度組み立てておけば、
    /// エディタを起動せずに気づける。
    /// </para>
    /// </summary>
    /// <returns>すべて組み立てられたら真。</returns>
    private static bool VerifyDialogsLoad()
    {
        var ok = true;

        foreach (var (name, build) in DialogCases())
        {
            try
            {
                var window = build();

                // 表示せずにレイアウトまで通す（テンプレートの解決もここで起きる）。
                window.Measure(new Size(DialogProbeWidthPx, DialogProbeHeightPx));
                window.Close();

                Console.WriteLine($"  ダイアログを組み立てました: {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [FAIL] ダイアログ {name}: {ex}");
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>組み立てを確かめるダイアログ（名前と作り方）。</summary>
    private static IEnumerable<(string Name, Func<Window> Build)> DialogCases()
    {
        var branches = new[] { "main", "feature/fishing", "old-experiment" };

        yield return ("ブランチのマージ", () => new SEEDEditor.Dialogs.BranchPickerWindow(
            VersionControlMessages.PANEL_BRANCH_MERGE_DIALOG_TITLE,
            string.Format(VersionControlMessages.PANEL_BRANCH_MERGE_DIALOG_PROMPT, "main"),
            branches,
            VersionControlMessages.PANEL_BRANCH_MERGE_DIALOG_NOTE));

        yield return ("ブランチの削除（アーカイブ）", () => new SEEDEditor.Dialogs.BranchPickerWindow(
            VersionControlMessages.PANEL_BRANCH_ARCHIVE_DIALOG_TITLE,
            VersionControlMessages.PANEL_BRANCH_ARCHIVE_DIALOG_PROMPT,
            branches,
            VersionControlMessages.PANEL_BRANCH_ARCHIVE_DIALOG_NOTE));

        // 補足なしでも組み立てられること（省略時の経路を踏む）。
        yield return ("ブランチ選択（補足なし）", () => new SEEDEditor.Dialogs.BranchPickerWindow(
            VersionControlMessages.PANEL_BRANCH_MERGE_DIALOG_TITLE,
            VersionControlMessages.PANEL_BRANCH_ARCHIVE_DIALOG_PROMPT,
            branches));
    }

    /// <summary>--out の値を解決する（無ければ実行ファイルの隣）。</summary>
    /// <param name="args">コマンドライン引数。</param>
    private static string ResolveOutDir(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], OPTION_OUT, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        return Path.GetFullPath(DEFAULT_OUT_DIR);
    }

    // ============================================================
    //  描画
    // ============================================================

    /// <summary>
    /// 1 つの状態を描画して PNG へ書き出す。
    /// </summary>
    /// <param name="scenario">据えるプロバイダと、描画前にパネルへ行う仕込み。</param>
    /// <param name="filePath">出力先の PNG。</param>
    private static void Render(Scenario scenario, string filePath)
    {
        VersionControlService.UseProviderForVerification(scenario.Provider);

        var panel = new VersionControlPanel
        {
            Width  = PanelWidthPx,
            Height = PanelHeightPx,
        };

        // Loaded は「画面に出たとき」に飛ぶ。ウィンドウを出さないので手で発火させ、
        // パネル本来の初期化経路（購読・状態取得・節の復元）をそのまま通す。
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

        Pump();

        // 節を開くなどの仕込みは、初期化が終わってから行う。
        scenario.Prepare?.Invoke(panel);
        Pump();

        panel.Measure(new Size(PanelWidthPx, PanelHeightPx));
        panel.Arrange(new Rect(0, 0, PanelWidthPx, PanelHeightPx));
        panel.UpdateLayout();
        Pump();

        var bitmap = new RenderTargetBitmap(
            PanelWidthPx, PanelHeightPx, RenderDpi, RenderDpi, PixelFormats.Pbgra32);
        bitmap.Render(panel);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(filePath);
        encoder.Save(stream);

        // 次の状態へ移る前に、据えた偽物を外す。
        VersionControlService.UseProviderForVerification(null);
    }

    /// <summary>
    /// Dispatcher に溜まった処理（BeginInvoke・レイアウト）を流し切る。
    /// </summary>
    private static void Pump()
    {
        for (var i = 0; i < DispatcherPumpCount; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(
                () => { }, DispatcherPriority.ContextIdle);
        }
    }

    /// <summary>節の見出しのトグルを押した状態にする（開いて中身も取りに行かせる）。</summary>
    /// <param name="panel">対象のパネル。</param>
    /// <param name="headerName">見出しトグルの名前（XAML の x:Name）。</param>
    private static void ExpandSection(VersionControlPanel panel, string headerName)
    {
        if (panel.FindName(headerName) is not ToggleButton toggle) return;

        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    // ============================================================
    //  状態（シナリオ）
    // ============================================================

    /// <summary>1 つの描画対象（据えるプロバイダと仕込み）。</summary>
    /// <param name="Provider">据える偽プロバイダ（null ならバージョン管理なし）。</param>
    /// <param name="Prepare">描画前にパネルへ行う仕込み（不要なら null）。</param>
    private sealed record Scenario(
        SEEDEditor.VersionControl.Abstractions.IVersionControlProvider? Provider,
        Action<VersionControlPanel>? Prepare = null);

    /// <summary>作業コピーのパス（ツリーの根に出る）。</summary>
    private const string WORKING_COPY = @"D:\SEED_projects\WarashibeFishing";

    /// <summary>描画する状態を順に返す。</summary>
    private static IEnumerable<(string Name, Scenario Scenario)> Scenarios()
    {
        yield return ("changes", new Scenario(
            new FakeVersionControlProvider(
                WORKING_COPY,
                new WorkingCopyStatus(
                    "Warashibe_Fishing", 42UL, ManyChanges(),
                    RemoteComparison.LocalAhead, StatusRefreshMode.ScanOnline))));

        yield return ("conflicts", new Scenario(
            new FakeVersionControlProvider(
                WORKING_COPY,
                new WorkingCopyStatus(
                    "Warashibe_Fishing", 42UL, WithConflicts(),
                    RemoteComparison.Diverged, StatusRefreshMode.ScanOnline))));

        yield return ("locks", new Scenario(
            new FakeVersionControlProvider(
                WORKING_COPY,
                new WorkingCopyStatus(
                    "Warashibe_Fishing", 42UL, ManyChanges(),
                    RemoteComparison.InSync, StatusRefreshMode.ScanOnline),
                locks: SampleLocks()),
            panel => ExpandSection(panel, "HeaderLocks")));

        yield return ("history", new Scenario(
            new FakeVersionControlProvider(
                WORKING_COPY,
                new WorkingCopyStatus(
                    "Warashibe_Fishing", 42UL, Array.Empty<ChangedFile>(),
                    RemoteComparison.RemoteAhead, StatusRefreshMode.ScanOnline),
                history: SampleHistory()),
            panel => ExpandSection(panel, "HeaderHistory")));

        yield return ("clean", new Scenario(
            new FakeVersionControlProvider(WORKING_COPY, CleanStatus())));

        // プロバイダ無し＝「このプロジェクトはバージョン管理されていません」の画面。
        yield return ("unavailable", new Scenario(null));
    }

    /// <summary>変更が 1 件も無い状態（「clean」の描画とメニューの確認で共用する）。</summary>
    private static WorkingCopyStatus CleanStatus()
        => new("Warashibe_Fishing", 42UL, Array.Empty<ChangedFile>(),
               RemoteComparison.NotChecked, StatusRefreshMode.ScanOffline);

    /// <summary>フォルダー階層が分かるだけの数の変更を作る。</summary>
    private static IReadOnlyList<ChangedFile> ManyChanges() => new[]
    {
        Change("assets/scenes/proLogue.scene",              FileChangeKind.Modified),
        Change("assets/scenes/title.scene",                 FileChangeKind.Added),
        Change("assets/textures/ui/button_normal.png",      FileChangeKind.Modified),
        Change("assets/textures/ui/button_hover.png",       FileChangeKind.Added),
        Change("assets/textures/fish/sardine.png",          FileChangeKind.Added),
        Change("assets/textures/fish/old_sardine.png",      FileChangeKind.Deleted),
        Change("assets/scripts/FishingRod.cs",              FileChangeKind.Modified),
        Change("assets/scripts/LineTension.cs",             FileChangeKind.Added),
        Change("assets/audio/bgm/sea.ogg",                  FileChangeKind.Added),
        Change("assets/materials/water.mat",                FileChangeKind.Modified),
        Change("docs/fishing_spec.md",                      FileChangeKind.Moved, "docs/spec.md"),
        Change("project.seedproj",                          FileChangeKind.Modified),
    };

    /// <summary>未解決の競合を混ぜた変更を作る。</summary>
    private static IReadOnlyList<ChangedFile> WithConflicts()
    {
        var list = new List<ChangedFile>(ManyChanges())
        {
            Change("assets/scenes/proLogue.scene", FileChangeKind.Modified,
                   conflict: FileConflictState.Unresolved),
            Change("assets/scripts/FishingRod.cs", FileChangeKind.Modified,
                   conflict: FileConflictState.Unresolved),
        };

        // 同じパスが 2 回入らないよう、競合させたものは元の行を落とす。
        list.RemoveAll(c => c.Conflict == FileConflictState.None
                            && (c.Path == "assets/scenes/proLogue.scene"
                                || c.Path == "assets/scripts/FishingRod.cs"));
        return list;
    }

    /// <summary>ロック一覧の例（自分のもの・他の人のもの・所有者不明）。</summary>
    private static IReadOnlyList<LockInfo> SampleLocks() => new[]
    {
        new LockInfo("assets/scenes/proLogue.scene", "tsubasa", LockHolder.Self,
                     "Warashibe_Fishing", DateTime.UtcNow.AddHours(-2)),
        new LockInfo("assets/textures/ui/button_normal.png", "hanako", LockHolder.Other,
                     "Warashibe_Fishing", DateTime.UtcNow.AddHours(-5)),
        new LockInfo("assets/materials/water.mat", LockInfo.UNKNOWN_OWNER, LockHolder.Unknown,
                     "Warashibe_Fishing", DateTime.UtcNow.AddDays(-1)),
    };

    /// <summary>履歴の例。</summary>
    private static IReadOnlyList<RevisionInfo> SampleHistory()
    {
        var list = new List<RevisionInfo>();
        var messages = new[]
        {
            "釣りの糸 HP とテンションゲージを実装",
            "水面のマテリアルを調整",
            "プロローグのシーンを差し替え",
            "UI のボタン画像を更新",
            "魚のスポーン表を追加",
        };

        for (var i = 0; i < messages.Length; i++)
        {
            list.Add(new RevisionInfo(
                (ulong)(42 - i),
                $"{i:x8}0000000000000000000000000000000",
                i % 2 == 0 ? "tsubasa" : "hanako",
                messages[i],
                DateTime.UtcNow.AddDays(-i)));
        }

        return list;
    }

    /// <summary>変更を 1 件作る。</summary>
    /// <param name="path">リポジトリ相対パス。</param>
    /// <param name="kind">変更の種類。</param>
    /// <param name="fromPath">移動・複製の元パス。</param>
    /// <param name="conflict">競合の状態。</param>
    private static ChangedFile Change(
        string path, FileChangeKind kind,
        string fromPath = "", FileConflictState conflict = FileConflictState.None)
        => new(path, kind, conflict, isStaged: true, isDirty: true, fromPath: fromPath);
}
