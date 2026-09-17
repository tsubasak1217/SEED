// ============================================================
//  PanelStateTests.cs — Version Control パネルのビューモデル / 状態機械のテスト
//
//  【なぜここを固定するのか】
//  ボタンの有効条件と結果の見せ方は分岐が多いのに、**間違えてもビルドが通る**。
//  そして間違いは GUI を起動しないと見えない（このリポジトリでは
//  実機での GUI 確認が自動化されていない）。
//  そこでビューから切り離した素のクラスにしてあるので、ここで全分岐を踏む。
//
//  【特に守りたいこと】
//   ・メッセージが空・競合が残っている間は「送信」を押せないこと
//   ・NeedsSync のとき「最新を取得」が強調されること
//   ・中断（Canceled）で赤いエラーを出さないこと
//   ・競合が必ず一覧の最上部に来ること
//   ・所有者不明のロックを「自分のもの」と決めつけず、解除も出さないこと
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Presentation;
using SpriteRigTests;

namespace SEEDEditor.Tests.VersionControl;

/// <summary>
/// パネルのビューモデル / 状態機械のテスト群。
/// </summary>
public static class PanelStateTests
{
    /// <summary>テストをランナーへ登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        // ── 結果 → 1 行メッセージ ──
        harness.Add("成功は成功色で結果の文言をそのまま出す",              NoticeSuccessKeepsMessage);
        harness.Add("NothingToDo は失敗ではなく情報として出す",            NoticeNothingToDoIsInfo);
        harness.Add("NeedsSync は注意色で「最新を取得」を強調する",        NoticeNeedsSyncEmphasizesFetch);
        harness.Add("RequiresConnection は短い言い回しに差し替える",       NoticeRequiresConnectionIsShort);
        harness.Add("競合は失敗ではなく注意として出す",                    NoticeConflictedIsWarning);
        harness.Add("中断は何も表示しない（赤いエラーを出さない）",        NoticeCanceledIsSilent);
        harness.Add("失敗は失敗色で出す",                                  NoticeFailedIsError);

        // ── ボタンの有効条件 ──
        harness.Add("利用不可なら主操作はすべて押せない",                  ButtonsDisabledWhenUnavailable);
        harness.Add("メッセージが空なら送信は押せない",                    SubmitDisabledWithoutMessage);
        harness.Add("空白だけのメッセージも空と同じ扱い",                  SubmitDisabledWithBlankMessage);
        harness.Add("未解決の競合が残っていると送信は押せない",            SubmitDisabledWhileConflicted);
        harness.Add("実行中は主操作をすべて無効にする",                    ButtonsDisabledWhileBusy);
        harness.Add("押せない理由が必ず 1 つ返る",                        SubmitBlockedReasonIsExplained);
        harness.Add("競合が無ければ 2 択ボタンは出さない",                 ResolveDisabledWithoutConflicts);

        // ── 実行中の表示 ──
        harness.Add("実行中は操作名つきの表示になる",                      BusyTextShowsOperation);
        harness.Add("実行中に別の操作は始められない",                      BeginOperationIsExclusive);
        harness.Add("操作を始めると前回の結果表示は消える",                BeginOperationClearsNotice);
        harness.Add("操作が終われば待機に戻る",                            EndOperationReturnsToIdle);

        // ── 変更一覧のグループ分け ──
        harness.Add("競合は必ず一覧の最上部のグループになる",              ConflictsGroupComesFirst);
        harness.Add("競合が無ければ競合グループを作らない",                NoConflictGroupWhenClean);
        harness.Add("解決済みの競合は通常の変更として並ぶ",                ResolvedConflictIsNotInConflictGroup);
        harness.Add("変更が無ければグループは空になる",                    EmptyStatusHasNoGroups);
        harness.Add("一覧はパス順に固定される",                            ChangesAreSortedByPath);

        // ── 表示名 ──
        harness.Add("変更の種類が日本語の表示名になる",                    ChangeKindHasDisplayName);
        harness.Add("未解決の競合は種類より「競合」を優先して出す",        ConflictOverridesChangeKind);
        harness.Add("変更の種類ごとに別のアイコンキーを返す",              ChangeKindHasDistinctIcons);
        harness.Add("所有者不明のロックは「不明」と出し解除させない",      UnknownLockIsShownAsUnknown);
        harness.Add("自分のロックだけ解除できる",                          OnlySelfLockCanBeReleased);
        harness.Add("日時が無ければ「日時不明」と出す",                    MissingTimestampIsLabeled);
        harness.Add("メッセージが空のリビジョンにも何か書く",              EmptyRevisionMessageIsLabeled);
        harness.Add("identity とリモート URL が 1 行にまとまる",           ConnectionTextIsFormatted);
        harness.Add("identity が無ければ「利用者不明」と出す",             UnknownIdentityIsLabeled);
        harness.Add("ログイン中は identity に「（ログイン中）」が付く",     SignedInIdentityIsLabeled);
    }

    // ============================================================
    //  結果 → 1 行メッセージ
    // ============================================================

    /// <summary>成功は成功色で、結果が持つ文言をそのまま出す。</summary>
    private static void NoticeSuccessKeepsMessage()
    {
        var notice = VersionControlNotice.FromResult(VersionControlResult.Success("送信しました。"));

        Check.Equal(VersionControlNoticeSeverity.Success, notice.Severity, "重大度");
        Check.Equal("送信しました。", notice.Text, "文言");
        Check.True(!notice.EmphasizeFetchLatest, "強調しない");
    }

    /// <summary>「送るものが無い」は失敗ではない。赤で出すと利用者が不安になる。</summary>
    private static void NoticeNothingToDoIsInfo()
    {
        var notice = VersionControlNotice.FromResult(
            VersionControlResult.NothingToDo(VersionControlMessages.SUBMIT_NOTHING));

        Check.Equal(VersionControlNoticeSeverity.Info, notice.Severity, "重大度");
        Check.Equal(VersionControlMessages.SUBMIT_NOTHING, notice.Text, "文言");
    }

    /// <summary>
    /// NeedsSync は「次に何をすればよいか」が唯一の重要情報。
    /// 短く言い切り、対応するボタンを強調する。
    /// </summary>
    private static void NoticeNeedsSyncEmphasizesFetch()
    {
        var notice = VersionControlNotice.FromResult(
            VersionControlResult.Create(
                VersionControlOutcome.NeedsSync, VersionControlMessages.SUBMIT_NEEDS_SYNC));

        Check.Equal(VersionControlNoticeSeverity.Warning, notice.Severity, "重大度");
        Check.Equal(VersionControlMessages.PANEL_NOTICE_NEEDS_SYNC, notice.Text, "文言");
        Check.True(notice.EmphasizeFetchLatest, "「最新を取得」を強調する");
    }

    /// <summary>サーバへ繋がらないときは短く言い切る（履歴以外でも出るため）。</summary>
    private static void NoticeRequiresConnectionIsShort()
    {
        var notice = VersionControlNotice.FromResult(
            VersionControlResult.Create(
                VersionControlOutcome.RequiresConnection,
                VersionControlMessages.REQUIRES_CONNECTION));

        Check.Equal(VersionControlNoticeSeverity.Error, notice.Severity, "重大度");
        Check.Equal(VersionControlMessages.PANEL_NOTICE_REQUIRES_CONNECTION, notice.Text, "文言");
    }

    /// <summary>競合は「選んでください」であって失敗ではない。</summary>
    private static void NoticeConflictedIsWarning()
    {
        var notice = VersionControlNotice.FromResult(
            VersionControlResult.Create(VersionControlOutcome.Conflicted, "競合しています。"));

        Check.Equal(VersionControlNoticeSeverity.Warning, notice.Severity, "重大度");
    }

    /// <summary>
    /// 中断は利用者が原因を知りたい出来事ではない。何も出さない。
    /// （タイムアウトでも Canceled が返るため、ここを赤にすると
    ///   「エディタが壊れた」と思わせてしまう）
    /// </summary>
    private static void NoticeCanceledIsSilent()
    {
        var notice = VersionControlNotice.FromResult(
            VersionControlResult.Create(
                VersionControlOutcome.Canceled, VersionControlMessages.CANCELED));

        Check.Equal(VersionControlNoticeSeverity.None, notice.Severity, "重大度");
        Check.True(!notice.HasText, "何も表示しない");
    }

    /// <summary>純粋な失敗は失敗色で出す。</summary>
    private static void NoticeFailedIsError()
    {
        var notice = VersionControlNotice.FromResult(
            VersionControlResult.Failed(VersionControlMessages.SUBMIT_FAILED));

        Check.Equal(VersionControlNoticeSeverity.Error, notice.Severity, "重大度");
        Check.Equal(VersionControlMessages.SUBMIT_FAILED, notice.Text, "文言");
    }

    // ============================================================
    //  ボタンの有効条件
    // ============================================================

    /// <summary>バージョン管理が無いプロジェクトでは操作させない。</summary>
    private static void ButtonsDisabledWhenUnavailable()
    {
        var state = new VersionControlPanelState();
        state.SetAvailability(false);
        state.SetCommitMessage("なにか");

        Check.True(!state.CanSubmit,        "送信");
        Check.True(!state.CanFetchLatest,   "最新を取得");
        Check.True(!state.CanRefresh,       "更新");
        Check.True(!state.CanChangeBranch,  "ブランチ");
        Check.True(!state.CanResolveConflicts, "競合の解決");
        Check.True(!state.CanChangeLocks,   "ロック");
    }

    /// <summary>メッセージが無いまま送らせない（Lore 側でも必ず弾かれる）。</summary>
    private static void SubmitDisabledWithoutMessage()
    {
        var state = NewAvailableState();
        Check.True(!state.CanSubmit, "空のときは押せない");

        state.SetCommitMessage("テクスチャを差し替え");
        Check.True(state.CanSubmit, "入力したら押せる");

        state.ClearCommitMessage();
        Check.True(!state.CanSubmit, "消したらまた押せない");
    }

    /// <summary>空白だけのメッセージは「入力済み」と見なさない。</summary>
    private static void SubmitDisabledWithBlankMessage()
    {
        var state = NewAvailableState();
        state.SetCommitMessage("   \t  ");
        Check.True(!state.CanSubmit, "空白だけでは押せない");
    }

    /// <summary>
    /// 未解決の競合があるうちは送信できない（プロバイダが Conflicted で止める）。
    /// 押せてしまうと「押したのに何も起きない」体験になる。
    /// </summary>
    private static void SubmitDisabledWhileConflicted()
    {
        var state = NewAvailableState();
        state.SetCommitMessage("送りたい");
        state.ApplyStatus(NewStatus(
            NewChange("a.txt", FileChangeKind.Modified, FileConflictState.Unresolved)));

        Check.True(state.HasUnresolvedConflicts, "競合を検知している");
        Check.True(!state.CanSubmit, "送信は押せない");
        Check.True(state.CanResolveConflicts, "解決の 2 択は押せる");
        Check.True(state.CanFetchLatest, "「最新を取得」は押せる");
    }

    /// <summary>実行中は主操作をすべて無効にする（多重実行を入口で止める）。</summary>
    private static void ButtonsDisabledWhileBusy()
    {
        var state = NewAvailableState();
        state.SetCommitMessage("送りたい");
        Check.True(state.CanSubmit, "開始前は押せる");

        state.BeginOperation(VersionControlOperation.Submit);

        Check.True(state.IsBusy, "実行中");
        Check.True(state.ShowProgress, "プログレスを出す");
        Check.True(!state.CanSubmit,       "送信");
        Check.True(!state.CanFetchLatest,  "最新を取得");
        Check.True(!state.CanRefresh,      "更新");
        Check.True(!state.CanChangeBranch, "ブランチ");
    }

    /// <summary>
    /// 押せないボタンには必ず理由が付く。
    /// 理由の無い無効ボタンは利用者にとって故障と区別が付かない。
    /// </summary>
    private static void SubmitBlockedReasonIsExplained()
    {
        var unavailable = new VersionControlPanelState();
        unavailable.SetAvailability(false);
        Check.Equal(VersionControlMessages.UNAVAILABLE,
                    unavailable.SubmitBlockedReason, "利用不可の理由");

        var noMessage = NewAvailableState();
        Check.Equal(VersionControlMessages.SUBMIT_MESSAGE_REQUIRED,
                    noMessage.SubmitBlockedReason, "メッセージ未入力の理由");

        var conflicted = NewAvailableState();
        conflicted.SetCommitMessage("送りたい");
        conflicted.ApplyStatus(NewStatus(
            NewChange("a.txt", FileChangeKind.Modified, FileConflictState.Unresolved)));
        Check.Equal(VersionControlMessages.SUBMIT_BLOCKED_BY_CONFLICTS,
                    conflicted.SubmitBlockedReason, "競合の理由");

        var ready = NewAvailableState();
        ready.SetCommitMessage("送りたい");
        Check.Equal(string.Empty, ready.SubmitBlockedReason, "押せるときは理由なし");
    }

    /// <summary>競合が無ければ 2 択ボタンは押させない。</summary>
    private static void ResolveDisabledWithoutConflicts()
    {
        var state = NewAvailableState();
        state.ApplyStatus(NewStatus(NewChange("a.txt", FileChangeKind.Modified)));

        Check.True(!state.CanResolveConflicts, "解決は押せない");
        Check.Equal(0, state.UnresolvedConflictCount, "競合件数");
    }

    // ============================================================
    //  実行中の表示
    // ============================================================

    /// <summary>実行中は「何をしているか」が分かる文言を出す。</summary>
    private static void BusyTextShowsOperation()
    {
        var state = NewAvailableState();
        Check.Equal(string.Empty, state.BusyText, "待機中は空");

        state.BeginOperation(VersionControlOperation.FetchLatest);
        Check.Equal(
            string.Format(VersionControlMessages.PANEL_BUSY_FORMAT,
                          VersionControlMessages.PANEL_OPERATION_FETCH),
            state.BusyText, "実行中の文言");
    }

    /// <summary>
    /// 実行中に別の操作を始めさせない。
    /// プロバイダ側も直列化しているが、押すたびにキューが伸びると
    /// 「反応が無い」状態になるので入口で止める。
    /// </summary>
    private static void BeginOperationIsExclusive()
    {
        var state = NewAvailableState();

        Check.True(state.BeginOperation(VersionControlOperation.Submit), "1 回目は開始できる");
        Check.True(!state.BeginOperation(VersionControlOperation.FetchLatest),
                   "実行中は開始できない");
        Check.Equal(VersionControlOperation.Submit, state.RunningOperation, "実行中の操作");

        // None は操作ではないので開始できない。
        var idle = NewAvailableState();
        Check.True(!idle.BeginOperation(VersionControlOperation.None), "None は開始できない");
    }

    /// <summary>実行中に前回の成功メッセージが残っていると紛らわしい。</summary>
    private static void BeginOperationClearsNotice()
    {
        var state = NewAvailableState();
        state.EndOperation(VersionControlResult.Success("前回の成功"));
        Check.True(state.Notice.HasText, "前回の結果が出ている");

        state.BeginOperation(VersionControlOperation.Refresh);
        Check.True(!state.Notice.HasText, "開始時に消える");
    }

    /// <summary>操作が終われば待機に戻り、結果が表示される。</summary>
    private static void EndOperationReturnsToIdle()
    {
        var state = NewAvailableState();
        state.BeginOperation(VersionControlOperation.Submit);
        state.EndOperation(VersionControlResult.Success("送信しました。"));

        Check.True(!state.IsBusy, "待機に戻る");
        Check.Equal(VersionControlOperation.None, state.RunningOperation, "実行中の操作");
        Check.Equal(VersionControlNoticeSeverity.Success, state.Notice.Severity, "結果の重大度");
    }

    // ============================================================
    //  変更一覧のグループ分け
    // ============================================================

    /// <summary>
    /// 競合は必ず最上部。100 件の変更に紛れると、利用者は
    /// 「送信できない理由」に辿り着けない。
    /// </summary>
    private static void ConflictsGroupComesFirst()
    {
        var status = NewStatus(
            NewChange("z.txt", FileChangeKind.Modified),
            NewChange("a.txt", FileChangeKind.Modified, FileConflictState.Unresolved),
            NewChange("b.txt", FileChangeKind.Added));

        var groups = ChangeListBuilder.Build(status);

        Check.Equal(2, groups.Count, "グループ数");
        Check.Equal(ChangeGroupKind.Conflicts, groups[0].Kind, "1 つ目は競合");
        Check.True(groups[0].IsConflictGroup, "競合グループの判定");
        Check.Equal(1, groups[0].Items.Count, "競合の件数");
        Check.Equal("a.txt", groups[0].Items[0].Path, "競合のパス");
        Check.Equal(ChangeGroupKind.Changes, groups[1].Kind, "2 つ目は変更");
        Check.Equal(2, groups[1].Items.Count, "変更の件数");

        // 見出しには件数が入る（利用者が総数を把握できるように）。
        Check.Equal(string.Format(VersionControlMessages.PANEL_GROUP_CONFLICTS_FORMAT, 1),
                    groups[0].Header, "競合グループの見出し");
    }

    /// <summary>競合が無ければ空の競合グループを作らない（見出しだけが並ぶのを避ける）。</summary>
    private static void NoConflictGroupWhenClean()
    {
        var groups = ChangeListBuilder.Build(
            NewStatus(NewChange("a.txt", FileChangeKind.Modified)));

        Check.Equal(1, groups.Count, "グループ数");
        Check.Equal(ChangeGroupKind.Changes, groups[0].Kind, "種類");
    }

    /// <summary>
    /// 解決済み・自動マージ済みの競合はもう利用者の操作が要らない。
    /// 競合グループに残すと「まだ選ばされる」と誤解させる。
    /// </summary>
    private static void ResolvedConflictIsNotInConflictGroup()
    {
        var status = NewStatus(
            NewChange("auto.txt",     FileChangeKind.Modified, FileConflictState.AutoMerged),
            NewChange("mine.txt",     FileChangeKind.Modified, FileConflictState.ResolvedKeepMine),
            NewChange("remote.txt",   FileChangeKind.Modified, FileConflictState.ResolvedTakeRemote));

        var groups = ChangeListBuilder.Build(status);

        Check.Equal(1, groups.Count, "グループ数（競合グループは作らない）");
        Check.Equal(ChangeGroupKind.Changes, groups[0].Kind, "種類");
        Check.Equal(3, groups[0].Items.Count, "件数");
    }

    /// <summary>変更が無い・状態が無いときは空の並びになる。</summary>
    private static void EmptyStatusHasNoGroups()
    {
        Check.Equal(0, ChangeListBuilder.Build(null).Count, "null のとき");
        Check.Equal(0, ChangeListBuilder.Build(NewStatus()).Count, "変更なしのとき");
    }

    /// <summary>
    /// 並びはパス順に固定する。Lore が返す順は更新のたびに入れ替わり得るため、
    /// 固定しないと利用者が同じ項目を目で追えない。
    /// </summary>
    private static void ChangesAreSortedByPath()
    {
        var status = NewStatus(
            NewChange("zeta/c.txt",  FileChangeKind.Modified),
            NewChange("Alpha/a.txt", FileChangeKind.Modified),
            NewChange("beta/b.txt",  FileChangeKind.Modified));

        var items = ChangeListBuilder.Build(status)[0].Items.Select(c => c.Path).ToList();

        Check.Equal("Alpha/a.txt", items[0], "1 番目（大文字小文字を区別しない）");
        Check.Equal("beta/b.txt",  items[1], "2 番目");
        Check.Equal("zeta/c.txt",  items[2], "3 番目");
    }

    // ============================================================
    //  表示名
    // ============================================================

    /// <summary>変更の種類はすべて日本語の表示名を持つ。</summary>
    private static void ChangeKindHasDisplayName()
    {
        Check.Equal(VersionControlMessages.PANEL_CHANGE_ADDED,
                    Text(FileChangeKind.Added), "追加");
        Check.Equal(VersionControlMessages.PANEL_CHANGE_MODIFIED,
                    Text(FileChangeKind.Modified), "変更");
        Check.Equal(VersionControlMessages.PANEL_CHANGE_DELETED,
                    Text(FileChangeKind.Deleted), "削除");
        Check.Equal(VersionControlMessages.PANEL_CHANGE_MOVED,
                    Text(FileChangeKind.Moved), "移動");
        Check.Equal(VersionControlMessages.PANEL_CHANGE_COPIED,
                    Text(FileChangeKind.Copied), "複製");
        Check.Equal(VersionControlMessages.PANEL_CHANGE_UNKNOWN,
                    Text(FileChangeKind.Unknown), "不明");

        static string Text(FileChangeKind kind)
            => VersionControlDisplay.ToChangeText(kind, FileConflictState.None);
    }

    /// <summary>未解決の競合は、種類が何であれ「競合」と出す。</summary>
    private static void ConflictOverridesChangeKind()
    {
        Check.Equal(VersionControlMessages.PANEL_CHANGE_CONFLICT,
                    VersionControlDisplay.ToChangeText(
                        FileChangeKind.Modified, FileConflictState.Unresolved),
                    "表示名");
        Check.Equal(VersionControlDisplay.ICON_KEY_CONFLICT,
                    VersionControlDisplay.ToChangeIconKey(
                        FileChangeKind.Added, FileConflictState.Unresolved),
                    "アイコン");

        // 解決済みなら元の種類へ戻る。
        Check.Equal(VersionControlMessages.PANEL_CHANGE_MODIFIED,
                    VersionControlDisplay.ToChangeText(
                        FileChangeKind.Modified, FileConflictState.ResolvedKeepMine),
                    "解決済みは種類を出す");
    }

    /// <summary>
    /// 種類ごとに別のアイコンキーを返す（同じ絵だと一覧で見分けられない）。
    /// キーの綴りが Icons.xaml と合っているかは check_icons.py が見る。
    /// </summary>
    private static void ChangeKindHasDistinctIcons()
    {
        var kinds = new[]
        {
            FileChangeKind.Added, FileChangeKind.Modified, FileChangeKind.Deleted,
            FileChangeKind.Moved, FileChangeKind.Copied,   FileChangeKind.Unknown,
        };

        var keys = kinds
            .Select(k => VersionControlDisplay.ToChangeIconKey(k, FileConflictState.None))
            .ToList();

        Check.Equal(kinds.Length, keys.Distinct(StringComparer.Ordinal).Count(),
                    "種類ごとに別のアイコンキー");
        Check.True(keys.All(k => k.StartsWith("Icon.", StringComparison.Ordinal)),
                   "すべて Icons.xaml のキー形式");
    }

    /// <summary>
    /// サーバ認証が無い構成では所有者が不明になる。これは普通の状態なので
    /// 「不明」と淡々と出し、**自分のものと決めつけて解除させない**。
    /// </summary>
    private static void UnknownLockIsShownAsUnknown()
    {
        Check.Equal(VersionControlMessages.PANEL_LOCK_HOLDER_UNKNOWN,
                    VersionControlDisplay.ToLockHolderText(
                        LockHolder.Unknown, LockInfo.UNKNOWN_OWNER),
                    "表示名");
        Check.True(!VersionControlDisplay.CanRelease(LockHolder.Unknown),
                   "解除ボタンを出さない");
    }

    /// <summary>解除できるのは自分のロックだけ。</summary>
    private static void OnlySelfLockCanBeReleased()
    {
        Check.True(VersionControlDisplay.CanRelease(LockHolder.Self), "自分");
        Check.True(!VersionControlDisplay.CanRelease(LockHolder.Other), "他人");
        Check.True(!VersionControlDisplay.CanRelease(LockHolder.None), "ロックなし");

        Check.Equal(VersionControlMessages.PANEL_LOCK_HOLDER_SELF,
                    VersionControlDisplay.ToLockHolderText(LockHolder.Self, "me@example.com"),
                    "自分の表示名");
        Check.Equal(string.Format(VersionControlMessages.PANEL_LOCK_HOLDER_OTHER_FORMAT, "bob"),
                    VersionControlDisplay.ToLockHolderText(LockHolder.Other, "bob"),
                    "他人の表示名");
    }

    /// <summary>日時が取得できていないときに 1970 年を出さない。</summary>
    private static void MissingTimestampIsLabeled()
    {
        Check.Equal(VersionControlMessages.PANEL_TIMESTAMP_UNKNOWN,
                    VersionControlDisplay.ToTimestampText(default), "未取得のとき");

        var text = VersionControlDisplay.ToTimestampText(
            new DateTime(2026, 9, 18, 3, 45, 0, DateTimeKind.Utc));
        Check.True(text != VersionControlMessages.PANEL_TIMESTAMP_UNKNOWN,
                   $"取得できていれば日時を出す（実際: {text}）");
    }

    /// <summary>
    /// 履歴のメッセージが空のまま並ぶと「壊れている」ように見える。
    /// メタデータを引けなかった行にも必ず何か書く。
    /// </summary>
    private static void EmptyRevisionMessageIsLabeled()
    {
        Check.Equal(VersionControlMessages.PANEL_REVISION_NO_MESSAGE,
                    VersionControlDisplay.ToRevisionMessageText(null), "null のとき");
        Check.Equal(VersionControlMessages.PANEL_REVISION_NO_MESSAGE,
                    VersionControlDisplay.ToRevisionMessageText("   "), "空白だけのとき");
        Check.Equal("初期投入",
                    VersionControlDisplay.ToRevisionMessageText(" 初期投入 "), "前後の空白を落とす");
        Check.Equal("#12", VersionControlDisplay.ToRevisionNumberText(12UL), "リビジョン番号");
    }

    /// <summary>ヘッダーの「identity — リモート URL」が 1 行にまとまる。</summary>
    private static void ConnectionTextIsFormatted()
    {
        Check.Equal(
            string.Format(VersionControlMessages.PANEL_CONNECTION_FORMAT,
                          "tsubasa", "lore://127.0.0.1:41337"),
            VersionControlDisplay.ToConnectionText("tsubasa", "lore://127.0.0.1:41337"),
            "接続先の表示");
    }

    /// <summary>
    /// SEED アカウントでログイン中は、名前のうしろに「（ログイン中）」が付く。
    ///
    /// <para>
    /// 匿名（`.lore/config.toml` の identity をそのまま使っている状態）と
    /// 見分けが付かないと、ロックの「自分／他の人」が成立しているのかが
    /// 利用者にもこちらにも分からない。
    /// </para>
    /// </summary>
    private static void SignedInIdentityIsLabeled()
    {
        var signedIn = VersionControlDisplay.ToConnectionText(
            "tsubasa", "lore://127.0.0.1:41337", isSignedIn: true);

        Check.Equal(
            string.Format(VersionControlMessages.PANEL_CONNECTION_FORMAT,
                          string.Format(VersionControlMessages.PANEL_IDENTITY_SIGNED_IN_FORMAT,
                                        "tsubasa"),
                          "lore://127.0.0.1:41337"),
            signedIn,
            "ログイン中の接続先の表示");

        // 匿名のときは今までどおり（既定引数で振る舞いが変わらないこと）。
        Check.Equal(
            VersionControlDisplay.ToConnectionText("tsubasa", "lore://127.0.0.1:41337"),
            VersionControlDisplay.ToConnectionText("tsubasa", "lore://127.0.0.1:41337",
                                                   isSignedIn: false),
            "匿名のときの表示は変わらないこと");

        // identity が不明なのに「ログイン中」とは出さない（矛盾した表示になる）。
        var unknown = VersionControlDisplay.ToConnectionText(
            LockInfo.UNKNOWN_OWNER, "lore://127.0.0.1:41337", isSignedIn: true);
        Check.True(
            !unknown.Contains("ログイン中", StringComparison.Ordinal),
            $"identity 不明ならログイン中と出さない（実際: {unknown}）");
    }

    /// <summary>identity やリモート URL が無くても空欄にしない。</summary>
    private static void UnknownIdentityIsLabeled()
    {
        var text = VersionControlDisplay.ToConnectionText(LockInfo.UNKNOWN_OWNER, null);

        Check.True(text.Contains(VersionControlMessages.PANEL_IDENTITY_UNKNOWN,
                                 StringComparison.Ordinal),
                   $"identity 不明の表示（実際: {text}）");
        Check.True(text.Contains(VersionControlMessages.PANEL_REMOTE_UNKNOWN,
                                 StringComparison.Ordinal),
                   $"リモート未設定の表示（実際: {text}）");
    }

    // ============================================================
    //  共通ヘルパー
    // ============================================================

    /// <summary>「使える・待機中」の状態を作る。</summary>
    private static VersionControlPanelState NewAvailableState()
    {
        var state = new VersionControlPanelState();
        state.SetAvailability(true);
        return state;
    }

    /// <summary>変更 1 件を作る。</summary>
    /// <param name="path">リポジトリ相対パス。</param>
    /// <param name="kind">変更の種類。</param>
    /// <param name="conflict">競合の状態。</param>
    private static ChangedFile NewChange(
        string path, FileChangeKind kind, FileConflictState conflict = FileConflictState.None)
        => new(path, kind, conflict, isStaged: true, isDirty: true);

    /// <summary>変更を並べた状態を作る。</summary>
    /// <param name="changes">変更。</param>
    private static WorkingCopyStatus NewStatus(params ChangedFile[] changes)
        => new("main", 1UL, changes, RemoteComparison.NotChecked, StatusRefreshMode.ScanOffline);
}
