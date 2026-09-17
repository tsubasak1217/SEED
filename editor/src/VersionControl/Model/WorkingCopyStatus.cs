// ============================================================
//  WorkingCopyStatus.cs — 作業コピー全体の状態（常時表示の素材）
//
//  【役割】
//  パネルのヘッダー（ブランチ名・リビジョン・リモートとの前後関係）と
//  変更一覧を 1 回の取得でまとめて返すための型。
//
//  【取得コストについて】
//  既定は「オフライン＋走査」= Lore の `status --scan --offline` 相当で、
//  サーバ往復をしない（実測 0.11 秒）。この場合リモート側の情報は取れないので
//  <see cref="RemoteState"/> は <see cref="RemoteComparison.NotChecked"/> になる。
//  サーバに聞くのは「送信」「最新を取得」「ロック」「履歴」のときだけにする。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.VersionControl.Model;

/// <summary>
/// 状態取得のやり方。上の層が「どこまでコストを払うか」を選ぶ。
/// </summary>
public enum StatusRefreshMode
{
    /// <summary>
    /// 記録済みの dirty だけを見る（ファイルシステムを走査しない・サーバにも繋がない）。
    /// 保存のたびの更新はこれ。Lore の `status --offline` 相当。
    /// </summary>
    TrackedOnly,

    /// <summary>
    /// ファイルシステムを走査して dirty を取り直す（サーバには繋がない）。
    /// プロジェクトを開いた直後と、エディタ外での変更が疑われるときに使う。
    /// Lore の `status --scan --offline` 相当。
    /// </summary>
    ScanOffline,

    /// <summary>
    /// 走査に加えてサーバへも問い合わせ、リモートとの前後関係まで取る。
    /// 「送信」前や利用者が明示的に更新したときだけ使う。Lore の `status --scan` 相当。
    /// </summary>
    ScanOnline,
}

/// <summary>
/// ローカルとリモートの前後関係。
/// </summary>
public enum RemoteComparison
{
    /// <summary>問い合わせていない（オフライン取得だった）。</summary>
    NotChecked,

    /// <summary>サーバに繋がらなかった、または権限が無くて読めなかった。</summary>
    Unavailable,

    /// <summary>リモートに同名ブランチがまだ無い（初回 push 前）。</summary>
    RemoteBranchMissing,

    /// <summary>一致している。</summary>
    InSync,

    /// <summary>ローカルが進んでいる（送信できる）。</summary>
    LocalAhead,

    /// <summary>リモートが進んでいる（先に最新を取得すべき）。</summary>
    RemoteAhead,

    /// <summary>双方が進んでいる（分岐。最新を取得してマージが要る）。</summary>
    Diverged,
}

/// <summary>
/// 作業コピー全体の状態（不変）。
/// </summary>
public sealed class WorkingCopyStatus
{
    /// <summary>現在のブランチ名。</summary>
    public string BranchName { get; }

    /// <summary>現在のリビジョン番号。</summary>
    public ulong RevisionNumber { get; }

    /// <summary>変更されたファイル（競合を含む全件）。</summary>
    public IReadOnlyList<ChangedFile> Changes { get; }

    /// <summary>リモートとの前後関係。</summary>
    public RemoteComparison RemoteState { get; }

    /// <summary>この状態を取得したときのモード（どこまで信用できるかの根拠）。</summary>
    public StatusRefreshMode Mode { get; }

    /// <summary>取得時刻（UTC）。パネルの「最終更新」表示用。</summary>
    public DateTime RetrievedAtUtc { get; }

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="branchName">現在のブランチ名。</param>
    /// <param name="revisionNumber">現在のリビジョン番号。</param>
    /// <param name="changes">変更ファイル一覧。</param>
    /// <param name="remoteState">リモートとの前後関係。</param>
    /// <param name="mode">取得モード。</param>
    /// <param name="retrievedAtUtc">取得時刻（省略時は現在時刻）。</param>
    public WorkingCopyStatus(
        string branchName,
        ulong revisionNumber,
        IReadOnlyList<ChangedFile> changes,
        RemoteComparison remoteState,
        StatusRefreshMode mode,
        DateTime? retrievedAtUtc = null)
    {
        BranchName     = branchName ?? string.Empty;
        RevisionNumber = revisionNumber;
        Changes        = changes ?? Array.Empty<ChangedFile>();
        RemoteState    = remoteState;
        Mode           = mode;
        RetrievedAtUtc = retrievedAtUtc ?? DateTime.UtcNow;
    }

    /// <summary>未解決の競合があるファイル。</summary>
    public IReadOnlyList<ChangedFile> UnresolvedConflicts
        => Changes.Where(c => c.IsUnresolvedConflict).ToList();

    /// <summary>未解決の競合が 1 件でもあるか（あるうちは「送信」できない）。</summary>
    public bool HasUnresolvedConflicts => Changes.Any(c => c.IsUnresolvedConflict);

    /// <summary>送るべき変更が 1 件も無いか（空コミット防止の判定に使う）。</summary>
    public bool IsClean => Changes.Count == 0;

    /// <summary>空の状態（プロバイダが利用不可のときに返す）。</summary>
    /// <param name="mode">取得モード。</param>
    public static WorkingCopyStatus Empty(StatusRefreshMode mode)
        => new(string.Empty, 0, Array.Empty<ChangedFile>(), RemoteComparison.NotChecked, mode);

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => $"branch={BranchName} rev={RevisionNumber} changes={Changes.Count} remote={RemoteState}";
}
