// ============================================================
//  InlineScheduler.cs — 呼び出したスレッドでそのまま実行するスケジューラ
//
//  【役割】
//  単体テスト用。スレッドを跨がないので、
//    ・失敗したときのスタックトレースがそのまま読める
//    ・実行順が確定するのでテストが非決定的にならない
//    ・タイムアウト待ちでテストが遅くならない
//  という利点がある。
//
//  【本番で使わないこと】
//  UI スレッドから呼ぶと Lore の同期ブロッキングでエディタが固まる。
//  本番は必ず <see cref="SerialWorkerScheduler"/> を使う。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない。
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.VersionControl.Scheduling;

/// <summary>
/// 呼び出したスレッドで即座に実行するスケジューラ（テスト用）。
/// </summary>
public sealed class InlineScheduler : IVersionControlScheduler
{
    /// <summary>
    /// 操作をその場で実行する。<paramref name="timeout"/> は無視する
    /// （同期実行なので期限を設ける意味が無い）。
    /// </summary>
    /// <typeparam name="T">戻り値の型。</typeparam>
    /// <param name="operationName">未使用（診断名）。</param>
    /// <param name="operation">実行本体。</param>
    /// <param name="timeout">未使用。</param>
    /// <param name="cancellationToken">呼び出し側の中断。</param>
    public Task<T> RunAsync<T>(
        string operationName,
        Func<CancellationToken, T> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        try
        {
            return Task.FromResult(operation(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    /// <summary>解放するものは無い。</summary>
    public void Dispose() { }
}
