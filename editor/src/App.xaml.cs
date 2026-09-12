using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using SEEDEditor.Project;
using SEEDEditor.Startup;

namespace SEEDEditor;

/// <summary>
/// SEED エディタの WPF アプリケーションエントリポイント。
///
/// <para>
/// 起動の分岐点でもある。<c>StartupUri</c> は使わず、
/// <see cref="OnStartup"/> が「どのウィンドウを出すか」を決める:
/// </para>
/// <list type="bullet">
///   <item>プロジェクトが確定した … <see cref="MainWindow"/>（エディタ本体）</item>
///   <item>確定できない（引数なし起動）… <see cref="StartWindow"/>（スタート画面）</item>
///   <item>ヘッドレスで確定できない … ログを残して終了コード非 0 で終了</item>
/// </list>
///
/// <para>
/// UI スレッド未処理例外・非 UI スレッド未処理例外・未観測タスク例外を捕捉し
/// crash.log に書き出すのも引き続きここの責務。
/// </para>
/// </summary>
public partial class App : Application
{
    /// <summary>プロジェクトを解決できずヘッドレス終了するときの終了コード。</summary>
    private const int EXIT_CODE_NO_PROJECT = 2;

    /// <summary>プロジェクトファイルを開けずヘッドレス終了するときの終了コード。</summary>
    private const int EXIT_CODE_PROJECT_LOAD_FAILED = 3;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { File.WriteAllText("crash.log", e.ExceptionObject?.ToString() ?? "(null)"); }
            catch { }
        };
        DispatcherUnhandledException += (_, e) =>
        {
            try { File.AppendAllText("crash.log", "DISPATCHER: " + e.Exception?.ToString() ?? "(null)"); }
            catch { }
            e.Handled = false;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { File.AppendAllText("crash.log", "TASK: " + e.Exception?.ToString() ?? "(null)"); }
            catch { }
        };
    }

    /// <summary>
    /// コマンドライン引数（--project / --headless / --scene / --ai-*）を解析し、
    /// 開くプロジェクトを確定させてから最初のウィンドウを表示する。
    ///
    /// <para>
    /// <c>MainWindow</c> は静的フィールド経由で
    /// <see cref="ProjectContext"/> のアセットルートを参照するため、
    /// ウィンドウ生成より前にプロジェクトを確定させる必要がある。
    /// </para>
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        Headless.EditorStartupOptions.Parse(e.Args);

        // AI ブリッジの許可ポリシー（ポート・トークン・読み取り専用既定）を確定させる。
        // MainWindow → AIAssistantPanel がブリッジを起動するより前に済ませておく必要がある。
        AI.AiOperationPolicy.Configure(
            Headless.EditorStartupOptions.AiPort,
            Headless.EditorStartupOptions.AiToken,
            Headless.EditorStartupOptions.IsHeadless);

        // プロジェクト生成の診断ログをエディタのログへ流す（ProjectCreator は
        // 単体テストへリンクできるよう EditorLog を直接参照しない設計）。
        ProjectCreator.Log = EditorLog.Write;

        base.OnStartup(e);

        ShowStartupWindow();
    }

    /// <summary>
    /// 起動時の最初のウィンドウを決めて表示する。
    /// </summary>
    private void ShowStartupWindow()
    {
        var settingsDir = SEEDEditor.Settings.EditorPaths.SettingsDir;
        var recentStore = new RecentProjectsStore(settingsDir);

        var isHeadless = Headless.EditorStartupOptions.IsHeadless;
        var result = ProjectStartupResolver.Resolve(
            Headless.EditorStartupOptions.ProjectFilePath,
            isHeadless,
            recentStore.Load().Select(entry => entry.Path));

        switch (result.Kind)
        {
            case ProjectStartupKind.OpenProject:
                OpenProjectAndShowEditor(result.ProjectFilePath!, recentStore, isHeadless);
                return;

            case ProjectStartupKind.ShowStartWindow:
                // ここで 1 行書くことには診断上の意味がある。スタート画面の経路では
                // 他に EditorLog を触る処理が無く、ログファイルすら作られないため。
                EditorLog.Write(result.ErrorMessage is null
                    ? "スタート画面を表示します（プロジェクト指定なし）"
                    : $"スタート画面を表示します — {result.ErrorMessage}");
                // スタート画面でもジャンプリストを最新にしておく（前回消えたプロジェクトを落とす）。
                ProjectJumpList.Refresh(recentStore);
                new StartWindow(result.ErrorMessage).Show();
                return;

            default:
                EditorLog.Write($"起動を中止します: {result.ErrorMessage}");
                Shutdown(EXIT_CODE_NO_PROJECT);
                return;
        }
    }

    /// <summary>
    /// プロジェクトを確定させてエディタ本体を開く。
    /// </summary>
    /// <param name="projectFilePath">開く .seedproj の絶対パス。</param>
    /// <param name="recentStore">最近のプロジェクト一覧（開いた記録を残す）。</param>
    /// <param name="isHeadless">ヘッドレス起動か（失敗時に画面を出せるか）。</param>
    private void OpenProjectAndShowEditor(
        string projectFilePath, RecentProjectsStore recentStore, bool isHeadless)
    {
        ProjectPaths paths;
        try
        {
            paths = ProjectContext.OpenFromFile(projectFilePath);
        }
        catch (Exception ex)
        {
            // .seedproj が壊れている／読めない。ヘッドレスは終了、通常はスタート画面へ。
            EditorLog.Write($"プロジェクトを開けませんでした: {ex.Message}");
            if (isHeadless)
            {
                Shutdown(EXIT_CODE_PROJECT_LOAD_FAILED);
                return;
            }
            new StartWindow(ex.Message).Show();
            return;
        }

        // 開けたものだけを最近の一覧へ記録する（壊れたパスを積み上げない）。
        try { recentStore.Add(paths.ProjectFilePath, paths.DisplayName); }
        catch (Exception ex) { EditorLog.Write($"最近のプロジェクトを更新できませんでした: {ex.Message}"); }
        // タスクバーのジャンプリスト（右クリックの「最近」欄）にも同じ一覧を反映する。
        ProjectJumpList.Refresh(recentStore);

        // MainWindow の生成〜初回描画までは UI スレッドが塞がり、ウィンドウが真っ白のまま
        // 数秒止まる。その間は別ウィンドウのスプラッシュ（起動中画像）で覆う。
        // SplashScreen は自前のレイヤードウィンドウに 1 回描くだけなので、UI スレッドが
        // 塞がっていても表示が保たれる。ヘッドレス（エージェント運用）では出さない。
        var splash = isHeadless ? null : ShowStartupSplash();

        // 型名 MainWindow と Application.MainWindow プロパティが同名なので、
        // どちらを指しているかが読んで分かるよう明示的に書き分ける。
        var window = new SEEDEditor.MainWindow();
        this.MainWindow = window;
        if (splash is not null)
        {
            // 初回描画が済んだらフェードアウト。万一 ContentRendered が来なくても上限時間で閉じる。
            window.ContentRendered += (_, _) => CloseStartupSplash(splash);
            _ = Task.Delay(STARTUP_SPLASH_MAX_MS).ContinueWith(_ =>
                Dispatcher.BeginInvoke(() => CloseStartupSplash(splash)));
        }
        window.Show();
    }

    // ── 起動スプラッシュ（別ウィンドウ） ─────────────────────────

    /// <summary>スプラッシュ画像のリソース名（csproj の Resource Include で埋め込まれる縮小版）。</summary>
    private const string STARTUP_SPLASH_RESOURCE = "resources/images/blueSky_ORE_splash.png";

    /// <summary>スプラッシュを閉じるときのフェード時間 [ms]。</summary>
    private const int STARTUP_SPLASH_FADE_MS = 250;

    /// <summary>ContentRendered が来なくてもスプラッシュを閉じる上限 [ms]。</summary>
    private const int STARTUP_SPLASH_MAX_MS = 20000;

    /// <summary>閉じたスプラッシュを二重に閉じないための印。</summary>
    private readonly HashSet<SplashScreen> _closedSplashes = new();

    /// <summary>起動スプラッシュを表示する。失敗しても起動は続ける（null を返す）。</summary>
    private SplashScreen? ShowStartupSplash()
    {
        try
        {
            var splash = new SplashScreen(STARTUP_SPLASH_RESOURCE);
            splash.Show(autoClose: false, topMost: true);
            return splash;
        }
        catch (Exception ex)
        {
            EditorLog.Write($"起動スプラッシュを表示できませんでした: {ex.Message}");
            return null;
        }
    }

    /// <summary>起動スプラッシュをフェードアウトで閉じる（多重呼び出しは無視）。</summary>
    private void CloseStartupSplash(SplashScreen splash)
    {
        if (!_closedSplashes.Add(splash)) return;
        try { splash.Close(TimeSpan.FromMilliseconds(STARTUP_SPLASH_FADE_MS)); }
        catch (Exception ex) { EditorLog.Write($"起動スプラッシュを閉じられませんでした: {ex.Message}"); }
    }
}
