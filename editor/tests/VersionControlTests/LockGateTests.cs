// ============================================================
//  LockGateTests.cs — ロックのゲートの判定表を固定する
//
//  【なぜこのテストが要るのか】
//  この判定は「他の人の作業を止めるかどうか」を決める。間違えると
//   ・止めるべきときに通して、他の人の編集を黙って上書きする
//   ・通すべきときに止めて、サーバ不調のあいだ誰も保存できなくなる
//  のどちらかが起きる。判定表（LockGatePolicy のヘッダー）の行を
//  1 行 1 テストで固定し、条件の抜け落ちを機械的に検出する。
//
//  【文言ではなく理由コードで表明する】
//  メッセージの言い回しは変わるが、分岐は変わらない。
//  <see cref="LockGateReason"/> で表明しておけば、文言を直してもテストは壊れない。
//  例外は「止めた本文に相手の名前が入っているか」だけ（これは利用者が
//  次に何をすべきか判断する材料なので、内容そのものを検査する）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using SEEDEditor.VersionControl.Locking;
using SEEDEditor.VersionControl.Model;
using SpriteRigTests;

namespace SEEDEditor.Tests.VersionControl;

/// <summary>
/// ロックのゲート（純関数）のテスト。
/// </summary>
public static class LockGateTests
{
    /// <summary>テストで使う対象パス。</summary>
    private const string TARGET_PATH = "assets/scenes/prologue.scene";

    /// <summary>テストで使う「他の人」の名前。</summary>
    private const string OTHER_OWNER = "hanako";

    /// <summary>全テストを登録する。</summary>
    /// <param name="harness">登録先。</param>
    public static void Register(TestHarness harness)
    {
        // ── 保存ゲートの判定表 ──────────────────────────────
        harness.Add("ゲート: バージョン管理下に無ければ無言で通す", UnavailablePasses);
        harness.Add("ゲート: サーバに繋がらなければ注意して通す", UnreachablePassesWithWarning);
        harness.Add("ゲート: 匿名 + ロック無しは無言で通す", AnonymousWithoutLockPasses);
        harness.Add("ゲート: 匿名 + 他人のロックは注意して通す", AnonymousWithLockWarns);
        harness.Add("ゲート: 自分のロックは無言で通す", SelfPasses);
        harness.Add("ゲート: 所有者不明のロックは注意して通す", UnknownOwnerWarns);
        harness.Add("ゲート: 他人のロックは止める（Enforce）", OtherBlocksUnderEnforce);
        harness.Add("ゲート: 止めた文言に相手の名前が入る", BlockMessageNamesOwner);
        harness.Add("ゲート: 他人のロックでも WarnOnly なら通す", OtherPassesUnderWarnOnly);
        harness.Add("ゲート: ロックが無ければ取得へ進む", NoLockGoesToAcquire);
        harness.Add("ゲート: 匿名ではロックを取りに行かない", AnonymousNeverAcquires);

        // ── 取得後の判定 ────────────────────────────────────
        harness.Add("取得後: 取れたら通す", AcquiredPasses);
        harness.Add("取得後: もともと自分のものでも通す", AlreadyMinePasses);
        harness.Add("取得後: 割り込まれたら止める（Enforce）", AcquireHeldByOtherBlocks);
        harness.Add("取得後: 割り込まれても WarnOnly なら通す", AcquireHeldByOtherWarnOnly);
        harness.Add("取得後: 所有者不明は止めない", AcquireHeldByUnknownWarns);
        harness.Add("取得後: 取得失敗は止める（Enforce）", AcquireFailedBlocks);
        harness.Add("取得後: 取得失敗でも WarnOnly なら通す", AcquireFailedWarnOnly);

        // ── 送信ゲート ──────────────────────────────────────
        harness.Add("送信: 他人のロックが無ければ通す", SubmitPassesWhenClean);
        harness.Add("送信: 他人のロックがあれば止める", SubmitBlocksOnOtherLocks);
        harness.Add("送信: 止めた文言に全件が並ぶ", SubmitBlockListsEveryFile);
        harness.Add("送信: 所有者不明は止めない", SubmitIgnoresUnknownOwner);
        harness.Add("送信: サーバに繋がらなければ通す", SubmitPassesWhenUnreachable);
        harness.Add("送信: 匿名なら止めずに注意する", SubmitWarnsWhenAnonymous);
        harness.Add("送信: WarnOnly なら止めない", SubmitPassesUnderWarnOnly);
        harness.Add("送信: 対象が空でも落ちない", SubmitWithoutPaths);

        // ── 設定 ────────────────────────────────────────────
        harness.Add("設定: 既定は Enforce", SettingsDefaultIsEnforce);
        harness.Add("設定: warn_only を読める", SettingsReadsWarnOnly);
        harness.Add("設定: 未知の値は Enforce へ倒す", SettingsUnknownFallsBack);
        harness.Add("設定: 書いて読み直せる", SettingsRoundTrip);

        // ── 自動ロックの台帳 ────────────────────────────────
        harness.Add("台帳: 追加・照会・削除", LedgerBasics);
        harness.Add("台帳: 大文字小文字を区別しない", LedgerIsCaseInsensitive);
        harness.Add("台帳: TakeAll で空になる", LedgerTakeAllEmpties);
        harness.Add("台帳: 空パスは記録しない", LedgerIgnoresEmpty);
    }

    // ============================================================
    //  保存ゲートの判定表
    // ============================================================

    /// <summary>バージョン管理下に無ければ何も言わずに通す。</summary>
    private static void UnavailablePasses()
    {
        var verdict = LockGatePolicy.DecideForWrite(Situation(
            available: false, reachable: true, signedIn: true, holder: LockHolder.Other));

        Check.Equal(LockGateAction.Allow, verdict.Action, "行動");
        Check.Equal(LockGateReason.VersionControlUnavailable, verdict.Reason, "理由");
        Check.True(!verdict.HasMessage, "利用者へは何も出さないはず");
    }

    /// <summary>
    /// サーバに繋がらないときは止めない。
    /// 「確かめられないから止める」にすると、サーバが落ちている間じゅう
    /// 誰も保存できなくなる。
    /// </summary>
    private static void UnreachablePassesWithWarning()
    {
        var verdict = LockGatePolicy.DecideForWrite(Situation(
            available: true, reachable: false, signedIn: true, holder: LockHolder.None));

        Check.Equal(LockGateAction.AllowWithWarning, verdict.Action, "行動");
        Check.Equal(LockGateReason.ServerUnreachable, verdict.Reason, "理由");
        Check.True(verdict.CanProceed, "止めてはいけない");
    }

    /// <summary>匿名でロックも無ければ、何も言うことが無い。</summary>
    private static void AnonymousWithoutLockPasses()
    {
        var verdict = LockGatePolicy.DecideForWrite(Situation(
            available: true, reachable: true, signedIn: false, holder: LockHolder.None));

        Check.Equal(LockGateAction.Allow, verdict.Action, "行動");
        Check.Equal(LockGateReason.NoLock, verdict.Reason, "理由");
    }

    /// <summary>
    /// 匿名で誰かがロックしているときは、止めずに事実だけ伝える。
    /// 匿名では「自分のロック」が成立しないため、他人のものと断定できない。
    /// </summary>
    private static void AnonymousWithLockWarns()
    {
        var verdict = LockGatePolicy.DecideForWrite(Situation(
            available: true, reachable: true, signedIn: false,
            holder: LockHolder.Other, owner: OTHER_OWNER));

        Check.Equal(LockGateAction.AllowWithWarning, verdict.Action, "行動");
        Check.Equal(LockGateReason.NotSignedIn, verdict.Reason, "理由");
    }

    /// <summary>自分のロックは最も多い経路。何も出さずに通す。</summary>
    private static void SelfPasses()
    {
        var verdict = LockGatePolicy.DecideForWrite(Situation(
            available: true, reachable: true, signedIn: true, holder: LockHolder.Self));

        Check.Equal(LockGateAction.Allow, verdict.Action, "行動");
        Check.Equal(LockGateReason.HeldBySelf, verdict.Reason, "理由");
        Check.True(!verdict.HasMessage, "毎回の保存で何か出しては邪魔になる");
    }

    /// <summary>
    /// 所有者不明のロックでは止めない。
    /// 誰も解除できない（解除ボタンは自分のロックにしか出ない）ため、
    /// 止めると永久に保存できないファイルが生まれる。
    /// </summary>
    private static void UnknownOwnerWarns()
    {
        var verdict = LockGatePolicy.DecideForWrite(Situation(
            available: true, reachable: true, signedIn: true, holder: LockHolder.Unknown));

        Check.Equal(LockGateAction.AllowWithWarning, verdict.Action, "行動");
        Check.Equal(LockGateReason.HeldByUnknownOwner, verdict.Reason, "理由");
        Check.True(verdict.CanProceed, "止めてはいけない");
    }

    /// <summary>他の人のロックは既定（Enforce）で止める ── この機能の芯。</summary>
    private static void OtherBlocksUnderEnforce()
    {
        var verdict = LockGatePolicy.DecideForWrite(Situation(
            available: true, reachable: true, signedIn: true,
            holder: LockHolder.Other, owner: OTHER_OWNER));

        Check.Equal(LockGateAction.Block, verdict.Action, "行動");
        Check.Equal(LockGateReason.HeldByOther, verdict.Reason, "理由");
        Check.True(!verdict.CanProceed, "書き込ませてはいけない");
    }

    /// <summary>
    /// 止めるときは「誰が」を必ず出す。名前が無いと利用者は次の手を選べない。
    /// </summary>
    private static void BlockMessageNamesOwner()
    {
        var verdict = LockGatePolicy.DecideForWrite(Situation(
            available: true, reachable: true, signedIn: true,
            holder: LockHolder.Other, owner: OTHER_OWNER));

        Check.True(verdict.Message.Contains(OTHER_OWNER, StringComparison.Ordinal),
                   $"止めた文言に所有者名が無い: {verdict.Message}");
        Check.True(verdict.Message.Contains(TARGET_PATH, StringComparison.Ordinal),
                   $"止めた文言に対象パスが無い: {verdict.Message}");
    }

    /// <summary>方針を「注意のみ」にすると、他の人のロックでも通る。</summary>
    private static void OtherPassesUnderWarnOnly()
    {
        var verdict = LockGatePolicy.DecideForWrite(Situation(
            available: true, reachable: true, signedIn: true,
            holder: LockHolder.Other, owner: OTHER_OWNER,
            policy: LockEnforcementPolicy.WarnOnly));

        Check.Equal(LockGateAction.AllowWithWarning, verdict.Action, "行動");
        Check.Equal(LockGateReason.WarnOnlyPolicy, verdict.Reason, "理由");
    }

    /// <summary>誰も持っていなければ、保存する以上はその場で取りに行く。</summary>
    private static void NoLockGoesToAcquire()
    {
        var verdict = LockGatePolicy.DecideForWrite(Situation(
            available: true, reachable: true, signedIn: true, holder: LockHolder.None));

        Check.Equal(LockGateAction.AcquireThenDecide, verdict.Action, "行動");
    }

    /// <summary>
    /// 匿名では取りに行かない。匿名で取ると所有者 &lt;unknown&gt; のロックができ、
    /// 誰にも解除できない状態を自分で作ってしまう。
    /// </summary>
    private static void AnonymousNeverAcquires()
    {
        var verdict = LockGatePolicy.DecideForWrite(Situation(
            available: true, reachable: true, signedIn: false, holder: LockHolder.None));

        Check.True(verdict.Action != LockGateAction.AcquireThenDecide,
                   "匿名でロックを取りに行ってはいけない");
    }

    // ============================================================
    //  取得後の判定
    // ============================================================

    /// <summary>取れたら通す。</summary>
    private static void AcquiredPasses()
    {
        var verdict = LockGatePolicy.DecideAfterAcquire(
            LockAcquireOutcome.Acquired, null, LockEnforcementPolicy.Enforce, TARGET_PATH);

        Check.Equal(LockGateAction.Allow, verdict.Action, "行動");
        Check.Equal(LockGateReason.Acquired, verdict.Reason, "理由");
    }

    /// <summary>もともと自分が持っていた場合も通す（何も変わっていない）。</summary>
    private static void AlreadyMinePasses()
    {
        var verdict = LockGatePolicy.DecideAfterAcquire(
            LockAcquireOutcome.AlreadyMine, null, LockEnforcementPolicy.Enforce, TARGET_PATH);

        Check.Equal(LockGateAction.Allow, verdict.Action, "行動");
        Check.Equal(LockGateReason.HeldBySelf, verdict.Reason, "理由");
    }

    /// <summary>照会と取得のあいだに割り込まれたら止める。</summary>
    private static void AcquireHeldByOtherBlocks()
    {
        var verdict = LockGatePolicy.DecideAfterAcquire(
            LockAcquireOutcome.HeldByOther, OTHER_OWNER,
            LockEnforcementPolicy.Enforce, TARGET_PATH);

        Check.Equal(LockGateAction.Block, verdict.Action, "行動");
        Check.True(verdict.Message.Contains(OTHER_OWNER, StringComparison.Ordinal),
                   "止めた文言に所有者名が要る");
    }

    /// <summary>割り込まれても方針が「注意のみ」なら通す。</summary>
    private static void AcquireHeldByOtherWarnOnly()
    {
        var verdict = LockGatePolicy.DecideAfterAcquire(
            LockAcquireOutcome.HeldByOther, OTHER_OWNER,
            LockEnforcementPolicy.WarnOnly, TARGET_PATH);

        Check.Equal(LockGateAction.AllowWithWarning, verdict.Action, "行動");
    }

    /// <summary>所有者不明は取得後も止めない（判定表 6 行目と同じ扱い）。</summary>
    private static void AcquireHeldByUnknownWarns()
    {
        var verdict = LockGatePolicy.DecideAfterAcquire(
            LockAcquireOutcome.HeldByUnknown, LockInfo.UNKNOWN_OWNER,
            LockEnforcementPolicy.Enforce, TARGET_PATH);

        Check.Equal(LockGateAction.AllowWithWarning, verdict.Action, "行動");
        Check.Equal(LockGateReason.HeldByUnknownOwner, verdict.Reason, "理由");
    }

    /// <summary>
    /// 取得そのものが失敗したときは止める。
    /// 「確かめられない」ではなく「取れていない」なので、編集させてはいけない。
    /// </summary>
    private static void AcquireFailedBlocks()
    {
        var verdict = LockGatePolicy.DecideAfterAcquire(
            LockAcquireOutcome.Failed, null, LockEnforcementPolicy.Enforce, TARGET_PATH);

        Check.Equal(LockGateAction.Block, verdict.Action, "行動");
        Check.Equal(LockGateReason.AcquireFailed, verdict.Reason, "理由");
    }

    /// <summary>取得失敗でも「注意のみ」なら通す（逃げ道として機能すること）。</summary>
    private static void AcquireFailedWarnOnly()
    {
        var verdict = LockGatePolicy.DecideAfterAcquire(
            LockAcquireOutcome.Failed, null, LockEnforcementPolicy.WarnOnly, TARGET_PATH);

        Check.True(verdict.CanProceed, "WarnOnly では止めない");
        Check.Equal(LockGateReason.AcquireFailed, verdict.Reason, "理由");
    }

    // ============================================================
    //  送信ゲート
    // ============================================================

    /// <summary>自分のロックしか無ければ送れる。</summary>
    private static void SubmitPassesWhenClean()
    {
        var verdict = LockGatePolicy.DecideForSubmit(
            new[] { Lock("a.scene", LockHolder.Self), Lock("b.actor", LockHolder.None) },
            isVersionControlAvailable: true, isServerReachable: true,
            isSignedIn: true, policy: LockEnforcementPolicy.Enforce);

        Check.Equal(LockGateAction.Allow, verdict.Action, "行動");
        Check.Equal(0, verdict.BlockingLocks.Count, "止める対象の件数");
    }

    /// <summary>他の人のロックが 1 件でもあれば送信を止める。</summary>
    private static void SubmitBlocksOnOtherLocks()
    {
        var verdict = LockGatePolicy.DecideForSubmit(
            new[] { Lock("a.scene", LockHolder.Self), Lock("b.actor", LockHolder.Other, OTHER_OWNER) },
            isVersionControlAvailable: true, isServerReachable: true,
            isSignedIn: true, policy: LockEnforcementPolicy.Enforce);

        Check.Equal(LockGateAction.Block, verdict.Action, "行動");
        Check.Equal(1, verdict.BlockingLocks.Count, "止める対象の件数");
        Check.Equal("b.actor", verdict.BlockingLocks[0].Path, "止める対象のパス");
    }

    /// <summary>
    /// 止めるときは全件を並べる。1 件だけ直しても次でまた止まるのでは、
    /// 利用者は何回もやり直すことになる。
    /// </summary>
    private static void SubmitBlockListsEveryFile()
    {
        var verdict = LockGatePolicy.DecideForSubmit(
            new[]
            {
                Lock("a.scene",  LockHolder.Other, OTHER_OWNER),
                Lock("b.actor",  LockHolder.Other, "taro"),
                Lock("c.anim",   LockHolder.Self),
            },
            isVersionControlAvailable: true, isServerReachable: true,
            isSignedIn: true, policy: LockEnforcementPolicy.Enforce);

        Check.Equal(2, verdict.BlockingLocks.Count, "止める対象の件数");
        Check.True(verdict.Message.Contains("a.scene", StringComparison.Ordinal), "1 件目のパス");
        Check.True(verdict.Message.Contains("b.actor", StringComparison.Ordinal), "2 件目のパス");
        Check.True(verdict.Message.Contains("taro", StringComparison.Ordinal), "2 件目の所有者名");
        Check.True(!verdict.Message.Contains("c.anim", StringComparison.Ordinal),
                   "自分のロックは止める理由に並べない");
    }

    /// <summary>所有者不明のロックでは送信を止めない（保存ゲートと同じ理由）。</summary>
    private static void SubmitIgnoresUnknownOwner()
    {
        var verdict = LockGatePolicy.DecideForSubmit(
            new[] { Lock("a.scene", LockHolder.Unknown, LockInfo.UNKNOWN_OWNER) },
            isVersionControlAvailable: true, isServerReachable: true,
            isSignedIn: true, policy: LockEnforcementPolicy.Enforce);

        Check.True(verdict.CanProceed, "所有者不明で送信を止めてはいけない");
    }

    /// <summary>サーバに繋がらないときは確かめられないので通す。</summary>
    private static void SubmitPassesWhenUnreachable()
    {
        var verdict = LockGatePolicy.DecideForSubmit(
            locks: null,
            isVersionControlAvailable: true, isServerReachable: false,
            isSignedIn: true, policy: LockEnforcementPolicy.Enforce);

        Check.Equal(LockGateAction.AllowWithWarning, verdict.Action, "行動");
        Check.Equal(LockGateReason.ServerUnreachable, verdict.Reason, "理由");
    }

    /// <summary>匿名では持ち主を断定できないので止めない。</summary>
    private static void SubmitWarnsWhenAnonymous()
    {
        var verdict = LockGatePolicy.DecideForSubmit(
            new[] { Lock("a.scene", LockHolder.Other, OTHER_OWNER) },
            isVersionControlAvailable: true, isServerReachable: true,
            isSignedIn: false, policy: LockEnforcementPolicy.Enforce);

        Check.Equal(LockGateAction.AllowWithWarning, verdict.Action, "行動");
        Check.Equal(LockGateReason.NotSignedIn, verdict.Reason, "理由");
    }

    /// <summary>方針が「注意のみ」なら送信も止めない。</summary>
    private static void SubmitPassesUnderWarnOnly()
    {
        var verdict = LockGatePolicy.DecideForSubmit(
            new[] { Lock("a.scene", LockHolder.Other, OTHER_OWNER) },
            isVersionControlAvailable: true, isServerReachable: true,
            isSignedIn: true, policy: LockEnforcementPolicy.WarnOnly);

        Check.True(verdict.CanProceed, "WarnOnly では止めない");
        Check.Equal(1, verdict.BlockingLocks.Count, "内訳は残す（注意に使う）");
    }

    /// <summary>対象が無い（送るものが無い）場合でも落ちない。</summary>
    private static void SubmitWithoutPaths()
    {
        var verdict = LockGatePolicy.DecideForSubmit(
            new List<LockInfo>(),
            isVersionControlAvailable: true, isServerReachable: true,
            isSignedIn: true, policy: LockEnforcementPolicy.Enforce);

        Check.Equal(LockGateAction.Allow, verdict.Action, "行動");
    }

    // ============================================================
    //  設定
    // ============================================================

    /// <summary>設定が無いときは「止める」で始まる（安全側ではなく、機能する側）。</summary>
    private static void SettingsDefaultIsEnforce()
    {
        Check.Equal(LockEnforcementPolicy.Enforce, new LockGateSettings().Policy, "既定の方針");
        Check.True(new LockGateSettings().AutoLockOpenedDocuments, "自動ロックの既定");
    }

    /// <summary>設定ファイルに書いた "warn_only" を読める。</summary>
    private static void SettingsReadsWarnOnly()
    {
        var settings = new LockGateSettings
        {
            Enforcement = LockGateSettings.POLICY_VALUE_WARN_ONLY,
        };
        Check.Equal(LockEnforcementPolicy.WarnOnly, settings.Policy, "方針");
    }

    /// <summary>綴り間違いや古い値は既定へ倒す（黙って強制が外れるのを防ぐ）。</summary>
    private static void SettingsUnknownFallsBack()
    {
        var settings = new LockGateSettings { Enforcement = "そのうち決める" };
        Check.Equal(LockEnforcementPolicy.Enforce, settings.Policy, "未知の値の扱い");
    }

    /// <summary>実ファイルへ書いて読み直せる（設定として使えること）。</summary>
    private static void SettingsRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seed_lock_gate_" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = new LockGateSettings
            {
                Enforcement             = LockGateSettings.POLICY_VALUE_WARN_ONLY,
                AutoLockOpenedDocuments = false,
            };
            Check.True(written.Save(dir), "設定を書けるはず");

            var read = LockGateSettings.Load(dir);
            Check.Equal(LockEnforcementPolicy.WarnOnly, read.Policy, "読み直した方針");
            Check.True(!read.AutoLockOpenedDocuments, "読み直した自動ロックの可否");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 後始末の失敗は無視 */ }
        }
    }

    // ============================================================
    //  自動ロックの台帳
    // ============================================================

    /// <summary>追加・照会・削除という最低限の振る舞い。</summary>
    private static void LedgerBasics()
    {
        var ledger = new AutoLockLedger();

        Check.True(ledger.Add(TARGET_PATH), "最初の追加は真");
        Check.True(!ledger.Add(TARGET_PATH), "2 回目の追加は偽");
        Check.True(ledger.Contains(TARGET_PATH), "入っているはず");
        Check.Equal(1, ledger.Count, "件数");

        Check.True(ledger.Remove(TARGET_PATH), "削除は真");
        Check.True(!ledger.Contains(TARGET_PATH), "消えているはず");
    }

    /// <summary>Windows の作業コピーが前提なので大文字小文字は同じものとして扱う。</summary>
    private static void LedgerIsCaseInsensitive()
    {
        var ledger = new AutoLockLedger();
        ledger.Add("Assets/Scenes/Prologue.scene");

        Check.True(ledger.Contains("assets/scenes/prologue.scene"),
                   "大文字小文字が違っても同じロックとして扱うはず");
    }

    /// <summary>まとめて解放するときに台帳が空になること。</summary>
    private static void LedgerTakeAllEmpties()
    {
        var ledger = new AutoLockLedger();
        ledger.Add("a.scene");
        ledger.Add("b.actor");

        var taken = ledger.TakeAll();
        Check.Equal(2, taken.Count, "取り出した件数");
        Check.Equal(0, ledger.Count, "取り出したあとは空");
    }

    /// <summary>空パスを記録すると、解放時に意味の無い呼び出しが増える。</summary>
    private static void LedgerIgnoresEmpty()
    {
        var ledger = new AutoLockLedger();
        Check.True(!ledger.Add(null),       "null は記録しない");
        Check.True(!ledger.Add(string.Empty), "空文字は記録しない");
        Check.True(!ledger.Add("   "),      "空白だけは記録しない");
        Check.Equal(0, ledger.Count, "件数");
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>判定に渡す状況を組み立てる（既定は「ログイン中・繋がる・Enforce」）。</summary>
    /// <param name="available">バージョン管理下にあるか。</param>
    /// <param name="reachable">サーバへ届くか。</param>
    /// <param name="signedIn">ログイン中か。</param>
    /// <param name="holder">保持者。</param>
    /// <param name="owner">保持者名。</param>
    /// <param name="policy">方針。</param>
    private static LockGateSituation Situation(
        bool available, bool reachable, bool signedIn, LockHolder holder,
        string owner = "", LockEnforcementPolicy policy = LockEnforcementPolicy.Enforce)
        => new(available, reachable, signedIn, holder, owner, policy, TARGET_PATH);

    /// <summary>テスト用のロック情報を作る。</summary>
    /// <param name="path">パス。</param>
    /// <param name="holder">保持者。</param>
    /// <param name="owner">所有者名。</param>
    private static LockInfo Lock(string path, LockHolder holder, string owner = "")
        => new(path, owner, holder);
}
