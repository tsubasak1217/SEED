// ============================================================
//  ILoreBackend.cs — Lore 呼び出しの抽象（テストで差し替える継ぎ目）
//
//  【役割】
//  Lore の各コマンドを 1 対 1 で並べただけの細い境界。ここより上（LoreProvider）は
//  NuGet パッケージ LoreVcs を一切参照しない。
//
//  【なぜこの境界が要るのか】
//  LoreProvider が持つ判断ロジック
//    ・空コミット防止（staged 0 件なら commit しない）
//    ・push 拒否 → NeedsSync への変換
//    ・sync 後の競合検出（終了コードではなく flagConflict* で判定）
//    ・LOCAL / REMOTE ブランチの統合
//    ・<unknown> 所有者の扱い
//    ・状態フラグ → モデル変換
//  は、実サーバが無くても検証できなければならない。この境界に偽物を差せば
//  すべて単体テストで固定できる。
//
//  【スレッドの約束】
//  実装は同期（ブロッキング）でよい。LoreVcs の .Wait() は gRPC 往復を伴う
//  同期呼び出しであり、非同期化しても中で待つだけなので隠さない。
//  UI を止めないための直列ワーカーは 1 つ上（LoreProvider が持つ
//  IVersionControlScheduler）の責務。
//
//  【パスの約束】
//  この境界に渡すパスは **すべてリポジトリルートからの相対パス**。
//  絶対パスからの変換は LoreProvider が行う（両方が流れる状態を作らない）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Threading;

namespace SEEDEditor.VersionControl.Lore.Backend;

/// <summary>
/// status の取得条件（Lore の LoreRepositoryStatusArgs のうち SEED が使う項目）。
/// </summary>
/// <param name="Scan">
/// ファイルシステムを走査して dirty を取り直すか（Lore の <c>scan</c>）。
/// 偽だと記録済みの dirty しか見ない。
/// </param>
/// <param name="Offline">
/// サーバに接続しないか（Lore の global <c>offline</c>）。
/// 真だとリモートとの前後関係は取れない。
/// </param>
public readonly record struct LoreStatusRequest(bool Scan, bool Offline);

/// <summary>
/// Lore のコマンド境界。
/// </summary>
public interface ILoreBackend : IDisposable
{
    /// <summary>作業コピーのルート（絶対パス）。</summary>
    string WorkingCopyRoot { get; }

    /// <summary>リモートリポジトリの URL（`.lore/config.toml` の remote_url）。不明なら空文字。</summary>
    string RemoteUrl { get; }

    /// <summary>identity（`.lore/config.toml` の identity）。不明なら空文字。</summary>
    string Identity { get; }

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>
    /// status を実行する（Lore の <c>repository status</c>）。
    /// staged 情報は常に含めて要求する（空コミット防止の判定に必要なため）。
    /// </summary>
    /// <param name="request">取得条件。</param>
    /// <param name="cancellationToken">中断用。</param>
    LoreStatusResult Status(LoreStatusRequest request, CancellationToken cancellationToken);

    // ── 変更の記録 ──────────────────────────────────────────

    /// <summary>
    /// ファイルを dirty として記録する（Lore の <c>file dirty</c>）。
    /// 既定の status はファイルシステムを見ないので、保存時にこれを呼ぶ。
    /// </summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    LoreCallResult FileDirty(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken);

    /// <summary>
    /// ファイルの移動を記録する（Lore の <c>file stage move</c>）。
    /// 素のファイル移動は削除 + 追加になり履歴が切れるため、改名は必ずこれを通す。
    /// </summary>
    /// <param name="fromRelativePath">移動前のリポジトリ相対パス。</param>
    /// <param name="toRelativePath">移動後のリポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    LoreCallResult FileStageMove(
        string fromRelativePath, string toRelativePath, CancellationToken cancellationToken);

    /// <summary>
    /// ファイルを stage する（Lore の <c>file stage</c>）。
    /// </summary>
    /// <param name="relativePaths">
    /// リポジトリ相対パス。作業コピー全体を対象にするときは
    /// <see cref="LoreBackendPaths.REPOSITORY_ROOT"/> 1 件を渡す。
    /// </param>
    /// <param name="scan">
    /// ディレクトリを再帰的に走査するか。<c>lore stage .</c> は走査しないと
    /// 何も stage されないため、全体 stage では必ず真にする。
    /// </param>
    /// <param name="cancellationToken">中断用。</param>
    LoreCallResult FileStage(
        IReadOnlyList<string> relativePaths, bool scan, CancellationToken cancellationToken);

    // ── 送信・取得 ──────────────────────────────────────────

    /// <summary>コミットする（Lore の <c>revision commit</c>）。</summary>
    /// <param name="message">コミットメッセージ。</param>
    /// <param name="cancellationToken">中断用。</param>
    LoreCallResult Commit(string message, CancellationToken cancellationToken);

    /// <summary>現在のブランチを push する（Lore の <c>branch push</c>）。</summary>
    /// <param name="cancellationToken">中断用。</param>
    LoreCallResult Push(CancellationToken cancellationToken);

    /// <summary>
    /// ブランチ先端へ同期する（Lore の <c>revision sync</c>）。
    /// 競合しても成功を返す点に注意（判定は status の flagConflict* で行う）。
    /// </summary>
    /// <param name="cancellationToken">中断用。</param>
    LoreCallResult Sync(CancellationToken cancellationToken);

    /// <summary>
    /// 競合を解決する（Lore の <c>branch merge resolve mine/theirs</c>）。
    /// </summary>
    /// <param name="relativePaths">対象のリポジトリ相対パス。</param>
    /// <param name="side">どちらの親を採るか（Lore の語彙のまま）。</param>
    /// <param name="cancellationToken">中断用。</param>
    LoreCallResult MergeResolve(
        IReadOnlyList<string> relativePaths, LoreResolveSide side,
        CancellationToken cancellationToken);

    // ── ブランチ ────────────────────────────────────────────

    /// <summary>
    /// ブランチを一覧する（Lore の <c>branch list</c>）。
    /// 同名ブランチが LOCAL / REMOTE の 2 行で返ることがある。
    /// </summary>
    /// <param name="cancellationToken">中断用。</param>
    LoreRowsResult<LoreBranchRow> BranchList(CancellationToken cancellationToken);

    /// <summary>ブランチを作る（Lore の <c>branch create</c>）。</summary>
    /// <param name="name">ブランチ名。</param>
    /// <param name="cancellationToken">中断用。</param>
    LoreCallResult BranchCreate(string name, CancellationToken cancellationToken);

    /// <summary>ブランチを切り替える（Lore の <c>branch switch</c>）。</summary>
    /// <param name="name">ブランチ名。</param>
    /// <param name="cancellationToken">中断用。</param>
    LoreCallResult BranchSwitch(string name, CancellationToken cancellationToken);

    // ── 履歴 ────────────────────────────────────────────────

    /// <summary>
    /// 履歴を取得する（Lore の <c>revision history</c>）。サーバ必須。
    /// </summary>
    /// <param name="maxCount">最大件数（0 は無制限だが、呼び出し側は必ず正の値を渡す）。</param>
    /// <param name="cancellationToken">中断用。</param>
    LoreRowsResult<LoreRevisionRow> History(int maxCount, CancellationToken cancellationToken);

    /// <summary>
    /// 1 リビジョンのメタデータを一覧する（Lore の <c>revision metadata list</c>）。
    ///
    /// <para>
    /// ★なぜ必要か: <c>revision history</c> が返すイベントは番号・ハッシュ・親しか
    /// 持たず、**コミットメッセージ・作者・日時が入っていない**。
    /// それらはリビジョンのメタデータとして別に付いているので、履歴を表示するには
    /// 1 件ずつこれを引いて補う必要がある。
    /// </para>
    /// <para>
    /// 1 リビジョンにつき 1 往復かかるため、呼び出し側（<c>LoreProvider</c>）は
    /// 補う件数を設定値で上限を切ること。
    /// </para>
    /// </summary>
    /// <param name="revisionId">リビジョン識別子（<see cref="LoreRevisionRow.Id"/>）。</param>
    /// <param name="cancellationToken">中断用。</param>
    LoreRowsResult<LoreMetadataRow> RevisionMetadata(
        string revisionId, CancellationToken cancellationToken);

    // ── ロック ──────────────────────────────────────────────

    /// <summary>
    /// 現在のブランチのロックを一覧する（Lore の <c>lock file query</c>）。
    /// </summary>
    /// <param name="cancellationToken">中断用。</param>
    LoreRowsResult<LoreLockRow> LockQuery(CancellationToken cancellationToken);

    /// <summary>
    /// ロックを取得する（Lore の <c>lock file acquire</c>）。
    /// 既に取得済みでも成功を返すため、呼び出し側は取得後に
    /// <see cref="LockStatus"/> で所有者を確認すること。
    /// </summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    LoreCallResult LockAcquire(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken);

    /// <summary>ロックを解放する（Lore の <c>lock file release</c>）。</summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    LoreCallResult LockRelease(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken);

    /// <summary>
    /// 指定パスのロック状態を取得する（Lore の <c>lock file status</c>）。
    /// </summary>
    /// <param name="relativePaths">リポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    LoreRowsResult<LoreLockRow> LockStatus(
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken);
}

/// <summary>
/// バックエンドへ渡す特別なパス文字列。
/// </summary>
public static class LoreBackendPaths
{
    /// <summary>
    /// 作業コピー全体を指すパス。Lore の CLI で言う <c>lore stage .</c> の "." に当たる。
    /// </summary>
    public const string REPOSITORY_ROOT = ".";
}
