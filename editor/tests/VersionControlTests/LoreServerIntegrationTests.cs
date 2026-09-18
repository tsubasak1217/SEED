// ============================================================
//  LoreServerIntegrationTests.cs — 本物の Lore サーバを通す結合テスト
//
//  【実行条件】
//  環境変数 SEED_LORE_TEST_SERVER が設定されているときだけ実行する。
//  サーバの起動に数秒かかり、証明書生成に openssl が要るため、
//  日常のビルド確認では回さない。
//
//  【何を確かめたいのか】
//  純粋ロジックのテストは「Lore がこう返したらこう判断する」を固定するが、
//  「Lore が本当にそう返すのか」は確かめられない。ここで確かめるのは主に:
//    ・resolve mine / theirs の向き（**逆だと利用者の作業が消える**）
//    ・push が分岐で本当に弾かれるか（NeedsSync の前提）
//    ・sync が競合しても成功を返すこと（Conflicted 判定の前提）
//    ・stage move で履歴が繋がること
//    ・ロックの所有者が実際に何になるか（認証なしなら <unknown>）
//
//  【テスト間の依存について】
//  サーバの起動は重いので 1 つの fixture を共有する。そのため各テストは
//  前のテストが作った状態の上で動く（順序に意味がある）。最後に登録した
//  後始末テストが必ずサーバを止める（途中で失敗してもランナーは続行するため）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LoreVcs;
using LoreVcs.Types.Args;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Lore;
using SEEDEditor.VersionControl.Lore.Backend;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Scheduling;
using SpriteRigTests;
using LoreApi = global::LoreVcs.Lore;

namespace SEEDEditor.Tests.VersionControl;

/// <summary>
/// 実サーバを起動して行う結合テスト。
/// </summary>
public static class LoreServerIntegrationTests
{
    /// <summary>テスト用リポジトリの名前。</summary>
    private const string REPOSITORY_NAME = "SeedVcsIntegration";

    /// <summary>1 人目の identity。</summary>
    private const string IDENTITY_A = "alice@example.com";

    /// <summary>2 人目の identity。</summary>
    private const string IDENTITY_B = "bob@example.com";

    /// <summary>競合を起こす共有ファイル（リポジトリ相対）。</summary>
    private const string SHARED_FILE = "shared.txt";

    /// <summary>改名テストで使うファイル（リポジトリ相対）。</summary>
    private const string RENAME_SOURCE = "rename_me.txt";

    /// <summary>改名後のファイル（リポジトリ相対）。</summary>
    private const string RENAME_TARGET = "renamed.txt";

    /// <summary>ロックの取得・照会に使うファイル（リポジトリ相対）。</summary>
    private const string LOCK_FILE = "locked.txt";

    /// <summary>フォルダ移動テストの移動元フォルダ（リポジトリ相対）。</summary>
    private const string FOLDER_MOVE_SOURCE = "folder_src";

    /// <summary>フォルダ移動テストの移動先フォルダ（リポジトリ相対）。</summary>
    private const string FOLDER_MOVE_TARGET = "folder_dst";

    /// <summary>フォルダ移動テストで中に置くファイル 1。</summary>
    private const string FOLDER_MOVE_FILE_A = "a.txt";

    /// <summary>フォルダ移動テストで中に置くファイル 2。</summary>
    private const string FOLDER_MOVE_FILE_B = "b.txt";

    /// <summary>履歴メタデータのテストで作るファイル（リポジトリ相対）。</summary>
    private const string HISTORY_PROBE_FILE = "history_probe.txt";

    /// <summary>履歴メタデータのテストで使う、他と紛れないコミットメッセージ。</summary>
    private const string HISTORY_PROBE_MESSAGE = "履歴メタデータの確認 probe-8f2a";

    /// <summary>ブランチのマージ（競合なし）で使うブランチ名。</summary>
    private const string MERGE_BRANCH = "merge-source";

    /// <summary>ブランチのマージ（競合なし）で作るファイル（リポジトリ相対）。</summary>
    private const string MERGE_FILE = "branch_merge.txt";

    /// <summary>ブランチのマージ（競合あり）で使うブランチ名。</summary>
    private const string CONFLICT_BRANCH = "conflict-source";

    /// <summary>ブランチのマージ（競合あり）で使うファイル（リポジトリ相対）。</summary>
    private const string CONFLICT_FILE = "branch_conflict.txt";

    /// <summary>削除（アーカイブ）のテストで作る、消すためだけのブランチ名。</summary>
    private const string ARCHIVE_BRANCH = "archive-me";

    /// <summary>
    /// 分岐の起点になるブランチ名（リポジトリ作成時の既定ブランチ）。
    /// 名前を決め打ちにせず、最初のブランチテストで実際の値を読んで入れる。
    /// </summary>
    private static string _baseBranch = string.Empty;

    /// <summary>失敗時に生のメタデータを出す件数の上限（全部出すと読めない）。</summary>
    private const int METADATA_DUMP_LIMIT = 3;

    // ── 共有状態（テスト間で持ち回る）────────────────────────

    /// <summary>起動中のサーバ。</summary>
    private static LoreServerFixture? _server;

    /// <summary>1 人目の作業コピー。</summary>
    private static LoreProvider? _providerA;

    /// <summary>2 人目の作業コピー。</summary>
    private static LoreProvider? _providerB;

    /// <summary>1 人目の作業コピーのルート。</summary>
    private static string _dirA = string.Empty;

    /// <summary>2 人目の作業コピーのルート。</summary>
    private static string _dirB = string.Empty;

    /// <summary>テストをランナーへ登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("[結合] サーバを起動して 2 つの作業コピーを用意する", SetUp);
        harness.Add("[結合] 送信（stage + commit + push）が通る",          SubmitInitial);
        harness.Add("[結合] もう一方が最新を取得できる",                    FetchOnSecondCopy);
        harness.Add("[結合] 同じ行を変えた送信は NeedsSync で弾かれる",     ConflictingSubmitNeedsSync);
        harness.Add("[結合] 最新を取得すると競合が報告される",              FetchReportsConflict);
        harness.Add("[結合] 「自分の変更を残す」でローカルの内容が残る",     ResolveKeepMineKeepsLocal);
        harness.Add("[結合] 解決後の送信が通る",                            SubmitAfterResolve);
        harness.Add("[結合] 「リモートを採用」でリモートの内容になる",       ResolveTakeRemoteTakesRemote);
        harness.Add("[結合] ロックの取得・照会・解放ができる",              LockRoundTrip);
        harness.Add("[結合] 改名は移動として記録される",                    RenameIsRecordedAsMove);
        harness.Add("[結合] フォルダの移動も移動として記録される",          FolderMoveIsRecordedAsMove);
        harness.Add("[結合] 履歴にメッセージ・作者・日時が入る",            HistoryCarriesMetadata);
        harness.Add("[結合] 別ブランチの変更をマージで取り込める",          MergeBranchBringsChanges);
        harness.Add("[結合] マージの競合で「自分の変更を残す」は現ブランチ側",
                                                                           MergeConflictKeepMineKeepsCurrentBranch);
        harness.Add("[結合] マージの競合で「リモートを採用」は取り込み元側",
                                                                           MergeConflictTakeRemoteTakesSourceBranch);
        harness.Add("[結合] 削除（アーカイブ）したブランチは一覧に出ない",  ArchiveBranchHidesItFromList);
        harness.Add("[結合] 現在のブランチは削除（アーカイブ）できない",    ArchiveCurrentBranchIsRefused);
        harness.Add("[結合] サーバを停止して後始末する",                    TearDown);
    }

    // ============================================================
    //  準備・後始末
    // ============================================================

    /// <summary>サーバを起動し、リポジトリの作成とクローンまで済ませる。</summary>
    private static void SetUp()
    {
        _server = new LoreServerFixture();

        _dirA = _server.CreateWorkingCopyDir("alice");
        _dirB = _server.CreateWorkingCopyDir("bob");

        var url = LoreServerFixture.RepositoryUrl(REPOSITORY_NAME);

        // リポジトリの作成とクローンは「エディタが行う操作」ではないので
        // プロバイダ境界には無い。ここだけ LoreVcs を直接呼ぶ。
        CreateRepository(_dirA, url, IDENTITY_A);
        CloneRepository(_dirB, url, IDENTITY_B);

        _providerA = NewProvider(_dirA);
        _providerB = NewProvider(_dirB);

        Check.True(VersionControlPaths.IsLoreWorkingCopy(_dirA), "A が作業コピーになっている");
        Check.True(VersionControlPaths.IsLoreWorkingCopy(_dirB), "B が作業コピーになっている");
        Check.Equal(IDENTITY_A, _providerA!.Identity, "A の identity");
        // `.lore/config.toml` の remote_url にはリポジトリ名は入らず、
        // サーバのアドレスまで（例 "lore://127.0.0.1:41357"）が記録される。
        Check.True(_providerA.RemoteUrl.StartsWith(
                       $"lore://{LoreServerFixture.HOST}:{LoreServerFixture.PORT_QUIC_GRPC}",
                       StringComparison.Ordinal),
                   $"A のリモート URL（実際: '{_providerA.RemoteUrl}'）");
    }

    /// <summary>サーバと作業コピーを片付ける。</summary>
    private static void TearDown()
    {
        try { _providerA?.Dispose(); } catch { /* 後始末の失敗は無視 */ }
        try { _providerB?.Dispose(); } catch { /* 後始末の失敗は無視 */ }
        _providerA = null;
        _providerB = null;

        try { _server?.Dispose(); } catch { /* 後始末の失敗は無視 */ }
        _server = null;
    }

    // ============================================================
    //  シナリオ
    // ============================================================

    /// <summary>A が初期ファイルを送信する。</summary>
    private static void SubmitInitial()
    {
        var provider = Require(_providerA);

        WriteLines(_dirA, SHARED_FILE, "line1", "line2", "line3");
        WriteLines(_dirA, RENAME_SOURCE, "rename target");
        WriteLines(_dirA, LOCK_FILE, "lock target");

        var result = provider.SubmitAsync("初期投入").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome,
                    $"送信の結末（{result.Message} / {string.Join(" | ", result.Details)}）");
        Check.True(result.Value!.FileCount > 0, "送信件数が 1 件以上");

        // 送るものが無ければ NothingToDo（空コミットを作らない）。
        var again = provider.SubmitAsync("2 回目").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.NothingToDo, again.Outcome, "2 回目の送信の結末");
    }

    /// <summary>B が最新を取得してファイルを受け取る。</summary>
    private static void FetchOnSecondCopy()
    {
        var provider = Require(_providerB);

        var result = provider.FetchLatestAsync().GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, result.Outcome,
                    $"取得の結末（{result.Message} / {string.Join(" | ", result.Details)}）");

        Check.True(File.Exists(Path.Combine(_dirB, SHARED_FILE)), "共有ファイルが届いている");
    }

    /// <summary>
    /// B → A の順に同じ行を変えると、A の送信が NeedsSync で弾かれる。
    /// これが「push が分岐で弾かれる」ことの実機確認。
    /// </summary>
    private static void ConflictingSubmitNeedsSync()
    {
        var providerA = Require(_providerA);
        var providerB = Require(_providerB);

        // B が先に送る。
        WriteLines(_dirB, SHARED_FILE, "bob-change", "line2", "line3");
        Notify(providerB, _dirB, SHARED_FILE);
        var submitB = providerB.SubmitAsync("B の変更").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, submitB.Outcome,
                    $"B の送信（{submitB.Message} / {string.Join(" | ", submitB.Details)}）");

        // A は B の変更を知らないまま同じ行を変えて送る。
        WriteLines(_dirA, SHARED_FILE, "alice-change", "line2", "line3");
        Notify(providerA, _dirA, SHARED_FILE);
        var submitA = providerA.SubmitAsync("A の変更").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.NeedsSync, submitA.Outcome,
                    $"A の送信の結末（{submitA.Message} / {string.Join(" | ", submitA.Details)}）");
        Check.True(submitA.Value!.CommittedButNotPushed, "コミットは手元に残っている");
    }

    /// <summary>
    /// A が最新を取得すると競合が報告される。
    /// Lore の sync は競合しても成功を返すので、ここが「状態のフラグで判定できている」
    /// ことの実機確認になる。
    /// </summary>
    private static void FetchReportsConflict()
    {
        var provider = Require(_providerA);

        var result = provider.FetchLatestAsync().GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Conflicted, result.Outcome,
                    $"取得の結末（{result.Message} / {string.Join(" | ", result.Details)}）");
        Check.True(result.Value!.Conflicts.Any(c => c.Path.EndsWith(SHARED_FILE, StringComparison.OrdinalIgnoreCase)),
                   $"共有ファイルが競合に含まれる（{string.Join(", ", result.Value.Conflicts.Select(c => c.Path))}）");
    }

    /// <summary>
    /// ★このテストが今回の実装で一番重要。
    /// 「自分の変更を残す」を選んだら、**ローカル（A）の内容**が残ること。
    /// Lore の resolve mine / theirs は意味が逆なので、対応表を取り違えていると
    /// ここで相手の内容になり、利用者の作業が黙って消える。
    /// </summary>
    private static void ResolveKeepMineKeepsLocal()
    {
        var provider = Require(_providerA);

        var result = provider.ResolveConflictsAsync(
            new[] { SHARED_FILE }, ConflictResolutionChoice.KeepMine).GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome,
                    $"解決の結末（{result.Message} / {string.Join(" | ", result.Details)}）");

        var content = File.ReadAllText(Path.Combine(_dirA, SHARED_FILE));
        Check.True(content.Contains("alice-change", StringComparison.Ordinal),
                   $"ローカル（自分）の内容が残る。実際の内容: {Summarize(content)}");
        Check.True(!content.Contains("bob-change", StringComparison.Ordinal),
                   $"リモートの内容で上書きされていない。実際の内容: {Summarize(content)}");

        // 競合が残っていないこと。
        var status = provider.GetStatusAsync(StatusRefreshMode.ScanOffline)
                             .GetAwaiter().GetResult();
        Check.True(!status.Value!.HasUnresolvedConflicts, "未解決の競合が残っていない");
    }

    /// <summary>
    /// 解決後の送信が通る（マージのコミットが手元に取り残されない）。
    /// </summary>
    private static void SubmitAfterResolve()
    {
        var provider = Require(_providerA);

        var result = provider.SubmitAsync("競合解決後の送信").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome,
                    $"送信の結末（{result.Message} / {string.Join(" | ", result.Details)}）");
    }

    /// <summary>
    /// 「リモートを採用」を選んだら、**リモート（相手）の内容**になること。
    /// KeepMine と対称に確かめることで、対応表が両方向とも正しいことを固定する。
    /// </summary>
    private static void ResolveTakeRemoteTakesRemote()
    {
        var providerA = Require(_providerA);
        var providerB = Require(_providerB);

        // B は A の解決結果を取り込んでから、同じ行を変えて送る。
        var fetchB = providerB.FetchLatestAsync().GetAwaiter().GetResult();
        Check.True(fetchB.Outcome is VersionControlOutcome.Success
                                  or VersionControlOutcome.Conflicted,
                   $"B の取得（{fetchB.Message}）");
        if (fetchB.Outcome == VersionControlOutcome.Conflicted)
        {
            // B 側に競合が出ていたら、ここではリモート（A の結果）を採って揃える。
            var paths = fetchB.Value!.Conflicts.Select(c => c.Path).ToArray();
            providerB.ResolveConflictsAsync(paths, ConflictResolutionChoice.TakeRemote)
                     .GetAwaiter().GetResult();
            providerB.SubmitAsync("B: 取り込み").GetAwaiter().GetResult();
        }

        WriteLines(_dirB, SHARED_FILE, "bob-second", "line2", "line3");
        Notify(providerB, _dirB, SHARED_FILE);
        var submitB = providerB.SubmitAsync("B の 2 回目").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, submitB.Outcome,
                    $"B の送信（{submitB.Message} / {string.Join(" | ", submitB.Details)}）");

        // A も同じ行を変えて競合させる。
        WriteLines(_dirA, SHARED_FILE, "alice-second", "line2", "line3");
        Notify(providerA, _dirA, SHARED_FILE);
        var submitA = providerA.SubmitAsync("A の 2 回目").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.NeedsSync, submitA.Outcome,
                    $"A の送信の結末（{submitA.Message}）");

        var fetchA = providerA.FetchLatestAsync().GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Conflicted, fetchA.Outcome,
                    $"A の取得の結末（{fetchA.Message}）");

        var resolve = providerA.ResolveConflictsAsync(
            new[] { SHARED_FILE }, ConflictResolutionChoice.TakeRemote).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, resolve.Outcome,
                    $"解決の結末（{resolve.Message} / {string.Join(" | ", resolve.Details)}）");

        var content = File.ReadAllText(Path.Combine(_dirA, SHARED_FILE));
        Check.True(content.Contains("bob-second", StringComparison.Ordinal),
                   $"リモート（相手）の内容になる。実際の内容: {Summarize(content)}");
        Check.True(!content.Contains("alice-second", StringComparison.Ordinal),
                   $"自分の内容は採られていない。実際の内容: {Summarize(content)}");

        providerA.SubmitAsync("A: リモート採用後の送信").GetAwaiter().GetResult();
    }

    /// <summary>
    /// ロックの取得 → 照会 → 解放が通ること。
    /// 所有者が <c>&lt;unknown&gt;</c> になる構成（サーバ認証なし）でも
    /// 結果が「不明」として区別できることを確かめる。
    /// </summary>
    private static void LockRoundTrip()
    {
        var provider = Require(_providerA);
        var locks = provider.Locks;

        var acquire = locks.AcquireAsync(new[] { LOCK_FILE }).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, acquire.Outcome,
                    $"取得の結末（{acquire.Message} / {string.Join(" | ", acquire.Details)}）");
        Check.Equal(1, acquire.Value!.Count, "結果の件数");

        // サーバ認証が無い構成では所有者が <unknown> になり得る。
        // 「取得できた」と「誰かが持っているが不明」のどちらかであればよく、
        // 「他人が持っている」と誤判定していないことを確かめる。
        var outcome = acquire.Value[0].Outcome;
        Check.True(outcome is LockAcquireOutcome.Acquired
                            or LockAcquireOutcome.AlreadyMine
                            or LockAcquireOutcome.HeldByUnknown,
                   $"取得結果が想定内（実際: {outcome} / 所有者 '{acquire.Value[0].Lock.Owner}'）");

        // 照会は必ず「照会したパスと同じ件数」を返す。
        var status = locks.GetStatusAsync(new[] { LOCK_FILE, SHARED_FILE })
                          .GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, status.Outcome,
                    $"照会の結末（{status.Message} / {string.Join(" | ", status.Details)}）");
        Check.Equal(2, status.Value!.Count, "照会結果の件数");
        Check.Equal(LOCK_FILE,  status.Value[0].Path, "1 件目のパス");
        Check.Equal(SHARED_FILE, status.Value[1].Path, "2 件目のパス");

        // 一覧にも出る。
        var list = locks.ListAsync().GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, list.Outcome,
                    $"一覧の結末（{list.Message} / {string.Join(" | ", list.Details)}）");

        var release = locks.ReleaseAsync(new[] { LOCK_FILE }).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, release.Outcome,
                    $"解放の結末（{release.Message} / {string.Join(" | ", release.Details)}）");

        var afterRelease = locks.GetStatusAsync(new[] { LOCK_FILE }).GetAwaiter().GetResult();
        Check.Equal(LockHolder.None, afterRelease.Value![0].Holder, "解放後はロックなし");
    }

    /// <summary>
    /// 改名を <c>stage move</c> として通知すると、状態に「移動」として現れること。
    /// 素のファイル移動だと削除 + 追加になり履歴が切れる。
    /// </summary>
    private static void RenameIsRecordedAsMove()
    {
        var provider = Require(_providerA);

        var from = Path.Combine(_dirA, RENAME_SOURCE);
        var to   = Path.Combine(_dirA, RENAME_TARGET);

        // 先にファイルシステム上で動かし、そのあと Lore へ通知する
        // （エディタのプロジェクトパネルと同じ順序）。
        File.Move(from, to, overwrite: true);

        var notify = provider.NotifyMovedAsync(from, to).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, notify.Outcome,
                    $"移動通知の結末（{notify.Message} / {string.Join(" | ", notify.Details)}）");

        var status = provider.GetStatusAsync(StatusRefreshMode.ScanOffline)
                             .GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, status.Outcome, "状態取得の結末");

        var moved = status.Value!.Changes.FirstOrDefault(
            c => c.Path.EndsWith(RENAME_TARGET, StringComparison.OrdinalIgnoreCase));
        Check.True(moved is not null,
                   $"移動後のパスが一覧に出る（{string.Join(", ", status.Value.Changes.Select(c => c.ToString()))}）");
        Check.Equal(FileChangeKind.Moved, moved!.Kind, "変更の種類");

        var submit = provider.SubmitAsync("改名を送信").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, submit.Outcome,
                    $"送信の結末（{submit.Message} / {string.Join(" | ", submit.Details)}）");
    }

    /// <summary>
    /// **フォルダ**ごと移動したときに Lore が何をするかを確かめる。
    ///
    /// <para>
    /// プロジェクトパネルではフォルダのドラッグ＆ドロップで中身ごと動く。
    /// このとき <c>file stage move</c> にフォルダを渡して履歴が繋がるのか、
    /// それともファイル単位に展開しないといけないのかは、
    /// ドキュメントからは分からないので実機で確かめる必要がある。
    /// </para>
    /// <para>
    /// 期待: 移動後のファイルが「移動」として一覧に出ること
    /// （「削除 + 追加」になっていたら履歴が切れている）。
    /// </para>
    /// </summary>
    private static void FolderMoveIsRecordedAsMove()
    {
        var provider = Require(_providerA);

        // 1. フォルダを作って中身ごと送信する（移動前の状態を履歴に入れる）。
        var sourceDir = Path.Combine(_dirA, FOLDER_MOVE_SOURCE);
        Directory.CreateDirectory(sourceDir);
        WriteLines(_dirA, Path.Combine(FOLDER_MOVE_SOURCE, FOLDER_MOVE_FILE_A), "folder file a");
        WriteLines(_dirA, Path.Combine(FOLDER_MOVE_SOURCE, FOLDER_MOVE_FILE_B), "folder file b");

        var initial = provider.SubmitAsync("フォルダ移動テストの初期投入").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, initial.Outcome,
                    $"初期投入の結末（{initial.Message} / {string.Join(" | ", initial.Details)}）");

        // 2. フォルダごと動かしてから通知する（プロジェクトパネルと同じ順序）。
        var targetDir = Path.Combine(_dirA, FOLDER_MOVE_TARGET);
        Directory.Move(sourceDir, targetDir);

        var notify = provider.NotifyMovedAsync(sourceDir, targetDir).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, notify.Outcome,
                    $"フォルダ移動通知の結末（{notify.Message} / {string.Join(" | ", notify.Details)}）");

        // 3. 状態を取り直し、中のファイルが「移動」として出ることを確かめる。
        var status = provider.GetStatusAsync(StatusRefreshMode.ScanOffline)
                             .GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, status.Outcome, "状態取得の結末");

        var all = string.Join(", ", status.Value!.Changes.Select(c => c.ToString()));

        // 移動後のフォルダ配下のファイルが、移動（または移動したフォルダ自体）として
        // 現れていること。削除 + 追加になっていたら履歴が切れている。
        var movedEntries = status.Value.Changes
            .Where(c => c.Path.Replace('\\', '/')
                         .StartsWith(FOLDER_MOVE_TARGET + "/", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.Path.Replace('\\', '/'), FOLDER_MOVE_TARGET,
                                         StringComparison.OrdinalIgnoreCase))
            .ToList();

        Check.True(movedEntries.Count > 0,
                   $"移動先が一覧に出る。実際の変更一覧: [{all}]");
        Check.True(movedEntries.All(c => c.Kind == FileChangeKind.Moved),
                   "移動先の項目がすべて「移動」として記録されている"
                   + $"（削除+追加になっていたら履歴が切れる）。実際の変更一覧: [{all}]");

        // 移動元が「削除」として別に出ていないこと（出ていたら履歴が切れている）。
        var deletedSource = status.Value.Changes.Any(
            c => c.Kind == FileChangeKind.Deleted
                 && c.Path.Replace('\\', '/')
                     .StartsWith(FOLDER_MOVE_SOURCE + "/", StringComparison.OrdinalIgnoreCase));
        Check.True(!deletedSource,
                   $"移動元が「削除」として残っていない。実際の変更一覧: [{all}]");

        var submit = provider.SubmitAsync("フォルダの移動を送信").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, submit.Outcome,
                    $"送信の結末（{submit.Message} / {string.Join(" | ", submit.Details)}）");
    }

    /// <summary>
    /// 履歴にコミットメッセージ・作者・日時が入ること。
    ///
    /// <para>
    /// Lore の <c>revision history</c> はリビジョン番号とハッシュしか返さないため、
    /// プロバイダが <c>revision metadata list</c> を 1 件ずつ引いて補っている。
    /// キー名は Lore の実装依存なので、**実機で確かめないと対応表が正しいか分からない**。
    /// 失敗したときはメタデータのキーと値をそのまま出して、対応表を直せるようにする。
    /// </para>
    /// </summary>
    private static void HistoryCarriesMetadata()
    {
        var provider = Require(_providerA);

        // このテスト専用の、他と紛れない印つきメッセージで 1 件コミットする。
        WriteLines(_dirA, HISTORY_PROBE_FILE, "history probe");
        var submit = provider.SubmitAsync(HISTORY_PROBE_MESSAGE).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, submit.Outcome,
                    $"送信の結末（{submit.Message} / {string.Join(" | ", submit.Details)}）");

        var history = provider.GetHistoryAsync().GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, history.Outcome,
                    $"履歴取得の結末（{history.Message} / {string.Join(" | ", history.Details)}）");
        Check.True(history.Value!.Count > 0, "履歴が 1 件以上返る");

        var found = history.Value.FirstOrDefault(
            r => r.Message.Contains(HISTORY_PROBE_MESSAGE, StringComparison.Ordinal));

        // 見つからなければ、生のメタデータを全部出して対応表を直せるようにする。
        Check.True(found is not null,
                   "直前のコミットメッセージが履歴に出る。"
                   + $"実際の履歴: [{string.Join(" / ", history.Value.Select(r => r.ToString()))}]"
                   + $" 生のメタデータ: [{DumpRawMetadata(_dirA, history.Value)}]");

        Check.True(found!.Author.Length > 0,
                   $"作者が入る（実際: '{found.Author}'）。"
                   + $" 生のメタデータ: [{DumpRawMetadata(_dirA, history.Value)}]");
        Check.True(found.TimestampUtc != default,
                   $"日時が入る（実際: {found.TimestampUtc:O}）。"
                   + $" 生のメタデータ: [{DumpRawMetadata(_dirA, history.Value)}]");
    }

    // ============================================================
    //  共通ヘルパー
    // ============================================================

    /// <summary>
    /// 履歴の各リビジョンのメタデータを生のまま 1 行へ畳む（対応表を直すための診断用）。
    /// </summary>
    /// <param name="dir">作業コピーのルート。</param>
    /// <param name="revisions">対象のリビジョン。</param>
    private static string DumpRawMetadata(string dir, IReadOnlyList<RevisionInfo> revisions)
    {
        try
        {
            using var backend = new LoreNativeBackend(dir);
            var parts = new List<string>();

            // 全部出すと読めないので、新しい方から数件だけ。
            foreach (var revision in revisions.Take(METADATA_DUMP_LIMIT))
            {
                var rows = backend.RevisionMetadata(revision.Id, CancellationToken.None);
                if (!rows.Call.Succeeded)
                {
                    parts.Add($"#{revision.Number} 取得失敗({rows.Call})");
                    continue;
                }

                var pairs = rows.Rows.Select(
                    r => $"{r.Key}={r.Kind}:'{r.StringValue}'/{r.NumericValue}");
                parts.Add($"#{revision.Number} {{{string.Join(", ", pairs)}}}");
            }

            return string.Join(" ; ", parts);
        }
        catch (Exception ex)
        {
            return $"（メタデータの採取に失敗: {ex.Message}）";
        }
    }

    // ============================================================
    //  ブランチのマージ・削除（アーカイブ）
    //
    //  ★ここで実機確認したいこと:
    //    ・branch merge が競合なしならコミットまで自動で打つこと
    //    ・競合したときの mine / theirs の向きが **sync のときと同じ** かどうか
    //      （sync のマージと branch merge で親の並びが違えば、対応表を分ける必要がある）
    //    ・Lore にブランチの削除が無く、archive が「一覧から隠す」こと
    // ============================================================

    /// <summary>
    /// 別ブランチで作ったファイルが、マージで現在のブランチへ入ってくること。
    /// 競合が無ければ Lore がマージのコミットまで打つ、という前提もここで確かめる。
    /// </summary>
    private static void MergeBranchBringsChanges()
    {
        var provider = Require(_providerA);

        // 起点のブランチ名は決め打ちにせず、実際の値を読む。
        _baseBranch = CurrentBranch(provider);
        Check.True(_baseBranch.Length > 0, "起点のブランチ名が取れる");

        // ── 取り込み元のブランチで、新しいファイルを送る ──
        CreateAndSwitch(provider, MERGE_BRANCH);
        WriteLines(_dirA, MERGE_FILE, "from-branch");
        Notify(provider, _dirA, MERGE_FILE);
        var submitOnBranch = provider.SubmitAsync("ブランチ側の追加").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, submitOnBranch.Outcome,
                    $"ブランチ側の送信（{submitOnBranch.Message} / {string.Join(" | ", submitOnBranch.Details)}）");

        // ── 起点へ戻ると、そのファイルはまだ無い ──
        Switch(provider, _baseBranch);
        Check.True(!File.Exists(Path.Combine(_dirA, MERGE_FILE)),
                   "戻った直後はブランチ側のファイルが無い");

        // ── マージで取り込む ──
        var merge = provider.MergeBranchAsync(MERGE_BRANCH).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, merge.Outcome,
                    $"マージの結末（{merge.Message} / {string.Join(" | ", merge.Details)}）");

        var path = Path.Combine(_dirA, MERGE_FILE);
        Check.True(File.Exists(path), "取り込んだファイルが手元にある");
        Check.True(File.ReadAllText(path).Contains("from-branch", StringComparison.Ordinal),
                   $"中身がブランチ側のもの。実際の内容: {Summarize(File.ReadAllText(path))}");

        // 競合が無ければコミットまで済んでいるので、そのまま送信できる。
        var submit = provider.SubmitAsync("マージの送信").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, submit.Outcome,
                    $"マージ後の送信（{submit.Message} / {string.Join(" | ", submit.Details)}）");
    }

    /// <summary>
    /// ★マージの競合で「自分の変更を残す」を選んだら、
    /// **現在のブランチ（取り込み先）の内容**が残ること。
    ///
    /// <para>
    /// sync の競合と同じ対応表（KeepMine → Lore の theirs）で正しいかどうかは、
    /// マージの親の並びが sync と同じかによる。逆なら利用者の変更が黙って消えるので、
    /// ここは必ずファイルの中身で確かめる。
    /// </para>
    /// </summary>
    private static void MergeConflictKeepMineKeepsCurrentBranch()
    {
        var provider = Require(_providerA);

        // 共通の土台を送ってから、両側で同じ行を変える。
        MakeConflictingBranch(provider, "branch-first", "current-first");

        var merge = provider.MergeBranchAsync(CONFLICT_BRANCH).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Conflicted, merge.Outcome,
                    $"マージの結末（{merge.Message} / {string.Join(" | ", merge.Details)}）");
        Check.True(merge.Value!.Conflicts.Any(
                       c => c.Path.EndsWith(CONFLICT_FILE, StringComparison.OrdinalIgnoreCase)),
                   $"対象ファイルが競合に含まれる（{string.Join(", ", merge.Value.Conflicts.Select(c => c.Path))}）");

        var resolve = provider.ResolveConflictsAsync(
            new[] { CONFLICT_FILE }, ConflictResolutionChoice.KeepMine).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, resolve.Outcome,
                    $"解決の結末（{resolve.Message} / {string.Join(" | ", resolve.Details)}）");

        var content = File.ReadAllText(Path.Combine(_dirA, CONFLICT_FILE));
        Check.True(content.Contains("current-first", StringComparison.Ordinal),
                   $"現在のブランチ側の内容が残る。実際の内容: {Summarize(content)}");
        Check.True(!content.Contains("branch-first", StringComparison.Ordinal),
                   $"取り込み元の内容で上書きされていない。実際の内容: {Summarize(content)}");

        provider.SubmitAsync("マージ（自分の変更を残す）の送信").GetAwaiter().GetResult();
    }

    /// <summary>
    /// 「リモートを採用」を選んだら、**取り込み元のブランチの内容**になること。
    /// KeepMine と対称に確かめて、対応表が両方向とも正しいことを固定する。
    /// </summary>
    private static void MergeConflictTakeRemoteTakesSourceBranch()
    {
        var provider = Require(_providerA);

        MakeConflictingBranch(provider, "branch-second", "current-second");

        var merge = provider.MergeBranchAsync(CONFLICT_BRANCH).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Conflicted, merge.Outcome,
                    $"マージの結末（{merge.Message} / {string.Join(" | ", merge.Details)}）");

        var resolve = provider.ResolveConflictsAsync(
            new[] { CONFLICT_FILE }, ConflictResolutionChoice.TakeRemote).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, resolve.Outcome,
                    $"解決の結末（{resolve.Message} / {string.Join(" | ", resolve.Details)}）");

        var content = File.ReadAllText(Path.Combine(_dirA, CONFLICT_FILE));
        Check.True(content.Contains("branch-second", StringComparison.Ordinal),
                   $"取り込み元の内容になる。実際の内容: {Summarize(content)}");
        Check.True(!content.Contains("current-second", StringComparison.Ordinal),
                   $"現在のブランチ側の内容は採られていない。実際の内容: {Summarize(content)}");

        provider.SubmitAsync("マージ（リモートを採用）の送信").GetAwaiter().GetResult();
    }

    /// <summary>
    /// 削除（アーカイブ）したブランチが一覧から消えること。
    /// Lore の archive は「隠す」なので、一覧に出ないことが唯一の観測点。
    /// </summary>
    private static void ArchiveBranchHidesItFromList()
    {
        var provider = Require(_providerA);

        // 消すためだけのブランチを作り、起点へ戻る（現在のブランチは消せないため）。
        CreateAndSwitch(provider, ARCHIVE_BRANCH);
        Switch(provider, _baseBranch);

        Check.True(BranchNames(provider).Contains(ARCHIVE_BRANCH, StringComparer.Ordinal),
                   $"削除前は一覧に出る（{string.Join(", ", BranchNames(provider))}）");

        var archive = provider.ArchiveBranchAsync(ARCHIVE_BRANCH).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, archive.Outcome,
                    $"削除の結末（{archive.Message} / {string.Join(" | ", archive.Details)}）");

        Check.True(!BranchNames(provider).Contains(ARCHIVE_BRANCH, StringComparer.Ordinal),
                   $"削除後は一覧に出ない（{string.Join(", ", BranchNames(provider))}）");
    }

    /// <summary>
    /// 現在のブランチは削除できないこと（足場を消させない）。
    /// プロバイダが Lore を呼ぶ前に弾くので、ブランチは一覧に残ったまま。
    /// </summary>
    private static void ArchiveCurrentBranchIsRefused()
    {
        var provider = Require(_providerA);

        var current = CurrentBranch(provider);
        var result  = provider.ArchiveBranchAsync(current).GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Failed, result.Outcome, $"結末（{result.Message}）");
        Check.True(BranchNames(provider).Contains(current, StringComparer.Ordinal),
                   $"現在のブランチは一覧に残っている（{string.Join(", ", BranchNames(provider))}）");
    }

    // ── ブランチテスト用の補助 ──────────────────────────────

    /// <summary>
    /// 競合する状態を作る。土台を送ってから、取り込み元と現在のブランチで
    /// **同じ行**を別の内容に変え、どちらも送っておく。
    /// </summary>
    /// <param name="provider">対象のプロバイダ（A の作業コピー）。</param>
    /// <param name="branchSideText">取り込み元のブランチに書く印。</param>
    /// <param name="currentSideText">現在のブランチに書く印。</param>
    private static void MakeConflictingBranch(
        LoreProvider provider, string branchSideText, string currentSideText)
    {
        // 1 回目だけブランチを作る。2 回目以降は既にあるので切り替えるだけ。
        var names = BranchNames(provider);
        if (!names.Contains(CONFLICT_BRANCH, StringComparer.Ordinal))
        {
            // 土台を起点のブランチへ送ってから枝を切る（共通の祖先を作るため）。
            WriteLines(_dirA, CONFLICT_FILE, "base", "line2", "line3");
            Notify(provider, _dirA, CONFLICT_FILE);
            var seed = provider.SubmitAsync("競合テストの土台").GetAwaiter().GetResult();
            Check.True(seed.Outcome is VersionControlOutcome.Success
                                    or VersionControlOutcome.NothingToDo,
                       $"土台の送信（{seed.Message} / {string.Join(" | ", seed.Details)}）");

            CreateAndSwitch(provider, CONFLICT_BRANCH);
        }
        else
        {
            Switch(provider, CONFLICT_BRANCH);
        }

        // ── 取り込み元のブランチ側を変えて送る ──
        WriteLines(_dirA, CONFLICT_FILE, branchSideText, "line2", "line3");
        Notify(provider, _dirA, CONFLICT_FILE);
        var onBranch = provider.SubmitAsync($"ブランチ側: {branchSideText}")
                               .GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, onBranch.Outcome,
                    $"ブランチ側の送信（{onBranch.Message} / {string.Join(" | ", onBranch.Details)}）");

        // ── 起点へ戻して、同じ行を別の内容に変えて送る ──
        Switch(provider, _baseBranch);
        WriteLines(_dirA, CONFLICT_FILE, currentSideText, "line2", "line3");
        Notify(provider, _dirA, CONFLICT_FILE);
        var onCurrent = provider.SubmitAsync($"現ブランチ側: {currentSideText}")
                                .GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, onCurrent.Outcome,
                    $"現ブランチ側の送信（{onCurrent.Message} / {string.Join(" | ", onCurrent.Details)}）");
    }

    /// <summary>ブランチを作って切り替える（失敗したらその場で分かるよう確かめる）。</summary>
    /// <param name="provider">対象のプロバイダ。</param>
    /// <param name="name">ブランチ名。</param>
    private static void CreateAndSwitch(LoreProvider provider, string name)
    {
        var created = provider.CreateBranchAsync(name).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, created.Outcome,
                    $"ブランチ「{name}」の作成（{created.Message} / {string.Join(" | ", created.Details)}）");
        Switch(provider, name);
    }

    /// <summary>ブランチを切り替え、実際に切り替わったことを確かめる。</summary>
    /// <param name="provider">対象のプロバイダ。</param>
    /// <param name="name">ブランチ名。</param>
    private static void Switch(LoreProvider provider, string name)
    {
        var switched = provider.SwitchBranchAsync(name).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, switched.Outcome,
                    $"ブランチ「{name}」への切り替え（{switched.Message} / {string.Join(" | ", switched.Details)}）");
        Check.Equal(name, CurrentBranch(provider), "切り替え後の現在のブランチ");
    }

    /// <summary>現在のブランチ名を状態から読む。</summary>
    /// <param name="provider">対象のプロバイダ。</param>
    private static string CurrentBranch(LoreProvider provider)
    {
        var status = provider.GetStatusAsync(StatusRefreshMode.ScanOffline)
                             .GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, status.Outcome,
                    $"状態の取得（{status.Message}）");
        return status.Value!.BranchName;
    }

    /// <summary>いま一覧に出るブランチ名を取る。</summary>
    /// <param name="provider">対象のプロバイダ。</param>
    private static IReadOnlyList<string> BranchNames(LoreProvider provider)
    {
        var result = provider.GetBranchesAsync().GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, result.Outcome,
                    $"ブランチ一覧の取得（{result.Message} / {string.Join(" | ", result.Details)}）");
        return result.Value!.Select(b => b.Name).ToList();
    }

    /// <summary>リポジトリを新規作成する（エディタの操作ではないので直接 Lore を呼ぶ）。</summary>
    /// <param name="dir">作業コピーのルート。</param>
    /// <param name="url">リポジトリ URL。</param>
    /// <param name="identity">identity。</param>
    private static void CreateRepository(string dir, string url, string identity)
    {
        using var globalArgs = new LoreGlobalArgs { RepositoryPath = dir, Identity = identity };
        using var args = new LoreRepositoryCreateArgs { RepositoryUrl = url };
        try
        {
            LoreApi.RepositoryCreate(globalArgs, args).Wait();
        }
        catch (LoreError error)
        {
            throw new InvalidOperationException(
                $"リポジトリを作成できませんでした（rc={error.ReturnCode}）: "
                + string.Join(" | ", error.Messages ?? Array.Empty<string>()));
        }
    }

    /// <summary>リポジトリをクローンする。</summary>
    /// <param name="dir">クローン先。</param>
    /// <param name="url">リポジトリ URL。</param>
    /// <param name="identity">identity。</param>
    private static void CloneRepository(string dir, string url, string identity)
    {
        using var globalArgs = new LoreGlobalArgs { RepositoryPath = dir, Identity = identity };
        using var args = new LoreRepositoryCloneArgs { RepositoryUrl = url };
        try
        {
            LoreApi.RepositoryClone(globalArgs, args).Wait();
        }
        catch (LoreError error)
        {
            throw new InvalidOperationException(
                $"クローンできませんでした（rc={error.ReturnCode}）: "
                + string.Join(" | ", error.Messages ?? Array.Empty<string>()));
        }
    }

    /// <summary>本物のバックエンドを使うプロバイダを作る。</summary>
    /// <param name="dir">作業コピーのルート。</param>
    private static LoreProvider NewProvider(string dir)
    {
        var settings  = VersionControlSettings.Default;
        var scheduler = new SerialWorkerScheduler(settings.ShutdownWait);
        return new LoreProvider(
            new LoreNativeBackend(dir), scheduler, settings, ownsScheduler: true);
    }

    /// <summary>行を並べてファイルへ書く（改行は LF で固定する）。</summary>
    /// <param name="dir">作業コピーのルート。</param>
    /// <param name="relativePath">リポジトリ相対パス。</param>
    /// <param name="lines">書き込む行。</param>
    private static void WriteLines(string dir, string relativePath, params string[] lines)
    {
        // CRLF/LF の違いが行マージの結果に影響しないよう、LF で統一する。
        var content = string.Join("\n", lines) + "\n";
        File.WriteAllText(Path.Combine(dir, relativePath), content);
    }

    /// <summary>ファイルの変更をプロバイダへ通知する。</summary>
    /// <param name="provider">通知先。</param>
    /// <param name="dir">作業コピーのルート。</param>
    /// <param name="relativePath">リポジトリ相対パス。</param>
    private static void Notify(LoreProvider provider, string dir, string relativePath)
    {
        var result = provider.NotifyChangedAsync(
            new[] { Path.Combine(dir, relativePath) }).GetAwaiter().GetResult();
        Check.True(result.Outcome is VersionControlOutcome.Success
                                  or VersionControlOutcome.NothingToDo,
                   $"変更通知の結末（{result.Message} / {string.Join(" | ", result.Details)}）");
    }

    /// <summary>前段のテストが失敗して null のままなら、その旨で失敗させる。</summary>
    /// <param name="provider">確認するプロバイダ。</param>
    private static LoreProvider Require(LoreProvider? provider)
        => provider ?? throw new AssertionException(
               "前段の準備が失敗しているため実行できません（最初の [結合] テストの失敗を見てください）。");

    /// <summary>失敗メッセージ用に内容を 1 行へ畳む。</summary>
    /// <param name="content">ファイルの内容。</param>
    private static string Summarize(string content)
        => content.Replace("\r", "\\r").Replace("\n", "\\n");
}
