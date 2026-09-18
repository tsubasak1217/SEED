// ============================================================
//  IVersionControlProvider.cs — バージョン管理の境界（上の層が知る唯一の顔）
//
//  【役割】
//  パネル（UI）が使う語彙を固定する。Lore v0.9.0 は pre-1.0 で API が変わり得るし、
//  将来 Perforce などへ差し替える可能性もある。上の層は必ずこの型だけを見る。
//
//  【語彙の設計方針（アーティストも使うこと）】
//  stage / rebase / detached HEAD といった概念はここへ出さない。出すのは:
//    ・状態の取得      … いま何が変わっているか
//    ・変更の通知      … 保存したファイルを「変わった」と記録する
//    ・移動・改名の通知 … 素のファイル移動は履歴が切れるので必ず通知経由にする
//    ・送信            … stage + commit + push をまとめた 1 操作
//    ・最新を取得      … sync
//    ・競合の解決      … 「自分の変更を残す」「リモートを採用」の 2 択
//    ・ブランチ / 履歴 / ロック / 現在の identity・リモート URL・接続状態
//
//  【スレッド（重要）】
//  すべて Task を返し、実装は専用ワーカー（直列キュー）で実行する。
//  LoreVcs の .Wait() は同期ブロッキングで gRPC 往復（実測 350 ms）が入るため、
//  UI スレッドで呼ぶとエディタが固まる。呼び出し側は UI スレッドから
//  await するだけでよい。同じ作業コピーへの操作が並行しないことも実装が保証する。
//
//  【エラー】
//  例外は境界の外へ出さない。すべて VersionControlResult の Outcome へ畳む。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Abstractions;

/// <summary>
/// バージョン管理プロバイダの境界。
/// </summary>
public interface IVersionControlProvider : IDisposable
{
    // ── 素性 ────────────────────────────────────────────────

    /// <summary>実装の表示名（例: "Lore"）。パネルのヘッダーに出す。</summary>
    string DisplayName { get; }

    /// <summary>
    /// このプロジェクトでバージョン管理が使えるか。
    /// 偽ならパネルは非表示にしてよく、以下の操作はすべて
    /// <see cref="VersionControlOutcome.Unavailable"/> を返す。
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>作業コピーのルート（= プロジェクトルート）。使えないときは空文字。</summary>
    string WorkingCopyRoot { get; }

    /// <summary>リモートリポジトリの URL。不明なら空文字。</summary>
    string RemoteUrl { get; }

    /// <summary>
    /// 現在の identity（コミットに記録される名前）。
    /// サーバ認証が無い構成ではサーバ側には伝わらない（ロック所有者は
    /// <c>&lt;unknown&gt;</c> になる）点に注意。
    /// </summary>
    string Identity { get; }

    /// <summary>ロックの境界（差し替え可能）。</summary>
    ILockService Locks { get; }

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>
    /// 作業コピーの状態を取得する。
    /// </summary>
    /// <param name="mode">
    /// どこまでコストを払うか。既定の
    /// <see cref="StatusRefreshMode.ScanOffline"/> はサーバ往復をしない。
    /// </param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult<WorkingCopyStatus>> GetStatusAsync(
        StatusRefreshMode mode = StatusRefreshMode.ScanOffline,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存などで内容が変わったファイルを「変わった」と記録する。
    ///
    /// <para>
    /// Lore の既定の status はファイルシステムを見ないため、この通知が無いと
    /// 保存した内容が変更一覧に出てこない。エディタの保存経路から必ず呼ぶこと。
    /// </para>
    /// </summary>
    /// <param name="absolutePaths">変更されたファイルの絶対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult> NotifyChangedAsync(
        IReadOnlyList<string> absolutePaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// ファイルの移動・改名を通知する。
    ///
    /// <para>
    /// 素のファイル移動は「削除 + 追加」として記録され履歴が切れる。
    /// プロジェクトパネルの改名・移動は必ずこの経路を通すこと。
    /// </para>
    /// </summary>
    /// <param name="fromAbsolutePath">移動前の絶対パス。</param>
    /// <param name="toAbsolutePath">移動後の絶対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult> NotifyMovedAsync(
        string fromAbsolutePath, string toAbsolutePath,
        CancellationToken cancellationToken = default);

    // ── 送信・取得 ──────────────────────────────────────────

    /// <summary>
    /// 変更を送信する（内部では 走査つき stage → commit → push）。
    ///
    /// <para>結末の読み方:</para>
    /// <list type="bullet">
    ///   <item><see cref="VersionControlOutcome.NothingToDo"/> … 送るものが無い（空コミット防止）</item>
    ///   <item><see cref="VersionControlOutcome.NeedsSync"/> … push がリモートとの分岐で弾かれた。
    ///         先に <see cref="FetchLatestAsync"/> を実行する。手元のコミットは残る</item>
    ///   <item><see cref="VersionControlOutcome.Conflicted"/> … 未解決の競合が残っているので送れない</item>
    /// </list>
    /// </summary>
    /// <param name="message">コミットメッセージ（空は拒否する）。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult<SubmitReport>> SubmitAsync(
        string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// 最新を取得する（sync）。
    ///
    /// <para>
    /// Lore の sync は競合しても成功を返すため、この実装は sync 後に状態を
    /// 引き直して競合を検出する。競合があれば結末は
    /// <see cref="VersionControlOutcome.Conflicted"/> で、
    /// <see cref="SyncReport.Conflicts"/> に対象ファイルが入る。
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult<SyncReport>> FetchLatestAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 競合したファイルを解決する。解決後、マージのコミットまで行う。
    /// </summary>
    /// <param name="relativePaths">対象のリポジトリ相対パス（競合しているもの）。</param>
    /// <param name="choice">
    /// 「自分の変更を残す」か「リモートを採用」か。Lore の mine / theirs への
    /// 対応付けは実装側で行う（逆になっているため呼び出し側では扱わない）。
    /// </param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult> ResolveConflictsAsync(
        IReadOnlyList<string> relativePaths, ConflictResolutionChoice choice,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// いま進行中のマージの「向き」を返す。
    ///
    /// <para>
    /// マージエディタの見出し（「取り込み元: ブランチ feature」「現在: main」）と、
    /// 「両方を取り込む」の並び順を決めるのに使う。
    /// 記録が無いときは <see cref="MergeContext.Unknown"/>（sync 扱い）。
    /// </para>
    /// </summary>
    MergeContext MergeContext { get; }

    /// <summary>
    /// **結果のテキストを書き込んで** 競合を解決する（マージエディタの「確定」）。
    ///
    /// <para>
    /// 書き込みは元の改行と BOM を保つ。競合の印が残っているテキストは
    /// Lore を呼ぶ前に拒否する（印が残っていると Lore は何もしないのに
    /// 成功を返すため、「解決した」という嘘になる）。
    /// </para>
    /// <para>
    /// 解決後は <see cref="ResolveConflictsAsync"/> と同じで、
    /// 残りの競合が無ければマージのコミットまで行う。
    /// </para>
    /// </summary>
    /// <param name="relativePath">対象のリポジトリ相対パス（1 件）。</param>
    /// <param name="resolvedText">書き込む中身（印を含まないこと）。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult> ResolveConflictsWithContentAsync(
        string relativePath, string resolvedText,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 「両方を取り込む」で競合を解決する。
    ///
    /// <para>
    /// 両者が同じ場所へ追加し合っただけの競合（印の「元」の節が空）でのみ成立する。
    /// 同じ箇所を両方が変更している場合は、1 件でも混ざっていれば
    /// **何も書き込まずに** 失敗させる（半端に書き換えない）。
    /// </para>
    /// <para>
    /// 並び順は「既に共有されていた側を先」。sync なら 取り込み元 → 現在、
    /// ブランチのマージなら 現在 → 取り込み元（<see cref="MergeContext"/> で決まる）。
    /// </para>
    /// </summary>
    /// <param name="relativePaths">対象のリポジトリ相対パス。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult> ResolveConflictsTakingBothAsync(
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default);

    // ── ブランチ ────────────────────────────────────────────

    /// <summary>
    /// ブランチを一覧する。
    ///
    /// <para>
    /// Lore は同名ブランチを LOCAL と REMOTE で別々に返すため、実装側で
    /// 名前ごとに 1 件へ統合してから返す。
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult<IReadOnlyList<BranchInfo>>> GetBranchesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>ブランチを作る。</summary>
    /// <param name="name">ブランチ名（空は拒否する）。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult> CreateBranchAsync(
        string name, CancellationToken cancellationToken = default);

    /// <summary>ブランチを切り替える。</summary>
    /// <param name="name">ブランチ名（空は拒否する）。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult> SwitchBranchAsync(
        string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 別のブランチを **現在のブランチへ取り込む**（マージ）。
    ///
    /// <para>結末の読み方:</para>
    /// <list type="bullet">
    ///   <item><see cref="VersionControlOutcome.Success"/> … 取り込んで手元にコミットまで済んだ。
    ///         ほかの人へ渡すには続けて <see cref="SubmitAsync"/> が要る</item>
    ///   <item><see cref="VersionControlOutcome.Conflicted"/> … 競合が出た。
    ///         <see cref="MergeReport.Conflicts"/> に対象ファイルが入る。
    ///         <see cref="ResolveConflictsAsync"/> の 2 択で解決すると、
    ///         そこでマージのコミットまで行われる</item>
    ///   <item><see cref="VersionControlOutcome.NeedsSync"/> … 分岐していて取り込めない。
    ///         先に <see cref="FetchLatestAsync"/> を実行する</item>
    ///   <item><see cref="VersionControlOutcome.RequiresConnection"/> … サーバに繋がらない</item>
    /// </list>
    /// <para>
    /// 現在のブランチ自身は取り込めない（実装が拒否する）。
    /// </para>
    /// </summary>
    /// <param name="sourceBranch">取り込み元のブランチ名（空は拒否する）。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult<MergeReport>> MergeBranchAsync(
        string sourceBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// ブランチを削除（アーカイブ）する。
    ///
    /// <para>
    /// ★Lore v0.9.0 に「ブランチの削除」は無く、<c>branch archive</c>
    /// （一覧から隠す）が相当する。コミットそのものは残るが、
    /// このエディタからは元に戻せない。利用者向けの語彙は「削除（アーカイブ）」。
    /// </para>
    /// <para>
    /// 現在のブランチと既定ブランチ（<see cref="VersionControlSettings.DefaultBranchName"/>）は
    /// 実装が拒否する（<see cref="VersionControlOutcome.Failed"/>）。
    /// </para>
    /// </summary>
    /// <param name="name">ブランチ名（空は拒否する）。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult> ArchiveBranchAsync(
        string name, CancellationToken cancellationToken = default);

    // ── 履歴 ────────────────────────────────────────────────

    /// <summary>
    /// 履歴を取得する（サーバ必須）。
    ///
    /// <para>
    /// オフラインでは <see cref="VersionControlOutcome.RequiresConnection"/> を返す
    /// （Lore の history はオフラインで失敗する）。
    /// </para>
    /// </summary>
    /// <param name="maxCount">最大件数（0 以下なら設定の既定値）。</param>
    /// <param name="cancellationToken">中断用。</param>
    Task<VersionControlResult<IReadOnlyList<RevisionInfo>>> GetHistoryAsync(
        int maxCount = 0, CancellationToken cancellationToken = default);
}
