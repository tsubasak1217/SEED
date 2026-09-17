// ============================================================
//  NullLockService.cs — バージョン管理が無いときのロック実装
//
//  【役割】
//  すべて「利用不可」を返す。null を返さないことで、呼び出し側が
//  `Locks is null` の分岐を書かずに済む（Null Object パターン）。
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
/// 何もしないロック実装。常に <see cref="VersionControlOutcome.Unavailable"/> を返す。
/// </summary>
public sealed class NullLockService : ILockService
{
    /// <summary>プロセス全体で使い回せる唯一のインスタンス（状態を持たないため）。</summary>
    public static NullLockService Instance { get; } = new();

    /// <summary>外部からの生成を禁じる（<see cref="Instance"/> を使う）。</summary>
    private NullLockService() { }

    /// <summary>常に偽。</summary>
    public bool IsAvailable => false;

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult<IReadOnlyList<LockInfo>>> ListAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable<IReadOnlyList<LockInfo>>());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="relativePaths">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult<IReadOnlyList<LockAcquireResult>>> AcquireAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable<IReadOnlyList<LockAcquireResult>>());

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="relativePaths">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult> ReleaseAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
        => Task.FromResult(VersionControlResult.Create(
            VersionControlOutcome.Unavailable, VersionControlMessages.UNAVAILABLE));

    /// <summary>常に利用不可を返す。</summary>
    /// <param name="relativePaths">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    public Task<VersionControlResult<IReadOnlyList<LockInfo>>> GetStatusAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable<IReadOnlyList<LockInfo>>());

    /// <summary>利用不可の結果を作る共通ヘルパー。</summary>
    /// <typeparam name="T">戻り値の型。</typeparam>
    private static VersionControlResult<T> Unavailable<T>()
        => VersionControlResult<T>.Create(
            VersionControlOutcome.Unavailable, default, VersionControlMessages.UNAVAILABLE);
}
