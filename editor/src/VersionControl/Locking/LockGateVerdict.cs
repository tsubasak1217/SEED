// ============================================================
//  LockGateVerdict.cs — ゲートの判定結果（通す／注意して通す／止める）
//
//  【役割】
//  <see cref="LockGatePolicy"/> が返す「結論」を 1 つの不変値で表す。
//  呼び出し側（保存経路・送信）は <see cref="LockGateVerdict.CanProceed"/> と
//  <see cref="LockGateVerdict.Message"/> だけ見れば正しく振る舞える。
//
//  【なぜ理由（Reason）を持つのか】
//  ・利用者向けメッセージは Reason ごとに違う（誰のロックか／サーバに繋がらない／匿名）
//  ・ログに出すときに、文言ではなく理由コードで grep したい
//  ・テストが「どの分岐を通ったか」を文言の一致ではなく列挙で固定できる
//  文言の比較でテストを書くと、言い回しを直すたびにテストが壊れる。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Locking;

/// <summary>
/// ゲートが取るべき行動。
/// </summary>
public enum LockGateAction
{
    /// <summary>そのまま通す（利用者へは何も出さない）。</summary>
    Allow,

    /// <summary>通すが、注意を 1 件出す（作業は止めない）。</summary>
    AllowWithWarning,

    /// <summary>
    /// まだ誰も持っていないので、その場でロックを取ってから決め直す。
    /// 取得の結果は <see cref="LockGatePolicy.DecideAfterAcquire"/> で判定する。
    /// </summary>
    AcquireThenDecide,

    /// <summary>止める（書き込ませない）。</summary>
    Block,
}

/// <summary>
/// なぜその行動になったのか。文言と 1 対 1 で対応する。
/// </summary>
public enum LockGateReason
{
    /// <summary>このプロジェクトはバージョン管理下に無い（ロックの概念が無い）。</summary>
    VersionControlUnavailable,

    /// <summary>誰もロックしていない。</summary>
    NoLock,

    /// <summary>自分が持っている。</summary>
    HeldBySelf,

    /// <summary>その場でロックを取れた。</summary>
    Acquired,

    /// <summary>ロックを取ろうとしたが取れなかった（通信失敗など）。</summary>
    AcquireFailed,

    /// <summary>他の人が持っている。</summary>
    HeldByOther,

    /// <summary>誰かが持っているが所有者が不明（サーバ認証が無い頃の古いロック）。</summary>
    HeldByUnknownOwner,

    /// <summary>サーバへ問い合わせられなかった（オフライン・タイムアウト）。</summary>
    ServerUnreachable,

    /// <summary>ログインしていないので、持ち主が自分かどうか判定できない。</summary>
    NotSignedIn,

    /// <summary>止めるべき状況だが、方針が「注意のみ」なので通した。</summary>
    WarnOnlyPolicy,
}

/// <summary>
/// 1 ファイルぶんの判定結果（不変）。
/// </summary>
public sealed class LockGateVerdict
{
    /// <summary>取るべき行動。</summary>
    public LockGateAction Action { get; }

    /// <summary>理由。</summary>
    public LockGateReason Reason { get; }

    /// <summary>
    /// 利用者へ見せる文言。<see cref="LockGateAction.Allow"/> のときは空。
    /// </summary>
    public string Message { get; }

    /// <summary>対象のリポジトリ相対パス（ログと文言に使う）。</summary>
    public string RelativePath { get; }

    /// <summary>判定の根拠になった保持者（分からなければ <see cref="LockHolder.None"/>）。</summary>
    public LockHolder Holder { get; }

    /// <summary>保持者の名前（分からなければ空）。</summary>
    public string OwnerName { get; }

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="action">取るべき行動。</param>
    /// <param name="reason">理由。</param>
    /// <param name="message">利用者へ見せる文言。</param>
    /// <param name="relativePath">対象のリポジトリ相対パス。</param>
    /// <param name="holder">判定の根拠になった保持者。</param>
    /// <param name="ownerName">保持者の名前。</param>
    public LockGateVerdict(
        LockGateAction action,
        LockGateReason reason,
        string? message,
        string? relativePath,
        LockHolder holder = LockHolder.None,
        string? ownerName = null)
    {
        Action       = action;
        Reason       = reason;
        Message      = message      ?? string.Empty;
        RelativePath = relativePath ?? string.Empty;
        Holder       = holder;
        OwnerName    = ownerName    ?? string.Empty;
    }

    /// <summary>書き込んでよいか（止められていないか）。</summary>
    public bool CanProceed => Action != LockGateAction.Block;

    /// <summary>利用者へ何か伝えるべきか。</summary>
    public bool HasMessage => Message.Length > 0;

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => $"{Action}/{Reason} {RelativePath} holder={Holder} owner={OwnerName}";
}

/// <summary>
/// 送信（commit + push）に対する判定結果（不変）。
///
/// <para>
/// 保存ゲートと違い、対象が複数ファイルになる。止めるときは
/// 「どのファイルが誰のロックで止まっているのか」を全部見せる必要があるため、
/// 内訳（<see cref="BlockingLocks"/>）を持つ。
/// </para>
/// </summary>
public sealed class LockGateSubmitVerdict
{
    /// <summary>取るべき行動（<see cref="LockGateAction.AcquireThenDecide"/> は返らない）。</summary>
    public LockGateAction Action { get; }

    /// <summary>理由。</summary>
    public LockGateReason Reason { get; }

    /// <summary>利用者へ見せる文言（通すだけのときは空）。</summary>
    public string Message { get; }

    /// <summary>止める原因になったロック（他の人が持っているもの）。</summary>
    public IReadOnlyList<LockInfo> BlockingLocks { get; }

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="action">取るべき行動。</param>
    /// <param name="reason">理由。</param>
    /// <param name="message">利用者へ見せる文言。</param>
    /// <param name="blockingLocks">止める原因になったロック。</param>
    public LockGateSubmitVerdict(
        LockGateAction action,
        LockGateReason reason,
        string? message,
        IReadOnlyList<LockInfo>? blockingLocks = null)
    {
        Action        = action;
        Reason        = reason;
        Message       = message ?? string.Empty;
        BlockingLocks = blockingLocks ?? Array.Empty<LockInfo>();
    }

    /// <summary>送信してよいか。</summary>
    public bool CanProceed => Action != LockGateAction.Block;

    /// <summary>利用者へ何か伝えるべきか。</summary>
    public bool HasMessage => Message.Length > 0;

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => $"{Action}/{Reason} blocking={BlockingLocks.Count}";
}
