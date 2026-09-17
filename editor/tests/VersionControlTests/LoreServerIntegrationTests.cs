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

    // ============================================================
    //  共通ヘルパー
    // ============================================================

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
