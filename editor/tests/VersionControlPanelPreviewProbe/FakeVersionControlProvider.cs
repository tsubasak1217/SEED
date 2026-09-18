// ============================================================
//  FakeVersionControlProvider.cs — 画面を作るためだけの偽プロバイダ
//
//  【役割】
//  「変更あり」「競合あり」「ロックあり」「履歴あり」といった各状態を、
//  サーバにも Lore にも触れずに作り出す。
//
//  【なぜ ILoreBackend ではなくこちらに偽物を置くのか】
//  単体テスト（VersionControlTests）は LoreProvider の判断を検証したいので
//  一段下の ILoreBackend に偽物を差す。こちらが確かめたいのは **画面** であって
//  プロバイダの判断ではないため、境界の一番上（IVersionControlProvider）を
//  丸ごと差し替えるのが最も単純で、余計なものを巻き込まない。
//
//  【書き込みはしない】
//  送信・解決・ロックといった操作は、状態を書き換えずに成功だけを返す。
//  このプローブの目的は静止画を撮ることであって、操作の流れを追うことではない。
// ============================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.VersionControl.Abstractions;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.Tests.VersionControlPanelPreview;

/// <summary>
/// 与えられた状態をそのまま返すだけのプロバイダ。
/// </summary>
public sealed class FakeVersionControlProvider : IVersionControlProvider
{
    /// <summary>この偽物が返す作業コピーの状態。</summary>
    private readonly WorkingCopyStatus _status;

    /// <summary>この偽物が返すロック一覧。</summary>
    private readonly IReadOnlyList<LockInfo> _locks;

    /// <summary>この偽物が返す履歴。</summary>
    private readonly IReadOnlyList<RevisionInfo> _history;

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="workingCopyRoot">作業コピーのパス（ツリーの根に出る）。</param>
    /// <param name="status">返す状態。</param>
    /// <param name="locks">返すロック一覧。</param>
    /// <param name="history">返す履歴。</param>
    /// <param name="identity">identity（ヘッダーに出る）。</param>
    /// <param name="remoteUrl">リモート URL（ヘッダーに出る）。</param>
    public FakeVersionControlProvider(
        string workingCopyRoot,
        WorkingCopyStatus status,
        IReadOnlyList<LockInfo>? locks = null,
        IReadOnlyList<RevisionInfo>? history = null,
        string identity = "tsubasa",
        string remoteUrl = "lore://127.0.0.1:41337/warashibe")
    {
        WorkingCopyRoot = workingCopyRoot;
        _status         = status;
        _locks          = locks   ?? Array.Empty<LockInfo>();
        _history        = history ?? Array.Empty<RevisionInfo>();
        Identity        = identity;
        RemoteUrl       = remoteUrl;
        Locks           = new FakeLockService(_locks);
    }

    /// <inheritdoc/>
    public string DisplayName => "Lore";

    /// <inheritdoc/>
    public bool IsAvailable => true;

    /// <inheritdoc/>
    public string WorkingCopyRoot { get; }

    /// <inheritdoc/>
    public string RemoteUrl { get; }

    /// <inheritdoc/>
    public string Identity { get; }

    /// <inheritdoc/>
    public ILockService Locks { get; }

    /// <inheritdoc/>
    public Task<VersionControlResult<WorkingCopyStatus>> GetStatusAsync(
        StatusRefreshMode mode = StatusRefreshMode.ScanOffline,
        CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult<WorkingCopyStatus>.Ok(_status, "状態を取得しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult> NotifyChangedAsync(
        IReadOnlyList<string> absolutePaths, CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult.Success("変更を記録しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult> NotifyMovedAsync(
        string fromAbsolutePath, string toAbsolutePath,
        CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult.Success("移動を記録しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult<SubmitReport>> SubmitAsync(
        string message, CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult<SubmitReport>.Ok(
            SubmitReport.Nothing, "送信しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult<SyncReport>> FetchLatestAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult<SyncReport>.Ok(
            new SyncReport(null, 0UL, 0), "最新を取得しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult> ResolveConflictsAsync(
        IReadOnlyList<string> relativePaths, ConflictResolutionChoice choice,
        CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult.Success("競合を解決しました。"));

    /// <summary>
    /// 進行中のマージの向き。見た目の確認用なので sync 固定
    /// （マージエディタの見出しは「リモート ／ 自分の変更」になる）。
    /// </summary>
    public MergeContext MergeContext => MergeContext.Unknown;

    /// <inheritdoc/>
    public Task<VersionControlResult> ResolveConflictsWithContentAsync(
        string relativePath, string resolvedText,
        CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult.Success("競合を解決しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult> ResolveConflictsTakingBothAsync(
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult.Success("競合を解決しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult<IReadOnlyList<BranchInfo>>> GetBranchesAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult<IReadOnlyList<BranchInfo>>.Ok(
            new[] { new BranchInfo(_status.BranchName, true, true, true) },
            "ブランチ一覧を取得しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult> CreateBranchAsync(
        string name, CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult.Success("ブランチを作成しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult> SwitchBranchAsync(
        string name, CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult.Success("ブランチを切り替えました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult<MergeReport>> MergeBranchAsync(
        string sourceBranch, CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult<MergeReport>.Ok(
            new MergeReport(sourceBranch, null, 0UL), "ブランチを取り込みました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult> ArchiveBranchAsync(
        string name, CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult.Success(
            "ブランチを削除（アーカイブ）しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult<IReadOnlyList<RevisionInfo>>> GetHistoryAsync(
        int maxCount = 0, CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult<IReadOnlyList<RevisionInfo>>.Ok(
            _history, $"履歴を {_history.Count} 件取得しました。"));

    /// <inheritdoc/>
    public void Dispose() { }
}

/// <summary>
/// 与えられたロック一覧をそのまま返すだけのロック境界。
/// </summary>
public sealed class FakeLockService : ILockService
{
    /// <summary>返すロック一覧。</summary>
    private readonly IReadOnlyList<LockInfo> _locks;

    /// <summary>ロック一覧を指定して生成する。</summary>
    /// <param name="locks">返すロック一覧。</param>
    public FakeLockService(IReadOnlyList<LockInfo> locks) => _locks = locks;

    /// <inheritdoc/>
    public bool IsAvailable => true;

    /// <inheritdoc/>
    public Task<VersionControlResult<IReadOnlyList<LockInfo>>> ListAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult<IReadOnlyList<LockInfo>>.Ok(
            _locks, "ロック一覧を取得しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult<IReadOnlyList<LockAcquireResult>>> AcquireAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult<IReadOnlyList<LockAcquireResult>>.Ok(
            Array.Empty<LockAcquireResult>(), "ロックを取得しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult> ReleaseAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult.Success("ロックを解放しました。"));

    /// <inheritdoc/>
    public Task<VersionControlResult<IReadOnlyList<LockInfo>>> GetStatusAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult<IReadOnlyList<LockInfo>>.Ok(
            Array.Empty<LockInfo>(), "ロック状態を取得しました。"));
}
