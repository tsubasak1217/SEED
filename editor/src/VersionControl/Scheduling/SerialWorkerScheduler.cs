// ============================================================
//  SerialWorkerScheduler.cs — 専用スレッド 1 本で操作を直列に実行する
//
//  【役割】
//  投入された操作を FIFO で 1 件ずつ実行する。UI スレッドは Task を await するだけで、
//  Lore の同期ブロッキング呼び出しに巻き込まれない。
//
//  【なぜスレッドプールではなく専用スレッドか】
//  ・Lore の呼び出しは秒単位でブロックする。スレッドプールのスレッドを長時間
//    占有すると、エディタの他の非同期処理（サムネイル生成・IPC）が枯渇する。
//  ・SemaphoreSlim で直列化する案もあるが、その場合も待ち側がプールスレッドを
//    掴んだまま待つので同じ問題が残る。
//  ・専用スレッドなら「Lore 用の枠は常に 1 本」と上限がはっきりする。
//
//  【キャンセルとタイムアウトについて（正直な限界）】
//  キャンセルは **キューで待っている間にだけ効く**。実行が始まってしまった
//  Lore の呼び出しは中断できない（LoreVcs の .Wait() に中断手段が無いため）。
//  タイムアウトすると呼び出し側には OperationCanceledException を返すが、
//  ワーカーは走り続け、完了してから次の操作へ進む。放置すると結果を捨てた
//  操作が積み上がるので、タイムアウトは「UI を無限に待たせない」ための保険であり、
//  「Lore を止める」機能ではないと理解して使うこと。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.VersionControl.Scheduling;

/// <summary>
/// 専用スレッド 1 本で操作を直列に実行するスケジューラ。
/// </summary>
public sealed class SerialWorkerScheduler : IVersionControlScheduler
{
    /// <summary>ワーカースレッドの名前（デバッガのスレッド一覧で識別するため）。</summary>
    private const string WORKER_THREAD_NAME = "SEED VersionControl Worker";

    /// <summary>実行待ちの操作。<see cref="CompleteAdding"/> で終端する。</summary>
    private readonly BlockingCollection<Action> _queue = new();

    /// <summary>実行を担う唯一のスレッド。</summary>
    private readonly Thread _worker;

    /// <summary>破棄時にキュー待ちの操作を一斉に中断するための印。</summary>
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>ワーカー停止時に実行中の操作を待つ上限。</summary>
    private readonly TimeSpan _shutdownWait;

    /// <summary>診断ログの出力先（無ければ捨てる）。</summary>
    private readonly Action<string>? _log;

    /// <summary>多重 Dispose を防ぐ印。</summary>
    private int _disposed;

    /// <summary>
    /// ワーカーを起動する。
    /// </summary>
    /// <param name="shutdownWait">停止時に実行中の操作を待つ上限。</param>
    /// <param name="log">診断ログの出力先（省略可）。</param>
    public SerialWorkerScheduler(TimeSpan shutdownWait, Action<string>? log = null)
    {
        _shutdownWait = shutdownWait;
        _log          = log;

        _worker = new Thread(WorkerLoop)
        {
            // フォアグラウンドにするとエディタ終了時にプロセスが残るのでバックグラウンドにする。
            IsBackground = true,
            Name         = WORKER_THREAD_NAME,
        };
        _worker.Start();
    }

    /// <summary>
    /// 操作を 1 件キューへ入れ、実行結果を待てる Task を返す。
    /// </summary>
    /// <typeparam name="T">戻り値の型。</typeparam>
    /// <param name="operationName">診断ログ用の操作名。</param>
    /// <param name="operation">実行本体（同期）。</param>
    /// <param name="timeout">この操作の期限。</param>
    /// <param name="cancellationToken">呼び出し側の中断。</param>
    public Task<T> RunAsync<T>(
        string operationName,
        Func<CancellationToken, T> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // 既に破棄されていれば、キューへ入れずに即座に中断として返す
        // （プロジェクトを閉じた後の遅れた呼び出しで例外を投げないため）。
        if (Volatile.Read(ref _disposed) != 0)
            return Task.FromCanceled<T>(new CancellationToken(canceled: true));

        // 呼び出し側の中断・破棄・タイムアウトを 1 本のトークンへまとめる。
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
        linked.CancelAfter(timeout);

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // タイムアウト・中断が起きたら、ワーカーの完了を待たずに呼び出し側を解放する。
        // （ワーカー側は走り切ってから次へ進む。上のコメントの「正直な限界」を参照）
        var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));

        try
        {
            _queue.Add(() =>
            {
                try
                {
                    // キューで待っている間に中断されていたら、実行せずに捨てる。
                    if (linked.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(linked.Token);
                        return;
                    }

                    completion.TrySetResult(operation(linked.Token));
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled(linked.Token);
                }
                catch (Exception ex)
                {
                    // 例外はここで Task へ載せる。プロバイダ側が受け取って結果型へ畳む。
                    _log?.Invoke($"[VCS] 操作が例外で終了しました ({operationName}): {ex.Message}");
                    completion.TrySetException(ex);
                }
                finally
                {
                    registration.Dispose();
                    linked.Dispose();
                }
            });
        }
        catch (Exception)
        {
            // Dispose との競合でキューが閉じられていた場合（InvalidOperationException /
            // ObjectDisposedException）。呼び出し側には中断として返す。
            registration.Dispose();
            linked.Dispose();
            completion.TrySetCanceled();
        }

        return completion.Task;
    }

    /// <summary>
    /// キューから 1 件ずつ取り出して実行し続ける（ワーカースレッドの本体）。
    /// </summary>
    private void WorkerLoop()
    {
        // CompleteAdding されるまでブロックしながら取り出す。
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            // work の中で全例外を握っているので、ここへは漏れてこない。
            work();
        }
    }

    /// <summary>
    /// ワーカーを停止する。待ち行列の操作は中断され、実行中の 1 件だけを
    /// <see cref="VersionControlSettings.ShutdownWait"/> まで待つ。
    /// </summary>
    public void Dispose()
    {
        // 2 回目以降は何もしない。
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // 先に待ち行列を中断させてから終端する（並び順が逆だと、
        // 終端後に取り出された操作が中断印を見ずに実行されてしまう）。
        try { _shutdown.Cancel(); } catch { /* 停止処理の失敗は無視 */ }
        try { _queue.CompleteAdding(); } catch { /* 既に終端済み */ }

        // 実行中の 1 件が終わるのを上限つきで待つ。
        // 超えたら待たない（エディタの終了を Lore の都合で止めない）。
        try { _worker.Join(_shutdownWait); } catch { /* 待機の失敗は無視 */ }

        try { _queue.Dispose(); }   catch { /* 後始末の失敗は無視 */ }
        try { _shutdown.Dispose(); } catch { /* 後始末の失敗は無視 */ }
    }
}
