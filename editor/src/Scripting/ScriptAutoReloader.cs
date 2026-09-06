using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Threading;

namespace SEEDEditor.Scripting;

/// <summary>
/// スクリプト自動再読込の進行状態。ステータス表示（色分け）とログ出力の分岐に使う。
/// </summary>
public enum ScriptReloadStatus
{
    /// <summary>再読込を開始した（全体コンパイル検証中 → ランタイムへ送信）。</summary>
    Running,
    /// <summary>ランタイム側の再コンパイル・再生成が成功した。</summary>
    Success,
    /// <summary>エディタ側の全体コンパイル検証でエラー。ランタイムへは送っていない。</summary>
    CompileError,
    /// <summary>送信できなかった、またはランタイム側で失敗した（サイレント故障を含む）。</summary>
    Failed,
}

/// <summary>
/// アセットルート配下の .cs を監視し、変更されたら自動でスクリプトを
/// ホットリロードする（Unity ライクな「保存したら反映」）。
///
/// 【役割】
/// - 内蔵スクリプトエディタからの保存だけでなく、VS Code など**外部エディタ**での
///   保存も検出する（従来は内蔵エディタの保存イベントだけがトリガーだった）。
/// - 連続保存・エディタの一時ファイル生成でリロードが多重発火しないよう、
///   最後のイベントから <see cref="DebounceMs"/> ミリ秒待ってから 1 回だけ発火する。
/// - リロード実行中（ランタイムの応答待ち）に来た変更は 1 回分にまとめて、
///   完了後にもう一度だけ再読込する（取りこぼし防止と多重送信防止の両立）。
/// - 送信前に必ずエディタ側で**プロジェクト全体コンパイル**を検証する。エラーがあれば
///   送信しない（ランタイムは直前の正常アセンブリのまま動き続ける）。
///   ※ ランタイムは全 .cs を 1 アセンブリでコンパイルするため、エラーがあると
///     全スクリプトが実行されなくなる（サイレント故障）。それを未然に防ぐ。
///
/// 【スレッド】FileSystemWatcher のイベントはスレッドプールで発火するため、
/// 内部状態の更新はすべて Dispatcher（UI スレッド）へ載せ替えて直列化する。
/// ロックを使わずに済み、コンパイル検証・ステータス表示と同じスレッドで完結する。
///
/// 【依存の持ち方】本クラスは RuntimeManager や UI を直接知らない。
/// 「検証する」「再読込を送る」「状態を伝える」の 3 つをコールバックで受け取り、
/// 監視とスケジューリングという単一責務だけを持つ。
/// </summary>
public sealed class ScriptAutoReloader : IDisposable
{
    // ── 定数（マジックナンバー禁止）──────────────────────────────

    /// <summary>最後のファイル変更からこの時間だけ待ってから再読込する（連続保存の集約）。</summary>
    private const int DebounceMs = 600;

    /// <summary>
    /// ランタイムからの応答（SCRIPTS_RELOADED）を待つ上限。
    /// ランタイム未起動・応答喪失で「実行中」のまま固まらないための保険。
    /// </summary>
    private const int ReloadTimeoutMs = 10000;

    /// <summary>監視対象の拡張子（ユーザースクリプト）。</summary>
    private const string ScriptExtension = ".cs";

    /// <summary>FileSystemWatcher のフィルタ。</summary>
    private const string WatchFilter = "*" + ScriptExtension;

    /// <summary>
    /// 無視するディレクトリ名。ビルド生成物や VCS の内部ファイルは
    /// ユーザースクリプトではないため、変更されても再読込しない。
    /// </summary>
    private static readonly string[] IgnoredDirectories = { "obj", "bin", ".git", ".vs", "node_modules" };

    /// <summary>
    /// 無視するファイル名の末尾。エディタが作る一時・バックアップファイルを弾く
    /// （Vim/Emacs の "~"、各種エディタの ".tmp"、Visual Studio の一時ファイル等）。
    /// </summary>
    private static readonly string[] IgnoredSuffixes = { "~", ".tmp", ".swp", ".bak", ".TMP" };

    // ── 依存（コールバック）───────────────────────────────────────

    /// <summary>自動再読込が有効かどうか（エディタ設定のトグル）。</summary>
    private readonly Func<bool> _isEnabled;

    /// <summary>
    /// プロジェクト全体のコンパイル検証を行い、エラーメッセージ一覧を返す
    /// （空リスト = エラーなし）。エラー一覧パネルへの反映も呼び出し側で行う。
    /// </summary>
    private readonly Func<IReadOnlyList<string>> _runCompileCheck;

    /// <summary>
    /// ランタイムへ RELOAD_SCRIPTS を送る。送れた場合のみ true
    /// （false = ランタイム未接続。応答を待たずに終了する）。
    /// </summary>
    private readonly Func<bool> _sendReload;

    /// <summary>進行状態を UI（ステータス表示・ログ）へ伝える。</summary>
    private readonly Action<ScriptReloadStatus, string> _report;

    // ── 内部状態（すべて UI スレッドからのみ触る）──────────────────

    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer    _debounceTimer;
    private readonly DispatcherTimer    _timeoutTimer;

    /// <summary>ランタイムへ送信済みで応答待ちかどうか。</summary>
    private bool _inFlight;

    /// <summary>応答待ちの間に来た変更があるか（完了後にもう 1 回だけ再読込する）。</summary>
    private bool _pending;

    /// <summary>監視対象のアセットルート（絶対パス）。</summary>
    private readonly string _assetsRoot;

    // ── 公開 API ────────────────────────────────────────────────

    /// <summary>
    /// 自動再読込が実際に機能しているか（監視の起動に成功し、かつ設定がオン）。
    /// false のときは呼び出し側が従来どおり手動トリガーで再読込する。
    /// </summary>
    public bool IsEnabled => _watcher is not null && _isEnabled();

    /// <param name="assetsRoot">監視するアセットルート（この配下の .cs を再帰監視する）。</param>
    /// <param name="dispatcher">UI スレッドの Dispatcher（タイマー・コールバックの実行先）。</param>
    /// <param name="isEnabled">自動再読込設定の現在値を返す関数。</param>
    /// <param name="runCompileCheck">全体コンパイル検証（エラーメッセージ一覧を返す）。</param>
    /// <param name="sendReload">ランタイムへ RELOAD_SCRIPTS を送る（送れたら true）。</param>
    /// <param name="report">進行状態の通知先。</param>
    public ScriptAutoReloader(
        string assetsRoot,
        Dispatcher dispatcher,
        Func<bool> isEnabled,
        Func<IReadOnlyList<string>> runCompileCheck,
        Func<bool> sendReload,
        Action<ScriptReloadStatus, string> report)
    {
        _assetsRoot      = assetsRoot;
        _isEnabled       = isEnabled;
        _runCompileCheck = runCompileCheck;
        _sendReload      = sendReload;
        _report          = report;

        // デバウンス用タイマー（UI スレッド）。イベントが来るたびに Stop→Start で
        // 期限を延ばし、静まってから 1 回だけ発火させる。
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(DebounceMs),
        };
        _debounceTimer.Tick += (_, _) => { _debounceTimer.Stop(); Fire(); };

        // 応答待ちのタイムアウト（ランタイム未起動・応答喪失で固まらないための保険）
        _timeoutTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(ReloadTimeoutMs),
        };
        _timeoutTimer.Tick += (_, _) =>
            NotifyReloadFailed("ランタイムからの応答がありませんでした（タイムアウト）");

        // 監視の開始。アセットルートが無い・アクセスできない場合でも
        // エディタ全体を落とさない（自動再読込だけ無効になる）。
        try
        {
            if (!Directory.Exists(assetsRoot))
            {
                EditorLog.Write($"[ScriptAutoReload] アセットルートが存在しないため監視しません: {assetsRoot}");
                return;
            }

            _watcher = new FileSystemWatcher(assetsRoot)
            {
                Filter                = WatchFilter,
                IncludeSubdirectories = true,
                // 保存（LastWrite）・作成/削除/リネーム（FileName）を拾う。
                NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            _watcher.Changed += (_, e) => OnFsEvent(dispatcher, e.FullPath);
            _watcher.Created += (_, e) => OnFsEvent(dispatcher, e.FullPath);
            _watcher.Deleted += (_, e) => OnFsEvent(dispatcher, e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                // 「.tmp → .cs」のようなリネーム保存（多くのエディタの原子的保存）にも
                // 対応するため、新旧どちらかが対象なら発火させる。
                if (IsRelevant(e.FullPath) || IsRelevant(e.OldFullPath))
                    dispatcher.InvokeAsync(Schedule);
            };
            _watcher.EnableRaisingEvents = true;
            EditorLog.Write($"[ScriptAutoReload] 監視開始: {assetsRoot}\\**\\*{ScriptExtension}");
        }
        catch (Exception ex)
        {
            _watcher = null;
            EditorLog.Write($"[ScriptAutoReload] 監視を開始できませんでした: {ex.Message}");
        }
    }

    /// <summary>
    /// ランタイムからの再読込成功通知（SCRIPTS_RELOADED:count,restored）を受け取る。
    /// 応答待ちを解除し、待機中の変更があればもう一度だけ再読込を予約する。
    /// </summary>
    /// <param name="compiledCount">コンパイルされたスクリプト型数。</param>
    public void NotifyReloadSucceeded(int compiledCount)
        => FinishInFlight(ScriptReloadStatus.Success, $"再読込完了 ({compiledCount})");

    /// <summary>
    /// ランタイム側の再読込失敗（SCRIPTS_RELOADED:-1 = サイレント故障）や
    /// タイムアウトを受け取る。応答待ちは解除する。
    /// </summary>
    /// <param name="reason">失敗理由（ステータス表示に出す短い文言）。</param>
    public void NotifyReloadFailed(string reason)
        => FinishInFlight(ScriptReloadStatus.Failed, reason);

    /// <summary>
    /// 外部から再読込を要求する（手動トリガー用）。デバウンス経路に載せるため、
    /// 直後にファイル監視イベントが来ても二重に発火しない。
    /// </summary>
    public void RequestReload() => Schedule();

    // ── 内部処理 ────────────────────────────────────────────────

    /// <summary>ファイル監視イベント（別スレッド）を UI スレッドの予約処理へ載せ替える。</summary>
    private void OnFsEvent(Dispatcher dispatcher, string fullPath)
    {
        if (!IsRelevant(fullPath)) return;
        dispatcher.InvokeAsync(Schedule);
    }

    /// <summary>
    /// 再読込をデバウンス予約する（UI スレッド）。
    /// 応答待ち中は予約せず「あとで 1 回」だけ立てておく。
    /// </summary>
    private void Schedule()
    {
        if (!IsEnabled) return;

        if (_inFlight)
        {
            // 実行中の変更は取りこぼさず、かつ多重送信もしない
            _pending = true;
            return;
        }

        // 最後のイベントから DebounceMs 静まるまで待つ
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    /// <summary>
    /// 実際の再読込。全体コンパイル検証 → 問題なければランタイムへ送信する。
    ///
    /// 注意: 全体コンパイル（Roslyn）は UI スレッドで同期実行される。
    /// 保存時の既存検証（ScriptEditorPanel.Save）と同じ経路・同じコストであり、
    /// 検証結果をそのままエラー一覧パネルへ反映できる利点を優先している。
    /// </summary>
    private void Fire()
    {
        if (!IsEnabled) return;
        if (_inFlight) { _pending = true; return; }

        _report(ScriptReloadStatus.Running, "スクリプト再読込中…");

        // 1. エディタ側で全 .cs を一括検証する。
        //    エラーがあれば送信しない（ランタイムは直前の正常アセンブリのまま動く）。
        IReadOnlyList<string> errors;
        try
        {
            errors = _runCompileCheck();
        }
        catch (Exception ex)
        {
            // 検証自体の失敗でエディタを止めない。安全側に倒して送信は行わない。
            _report(ScriptReloadStatus.Failed, $"コンパイル検証に失敗: {ex.Message}");
            return;
        }

        if (errors.Count > 0)
        {
            _report(ScriptReloadStatus.CompileError, errors[0]);
            return;
        }

        // 2. ランタイムへホットリロードを要求する。
        //    未接続なら応答は来ないので、応答待ちには入らない。
        if (!_sendReload())
        {
            _report(ScriptReloadStatus.Failed, "ランタイムが起動していないため再読込しません");
            return;
        }

        _inFlight = true;
        _timeoutTimer.Stop();
        _timeoutTimer.Start();
    }

    /// <summary>
    /// 応答待ちを終了し、待機中の変更があれば再度デバウンス予約する。
    /// 応答待ちでない状態で呼ばれた場合（手動再読込の応答など）は状態通知のみ行う。
    /// </summary>
    private void FinishInFlight(ScriptReloadStatus status, string message)
    {
        _timeoutTimer.Stop();
        bool wasInFlight = _inFlight;
        _inFlight = false;

        _report(status, message);

        // 実行中に来た変更をここで 1 回だけ消化する
        if (_pending)
        {
            _pending = false;
            if (wasInFlight) Schedule();
        }
    }

    /// <summary>
    /// 再読込のトリガーにすべきファイルかどうか。
    /// ユーザースクリプト（.cs）のうち、ビルド生成物・一時/バックアップファイルを除外する。
    /// </summary>
    private bool IsRelevant(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;

        // 拡張子（FileSystemWatcher のフィルタは "*.cs" でも "*.cs~" 等を拾う環境があるため再確認）
        if (!fullPath.EndsWith(ScriptExtension, StringComparison.OrdinalIgnoreCase)) return false;

        // 一時・バックアップファイル
        foreach (var suffix in IgnoredSuffixes)
            if (fullPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;

        // ビルド生成物ディレクトリ配下（obj/ bin/ など）
        var relative = fullPath.StartsWith(_assetsRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath[_assetsRoot.Length..]
            : fullPath;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            foreach (var ignored in IgnoredDirectories)
                if (string.Equals(segment, ignored, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    /// <summary>監視とタイマーを停止する（エディタ終了時）。</summary>
    public void Dispose()
    {
        _debounceTimer.Stop();
        _timeoutTimer.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
