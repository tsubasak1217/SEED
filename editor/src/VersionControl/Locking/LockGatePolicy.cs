// ============================================================
//  LockGatePolicy.cs — 「通すか／注意するか／止めるか」の判定表（純関数）
//
//  【役割】
//  ロックの保持者・サーバへの到達可否・ログイン状態・強制方針の 4 つから、
//  ゲートが取るべき行動を 1 つ決める。**ここに副作用は無い**
//  （通信もファイル操作もログもしない）。実際に問い合わせて動くのは
//  <see cref="LockGatekeeper"/> の仕事。
//
//  【なぜ純関数へ切り出すのか】
//  この判定は「他人の作業を止めるかどうか」を決める。間違えると
//  ・止めるべきときに通して、他の人の編集を黙って上書きする
//  ・通すべきときに止めて、サーバ不調のあいだ誰も保存できなくなる
//  のどちらかが起きる。WPF も通信も混ざっていない形にしておけば、
//  全分岐を単体テストで固定できる（editor/tests/VersionControlTests）。
//
//  【判定表（保存ゲート）】
//  上から順に見て、最初に当てはまった行で決まる。
//
//   # | バージョン管理 | サーバ到達 | ログイン | 保持者 | 方針      | 結果
//  ---+----------------+-----------+---------+--------+-----------+------------------
//   1 | 無し           | -         | -       | -      | -         | 通す（無言）
//   2 | 有り           | ✕        | -       | 不明   | -         | 通す＋注意
//   3 | 有り           | ○        | 匿名    | なし   | -         | 通す（無言）
//   4 | 有り           | ○        | 匿名    | 誰か   | -         | 通す＋注意
//   5 | 有り           | ○        | 済      | 自分   | -         | 通す（無言）
//   6 | 有り           | ○        | 済      | 不明   | -         | 通す＋注意
//   7 | 有り           | ○        | 済      | 他人   | Enforce   | **止める**
//   8 | 有り           | ○        | 済      | 他人   | WarnOnly  | 通す＋注意
//   9 | 有り           | ○        | 済      | なし   | -         | その場で取得 →
//                                                                 取得後の判定へ
//
//  【2・4 行目（情報が無いときは止めない）の理由】
//  サーバに繋がらない／匿名のときは「他人のロック」という事実を確かめられない。
//  確かめられないことを根拠に保存を止めると、サーバが落ちている間は誰も
//  作業できなくなる。ロックは事故防止の仕組みであって、
//  作業を人質に取る仕組みではない。だから通して、注意だけ残す。
//
//  【6 行目（所有者不明）の理由】
//  サーバ認証を入れる前に取られたロックは所有者が `<unknown>` になる。
//  これは自分のものかもしれず、しかも誰も解除できない
//  （<see cref="Presentation.VersionControlDisplay.CanRelease"/> は Self のみ許す）。
//  止めると永久に保存できないファイルが生まれるため、注意に留める。
//
//  【9 行目（誰も持っていないときは取ってから通す）の理由】
//  「保存した＝編集した」なので、その時点でロックを持っているのが正しい。
//  ただし **匿名のときは取らない**（3 行目で先に返る）。匿名で取ると
//  所有者 `<unknown>` のロックができ、誰にも解除できない 6 行目の状態を
//  自分で作り出してしまう。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Locking;

/// <summary>
/// ゲートに渡す状況（不変）。判定に要る情報をこれだけに限定する。
/// </summary>
/// <param name="IsVersionControlAvailable">このプロジェクトがバージョン管理下にあるか。</param>
/// <param name="IsServerReachable">ロックの状態をサーバから取得できたか。</param>
/// <param name="IsSignedIn">SEED アカウントでログイン中か。</param>
/// <param name="Holder">ロックの保持者（サーバから取れていないときは意味を持たない）。</param>
/// <param name="OwnerName">保持者の名前（分からなければ空）。</param>
/// <param name="Policy">強制方針。</param>
/// <param name="RelativePath">対象のリポジトリ相対パス（文言に載せる）。</param>
public readonly record struct LockGateSituation(
    bool IsVersionControlAvailable,
    bool IsServerReachable,
    bool IsSignedIn,
    LockHolder Holder,
    string OwnerName,
    LockEnforcementPolicy Policy,
    string RelativePath);

/// <summary>
/// ロックのゲートの判定（純関数のみ）。
/// </summary>
public static class LockGatePolicy
{
    /// <summary>
    /// 保存ゲートの判定。判定表の 1〜9 行目をそのまま実装している。
    /// </summary>
    /// <param name="situation">判定に使う状況。</param>
    /// <returns>取るべき行動。</returns>
    public static LockGateVerdict DecideForWrite(LockGateSituation situation)
    {
        var path  = situation.RelativePath ?? string.Empty;
        var owner = DisplayOwner(situation.OwnerName);

        // 1) バージョン管理下に無い → ロックという概念が無いので素通し。
        if (!situation.IsVersionControlAvailable)
        {
            return Allow(LockGateReason.VersionControlUnavailable, path);
        }

        // 2) サーバへ問い合わせられなかった → 事実が分からない。止めずに注意だけ。
        if (!situation.IsServerReachable)
        {
            return Warn(
                LockGateReason.ServerUnreachable,
                Format(VersionControlMessages.LOCK_GATE_WARN_UNREACHABLE_FORMAT, path),
                path, situation.Holder, situation.OwnerName);
        }

        // 3・4) 匿名 → 「自分のロック」が成立しないので、止めることも取ることもしない。
        //       ただし誰かが持っているなら、その事実は伝える価値がある。
        if (!situation.IsSignedIn)
        {
            if (situation.Holder == LockHolder.None)
                return Allow(LockGateReason.NoLock, path);

            return Warn(
                LockGateReason.NotSignedIn,
                Format(VersionControlMessages.LOCK_GATE_WARN_ANONYMOUS_FORMAT, path),
                path, situation.Holder, situation.OwnerName);
        }

        // 5〜9) ログイン中。保持者で分かれる。
        switch (situation.Holder)
        {
            // 5) 自分のロック。何も言わずに通す（最も多い経路）。
            case LockHolder.Self:
                return Allow(LockGateReason.HeldBySelf, path);

            // 6) 所有者不明の古いロック。誰も解除できないので止めない。
            case LockHolder.Unknown:
                return Warn(
                    LockGateReason.HeldByUnknownOwner,
                    Format(VersionControlMessages.LOCK_GATE_WARN_UNKNOWN_OWNER_FORMAT, path),
                    path, situation.Holder, situation.OwnerName);

            // 7・8) 他の人のロック。方針次第で止める／注意する。
            case LockHolder.Other:
                if (situation.Policy == LockEnforcementPolicy.WarnOnly)
                {
                    return Warn(
                        LockGateReason.WarnOnlyPolicy,
                        Format(VersionControlMessages.LOCK_GATE_WARN_ONLY_FORMAT, owner, path),
                        path, situation.Holder, situation.OwnerName);
                }

                return new LockGateVerdict(
                    LockGateAction.Block,
                    LockGateReason.HeldByOther,
                    Format(VersionControlMessages.LOCK_GATE_BLOCKED_BY_OTHER_FORMAT, owner, path),
                    path, situation.Holder, situation.OwnerName);

            // 9) 誰も持っていない。編集する以上は持っておくべきなので取りに行く。
            default:
                return new LockGateVerdict(
                    LockGateAction.AcquireThenDecide,
                    LockGateReason.NoLock,
                    message: null,
                    path, situation.Holder, situation.OwnerName);
        }
    }

    /// <summary>
    /// 「その場で取得」したあとの判定。
    ///
    /// <para>
    /// 取得と取得後の照会のあいだに他の人が割り込むことがある（Lore の
    /// <c>lock acquire</c> は既に取得済みでも成功を返すため、結末は
    /// <see cref="LockAcquireOutcome"/> でしか分からない）。
    /// 取れていなければ、その理由に応じて判定表の 6〜8 行目と同じ扱いにする。
    /// </para>
    /// </summary>
    /// <param name="outcome">取得の結末。</param>
    /// <param name="ownerName">取れなかった場合の保持者名。</param>
    /// <param name="policy">強制方針。</param>
    /// <param name="relativePath">対象のリポジトリ相対パス。</param>
    /// <returns>取るべき行動（<see cref="LockGateAction.AcquireThenDecide"/> は返らない）。</returns>
    public static LockGateVerdict DecideAfterAcquire(
        LockAcquireOutcome outcome,
        string? ownerName,
        LockEnforcementPolicy policy,
        string? relativePath)
    {
        var path  = relativePath ?? string.Empty;
        var owner = DisplayOwner(ownerName);

        switch (outcome)
        {
            // 取れた／もともと自分のものだった → 通す。
            case LockAcquireOutcome.Acquired:
                return Allow(LockGateReason.Acquired, path);

            case LockAcquireOutcome.AlreadyMine:
                return Allow(LockGateReason.HeldBySelf, path);

            // 割り込まれた。判定表 7・8 行目と同じ扱い。
            case LockAcquireOutcome.HeldByOther:
                if (policy == LockEnforcementPolicy.WarnOnly)
                {
                    return Warn(
                        LockGateReason.WarnOnlyPolicy,
                        Format(VersionControlMessages.LOCK_GATE_WARN_ONLY_FORMAT, owner, path),
                        path, LockHolder.Other, ownerName);
                }

                return new LockGateVerdict(
                    LockGateAction.Block,
                    LockGateReason.HeldByOther,
                    Format(VersionControlMessages.LOCK_GATE_BLOCKED_BY_OTHER_FORMAT, owner, path),
                    path, LockHolder.Other, ownerName);

            // 所有者不明。判定表 6 行目と同じく止めない。
            case LockAcquireOutcome.HeldByUnknown:
                return Warn(
                    LockGateReason.HeldByUnknownOwner,
                    Format(VersionControlMessages.LOCK_GATE_WARN_UNKNOWN_OWNER_FORMAT, path),
                    path, LockHolder.Unknown, ownerName);

            // 取得そのものが失敗した。
            // ここは「情報が無い」ではなく「取れなかった」なので、
            // 強制方針のときは止める（取れていないのに編集させない）。
            default:
                if (policy == LockEnforcementPolicy.WarnOnly)
                {
                    return Warn(
                        LockGateReason.AcquireFailed,
                        Format(VersionControlMessages.LOCK_GATE_ACQUIRE_FAILED_FORMAT, path),
                        path, LockHolder.None, ownerName);
                }

                return new LockGateVerdict(
                    LockGateAction.Block,
                    LockGateReason.AcquireFailed,
                    Format(VersionControlMessages.LOCK_GATE_ACQUIRE_FAILED_FORMAT, path),
                    path, LockHolder.None, ownerName);
        }
    }

    /// <summary>
    /// 送信ゲートの判定。
    ///
    /// <para>
    /// サーバの push フックは「どのファイルが変わったか」を受け取れないため、
    /// 他の人のロックを守れるのはクライアント側だけ。送信しようとしている
    /// 変更ファイルのロック状態をまとめて見て、1 件でも他の人のものがあれば止める。
    /// </para>
    /// <para>
    /// 保存ゲートと違い、ここでは **ロックを取りに行かない**。
    /// 送信は「もう手元で編集し終えたものを送る」操作で、いまさら
    /// 編集権を取っても意味が無い（保存の時点で取れているべきもの）。
    /// </para>
    /// </summary>
    /// <param name="locks">変更ファイルのロック状態（照会結果）。</param>
    /// <param name="isVersionControlAvailable">バージョン管理下にあるか。</param>
    /// <param name="isServerReachable">ロックの状態を取得できたか。</param>
    /// <param name="isSignedIn">ログイン中か。</param>
    /// <param name="policy">強制方針。</param>
    /// <returns>取るべき行動。</returns>
    public static LockGateSubmitVerdict DecideForSubmit(
        IReadOnlyList<LockInfo>? locks,
        bool isVersionControlAvailable,
        bool isServerReachable,
        bool isSignedIn,
        LockEnforcementPolicy policy)
        => DecideForManyPaths(
               locks, isVersionControlAvailable, isServerReachable, isSignedIn, policy,
               SubmitWording);

    /// <summary>
    /// 一括書き込みゲートの判定（プロジェクトの形式アップグレードなど）。
    ///
    /// <para>
    /// 判定表は送信ゲートと**まったく同じ**（ロックを取りに行かず、他の人のロックが
    /// 1 件でもあれば止める）。違うのは文言だけなので、判定の本体は
    /// <see cref="DecideForManyPaths"/> で共有する。表を 2 つ持つと片方だけ直す事故が起きる。
    /// </para>
    /// <para>
    /// 保存ゲート（<see cref="DecideForWrite"/>）と違って**取りに行かない**のは、
    /// 対象が数十〜数百ファイルになるためである。まとめてロックを取ると、
    /// 実行した人が大量のロックを握ったままになり、他の人が何も編集できなくなる。
    /// </para>
    /// </summary>
    /// <param name="locks">書き換える予定のファイルのロック状態（照会結果）。</param>
    /// <param name="isVersionControlAvailable">バージョン管理下にあるか。</param>
    /// <param name="isServerReachable">ロックの状態を取得できたか。</param>
    /// <param name="isSignedIn">ログイン中か。</param>
    /// <param name="policy">強制方針。</param>
    /// <returns>取るべき行動。</returns>
    public static LockGateSubmitVerdict DecideForBulkWrite(
        IReadOnlyList<LockInfo>? locks,
        bool isVersionControlAvailable,
        bool isServerReachable,
        bool isSignedIn,
        LockEnforcementPolicy policy)
        => DecideForManyPaths(
               locks, isVersionControlAvailable, isServerReachable, isSignedIn, policy,
               BulkWriteWording);

    /// <summary>
    /// 複数パスをまとめて見るゲートの文言一式。
    ///
    /// <para>
    /// 送信ゲートと一括書き込みゲートは判定が同じで文言だけが違う。
    /// 文言を差し替えられるようにしておくことで、判定表を 1 つに保てる。
    /// </para>
    /// </summary>
    /// <param name="UnreachableWarning">サーバへ届かなかったときの注意。</param>
    /// <param name="AnonymousWarning">ログインしていないときの注意。</param>
    /// <param name="WarnOnlyFormat">方針が「注意のみ」のときの注意（書式: 件数）。</param>
    /// <param name="BlockedFormat">止めるときの文言（書式: 件数, 内訳）。</param>
    public readonly record struct LockGateManyPathsWording(
        string UnreachableWarning,
        string AnonymousWarning,
        string WarnOnlyFormat,
        string BlockedFormat);

    /// <summary>送信ゲートの文言。</summary>
    private static readonly LockGateManyPathsWording SubmitWording = new(
        VersionControlMessages.SUBMIT_LOCK_WARN_UNREACHABLE,
        VersionControlMessages.SUBMIT_LOCK_WARN_ANONYMOUS,
        VersionControlMessages.SUBMIT_LOCK_WARN_ONLY_FORMAT,
        VersionControlMessages.SUBMIT_BLOCKED_BY_LOCKS_FORMAT);

    /// <summary>一括書き込みゲートの文言。</summary>
    private static readonly LockGateManyPathsWording BulkWriteWording = new(
        VersionControlMessages.BULK_WRITE_LOCK_WARN_UNREACHABLE,
        VersionControlMessages.BULK_WRITE_LOCK_WARN_ANONYMOUS,
        VersionControlMessages.BULK_WRITE_LOCK_WARN_ONLY_FORMAT,
        VersionControlMessages.BULK_WRITE_BLOCKED_BY_LOCKS_FORMAT);

    /// <summary>
    /// 複数パスをまとめて見るゲートの判定本体（送信・一括書き込みで共有）。
    /// </summary>
    /// <param name="locks">対象ファイルのロック状態（照会結果）。</param>
    /// <param name="isVersionControlAvailable">バージョン管理下にあるか。</param>
    /// <param name="isServerReachable">ロックの状態を取得できたか。</param>
    /// <param name="isSignedIn">ログイン中か。</param>
    /// <param name="policy">強制方針。</param>
    /// <param name="wording">文言一式。</param>
    /// <returns>取るべき行動。</returns>
    private static LockGateSubmitVerdict DecideForManyPaths(
        IReadOnlyList<LockInfo>? locks,
        bool isVersionControlAvailable,
        bool isServerReachable,
        bool isSignedIn,
        LockEnforcementPolicy policy,
        LockGateManyPathsWording wording)
    {
        // 1) バージョン管理下に無い → そもそも送信できないが、ここでは止めない。
        if (!isVersionControlAvailable)
        {
            return new LockGateSubmitVerdict(
                LockGateAction.Allow, LockGateReason.VersionControlUnavailable, message: null);
        }

        // 2) サーバへ問い合わせられなかった → 確かめられないので止めない。
        if (!isServerReachable)
        {
            return new LockGateSubmitVerdict(
                LockGateAction.AllowWithWarning,
                LockGateReason.ServerUnreachable,
                wording.UnreachableWarning);
        }

        // 他の人のロックだけを集める。
        // 所有者不明（Unknown）は保存ゲートと同じ理由でここでも止めない。
        var blocking = new List<LockInfo>();
        if (locks is not null)
        {
            foreach (var info in locks)
            {
                if (info.Holder == LockHolder.Other) blocking.Add(info);
            }
        }

        // 3) 匿名 → 「他の人のもの」と断定できない。止めずに、あれば注意する。
        if (!isSignedIn)
        {
            if (blocking.Count == 0)
            {
                return new LockGateSubmitVerdict(
                    LockGateAction.Allow, LockGateReason.NoLock, message: null);
            }

            return new LockGateSubmitVerdict(
                LockGateAction.AllowWithWarning,
                LockGateReason.NotSignedIn,
                wording.AnonymousWarning,
                blocking);
        }

        // 4) 他の人のロックが 1 件も無い → そのまま送信。
        if (blocking.Count == 0)
        {
            return new LockGateSubmitVerdict(
                LockGateAction.Allow, LockGateReason.NoLock, message: null);
        }

        // 5) 方針が「注意のみ」なら止めない。
        if (policy == LockEnforcementPolicy.WarnOnly)
        {
            return new LockGateSubmitVerdict(
                LockGateAction.AllowWithWarning,
                LockGateReason.WarnOnlyPolicy,
                Format(wording.WarnOnlyFormat,
                       blocking.Count.ToString(CultureInfo.InvariantCulture)),
                blocking);
        }

        // 6) 止める。どのファイルが誰のものかを全部見せる。
        return new LockGateSubmitVerdict(
            LockGateAction.Block,
            LockGateReason.HeldByOther,
            Format(wording.BlockedFormat,
                   blocking.Count.ToString(CultureInfo.InvariantCulture),
                   DescribeLocks(blocking)),
            blocking);
    }

    /// <summary>
    /// 止める原因になったロックを「・パス（名前 さん）」の複数行にする。
    /// </summary>
    /// <param name="locks">対象のロック。</param>
    public static string DescribeLocks(IReadOnlyList<LockInfo> locks)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < locks.Count; i++)
        {
            if (i > 0) builder.Append('\n');
            builder.Append(Format(
                VersionControlMessages.SUBMIT_LOCK_LINE_FORMAT,
                locks[i].Path,
                DisplayOwner(locks[i].Owner)));
        }
        return builder.ToString();
    }

    // ── 共通ヘルパー ────────────────────────────────────────

    /// <summary>そのまま通す判定を作る（文言なし）。</summary>
    /// <param name="reason">理由。</param>
    /// <param name="path">対象パス。</param>
    private static LockGateVerdict Allow(LockGateReason reason, string path)
        => new(LockGateAction.Allow, reason, message: null, path);

    /// <summary>注意して通す判定を作る。</summary>
    /// <param name="reason">理由。</param>
    /// <param name="message">文言。</param>
    /// <param name="path">対象パス。</param>
    /// <param name="holder">保持者。</param>
    /// <param name="owner">保持者名。</param>
    private static LockGateVerdict Warn(
        LockGateReason reason, string message, string path,
        LockHolder holder, string? owner)
        => new(LockGateAction.AllowWithWarning, reason, message, path, holder, owner);

    /// <summary>
    /// 表示に使う所有者名を決める。空や <c>&lt;unknown&gt;</c> は「利用者不明」にする
    /// （「&lt;unknown&gt; さんがロック中」と出さないため）。
    /// </summary>
    /// <param name="owner">Lore が返した所有者名。</param>
    private static string DisplayOwner(string? owner)
        => LockInfo.IsUnknownOwnerName(owner)
            ? VersionControlMessages.PANEL_IDENTITY_UNKNOWN
            : owner!.Trim();

    /// <summary>文言の書式を当てる（文化依存の書式差を避けるため固定文化で行う）。</summary>
    /// <param name="format">書式。</param>
    /// <param name="args">引数。</param>
    private static string Format(string format, params object[] args)
        => string.Format(CultureInfo.InvariantCulture, format, args);
}
