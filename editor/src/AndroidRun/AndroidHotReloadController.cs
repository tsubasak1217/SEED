// ============================================================
//  AndroidHotReloadController.cs — エディタの Android の実行中に、保存したアセット・シーン・スクリプトを端末へ差し替える
//                                   段取り（ファイルの監視 → デバウンス → 差し替え → Output。docs/android.md §23）
//
//  【いつ動くか】
//  Android の実行で端末のアプリが動いている間（Running / Paused。AndroidRunController.StateChanged で見る）だけ、
//  アセットルートを監視する（FileSystemWatcher。書き込み・作成・名前の変更）。実行が終わったら監視を止め、覚えた変更を捨てる。
//  PC の Play の自動再読み込み（ScriptAutoReloader・SceneAutoReloader）とは別で、そちらの振る舞いは変えない。
//
//  【流れ】
//    変わったファイル → 表で関係ないものを落とす（AndroidHotReloadTable の Ignore）→ まとめ役へ（AndroidHotReloadChangeQueue）
//    → 最後の変更から静かな時間（既定 0.6 秒。ScriptAutoReloader と同じ）が過ぎたら 1 組として取り出す（連続保存は最後の 1 回に）
//    → 端末のアプリとつながっていれば差し替える（IAndroidHotReloadBackend → 中核の AndroidHotReloadApplier）
//       つながっていなければ理由を 1 行出して捨てる
//    → 結果を Output へ（AndroidHotReloadOutputFormatter）。差し替えの途中に来た変更は、終わった後に次の組にする
//  シーンはディスク上のファイルから送る（未保存の変更は送らない＝シーンを保存したときに送られる）。
//  PC の Play の自動再読み込みは Play 中の変更を保留する（OnStart の再実行で進行が戻るため。docs/editor_auto_reload.md）が、
//  Android の差し替えは「動かしたまま反映する」ための機能なので保留しない（同じ副作用は起きる）。スクリプト・シーンは
//  エディタの設定「スクリプトを自動再読込」「シーンを自動再読込」がオフなら差し替えない（kindEnabled。MainWindow が渡す）。
//
//  【スレッド】ファイル監視のイベント・タイマーは任意のスレッドから来るので、まとめ役はロック（_gate）の中だけで触る。
//  差し替えはスレッドプールで 1 組ずつ（同時に 2 組は走らせない）。イベント（OutputWritten）はロックの外で発火する。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests が偽の中核と手で進める時計で確かめる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.HotReload;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.AndroidRun;

/// <summary>実行中の差し替えの中核の入口（差し替え可能な窓口。単体テストは偽物）。</summary>
public interface IAndroidHotReloadBackend
{
    /// <summary>
    /// 変わったファイルの組を差し替える（失敗は例外ではなく結果に入れる。予期しない失敗だけ例外）。
    /// </summary>
    /// <param name="changed">変わったファイル（アセットルートからの相対パス）。</param>
    /// <param name="target">差し替える相手（端末のアプリと IPC）。</param>
    /// <param name="info">途中の説明の 1 行（SeedPak の出力など。エディタのログへ）。</param>
    /// <param name="cancellationToken">中断の合図（実行を止めた・エディタを閉じた）。</param>
    /// <returns>結果。</returns>
    Task<AndroidHotReloadResult> ApplyAsync(
        IReadOnlyList<string> changed, AndroidHotReloadTarget target, Action<string> info, CancellationToken cancellationToken);
}

/// <summary>本番の入口（中核の AndroidHotReloadApplier をそのまま呼ぶ）。</summary>
public sealed class AndroidHotReloadBackend : IAndroidHotReloadBackend
{
    /// <summary>道具。</summary>
    private readonly AndroidToolchain _toolchain;

    /// <summary>エンジン側の置き場。</summary>
    private readonly AndroidEnginePaths _engine;

    /// <summary>プロジェクトのルート（実行ボタンが中核へ渡すものと同じ）。</summary>
    private readonly string _projectDir;

    /// <summary>必要なものを指定して作る。</summary>
    /// <param name="toolchain">道具。</param>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <param name="projectDir">プロジェクトのルート。</param>
    public AndroidHotReloadBackend(AndroidToolchain toolchain, AndroidEnginePaths engine, string projectDir)
    {
        _toolchain = toolchain;
        _engine = engine;
        _projectDir = projectDir;
    }

    /// <inheritdoc />
    public Task<AndroidHotReloadResult> ApplyAsync(
        IReadOnlyList<string> changed, AndroidHotReloadTarget target, Action<string> info, CancellationToken cancellationToken)
    {
        var project = AndroidProjectResolver.Resolve(_projectDir, null)
                      ?? throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest, $"プロジェクトが見つかりません: {_projectDir}");
        return new AndroidHotReloadApplier(_toolchain, _engine, project).ApplyAsync(changed, target, info, cancellationToken);
    }
}

/// <summary>エディタの Android の実行中の差し替えの段取り。</summary>
public sealed class AndroidHotReloadController : IDisposable
{
    /// <summary>既定の静かな時間（最後の変更からこれだけ待ってまとめる。ScriptAutoReloader・SceneAutoReloader と同じ 0.6 秒）。</summary>
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMilliseconds(600);

    /// <summary>既定の見回りの間隔（まとめ役が組を出せるかを見る）。</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>中核の入口。</summary>
    private readonly IAndroidHotReloadBackend _backend;

    /// <summary>差し替える相手を返す（つながっていなければ null。AndroidRunController.HotReloadTarget）。</summary>
    private readonly Func<AndroidHotReloadTarget?> _targetProvider;

    /// <summary>今の時刻（テストは手で進める）。</summary>
    private readonly Func<DateTime> _utcNow;

    /// <summary>ファイルを監視するか（テストは監視せず NotifyChanged を直接呼ぶ）。</summary>
    private readonly bool _watchFileSystem;

    /// <summary>
    /// 種類ごとに差し替えるか（エディタの設定「スクリプトを自動再読込」「シーンを自動再読込」に合わせる。null なら全部）。
    /// </summary>
    private readonly Func<AndroidHotReloadKind, bool>? _kindEnabled;

    /// <summary>見回りの間隔（null ならタイマーを使わない＝テストは TickAsync を直接呼ぶ）。</summary>
    private readonly TimeSpan? _pollInterval;

    /// <summary>状態の排他。</summary>
    private readonly object _gate = new();

    /// <summary>変わったファイルのまとめ役。</summary>
    private readonly AndroidHotReloadChangeQueue _queue;

    /// <summary>監視しているアセットルート（監視していなければ null）。</summary>
    private string? _assetsRoot;

    /// <summary>ファイルの監視（監視していなければ null）。</summary>
    private FileSystemWatcher? _watcher;

    /// <summary>見回りのタイマー（動いていなければ null）。</summary>
    private Timer? _timer;

    /// <summary>差し替えの中断の合図（監視していなければ null）。</summary>
    private CancellationTokenSource? _cancellation;

    /// <summary>破棄済みか。</summary>
    private bool _disposed;

    /// <summary>
    /// 必要なものを指定して作る。
    /// </summary>
    /// <param name="backend">中核の入口。</param>
    /// <param name="targetProvider">差し替える相手を返す（AndroidRunController.HotReloadTarget）。</param>
    /// <param name="quietPeriod">静かな時間（null なら既定）。</param>
    /// <param name="pollInterval">見回りの間隔（null ならタイマーを使わない）。</param>
    /// <param name="utcNow">今の時刻（null なら DateTime.UtcNow）。</param>
    /// <param name="watchFileSystem">ファイルを監視するか。</param>
    /// <param name="kindEnabled">種類ごとに差し替えるか（null なら全部。変更が届いたときに毎回聞く＝設定の切り替えがすぐ効く）。</param>
    public AndroidHotReloadController(
        IAndroidHotReloadBackend backend,
        Func<AndroidHotReloadTarget?> targetProvider,
        TimeSpan? quietPeriod = null,
        TimeSpan? pollInterval = null,
        Func<DateTime>? utcNow = null,
        bool watchFileSystem = true,
        Func<AndroidHotReloadKind, bool>? kindEnabled = null)
    {
        _backend = backend;
        _targetProvider = targetProvider;
        _queue = new AndroidHotReloadChangeQueue(quietPeriod ?? DefaultQuietPeriod);
        _pollInterval = pollInterval;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _watchFileSystem = watchFileSystem;
        _kindEnabled = kindEnabled;
    }

    /// <summary>Output パネルへ出す行（任意のスレッドから）。</summary>
    public event Action<AndroidRunOutputLine>? OutputWritten;

    /// <summary>途中の説明の 1 行（SeedPak の出力など。エディタのログだけへ。任意のスレッドから）。</summary>
    public event Action<string>? InfoWritten;

    /// <summary>1 組の差し替えが終わった（テスト用。任意のスレッドから）。</summary>
    public event Action<AndroidHotReloadResult?>? BatchCompleted;

    /// <summary>監視しているか。</summary>
    public bool IsWatching
    {
        get
        {
            lock (_gate) return _assetsRoot is not null;
        }
    }

    /// <summary>
    /// Android の実行の状態に合わせて監視を始める・止める（AndroidRunController.StateChanged から呼ぶ）。
    /// </summary>
    /// <param name="snapshot">Android の実行のいまの写し。</param>
    /// <param name="assetsRoot">アセットルート（監視する場所）。</param>
    public void OnRunStateChanged(AndroidRunSnapshot snapshot, string assetsRoot)
    {
        if (snapshot.IsAppAlive) Start(assetsRoot);
        else Stop();
    }

    /// <summary>
    /// 監視を始める（既に同じ場所を監視していれば何もしない）。
    /// </summary>
    /// <param name="assetsRoot">アセットルート。</param>
    public void Start(string assetsRoot)
    {
        lock (_gate)
        {
            if (_disposed || _assetsRoot is not null) return;
            _assetsRoot = Path.GetFullPath(assetsRoot);
            _cancellation = new CancellationTokenSource();
            if (_watchFileSystem) _watcher = CreateWatcher(_assetsRoot);
            if (_pollInterval is { } interval) _timer = new Timer(_ => _ = TickAsync(), null, interval, interval);
        }
    }

    /// <summary>監視を止め、覚えた変更を捨てる（差し替えの途中なら中断の合図を送る）。</summary>
    public void Stop()
    {
        FileSystemWatcher? watcher;
        Timer? timer;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            watcher = _watcher;
            timer = _timer;
            cancellation = _cancellation;
            _watcher = null;
            _timer = null;
            _cancellation = null;
            _assetsRoot = null;
            _queue.Clear();
        }
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        timer?.Dispose();
        TryCancel(cancellation);
    }

    /// <summary>
    /// ファイルが変わった（ファイル監視のイベント。テストは直接呼ぶ）。関係ないファイルは覚えない。
    /// </summary>
    /// <param name="fullPath">変わったファイルの絶対パス。</param>
    public void NotifyChanged(string fullPath)
    {
        lock (_gate)
        {
            if (_assetsRoot is null) return;
            var relative = RelativeTo(_assetsRoot, fullPath);
            if (relative is null) return;
            var kind = AndroidHotReloadTable.Classify(relative);
            if (kind == AndroidHotReloadKind.Ignore || _kindEnabled?.Invoke(kind) == false) return;
            // フォルダの変更（作成・名前の変更）は中のファイルのイベントで拾う
            if (Directory.Exists(fullPath)) return;
            _queue.Add(relative, _utcNow());
        }
    }

    /// <summary>
    /// 見回り: 組を出せるなら 1 組を差し替える（タイマーから。テストは直接呼ぶ）。差し替えが終わると完了するタスクを返す。
    /// </summary>
    /// <returns>完了（組を出さなかったらすぐ完了）。</returns>
    public async Task TickAsync()
    {
        IReadOnlyList<string> batch;
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || _cancellation is null) return;
            batch = _queue.TakeBatch(_utcNow());
            if (batch.Count == 0) return;
            token = _cancellation.Token;
        }

        AndroidHotReloadResult? result = null;
        try
        {
            var target = _targetProvider();
            if (target is null)
            {
                Emit(AndroidHotReloadOutputFormatter.NotConnected(batch));
                return;
            }
            Emit(AndroidHotReloadOutputFormatter.Started(batch));
            result = await Task.Run(() => _backend.ApplyAsync(batch, target, WriteInfo, token), token).ConfigureAwait(false);
            foreach (var line in AndroidHotReloadOutputFormatter.Completed(result)) Emit(line);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 実行を止めた・エディタを閉じた: 何も出さない
        }
        catch (Exception ex)
        {
            // 中核の不具合でも監視は続ける（次の保存でもう一度試せる）
            Emit(AndroidHotReloadOutputFormatter.Crashed(ex.Message));
        }
        finally
        {
            lock (_gate) _queue.Complete(_utcNow());
            RaiseBatchCompleted(result);
        }
    }

    /// <summary>監視を止める（エディタを閉じる）。</summary>
    public void Dispose()
    {
        Stop();
        lock (_gate) _disposed = true;
    }

    /// <summary>アセットルートを監視する FileSystemWatcher を作る（作れなければ理由を 1 行出して null）。</summary>
    private FileSystemWatcher? CreateWatcher(string assetsRoot)
    {
        try
        {
            if (!Directory.Exists(assetsRoot)) return null;
            var watcher = new FileSystemWatcher(assetsRoot)
            {
                IncludeSubdirectories = true,
                // 保存（LastWrite・Size）と、作成・名前の変更による差し替え（FileName。一時ファイル → 本名の原子的な保存）を拾う
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            watcher.Changed += (_, e) => NotifyChanged(e.FullPath);
            watcher.Created += (_, e) => NotifyChanged(e.FullPath);
            watcher.Renamed += (_, e) => NotifyChanged(e.FullPath);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            WriteInfo($"[Android] 差し替えのためのアセットの監視を始められません: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 絶対パスをアセットルートからの相対パス（区切り /）にする（外なら null。純粋な処理）。
    /// </summary>
    /// <param name="assetsRoot">アセットルート（絶対パス）。</param>
    /// <param name="fullPath">絶対パス。</param>
    /// <returns>相対パス。</returns>
    public static string? RelativeTo(string assetsRoot, string fullPath)
    {
        var root = assetsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full;
        try
        {
            full = Path.GetFullPath(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full[root.Length..].Replace(Path.DirectorySeparatorChar, '/')
            : null;
    }

    /// <summary>中断の合図を送る（破棄済みでも失敗させない）。</summary>
    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 既に終わった
        }
    }

    /// <summary>1 行を出す（受け手の例外で段取りを止めない）。</summary>
    private void Emit(AndroidRunOutputLine line)
    {
        try
        {
            OutputWritten?.Invoke(line);
        }
        catch (Exception)
        {
            // 受け手（UI・ログ）の不具合で段取りを止めない
        }
    }

    /// <summary>途中の説明を出す（受け手の例外で段取りを止めない）。</summary>
    private void WriteInfo(string text)
    {
        try
        {
            InfoWritten?.Invoke(text);
        }
        catch (Exception)
        {
            // 同上
        }
    }

    /// <summary>1 組の終わりを知らせる（受け手の例外で段取りを止めない）。</summary>
    private void RaiseBatchCompleted(AndroidHotReloadResult? result)
    {
        try
        {
            BatchCompleted?.Invoke(result);
        }
        catch (Exception)
        {
            // 同上
        }
    }
}
