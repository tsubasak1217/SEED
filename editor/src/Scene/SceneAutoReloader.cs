using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;

namespace SEEDEditor.Scene;

/// <summary>
/// シーン自動再読込の進行状態。ステータス表示（色分け）とログ出力の分岐に使う。
/// スクリプト側（<see cref="SEEDEditor.Scripting.ScriptReloadStatus"/>）と役割は同じだが、
/// 分岐の意味が異なる（コンパイル検証が無く、代わりに「見送り」がある）ため別の列挙にする。
/// </summary>
public enum SceneReloadStatus
{
    /// <summary>再読込を開始した（LOAD_SCENE 送信直前）。</summary>
    Running,

    /// <summary>再読込が完了した。</summary>
    Success,

    /// <summary>
    /// 変更は検出したが、意図的に再読込しなかった
    /// （未保存の編集がある / Play 中で停止待ち）。警告色で表示する。
    /// </summary>
    Skipped,

    /// <summary>ファイルが読めない等で再読込できなかった。</summary>
    Failed,
}

/// <summary>
/// 現在開いている .scene ファイルをディスク上で監視し、外部（別ツール・別プロセス・
/// 手動書き換え）から変更されたらエディタ側で自動的に読み直す。
///
/// 【役割】
/// - 監視対象は「今開いている 1 ファイル」だけ。シーンを開き直すたびに
///   <see cref="SetScenePath"/> で監視先を張り替える。
/// - 連続書き込み・一時ファイル経由の原子的保存でリロードが多重発火しないよう、
///   最後のイベントから <see cref="DebounceMs"/> ミリ秒待ってから 1 回だけ発火する。
/// - **エディタ自身の保存で再読込しない**。シーン保存はランタイムが
///   SAVE_SCENE 応答で非同期に書き出すため、時間の窓（保存開始〜完了＋余韻）と
///   内容ハッシュの 2 段で自分の書き込みを除外する。
/// - 内容が実際に変わっていないイベント（タイムスタンプだけの更新、
///   ウイルススキャン等による touch）はハッシュ比較で握り潰す。
///
/// 【再読込しないケース】
/// - Play 中（埋め込み / 別ウィンドウ問わず）: 予約しておき、Edit 復帰時に再読込する。
/// - エディタ側に未保存の編集がある: 破棄すると取り返しがつかないため再読込しない。
///   ステータスで通知し、ユーザーがメニューから明示的に再読込できるようにする。
///
/// 【スレッド】FileSystemWatcher のイベントはスレッドプールで発火するため、
/// 内部状態の更新はすべて Dispatcher（UI スレッド）へ載せ替えて直列化する。
///
/// 【依存の持ち方】本クラスは MainWindow・RuntimeManager・UI を直接知らない。
/// 「有効か」「未保存か」「Play 中か」「読み込む」「状態を伝える」をコールバックで
/// 受け取り、監視とスケジューリングという単一責務だけを持つ。
/// </summary>
public sealed class SceneAutoReloader : IDisposable
{
    // ── 定数（マジックナンバー禁止）──────────────────────────────

    /// <summary>最後のファイル変更からこの時間だけ待ってから再読込する（連続書き込みの集約）。</summary>
    private const int DebounceMs = 600;

    /// <summary>
    /// 自分の保存（SAVE_SCENE）を出してから応答が来るまで、自分の書き込みとみなす上限。
    /// 応答が失われても永久に監視が死なないための保険。
    /// </summary>
    private const int SelfSaveMaxWaitMs = 15000;

    /// <summary>
    /// 保存完了通知のあと、この時間だけは引き続き自分の書き込みとみなす。
    /// ファイル監視イベントは書き込み完了より遅れて届くことがあるため。
    /// </summary>
    private const int SelfSaveTailMs = 1500;

    /// <summary>
    /// ハッシュ計算に失敗（＝まだ書き込み中でロックされている）したときの再試行回数。
    /// 上限を超えたら失敗として通知し、無限ループにしない。
    /// </summary>
    private const int HashRetryLimit = 5;

    /// <summary>監視対象の拡張子。</summary>
    private const string SceneExtension = ".scene";

    // ── ステータス文言（UI 文言も 1 箇所に集約する）────────────────

    private const string MessageRunning = "シーンを再読込中…";
    private const string MessageSuccess = "シーンを再読込しました";
    private const string MessagePendingPlay = "Play 停止後にシーンを再読込します";
    private const string MessageDirty =
        "シーンがディスク上で変更されましたが未保存の編集があるため再読込しません（メニューから再読込可）";

    // ── 依存（コールバック）───────────────────────────────────────

    /// <summary>自動再読込が有効かどうか（エディタ設定のトグル）。</summary>
    private readonly Func<bool> _isEnabled;

    /// <summary>エディタ側に未保存の編集があるか。</summary>
    private readonly Func<bool> _isDirty;

    /// <summary>Play 中（埋め込み / 別ウィンドウ）かどうか。</summary>
    private readonly Func<bool> _isPlaying;

    /// <summary>シーンを読み込む（＝ファイルを開いたときと同じ経路）。</summary>
    private readonly Action<string> _loadScene;

    /// <summary>進行状態を UI（ステータス表示・ログ）へ伝える。</summary>
    private readonly Action<SceneReloadStatus, string> _report;

    // ── 内部状態（すべて UI スレッドからのみ触る）──────────────────

    private readonly Dispatcher     _dispatcher;
    private readonly DispatcherTimer _debounceTimer;

    /// <summary>監視対象シーンの絶対パス（null = 監視していない）。</summary>
    private string? _scenePath;

    private FileSystemWatcher? _watcher;

    /// <summary>最後に「エディタが知っている」ファイル内容のハッシュ（no-op イベントの除外用）。</summary>
    private string? _knownHash;

    /// <summary>Play 中に検出した変更（Edit 復帰時に 1 回だけ再読込する）。</summary>
    private bool _pendingWhilePlaying;

    /// <summary>自分の保存（SAVE_SCENE）が応答待ちか。</summary>
    private bool _selfSaveInFlight;

    /// <summary>応答待ちを打ち切る時刻（UTC）。応答喪失で監視が死なないための保険。</summary>
    private DateTime _selfSaveDeadlineUtc;

    /// <summary>保存完了後、まだ自分の書き込みとみなす時刻（UTC）。</summary>
    private DateTime _selfSaveTailUntilUtc;

    /// <summary>ハッシュ計算の連続失敗回数（<see cref="HashRetryLimit"/> で打ち切る）。</summary>
    private int _hashRetryCount;

    // ── 公開 API ────────────────────────────────────────────────

    /// <summary>
    /// 自動再読込が実際に機能しているか（監視の起動に成功し、かつ設定がオン）。
    /// </summary>
    public bool IsEnabled => _watcher is not null && _isEnabled();

    /// <summary>現在監視しているシーンのパス（null = 監視していない）。</summary>
    public string? ScenePath => _scenePath;

    /// <param name="dispatcher">UI スレッドの Dispatcher（タイマー・コールバックの実行先）。</param>
    /// <param name="isEnabled">自動再読込設定の現在値を返す関数。</param>
    /// <param name="isDirty">エディタ側に未保存の編集があるかを返す関数。</param>
    /// <param name="isPlaying">Play 中かどうかを返す関数。</param>
    /// <param name="loadScene">シーン読み込み（ファイルを開くのと同じ経路）。</param>
    /// <param name="report">進行状態の通知先。</param>
    public SceneAutoReloader(
        Dispatcher dispatcher,
        Func<bool> isEnabled,
        Func<bool> isDirty,
        Func<bool> isPlaying,
        Action<string> loadScene,
        Action<SceneReloadStatus, string> report)
    {
        _dispatcher = dispatcher;
        _isEnabled  = isEnabled;
        _isDirty    = isDirty;
        _isPlaying  = isPlaying;
        _loadScene  = loadScene;
        _report     = report;

        // デバウンス用タイマー（UI スレッド）。イベントが来るたびに Stop→Start で
        // 期限を延ばし、静まってから 1 回だけ発火させる。
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(DebounceMs),
        };
        _debounceTimer.Tick += (_, _) => { _debounceTimer.Stop(); Fire(); };
    }

    /// <summary>
    /// 監視対象のシーンを張り替える（シーンを開いた / 名前を付けて保存した直後に呼ぶ）。
    /// 現在のファイル内容を「既知の内容」として取り込むため、
    /// 直後に届く自分由来のイベントは no-op として捨てられる。
    /// </summary>
    /// <param name="path">シーンの絶対パス。null / 空 なら監視を止める。</param>
    public void SetScenePath(string? path)
    {
        // 予約は監視対象が変わった時点で無効（旧シーンの変更を新シーンへ適用しない）
        _pendingWhilePlaying = false;
        _hashRetryCount      = 0;
        _debounceTimer.Stop();

        _scenePath = string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);
        _knownHash = _scenePath is null ? null : TryComputeHash(_scenePath);

        StartWatching();
    }

    /// <summary>
    /// エディタ自身のシーン保存（SAVE_SCENE 送信）が始まったことを通知する。
    /// これ以降 <see cref="NotifySelfSaveCompleted"/> ＋余韻の間に届く変更は
    /// 自分の書き込みとみなし、再読込しない。
    /// </summary>
    public void NotifySelfSaveStarted()
    {
        _selfSaveInFlight     = true;
        _selfSaveDeadlineUtc  = DateTime.UtcNow.AddMilliseconds(SelfSaveMaxWaitMs);
        _selfSaveTailUntilUtc = _selfSaveDeadlineUtc;
    }

    /// <summary>
    /// エディタ自身のシーン保存が完了（成功・失敗どちらも）したことを通知する。
    /// 監視イベントは書き込みより遅れて届くため、余韻（<see cref="SelfSaveTailMs"/>）を残す。
    /// </summary>
    public void NotifySelfSaveCompleted()
    {
        _selfSaveInFlight     = false;
        _selfSaveTailUntilUtc = DateTime.UtcNow.AddMilliseconds(SelfSaveTailMs);

        // 保存後の内容を「既知」として取り込む。以後の自分由来イベントは
        // 余韻が切れてもハッシュ一致で捨てられる。
        if (_scenePath is not null)
        {
            var hash = TryComputeHash(_scenePath);
            if (hash is not null) _knownHash = hash;
        }
    }

    /// <summary>
    /// Play が終了して Edit へ戻ったことを通知する。
    /// Play 中に検出した変更があれば、ここで初めて再読込する。
    /// </summary>
    public void NotifyReturnedToEdit()
    {
        if (!_pendingWhilePlaying) return;
        _pendingWhilePlaying = false;
        Schedule();
    }

    /// <summary>
    /// 外部から再読込を要求する（メニューからの手動再読込用）。
    /// 設定トグル・ダーティ判定を無視して即座に読み込む。Play 中だけは
    /// ランタイムの状態を壊さないよう予約に回す。
    /// </summary>
    public void ForceReload()
    {
        if (_scenePath is null) return;

        if (_isPlaying())
        {
            _pendingWhilePlaying = true;
            _report(SceneReloadStatus.Skipped, MessagePendingPlay);
            return;
        }

        Reload();
    }

    // ── 内部処理 ────────────────────────────────────────────────

    /// <summary>
    /// 現在の <see cref="_scenePath"/> の親ディレクトリを監視する。
    /// ファイル単体ではなくディレクトリを監視するのは、多くのツールが
    /// 「一時ファイルへ書いてリネーム」で保存し、元ファイルのハンドルが
    /// 差し替わるとファイル単体の監視が外れてしまうため。
    /// </summary>
    private void StartWatching()
    {
        StopWatching();
        if (_scenePath is null) return;

        try
        {
            var dir      = Path.GetDirectoryName(_scenePath);
            var fileName = Path.GetFileName(_scenePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                EditorLog.Write($"[SceneAutoReload] シーンのフォルダが存在しないため監視しません: {_scenePath}");
                return;
            }

            _watcher = new FileSystemWatcher(dir)
            {
                // 対象は「今開いているシーンファイル」1 件だけ
                Filter                = fileName,
                IncludeSubdirectories = false,
                // 書き込み（LastWrite/Size）と、作成・リネームによる差し替え（FileName）を拾う
                NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += (_, e) => OnFsEvent(e.FullPath);
            _watcher.Created += (_, e) => OnFsEvent(e.FullPath);
            // 「.tmp → .scene」のリネーム保存も差し替えとして拾う
            _watcher.Renamed += (_, e) => OnFsEvent(e.FullPath);
            _watcher.EnableRaisingEvents = true;
            EditorLog.Write($"[SceneAutoReload] 監視開始: {_scenePath}");
        }
        catch (Exception ex)
        {
            _watcher = null;
            EditorLog.Write($"[SceneAutoReload] 監視を開始できませんでした: {ex.Message}");
        }
    }

    /// <summary>監視を止める（張り替え・破棄時）。</summary>
    private void StopWatching()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>ファイル監視イベント（別スレッド）を UI スレッドの予約処理へ載せ替える。</summary>
    private void OnFsEvent(string? fullPath)
    {
        if (!IsTargetFile(fullPath)) return;
        _dispatcher.InvokeAsync(Schedule);
    }

    /// <summary>監視対象シーンそのもののイベントかどうか。</summary>
    private bool IsTargetFile(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || _scenePath is null) return false;
        if (!fullPath.EndsWith(SceneExtension, StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(Path.GetFullPath(fullPath), _scenePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>再読込をデバウンス予約する（UI スレッド）。</summary>
    private void Schedule()
    {
        if (!IsEnabled) return;

        // 最後のイベントから DebounceMs 静まるまで待つ
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    /// <summary>
    /// デバウンス満了時の判定本体。
    /// 「no-op → 自分の保存 → Play 中 → 未保存編集あり」の順に除外し、
    /// どれにも当たらなければ再読込する。
    /// </summary>
    private void Fire()
    {
        if (!IsEnabled || _scenePath is null) return;

        // 1. 内容ハッシュを取る。まだ書き込み中でロックされていることがあるため、
        //    読めなければ少し待って再試行する（回数上限あり）。
        var hash = TryComputeHash(_scenePath);
        if (hash is null)
        {
            if (++_hashRetryCount <= HashRetryLimit)
            {
                _debounceTimer.Start();
                return;
            }
            _hashRetryCount = 0;
            _report(SceneReloadStatus.Failed, "シーンファイルを読み取れませんでした（再読込を中止）");
            return;
        }
        _hashRetryCount = 0;

        // 2. 内容が変わっていないイベント（touch・属性変更など）は無視する。
        if (string.Equals(hash, _knownHash, StringComparison.Ordinal)) return;

        // 3. エディタ自身の保存による書き込みは再読込しない。
        //    ここで既知ハッシュを更新しておくと、余韻切れ後の重複イベントも捨てられる。
        if (IsSelfSaveWindow())
        {
            _knownHash = hash;
            return;
        }

        // 4. Play 中はランタイムの実行状態を壊さないため読み込まない。
        //    予約だけしておき、Edit 復帰時に再判定する（ハッシュは未確定のまま残す）。
        if (_isPlaying())
        {
            _pendingWhilePlaying = true;
            _report(SceneReloadStatus.Skipped, MessagePendingPlay);
            return;
        }

        // 5. 未保存の編集があるときは破棄になるため再読込しない。
        //    既知ハッシュを更新しないので、ユーザーが保存すれば
        //    エディタ側の内容で上書きされる（＝この差分は解消する）。
        if (_isDirty())
        {
            _report(SceneReloadStatus.Skipped, MessageDirty);
            return;
        }

        // 6. 実際に読み直す
        _knownHash = hash;
        Reload();
    }

    /// <summary>
    /// シーンを読み直す。読み込み経路は「ファイルを開いたとき」と完全に同じ
    /// （LoadScene）で、シーン設定・ビュー状態の再適用もそちらに任せる。
    /// </summary>
    private void Reload()
    {
        if (_scenePath is null) return;

        _report(SceneReloadStatus.Running, MessageRunning);
        try
        {
            _loadScene(_scenePath);
            _report(SceneReloadStatus.Success, MessageSuccess);
        }
        catch (Exception ex)
        {
            _report(SceneReloadStatus.Failed, $"シーンの再読込に失敗しました: {ex.Message}");
        }
    }

    /// <summary>
    /// いま届いている書き込みを「エディタ自身の保存」とみなす期間か。
    /// 保存は SAVE_SCENE の応答待ち（＝ランタイムが書き出す）なので、
    /// 送信〜完了通知＋余韻を 1 つの窓として扱う。
    /// </summary>
    private bool IsSelfSaveWindow()
    {
        var now = DateTime.UtcNow;
        if (_selfSaveInFlight && now < _selfSaveDeadlineUtc) return true;
        return now < _selfSaveTailUntilUtc;
    }

    /// <summary>
    /// ファイル内容の SHA-256 を 16 進文字列で返す。
    /// 読めない（書き込み中でロックされている等）場合は null。
    /// </summary>
    private static string? TryComputeHash(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (Exception)
        {
            // ファイルが無い / ロック中 / 権限不足。呼び出し側が再試行または中止を決める。
            return null;
        }
    }

    /// <summary>監視とタイマーを停止する（エディタ終了時）。</summary>
    public void Dispose()
    {
        _debounceTimer.Stop();
        StopWatching();
    }
}
