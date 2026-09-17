// ============================================================
//  IVersionControlScheduler.cs — バージョン管理の操作を走らせる場所の抽象
//
//  【なぜ要るのか（守りたい 2 つの性質）】
//  1. UI スレッドを塞がない。
//     LoreVcs の .Wait() は同期ブロッキングで、gRPC 往復（実測 350 ms）を含む。
//     UI スレッドで呼ぶとエディタが固まる。
//  2. 同じ作業コピーへの操作を並行させない。
//     Lore は作業コピーのメタデータ（.lore/）を書き換えるため、
//     status と commit が同時に走ると壊れ得る。スレッドプールへ投げるだけでは
//     並行してしまうので、**1 本のワーカーで直列に**実行する。
//
//  【テストで差し替える理由】
//  単体テストではスレッドを跨がず、その場で同期実行したい（失敗時の
//  スタックトレースが読めるのと、テストが非決定的にならないため）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.VersionControl.Scheduling;

/// <summary>
/// バージョン管理の操作を実行する場所。
/// </summary>
public interface IVersionControlScheduler : IDisposable
{
    /// <summary>
    /// 操作を 1 件実行する。実装は他の操作と重ならないことを保証する。
    /// </summary>
    /// <typeparam name="T">戻り値の型。</typeparam>
    /// <param name="operationName">診断ログ用の操作名。</param>
    /// <param name="operation">
    /// 実行本体（同期）。渡される <see cref="CancellationToken"/> は
    /// 呼び出し側の中断とタイムアウトを統合したもの。
    /// </param>
    /// <param name="timeout">この操作の期限。</param>
    /// <param name="cancellationToken">呼び出し側の中断。</param>
    /// <returns>実行結果。中断・期限切れは <see cref="OperationCanceledException"/>。</returns>
    Task<T> RunAsync<T>(
        string operationName,
        Func<CancellationToken, T> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
