// ============================================================
//  LoreLockService.cs — ILockService の Lore 実装
//
//  【役割】
//  ロックの一覧・取得・解放・照会を Lore の lock file コマンドへ翻訳する。
//
//  【吸収している罠】
//  ・lock acquire は **既に取得済みでも成功を返す**。
//    → acquire の前後で lock status を取り、所有者の変化から結果を決める。
//  ・サーバ認証が無い構成では所有者が `<unknown>` になる。
//    → 「自分のもの」と決めつけず、HeldByUnknown として上へ返す。
//  ・lock status はロックされていないパスの行を返さないことがある。
//    → 照会したパスと同じ件数・同じ順序になるよう Translator が補う。
//
//  【今回入れないもの】
//  強制（保存ゲート）。所有者が <unknown> になり得る状態で保存を止めると、
//  自分のロックでも保存できなくなる。認証（docs/vcs_lore.md 4 章）が
//  決まるまでは「表示と取得・解放」に留める。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.VersionControl.Abstractions;
using SEEDEditor.VersionControl.Lore.Backend;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Scheduling;

namespace SEEDEditor.VersionControl.Lore;

/// <summary>
/// Lore のファイルロックを扱う実装。
/// </summary>
public sealed class LoreLockService : ILockService
{
    /// <summary>ロック一覧の操作名（診断ログ用）。</summary>
    private const string OP_LOCK_LIST = "lock-list";

    /// <summary>ロック取得の操作名。</summary>
    private const string OP_LOCK_ACQUIRE = "lock-acquire";

    /// <summary>ロック解放の操作名。</summary>
    private const string OP_LOCK_RELEASE = "lock-release";

    /// <summary>ロック照会の操作名。</summary>
    private const string OP_LOCK_STATUS = "lock-status";

    /// <summary>Lore の呼び出し先。</summary>
    private readonly ILoreBackend _backend;

    /// <summary>操作を実行する場所（プロバイダと同じものを共有する）。</summary>
    private readonly IVersionControlScheduler _scheduler;

    /// <summary>タイムアウト等の設定。</summary>
    private readonly VersionControlSettings _settings;

    /// <summary>
    /// バックエンド・スケジューラ・設定を指定して生成する。
    ///
    /// <para>
    /// スケジューラは <see cref="LoreProvider"/> と共有する。別のスケジューラを
    /// 渡すと、ロック操作と送信が同じ作業コピーへ並行して走ってしまう。
    /// </para>
    /// </summary>
    /// <param name="backend">Lore の呼び出し先。</param>
    /// <param name="scheduler">操作を実行する場所。</param>
    /// <param name="settings">設定。</param>
    public LoreLockService(
        ILoreBackend backend, IVersionControlScheduler scheduler,
        VersionControlSettings settings)
    {
        _backend   = backend   ?? throw new ArgumentNullException(nameof(backend));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _settings  = settings  ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>常に真（この型はバージョン管理下でのみ生成される）。</summary>
    public bool IsAvailable => true;

    /// <summary>現在のブランチのロックを一覧する。</summary>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult<IReadOnlyList<LockInfo>>> ListAsync(
        CancellationToken cancellationToken = default)
        => RunAsync(
            OP_LOCK_LIST,
            cancellationToken,
            token =>
            {
                var result = _backend.LockQuery(token);
                if (!result.Call.Succeeded)
                {
                    // 型引数は明示する。省略すると値なしの overload が選ばれてしまう。
                    return FailOrOffline<IReadOnlyList<LockInfo>>(
                        result.Call, VersionControlMessages.LOCK_LIST_FAILED);
                }

                return VersionControlResult<IReadOnlyList<LockInfo>>.Ok(
                    LoreLockTranslator.ToLockInfos(result.Rows, _backend.Identity),
                    VersionControlMessages.LOCK_LIST_OK);
            });

    /// <summary>ロックを取得する。</summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult<IReadOnlyList<LockAcquireResult>>> AcquireAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
    {
        if (relativePaths is null || relativePaths.Count == 0)
        {
            return Task.FromResult(VersionControlResult<IReadOnlyList<LockAcquireResult>>.Create(
                VersionControlOutcome.NothingToDo,
                Array.Empty<LockAcquireResult>(),
                VersionControlMessages.LOCK_NO_PATHS));
        }

        var paths = new List<string>(relativePaths);

        return RunAsync(
            OP_LOCK_ACQUIRE,
            cancellationToken,
            token => AcquireCore(paths, token));
    }

    /// <summary>
    /// ロック取得の本体。acquire の前後で状態を取り、結果を確定させる。
    /// </summary>
    /// <param name="paths">リポジトリ相対パス。</param>
    /// <param name="token">中断用。</param>
    private VersionControlResult<IReadOnlyList<LockAcquireResult>> AcquireCore(
        IReadOnlyList<string> paths, CancellationToken token)
    {
        var identity = _backend.Identity;

        // 1) 取得前の状態。「もともと自分が持っていた」を区別するために要る。
        //    ここが失敗しても取得自体は試みられるので、失敗は null 扱いで続行する。
        var before = _backend.LockStatus(paths, token);
        var beforeByPath = before.Call.Succeeded
            ? ToLookup(LoreLockTranslator.ToLockInfosForPaths(paths, before.Rows, identity))
            : null;

        // 2) 取得を試みる。Lore は既に取得済みでも成功を返すので、
        //    この戻り値だけでは「取れた」と言えない。
        var acquire = _backend.LockAcquire(paths, token);

        // 3) 取得後の状態を読み、所有者から本当の結果を決める。
        var after = _backend.LockStatus(paths, token);
        if (!after.Call.Succeeded)
        {
            // 状態が読めないと結果を判定できない。成功と偽らずに失敗を返す。
            return FailOrOffline<IReadOnlyList<LockAcquireResult>>(
                after.Call, VersionControlMessages.LOCK_ACQUIRE_FAILED);
        }

        var afterStates = LoreLockTranslator.ToLockInfosForPaths(paths, after.Rows, identity);

        var results = new List<LockAcquireResult>(afterStates.Count);
        var acquiredCount = 0;
        foreach (var state in afterStates)
        {
            // 取得前の状態が読めていれば「もともと自分のものだったか」を渡す。
            // 読めていなければ null（= 判断材料なし）で、Acquired 扱いになる。
            LockInfo? previous = null;
            if (beforeByPath is not null
                && beforeByPath.TryGetValue(state.Path, out var found))
            {
                previous = found;
            }

            var result = LoreLockTranslator.ToAcquireResult(acquire, previous, state);
            results.Add(result);
            if (result.CanEdit) acquiredCount++;
        }

        return VersionControlResult<IReadOnlyList<LockAcquireResult>>.Ok(
            results,
            string.Format(VersionControlMessages.LOCK_ACQUIRE_OK_FORMAT, acquiredCount));

        // パスをキーにした引き当て表を作る（Windows なので大文字小文字は区別しない）。
        static Dictionary<string, LockInfo> ToLookup(IReadOnlyList<LockInfo> items)
        {
            var map = new Dictionary<string, LockInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items) map[item.Path] = item;
            return map;
        }
    }

    /// <summary>ロックを解放する。</summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult> ReleaseAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
    {
        if (relativePaths is null || relativePaths.Count == 0)
        {
            return Task.FromResult(VersionControlResult.NothingToDo(
                VersionControlMessages.LOCK_NO_PATHS));
        }

        var paths = new List<string>(relativePaths);

        return RunAsync(
            OP_LOCK_RELEASE,
            cancellationToken,
            token =>
            {
                var call = _backend.LockRelease(paths, token);
                if (!call.Succeeded)
                    return FailOrOffline(call, VersionControlMessages.LOCK_RELEASE_FAILED);

                return VersionControlResult.Success(
                    string.Format(VersionControlMessages.LOCK_RELEASE_OK_FORMAT, paths.Count));
            });
    }

    /// <summary>ロック状態を照会する。</summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult<IReadOnlyList<LockInfo>>> GetStatusAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
    {
        if (relativePaths is null || relativePaths.Count == 0)
        {
            return Task.FromResult(VersionControlResult<IReadOnlyList<LockInfo>>.Create(
                VersionControlOutcome.NothingToDo,
                Array.Empty<LockInfo>(),
                VersionControlMessages.LOCK_NO_PATHS));
        }

        var paths = new List<string>(relativePaths);

        return RunAsync(
            OP_LOCK_STATUS,
            cancellationToken,
            token =>
            {
                var result = _backend.LockStatus(paths, token);
                if (!result.Call.Succeeded)
                {
                    return FailOrOffline<IReadOnlyList<LockInfo>>(
                        result.Call, VersionControlMessages.LOCK_STATUS_FAILED);
                }

                return VersionControlResult<IReadOnlyList<LockInfo>>.Ok(
                    LoreLockTranslator.ToLockInfosForPaths(
                        paths, result.Rows, _backend.Identity),
                    VersionControlMessages.LOCK_STATUS_OK);
            });
    }

    // ── 共通ヘルパー ────────────────────────────────────────

    /// <summary>
    /// 失敗を結果へ畳む。接続できないことが原因なら RequiresConnection にする
    /// （ロックはすべてサーバ必須なので、オフラインでは必ずここへ来る）。
    /// </summary>
    /// <param name="call">Lore の呼び出し結果（失敗）。</param>
    /// <param name="message">利用者向けメッセージ。</param>
    private static VersionControlResult FailOrOffline(LoreCallResult call, string message)
    {
        if (call.WasCanceled)
        {
            return VersionControlResult.Create(
                VersionControlOutcome.Canceled, VersionControlMessages.CANCELED, call.Messages);
        }
        if (LoreConnectionDiagnosis.IsConnectionFailure(call))
        {
            return VersionControlResult.Create(
                VersionControlOutcome.RequiresConnection,
                VersionControlMessages.REQUIRES_CONNECTION, call.Messages);
        }
        return VersionControlResult.Failed(message, call.Messages);
    }

    /// <summary>失敗を値つき結果へ畳む（上の関数の値つき版）。</summary>
    /// <typeparam name="T">戻り値の型。</typeparam>
    /// <param name="call">Lore の呼び出し結果（失敗）。</param>
    /// <param name="message">利用者向けメッセージ。</param>
    private static VersionControlResult<T> FailOrOffline<T>(LoreCallResult call, string message)
        => VersionControlResult<T>.From(FailOrOffline(call, message));

    /// <summary>値を返さない操作をスケジューラで実行する。</summary>
    /// <param name="operationName">診断ログ用の操作名。</param>
    /// <param name="cancellationToken">呼び出し側の中断。</param>
    /// <param name="operation">実行本体。</param>
    private async Task<VersionControlResult> RunAsync(
        string operationName,
        CancellationToken cancellationToken,
        Func<CancellationToken, VersionControlResult> operation)
    {
        try
        {
            // ロックはすべてサーバ往復を伴うので、期限はリモート用を使う。
            return await _scheduler
                .RunAsync(operationName, operation,
                          _settings.RemoteOperationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return VersionControlResult.Create(
                VersionControlOutcome.Canceled, VersionControlMessages.CANCELED);
        }
        catch (Exception ex)
        {
            return VersionControlResult.Failed(
                $"{VersionControlMessages.UNEXPECTED_FAILURE} ({operationName})",
                new[] { ex.Message });
        }
    }

    /// <summary>値を返す操作をスケジューラで実行する。</summary>
    /// <typeparam name="T">戻り値の型。</typeparam>
    /// <param name="operationName">診断ログ用の操作名。</param>
    /// <param name="cancellationToken">呼び出し側の中断。</param>
    /// <param name="operation">実行本体。</param>
    private async Task<VersionControlResult<T>> RunAsync<T>(
        string operationName,
        CancellationToken cancellationToken,
        Func<CancellationToken, VersionControlResult<T>> operation)
    {
        try
        {
            return await _scheduler
                .RunAsync(operationName, operation,
                          _settings.RemoteOperationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return VersionControlResult<T>.Create(
                VersionControlOutcome.Canceled, default, VersionControlMessages.CANCELED);
        }
        catch (Exception ex)
        {
            return VersionControlResult<T>.Failed(
                $"{VersionControlMessages.UNEXPECTED_FAILURE} ({operationName})",
                new[] { ex.Message });
        }
    }
}
