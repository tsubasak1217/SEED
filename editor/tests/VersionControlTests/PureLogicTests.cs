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
using ProjectSystemTests;   // TempDir（実ファイルを書くテストで共有している後始末つき一時フォルダ）
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
        harness.Add("sync:「自分の変更を残す」は Lore の theirs に対応する", KeepMineMapsToTheirs);
        harness.Add("sync:「リモートを採用」は Lore の mine に対応する",     TakeRemoteMapsToMine);
        harness.Add("ブランチのマージでは対応が sync と入れ替わる",          BranchMergeMapIsFlippedFromSync);
        harness.Add("対応表は往復しても同じ値に戻る",                        ResolutionMapRoundTrips);
        harness.Add("解決を頼むと対応表どおりの側が Lore へ渡る",            ResolveSendsMappedSide);
        harness.Add("マージ後の解決では入れ替わった側が Lore へ渡る",        ResolveAfterBranchMergeSendsFlippedSide);
        harness.Add("解決しきったらマージの出どころの印が消える",            MergeOriginIsClearedAfterResolve);
        harness.Add("最新の取得は古いマージの出どころの印を消す",            SyncClearsStaleBranchMergeOrigin);
        harness.Add("ブランチの切り替えも出どころの印を消す",                BranchSwitchClearsMergeOrigin);
        harness.Add("作業コピー外では出どころの印を書かない",                MergeOriginStoreIsSilentOutsideWorkingCopy);
        harness.Add("flag_conflict_mine は出どころで読み方が変わる",          ConflictMineReadsAsTakeRemote);
        harness.Add("flag_conflict_theirs は出どころで読み方が変わる",
                                                                             ConflictTheirsReadsAsKeepMine);

        // ── 状態フラグ → モデル変換 ──
        harness.Add("KEEP は「変更」として読まれる（MODIFY は存在しない）",  KeepActionIsModified);
        harness.Add("ADD / DELETE / MOVE / COPY が対応する種類になる",       KnownActionsAreMapped);
        harness.Add("未知の action は Unknown になり落ちない",               UnknownActionIsUnknown);
        harness.Add("未解決の競合は Unresolved になる",                      UnresolvedConflictIsDetected);
        harness.Add("自動マージ済みは AutoMerged になる",                    AutoMergedIsDetected);
        harness.Add("競合フラグが立っていなければ None",                     NoConflictIsNone);
        harness.Add("競合フラグだけの行は「中身のまま解決済み」（実測の形）", ResolvedWithContentIsDetected);
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

        // ── ブランチのマージ ──
        harness.Add("マージは競合が無ければ成功し、送信を促す文言を返す",    MergeWithoutConflictsSucceeds);
        harness.Add("マージは戻り値が成功でも競合があれば Conflicted",       MergeSuccessWithConflictsIsConflicted);
        harness.Add("マージが失敗していても競合があれば Conflicted",         MergeFailureWithConflictsIsConflicted);
        harness.Add("分岐で弾かれたマージは NeedsSync を返す",               MergeRejectionBecomesNeedsSync);
        harness.Add("接続できないマージは RequiresConnection を返す",        MergeOfflineRequiresConnection);
        harness.Add("現在のブランチ自身は取り込めず Lore を呼ばない",        MergeIntoSelfIsRejected);
        harness.Add("マージ元が空なら Lore を一度も呼ばない",                MergeRequiresSourceName);
        harness.Add("自動コミットの文言に取り込み元と先の両方が入る",        MergeCommitMessageNamesBothBranches);

        // ── ブランチの削除（アーカイブ）──
        harness.Add("削除（アーカイブ）が成功すると Lore へ名前が渡る",      ArchivePassesNameToLore);
        harness.Add("現在のブランチは削除できず Lore を呼ばない",            ArchiveCurrentBranchIsRejected);
        harness.Add("既定ブランチは削除できず Lore を呼ばない",              ArchiveDefaultBranchIsRejected);
        harness.Add("既定ブランチ名は設定から差し替えられる",                ArchiveUsesConfiguredDefaultBranch);
        harness.Add("名前が空なら Lore を一度も呼ばない",                    ArchiveRequiresName);
        harness.Add("Lore が失敗したら Failed になる",                       ArchiveFailureIsFailed);

        // ── ブランチ操作の除外規則（ダイアログの候補と同じ規則）──
        harness.Add("マージ候補から現在のブランチが外れる",                  MergeCandidatesExcludeCurrent);
        harness.Add("削除候補から現在のブランチと既定ブランチが外れる",      ArchiveCandidatesExcludeProtected);
        harness.Add("除外規則は大文字小文字を区別しない",                    BranchRulesIgnoreCase);
        harness.Add("候補の重複と空名は落とし、並び順は保つ",                BranchCandidatesAreDeduplicated);
        harness.Add("現在のブランチが不明なら候補を空にしない",              BranchCandidatesSurviveUnknownCurrent);

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

        // ── 履歴のメタデータ（history にはメッセージ・作者・日時が無い）──
        harness.Add("実機どおりのキーからメッセージ・作者・日時を取り出す",  MetadataExtractsRealKeys);
        harness.Add("作者は committed-by を created-by より優先する",        MetadataPrefersCommittedBy);
        harness.Add("キーの区切りと大文字小文字は無視して照合する",          MetadataKeysAreNormalized);
        harness.Add("未知のキー・種類は無視して落ちない",                    MetadataIgnoresUnknownKeys);
        harness.Add("Unix ミリ秒・マイクロ秒・ナノ秒を秒へ直す",            MetadataNormalizesTimeUnits);
        harness.Add("現実的でない時刻は「不明」にして表示しない",            MetadataRejectsImplausibleTime);
        harness.Add("履歴はメタデータで埋められる",                          HistoryIsEnrichedWithMetadata);
        harness.Add("メタデータを引く件数は上限で打ち切られる",              HistoryMetadataIsLimited);
        harness.Add("メタデータの取得に失敗しても履歴は消えない",            HistorySurvivesMetadataFailure);

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

    /// <summary>sync のマージでは「自分の変更を残す」が Lore の theirs（= ローカル側）。</summary>
    private static void KeepMineMapsToTheirs()
        => Check.Equal(LoreResolveSide.Theirs,
                       LoreConflictResolutionMap.ToLoreSide(
                           ConflictResolutionChoice.KeepMine, MergeOrigin.Sync),
                       "KeepMine の対応先（sync）");

    /// <summary>sync のマージでは「リモートを採用」が Lore の mine（= リモート側）。</summary>
    private static void TakeRemoteMapsToMine()
        => Check.Equal(LoreResolveSide.Mine,
                       LoreConflictResolutionMap.ToLoreSide(
                           ConflictResolutionChoice.TakeRemote, MergeOrigin.Sync),
                       "TakeRemote の対応先（sync）");

    /// <summary>
    /// ★ブランチのマージでは向きが入れ替わる（実機で確認した Lore の罠）。
    /// ここを sync と同じにすると、マージの解決で逆の側を採ってしまう。
    /// </summary>
    private static void BranchMergeMapIsFlippedFromSync()
    {
        Check.Equal(LoreResolveSide.Mine,
                    LoreConflictResolutionMap.ToLoreSide(
                        ConflictResolutionChoice.KeepMine, MergeOrigin.BranchMerge),
                    "KeepMine の対応先（branch merge）");
        Check.Equal(LoreResolveSide.Theirs,
                    LoreConflictResolutionMap.ToLoreSide(
                        ConflictResolutionChoice.TakeRemote, MergeOrigin.BranchMerge),
                    "TakeRemote の対応先（branch merge）");

        // 2 つの出どころで同じ選択が別の側になる、という事実そのものを固定する。
        foreach (var choice in new[] { ConflictResolutionChoice.KeepMine,
                                       ConflictResolutionChoice.TakeRemote })
        {
            Check.True(LoreConflictResolutionMap.ToLoreSide(choice, MergeOrigin.Sync)
                       != LoreConflictResolutionMap.ToLoreSide(choice, MergeOrigin.BranchMerge),
                       $"{choice} は出どころで対応先が変わる");
        }
    }

    /// <summary>対応表を往復しても元の選択に戻る（どちらの出どころでも）。</summary>
    private static void ResolutionMapRoundTrips()
    {
        foreach (var origin in new[] { MergeOrigin.Sync, MergeOrigin.BranchMerge })
        {
            foreach (var choice in new[] { ConflictResolutionChoice.KeepMine,
                                           ConflictResolutionChoice.TakeRemote })
            {
                var side = LoreConflictResolutionMap.ToLoreSide(choice, origin);
                Check.Equal(choice, LoreConflictResolutionMap.ToChoice(side, origin),
                            $"{choice}（{origin}）の往復");
            }
        }
    }

    /// <summary>プロバイダ経由でも対応表どおりの側が Lore へ渡る（sync の既定）。</summary>
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

    /// <summary>
    /// ★ブランチのマージで競合したあとの解決では、入れ替わった側が Lore へ渡ること。
    ///
    /// <para>
    /// 出どころは作業コピー（`.lore/`）へ記録されるので、
    /// 実ファイルを使って「マージ → 解決」の流れをそのまま踏む。
    /// </para>
    /// </summary>
    private static void ResolveAfterBranchMergeSendsFlippedSide()
    {
        using var temp = new TempDir();

        // `.lore/` が無いと印を書けない（作業コピーでない場所では記録しない設計）。
        Directory.CreateDirectory(
            Path.Combine(temp.Path, VersionControlSettings.LORE_METADATA_DIR_NAME));

        var backend = new FakeLoreBackend { WorkingCopyRoot = temp.Path };

        // 1 回目 = マージ前（現在のブランチ名）、2 回目 = マージ後（競合あり）。
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        backend.StatusResults.Add(FakeRows.Status(new[]
        {
            FakeRows.File("a.txt", conflict: true, conflictUnresolved: true),
        }, branchName: "main"));
        using var provider = NewProvider(backend);

        var merge = provider.MergeBranchAsync("feature").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Conflicted, merge.Outcome, "マージの結末");

        // 以降の status は「競合なし」を返す（解決できたことにする）。
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));

        provider.ResolveConflictsAsync(
            new[] { "a.txt" }, ConflictResolutionChoice.KeepMine).GetAwaiter().GetResult();
        Check.Equal(LoreResolveSide.Mine, backend.LastResolveSide,
                    "ブランチのマージでは KeepMine → Lore の mine");
    }

    /// <summary>
    /// マージが片付いたら出どころの印を消し、次の競合では sync の対応表へ戻ること。
    /// 消し忘れると、ありふれた sync の競合で逆の側を採ってしまう。
    /// </summary>
    private static void MergeOriginIsClearedAfterResolve()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(
            Path.Combine(temp.Path, VersionControlSettings.LORE_METADATA_DIR_NAME));

        var backend = new FakeLoreBackend { WorkingCopyRoot = temp.Path };
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        backend.StatusResults.Add(FakeRows.Status(new[]
        {
            FakeRows.File("a.txt", conflict: true, conflictUnresolved: true),
        }, branchName: "main"));
        using var provider = NewProvider(backend);

        provider.MergeBranchAsync("feature").GetAwaiter().GetResult();

        // 印は Lore のメタデータ置き場ではなく SEED のユーザー別状態（cache/editor/vcs/）に置く
        var marker = Path.Combine(
            new[] { temp.Path }
                .Concat(VersionControlSettings.EDITOR_VCS_STATE_DIR_SEGMENTS)
                .Append(LoreMergeOriginStore.FILE_NAME)
                .ToArray());
        Check.True(File.Exists(marker), "競合が出たら印が残る");

        // 解決 → 競合なし → マージのコミットまで進む。
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        provider.ResolveConflictsAsync(
            new[] { "a.txt" }, ConflictResolutionChoice.KeepMine).GetAwaiter().GetResult();

        Check.True(!File.Exists(marker), "解決しきったら印は消える");

        // 印が無い状態では sync の対応表に戻る。
        provider.ResolveConflictsAsync(
            new[] { "a.txt" }, ConflictResolutionChoice.KeepMine).GetAwaiter().GetResult();
        Check.Equal(LoreResolveSide.Theirs, backend.LastResolveSide,
                    "印が無ければ sync の対応表");
    }

    /// <summary>
    /// 「最新を取得」が通ったら、残っていたブランチのマージの印を消すこと。
    /// 進行中のマージが sync 由来へ入れ替わるため。
    /// </summary>
    private static void SyncClearsStaleBranchMergeOrigin()
    {
        using var temp = new TempDir();
        var loreDir = Path.Combine(temp.Path, VersionControlSettings.LORE_METADATA_DIR_NAME);
        Directory.CreateDirectory(loreDir);

        // 印だけを先に置いておく（前回のマージが残っている状況）。
        var store = new LoreMergeOriginStore(temp.Path);
        store.MarkBranchMerge();
        Check.Equal(MergeOrigin.BranchMerge, store.Read(), "書いた印が読める");

        var backend = new FakeLoreBackend { WorkingCopyRoot = temp.Path };
        backend.StatusResults.Add(FakeRows.Status());
        using var provider = NewProvider(backend);

        provider.FetchLatestAsync().GetAwaiter().GetResult();

        Check.Equal(MergeOrigin.Sync, store.Read(), "取得後は sync 扱いへ戻る");
    }

    /// <summary>
    /// ブランチを切り替えたら出どころの印を消すこと。
    /// 前のブランチで始めたマージは「いま進行中のマージ」ではなくなるため、
    /// 残すと移った先の sync の競合で誤った対応表を引く。
    /// </summary>
    private static void BranchSwitchClearsMergeOrigin()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(
            Path.Combine(temp.Path, VersionControlSettings.LORE_METADATA_DIR_NAME));

        var store = new LoreMergeOriginStore(temp.Path);
        store.MarkBranchMerge();

        var backend = new FakeLoreBackend { WorkingCopyRoot = temp.Path };
        using var provider = NewProvider(backend);

        provider.SwitchBranchAsync("other").GetAwaiter().GetResult();
        Check.Equal(MergeOrigin.Sync, store.Read(), "切り替え後は sync 扱いへ戻る");

        // 切り替えに失敗したときは消さない（移れていないので、まだ同じマージの途中）。
        store.MarkBranchMerge();
        backend.BranchSwitchResult = LoreCallResult.Failure(2, new[] { "Branch not found" });
        provider.SwitchBranchAsync("missing").GetAwaiter().GetResult();
        Check.Equal(MergeOrigin.BranchMerge, store.Read(), "失敗したときは印を残す");
    }

    /// <summary>作業コピーでない場所では印を書かず、読みは sync のままになる。</summary>
    private static void MergeOriginStoreIsSilentOutsideWorkingCopy()
    {
        using var temp = new TempDir();   // `.lore/` を作らない

        var store = new LoreMergeOriginStore(temp.Path);
        store.MarkBranchMerge();          // 例外を出さずに諦める

        Check.Equal(MergeOrigin.Sync, store.Read(), "印が無いので sync");
        Check.True(!File.Exists(Path.Combine(
                       temp.Path, VersionControlSettings.LORE_METADATA_DIR_NAME,
                       LoreMergeOriginStore.FILE_NAME)),
                   "ファイルは作られない");

        // ルートが空でも落ちない。
        var empty = new LoreMergeOriginStore(string.Empty);
        empty.MarkBranchMerge();
        empty.Clear();
        Check.Equal(MergeOrigin.Sync, empty.Read(), "ルート未指定でも sync");
    }

    /// <summary>flag_conflict_mine は sync では「リモートを採用」で解決済みと読む。</summary>
    private static void ConflictMineReadsAsTakeRemote()
    {
        var row = FakeRows.File("a.txt", conflict: true, conflictMine: true);
        Check.Equal(FileConflictState.ResolvedTakeRemote,
                    LoreStatusTranslator.ToConflictState(row, MergeOrigin.Sync),
                    "flag_conflict_mine の読み（sync）");

        // ブランチのマージでは逆（mine = 現在のブランチ = 自分の変更を残した）。
        Check.Equal(FileConflictState.ResolvedKeepMine,
                    LoreStatusTranslator.ToConflictState(row, MergeOrigin.BranchMerge),
                    "flag_conflict_mine の読み（branch merge）");
    }

    /// <summary>flag_conflict_theirs は sync では「自分の変更を残す」で解決済みと読む。</summary>
    private static void ConflictTheirsReadsAsKeepMine()
    {
        var row = FakeRows.File("a.txt", conflict: true, conflictTheirs: true);
        Check.Equal(FileConflictState.ResolvedKeepMine,
                    LoreStatusTranslator.ToConflictState(row, MergeOrigin.Sync),
                    "flag_conflict_theirs の読み（sync）");

        Check.Equal(FileConflictState.ResolvedTakeRemote,
                    LoreStatusTranslator.ToConflictState(row, MergeOrigin.BranchMerge),
                    "flag_conflict_theirs の読み（branch merge）");
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

    /// <summary>
    /// 競合フラグだけが立ち、未解決・自動・mine・theirs のどれでもない行は
    /// 「作業コピーの中身のまま解決済み」。
    ///
    /// <para>
    /// 実測（2026-09-19、v0.9.0）: <c>branch merge resolve &lt;path&gt;</c> の直後は
    /// <c>flagConflict=true, flagConflictUnresolved=false, mine=false, theirs=false</c>。
    /// これを「未解決」へ倒すと、マージエディタ／「両方を取り込む」の直後に
    /// 「まだ競合している」と誤判定してマージのコミットが行われない（実機で再現した不具合）。
    /// </para>
    /// </summary>
    private static void ResolvedWithContentIsDetected()
    {
        var row = FakeRows.File("a.txt", staged: true, dirty: false, conflict: true);
        Check.Equal(FileConflictState.ResolvedWithContent,
                    LoreStatusTranslator.ToConflictState(row), "中身のまま解決済み");
        Check.True(!LoreStatusTranslator.ToChangedFile(row).IsUnresolvedConflict,
                   "未解決とは数えない");
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
    //  履歴のメタデータ
    //  （Lore の history は番号とハッシュしか返さない。メッセージ・作者・日時は
    //    revision metadata list を 1 件ずつ引いて補う）
    // ============================================================

    /// <summary>
    /// 実機（Lore v0.9.0 + loreserver）で実際に返ってきたキーの組から
    /// 3 項目を取り出せること。ここが対応表の正しさを固定する要のテスト。
    /// </summary>
    private static void MetadataExtractsRealKeys()
    {
        // 実機の結合テストで観測したそのままの形
        // （branch は SEED が解釈しない種類で返ってくる）。
        var rows = new[]
        {
            new LoreMetadataRow("branch",       LoreMetadataValueKind.Unknown, "", 0UL),
            new LoreMetadataRow("timestamp",    LoreMetadataValueKind.Numeric, "", 1_789_661_083_806UL),
            new LoreMetadataRow("message",      LoreMetadataValueKind.String,  "初期投入", 0UL),
            new LoreMetadataRow("created-by",   LoreMetadataValueKind.String,  "alice@example.com", 0UL),
            new LoreMetadataRow("committed-by", LoreMetadataValueKind.String,  "alice@example.com", 0UL),
        };

        var fields = LoreRevisionMetadataTranslator.Extract(rows);

        Check.Equal("初期投入", fields.Message, "メッセージ");
        Check.Equal("alice@example.com", fields.Author, "作者");
        // 1789661083806 ms → 1789661083 s。
        Check.Equal(1_789_661_083L, fields.UnixTimeSeconds, "コミット時刻（Unix 秒）");
        Check.True(fields.HasAny, "1 項目でも取れている");
    }

    /// <summary>
    /// 作者は「作った人（created-by）」より「コミットした人（committed-by）」を優先する。
    /// amend されたリビジョンで両者が食い違うため、どちらを採るかを固定しておく。
    /// </summary>
    private static void MetadataPrefersCommittedBy()
    {
        var rows = new[]
        {
            new LoreMetadataRow("created-by",   LoreMetadataValueKind.String, "alice@example.com", 0UL),
            new LoreMetadataRow("committed-by", LoreMetadataValueKind.String, "bob@example.com",   0UL),
        };

        Check.Equal("bob@example.com",
                    LoreRevisionMetadataTranslator.Extract(rows).Author,
                    "作者（committed-by が優先される）");

        // 並び順を入れ替えても結果が変わらないこと（メタデータの順序に依存しない）。
        var reversed = new[] { rows[1], rows[0] };
        Check.Equal("bob@example.com",
                    LoreRevisionMetadataTranslator.Extract(reversed).Author,
                    "並び順を変えても同じ");
    }

    /// <summary>
    /// キーは大文字小文字と区切り（<c>_</c> <c>-</c> 空白）を無視して照合する。
    /// Lore 側の綴りが <c>committed_by</c> や <c>committedBy</c> に変わっても拾えるようにするため。
    /// </summary>
    private static void MetadataKeysAreNormalized()
    {
        var rows = new[]
        {
            new LoreMetadataRow("Committed_By", LoreMetadataValueKind.String, "carol", 0UL),
            new LoreMetadataRow("MESSAGE",      LoreMetadataValueKind.String, "大文字キー", 0UL),
        };

        var fields = LoreRevisionMetadataTranslator.Extract(rows);
        Check.Equal("carol",     fields.Author,  "作者");
        Check.Equal("大文字キー", fields.Message, "メッセージ");
    }

    /// <summary>
    /// 知らないキー・知らない値の種類・空の並びでも落ちず、空の結果を返すこと。
    /// Lore の版が上がってキーが増えても壊れないことの保証。
    /// </summary>
    private static void MetadataIgnoresUnknownKeys()
    {
        var rows = new[]
        {
            new LoreMetadataRow("branch",        LoreMetadataValueKind.Unknown, "", 0UL),
            new LoreMetadataRow("some-new-key",  LoreMetadataValueKind.String,  "値", 0UL),
            new LoreMetadataRow("",              LoreMetadataValueKind.String,  "キー無し", 0UL),
            // 文字列の種類なのに中身が空 → 採用しない（空文字で上書きしない）。
            new LoreMetadataRow("message",       LoreMetadataValueKind.String,  "   ", 0UL),
        };

        var fields = LoreRevisionMetadataTranslator.Extract(rows);
        Check.Equal(string.Empty, fields.Message, "メッセージ");
        Check.Equal(string.Empty, fields.Author,  "作者");
        Check.Equal(0L, fields.UnixTimeSeconds,   "コミット時刻");
        Check.True(!fields.HasAny, "何も取れていない");

        // null / 空の並びでも落ちない。
        Check.True(!LoreRevisionMetadataTranslator.Extract(null).HasAny, "null でも落ちない");
        Check.True(!LoreRevisionMetadataTranslator.Extract(Array.Empty<LoreMetadataRow>()).HasAny,
                   "空でも落ちない");
    }

    /// <summary>
    /// 時刻の単位（秒・ミリ・マイクロ・ナノ）を桁から判定して秒へ直すこと。
    /// Lore v0.9.0 は **ミリ秒** だが、版によって変わり得るので単位に依存しない。
    /// </summary>
    private static void MetadataNormalizesTimeUnits()
    {
        const long SECONDS = 1_789_661_083L;

        Check.Equal(SECONDS,
                    LoreRevisionMetadataTranslator.ToPlausibleUnixSeconds((ulong)SECONDS),
                    "秒そのまま");
        Check.Equal(SECONDS,
                    LoreRevisionMetadataTranslator.ToPlausibleUnixSeconds((ulong)SECONDS * 1_000UL),
                    "ミリ秒（実機の形式）");
        Check.Equal(SECONDS,
                    LoreRevisionMetadataTranslator.ToPlausibleUnixSeconds((ulong)SECONDS * 1_000_000UL),
                    "マイクロ秒");
        Check.Equal(SECONDS,
                    LoreRevisionMetadataTranslator.ToPlausibleUnixSeconds((ulong)SECONDS * 1_000_000_000UL),
                    "ナノ秒");
    }

    /// <summary>
    /// 現実的な日時にならない値は 0（不明）にして、でたらめな日付を出さないこと。
    /// 1970 年や遠い未来の日付が履歴に並ぶ方が、空欄より害が大きい。
    /// </summary>
    private static void MetadataRejectsImplausibleTime()
    {
        Check.Equal(0L, LoreRevisionMetadataTranslator.ToPlausibleUnixSeconds(0UL), "0 は不明");
        Check.Equal(0L, LoreRevisionMetadataTranslator.ToPlausibleUnixSeconds(12UL), "小さすぎる値");
        Check.Equal(0L, LoreRevisionMetadataTranslator.ToPlausibleUnixSeconds(ulong.MaxValue),
                    "long に収まらない値");
    }

    /// <summary>
    /// 履歴の各行がメタデータで埋められること（プロバイダ経由の結合）。
    /// </summary>
    private static void HistoryIsEnrichedWithMetadata()
    {
        var backend = new FakeLoreBackend
        {
            HistoryResult = new LoreRowsResult<LoreRevisionRow>(
                LoreCallResult.Success,
                new[]
                {
                    new LoreRevisionRow(2, "rev2", "", "", 0),
                    new LoreRevisionRow(1, "rev1", "", "", 0),
                }),
        };

        backend.RevisionMetadataResults["rev2"] = new LoreRowsResult<LoreMetadataRow>(
            LoreCallResult.Success,
            new[]
            {
                new LoreMetadataRow("message",      LoreMetadataValueKind.String,  "2 件目", 0UL),
                new LoreMetadataRow("committed-by", LoreMetadataValueKind.String,  "alice",  0UL),
                new LoreMetadataRow("timestamp",    LoreMetadataValueKind.Numeric, "", 1_789_661_083_806UL),
            });

        using var provider = NewProvider(backend);
        var result = provider.GetHistoryAsync().GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome, "結末");
        Check.Equal(2, result.Value!.Count, "件数");
        Check.Equal("2 件目", result.Value[0].Message, "1 行目のメッセージ");
        Check.Equal("alice",  result.Value[0].Author,  "1 行目の作者");
        Check.True(result.Value[0].TimestampUtc != default, "1 行目の日時が入る");

        // メタデータを持たないリビジョンも一覧から消えない（番号だけ残る）。
        Check.Equal(1UL, result.Value[1].Number, "2 行目のリビジョン番号");
        Check.Equal(string.Empty, result.Value[1].Message, "2 行目のメッセージは空");
    }

    /// <summary>
    /// メタデータは 1 リビジョンにつき 1 往復かかるため、設定の上限で打ち切ること。
    /// 上限を超えた行も一覧からは消えない。
    /// </summary>
    private static void HistoryMetadataIsLimited()
    {
        const int LIMIT = 2;

        var rows = new List<LoreRevisionRow>();
        for (var i = 0; i < 5; i++) rows.Add(new LoreRevisionRow((ulong)(5 - i), $"rev{5 - i}", "", "", 0));

        var backend = new FakeLoreBackend
        {
            HistoryResult = new LoreRowsResult<LoreRevisionRow>(LoreCallResult.Success, rows),
        };

        var settings = new VersionControlSettings(historyMetadataLimit: LIMIT);
        using var provider = NewProvider(backend, settings);

        var result = provider.GetHistoryAsync().GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome, "結末");
        Check.Equal(5, result.Value!.Count, "履歴の件数は減らない");
        Check.Equal(LIMIT, backend.RevisionMetadataRequests.Count, "メタデータを引いた回数");
        Check.Equal("rev5", backend.RevisionMetadataRequests[0], "新しい方から引く");
    }

    /// <summary>
    /// メタデータの取得が失敗しても履歴そのものは返ること。
    /// 「1 件引けなかったから履歴全体が出ない」より「その行だけ空欄」の方が良い。
    /// </summary>
    private static void HistorySurvivesMetadataFailure()
    {
        var backend = new FakeLoreBackend
        {
            HistoryResult = new LoreRowsResult<LoreRevisionRow>(
                LoreCallResult.Success,
                new[] { new LoreRevisionRow(1, "rev1", "", "", 0) }),
            DefaultRevisionMetadataResult = LoreRowsResult<LoreMetadataRow>.FromFailure(
                LoreCallResult.Failure(-1, new[] { "metadata not found" })),
        };

        using var provider = NewProvider(backend);
        var result = provider.GetHistoryAsync().GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome, "結末");
        Check.Equal(1, result.Value!.Count, "件数");
        Check.Equal(1UL, result.Value[0].Number, "リビジョン番号は残る");
        Check.Equal(string.Empty, result.Value[0].Message, "メッセージは空のまま");
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
    //  ブランチのマージ
    //
    //  ★ここで固定したいのは「競合の有無を戻り値ではなく status で見ている」こと。
    //    Lore の branch merge は sync と同じく、競合していても成功を返すことがあり、
    //    逆に失敗を返していても作業コピーには競合が残っていることがある。
    //    どちらの向きで取り違えても、利用者は競合に気づかないまま次の操作へ進む。
    // ============================================================

    /// <summary>競合が無ければ成功し、「送信で共有される」ことを伝える。</summary>
    private static void MergeWithoutConflictsSucceeds()
    {
        var backend = new FakeLoreBackend();
        // 1 回目 = マージ前（現在のブランチ名を読む）、2 回目 = マージ後（競合の確認）。
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        backend.StatusResults.Add(FakeRows.Status(branchName: "main", revisionNumber: 7));
        using var provider = NewProvider(backend);

        var result = provider.MergeBranchAsync("feature").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome, "結末");
        Check.Equal(1, backend.BranchMergeCallCount, "Lore の merge が 1 回だけ呼ばれる");
        Check.Equal("feature", backend.LastBranchMergeSource, "取り込み元");
        Check.Equal("feature", result.Value!.SourceBranch, "明細の取り込み元");
        Check.Equal(7UL, result.Value.RevisionNumber, "明細のリビジョン番号");
        Check.True(result.Message.Contains("送信", StringComparison.Ordinal),
                   $"送信を促す文言が入る（実際: {result.Message}）");
    }

    /// <summary>戻り値が成功でも、status に競合が出ていれば Conflicted。</summary>
    private static void MergeSuccessWithConflictsIsConflicted()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        backend.StatusResults.Add(FakeRows.Status(new[]
        {
            FakeRows.File("a.txt", conflict: true, conflictUnresolved: true),
        }, branchName: "main"));
        using var provider = NewProvider(backend);

        var result = provider.MergeBranchAsync("feature").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Conflicted, result.Outcome, "結末");
        Check.Equal(1, result.Value!.Conflicts.Count, "競合の件数");
        Check.Equal("a.txt", result.Value.Conflicts[0].Path, "競合したファイル");
    }

    /// <summary>
    /// Lore が失敗を返していても、競合が出ているなら Conflicted として扱う。
    /// ここを Failed にすると、利用者は競合が残ったまま「失敗した」と思って放置する。
    /// </summary>
    private static void MergeFailureWithConflictsIsConflicted()
    {
        var backend = new FakeLoreBackend
        {
            BranchMergeResult = LoreCallResult.Failure(
                2, new[] { "Merge has unresolved conflicts" }),
        };
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        backend.StatusResults.Add(FakeRows.Status(new[]
        {
            FakeRows.File("a.txt", conflict: true, conflictUnresolved: true),
        }, branchName: "main"));
        using var provider = NewProvider(backend);

        var result = provider.MergeBranchAsync("feature").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Conflicted, result.Outcome, "結末");
        Check.Equal(1, result.Value!.Conflicts.Count, "競合の件数");
    }

    /// <summary>分岐で弾かれたら「先に最新を取得」と案内できる NeedsSync。</summary>
    private static void MergeRejectionBecomesNeedsSync()
    {
        var backend = new FakeLoreBackend
        {
            BranchMergeResult = LoreCallResult.Failure(
                2, new[] { "Branch history is divergent" }),
        };
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        using var provider = NewProvider(backend);

        var result = provider.MergeBranchAsync("feature").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.NeedsSync, result.Outcome, "結末");
    }

    /// <summary>サーバへ届かないときは「異常」ではなく RequiresConnection。</summary>
    private static void MergeOfflineRequiresConnection()
    {
        var backend = new FakeLoreBackend
        {
            BranchMergeResult = LoreCallResult.Failure(
                2, new[] { "Cannot connect to remote server" }),
        };
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        using var provider = NewProvider(backend);

        var result = provider.MergeBranchAsync("feature").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.RequiresConnection, result.Outcome, "結末");
    }

    /// <summary>現在のブランチ自身の取り込みは意味が無いので Lore へ渡さない。</summary>
    private static void MergeIntoSelfIsRejected()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        using var provider = NewProvider(backend);

        var result = provider.MergeBranchAsync("main").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Failed, result.Outcome, "結末");
        Check.Equal(0, backend.BranchMergeCallCount, "Lore の merge は呼ばれない");
        Check.Equal(VersionControlMessages.BRANCH_MERGE_SELF, result.Message, "理由の文言");
    }

    /// <summary>名前が空なら status すら引かずに弾く。</summary>
    private static void MergeRequiresSourceName()
    {
        var backend = new FakeLoreBackend();
        using var provider = NewProvider(backend);

        var result = provider.MergeBranchAsync("   ").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Failed, result.Outcome, "結末");
        Check.Equal(0, backend.StatusCallCount, "Lore を一度も呼ばない");
        Check.Equal(0, backend.BranchMergeCallCount, "Lore の merge も呼ばれない");
    }

    /// <summary>
    /// 自動コミットのメッセージは履歴に残る。
    /// 「どちらをどちらへ取り込んだか」が後から読めること。
    /// </summary>
    private static void MergeCommitMessageNamesBothBranches()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        using var provider = NewProvider(backend);

        provider.MergeBranchAsync("feature").GetAwaiter().GetResult();

        Check.True(backend.LastBranchMergeMessage.Contains("feature", StringComparison.Ordinal),
                   $"取り込み元が入る（実際: {backend.LastBranchMergeMessage}）");
        Check.True(backend.LastBranchMergeMessage.Contains("main", StringComparison.Ordinal),
                   $"取り込み先が入る（実際: {backend.LastBranchMergeMessage}）");
    }

    // ============================================================
    //  ブランチの削除（アーカイブ）
    //
    //  ★Lore の archive は取り消せない。守るべきブランチ（現在・既定）を
    //    **プロバイダ側でも**弾いていることを固定する。UI の絞り込みだけに頼ると、
    //    UI 以外から呼ばれたときに消えてしまう。
    // ============================================================

    /// <summary>削除できるブランチなら Lore へそのまま名前が渡る。</summary>
    private static void ArchivePassesNameToLore()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        using var provider = NewProvider(backend);

        var result = provider.ArchiveBranchAsync("old-feature").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome, "結末");
        Check.Equal(1, backend.BranchArchiveCallCount, "Lore の archive が 1 回だけ呼ばれる");
        Check.Equal("old-feature", backend.LastBranchArchiveName, "渡された名前");
    }

    /// <summary>現在のブランチは削除させない（足場を消す操作になる）。</summary>
    private static void ArchiveCurrentBranchIsRejected()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(branchName: "feature"));
        using var provider = NewProvider(backend);

        var result = provider.ArchiveBranchAsync("feature").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Failed, result.Outcome, "結末");
        Check.Equal(0, backend.BranchArchiveCallCount, "Lore の archive は呼ばれない");
        Check.Equal(VersionControlMessages.BRANCH_ARCHIVE_CURRENT, result.Message, "理由の文言");
    }

    /// <summary>既定ブランチ（main）は削除させない。</summary>
    private static void ArchiveDefaultBranchIsRejected()
    {
        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(branchName: "feature"));
        using var provider = NewProvider(backend);

        var result = provider.ArchiveBranchAsync("main").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Failed, result.Outcome, "結末");
        Check.Equal(0, backend.BranchArchiveCallCount, "Lore の archive は呼ばれない");
    }

    /// <summary>
    /// 既定ブランチ名は設定値。"main" 以外を既定にしているリポジトリでも守れること。
    /// </summary>
    private static void ArchiveUsesConfiguredDefaultBranch()
    {
        var settings = new VersionControlSettings(defaultBranchName: "trunk");

        var backend = new FakeLoreBackend();
        backend.StatusResults.Add(FakeRows.Status(branchName: "feature"));
        using var provider = NewProvider(backend, settings);

        var protectedResult = provider.ArchiveBranchAsync("trunk").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Failed, protectedResult.Outcome, "trunk の結末");
        Check.Equal(0, backend.BranchArchiveCallCount, "trunk では Lore を呼ばない");

        // 設定を差し替えたので、"main" はもう守られない（普通のブランチとして消せる）。
        var mainResult = provider.ArchiveBranchAsync("main").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, mainResult.Outcome, "main の結末");
        Check.Equal(1, backend.BranchArchiveCallCount, "main では Lore を呼ぶ");
    }

    /// <summary>名前が空なら status すら引かずに弾く。</summary>
    private static void ArchiveRequiresName()
    {
        var backend = new FakeLoreBackend();
        using var provider = NewProvider(backend);

        var result = provider.ArchiveBranchAsync("").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Failed, result.Outcome, "結末");
        Check.Equal(0, backend.StatusCallCount, "Lore を一度も呼ばない");
    }

    /// <summary>Lore 側が失敗したらそのまま Failed。</summary>
    private static void ArchiveFailureIsFailed()
    {
        var backend = new FakeLoreBackend
        {
            BranchArchiveResult = LoreCallResult.Failure(2, new[] { "Branch not found" }),
        };
        backend.StatusResults.Add(FakeRows.Status(branchName: "main"));
        using var provider = NewProvider(backend);

        var result = provider.ArchiveBranchAsync("missing").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Failed, result.Outcome, "結末");
        Check.Equal(VersionControlMessages.BRANCH_ARCHIVE_FAILED, result.Message, "文言");
    }

    // ============================================================
    //  ブランチ操作の除外規則
    //
    //  ★パネルのダイアログとプロバイダの拒否が **同じ規則** を使っていること。
    //    ずれると「一覧に出るのに必ず失敗する」「一覧に出ないのに実行できる」が生まれる。
    // ============================================================

    /// <summary>マージ元の候補から現在のブランチが外れる。</summary>
    private static void MergeCandidatesExcludeCurrent()
    {
        var branches = new[]
        {
            new BranchInfo("main",    true,  true, true),
            new BranchInfo("feature", false, true, false),
        };

        var candidates = BranchOperationRules.MergeSourceCandidates(branches, "main");

        Check.Equal(1, candidates.Count, "候補の件数");
        Check.Equal("feature", candidates[0], "残る候補");
    }

    /// <summary>削除の候補から現在のブランチと既定ブランチが外れる。</summary>
    private static void ArchiveCandidatesExcludeProtected()
    {
        var branches = new[]
        {
            new BranchInfo("main",    false, true, true),
            new BranchInfo("feature", true,  true, false),
            new BranchInfo("old",     false, true, false),
        };

        var candidates = BranchOperationRules.ArchiveCandidates(branches, "feature", "main");

        Check.Equal(1, candidates.Count, "候補の件数");
        Check.Equal("old", candidates[0], "残る候補");
    }

    /// <summary>
    /// 守るべきブランチの判定は大文字小文字を区別しない。
    /// "Main" を消せてしまうより、消せない側へ倒す方が損失が小さいため。
    /// </summary>
    private static void BranchRulesIgnoreCase()
    {
        Check.True(!BranchOperationRules.CheckArchive("MAIN", "feature", "main").Allowed,
                   "MAIN は既定ブランチとして守られる");
        Check.True(!BranchOperationRules.CheckArchive("Feature", "feature", "main").Allowed,
                   "Feature は現在のブランチとして守られる");
        Check.True(!BranchOperationRules.CheckMergeSource("MAIN", "main").Allowed,
                   "MAIN は自分自身として弾かれる");
    }

    /// <summary>
    /// Lore は同名ブランチを LOCAL / REMOTE の 2 行で返すことがある。
    /// 候補を作る段階で 1 件へ畳み、空の名前は落とす。並び順は最初に現れた順を保つ。
    /// </summary>
    private static void BranchCandidatesAreDeduplicated()
    {
        var branches = new[]
        {
            new BranchInfo("zeta",    false, true,  false),
            new BranchInfo("",        false, true,  false),
            new BranchInfo("alpha",   false, true,  false),
            new BranchInfo("ZETA",    false, false, true),
        };

        var candidates = BranchOperationRules.MergeSourceCandidates(branches, "main");

        Check.Equal(2, candidates.Count, "候補の件数");
        Check.Equal("zeta",  candidates[0], "1 件目（最初に現れた綴り）");
        Check.Equal("alpha", candidates[1], "2 件目");
    }

    /// <summary>
    /// 現在のブランチ名が分からない（状態が未取得）ときに候補を空にしない。
    /// ここで全部弾くと、オフライン直後に何も選べなくなる。
    /// </summary>
    private static void BranchCandidatesSurviveUnknownCurrent()
    {
        var branches = new[]
        {
            new BranchInfo("main",    false, true, true),
            new BranchInfo("feature", false, true, false),
        };

        var merge = BranchOperationRules.MergeSourceCandidates(branches, string.Empty);
        Check.Equal(2, merge.Count, "マージ候補は減らない");

        // 削除の方は「既定ブランチ」だけは名前で守れるので、そこは残り続ける。
        var archive = BranchOperationRules.ArchiveCandidates(branches, string.Empty, "main");
        Check.Equal(1, archive.Count, "削除候補は既定ブランチだけ外れる");
        Check.Equal("feature", archive[0], "残る候補");
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
