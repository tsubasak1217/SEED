// ============================================================
//  PureLogicTests.cs — Lore も実サーバも使わない判断ロジックのテスト
//
//  【なぜここが本命なのか】
//  Lore の罠（mine/theirs の逆転、空コミット、sync が競合でも成功、
//  LOCAL/REMOTE の二重報告、所有者 <unknown>）を吸収しているのは
//  すべて純粋関数か ILoreBackend の上の判断で、実サーバ無しで全分岐を踏める。
//  逆にここが緩いと、実機で「たまに壊れる」形でしか気づけなくなる。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Lore;
using SEEDEditor.VersionControl.Lore.Backend;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Null;
using SEEDEditor.VersionControl.Scheduling;
using SpriteRigTests;

namespace SEEDEditor.Tests.VersionControl;

/// <summary>
/// 実サーバを使わないテスト群。
/// </summary>
public static class PureLogicTests
{
    /// <summary>テストをランナーへ登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        // ── 競合選択の対応表（最重要。逆転すると作業が消える）──
        harness.Add("「自分の変更を残す」は Lore の theirs に対応する",      KeepMineMapsToTheirs);
        harness.Add("「リモートを採用」は Lore の mine に対応する",          TakeRemoteMapsToMine);
        harness.Add("対応表は往復しても同じ値に戻る",                        ResolutionMapRoundTrips);
        harness.Add("解決を頼むと対応表どおりの側が Lore へ渡る",            ResolveSendsMappedSide);
        harness.Add("flag_conflict_mine は「リモートを採用」として読まれる",  ConflictMineReadsAsTakeRemote);
        harness.Add("flag_conflict_theirs は「自分の変更を残す」として読まれる",
                                                                             ConflictTheirsReadsAsKeepMine);

        // ── 状態フラグ → モデル変換 ──
        harness.Add("KEEP は「変更」として読まれる（MODIFY は存在しない）",  KeepActionIsModified);
        harness.Add("ADD / DELETE / MOVE / COPY が対応する種類になる",       KnownActionsAreMapped);
        harness.Add("未知の action は Unknown になり落ちない",               UnknownActionIsUnknown);
        harness.Add("未解決の競合は Unresolved になる",                      UnresolvedConflictIsDetected);
        harness.Add("自動マージ済みは AutoMerged になる",                    AutoMergedIsDetected);
        harness.Add("競合フラグが立っていなければ None",                     NoConflictIsNone);
        harness.Add("競合フラグだけ立って判別できない行は Unresolved 扱い",  AmbiguousConflictIsUnresolved);
        harness.Add("オフライン取得ではリモート比較は NotChecked",           OfflineStatusDoesNotClaimRemote);
        harness.Add("オンライン取得で双方が進んでいれば Diverged",           DivergedIsDetected);
        harness.Add("オンライン取得でリモートが進んでいれば RemoteAhead",    RemoteAheadIsDetected);
        harness.Add("サーバへ届かなければ Unavailable",                      RemoteUnavailableIsDetected);
        harness.Add("リモートに同名ブランチが無ければ RemoteBranchMissing",  RemoteBranchMissingIsDetected);

        // ── 送信（空コミット防止・競合ブロック・NeedsSync）──
        harness.Add("送信は走査つきで作業コピー全体を stage する",           SubmitStagesWithScan);
        harness.Add("staged が 0 件なら commit を呼ばず NothingToDo",        SubmitWithNothingStagedDoesNotCommit);
        harness.Add("未解決の競合があれば送信は Conflicted で止まる",        SubmitBlockedByConflicts);
        harness.Add("stage が空でも手元が進んでいれば push だけ行う",        SubmitPushesUnpushedCommits);
        harness.Add("メッセージが空なら Lore を一度も呼ばない",              SubmitRequiresMessage);
        harness.Add("push が分岐で弾かれたら NeedsSync を返す",              PushRejectionBecomesNeedsSync);
        harness.Add("NeedsSync でもコミット済みであることが明細に残る",      NeedsSyncKeepsCommittedFlag);
        harness.Add("分岐と無関係な push 失敗は Failed のまま",              UnrelatedPushFailureStaysFailed);
        harness.Add("送信が成功すると件数が明細に入る",                      SubmitSuccessReportsCount);

        // ── 最新を取得（sync は競合しても成功を返す）──
        harness.Add("sync が成功でも競合があれば Conflicted",                SyncSuccessWithConflictsIsConflicted);
        harness.Add("競合が無ければ sync は成功を返す",                      SyncWithoutConflictsSucceeds);

        // ── 競合の解決 ──
        harness.Add("全部解決したらマージのコミットまで行う",                ResolveCommitsWhenAllResolved);
        harness.Add("競合が残っていればコミットしない",                      ResolveDoesNotCommitWhileConflictsRemain);

        // ── ブランチの統合 ──
        harness.Add("同名ブランチの LOCAL / REMOTE が 1 件へ統合される",      BranchesAreMerged);
        harness.Add("ローカルだけのブランチは IsLocalOnly になる",           LocalOnlyBranchIsFlagged);
        harness.Add("リモートだけのブランチは IsRemoteOnly になる",          RemoteOnlyBranchIsFlagged);
        harness.Add("現在のブランチはどちらの行で報告されても拾う",          CurrentBranchIsPickedUpFromEitherRow);
        harness.Add("ブランチの並びは最初に現れた順を保つ",                  BranchOrderIsPreserved);
        harness.Add("未知の location はローカル扱いで消えない",              UnknownLocationIsTreatedAsLocal);

        // ── ロック（所有者不明の扱い）──
        harness.Add("所有者 <unknown> は Unknown として扱う",                UnknownOwnerIsNotSelf);
        harness.Add("所有者が空でも Unknown として扱う",                     EmptyOwnerIsUnknown);
        harness.Add("自分の identity と一致すれば Self",                     MatchingOwnerIsSelf);
        harness.Add("別人の identity なら Other",                            DifferentOwnerIsOther);
        harness.Add("行が返らなかったパスは None として 1 件返る",           MissingLockRowBecomesNone);
        harness.Add("<unknown> のロック取得は HeldByUnknown になる",         AcquireOnUnknownOwnerIsHeldByUnknown);
        harness.Add("他人が持っていれば HeldByOther で編集不可",             AcquireOnOtherOwnerIsHeldByOther);
        harness.Add("もともと自分が持っていれば AlreadyMine",                AcquireWhenAlreadyMine);
        harness.Add("新しく取れたら Acquired",                               AcquireWhenNewlyTaken);

        // ── 履歴（オフラインは取得できない）──
        harness.Add("接続できないときの履歴は RequiresConnection",           HistoryOfflineRequiresConnection);
        harness.Add("履歴の件数指定 0 は既定値へ丸められる",                 HistoryLengthIsClamped);

        // ── 通知（パス変換）──
        harness.Add("作業コピー外のパスは Lore へ渡さない",                  OutsidePathsAreRejected);
        harness.Add("同じファイルを 2 回渡しても 1 回だけ通知する",          DuplicatePathsAreDeduplicated);
        harness.Add("改名は stage move として通知される",                    MoveIsReportedAsStageMove);
        harness.Add("パス区切りはスラッシュへ正規化される",                  PathSeparatorsAreNormalized);

        // ── VCS 無し ──
        harness.Add("NullProvider はすべて Unavailable を返す",              NullProviderIsUnavailable);
        harness.Add("NullProvider のロックも Unavailable",                   NullLockServiceIsUnavailable);
        harness.Add(".lore が無いフォルダは作業コピーと判定しない",          NonLoreFolderIsNotWorkingCopy);
        harness.Add(".lore があるフォルダは作業コピーと判定する",            LoreFolderIsWorkingCopy);

        // ── スケジューラ ──
        harness.Add("直列ワーカーは操作を重ねて実行しない",                  SerialWorkerDoesNotOverlap);
        harness.Add("直列ワーカーは投入順に実行する",                        SerialWorkerKeepsOrder);
        harness.Add("破棄後の投入は中断として返る",                          SerialWorkerAfterDisposeIsCanceled);
    }

    // ============================================================
    //  競合選択の対応表
    // ============================================================

    /// <summary>「自分の変更を残す」が Lore の theirs（= ローカル側）になる。</summary>
    private static void KeepMineMapsToTheirs()
        => Check.Equal(LoreResolveSide.Theirs,
                       LoreConflictResolutionMap.ToLoreSide(ConflictResolutionChoice.KeepMine),
                       "KeepMine の対応先");

    /// <summary>「リモートを採用」が Lore の mine（= リモート側）になる。</summary>
    private static void TakeRemoteMapsToMine()
        => Check.Equal(LoreResolveSide.Mine,
                       LoreConflictResolutionMap.ToLoreSide(ConflictResolutionChoice.TakeRemote),
                       "TakeRemote の対応先");

    /// <summary>対応表を往復しても元の選択に戻る。</summary>
    private static void ResolutionMapRoundTrips()
    {
        foreach (var choice in new[] { ConflictResolutionChoice.KeepMine,
                                       ConflictResolutionChoice.TakeRemote })
        {
            var side = LoreConflictResolutionMap.ToLoreSide(choice);
            Check.Equal(choice, LoreConflictResolutionMap.ToChoice(side), $"{choice} の往復");
        }
    }

    /// <summary>プロバイダ経由でも対応表どおりの側が Lore へ渡る。</summary>
    private static void ResolveSendsMappedSide()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status());   // 解決後: 競合なし
        using var provider = NewProvider(backend);

        provider.ResolveConflictsAsync(
            new[] { "a.txt" }, ConflictResolutionChoice.KeepMine).GetAwaiter().GetResult();
        Check.Equal(LoreResolveSide.Theirs, backend.LastResolveSide, "KeepMine で渡る側");

        provider.ResolveConflictsAsync(
            new[] { "a.txt" }, ConflictResolutionChoice.TakeRemote).GetAwaiter().GetResult();
        Check.Equal(LoreResolveSide.Mine, backend.LastResolveSide, "TakeRemote で渡る側");
    }

    /// <summary>flag_conflict_mine は「リモートを採用」で解決済みと読む。</summary>
    private static void ConflictMineReadsAsTakeRemote()
    {
        var row = FakeRows.File("a.txt", conflict: true, conflictMine: true);
        Check.Equal(FileConflictState.ResolvedTakeRemote,
                    LoreStatusTranslator.ToConflictState(row), "flag_conflict_mine の読み");
    }

    /// <summary>flag_conflict_theirs は「自分の変更を残す」で解決済みと読む。</summary>
    private static void ConflictTheirsReadsAsKeepMine()
    {
        var row = FakeRows.File("a.txt", conflict: true, conflictTheirs: true);
        Check.Equal(FileConflictState.ResolvedKeepMine,
                    LoreStatusTranslator.ToConflictState(row), "flag_conflict_theirs の読み");
    }

    // ============================================================
    //  状態フラグ → モデル変換
    // ============================================================

    /// <summary>Lore の KEEP は「内容が変わった」を意味する。</summary>
    private static void KeepActionIsModified()
    {
        Check.Equal(FileChangeKind.Modified,
                    LoreStatusTranslator.ToChangeKind("KEEP"), "KEEP の種類");
        // 綴りがぶれても拾えること（バインディングの ToString() 依存を避ける）。
        Check.Equal(FileChangeKind.Modified,
                    LoreStatusTranslator.ToChangeKind("Keep"), "Keep の種類");
    }

    /// <summary>既知の action がすべて対応する種類になる。</summary>
    private static void KnownActionsAreMapped()
    {
        Check.Equal(FileChangeKind.Added,   LoreStatusTranslator.ToChangeKind("ADD"),    "ADD");
        Check.Equal(FileChangeKind.Deleted, LoreStatusTranslator.ToChangeKind("DELETE"), "DELETE");
        Check.Equal(FileChangeKind.Moved,   LoreStatusTranslator.ToChangeKind("MOVE"),   "MOVE");
        Check.Equal(FileChangeKind.Copied,  LoreStatusTranslator.ToChangeKind("COPY"),   "COPY");
    }

    /// <summary>未知の action は Unknown になる（例外にしない）。</summary>
    private static void UnknownActionIsUnknown()
    {
        Check.Equal(FileChangeKind.Unknown, LoreStatusTranslator.ToChangeKind("GRAFT"), "未知の action");
        Check.Equal(FileChangeKind.Unknown, LoreStatusTranslator.ToChangeKind(null),    "null の action");
        Check.Equal(FileChangeKind.Unknown, LoreStatusTranslator.ToChangeKind("  "),    "空白の action");
    }

    /// <summary>未解決の競合が検出される。</summary>
    private static void UnresolvedConflictIsDetected()
    {
        var row = FakeRows.File("a.txt", conflict: true, conflictUnresolved: true);
        Check.Equal(FileConflictState.Unresolved,
                    LoreStatusTranslator.ToConflictState(row), "未解決の競合");
        Check.True(LoreStatusTranslator.ToChangedFile(row).IsUnresolvedConflict,
                   "IsUnresolvedConflict が真になる");
    }

    /// <summary>自動マージ済みが検出される。</summary>
    private static void AutoMergedIsDetected()
    {
        var row = FakeRows.File("a.txt", conflict: true, conflictAutomerged: true);
        Check.Equal(FileConflictState.AutoMerged,
                    LoreStatusTranslator.ToConflictState(row), "自動マージ済み");
    }

    /// <summary>競合していない行は None。</summary>
    private static void NoConflictIsNone()
    {
        // 競合フラグが立っていなければ、下位のフラグが立っていても None にする
        // （Lore が古い値を残していても誤って競合表示しない）。
        var row = FakeRows.File("a.txt", conflict: false, conflictMine: true);
        Check.Equal(FileConflictState.None,
                    LoreStatusTranslator.ToConflictState(row), "非競合");
    }

    /// <summary>判別できない競合は「未解決」に倒す（勝手に片方を採らない）。</summary>
    private static void AmbiguousConflictIsUnresolved()
    {
        var row = FakeRows.File("a.txt", conflict: true);
        Check.Equal(FileConflictState.Unresolved,
                    LoreStatusTranslator.ToConflictState(row), "判別できない競合");
    }

    /// <summary>オフライン取得ではリモートとの比較を主張しない。</summary>
    private static void OfflineStatusDoesNotClaimRemote()
    {
        // サーバへ聞いていないのにフラグが 0 だからといって「一致」と言ってはいけない。
        var status = FakeRows.Status(remoteAvailable: false, remoteBranchExists: false);
        Check.Equal(RemoteComparison.NotChecked,
                    LoreStatusTranslator.ToRemoteComparison(status.Revision,
                                                            StatusRefreshMode.ScanOffline),
                    "オフラインでの比較結果");
        Check.Equal(RemoteComparison.NotChecked,
                    LoreStatusTranslator.ToRemoteComparison(status.Revision,
                                                            StatusRefreshMode.TrackedOnly),
                    "記録のみでの比較結果");
    }

    /// <summary>双方が進んでいれば分岐。</summary>
    private static void DivergedIsDetected()
    {
        var status = FakeRows.Status(isLocalAhead: true, isRemoteAhead: true);
        Check.Equal(RemoteComparison.Diverged,
                    LoreStatusTranslator.ToRemoteComparison(status.Revision,
                                                            StatusRefreshMode.ScanOnline),
                    "分岐の判定");
    }

    /// <summary>リモートだけ進んでいれば RemoteAhead。</summary>
    private static void RemoteAheadIsDetected()
    {
        var status = FakeRows.Status(isRemoteAhead: true);
        Check.Equal(RemoteComparison.RemoteAhead,
                    LoreStatusTranslator.ToRemoteComparison(status.Revision,
                                                            StatusRefreshMode.ScanOnline),
                    "リモートが先行");
    }

    /// <summary>サーバへ届かない・権限が無いときは Unavailable。</summary>
    private static void RemoteUnavailableIsDetected()
    {
        var unreachable = FakeRows.Status(remoteAvailable: false);
        Check.Equal(RemoteComparison.Unavailable,
                    LoreStatusTranslator.ToRemoteComparison(unreachable.Revision,
                                                            StatusRefreshMode.ScanOnline),
                    "到達できない");

        var unauthorized = FakeRows.Status(remoteAuthorized: false);
        Check.Equal(RemoteComparison.Unavailable,
                    LoreStatusTranslator.ToRemoteComparison(unauthorized.Revision,
                                                            StatusRefreshMode.ScanOnline),
                    "権限が無い");
    }

    /// <summary>リモートに同名ブランチが無いとき（初回 push 前）。</summary>
    private static void RemoteBranchMissingIsDetected()
    {
        var status = FakeRows.Status(remoteBranchExists: false, isLocalAhead: true);
        Check.Equal(RemoteComparison.RemoteBranchMissing,
                    LoreStatusTranslator.ToRemoteComparison(status.Revision,
                                                            StatusRefreshMode.ScanOnline),
                    "リモートにブランチが無い");
    }

    // ============================================================
    //  送信
    // ============================================================

    /// <summary>送信は必ず走査つきで作業コピー全体を stage する。</summary>
    private static void SubmitStagesWithScan()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(new[] { FakeRows.File("a.txt", staged: true) }));
        using var provider = NewProvider(backend);

        provider.SubmitAsync("メッセージ").GetAwaiter().GetResult();

        Check.Equal(1, backend.StageCallCount, "stage の呼び出し回数");
        Check.True(backend.LastStageScan, "走査が有効になっている（無いと何も stage されない）");
        Check.Equal(1, backend.LastStagePaths.Count, "stage のパス件数");
        Check.Equal(LoreBackendPaths.REPOSITORY_ROOT, backend.LastStagePaths[0], "stage の対象");
    }

    /// <summary>staged が 0 件なら commit を呼ばない（空リビジョン防止）。</summary>
    private static void SubmitWithNothingStagedDoesNotCommit()
    {
        var backend = new FakeLoreBackend();
        // dirty だが staged ではない行しか無い状態。
        backend.StatusResults.Add(FakeRows.Status(new[] { FakeRows.File("a.txt", staged: false) }));
        using var provider = NewProvider(backend);

        var result = provider.SubmitAsync("メッセージ").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.NothingToDo, result.Outcome, "結末");
        Check.Equal(0, backend.CommitCallCount, "commit の呼び出し回数");
        Check.Equal(0, backend.PushCallCount,   "push の呼び出し回数");
    }

    /// <summary>未解決の競合があると送信は止まる。</summary>
    private static void SubmitBlockedByConflicts()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(new[]
        {
            FakeRows.File("a.txt", staged: true),
            FakeRows.File("b.txt", staged: true, conflict: true, conflictUnresolved: true),
        }));
        using var provider = NewProvider(backend);

        var result = provider.SubmitAsync("メッセージ").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Conflicted, result.Outcome, "結末");
        Check.Equal(0, backend.CommitCallCount, "競合中は commit しない");
    }

    /// <summary>
    /// stage するものが無くても、手元に未 push のコミット（競合解決のマージなど）が
    /// あれば push する。ここを NothingToDo で止めると、解決したマージが
    /// 永久に手元へ取り残されて誰にも届かない。
    /// </summary>
    private static void SubmitPushesUnpushedCommits()
    {
        var backend = new FakeLoreBackend();
        // 1 回目（stage 直後・オフライン）: staged は 0 件。
        backend.StatusResults.Add(FakeRows.Status(new[] { FakeRows.File("a.txt", staged: false) }));
        // 2 回目（オンライン確認）: 手元が進んでいる。
        backend.StatusResults.Add(FakeRows.Status(isLocalAhead: true, revisionNumber: 9));
        using var provider = NewProvider(backend);

        var result = provider.SubmitAsync("メッセージ").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome, "結末");
        Check.Equal(0, backend.CommitCallCount, "空コミットは作らない");
        Check.Equal(1, backend.PushCallCount,   "push は行う");
    }

    /// <summary>メッセージが空なら Lore を呼ばない。</summary>
    private static void SubmitRequiresMessage()
    {
        var backend = new FakeLoreBackend();
        using var provider = NewProvider(backend);

        var result = provider.SubmitAsync("   ").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Failed, result.Outcome, "結末");
        Check.Equal(0, backend.StageCallCount, "stage も呼ばない");
    }

    /// <summary>push が分岐で弾かれたら NeedsSync。</summary>
    private static void PushRejectionBecomesNeedsSync()
    {
        foreach (var message in new[]
                 {
                     "Branch history is divergent",
                     "Local branch has diverged, synchronize to merge",
                     "Local branch is behind remote",
                 })
        {
            var backend = new FakeLoreBackend
            {
                // Lore は分岐を汎用の InvalidArguments へ畳むので、コードでは区別できない。
                PushResult = LoreCallResult.Failure(2, new[] { message }),
            };
            backend.StatusResults.Add(
                FakeRows.Status(new[] { FakeRows.File("a.txt", staged: true) }));
            using var provider = NewProvider(backend);

            var result = provider.SubmitAsync("メッセージ").GetAwaiter().GetResult();
            Check.Equal(VersionControlOutcome.NeedsSync, result.Outcome, $"「{message}」の結末");
        }
    }

    /// <summary>NeedsSync でも「コミットは済んでいる」ことが分かる。</summary>
    private static void NeedsSyncKeepsCommittedFlag()
    {
        var backend = new FakeLoreBackend
        {
            PushResult = LoreCallResult.Failure(2, new[] { "Branch history is divergent" }),
        };
        backend.StatusResults.Add(FakeRows.Status(new[] { FakeRows.File("a.txt", staged: true) }));
        using var provider = NewProvider(backend);

        var result = provider.SubmitAsync("メッセージ").GetAwaiter().GetResult();

        Check.Equal(1, backend.CommitCallCount, "commit は行われている");
        Check.True(result.Value is not null, "明細が返る");
        Check.True(result.Value!.CommittedButNotPushed, "コミット済み・未 push の印");
    }

    /// <summary>分岐と関係ない失敗は Failed のまま（誤って sync を促さない）。</summary>
    private static void UnrelatedPushFailureStaysFailed()
    {
        var backend = new FakeLoreBackend
        {
            PushResult = LoreCallResult.Failure(2, new[] { "Branch is protected" }),
        };
        backend.StatusResults.Add(FakeRows.Status(new[] { FakeRows.File("a.txt", staged: true) }));
        using var provider = NewProvider(backend);

        var result = provider.SubmitAsync("メッセージ").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Failed, result.Outcome, "結末");
    }

    /// <summary>送信が成功すると件数が明細に入る。</summary>
    private static void SubmitSuccessReportsCount()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(new[]
        {
            FakeRows.File("a.txt", staged: true),
            FakeRows.File("b.txt", staged: true),
            FakeRows.File("c.txt", staged: false),
        }, revisionNumber: 7));
        using var provider = NewProvider(backend);

        var result = provider.SubmitAsync("メッセージ").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome, "結末");
        Check.Equal(2, result.Value!.FileCount, "送信件数（staged のみ数える）");
        Check.Equal(7UL, result.Value.RevisionNumber, "リビジョン番号");
        Check.Equal("メッセージ", backend.LastCommitMessage, "コミットメッセージ");
    }

    // ============================================================
    //  最新を取得
    // ============================================================

    /// <summary>sync 自体が成功でも、競合があれば Conflicted を返す。</summary>
    private static void SyncSuccessWithConflictsIsConflicted()
    {
        var backend = new FakeLoreBackend
        {
            // ここが Lore の罠。sync は競合しても成功を返す。
            SyncResult = LoreCallResult.Success,
        };
        backend.StatusResults.Add(FakeRows.Status(new[]
        {
            FakeRows.File("scene.json", conflict: true, conflictUnresolved: true),
            FakeRows.File("other.txt"),
        }));
        using var provider = NewProvider(backend);

        var result = provider.FetchLatestAsync().GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Conflicted, result.Outcome, "結末");
        Check.Equal(1, result.Value!.Conflicts.Count, "競合の件数");
        Check.Equal("scene.json", result.Value.Conflicts[0].Path, "競合したファイル");
    }

    /// <summary>競合が無ければ成功。</summary>
    private static void SyncWithoutConflictsSucceeds()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(new[] { FakeRows.File("a.txt") }));
        using var provider = NewProvider(backend);

        var result = provider.FetchLatestAsync().GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome, "結末");
        Check.True(!result.Value!.HasConflicts, "競合なし");
    }

    // ============================================================
    //  競合の解決
    // ============================================================

    /// <summary>全部解決したらマージのコミットまで行う。</summary>
    private static void ResolveCommitsWhenAllResolved()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(new[]
        {
            FakeRows.File("a.txt", conflict: true, conflictTheirs: true),
        }));
        using var provider = NewProvider(backend);

        var result = provider.ResolveConflictsAsync(
            new[] { "a.txt" }, ConflictResolutionChoice.KeepMine).GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome, "結末");
        Check.Equal(1, backend.CommitCallCount, "マージのコミットが行われる");
    }

    /// <summary>競合が残っている間はコミットしない。</summary>
    private static void ResolveDoesNotCommitWhileConflictsRemain()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(new[]
        {
            FakeRows.File("a.txt", conflict: true, conflictTheirs: true),
            FakeRows.File("b.txt", conflict: true, conflictUnresolved: true),
        }));
        using var provider = NewProvider(backend);

        var result = provider.ResolveConflictsAsync(
            new[] { "a.txt" }, ConflictResolutionChoice.KeepMine).GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome, "結末");
        Check.Equal(0, backend.CommitCallCount, "残っている間はコミットしない");
    }

    // ============================================================
    //  ブランチの統合
    // ============================================================

    /// <summary>同名ブランチが 1 件へ統合される。</summary>
    private static void BranchesAreMerged()
    {
        var merged = LoreBranchTranslator.Merge(new[]
        {
            new LoreBranchRow("main", "LOCAL",  true),
            new LoreBranchRow("main", "REMOTE", false),
        });

        Check.Equal(1, merged.Count, "統合後の件数");
        Check.True(merged[0].ExistsLocally,  "ローカルに在る");
        Check.True(merged[0].ExistsOnRemote, "リモートに在る");
    }

    /// <summary>ローカルにしか無いブランチ。</summary>
    private static void LocalOnlyBranchIsFlagged()
    {
        var merged = LoreBranchTranslator.Merge(new[]
        {
            new LoreBranchRow("feature", "LOCAL", false),
        });
        Check.True(merged[0].IsLocalOnly, "未送信のブランチ");
    }

    /// <summary>リモートにしか無いブランチ。</summary>
    private static void RemoteOnlyBranchIsFlagged()
    {
        var merged = LoreBranchTranslator.Merge(new[]
        {
            new LoreBranchRow("hotfix", "REMOTE", false),
        });
        Check.True(merged[0].IsRemoteOnly, "手元に無いブランチ");
    }

    /// <summary>現在のブランチはどちらの行で報告されても拾う。</summary>
    private static void CurrentBranchIsPickedUpFromEitherRow()
    {
        var fromRemoteRow = LoreBranchTranslator.Merge(new[]
        {
            new LoreBranchRow("main", "LOCAL",  false),
            new LoreBranchRow("main", "REMOTE", true),
        });
        Check.True(fromRemoteRow[0].IsCurrent, "REMOTE 行で報告された現在ブランチ");
    }

    /// <summary>並び順は最初に現れた順を保つ。</summary>
    private static void BranchOrderIsPreserved()
    {
        var merged = LoreBranchTranslator.Merge(new[]
        {
            new LoreBranchRow("zeta",  "LOCAL",  false),
            new LoreBranchRow("alpha", "LOCAL",  false),
            new LoreBranchRow("zeta",  "REMOTE", false),
        });

        Check.Equal(2, merged.Count, "件数");
        Check.Equal("zeta",  merged[0].Name, "1 件目");
        Check.Equal("alpha", merged[1].Name, "2 件目");
    }

    /// <summary>未知の location はローカル扱い（一覧から消えない）。</summary>
    private static void UnknownLocationIsTreatedAsLocal()
    {
        var merged = LoreBranchTranslator.Merge(new[]
        {
            new LoreBranchRow("weird", "ARCHIVED_SOMEWHERE", false),
        });

        Check.Equal(1, merged.Count, "件数");
        Check.True(merged[0].ExistsLocally, "ローカル扱いになる");
    }

    // ============================================================
    //  ロック
    // ============================================================

    /// <summary>所有者 <c>&lt;unknown&gt;</c> は自分のものと決めつけない。</summary>
    private static void UnknownOwnerIsNotSelf()
    {
        var row = new LoreLockRow("a.txt", LockInfo.UNKNOWN_OWNER, "main", true, 0);
        Check.Equal(LockHolder.Unknown,
                    LoreLockTranslator.ResolveHolder(row, "tester@example.com"), "所有者の判定");
    }

    /// <summary>所有者が空でも Unknown。</summary>
    private static void EmptyOwnerIsUnknown()
    {
        var row = new LoreLockRow("a.txt", "", "main", true, 0);
        Check.Equal(LockHolder.Unknown,
                    LoreLockTranslator.ResolveHolder(row, "tester@example.com"), "所有者の判定");
    }

    /// <summary>自分の identity と一致すれば Self。</summary>
    private static void MatchingOwnerIsSelf()
    {
        var row = new LoreLockRow("a.txt", "tester@example.com", "main", true, 0);
        Check.Equal(LockHolder.Self,
                    LoreLockTranslator.ResolveHolder(row, "tester@example.com"), "所有者の判定");
    }

    /// <summary>別人なら Other。</summary>
    private static void DifferentOwnerIsOther()
    {
        var row = new LoreLockRow("a.txt", "other@example.com", "main", true, 0);
        Check.Equal(LockHolder.Other,
                    LoreLockTranslator.ResolveHolder(row, "tester@example.com"), "所有者の判定");
    }

    /// <summary>行が返らなかったパスも 1 件返る（None）。</summary>
    private static void MissingLockRowBecomesNone()
    {
        var infos = LoreLockTranslator.ToLockInfosForPaths(
            new[] { "a.txt", "b.txt" },
            new[] { new LoreLockRow("a.txt", "other@example.com", "main", true, 0) },
            "tester@example.com");

        Check.Equal(2, infos.Count, "照会したパスと同じ件数");
        Check.Equal("b.txt", infos[1].Path, "順序が保たれる");
        Check.Equal(LockHolder.None, infos[1].Holder, "行が無いパスはロックなし");
    }

    /// <summary>所有者不明のロックは取得できたと言わない。</summary>
    private static void AcquireOnUnknownOwnerIsHeldByUnknown()
    {
        var after = new LockInfo("a.txt", LockInfo.UNKNOWN_OWNER, LockHolder.Unknown);
        var result = LoreLockTranslator.ToAcquireResult(LoreCallResult.Success, null, after);

        Check.Equal(LockAcquireOutcome.HeldByUnknown, result.Outcome, "結末");
        Check.True(!result.CanEdit, "編集できると言わない");
    }

    /// <summary>他人が持っていれば取得できていない。</summary>
    private static void AcquireOnOtherOwnerIsHeldByOther()
    {
        var after = new LockInfo("a.txt", "other@example.com", LockHolder.Other);
        // Lore は二重取得でも成功を返すので、成功でも状態で判断する。
        var result = LoreLockTranslator.ToAcquireResult(LoreCallResult.Success, null, after);

        Check.Equal(LockAcquireOutcome.HeldByOther, result.Outcome, "結末");
        Check.True(!result.CanEdit, "編集できない");
    }

    /// <summary>もともと自分が持っていたら AlreadyMine。</summary>
    private static void AcquireWhenAlreadyMine()
    {
        var before = new LockInfo("a.txt", "tester@example.com", LockHolder.Self);
        var after  = new LockInfo("a.txt", "tester@example.com", LockHolder.Self);
        var result = LoreLockTranslator.ToAcquireResult(LoreCallResult.Success, before, after);

        Check.Equal(LockAcquireOutcome.AlreadyMine, result.Outcome, "結末");
        Check.True(result.CanEdit, "編集できる");
    }

    /// <summary>取得前はロックされておらず、取得後に自分のものになったら Acquired。</summary>
    private static void AcquireWhenNewlyTaken()
    {
        var before = new LockInfo("a.txt", string.Empty, LockHolder.None);
        var after  = new LockInfo("a.txt", "tester@example.com", LockHolder.Self);
        var result = LoreLockTranslator.ToAcquireResult(LoreCallResult.Success, before, after);

        Check.Equal(LockAcquireOutcome.Acquired, result.Outcome, "結末");
        Check.True(result.CanEdit, "編集できる");
    }

    // ============================================================
    //  履歴
    // ============================================================

    /// <summary>接続できないときの履歴失敗は RequiresConnection。</summary>
    private static void HistoryOfflineRequiresConnection()
    {
        var backend = new FakeLoreBackend
        {
            HistoryResult = LoreRowsResult<LoreRevisionRow>.FromFailure(
                LoreCallResult.Failure(2, new[] { "Cannot connect to remote server" })),
        };
        using var provider = NewProvider(backend);

        var result = provider.GetHistoryAsync().GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.RequiresConnection, result.Outcome, "結末");
    }

    /// <summary>0 件指定は既定値へ丸められる（Lore の 0 = 無制限を避ける）。</summary>
    private static void HistoryLengthIsClamped()
    {
        var settings = new VersionControlSettings(historyLength: 25);
        var backend  = new FakeLoreBackend
        {
            HistoryResult = new LoreRowsResult<LoreRevisionRow>(
                LoreCallResult.Success,
                new[] { new LoreRevisionRow(1, "abcd", "", "", 0) }),
        };
        using var provider = NewProvider(backend, settings);

        var result = provider.GetHistoryAsync(0).GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome, "結末");
        Check.Equal(1, result.Value!.Count, "件数");
        Check.Equal(1UL, result.Value[0].Number, "リビジョン番号");
    }

    // ============================================================
    //  通知（パス変換）
    // ============================================================

    /// <summary>作業コピーの外は Lore へ渡さない。</summary>
    private static void OutsidePathsAreRejected()
    {
        var backend = new FakeLoreBackend { WorkingCopyRoot = @"C:\proj" };
        using var provider = NewProvider(backend);

        var result = provider.NotifyChangedAsync(
            new[] { @"C:\other\a.txt" }).GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.NothingToDo, result.Outcome, "結末");
        Check.Equal(0, backend.DirtyCallCount, "Lore を呼ばない");
    }

    /// <summary>同じファイルを 2 回渡しても Lore へは 1 回だけ。</summary>
    private static void DuplicatePathsAreDeduplicated()
    {
        var backend = new FakeLoreBackend { WorkingCopyRoot = @"C:\proj" };
        using var provider = NewProvider(backend);

        provider.NotifyChangedAsync(new[]
        {
            @"C:\proj\assets\a.png",
            @"C:\proj\assets\a.png",
            @"C:\proj\assets\b.png",
        }).GetAwaiter().GetResult();

        Check.Equal(1, backend.DirtyCallCount, "呼び出し回数");
        Check.Equal(2, backend.LastDirtyPaths.Count, "重複を除いたパス件数");
    }

    /// <summary>改名は stage move で通知される（素の移動は履歴が切れる）。</summary>
    private static void MoveIsReportedAsStageMove()
    {
        var backend = new FakeLoreBackend { WorkingCopyRoot = @"C:\proj" };
        using var provider = NewProvider(backend);

        var result = provider.NotifyMovedAsync(
            @"C:\proj\assets\old.png", @"C:\proj\assets\new.png").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome, "結末");
        Check.Equal(1, backend.StageMoveCallCount, "呼び出し回数");
        Check.Equal("assets/old.png", backend.LastMove.From, "移動前");
        Check.Equal("assets/new.png", backend.LastMove.To,   "移動後");
    }

    /// <summary>区切り文字はスラッシュへ揃える。</summary>
    private static void PathSeparatorsAreNormalized()
    {
        var relative = VersionControlPaths.ToRepositoryRelative(
            @"C:\proj", @"C:\proj\assets\sub\a.png");
        Check.Equal("assets/sub/a.png", relative, "相対パス");

        // 作業コピーの外は null。
        Check.True(VersionControlPaths.ToRepositoryRelative(@"C:\proj", @"C:\a.png") is null,
                   "外のパスは null");
        // ルート自身は "." になる。
        Check.Equal(".", VersionControlPaths.ToRepositoryRelative(@"C:\proj", @"C:\proj"),
                    "ルート自身");
    }

    // ============================================================
    //  VCS 無し
    // ============================================================

    /// <summary>NullProvider はすべて Unavailable を返す。</summary>
    private static void NullProviderIsUnavailable()
    {
        using var provider = new NullVersionControlProvider(@"C:\proj");

        Check.True(!provider.IsAvailable, "利用不可");
        Check.Equal(VersionControlOutcome.Unavailable,
                    provider.GetStatusAsync().GetAwaiter().GetResult().Outcome, "状態取得");
        Check.Equal(VersionControlOutcome.Unavailable,
                    provider.SubmitAsync("m").GetAwaiter().GetResult().Outcome, "送信");
        Check.Equal(VersionControlOutcome.Unavailable,
                    provider.FetchLatestAsync().GetAwaiter().GetResult().Outcome, "最新を取得");
        Check.Equal(VersionControlOutcome.Unavailable,
                    provider.GetBranchesAsync().GetAwaiter().GetResult().Outcome, "ブランチ");
        Check.Equal(VersionControlOutcome.Unavailable,
                    provider.GetHistoryAsync().GetAwaiter().GetResult().Outcome, "履歴");
        // 状態取得は Unavailable でも空の値を返す（呼び出し側が null を扱わなくて済む）。
        Check.True(provider.GetStatusAsync().GetAwaiter().GetResult().Value is not null,
                   "状態は空でも非 null");
    }

    /// <summary>NullProvider のロックも Unavailable。</summary>
    private static void NullLockServiceIsUnavailable()
    {
        using var provider = new NullVersionControlProvider();
        var locks = provider.Locks;

        Check.True(locks is not null, "Locks は常に非 null");
        Check.True(!locks!.IsAvailable, "利用不可");
        Check.Equal(VersionControlOutcome.Unavailable,
                    locks.ListAsync().GetAwaiter().GetResult().Outcome, "一覧");
        Check.Equal(VersionControlOutcome.Unavailable,
                    locks.AcquireAsync(new[] { "a.txt" }).GetAwaiter().GetResult().Outcome, "取得");
    }

    /// <summary>`.lore` が無ければ作業コピーではない。</summary>
    private static void NonLoreFolderIsNotWorkingCopy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SEED_VcsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Check.True(!VersionControlPaths.IsLoreWorkingCopy(dir), "作業コピーではない");
            Check.True(!VersionControlPaths.IsLoreWorkingCopy(null), "null は作業コピーではない");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 後始末の失敗は無視 */ }
        }
    }

    /// <summary>`.lore` があれば作業コピー。</summary>
    private static void LoreFolderIsWorkingCopy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SEED_VcsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, VersionControlSettings.LORE_METADATA_DIR_NAME));
        try
        {
            Check.True(VersionControlPaths.IsLoreWorkingCopy(dir), "作業コピーと判定される");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 後始末の失敗は無視 */ }
        }
    }

    // ============================================================
    //  スケジューラ
    // ============================================================

    /// <summary>
    /// 直列ワーカーは操作を重ねない。
    /// 同じ作業コピーへ 2 つの Lore 呼び出しが同時に走ると壊れるため、
    /// ここが崩れると実機で散発的にリポジトリが壊れる。
    /// </summary>
    private static void SerialWorkerDoesNotOverlap()
    {
        using var scheduler = new SerialWorkerScheduler(TimeSpan.FromSeconds(5));

        var concurrent = 0;
        var maxConcurrent = 0;
        var tasks = new List<System.Threading.Tasks.Task<int>>();

        for (var i = 0; i < 20; i++)
        {
            tasks.Add(scheduler.RunAsync(
                "test",
                _ =>
                {
                    var now = Interlocked.Increment(ref concurrent);
                    // 観測した最大同時実行数を記録する（1 を超えたら直列化が壊れている）。
                    var observed = Volatile.Read(ref maxConcurrent);
                    if (now > observed) Volatile.Write(ref maxConcurrent, now);
                    Thread.Sleep(2);
                    Interlocked.Decrement(ref concurrent);
                    return 0;
                },
                TimeSpan.FromSeconds(10),
                CancellationToken.None));
        }

        System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
        Check.Equal(1, Volatile.Read(ref maxConcurrent), "観測された最大同時実行数");
    }

    /// <summary>投入順に実行される。</summary>
    private static void SerialWorkerKeepsOrder()
    {
        using var scheduler = new SerialWorkerScheduler(TimeSpan.FromSeconds(5));

        var order = new List<int>();
        var tasks = new List<System.Threading.Tasks.Task<int>>();
        for (var i = 0; i < 10; i++)
        {
            var index = i;
            tasks.Add(scheduler.RunAsync(
                "test",
                _ => { order.Add(index); return index; },
                TimeSpan.FromSeconds(10),
                CancellationToken.None));
        }

        System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
        Check.Equal(10, order.Count, "実行件数");
        Check.True(order.SequenceEqual(Enumerable.Range(0, 10)), "投入順に実行される");
    }

    /// <summary>破棄後の投入は中断として返る（例外にしない）。</summary>
    private static void SerialWorkerAfterDisposeIsCanceled()
    {
        var scheduler = new SerialWorkerScheduler(TimeSpan.FromSeconds(1));
        scheduler.Dispose();

        var task = scheduler.RunAsync(
            "test", _ => 1, TimeSpan.FromSeconds(1), CancellationToken.None);

        Check.True(task.IsCanceled, "中断として完了する");
    }

    // ============================================================
    //  共通ヘルパー
    // ============================================================

    /// <summary>
    /// テスト用のプロバイダを作る。スケジューラはその場実行（<see cref="InlineScheduler"/>）
    /// にして、テストが非決定的にならないようにする。
    /// </summary>
    /// <param name="backend">差し替えるバックエンド。</param>
    /// <param name="settings">設定（省略時は既定値）。</param>
    private static LoreProvider NewProvider(
        FakeLoreBackend backend, VersionControlSettings? settings = null)
        => new(backend, new InlineScheduler(), settings, ownsScheduler: true);
}
