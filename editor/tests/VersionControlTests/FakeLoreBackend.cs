// ============================================================
//  FakeLoreBackend.cs — ILoreBackend の偽物（純粋ロジックのテスト用）
//
//  【役割】
//  Lore も実サーバも使わずに LoreProvider / LoreLockService の判断を検証するため、
//  「次の呼び出しで何を返すか」をテスト側から並べられるようにした差し替え実装。
//
//  【設計方針】
//  ・status は呼び出し回数ごとに別の結果を返せる（送信は stage → status → commit →
//    push → status と複数回 status を通るので、回数で切り替えられないと組み立てられない）。
//  ・どの操作が何回呼ばれたかを記録する（「空コミット防止で commit を呼ばない」ことを
//    「呼ばれていない」で確かめるため）。
//  ・MergeResolve に渡された side を記録する（mine / theirs の対応表を固定するため）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Threading;
using SEEDEditor.VersionControl.Lore.Backend;

namespace SEEDEditor.Tests.VersionControl;

/// <summary>
/// テストから戻り値を指定できる <see cref="ILoreBackend"/>。
/// </summary>
public sealed class FakeLoreBackend : ILoreBackend
{
    /// <summary>作業コピーのルート（テストでは実在しなくてよい）。</summary>
    public string WorkingCopyRoot { get; set; } = @"C:\fake\project";

    /// <summary>リモート URL。</summary>
    public string RemoteUrl { get; set; } = "lore://127.0.0.1:41357/Fake";

    /// <summary>identity。</summary>
    public string Identity { get; set; } = "tester@example.com";

    // ── 仕込み（テストが事前に並べる戻り値）────────────────

    /// <summary>
    /// status の戻り値を呼び出し順に並べたもの。
    /// 足りなくなったら最後の要素を繰り返す（毎回並べ直さなくて済む）。
    /// </summary>
    public List<LoreStatusResult> StatusResults { get; } = new();

    /// <summary>stage の戻り値。</summary>
    public LoreCallResult StageResult { get; set; } = LoreCallResult.Success;

    /// <summary>dirty 通知の戻り値。</summary>
    public LoreCallResult DirtyResult { get; set; } = LoreCallResult.Success;

    /// <summary>move 通知の戻り値。</summary>
    public LoreCallResult StageMoveResult { get; set; } = LoreCallResult.Success;

    /// <summary>commit の戻り値。</summary>
    public LoreCallResult CommitResult { get; set; } = LoreCallResult.Success;

    /// <summary>push の戻り値。</summary>
    public LoreCallResult PushResult { get; set; } = LoreCallResult.Success;

    /// <summary>sync の戻り値。</summary>
    public LoreCallResult SyncResult { get; set; } = LoreCallResult.Success;

    /// <summary>競合解決の戻り値。</summary>
    public LoreCallResult MergeResolveResult { get; set; } = LoreCallResult.Success;

    /// <summary>「作業コピーの中身のまま解決」の戻り値。</summary>
    public LoreCallResult MergeResolveAsIsResult { get; set; } = LoreCallResult.Success;

    /// <summary>ブランチ一覧の戻り値。</summary>
    public LoreRowsResult<LoreBranchRow> BranchListResult { get; set; }
        = new(LoreCallResult.Success, Array.Empty<LoreBranchRow>());

    /// <summary>ブランチ作成の戻り値。</summary>
    public LoreCallResult BranchCreateResult { get; set; } = LoreCallResult.Success;

    /// <summary>ブランチ切替の戻り値。</summary>
    public LoreCallResult BranchSwitchResult { get; set; } = LoreCallResult.Success;

    /// <summary>ブランチのマージの戻り値。</summary>
    public LoreCallResult BranchMergeResult { get; set; } = LoreCallResult.Success;

    /// <summary>ブランチの削除（アーカイブ）の戻り値。</summary>
    public LoreCallResult BranchArchiveResult { get; set; } = LoreCallResult.Success;

    /// <summary>履歴の戻り値。</summary>
    public LoreRowsResult<LoreRevisionRow> HistoryResult { get; set; }
        = new(LoreCallResult.Success, Array.Empty<LoreRevisionRow>());

    /// <summary>
    /// リビジョン識別子ごとのメタデータの戻り値。
    /// 並んでいない識別子には <see cref="DefaultRevisionMetadataResult"/> を返す。
    /// </summary>
    public Dictionary<string, LoreRowsResult<LoreMetadataRow>> RevisionMetadataResults { get; }
        = new(StringComparer.Ordinal);

    /// <summary>並んでいない識別子に対して返すメタデータ（既定は「空だが成功」）。</summary>
    public LoreRowsResult<LoreMetadataRow> DefaultRevisionMetadataResult { get; set; }
        = new(LoreCallResult.Success, Array.Empty<LoreMetadataRow>());

    /// <summary>メタデータを引きにきたリビジョン識別子（呼ばれた順）。</summary>
    public List<string> RevisionMetadataRequests { get; } = new();

    /// <summary>ロック一覧の戻り値。</summary>
    public LoreRowsResult<LoreLockRow> LockQueryResult { get; set; }
        = new(LoreCallResult.Success, Array.Empty<LoreLockRow>());

    /// <summary>ロック取得の戻り値。</summary>
    public LoreCallResult LockAcquireResult { get; set; } = LoreCallResult.Success;

    /// <summary>ロック解放の戻り値。</summary>
    public LoreCallResult LockReleaseResult { get; set; } = LoreCallResult.Success;

    /// <summary>
    /// ロック状態の戻り値を呼び出し順に並べたもの（acquire 前後で変える用）。
    /// 足りなくなったら最後の要素を繰り返す。
    /// </summary>
    public List<LoreRowsResult<LoreLockRow>> LockStatusResults { get; } = new();

    // ── 記録（テストが事後に確かめる）──────────────────────

    /// <summary>status が呼ばれた回数。</summary>
    public int StatusCallCount { get; private set; }

    /// <summary>status に渡された条件の履歴。</summary>
    public List<LoreStatusRequest> StatusRequests { get; } = new();

    /// <summary>stage が呼ばれた回数。</summary>
    public int StageCallCount { get; private set; }

    /// <summary>stage に渡されたパス（最後の呼び出し）。</summary>
    public IReadOnlyList<string> LastStagePaths { get; private set; } = Array.Empty<string>();

    /// <summary>stage に渡された走査指定（最後の呼び出し）。</summary>
    public bool LastStageScan { get; private set; }

    /// <summary>dirty が呼ばれた回数。</summary>
    public int DirtyCallCount { get; private set; }

    /// <summary>dirty に渡されたパス（最後の呼び出し）。</summary>
    public IReadOnlyList<string> LastDirtyPaths { get; private set; } = Array.Empty<string>();

    /// <summary>move が呼ばれた回数。</summary>
    public int StageMoveCallCount { get; private set; }

    /// <summary>move に渡された移動前後のパス（最後の呼び出し）。</summary>
    public (string From, string To) LastMove { get; private set; }

    /// <summary>commit が呼ばれた回数。</summary>
    public int CommitCallCount { get; private set; }

    /// <summary>commit に渡されたメッセージ（最後の呼び出し）。</summary>
    public string LastCommitMessage { get; private set; } = string.Empty;

    /// <summary>push が呼ばれた回数。</summary>
    public int PushCallCount { get; private set; }

    /// <summary>sync が呼ばれた回数。</summary>
    public int SyncCallCount { get; private set; }

    /// <summary>競合解決に渡された側（最後の呼び出し）。</summary>
    public LoreResolveSide? LastResolveSide { get; private set; }

    /// <summary>競合解決に渡されたパス（最後の呼び出し）。</summary>
    public IReadOnlyList<string> LastResolvePaths { get; private set; } = Array.Empty<string>();

    /// <summary>「作業コピーの中身のまま解決」が呼ばれた回数。</summary>
    public int MergeResolveAsIsCallCount { get; private set; }

    /// <summary>「作業コピーの中身のまま解決」に渡されたパス（最後の呼び出し）。</summary>
    public IReadOnlyList<string> LastResolveAsIsPaths { get; private set; } = Array.Empty<string>();

    /// <summary>ブランチのマージが呼ばれた回数。</summary>
    public int BranchMergeCallCount { get; private set; }

    /// <summary>ブランチのマージに渡された取り込み元（最後の呼び出し）。</summary>
    public string LastBranchMergeSource { get; private set; } = string.Empty;

    /// <summary>ブランチのマージに渡されたコミットメッセージ（最後の呼び出し）。</summary>
    public string LastBranchMergeMessage { get; private set; } = string.Empty;

    /// <summary>ブランチの削除（アーカイブ）が呼ばれた回数。</summary>
    public int BranchArchiveCallCount { get; private set; }

    /// <summary>ブランチの削除（アーカイブ）に渡された名前（最後の呼び出し）。</summary>
    public string LastBranchArchiveName { get; private set; } = string.Empty;

    /// <summary>ロック取得に渡されたパス（最後の呼び出し）。</summary>
    public IReadOnlyList<string> LastLockAcquirePaths { get; private set; } = Array.Empty<string>();

    /// <summary>破棄されたか。</summary>
    public bool IsDisposed { get; private set; }

    // ── ILoreBackend の実装 ────────────────────────────────

    /// <summary>status を返す（並べた順、足りなければ最後を繰り返す）。</summary>
    /// <param name="request">取得条件。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreStatusResult Status(LoreStatusRequest request, CancellationToken cancellationToken)
    {
        StatusRequests.Add(request);
        var index = StatusCallCount;
        StatusCallCount++;

        if (StatusResults.Count == 0)
            return new LoreStatusResult(LoreCallResult.Success, default, null);

        return StatusResults[Math.Min(index, StatusResults.Count - 1)];
    }

    /// <summary>dirty 通知。</summary>
    /// <param name="relativePaths">パス。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult FileDirty(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken)
    {
        DirtyCallCount++;
        LastDirtyPaths = relativePaths;
        return DirtyResult;
    }

    /// <summary>move 通知。</summary>
    /// <param name="fromRelativePath">移動前。</param>
    /// <param name="toRelativePath">移動後。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult FileStageMove(
        string fromRelativePath, string toRelativePath, CancellationToken cancellationToken)
    {
        StageMoveCallCount++;
        LastMove = (fromRelativePath, toRelativePath);
        return StageMoveResult;
    }

    /// <summary>stage。</summary>
    /// <param name="relativePaths">パス。</param>
    /// <param name="scan">走査するか。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult FileStage(
        IReadOnlyList<string> relativePaths, bool scan, CancellationToken cancellationToken)
    {
        StageCallCount++;
        LastStagePaths = relativePaths;
        LastStageScan  = scan;
        return StageResult;
    }

    /// <summary>commit。</summary>
    /// <param name="message">メッセージ。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult Commit(string message, CancellationToken cancellationToken)
    {
        CommitCallCount++;
        LastCommitMessage = message;
        return CommitResult;
    }

    /// <summary>push。</summary>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult Push(CancellationToken cancellationToken)
    {
        PushCallCount++;
        return PushResult;
    }

    /// <summary>sync。</summary>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult Sync(CancellationToken cancellationToken)
    {
        SyncCallCount++;
        return SyncResult;
    }

    /// <summary>競合解決。</summary>
    /// <param name="relativePaths">パス。</param>
    /// <param name="side">採る側。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult MergeResolve(
        IReadOnlyList<string> relativePaths, LoreResolveSide side,
        CancellationToken cancellationToken)
    {
        LastResolveSide  = side;
        LastResolvePaths = relativePaths;
        return MergeResolveResult;
    }

    /// <summary>作業コピーの中身のまま解決。渡されたパスを記録する。</summary>
    /// <param name="relativePaths">パス。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult MergeResolveAsIs(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken)
    {
        MergeResolveAsIsCallCount++;
        LastResolveAsIsPaths = relativePaths;
        return MergeResolveAsIsResult;
    }

    /// <summary>ブランチ一覧。</summary>
    /// <param name="cancellationToken">未使用。</param>
    public LoreRowsResult<LoreBranchRow> BranchList(CancellationToken cancellationToken)
        => BranchListResult;

    /// <summary>ブランチ作成。</summary>
    /// <param name="name">名前。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult BranchCreate(string name, CancellationToken cancellationToken)
        => BranchCreateResult;

    /// <summary>ブランチ切替。</summary>
    /// <param name="name">名前。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult BranchSwitch(string name, CancellationToken cancellationToken)
        => BranchSwitchResult;

    /// <summary>ブランチのマージ。取り込み元とメッセージを記録する。</summary>
    /// <param name="sourceBranch">取り込み元。</param>
    /// <param name="message">自動コミットのメッセージ。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult BranchMerge(
        string sourceBranch, string message, CancellationToken cancellationToken)
    {
        BranchMergeCallCount++;
        LastBranchMergeSource  = sourceBranch;
        LastBranchMergeMessage = message;
        return BranchMergeResult;
    }

    /// <summary>ブランチの削除（アーカイブ）。名前を記録する。</summary>
    /// <param name="name">名前。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult BranchArchive(string name, CancellationToken cancellationToken)
    {
        BranchArchiveCallCount++;
        LastBranchArchiveName = name;
        return BranchArchiveResult;
    }

    /// <summary>履歴。</summary>
    /// <param name="maxCount">最大件数。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreRowsResult<LoreRevisionRow> History(
        int maxCount, CancellationToken cancellationToken)
        => HistoryResult;

    /// <summary>リビジョンのメタデータ。呼ばれた識別子を記録する。</summary>
    /// <param name="revisionId">リビジョン識別子。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreRowsResult<LoreMetadataRow> RevisionMetadata(
        string revisionId, CancellationToken cancellationToken)
    {
        RevisionMetadataRequests.Add(revisionId);
        return RevisionMetadataResults.TryGetValue(revisionId, out var result)
            ? result
            : DefaultRevisionMetadataResult;
    }

    /// <summary>ロック一覧。</summary>
    /// <param name="cancellationToken">未使用。</param>
    public LoreRowsResult<LoreLockRow> LockQuery(CancellationToken cancellationToken)
        => LockQueryResult;

    /// <summary>ロック取得。</summary>
    /// <param name="relativePaths">パス。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult LockAcquire(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken)
    {
        LastLockAcquirePaths = relativePaths;
        return LockAcquireResult;
    }

    /// <summary>ロック解放。</summary>
    /// <param name="relativePaths">パス。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreCallResult LockRelease(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken)
        => LockReleaseResult;

    /// <summary>ロック状態（並べた順、足りなければ最後を繰り返す）。</summary>
    /// <param name="relativePaths">パス。</param>
    /// <param name="cancellationToken">未使用。</param>
    public LoreRowsResult<LoreLockRow> LockStatus(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken)
    {
        var index = LockStatusCallCount;
        LockStatusCallCount++;

        if (LockStatusResults.Count == 0)
            return new LoreRowsResult<LoreLockRow>(LoreCallResult.Success, null);

        return LockStatusResults[Math.Min(index, LockStatusResults.Count - 1)];
    }

    /// <summary>ロック状態が呼ばれた回数。</summary>
    public int LockStatusCallCount { get; private set; }

    /// <summary>破棄印を立てるだけ。</summary>
    public void Dispose() => IsDisposed = true;
}

/// <summary>
/// テストで status の行を手早く組み立てるための補助。
/// </summary>
public static class FakeRows
{
    /// <summary>Lore が「内容が変わった」を表すときの action（MODIFY ではない）。</summary>
    public const string ACTION_KEEP = "KEEP";

    /// <summary>追加の action。</summary>
    public const string ACTION_ADD = "ADD";

    /// <summary>削除の action。</summary>
    public const string ACTION_DELETE = "DELETE";

    /// <summary>移動の action。</summary>
    public const string ACTION_MOVE = "MOVE";

    /// <summary>
    /// status の 1 行を作る。指定しないフラグはすべて false。
    /// </summary>
    /// <param name="path">パス。</param>
    /// <param name="action">action 文字列。</param>
    /// <param name="staged">staged か。</param>
    /// <param name="dirty">dirty か。</param>
    /// <param name="conflict">競合しているか。</param>
    /// <param name="conflictUnresolved">未解決か。</param>
    /// <param name="conflictAutomerged">自動マージ済みか。</param>
    /// <param name="conflictMine">Lore の mine（= リモート側）で解決済みか。</param>
    /// <param name="conflictTheirs">Lore の theirs（= ローカル側）で解決済みか。</param>
    /// <param name="fromPath">移動元。</param>
    public static LoreStatusFileRow File(
        string path,
        string action = ACTION_KEEP,
        bool staged = false,
        bool dirty = true,
        bool conflict = false,
        bool conflictUnresolved = false,
        bool conflictAutomerged = false,
        bool conflictMine = false,
        bool conflictTheirs = false,
        string fromPath = "")
        => new(
            Path: path,
            Action: action,
            FromPath: fromPath,
            SizeBytes: 0,
            FlagStaged: staged,
            FlagDirty: dirty,
            FlagMerged: false,
            FlagConflict: conflict,
            FlagConflictUnresolved: conflictUnresolved,
            FlagConflictAutomerged: conflictAutomerged,
            FlagConflictMine: conflictMine,
            FlagConflictTheirs: conflictTheirs);

    /// <summary>
    /// status の結果を作る。
    /// </summary>
    /// <param name="files">ファイル行。</param>
    /// <param name="branchName">ブランチ名。</param>
    /// <param name="revisionNumber">リビジョン番号。</param>
    /// <param name="isLocalAhead">ローカルが進んでいるか。</param>
    /// <param name="isRemoteAhead">リモートが進んでいるか。</param>
    /// <param name="remoteAvailable">サーバへ到達できたか。</param>
    /// <param name="remoteAuthorized">読む権限があったか。</param>
    /// <param name="remoteBranchExists">リモートに同名ブランチがあるか。</param>
    public static LoreStatusResult Status(
        IReadOnlyList<LoreStatusFileRow>? files = null,
        string branchName = "main",
        ulong revisionNumber = 1,
        bool isLocalAhead = false,
        bool isRemoteAhead = false,
        bool remoteAvailable = true,
        bool remoteAuthorized = true,
        bool remoteBranchExists = true)
        => new(
            LoreCallResult.Success,
            new LoreStatusRevisionRow(
                BranchName: branchName,
                RevisionNumber: revisionNumber,
                RemoteRevisionNumber: revisionNumber,
                IsLocalAhead: isLocalAhead,
                IsRemoteAhead: isRemoteAhead,
                RemoteAvailable: remoteAvailable,
                RemoteAuthorized: remoteAuthorized,
                RemoteBranchExists: remoteBranchExists),
            files);
}
