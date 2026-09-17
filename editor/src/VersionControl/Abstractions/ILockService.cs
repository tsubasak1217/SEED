// ============================================================
//  ILockService.cs — ロックの境界
//
//  【役割】
//  「このファイルは誰かが編集中か」「自分が編集権を取れるか」だけを扱う。
//
//  【なぜプロバイダから独立させるのか】
//  Lore v0.9.0 のロックは **通知のみ** で、他人がロックしていても書き込みを
//  止められない。SEED では将来これを強制（保存ゲート）したいが、
//  Epic 公式の強制ロック（successor-locks LEP、承認済み）が出たらそちらへ
//  差し替えたい。実装の差し替え先を 1 本の細いインターフェースに閉じておけば、
//  パネルとサービスを書き換えずに入れ替えられる。
//
//  【今回の範囲】
//  一覧・取得・解放・照会まで。強制（保存ゲート）は入れない
//  （サーバ認証が無いと所有者が <unknown> になり「他人のロック」が成立しないため。
//    docs/vcs_lore.md 4 章）。
//
//  【スレッド】
//  すべて Task を返す。実装は専用ワーカーで直列に実行するので、
//  UI スレッドから呼んで await してよい（ブロックしない）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Abstractions;

/// <summary>
/// ファイルロックの境界。
/// </summary>
public interface ILockService
{
    /// <summary>この実装でロックを扱えるか（VCS 無しの場合は偽）。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 現在のブランチのロックを一覧する（サーバ必須）。
    /// </summary>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>ロック一覧。サーバへ繋がらなければ RequiresConnection。</returns>
    Task<VersionControlResult<IReadOnlyList<LockInfo>>> ListAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定パスのロックを取得する（サーバ必須）。
    ///
    /// <para>
    /// Lore は既にロック済みのパスに対しても成功を返すため、取得後に状態を
    /// 引き直して <see cref="LockAcquireOutcome"/> を決める。所有者が
    /// <c>&lt;unknown&gt;</c> の場合は <see cref="LockAcquireOutcome.HeldByUnknown"/> になる。
    /// </para>
    /// </summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>パスごとの取得結果。</returns>
    Task<VersionControlResult<IReadOnlyList<LockAcquireResult>>> AcquireAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定パスのロックを解放する（サーバ必須）。
    /// </summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult> ReleaseAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定パスのロック状態を照会する（サーバ必須）。
    /// </summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>
    /// パスごとの状態。ロックされていないパスは
    /// <see cref="LockHolder.None"/> の <see cref="LockInfo"/> として必ず 1 件返る
    /// （「返ってこなかった＝ロックされていない」を呼び出し側に推測させない）。
    /// </returns>
    Task<VersionControlResult<IReadOnlyList<LockInfo>>> GetStatusAsync(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default);
}
