// ============================================================
//  LoreProvider.cs — IVersionControlProvider の Lore 実装
//
//  【役割】
//  利用者向けの操作（送信 / 最新を取得 / 競合の解決 / ブランチ / 履歴）を、
//  Lore のコマンド列へ翻訳する。Lore の罠（docs/vcs_lore.md 3.1）を
//  吸収するのはこの層の仕事。
//
//  【吸収している罠と対応】
//  | 罠                                      | ここでの対応                                  |
//  |-----------------------------------------|-----------------------------------------------|
//  | `stage .` は走査しないと何も stage しない | FileStage(".", scan: true) で必ず走査する      |
//  | 未 stage の commit が空リビジョンを作る  | stage 直後の status で staged 件数を数え、0 なら止める |
//  | `sync` は競合しても成功を返す            | sync 後の status の flagConflict* で判定する   |
//  | `resolve mine/theirs` が逆             | LoreConflictResolutionMap で対応付ける          |
//  | 素のファイル移動は履歴が切れる            | NotifyMovedAsync → FileStageMove               |
//  | 既定の status はファイルシステムを見ない  | 保存時に FileDirty、開いた直後だけ scan        |
//  | `history --offline` は失敗               | 接続失敗を RequiresConnection へ畳む           |
//
//  【LoreVcs を参照しない理由】
//  この型が持つのは判断ロジックだけで、Lore の呼び出しは ILoreBackend に委ねる。
//  そのおかげで上記の判断すべてを実サーバ無しの単体テストで固定できる。
//
//  【スレッド】
//  公開メソッドはすべて IVersionControlScheduler 経由で実行される。
//  本番は専用スレッド 1 本（直列）なので、同じ作業コピーへの操作は重ならない。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.VersionControl.Abstractions;
using SEEDEditor.VersionControl.Lore.Backend;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Scheduling;

namespace SEEDEditor.VersionControl.Lore;

/// <summary>
/// Lore を使うバージョン管理プロバイダ。
/// </summary>
public sealed class LoreProvider : IVersionControlProvider
{
    // ── 操作名（診断ログ用。スケジューラへ渡す）──────────────

    /// <summary>状態取得の操作名。</summary>
    private const string OP_STATUS = "status";

    /// <summary>変更通知の操作名。</summary>
    private const string OP_NOTIFY_CHANGED = "notify-changed";

    /// <summary>移動通知の操作名。</summary>
    private const string OP_NOTIFY_MOVED = "notify-moved";

    /// <summary>送信の操作名。</summary>
    private const string OP_SUBMIT = "submit";

    /// <summary>最新取得の操作名。</summary>
    private const string OP_FETCH = "fetch-latest";

    /// <summary>競合解決の操作名。</summary>
    private const string OP_RESOLVE = "resolve-conflicts";

    /// <summary>ブランチ一覧の操作名。</summary>
    private const string OP_BRANCH_LIST = "branch-list";

    /// <summary>ブランチ作成の操作名。</summary>
    private const string OP_BRANCH_CREATE = "branch-create";

    /// <summary>ブランチ切替の操作名。</summary>
    private const string OP_BRANCH_SWITCH = "branch-switch";

    /// <summary>履歴取得の操作名。</summary>
    private const string OP_HISTORY = "history";

    // ── フィールド ──────────────────────────────────────────

    /// <summary>Lore の呼び出し先。</summary>
    private readonly ILoreBackend _backend;

    /// <summary>操作を実行する場所（本番は専用スレッド 1 本）。</summary>
    private readonly IVersionControlScheduler _scheduler;

    /// <summary>タイムアウト等の設定。</summary>
    private readonly VersionControlSettings _settings;

    /// <summary>ロックの実装（同じバックエンドとスケジューラを共有する）。</summary>
    private readonly LoreLockService _locks;

    /// <summary>スケジューラをこの型が所有しているか（所有していれば Dispose で閉じる）。</summary>
    private readonly bool _ownsScheduler;

    /// <summary>多重 Dispose を防ぐ印。</summary>
    private bool _disposed;

    // ── 生成 ────────────────────────────────────────────────

    /// <summary>
    /// バックエンドとスケジューラを指定して生成する。
    /// </summary>
    /// <param name="backend">Lore の呼び出し先。</param>
    /// <param name="scheduler">操作を実行する場所。</param>
    /// <param name="settings">設定（省略時は既定値）。</param>
    /// <param name="ownsScheduler">
    /// スケジューラをこの型が所有するか。真なら <see cref="Dispose"/> で一緒に閉じる。
    /// </param>
    public LoreProvider(
        ILoreBackend backend,
        IVersionControlScheduler scheduler,
        VersionControlSettings? settings = null,
        bool ownsScheduler = false)
    {
        _backend       = backend   ?? throw new ArgumentNullException(nameof(backend));
        _scheduler     = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _settings      = settings  ?? VersionControlSettings.Default;
        _ownsScheduler = ownsScheduler;
        _locks         = new LoreLockService(_backend, _scheduler, _settings);
    }

    // ── 素性 ────────────────────────────────────────────────

    /// <summary>表示名。</summary>
    public string DisplayName => VersionControlMessages.PROVIDER_NAME_LORE;

    /// <summary>常に真（この型はバージョン管理下でのみ生成される）。</summary>
    public bool IsAvailable => true;

    /// <summary>作業コピーのルート。</summary>
    public string WorkingCopyRoot => _backend.WorkingCopyRoot;

    /// <summary>リモート URL。</summary>
    public string RemoteUrl => _backend.RemoteUrl;

    /// <summary>identity。</summary>
    public string Identity => _backend.Identity;

    /// <summary>ロックの境界。</summary>
    public ILockService Locks => _locks;

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>作業コピーの状態を取得する。</summary>
    /// <param name="mode">取得モード。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult<WorkingCopyStatus>> GetStatusAsync(
        StatusRefreshMode mode = StatusRefreshMode.ScanOffline,
        CancellationToken cancellationToken = default)
        => RunAsync(
            OP_STATUS,
            // オンライン取得は送信・取得より短い専用の期限（無応答のサーバで 2 分固まらないため）
            mode == StatusRefreshMode.ScanOnline
                ? _settings.OnlineStatusTimeout
                : _settings.LocalOperationTimeout,
            cancellationToken,
            token =>
            {
                var result = _backend.Status(ToRequest(mode), token);
                if (!result.Call.Succeeded)
                {
                    return ToFailure<WorkingCopyStatus>(
                        result.Call, VersionControlMessages.STATUS_FAILED);
                }

                return VersionControlResult<WorkingCopyStatus>.Ok(
                    LoreStatusTranslator.ToWorkingCopyStatus(result, mode),
                    VersionControlMessages.STATUS_OK);
            });

    /// <summary>取得モードを Lore の status 条件へ変換する。</summary>
    /// <param name="mode">取得モード。</param>
    private static LoreStatusRequest ToRequest(StatusRefreshMode mode) => new(
        // TrackedOnly 以外は走査する（記録済み dirty だけでは実体の変更を見逃すため）。
        Scan:    mode != StatusRefreshMode.TrackedOnly,
        // ScanOnline のときだけサーバへ繋ぐ。
        Offline: mode != StatusRefreshMode.ScanOnline);

    // ── 変更の記録 ──────────────────────────────────────────

    /// <summary>保存したファイルを「変わった」と記録する。</summary>
    /// <param name="absolutePaths">変更されたファイルの絶対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult> NotifyChangedAsync(
        IReadOnlyList<string> absolutePaths, CancellationToken cancellationToken = default)
    {
        // 変換は Lore を呼ばないのでワーカーへ入れる前に済ませ、
        // 対象 0 件ならキューを使わずに返す（保存のたびにワーカーを起こさない）。
        var relative = VersionControlPaths.ToRepositoryRelative(
            WorkingCopyRoot, absolutePaths, out var skipped);

        if (relative.Count == 0)
        {
            return Task.FromResult(VersionControlResult.NothingToDo(
                skipped.Count > 0
                    ? VersionControlMessages.PATH_OUTSIDE_WORKING_COPY
                    : VersionControlMessages.NOTIFY_NO_PATHS));
        }

        return RunAsync(
            OP_NOTIFY_CHANGED,
            _settings.LocalOperationTimeout,
            cancellationToken,
            token =>
            {
                var call = _backend.FileDirty(relative, token);
                return call.Succeeded
                    ? VersionControlResult.Success(VersionControlMessages.NOTIFY_CHANGED_OK)
                    : ToFailure(call, VersionControlMessages.NOTIFY_CHANGED_FAILED);
            });
    }

    /// <summary>ファイルの移動・改名を記録する。</summary>
    /// <param name="fromAbsolutePath">移動前の絶対パス。</param>
    /// <param name="toAbsolutePath">移動後の絶対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult> NotifyMovedAsync(
        string fromAbsolutePath, string toAbsolutePath,
        CancellationToken cancellationToken = default)
    {
        var from = VersionControlPaths.ToRepositoryRelative(WorkingCopyRoot, fromAbsolutePath);
        var to   = VersionControlPaths.ToRepositoryRelative(WorkingCopyRoot, toAbsolutePath);

        // 片方でも作業コピーの外なら Lore に渡せない。
        // 「プロジェクト外へ持ち出した」「外から持ち込んだ」は移動ではなく
        // 削除／追加として扱われるべきで、それは次の status が拾う。
        if (from is null || to is null)
        {
            return Task.FromResult(VersionControlResult.NothingToDo(
                VersionControlMessages.PATH_OUTSIDE_WORKING_COPY));
        }

        return RunAsync(
            OP_NOTIFY_MOVED,
            _settings.LocalOperationTimeout,
            cancellationToken,
            token =>
            {
                var call = _backend.FileStageMove(from, to, token);
                return call.Succeeded
                    ? VersionControlResult.Success(VersionControlMessages.NOTIFY_MOVED_OK)
                    : ToFailure(call, VersionControlMessages.NOTIFY_MOVED_FAILED);
            });
    }

    // ── 送信 ────────────────────────────────────────────────

    /// <summary>
    /// 変更を送信する（走査つき stage → 空判定 → commit → push）。
    /// </summary>
    /// <param name="message">コミットメッセージ。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult<SubmitReport>> SubmitAsync(
        string message, CancellationToken cancellationToken = default)
    {
        // メッセージ必須の判定は Lore を呼ぶ前に済ませる。
        if (string.IsNullOrWhiteSpace(message))
        {
            return Task.FromResult(VersionControlResult<SubmitReport>.Create(
                VersionControlOutcome.Failed,
                SubmitReport.Nothing,
                VersionControlMessages.SUBMIT_MESSAGE_REQUIRED));
        }

        return RunAsync(
            OP_SUBMIT,
            _settings.RemoteOperationTimeout,
            cancellationToken,
            token => SubmitCore(message, token));
    }

    /// <summary>
    /// 送信の本体（ワーカースレッド上で同期実行される）。
    /// </summary>
    /// <param name="message">コミットメッセージ。</param>
    /// <param name="token">中断用。</param>
    private VersionControlResult<SubmitReport> SubmitCore(
        string message, CancellationToken token)
    {
        // 1) 作業コピー全体を走査つきで stage する。
        //    `lore stage .` は走査しないと何も stage しない（罠）。
        var stage = _backend.FileStage(
            new[] { LoreBackendPaths.REPOSITORY_ROOT }, scan: true, cancellationToken: token);
        if (!stage.Succeeded)
            return ToFailure<SubmitReport>(stage, VersionControlMessages.SUBMIT_FAILED);

        // 2) stage の結果を確認する。走査は 1) で済んでいるので、
        //    ここは記録済みの状態を読むだけ（TrackedOnly）で足りる。
        var status = _backend.Status(
            new LoreStatusRequest(Scan: false, Offline: true), token);
        if (!status.Call.Succeeded)
            return ToFailure<SubmitReport>(status.Call, VersionControlMessages.SUBMIT_FAILED);

        // 3) 未解決の競合が残っていたら送らせない。
        //    ここで止めないと、マージを未解決のままコミットしてしまう。
        var conflicts = LoreStatusTranslator.ExtractUnresolvedConflicts(status.Files);
        if (conflicts.Count > 0)
        {
            return VersionControlResult<SubmitReport>.Create(
                VersionControlOutcome.Conflicted,
                SubmitReport.Nothing,
                VersionControlMessages.SUBMIT_BLOCKED_BY_CONFLICTS);
        }

        // 4) 空コミット防止。未 stage のまま commit すると Lore は
        //    空リビジョンを作って成功を返す（罠）。
        //
        //    ただし「stage するものが無い ＝ 送るものが無い」ではない。
        //    競合を解決した直後はマージのコミットが手元にあり、stage は空でも
        //    push すべき状態になっている。ここで NothingToDo を返すと、
        //    そのマージが永久に手元へ取り残される。
        //    そこでサーバへ 1 回だけ聞いて「手元が進んでいるか」を確かめる。
        var stagedCount = LoreStatusTranslator.CountStaged(status.Files);
        var commitNeeded = stagedCount > 0;

        if (!commitNeeded)
        {
            var online = _backend.Status(
                new LoreStatusRequest(Scan: false, Offline: false), token);

            var comparison = online.Call.Succeeded
                ? LoreStatusTranslator.ToRemoteComparison(
                      online.Revision, StatusRefreshMode.ScanOnline)
                : RemoteComparison.NotChecked;

            // 手元が進んでいない（または確認できない）なら、本当に送るものが無い。
            var hasUnpushed = comparison is RemoteComparison.LocalAhead
                                          or RemoteComparison.Diverged
                                          or RemoteComparison.RemoteBranchMissing;
            if (!hasUnpushed)
            {
                return VersionControlResult<SubmitReport>.Create(
                    VersionControlOutcome.NothingToDo,
                    SubmitReport.Nothing,
                    VersionControlMessages.SUBMIT_NOTHING);
            }
        }

        // 5) コミットする（stage するものがあったときだけ）。
        if (commitNeeded)
        {
            var commit = _backend.Commit(message, token);
            if (!commit.Succeeded)
                return ToFailure<SubmitReport>(commit, VersionControlMessages.SUBMIT_FAILED);
        }

        // 6) push する。
        var push = _backend.Push(token);
        if (!push.Succeeded)
        {
            // コミットは手元に残っている。利用者には「最新を取得してからもう一度」と伝える。
            var report = new SubmitReport(
                stagedCount, ReadRevisionNumber(token), message, committedButNotPushed: true);

            if (LorePushDiagnosis.NeedsSync(push, remoteWasAhead: false))
            {
                return VersionControlResult<SubmitReport>.Create(
                    VersionControlOutcome.NeedsSync, report,
                    VersionControlMessages.SUBMIT_NEEDS_SYNC, push.Messages);
            }

            if (push.WasCanceled)
            {
                return VersionControlResult<SubmitReport>.Create(
                    VersionControlOutcome.Canceled, report,
                    VersionControlMessages.CANCELED, push.Messages);
            }

            return VersionControlResult<SubmitReport>.Create(
                VersionControlOutcome.Failed, report,
                VersionControlMessages.SUBMIT_FAILED, push.Messages);
        }

        // 7) 送信後のリビジョン番号を読む（表示用。失敗しても送信の成否は変わらない）。
        //    stage するものが無かった場合は「手元のコミットを送った」ので文言を分ける
        //    （0 件送信しました、と出ると利用者が何も起きていないと誤解する）。
        return VersionControlResult<SubmitReport>.Ok(
            new SubmitReport(stagedCount, ReadRevisionNumber(token), message),
            commitNeeded
                ? string.Format(VersionControlMessages.SUBMIT_OK_FORMAT, stagedCount)
                : VersionControlMessages.SUBMIT_PUSH_ONLY_OK);
    }

    /// <summary>
    /// 現在のリビジョン番号を読む。失敗したら 0（表示用なので握りつぶす）。
    /// </summary>
    /// <param name="token">中断用。</param>
    private ulong ReadRevisionNumber(CancellationToken token)
    {
        var status = _backend.Status(new LoreStatusRequest(Scan: false, Offline: true), token);
        return status.Call.Succeeded ? status.Revision.RevisionNumber : 0UL;
    }

    // ── 最新を取得 ──────────────────────────────────────────

    /// <summary>最新を取得する（sync）。</summary>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult<SyncReport>> FetchLatestAsync(
        CancellationToken cancellationToken = default)
        => RunAsync(
            OP_FETCH,
            _settings.RemoteOperationTimeout,
            cancellationToken,
            FetchLatestCore);

    /// <summary>最新取得の本体（ワーカースレッド上で同期実行される）。</summary>
    /// <param name="token">中断用。</param>
    private VersionControlResult<SyncReport> FetchLatestCore(CancellationToken token)
    {
        var sync = _backend.Sync(token);
        if (!sync.Succeeded)
            return ToFailure<SyncReport>(sync, VersionControlMessages.FETCH_FAILED);

        // sync は競合しても成功を返す（罠）。状態を引き直して flagConflict* で判定する。
        var status = _backend.Status(new LoreStatusRequest(Scan: false, Offline: true), token);
        if (!status.Call.Succeeded)
            return ToFailure<SyncReport>(status.Call, VersionControlMessages.FETCH_FAILED);

        var conflicts = LoreStatusTranslator.ExtractUnresolvedConflicts(status.Files);
        var report    = new SyncReport(
            conflicts, status.Revision.RevisionNumber, status.Files.Count);

        if (conflicts.Count > 0)
        {
            return VersionControlResult<SyncReport>.Create(
                VersionControlOutcome.Conflicted, report,
                string.Format(VersionControlMessages.FETCH_CONFLICTED_FORMAT, conflicts.Count));
        }

        return VersionControlResult<SyncReport>.Ok(
            report,
            string.Format(VersionControlMessages.FETCH_OK_FORMAT, report.UpdatedFileCount));
    }

    // ── 競合の解決 ──────────────────────────────────────────

    /// <summary>競合を解決し、残りが無ければマージをコミットする。</summary>
    /// <param name="relativePaths">対象のリポジトリ相対パス。</param>
    /// <param name="choice">利用者の選択。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult> ResolveConflictsAsync(
        IReadOnlyList<string> relativePaths, ConflictResolutionChoice choice,
        CancellationToken cancellationToken = default)
    {
        if (relativePaths is null || relativePaths.Count == 0)
        {
            return Task.FromResult(VersionControlResult.Failed(
                VersionControlMessages.RESOLVE_NO_PATHS));
        }

        // 対応表の引き当てはここで済ませる（Lore を呼ぶ前に落ちる方が原因が分かりやすい）。
        var side  = LoreConflictResolutionMap.ToLoreSide(choice);
        var paths = new List<string>(relativePaths);

        return RunAsync(
            OP_RESOLVE,
            _settings.RemoteOperationTimeout,
            cancellationToken,
            token => ResolveConflictsCore(paths, side, token));
    }

    /// <summary>競合解決の本体（ワーカースレッド上で同期実行される）。</summary>
    /// <param name="paths">対象のリポジトリ相対パス。</param>
    /// <param name="side">Lore へ渡す側。</param>
    /// <param name="token">中断用。</param>
    private VersionControlResult ResolveConflictsCore(
        IReadOnlyList<string> paths, LoreResolveSide side, CancellationToken token)
    {
        var resolve = _backend.MergeResolve(paths, side, token);
        if (!resolve.Succeeded)
            return ToFailure(resolve, VersionControlMessages.RESOLVE_FAILED);

        // 残りの競合を確認する。
        var status = _backend.Status(new LoreStatusRequest(Scan: false, Offline: true), token);
        if (!status.Call.Succeeded)
            return ToFailure(status.Call, VersionControlMessages.RESOLVE_FAILED);

        var remaining = LoreStatusTranslator.ExtractUnresolvedConflicts(status.Files);

        // 頼んだファイルがまだ未解決のまま残っていたら、それは失敗。
        // Lore は対象パスがどれにも一致しなくても成功（rc=0）を返すことがあるため
        // （相対パスの解決先がずれていた等）、戻り値だけを信じると
        // 「解決しました」と言いながら何も変わっていない、という最悪の嘘になる。
        var requested = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var stillUnresolved = remaining.Where(c => requested.Contains(c.Path)).ToList();
        if (stillUnresolved.Count > 0)
        {
            return VersionControlResult.Failed(
                VersionControlMessages.RESOLVE_FAILED,
                stillUnresolved.Select(c => $"未解決のまま: {c.Path}").ToList());
        }

        if (remaining.Count > 0)
        {
            // 頼んだ分は片付いたが、別のファイルがまだ競合している。
            // ここでコミットするとマージが中途半端に確定するので、コミットしない。
            return VersionControlResult.Success(
                string.Format(VersionControlMessages.RESOLVE_PARTIAL_FORMAT,
                              paths.Count, remaining.Count));
        }

        // 全部片付いたのでマージをコミットする（これをしないと
        // 「マージ中」の状態が残り、次の送信が通らない）。
        var commit = _backend.Commit(
            string.Format(VersionControlMessages.MERGE_COMMIT_MESSAGE_FORMAT, paths.Count), token);
        if (!commit.Succeeded)
            return ToFailure(commit, VersionControlMessages.RESOLVE_FAILED);

        return VersionControlResult.Success(
            string.Format(VersionControlMessages.RESOLVE_OK_FORMAT, paths.Count));
    }

    // ── ブランチ ────────────────────────────────────────────

    /// <summary>ブランチを一覧する（LOCAL / REMOTE を統合して返す）。</summary>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult<IReadOnlyList<BranchInfo>>> GetBranchesAsync(
        CancellationToken cancellationToken = default)
        => RunAsync(
            OP_BRANCH_LIST,
            _settings.RemoteOperationTimeout,
            cancellationToken,
            token =>
            {
                var result = _backend.BranchList(token);
                if (!result.Call.Succeeded)
                {
                    return ToFailure<IReadOnlyList<BranchInfo>>(
                        result.Call, VersionControlMessages.BRANCH_LIST_FAILED);
                }

                return VersionControlResult<IReadOnlyList<BranchInfo>>.Ok(
                    LoreBranchTranslator.Merge(result.Rows),
                    VersionControlMessages.BRANCH_LIST_OK);
            });

    /// <summary>ブランチを作る。</summary>
    /// <param name="name">ブランチ名。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult> CreateBranchAsync(
        string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(VersionControlResult.Failed(
                VersionControlMessages.BRANCH_NAME_REQUIRED));
        }

        return RunAsync(
            OP_BRANCH_CREATE,
            _settings.RemoteOperationTimeout,
            cancellationToken,
            token =>
            {
                var call = _backend.BranchCreate(name, token);
                return call.Succeeded
                    ? VersionControlResult.Success(
                        string.Format(VersionControlMessages.BRANCH_CREATE_OK_FORMAT, name))
                    : ToFailure(call, VersionControlMessages.BRANCH_CREATE_FAILED);
            });
    }

    /// <summary>ブランチを切り替える。</summary>
    /// <param name="name">ブランチ名。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult> SwitchBranchAsync(
        string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(VersionControlResult.Failed(
                VersionControlMessages.BRANCH_NAME_REQUIRED));
        }

        return RunAsync(
            OP_BRANCH_SWITCH,
            _settings.RemoteOperationTimeout,
            cancellationToken,
            token =>
            {
                var call = _backend.BranchSwitch(name, token);
                return call.Succeeded
                    ? VersionControlResult.Success(
                        string.Format(VersionControlMessages.BRANCH_SWITCH_OK_FORMAT, name))
                    : ToFailure(call, VersionControlMessages.BRANCH_SWITCH_FAILED);
            });
    }

    // ── 履歴 ────────────────────────────────────────────────

    /// <summary>履歴を取得する（サーバ必須）。</summary>
    /// <param name="maxCount">最大件数（0 以下なら設定の既定値）。</param>
    /// <param name="cancellationToken">中断用。</param>
    public Task<VersionControlResult<IReadOnlyList<RevisionInfo>>> GetHistoryAsync(
        int maxCount = 0, CancellationToken cancellationToken = default)
    {
        // 0 は Lore では「無制限」を意味する。巨大なリポジトリで全件取ると
        // UI が固まるので、必ず正の値に丸めてから渡す。
        var length = maxCount > 0 ? maxCount : _settings.HistoryLength;

        return RunAsync(
            OP_HISTORY,
            _settings.RemoteOperationTimeout,
            cancellationToken,
            token =>
            {
                var result = _backend.History(length, token);
                if (!result.Call.Succeeded)
                {
                    // Lore の history はオフラインで失敗する。
                    // 接続の問題なら「異常」ではなく「今は取れない」と伝える。
                    if (LoreConnectionDiagnosis.IsConnectionFailure(result.Call))
                    {
                        return VersionControlResult<IReadOnlyList<RevisionInfo>>.Create(
                            VersionControlOutcome.RequiresConnection, null,
                            VersionControlMessages.REQUIRES_CONNECTION, result.Call.Messages);
                    }

                    return ToFailure<IReadOnlyList<RevisionInfo>>(
                        result.Call, VersionControlMessages.HISTORY_FAILED);
                }

                // ★history はリビジョン番号とハッシュしか返さない。
                //   メッセージ・作者・日時はメタデータを 1 件ずつ引いて補う。
                var enriched = FillRevisionMetadata(result.Rows, token);

                var revisions = new List<RevisionInfo>(enriched.Count);
                foreach (var row in enriched)
                {
                    revisions.Add(new RevisionInfo(
                        row.Number, row.Id, row.Author, row.Message,
                        LoreLockTranslator.ToUtc(row.UnixTimeSeconds)));
                }

                return VersionControlResult<IReadOnlyList<RevisionInfo>>.Ok(
                    revisions,
                    string.Format(VersionControlMessages.HISTORY_OK_FORMAT, revisions.Count));
            });
    }

    /// <summary>
    /// 履歴の各行へ、リビジョンのメタデータ（メッセージ・作者・日時）を補う。
    ///
    /// <para>
    /// 1 リビジョンにつき 1 往復かかるため、補うのは
    /// <see cref="VersionControlSettings.HistoryMetadataLimit"/> 件までにする。
    /// それを超えた分は番号とハッシュだけの行として残す（消さない）。
    /// </para>
    /// <para>
    /// メタデータの取得に失敗した行は **黙って元のまま残す**。
    /// 「1 件のメタデータが引けなかったから履歴全体が出ない」より、
    /// 「その行だけメッセージが空」の方が利用者にとって良いため。
    /// </para>
    /// </summary>
    /// <param name="rows">history が返した行。</param>
    /// <param name="cancellationToken">中断用（途中で打ち切られたらそこまでで返す）。</param>
    private IReadOnlyList<LoreRevisionRow> FillRevisionMetadata(
        IReadOnlyList<LoreRevisionRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return rows;

        var limit  = Math.Max(0, _settings.HistoryMetadataLimit);
        var filled = new List<LoreRevisionRow>(rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            // 上限を超えた分・識別子が無い行・中断されたあとはそのまま積む。
            if (i >= limit || row.Id.Length == 0 || cancellationToken.IsCancellationRequested)
            {
                filled.Add(row);
                continue;
            }

            var metadata = _backend.RevisionMetadata(row.Id, cancellationToken);
            if (!metadata.Call.Succeeded)
            {
                filled.Add(row);
                continue;
            }

            var fields = LoreRevisionMetadataTranslator.Extract(metadata.Rows);
            filled.Add(fields.HasAny
                ? row.WithMetadata(fields.Author, fields.Message, fields.UnixTimeSeconds)
                : row);
        }

        return filled;
    }

    // ── 共通ヘルパー ────────────────────────────────────────

    /// <summary>
    /// 値を返さない操作をスケジューラで実行し、例外・中断を結果型へ畳む。
    /// </summary>
    /// <param name="operationName">診断ログ用の操作名。</param>
    /// <param name="timeout">期限。</param>
    /// <param name="cancellationToken">呼び出し側の中断。</param>
    /// <param name="operation">実行本体。</param>
    private async Task<VersionControlResult> RunAsync(
        string operationName,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<CancellationToken, VersionControlResult> operation)
    {
        try
        {
            return await _scheduler
                .RunAsync(operationName, operation, timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 呼び出し側の中断とタイムアウトはどちらもここへ来る。
            // 利用者から見れば「終わらなかった」で同じなので区別しない。
            return VersionControlResult.Create(
                VersionControlOutcome.Canceled, VersionControlMessages.CANCELED);
        }
        catch (Exception ex)
        {
            // バックエンドが握り損ねた例外（引数不正など）。境界の外へは出さない。
            return VersionControlResult.Failed(
                UnexpectedFailureMessage(operationName), new[] { ex.Message });
        }
    }

    /// <summary>
    /// 値を返す操作をスケジューラで実行し、例外・中断を結果型へ畳む。
    /// </summary>
    /// <typeparam name="T">戻り値の型。</typeparam>
    /// <param name="operationName">診断ログ用の操作名。</param>
    /// <param name="timeout">期限。</param>
    /// <param name="cancellationToken">呼び出し側の中断。</param>
    /// <param name="operation">実行本体。</param>
    private async Task<VersionControlResult<T>> RunAsync<T>(
        string operationName,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<CancellationToken, VersionControlResult<T>> operation)
    {
        try
        {
            return await _scheduler
                .RunAsync(operationName, operation, timeout, cancellationToken)
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
                UnexpectedFailureMessage(operationName), new[] { ex.Message });
        }
    }

    /// <summary>想定外の例外で終わったときの利用者向けメッセージを作る。</summary>
    /// <param name="operationName">診断ログ用の操作名。</param>
    private static string UnexpectedFailureMessage(string operationName)
        => $"{VersionControlMessages.UNEXPECTED_FAILURE} ({operationName})";

    /// <summary>値を返さない失敗結果を作る。</summary>
    /// <param name="call">Lore の呼び出し結果（失敗）。</param>
    /// <param name="message">利用者向けメッセージ。</param>
    private static VersionControlResult ToFailure(LoreCallResult call, string message)
        => call.WasCanceled
            ? VersionControlResult.Create(
                VersionControlOutcome.Canceled, VersionControlMessages.CANCELED, call.Messages)
            : VersionControlResult.Failed(message, call.Messages);

    /// <summary>値を返す失敗結果を作る。</summary>
    /// <typeparam name="T">戻り値の型。</typeparam>
    /// <param name="call">Lore の呼び出し結果（失敗）。</param>
    /// <param name="message">利用者向けメッセージ。</param>
    private static VersionControlResult<T> ToFailure<T>(LoreCallResult call, string message)
        => call.WasCanceled
            ? VersionControlResult<T>.Create(
                VersionControlOutcome.Canceled, default,
                VersionControlMessages.CANCELED, call.Messages)
            : VersionControlResult<T>.Failed(message, call.Messages);

    /// <summary>
    /// バックエンドと（所有している場合は）スケジューラを閉じる。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 先にスケジューラを止める。バックエンドを先に閉じると、
        // 待ち行列に残った操作が破棄済みのバックエンドを触る。
        if (_ownsScheduler)
        {
            try { _scheduler.Dispose(); } catch { /* 後始末の失敗は無視 */ }
        }
        try { _backend.Dispose(); } catch { /* 後始末の失敗は無視 */ }
    }
}
