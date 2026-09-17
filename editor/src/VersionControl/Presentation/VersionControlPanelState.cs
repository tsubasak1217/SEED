// ============================================================
//  VersionControlPanelState.cs — パネルの状態機械（WPF を知らない側）
//
//  【役割】
//  「いま何が実行中か」「どのボタンが押せるか」「何色で何と出すか」を
//  すべてここで決める。ビュー（XAML + コードビハインド）は、
//  このオブジェクトを読んでコントロールへ写すだけにする。
//
//  【なぜ分けるのか】
//  ボタンの有効条件は分岐が多く（利用可能か / 実行中か / メッセージが空か /
//  競合が残っているか）、しかも間違えても**ビルドは通る**。
//  GUI を起動しないと確かめられない場所に置くと、実質だれも検証できない。
//  WPF に依存しない素のクラスにして単体テストで固定する。
//
//  【使い方（ビュー側の流れ）】
//    1. 操作を始める前に BeginOperation(op)
//    2. await でプロバイダを呼ぶ
//    3. 終わったら EndOperation(result)
//    4. 状態を取り直したら ApplyStatus(status)
//    5. 毎回 Sync 系メソッドでコントロールへ反映する
//
//  【スレッド】
//  このクラス自体はスレッド安全ではない。**UI スレッドからのみ触る**こと
//  （VersionControlService.StatusChanged はワーカースレッドから来るので、
//    ビュー側が Dispatcher へ移してから ApplyStatus を呼ぶ）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Presentation;

/// <summary>
/// パネルが実行し得る操作。実行中の表示と、同時実行の抑止に使う。
/// </summary>
public enum VersionControlOperation
{
    /// <summary>何も実行していない。</summary>
    None,

    /// <summary>状態の取り直し。</summary>
    Refresh,

    /// <summary>最新を取得。</summary>
    FetchLatest,

    /// <summary>送信。</summary>
    Submit,

    /// <summary>競合の解決。</summary>
    Resolve,

    /// <summary>ブランチの作成・切り替え。</summary>
    Branch,

    /// <summary>履歴の取得。</summary>
    History,

    /// <summary>ロックの取得・解放・一覧。</summary>
    Lock,
}

/// <summary>
/// Version Control パネルの状態（UI スレッド専用）。
/// </summary>
public sealed class VersionControlPanelState
{
    /// <summary>バージョン管理が使えるか（偽なら操作 UI を出さない）。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>実行中の操作（<see cref="VersionControlOperation.None"/> なら待機中）。</summary>
    public VersionControlOperation RunningOperation { get; private set; }
        = VersionControlOperation.None;

    /// <summary>何か実行中か。</summary>
    public bool IsBusy => RunningOperation != VersionControlOperation.None;

    /// <summary>直近に取得できた作業コピーの状態（未取得なら null）。</summary>
    public WorkingCopyStatus? Status { get; private set; }

    /// <summary>変更一覧のグループ（競合が先頭）。</summary>
    public IReadOnlyList<ChangeListGroup> Groups { get; private set; }
        = Array.Empty<ChangeListGroup>();

    /// <summary>入力中の送信メッセージ。</summary>
    public string CommitMessage { get; private set; } = string.Empty;

    /// <summary>いま出している 1 行メッセージ。</summary>
    public VersionControlNotice Notice { get; private set; } = VersionControlNotice.None;

    /// <summary>未解決の競合があるか。</summary>
    public bool HasUnresolvedConflicts => Status?.HasUnresolvedConflicts ?? false;

    /// <summary>未解決の競合の件数。</summary>
    public int UnresolvedConflictCount => Status?.UnresolvedConflicts.Count ?? 0;

    /// <summary>変更（競合を含む）の件数。</summary>
    public int ChangeCount => Status?.Changes.Count ?? 0;

    /// <summary>現在のブランチ名（未取得なら空文字）。</summary>
    public string BranchName => Status?.BranchName ?? string.Empty;

    // ── ボタンの有効条件（分岐はここだけ）────────────────────

    /// <summary>
    /// 「送信」を押せるか。
    /// メッセージが空のときと、未解決の競合が残っているときは押せない
    /// （どちらもプロバイダ側で必ず弾かれるため、押させても無駄足になる）。
    /// </summary>
    public bool CanSubmit
        => IsAvailable
           && !IsBusy
           && !string.IsNullOrWhiteSpace(CommitMessage)
           && !HasUnresolvedConflicts;

    /// <summary>「最新を取得」を押せるか。</summary>
    public bool CanFetchLatest => IsAvailable && !IsBusy;

    /// <summary>更新ボタンを押せるか。</summary>
    public bool CanRefresh => IsAvailable && !IsBusy;

    /// <summary>ブランチの切り替え・作成ができるか。</summary>
    public bool CanChangeBranch => IsAvailable && !IsBusy;

    /// <summary>競合の 2 択ボタンを押せるか。</summary>
    public bool CanResolveConflicts => IsAvailable && !IsBusy && HasUnresolvedConflicts;

    /// <summary>ロックの取得・解放ができるか。</summary>
    public bool CanChangeLocks => IsAvailable && !IsBusy;

    /// <summary>
    /// 「最新を取得」ボタンを強調表示すべきか
    /// （直前の操作が <see cref="VersionControlOutcome.NeedsSync"/> だったとき）。
    /// </summary>
    public bool EmphasizeFetchLatest => Notice.EmphasizeFetchLatest;

    /// <summary>不確定プログレスを出すべきか。</summary>
    public bool ShowProgress => IsBusy;

    /// <summary>
    /// 実行中の表示文字列（待機中は空文字）。
    /// </summary>
    public string BusyText
        => RunningOperation == VersionControlOperation.None
            ? string.Empty
            : string.Format(
                VersionControlMessages.PANEL_BUSY_FORMAT, ToOperationName(RunningOperation));

    /// <summary>
    /// 「送信」が押せない理由（押せるときは空文字）。ボタンのツールチップに出す。
    /// 無効になっている理由が分からないボタンは、利用者にとって故障と同じ。
    /// </summary>
    public string SubmitBlockedReason
    {
        get
        {
            if (!IsAvailable)             return VersionControlMessages.UNAVAILABLE;
            if (HasUnresolvedConflicts)   return VersionControlMessages.SUBMIT_BLOCKED_BY_CONFLICTS;
            if (string.IsNullOrWhiteSpace(CommitMessage))
                                          return VersionControlMessages.SUBMIT_MESSAGE_REQUIRED;
            return string.Empty;
        }
    }

    // ── 遷移 ────────────────────────────────────────────────

    /// <summary>
    /// バージョン管理が使えるかを設定する。使えなくなったら状態も捨てる
    /// （古い一覧が残ると「まだ使える」と誤解させるため）。
    /// </summary>
    /// <param name="isAvailable">使えるか。</param>
    public void SetAvailability(bool isAvailable)
    {
        IsAvailable = isAvailable;
        if (isAvailable) return;

        Status = null;
        Groups = Array.Empty<ChangeListGroup>();
        Notice = VersionControlNotice.None;
    }

    /// <summary>
    /// 操作を開始する。実行中の表示へ移り、主操作のボタンを無効にする。
    ///
    /// <para>
    /// すでに別の操作が走っているときは開始しない（偽を返す）。
    /// プロバイダ側も直列化しているので壊れはしないが、
    /// 「押すたびにキューが伸びて反応が無い」状態を作らないために入口で止める。
    /// </para>
    /// </summary>
    /// <param name="operation">開始する操作。</param>
    /// <returns>開始できたか。</returns>
    public bool BeginOperation(VersionControlOperation operation)
    {
        if (operation == VersionControlOperation.None) return false;
        if (IsBusy) return false;

        RunningOperation = operation;

        // 前の結果は消す（実行中に古い成功メッセージが残っていると紛らわしい）。
        Notice = VersionControlNotice.None;
        return true;
    }

    /// <summary>
    /// 操作を終える。結果から 1 行メッセージを決める。
    /// </summary>
    /// <param name="result">操作結果（null なら何も表示しない）。</param>
    public void EndOperation(VersionControlResult? result)
    {
        RunningOperation = VersionControlOperation.None;
        Notice           = VersionControlNotice.FromResult(result);
    }

    /// <summary>
    /// 取得した状態を反映する（変更一覧のグループもここで組み立てる）。
    /// </summary>
    /// <param name="status">新しい状態。</param>
    public void ApplyStatus(WorkingCopyStatus? status)
    {
        Status = status;
        Groups = ChangeListBuilder.Build(status);
    }

    /// <summary>送信メッセージを設定する。</summary>
    /// <param name="message">入力されたメッセージ。</param>
    public void SetCommitMessage(string? message)
        => CommitMessage = message ?? string.Empty;

    /// <summary>送信メッセージを空にする（送信成功後に呼ぶ）。</summary>
    public void ClearCommitMessage() => CommitMessage = string.Empty;

    /// <summary>1 行メッセージを差し替える（確認をやめたときなどに使う）。</summary>
    /// <param name="notice">新しいメッセージ。</param>
    public void SetNotice(VersionControlNotice? notice)
        => Notice = notice ?? VersionControlNotice.None;

    // ── 表示名 ──────────────────────────────────────────────

    /// <summary>操作の表示名を返す（実行中の表示に使う）。</summary>
    /// <param name="operation">操作。</param>
    public static string ToOperationName(VersionControlOperation operation) => operation switch
    {
        VersionControlOperation.Refresh     => VersionControlMessages.PANEL_OPERATION_REFRESH,
        VersionControlOperation.FetchLatest => VersionControlMessages.PANEL_OPERATION_FETCH,
        VersionControlOperation.Submit      => VersionControlMessages.PANEL_OPERATION_SUBMIT,
        VersionControlOperation.Resolve     => VersionControlMessages.PANEL_OPERATION_RESOLVE,
        VersionControlOperation.Branch      => VersionControlMessages.PANEL_OPERATION_BRANCH,
        VersionControlOperation.History     => VersionControlMessages.PANEL_OPERATION_HISTORY,
        VersionControlOperation.Lock        => VersionControlMessages.PANEL_OPERATION_LOCK,
        _                                   => string.Empty,
    };
}
