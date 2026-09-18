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

        // SEED アカウント（docs/seed_accounts.md）を読み、バージョン管理の層へ
        // 資格情報の窓口を差し込む。**プロジェクトを開くより前**に済ませる必要がある
        // （ProjectContext.Open が自動ログインを起こすため）。
        // アカウントが無くても失敗しない（匿名で動く）。
        try
        {
            Accounts.AccountService.Log = EditorLog.Write;
            Accounts.AccountService.Initialize(SEEDEditor.Settings.EditorPaths.SettingsDir);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"アカウントを初期化できませんでした: {ex.Message}");
        }

        // ロックのゲート（保存・送信を止めるかどうかの方針）を読む。
        // 設定ファイルが無ければ既定（Enforce = 他の人のロックがあれば止める）。
        // 画面への提示は MainWindow が Notifier を差し込むまでログだけになる。
        try
        {
            VersionControl.Locking.LockGatekeeper.Log = EditorLog.Write;
            VersionControl.Locking.LockGatekeeper.Configure(
                SEEDEditor.Settings.EditorPaths.SettingsDir);

            // ★自動ログインはプロジェクトを開いた「あと」に終わる。それより先に
            //   起動時のシーンが開くため、その時点ではまだ匿名で、匿名では
            //   ロックを取らない（所有者不明のロックを作らないため）。
            //   ログインできた時点で取り直す。ここが唯一の配線。
            Accounts.AccountService.AuthStateChanged += (_, state) =>
            {
                if (!state.IsSignedIn) return;
                try { VersionControl.Locking.LockGatekeeper.RetryPendingAutoLocks(); }
                catch (Exception ex)
                {
                    EditorLog.Write($"ロックの取り直しに失敗しました: {ex.Message}");
                }
            };
        }
        catch (Exception ex)
        {
            EditorLog.Write($"ロックの設定を読めませんでした: {ex.Message}");
        }

        base.OnStartup(e);

        ShowStartupWindow();
    }

    /// <summary>
    /// アプリ終了時の後始末。
    ///
    /// <para>
    /// バージョン管理（Lore）のワーカースレッドを止め、ネイティブ側を終了させる。
    /// <c>Lore.Shutdown()</c> は **プロセスで 1 回だけ** 呼ぶものなので、
    /// 呼び出しは <see cref="VersionControl.Lore.Backend.LoreShutdownGuard"/> に一本化してある。
    /// </para>
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        // アカウントのトークンを捨て、自動更新のタイマーを止める。
        // Lore へトークンを渡す経路を先に閉じてから、バージョン管理を止める。
        try { Accounts.AccountService.DetachFromProject(); }
        catch (Exception ex) { EditorLog.Write($"アカウントの停止に失敗しました: {ex.Message}"); }

        // 先にワーカーを止める。止める前に Shutdown すると、
        // 走行中の Lore 呼び出しが解放済みのネイティブ側を触る。
        try { VersionControl.VersionControlService.Close(); }
        catch (Exception ex) { EditorLog.Write($"バージョン管理の停止に失敗しました: {ex.Message}"); }

        VersionControl.Lore.Backend.LoreShutdownGuard.Shutdown(EditorLog.Write);

        base.OnExit(e);
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
        // engine_version の食い違いを確認する（判定・通知は EngineVersionGate に一任）。
        // 利用者が明示的に「開かない」を選んだ場合だけ false が返る
        // （プロジェクトの方が新しいエンジンで作られていた警告ダイアログでのみ起こり得る）。
        // ヘッドレスではこの分岐へは来ない（ダイアログが自動的に「続行」側の既定値へ倒れるため）。
        if (!EngineVersionGate.CheckBeforeOpen(projectFilePath))
        {
            new StartWindow(EngineVersionGate.DeclinedStatusMessage).Show();
            return;
        }

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

    /// <summary>
    /// 起動スプラッシュ画像のリソース名（csproj の Resource Include で埋め込まれる）。
    /// ビューポート内の起動中画面（blueSky_ORE.png）とは別の画像。差し替えるときは同じファイル名で上書きする。
    /// </summary>
    private const string STARTUP_SPLASH_RESOURCE = "resources/images/startup_splash.png";

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
