using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace SEEDEditor.Reload;

/// <summary>
/// アセットルート配下のプレハブ（.actor / .actor2d）を監視し、外部（テキストエディタ・AI・別ツール）で
/// 書き換えられたら知らせる（docs/editor_auto_reload.md §7.1）。
///
/// 【役割】
/// - ファイル監視のイベントを UI スレッドへ集め、判定は純粋な <see cref="PrefabExternalChangeTracker"/>
///   （デバウンス・自己書き込みの除外・内容のハッシュの比較）へ任せる。ここはタイマーの張り直しと配線だけ。
/// - 外部の変更と判定したパスを <c>onExternalChange</c> へ渡す。何を送るか（PREFAB_REAPPLY_PATH /
///   PREFAB_STATUS / PREFAB_LIVE_PATCH_PATH）は呼び出し側（MainWindow.PrefabAutoReload.cs）が
///   <see cref="AutoReloadPolicy.DecidePrefabExternalChange"/> で決める。
/// - エディタ自身の書き込み（SAVE_ACTOR・PREFAB_WRITE_BACK・EXPORT_ACTOR）の開始・終了と、
///   一括の書き換え（形式のアップグレード）の間の抑止を受け付ける。
///
/// 【スレッド】FileSystemWatcher のイベントはスレッドプールで発火するため、追跡器へは必ず
/// Dispatcher（UI スレッド）経由で触る（ScriptAutoReloader / SceneAutoReloader と同じ流儀。ロック不要）。
///
/// 【依存の持ち方】RuntimeManager・MainWindow を知らない。「有効か」「外部の変更を受け取る」
/// 「ログ」をコールバックで受け取る。
///
/// 【開始時の覚え込み（2026-10-03 の 2 回目のレビュー #15）】監視を始めたとき（設定がオンなら）と、設定をオンにしたとき
/// （<see cref="Reseed"/>）に、既存の .actor / .actor2d の内容のハッシュを背景スレッドで読み（<see cref="PrefabKnownHashSeeder"/>）、
/// UI スレッドで追跡器へ覚えさせる。これで起動直後の「同じ内容の書き込み・touch」も規則 3 どおり Unchanged になる。
/// 覚え終わる前に届いたイベントは従来どおり Changed。
/// </summary>
public sealed class PrefabAutoReloader : IDisposable
{
    /// <summary>
    /// タイマーの最短の間隔（ミリ秒）。満了済みの判定待ちがあるとき 0 で回さず、UI スレッドへ一呼吸おくための下限。
    /// </summary>
    private const int MinTimerIntervalMs = 1;

    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[PrefabAutoReload]";

    // ── 依存（コールバック）───────────────────────────────────────

    /// <summary>自動再読込が有効か（設定「プレハブを自動再読込」）。</summary>
    private readonly Func<bool> _isEnabled;

    /// <summary>外部の変更と判定したプレハブの絶対パスを受け取る。</summary>
    private readonly Action<string> _onExternalChange;

    /// <summary>ログの出力先。</summary>
    private readonly Action<string> _log;

    // ── 内部状態（すべて UI スレッドからのみ触る）──────────────────

    /// <summary>判定の本体（純粋なクラス）。</summary>
    private readonly PrefabExternalChangeTracker _tracker = new(FileContentHash.TryCompute);

    /// <summary>判定待ちの満了を待つタイマー。</summary>
    private readonly DispatcherTimer _timer;

    /// <summary>監視しているアセットルート（絶対パス）。</summary>
    private readonly string _assetsRoot;

    /// <summary>ファイル監視（起動に失敗したら null＝この機能だけ無効）。</summary>
    private readonly FileSystemWatcher? _watcher;

    /// <summary>UI スレッドの Dispatcher（覚え込みの結果を追跡器へ渡す先）。</summary>
    private readonly Dispatcher _dispatcher;

    /// <summary>走っている覚え込みの取り消し（覚え直し・終了で取り消す。走っていなければ null）。UI スレッドからのみ触る。</summary>
    private CancellationTokenSource? _seedCancel;

    /// <summary>
    /// 自動再読込が実際に機能しているか（監視の起動に成功し、かつ設定がオン）。
    /// </summary>
    public bool IsEnabled => _watcher is not null && _isEnabled();

    /// <param name="assetsRoot">監視するアセットルート（この配下の .actor / .actor2d を再帰監視する）。</param>
    /// <param name="dispatcher">UI スレッドの Dispatcher（タイマー・コールバックの実行先）。</param>
    /// <param name="isEnabled">設定「プレハブを自動再読込」の現在値を返す関数。</param>
    /// <param name="onExternalChange">外部の変更と判定したプレハブの絶対パスを受け取る（UI スレッドで呼ばれる）。</param>
    /// <param name="log">ログの出力先（接頭辞なしの本文を渡す）。</param>
    public PrefabAutoReloader(
        string assetsRoot,
        Dispatcher dispatcher,
        Func<bool> isEnabled,
        Action<string> onExternalChange,
        Action<string> log)
    {
        _assetsRoot       = assetsRoot;
        _isEnabled        = isEnabled;
        _onExternalChange = onExternalChange;
        _log              = log;
        _dispatcher       = dispatcher;

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += (_, _) => OnTimerTick();

        // 監視の開始。アセットルートが無い・アクセスできない場合でもエディタ全体を落とさない（この機能だけ無効になる）。
        try
        {
            if (!Directory.Exists(assetsRoot))
            {
                _log($"{LogPrefix} アセットルートが存在しないため監視しません: {assetsRoot}");
                return;
            }

            var watcher = new FileSystemWatcher(assetsRoot)
            {
                IncludeSubdirectories = true,
                // 書き込み（LastWrite・Size）と、作成・名前の変更による差し替え（FileName。一時ファイル → 本名の原子的な保存）を拾う
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            foreach (var filter in PrefabWatchPaths.WatchFilters) watcher.Filters.Add(filter);
            watcher.Changed += (_, e) => OnFsEvent(dispatcher, e.FullPath);
            watcher.Created += (_, e) => OnFsEvent(dispatcher, e.FullPath);
            // 「X.actor.tmp → X.actor」の名前の変更（ランタイムの safe_write の原子的な保存）は新しい名前で拾う
            watcher.Renamed += (_, e) => OnFsEvent(dispatcher, e.FullPath);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _log($"{LogPrefix} 監視開始: {assetsRoot}\\**\\({string.Join(", ", PrefabWatchPaths.WatchFilters)})");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _watcher = null;
            _log($"{LogPrefix} 監視を開始できませんでした: {ex.Message}");
        }

        // 既存のプレハブの内容を覚える（監視を張った後に始める。張る前の書き込みは覚え込みに含まれ、後の書き込みはイベントで届く）。
        // 設定がオフなら覚えない（オンにしたとき Reseed で覚える）
        if (IsEnabled) StartSeeding();
    }

    // ── 開始時の覚え込み（2 回目のレビュー #15）─────────────────────

    /// <summary>
    /// 既存のプレハブの内容を覚え直す（設定「プレハブを自動再読込」をオンにしたときに呼ぶ。UI スレッドから）。
    /// オフの間はイベントを捨てているので、知っている内容が古くなっている。走っている覚え込みがあれば取り消してやり直す。
    /// </summary>
    public void Reseed()
    {
        if (!IsEnabled) return;
        StartSeeding();
    }

    /// <summary>
    /// 覚え込みを背景スレッドで始める（列挙とハッシュの読み取りは背景、追跡器へ渡すのは UI スレッド。数が多くても UI を止めない）。
    /// </summary>
    private void StartSeeding()
    {
        _seedCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _seedCancel = cancel;
        var seedStartedUtc = DateTime.UtcNow;
        var root = _assetsRoot;

        Task.Run(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var files = PrefabKnownHashSeeder.EnumeratePrefabFiles(root);
            var seeds = PrefabKnownHashSeeder.Collect(
                files, seedStartedUtc, PrefabKnownHashSeeder.TryGetLastWriteUtc, FileContentHash.TryCompute, cancel.Token);
            watch.Stop();
            if (cancel.IsCancellationRequested) return;

            _dispatcher.InvokeAsync(() =>
            {
                // 取り消された（覚え直し・終了）結果は捨てる。新しい覚え込みの結果だけを使う
                if (cancel.IsCancellationRequested || !ReferenceEquals(_seedCancel, cancel)) return;
                int seeded = 0;
                foreach (var seed in seeds)
                    if (_tracker.SeedKnownHash(Normalize(seed.Path), seed.Hash, seedStartedUtc)) seeded++;
                _seedCancel = null;
                cancel.Dispose();
                _log($"{LogPrefix} 既存のプレハブの内容を覚えました: {seeded} / {files.Count} 件（{watch.ElapsedMilliseconds} ms）");
            });
        }, cancel.Token);
    }

    // ── エディタ自身の書き込み・一括の書き換え（UI スレッドから呼ぶ）──────

    /// <summary>エディタ自身がそのプレハブの書き込みを始めた（SAVE_ACTOR / PREFAB_WRITE_BACK を送った）。</summary>
    /// <param name="absolutePath">書き込み先の絶対パス。</param>
    public void NotifySelfWriteStarted(string absolutePath)
        => _tracker.NotifySelfWriteStarted(Normalize(absolutePath), DateTime.UtcNow);

    /// <summary>エディタ自身のそのプレハブの書き込みが終わった（成功・失敗どちらでも呼ぶ）。</summary>
    /// <param name="absolutePath">書き込み先の絶対パス。</param>
    public void NotifySelfWriteFinished(string absolutePath)
        => _tracker.NotifySelfWriteFinished(Normalize(absolutePath), DateTime.UtcNow);

    /// <summary>一括の書き換え（形式のアップグレード）を始める（終わったら必ず <see cref="EndSuppression"/>）。</summary>
    public void BeginSuppression() => _tracker.BeginSuppression();

    /// <summary>一括の書き換えを終える（余韻の間に遅れて届いたイベントも当て直さない）。</summary>
    public void EndSuppression() => _tracker.EndSuppression(DateTime.UtcNow);

    // ── 内部処理 ────────────────────────────────────────────────

    /// <summary>ファイル監視のイベント（別スレッド）を UI スレッドの追跡器へ載せ替える。</summary>
    private void OnFsEvent(Dispatcher dispatcher, string? fullPath)
    {
        if (!PrefabWatchPaths.IsPrefabFile(fullPath, _assetsRoot)) return;
        var path = fullPath!;
        dispatcher.InvokeAsync(() =>
        {
            // 設定オフの間は覚えもしない（オフにしたのに後で反映される、を作らない。AutoReloadPolicy の Drop と同じ）
            if (!IsEnabled) return;
            _tracker.OnFileEvent(Normalize(path), DateTime.UtcNow);
            Reschedule();
        });
    }

    /// <summary>判定待ちの満了を判定し、外部の変更だけを知らせる。</summary>
    private void OnTimerTick()
    {
        _timer.Stop();
        foreach (var outcome in _tracker.TakeDue(DateTime.UtcNow))
        {
            switch (outcome.Verdict)
            {
                case PrefabChangeVerdict.Changed:
                    // 判定待ちの間に設定がオフになっていたら送らない（受け取り側も方針で再確認する）
                    if (IsEnabled) _onExternalChange(outcome.Path);
                    break;
                case PrefabChangeVerdict.SelfWrite:
                    _log($"{LogPrefix} エディタ自身の書き込みのため当て直しません: {outcome.Path}");
                    break;
                case PrefabChangeVerdict.Suppressed:
                    _log($"{LogPrefix} 一括の書き換えの最中のため当て直しません: {outcome.Path}");
                    break;
                case PrefabChangeVerdict.Unreadable:
                    _log($"{LogPrefix} ファイルを読めないため当て直しません（消えた・ロック中）: {outcome.Path}");
                    break;
                case PrefabChangeVerdict.Unchanged:
                    // 内容が同じ（touch・重複イベント）。毎回出すと騒がしいのでログにも出さない
                    break;
            }
        }
        Reschedule();
    }

    /// <summary>次の満了の時刻にタイマーを張り直す（判定待ちが無ければ止める）。</summary>
    private void Reschedule()
    {
        _timer.Stop();
        if (_tracker.NextDueUtc is not { } due) return;
        var wait = due - DateTime.UtcNow;
        var min  = TimeSpan.FromMilliseconds(MinTimerIntervalMs);
        _timer.Interval = wait > min ? wait : min;
        _timer.Start();
    }

    /// <summary>パスを絶対パスへ揃える（揃えられなければそのまま）。</summary>
    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return path;
        }
    }

    /// <summary>監視とタイマーを止める（エディタ終了時）。</summary>
    public void Dispose()
    {
        _timer.Stop();
        // 走っている覚え込みを止める（結果は UI スレッドで捨てられる）
        _seedCancel?.Cancel();
        _seedCancel = null;
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}
