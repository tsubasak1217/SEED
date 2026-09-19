using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Threading;

namespace SEEDEditor.Panels.ScriptEditor.DiskSync;

/// <summary>
/// 開いているタブのファイルがディスク上で書き換わった・消えたことを検知して、
/// デバウンスした 1 回の「点検して」通知を UI スレッドで上げる監視役。
///
/// 【設計】
/// - ファイル単体ではなく **親フォルダ** を監視する。
///   原子的保存（一時ファイルへ書いてからリネーム）やバージョン管理のブランチ切り替えでは
///   ファイルハンドルが差し替わるため、ファイル単体の監視では取りこぼす。
/// - どのファイルが変わったかは伝えない。ブランチ切り替えでは何百件ものイベントが
///   一斉に飛ぶため、個別に突き合わせるより「まとめて全タブを点検し直す」方が
///   単純で速く、取りこぼしも無い（開いているタブはせいぜい数十件）。
/// - バッファ溢れ（Error イベント）も同じ通知に落とす。溢れた＝大量に変わった、なので
///   全タブ点検がそのまま正しい応答になる。
///
/// 監視イベントはスレッドプールから飛んでくるため、Dispatcher で UI スレッドへ載せ替え、
/// 以降の状態はすべて UI スレッド上で直列に触る（ロック不要）。
/// 考え方は <see cref="SEEDEditor.Scene.SceneAutoReloader"/> と揃えてある。
/// </summary>
public sealed class ScriptDiskWatcher : IDisposable
{
    /// <summary>最後のイベントからこの時間静まったら点検を走らせる（ミリ秒）。</summary>
    private const int DebounceMs = 400;

    /// <summary>
    /// 1 フォルダあたりの監視バッファ（バイト）。
    /// 既定の 8KB はブランチ切り替えのような一斉書き換えで溢れやすいので広げる。
    /// 溢れても Error 経由で点検は走るが、無駄な取りこぼしは減らしておく。
    /// </summary>
    private const int WatcherBufferBytes = 64 * 1024;

    /// <summary>UI スレッドのディスパッチャ（監視イベントの載せ替え先）。</summary>
    private readonly Dispatcher _dispatcher;

    /// <summary>デバウンス後に呼ぶ「全タブを点検して」通知。UI スレッドで実行される。</summary>
    private readonly Action _onDiskActivity;

    /// <summary>デバウンス用タイマー（UI スレッドにバインド）。</summary>
    private readonly DispatcherTimer _debounceTimer;

    /// <summary>監視中のフォルダ（フォルダの絶対パス → 監視オブジェクト）。</summary>
    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 監視を張れなかったフォルダ（権限不足など）。
    /// 対象の張り直しはタブを開く・閉じる・保存するたびに走るので、
    /// 同じ失敗を毎回ログへ出すと Output が埋まる。ログは 1 フォルダ 1 回だけにする
    /// （張り直し自体は毎回試すので、権限が直れば次の機会に監視が始まる）。
    /// </summary>
    private readonly HashSet<string> _loggedFailures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 監視が無効になったフォルダ（Error を上げた＝フォルダごと消された・バッファ溢れ）。
    ///
    /// フォルダ自体が削除されると <see cref="FileSystemWatcher"/> は以後イベントを出さない。
    /// 捨てずに辞書へ残すと「もう死んでいるのに有るのでスキップ」になり、
    /// フォルダが戻っても二度と監視が復活しない。ブランチ切り替えでフォルダごと
    /// 入れ替わるのは本機能の主要ユースケースなので、必ず張り直せるようにする。
    ///
    /// 監視イベントはスレッドプールから来るので、ここへ積むだけにして、
    /// 実際の破棄と作り直しは UI スレッドの <see cref="SetWatchedFiles"/> で行う。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _brokenDirectories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// UI スレッドへの載せ替えがすでに 1 件予約されているか（0=無し / 1=有り）。
    /// 一斉書き換えで数千件のイベントが飛んでも、ディスパッチャのキューへ
    /// 積むのは 1 件だけにするための間引き。
    /// </summary>
    private int _dispatchPending;

    /// <summary>
    /// 破棄済みか（破棄後に飛んでくる遅延イベントを捨てるため）。
    /// UI スレッドで書き、監視イベント（スレッドプール）から読むので volatile にする。
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// 監視役を作る。この時点では何も監視しない
    /// （<see cref="SetWatchedFiles"/> で対象を与える）。
    /// </summary>
    /// <param name="dispatcher">UI スレッドのディスパッチャ。</param>
    /// <param name="onDiskActivity">デバウンス後に UI スレッドで呼ばれる点検要求。</param>
    public ScriptDiskWatcher(Dispatcher dispatcher, Action onDiskActivity)
    {
        _dispatcher     = dispatcher;
        _onDiskActivity = onDiskActivity;

        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(DebounceMs),
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            if (!_disposed) _onDiskActivity();
        };
    }

    /// <summary>
    /// 監視対象のファイル一覧を差し替える。
    /// ファイルが属するフォルダの集合を取り直し、不要になったフォルダの監視は止める。
    /// タブを開いた・閉じたタイミングで呼ぶ。
    /// </summary>
    /// <param name="filePaths">開いているタブのファイル絶対パス。</param>
    public void SetWatchedFiles(IEnumerable<string> filePaths)
    {
        if (_disposed) return;

        // 監視したいフォルダの集合を作る。
        // フォルダごと消えている場合は監視を張れないため除外する
        // （その場合はウィンドウのアクティブ化時の全タブ点検で拾う）。
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in filePaths)
        {
            string? dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(path)); }
            catch { continue; }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            wanted.Add(dir);
        }

        // 無効になった監視（フォルダごと消された等）を先に捨てる。
        // 捨てておけば、下の「新しく必要になったフォルダ」で作り直される。
        foreach (var dir in _brokenDirectories.Keys)
        {
            _brokenDirectories.TryRemove(dir, out _);
            if (!_watchers.TryGetValue(dir, out var broken)) continue;
            DisposeWatcher(broken);
            _watchers.Remove(dir);
        }

        // 不要になったフォルダの監視を止める
        foreach (var dir in new List<string>(_watchers.Keys))
        {
            if (wanted.Contains(dir)) continue;
            DisposeWatcher(_watchers[dir]);
            _watchers.Remove(dir);
        }

        // 新しく必要になったフォルダの監視を始める
        foreach (var dir in wanted)
        {
            if (_watchers.ContainsKey(dir)) continue;
            var watcher = TryCreateWatcher(dir);
            if (watcher is not null) _watchers[dir] = watcher;
        }
    }

    /// <summary>
    /// 1 フォルダ分の監視を作る。作れなければ null（フォルダが消えた直後・権限不足など）。
    /// </summary>
    /// <param name="directory">監視するフォルダの絶対パス。</param>
    /// <returns>監視オブジェクト。作れなければ null。</returns>
    private FileSystemWatcher? TryCreateWatcher(string directory)
    {
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                // フォルダ内のどのファイルが変わっても点検し直すので、種類では絞らない。
                // （開いているタブ以外の変化も拾うが、点検側がハッシュで弾くので無害）
                Filter                = "*",
                IncludeSubdirectories = false,
                // 書き込み（LastWrite/Size）と、作成・削除・リネームによる差し替え（FileName）
                NotifyFilter          = NotifyFilters.LastWrite
                                      | NotifyFilters.Size
                                      | NotifyFilters.FileName
                                      | NotifyFilters.CreationTime,
                InternalBufferSize    = WatcherBufferBytes,
            };
            watcher.Changed += (_, _) => Signal();
            watcher.Created += (_, _) => Signal();
            watcher.Deleted += (_, _) => Signal();
            watcher.Renamed += (_, _) => Signal();
            // Error = バッファ溢れ、またはフォルダごと消されて監視が無効になった。
            // どちらも「大量に変わった」ので全タブ点検へ落としつつ、
            // この監視は作り直しが必要なものとして印を付ける
            // （無効になった監視を残すと、フォルダが戻っても復活しない）。
            watcher.Error   += (_, _) =>
            {
                _brokenDirectories[directory] = 0;
                Signal();
            };
            watcher.EnableRaisingEvents = true;
            // 一度直ったら、次に失敗したときはまた知らせる
            _loggedFailures.Remove(directory);
            return watcher;
        }
        catch (Exception ex)
        {
            if (_loggedFailures.Add(directory))
                EditorLog.Write(
                    $"スクリプトのディスク監視を開始できませんでした [{directory}]: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 監視イベント（スレッドプール）を受けて、UI スレッドでのデバウンス開始を予約する。
    /// すでに予約済みなら何もしない（一斉書き換えでキューを溢れさせないため）。
    /// </summary>
    private void Signal()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _dispatchPending, 1) == 1) return;

        try
        {
            _dispatcher.InvokeAsync(() =>
            {
                Interlocked.Exchange(ref _dispatchPending, 0);
                if (_disposed) return;
                // 最後のイベントから DebounceMs 静まるまで待ってから点検する
                _debounceTimer.Stop();
                _debounceTimer.Start();
            });
        }
        catch (Exception)
        {
            // アプリ終了中でディスパッチャが閉じている。監視イベントで落とさない。
            Interlocked.Exchange(ref _dispatchPending, 0);
        }
    }

    /// <summary>監視を全て止めて破棄する。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceTimer.Stop();
        foreach (var watcher in _watchers.Values) DisposeWatcher(watcher);
        _watchers.Clear();
    }

    /// <summary>1 つの監視を安全に止めて破棄する。</summary>
    private static void DisposeWatcher(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch { /* 破棄時の失敗は無視（すでに無効化されている場合がある） */ }
    }
}
