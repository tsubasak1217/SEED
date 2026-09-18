// ============================================================
//  NullVersionControlProvider.cs — バージョン管理が無いときのプロバイダ
//
//  【役割】
//  プロジェクト直下に `.lore/` が無い（= まだバージョン管理下に置いていない）
//  ときに使う実装。すべての操作が「利用不可」を返す。
//
//  【なぜ null ではなくこの型を返すのか】
//  サービスが null を返すと、パネル・保存経路・プロジェクトパネルの
//  すべてで null チェックが必要になり、1 か所忘れるだけで
//  NullReferenceException になる。常に非 null のプロバイダを返し、
//  パネルは IsAvailable が偽なら自分を隠す、という約束にする。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.VersionControl.Abstractions;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Null;

/// <summary>
/// 何もしないプロバイダ。常に <see cref="VersionControlOutcome.Unavailable"/> を返す。
/// </summary>
public sealed class NullVersionControlProvider : IVersionControlProvider
{
    /// <summary>作業コピーのルート（診断用に保持するだけで、操作には使わない）。</summary>
    private readonly string _rootDir;

    /// <summary>ルートを指定して生成する。</summary>
    /// <param name="rootDir">プロジェクトルート（診断表示用。無くてもよい）。</param>
    public NullVersionControlProvider(string? rootDir = null)
    {
        _rootDir = rootDir ?? string.Empty;
    }

    /// <summary>表示名。</summary>
    public string DisplayName => VersionControlMessages.PROVIDER_NAME_NONE;

    /// <summary>常に偽。</summary>
    public bool IsAvailable => false;

    /// <summary>プロジェクトルート（バージョン管理下ではない）。</summary>
    public string WorkingCopyRoot => _rootDir;

    /// <summary>常に空文字。</summary>
    public string RemoteUrl => string.Empty;

    /// <summary>常に空文字。</summary>
    public string Identity => string.Empty;

    /// <summary>常に利用不可を返すロック実装。</summary>
    public ILockService Locks => NullLockService.Instance;

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="mode">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult<WorkingCopyStatus>> GetStatusAsync(
        StatusRefreshMode mode = StatusRefreshMode.ScanOffline,
        CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult<WorkingCopyStatus>.Create(
            VersionControlOutcome.Unavailable,
            WorkingCopyStatus.Empty(mode),
            VersionControlMessages.UNAVAILABLE));

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="absolutePaths">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult> NotifyChangedAsync(
        IReadOnlyList<string> absolutePaths, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="fromAbsolutePath">未使用。</param>
    /// <param name="toAbsolutePath">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult> NotifyMovedAsync(
        string fromAbsolutePath, string toAbsolutePath,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="message">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult<SubmitReport>> SubmitAsync(
        string message, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable<SubmitReport>());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult<SyncReport>> FetchLatestAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable<SyncReport>());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="relativePaths">未使用。</param>
    /// <param name="choice">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult> ResolveConflictsAsync(
        IReadOnlyList<string> relativePaths, ConflictResolutionChoice choice,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable());

    /// <summary>進行中のマージは存在しない。</summary>
    public MergeContext MergeContext => MergeContext.Unknown;

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="relativePath">未使用。</param>
    /// <param name="resolvedText">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult> ResolveConflictsWithContentAsync(
        string relativePath, string resolvedText,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="relativePaths">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult> ResolveConflictsTakingBothAsync(
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult<IReadOnlyList<BranchInfo>>> GetBranchesAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable<IReadOnlyList<BranchInfo>>());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="name">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult> CreateBranchAsync(
        string name, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="name">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult> SwitchBranchAsync(
        string name, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="sourceBranch">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult<MergeReport>> MergeBranchAsync(
        string sourceBranch, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable<MergeReport>());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="name">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult> ArchiveBranchAsync(
        string name, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="maxCount">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult<IReadOnlyList<RevisionInfo>>> GetHistoryAsync(
        int maxCount = 0, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable<IReadOnlyList<RevisionInfo>>());

    /// <summary>解放するものは無い（インターフェースの要求を満たすだけ）。</summary>
    public void Dispose() { }

    // ── 共通ヘルパー ────────────────────────────────────────

    /// <summary>値を返さない利用不可の結果。</summary>
    private static VersionControlResult Unavailable()
        => VersionControlResult.Create(
            VersionControlOutcome.Unavailable, VersionControlMessages.UNAVAILABLE);

    /// <summary>値を返す利用不可の結果。</summary>
    /// <typeparam name="T">戻り値の型。</typeparam>
    private static VersionControlResult<T> Unavailable<T>()
        => VersionControlResult<T>.Create(
            VersionControlOutcome.Unavailable, default, VersionControlMessages.UNAVAILABLE);
}
