// ============================================================
//  ServerIntegrationTests.cs — SEED アカウントを「実サーバ × エディタの実クラス」で通す
//
//  【なぜ要るのか】
//  サーバ側（tools/seed-loreserver/src/auth/）は CLI で、
//  エディタ側（editor/src/Accounts/）は **偽の窓口と偽のクローン**で
//  それぞれ検証されていた。両者を繋いだ経路
//  ── とくに **トークン付きのクローン**（IdentityToken と AccessToken に同じ JWT、
//  Identity は空）と、認証を有効にしたサーバに対する送信・取得・ロック ──
//  は一度も動いていない。ここを実際に動かして固定する。
//
//  【走らせ方（既定では走らない）】
//    $env:SEED_ACCOUNTS_TEST_SERVER = "1"
//    dotnet run --project editor/tests/AccountsTests
//  環境変数が無ければ 1 件も登録しない（CI やふだんのテストを遅くしない）。
//
//  【筋書き（契約 docs/seed_accounts.md 5 章「有効化の順番」そのまま）】
//   第 1 段（`[seed_auth]` のみ・Lore は匿名）
//     リポジトリ作成 → 匿名で最初の送信 → A のアカウント作成 →
//     `.lore/id` から repository_id → bootstrap → ログイン → 招待 →
//     B のアカウント作成 → join → B ログイン
//   第 2 段（`[server.auth]` と `[environment.endpoint]` を足して**1 回だけ**再起動）
//     B がトークン付きでクローン → B が送信 → A が取得 →
//     A が 2 つめのリポジトリを作る（作成者が自動で owner になる）→
//     作成を許されていない B は作れない → 参加していない C は引けない →
//     A がロック → B から見た保持者は「他の人」 → B の解放は失敗・A は成功 →
//     匿名は拒否 → 参加者一覧 → B を失効 → 失効が即座に効く
//
//  【★ここが以前と決定的に違う点】
//  以前は `[environment.endpoint] auth_url` を**操作ごとに付け外しして
//  サーバを再起動**しないと、クローン・送信・取得のすべてを通せなかった。
//  seed-loreserver が権限サービス（`epic_urc.UrcAuthApi` /
//  `ucs.auth.RebacApi`）を持つようになったので、**auth_url を書きっぱなしの
//  1 つの設定**で全部通る。このテストは「第 1 段 → 第 2 段の 1 回だけ再起動する」
//  形になっており、途中で設定を書き換えない。
//
//  【本番環境を触らないための約束】
//  ポートは Lore 41357 / 41359、窓口 41361、権限サービス 41362
//  （本番 41337 / 41339 / 41350 / 41352 とは別）。
//  アカウントの保管先は使い捨てフォルダ（%APPDATA% の本物には触れない）。
//  止めるのは自分が起動したプロセスだけ。
//
//  【秘密をログへ出さない】
//  アクセストークン・招待コード・秘密鍵は Console へ出さない。
//  「長さ」や「一致したか」だけを出す。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LoreVcs;
using LoreVcs.Types.Args;
using SEEDEditor.Accounts;
using SEEDEditor.Accounts.Crypto;
using SEEDEditor.Accounts.Http;
using SEEDEditor.Accounts.Model;
using SEEDEditor.Accounts.Storage;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Lore;
using SEEDEditor.VersionControl.Lore.Backend;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Scheduling;
using SpriteRigTests;

// 自分の名前空間 SEEDEditor.VersionControl.Lore が LoreVcs の静的クラス Lore を
// 隠してしまうため、明示的な別名で参照する。
using LoreApi = global::LoreVcs.Lore;

namespace SEEDEditor.Tests.Accounts;

/// <summary>
/// 実サーバ（seed-loreserver）とエディタの実クラスを繋いだ結合テスト。
/// </summary>
public static class ServerIntegrationTests
{
    // ── 実行の可否 ──────────────────────────────────────────

    /// <summary>この結合テストを走らせる環境変数。</summary>
    public const string ENV_RUN_SERVER_TESTS = "SEED_ACCOUNTS_TEST_SERVER";

    /// <summary>環境変数が立っていれば走らせる。</summary>
    public static bool IsEnabled
        => !string.IsNullOrWhiteSpace(
               Environment.GetEnvironmentVariable(ENV_RUN_SERVER_TESTS));

    // ── 登場人物と対象ファイル ──────────────────────────────

    /// <summary>オーナー A の名前。**日本語を含む名前が端から端まで通ることを確かめる。**</summary>
    private const string NAME_A = "つばさ-01";

    /// <summary>参加者 B の名前。</summary>
    private const string NAME_B = "bob-02";

    /// <summary>
    /// 参加者 C の名前。**2 つめのリポジトリにだけ**参加させ、
    /// 1 つめのリポジトリを引けないことを確かめるために使う。
    /// </summary>
    private const string NAME_C = "carol-03";

    /// <summary>
    /// 全角英数字の名前（サーバに拒否されること）。
    /// 半角の `tsubasa` と見分けがつかない名前を作らせないための規則（契約 2 章）。
    /// </summary>
    private const string NAME_FULLWIDTH = "ｔｓｕｂａｓａ";

    /// <summary>サーバ上のリポジトリ（プロジェクト）名。</summary>
    private const string REPOSITORY_NAME = "SeedAccountsIT";

    /// <summary>
    /// **認証を有効にしたまま**作る 2 つめのリポジトリ名。
    /// 以前はこれができず、`[environment.endpoint]` を外して再起動するしかなかった。
    /// </summary>
    private const string REPOSITORY_NAME_SECOND = "SeedAccountsIT2";

    /// <summary>
    /// B が作ろうとして拒否されるリポジトリ名
    /// （`repository_creators` に載っていない人は作れない）。
    /// </summary>
    private const string REPOSITORY_NAME_DENIED = "SeedAccountsITDenied";

    /// <summary>プロジェクトファイルの拡張子（参加後にこれを探す）。</summary>
    private const string PROJECT_FILE_EXTENSION = ".seedproj";

    /// <summary>リポジトリ直下に置くプロジェクトファイル。</summary>
    private const string PROJECT_FILE = REPOSITORY_NAME + PROJECT_FILE_EXTENSION;

    /// <summary>A が最初に入れるファイル。</summary>
    private const string SHARED_FILE = "assets/shared.txt";

    /// <summary>ロックの対象にするファイル。</summary>
    private const string LOCK_FILE = "assets/locked.txt";

    /// <summary>B が送信するファイル。</summary>
    private const string FROM_B_FILE = "assets/from_b.txt";

    /// <summary>B が送信する内容（A 側で一致を確かめる）。</summary>
    private const string FROM_B_CONTENT = "bob が送った 1 行";

    /// <summary>失効の直前に B が送るファイル（送信が通ることの確認用）。</summary>
    private const string FROM_B_BEFORE_REVOKE_FILE = "assets/from_b_before_revoke.txt";

    /// <summary>失効の直後に B が送ろうとするファイル（拒否されることの確認用）。</summary>
    private const string FROM_B_AFTER_REVOKE_FILE = "assets/from_b_after_revoke.txt";

    /// <summary>リポジトリ作成時に使う identity（匿名段階なので誰でもよい）。</summary>
    private const string SETUP_IDENTITY = "seed-it-setup";

    /// <summary>A の作業コピーのフォルダ名。</summary>
    private const string DIR_NAME_A = "owner_a";

    /// <summary>B の作業コピー（クローン先）のフォルダ名。</summary>
    private const string DIR_NAME_B = "member_b";

    /// <summary>A が 2 つめのリポジトリを作るフォルダ名。</summary>
    private const string DIR_NAME_A_SECOND = "owner_a_second";

    /// <summary>B が作成を拒否されるリポジトリのフォルダ名。</summary>
    private const string DIR_NAME_B_DENIED = "member_b_denied";

    /// <summary>C が 1 つめのリポジトリを引こうとするフォルダ名（失敗する）。</summary>
    private const string DIR_NAME_C_DENIED = "outsider_c_denied";

    /// <summary>失効した B がクローンを試みるフォルダ名（失敗する）。</summary>
    private const string DIR_NAME_B_AFTER_REVOKE = "member_b_after_revoke";

    /// <summary>リポジトリ ID の文字数（32 桁の 16 進小文字）。</summary>
    private const int REPOSITORY_ID_HEX_LENGTH = 32;

    /// <summary>1 つめのリポジトリの参加者一覧に期待する人数（A と B）。</summary>
    private const int EXPECTED_MEMBER_COUNT = 2;

    /// <summary>
    /// 2 つめのリポジトリを作った直後の参加者数（作成者 A だけ）。
    /// **`bootstrap` を踏んでいないのに owner が居る**ことがこの機能の要点。
    /// </summary>
    private const int EXPECTED_SECOND_MEMBER_COUNT = 1;

    /// <summary>秘密を出さずに「あるかどうか」を伝えるための最小長。</summary>
    private const int MIN_TOKEN_LENGTH = 16;

    /// <summary>Lore が「見つからない」ときに返すメッセージ（終了コード 13）。</summary>
    private const string LORE_NOT_FOUND_MARKER = "Not found";

    /// <summary>
    /// 自動ロックの取得・解放を待つ上限 [ms]。
    /// 自動ロックは「待たない」設計（開く操作を止めないため）なので、
    /// テスト側でサーバ往復 1〜2 回ぶんだけ待つ。
    /// </summary>
    private const int GATE_WAIT_TIMEOUT_MS = 15_000;

    /// <summary>自動ロックを待つときの見に行く間隔 [ms]。</summary>
    private const int GATE_WAIT_POLL_MS = 200;

    // ── 共有する状態（テストは登録順に走る）────────────────

    /// <summary>使い捨てサーバ。</summary>
    private static AccountsServerFixture? _server;

    /// <summary>A の作業コピー。</summary>
    private static string _dirA = string.Empty;

    /// <summary>B の作業コピー（クローン先）。</summary>
    private static string _dirB = string.Empty;

    /// <summary>リポジトリ ID（32 桁の 16 進小文字）。</summary>
    private static string _repositoryId = string.Empty;

    /// <summary>A のアカウント（オーナー）。</summary>
    private static SeedAccount? _accountA;

    /// <summary>B のアカウント（参加者）。</summary>
    private static SeedAccount? _accountB;

    /// <summary>C のアカウント（2 つめのリポジトリにだけ参加する）。</summary>
    private static SeedAccount? _accountC;

    /// <summary>2 つめのリポジトリ ID（認証を有効にしたまま作ったもの）。</summary>
    private static string _repositoryIdSecond = string.Empty;

    /// <summary>A のアカウント保管フォルダ。</summary>
    private static string _accountDirA = string.Empty;

    /// <summary>A のログインセッション管理（実クラス）。</summary>
    private static AccountSessionManager? _sessionA;

    /// <summary>B のログインセッション管理（実クラス）。</summary>
    private static AccountSessionManager? _sessionB;

    /// <summary>A のプロバイダ（トークン付き）。</summary>
    private static LoreProvider? _providerA;

    /// <summary>B のプロバイダ（トークン付き）。</summary>
    private static LoreProvider? _providerB;

    /// <summary>後始末を 2 回走らせないための印。</summary>
    private static bool _cleanedUp;

    // ── 登録 ────────────────────────────────────────────────

    /// <summary>
    /// 結合テストを登録する（環境変数が無ければ何もしない）。
    /// </summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        if (!IsEnabled) return;

        // ── 第 1 段: [seed_auth] だけ有効（Lore は匿名）──
        harness.Add("[結合1] サーバ起動・リポジトリ作成・匿名で最初の送信", Stage1_SetUpAndFirstSubmit);
        harness.Add("[結合1] 窓口の health と .lore/id からの repository_id", Stage1_HealthAndRepositoryId);
        harness.Add("[結合1] A のアカウント作成 → bootstrap → ログイン", Stage1_BootstrapOwner);
        harness.Add("[結合1] 全角英数字の名前はサーバもエディタも拒否する", Stage1_FullWidthNameRejected);
        harness.Add("[結合1] 招待コード発行 → B が join → B ログイン", Stage1_InviteAndJoin);

        // ── 第 2 段: [server.auth] と [environment.endpoint] を足して 1 回だけ再起動 ──
        harness.Add("[結合2] 認証を有効にして再起動 → 匿名のプロバイダは拒否される", Stage2_RestartAndRejectAnonymous);
        harness.Add("[結合2] auth_url を書いたままクローンできる（ProjectJoinService）", Stage2_JoinWithTokenClone);
        harness.Add("[結合2] auth_url を書いたまま B が送信 → A が最新を取得できる", Stage2_SubmitAndFetch);
        harness.Add("[結合2] 作成を許された A は認証したまま新しいリポジトリを作れて owner になる", Stage2_CreateRepositoryWhileAuthenticated);
        harness.Add("[結合2] 作成を許されていない B は新しいリポジトリを作れない", Stage2_NonCreatorCannotCreateRepository);
        harness.Add("[結合2] 参加していないリポジトリはクローンも取得もできない", Stage2_NonMemberCannotAccess);
        harness.Add("[結合2] A がロック → B から見ると「他の人」", Stage2_LockHolderIsOtherForB);
        harness.Add("[結合2] B は解放できず、A（owner）は解放できる", Stage2_ReleaseOnlyByOwner);
        harness.Add("[結合2] A がロック中は B の保存ゲート・送信ゲートが止まる", Stage2_GateBlocksWhileOtherHolds);
        harness.Add("[結合2] A が解放すると B は保存・送信でき、自動ロックも往復する", Stage2_GateAllowsAfterRelease);
        harness.Add("[結合2] 期限前にトークンを取り直しても操作が通る", Stage2_RefreshTokenKeepsWorking);
        harness.Add("[結合2] 参加者一覧 → B を失効 → 失効が即座に効く", Stage2_MembersAndRevoke);
        harness.Add("[結合2] AccountService の自動ログインが実サーバで通る", Stage2_AccountServiceAutoSignIn);
        harness.Add("[結合2] 秘密がファイルへ残っていない（招待コード・トークン）", Stage2_NoSecretsOnDisk);

        // 最後に必ず走る後始末（前段が失敗してもここは登録順で実行される）。
        harness.Add("[結合] 後始末（サーバ停止・一時フォルダ削除）", Cleanup);
    }

    // ══════════════════════════════════════════════════════════
    //  第 1 段
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// サーバを起動し、リポジトリを作って匿名で最初の送信まで行う。
    /// この段階では Lore は匿名（契約 5 章の 1〜2）。
    /// </summary>
    private static void Stage1_SetUpAndFirstSubmit()
    {
        // ★A だけを `[seed_auth] repository_creators` に載せる。
        //   認証を有効にしたまま新しいリポジトリを作れるのは、ここに載っている人だけ。
        _server = new AccountsServerFixture(NAME_A);

        _dirA = _server.CreateWorkingCopyDir(DIR_NAME_A);
        _dirB = _server.ReserveWorkingCopyDir(DIR_NAME_B);

        var url = AccountsServerFixture.RepositoryUrl(REPOSITORY_NAME);

        // リポジトリの作成は「エディタが行う操作」ではないので、
        // プロバイダ境界には無い。ここだけ LoreVcs を直接呼ぶ。
        CreateRepository(_dirA, url, SETUP_IDENTITY);

        Check.True(VersionControlPaths.IsLoreWorkingCopy(_dirA), "A が作業コピーになっている");

        // `.seedproj` を入れておく（参加後にこれが見つかることを第 2 段で確かめる）。
        WriteFile(_dirA, PROJECT_FILE, "{}");
        WriteFile(_dirA, SHARED_FILE, "共有される 1 行");
        WriteFile(_dirA, LOCK_FILE, "ロックの対象");

        using var anonymous = NewProvider(_dirA, credentialProvider: null);
        var result = anonymous.SubmitAsync("初期投入（匿名）").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, result.Outcome,
                    $"匿名での送信の結末（{result.Message} / {Join(result.Details)}）");
        Check.True(result.Value!.FileCount > 0, "送信件数が 1 件以上");
    }

    /// <summary>
    /// 発行窓口が応答し、`.lore/id` から API へ渡す形の repository_id が取れること。
    /// </summary>
    private static void Stage1_HealthAndRepositoryId()
    {
        var server = Require(_server);

        using var client = new AuthGatewayClient(server.AuthBaseAddress, AccountSettings.Default);
        var health = client.GetHealthAsync().GetAwaiter().GetResult();

        Check.Equal("ok", health.Status, "窓口の状態");
        Check.Equal(AccountsServerFixture.JWT_ISSUER, health.Issuer, "issuer");
        Check.Equal(AccountsServerFixture.JWT_AUDIENCE, health.Audience, "audience");

        // ★`.lore/id` は生の 16 バイト。文字列として読むと化ける。
        _repositoryId = VersionControlPaths.ReadRepositoryId(_dirA);

        Check.Equal(REPOSITORY_ID_HEX_LENGTH, _repositoryId.Length, "repository_id の文字数");
        Check.True(_repositoryId.All(IsLowerHexDigit),
                   $"repository_id が 16 進小文字（実際: '{_repositoryId}'）");
    }

    /// <summary>
    /// A のアカウントを作り、ループバックから bootstrap してオーナーになり、ログインする。
    /// </summary>
    private static void Stage1_BootstrapOwner()
    {
        var server = Require(_server);
        RequireText(_repositoryId, "repository_id");

        // エディタ側の名前の規則（サーバと同じ表）を通ること。
        var check = AccountNameRule.Check(NAME_A);
        Check.True(check.IsValid, $"日本語を含む名前がエディタの規則を通る（{check.Error}）");

        _accountDirA = TestPaths.NewDirectory("account_a");
        _accountA    = CreateAndPersistAccount(NAME_A, _accountDirA);

        using var client = new AuthGatewayClient(server.AuthBaseAddress, AccountSettings.Default);

        var bootstrap = client.BootstrapAsync(new AuthBootstrapRequest
        {
            Name         = _accountA.Name,
            PublicKey    = _accountA.PublicKeyBase64Url,
            RepositoryId = _repositoryId,
            ProjectName  = REPOSITORY_NAME,
        }).GetAwaiter().GetResult();

        Check.Equal(NAME_A, bootstrap.Name, "bootstrap で登録された名前");
        Check.Equal(AccountSettings.ROLE_OWNER, bootstrap.Role, "bootstrap の役割");

        // 2 回目は 409 owner_exists（契約 3 章）。
        var second = CatchGatewayError(() => client.BootstrapAsync(new AuthBootstrapRequest
        {
            Name         = NAME_B,
            PublicKey    = _accountA.PublicKeyBase64Url,
            RepositoryId = _repositoryId,
            ProjectName  = REPOSITORY_NAME,
        }).GetAwaiter().GetResult());

        Check.Equal(AuthErrorCodes.OWNER_EXISTS, second.ErrorCode, "2 回目の bootstrap のエラーコード");

        // ── ログイン（実クラス AccountSessionManager を通す）──
        _sessionA = SignIn(server, _accountA);

        var session = _sessionA.CurrentSession;
        Check.True(session is not null, "A のセッションが取れている");
        Check.Equal(NAME_A, session!.Name, "A のログイン名");
        Check.True(session.AccessToken.Length > MIN_TOKEN_LENGTH, "A のトークンが空でない");
        Check.True(session.IsOwnerOf(_repositoryId), "A がこのリポジトリのオーナーとして載っている");
    }

    /// <summary>
    /// 全角英数字の名前が、サーバ（`model.rs`）でもエディタ（`AccountNameRule`）でも拒否されること。
    /// 同じ表を 2 か所に持っているので、片方だけ緩むと「なりすまし」が作れてしまう。
    /// </summary>
    private static void Stage1_FullWidthNameRejected()
    {
        var server = Require(_server);
        RequireText(_repositoryId, "repository_id");

        // エディタ側。
        var check = AccountNameRule.Check(NAME_FULLWIDTH);
        Check.True(!check.IsValid, "エディタが全角英数字の名前を弾く");

        // サーバ側。エディタの検証を迂回して、生の要求を送る。
        using var keyPair = AccountKeyPair.Create();
        using var client  = new AuthGatewayClient(server.AuthBaseAddress, AccountSettings.Default);

        var error = CatchGatewayError(() => client.BootstrapAsync(new AuthBootstrapRequest
        {
            Name         = NAME_FULLWIDTH,
            PublicKey    = keyPair.PublicKeyBase64Url,
            RepositoryId = _repositoryId,
            ProjectName  = REPOSITORY_NAME,
        }).GetAwaiter().GetResult());

        Check.Equal(AuthErrorCodes.INVALID_REQUEST, error.ErrorCode,
                    $"サーバが全角英数字の名前を弾く（メッセージ: {error.Message}）");
    }

    /// <summary>
    /// A が招待コードを出し、B が参加して（生の `POST /v1/join`）ログインできること。
    /// クローンは第 2 段（トークン付き）で行う。
    /// </summary>
    private static void Stage1_InviteAndJoin()
    {
        var server   = Require(_server);
        var sessionA = Require(_sessionA);
        RequireText(_repositoryId, "repository_id");

        var tokenA = RequireToken(sessionA);

        using var client = new AuthGatewayClient(server.AuthBaseAddress, AccountSettings.Default);

        var invite = client.CreateInviteAsync(tokenA, new AuthInviteRequest
        {
            RepositoryId   = _repositoryId,
            Role           = AccountSettings.ROLE_MEMBER,
            ExpiresInHours = AccountSettings.Default.InviteExpiresInHours,
        }).GetAwaiter().GetResult();

        // ★招待コードそのものは絶対にログへ出さない。
        Check.True(invite.InviteCode.Length > 0, "招待コードが返っている");

        _accountB = CreateAndPersistAccount(NAME_B, TestPaths.NewDirectory("account_b"));

        var join = client.JoinAsync(new AuthJoinRequest
        {
            InviteCode = invite.InviteCode,
            Name       = _accountB.Name,
            PublicKey  = _accountB.PublicKeyBase64Url,
        }).GetAwaiter().GetResult();

        Check.Equal(NAME_B, join.Name, "参加した名前");
        Check.Equal(_repositoryId, join.RepositoryId, "参加したリポジトリ");
        Check.Equal(REPOSITORY_NAME, join.ProjectName, "参加したプロジェクト名");
        Check.Equal(AccountSettings.ROLE_MEMBER, join.Role, "参加した役割");

        // 同じコードの 2 回目は 403 invite_invalid（使い捨て）。
        var reused = CatchGatewayError(() => client.JoinAsync(new AuthJoinRequest
        {
            InviteCode = invite.InviteCode,
            Name       = NAME_B,
            PublicKey  = _accountB.PublicKeyBase64Url,
        }).GetAwaiter().GetResult());

        Check.Equal(AuthErrorCodes.INVITE_INVALID, reused.ErrorCode, "使い回した招待コードのエラーコード");

        // ── B のログイン ──
        _sessionB = SignIn(server, _accountB);

        var session = _sessionB.CurrentSession;
        Check.True(session is not null, "B のセッションが取れている");
        Check.Equal(NAME_B, session!.Name, "B のログイン名");
        Check.Equal(AccountSettings.ROLE_MEMBER, session.RoleFor(_repositoryId), "B の役割");
    }

    // ══════════════════════════════════════════════════════════
    //  第 2 段
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// `[server.auth]` / `[server.auth.jwk]` / `[environment.endpoint]` を足して
    /// **1 回だけ**再起動し、**トークンを持たないプロバイダが拒否される**ことを確かめる。
    ///
    /// <para>
    /// ここから先は**設定を一切書き換えない**。
    /// `auth_url` は seed-loreserver 自身の権限サービス（既定 41352 / テストは 41362）を
    /// 指しているので、クローンも作成も送信も取得もこの 1 つの設定で通る。
    /// </para>
    /// </summary>
    private static void Stage2_RestartAndRejectAnonymous()
    {
        var server = Require(_server);

        server.RestartWithLoreAuth();
        Check.True(server.LoreAuthEnabled, "Lore 本体の認証が有効になっている");

        // 匿名（トークンを配らない）プロバイダでサーバへ行く操作を試す。
        using var anonymous = NewProvider(_dirA, credentialProvider: null);
        var list = anonymous.Locks.ListAsync().GetAwaiter().GetResult();

        Check.True(list.Outcome != VersionControlOutcome.Success,
                   $"匿名のロック一覧が拒否される（実際: {list.Outcome} / {list.Message}）");
    }

    /// <summary>
    /// **このテストの本命。** B が「プロジェクトに参加」の実クラス
    /// （<see cref="ProjectJoinService"/> ＋ <see cref="LoreNativeCloner"/>）で
    /// トークン付きのクローンを行える。
    ///
    /// <para>
    /// ★**`[environment.endpoint] auth_url` を書いたまま**通ることが要点。
    /// 以前はここでサーバを止めて auth_url を外し、終わったら戻す必要があった。
    /// いまは Lore が auth_url（= seed-loreserver の権限サービス）へ
    /// `CheckUserPermission` を尋ね、台帳に B の権限があるので許可される。
    /// </para>
    /// </summary>
    private static void Stage2_JoinWithTokenClone()
    {
        var server   = Require(_server);
        var sessionA = Require(_sessionA);
        var accountB = Require(_accountB);
        RequireText(_repositoryId, "repository_id");

        // 招待コードは使い捨てなので、参加のたびに出し直す
        // （同じ公開鍵なら権限を足すだけ ＝ 契約 3 章）。
        using var client = new AuthGatewayClient(server.AuthBaseAddress, AccountSettings.Default);
        var invite = client.CreateInviteAsync(RequireToken(sessionA), new AuthInviteRequest
        {
            RepositoryId   = _repositoryId,
            Role           = AccountSettings.ROLE_MEMBER,
            ExpiresInHours = AccountSettings.Default.InviteExpiresInHours,
        }).GetAwaiter().GetResult();

        var request = new JoinProjectRequest(
            AccountsServerFixture.HOST,
            invite.InviteCode,
            _dirB,
            AuthPort: AccountsServerFixture.PORT_AUTH,
            LorePort: AccountsServerFixture.PORT_QUIC_GRPC);

        var result = ProjectJoinService.JoinAsync(
            request, accountB, new LoreNativeCloner(), PROJECT_FILE_EXTENSION)
            .GetAwaiter().GetResult();

        Check.True(result.Success, $"参加とクローンが成功する（{result.Message}）");
        Check.Equal(REPOSITORY_NAME, result.ProjectName, "参加したプロジェクト名");
        Check.Equal(_repositoryId, result.RepositoryId, "参加したリポジトリ ID");
        Check.True(result.ProjectFilePath.EndsWith(PROJECT_FILE, StringComparison.OrdinalIgnoreCase),
                   $".seedproj が見つかる（実際: '{result.ProjectFilePath}'）");
        Check.True(VersionControlPaths.IsLoreWorkingCopy(_dirB), "B のクローン先が作業コピーになっている");
        Check.Equal(_repositoryId, VersionControlPaths.ReadRepositoryId(_dirB),
                    "クローン先の .lore/id が同じリポジトリ");
    }

    /// <summary>
    /// B が変更を送信し、A が「最新を取得」して内容が届くこと。
    /// どちらもトークン付きで、**設定はそのまま**（再起動しない）。
    ///
    /// <para>
    /// `[environment.endpoint] auth_url` は pull に必須（無いと QUIC の
    /// ストレージセッションがトークンを載せない）。送信（push）は
    /// `RepositoryGet` でメタデータを引くので権限サービスを通る。
    /// **以前はこの 2 つが両立せず、ここで 2 回再起動していた。**
    /// </para>
    /// </summary>
    private static void Stage2_SubmitAndFetch()
    {
        var server   = Require(_server);
        var sessionA = Require(_sessionA);
        var accountB = Require(_accountB);

        // クローン直後は `.lore/config.toml` の identity が空なので、
        // B もログインし直してトークンを配る（実運用では「プロジェクトを開く」でこれが起きる）。
        _sessionB?.Dispose();
        _sessionB = SignIn(server, accountB);

        _providerA = NewProvider(_dirA, () => CredentialOf(sessionA));
        _providerB = NewProvider(_dirB, () => CredentialOf(Require(_sessionB)));

        // 表示・比較用の identity はアカウント名になる（ロックの「自分／他の人」の前提）。
        Check.Equal(NAME_A, _providerA.Identity, "A の表示 identity");
        Check.Equal(NAME_B, _providerB.Identity, "B の表示 identity");

        WriteFile(_dirB, FROM_B_FILE, FROM_B_CONTENT);

        // ★設定を触らずにそのまま送る（以前はここで 2 回再起動していた）。
        var submit = _providerB.SubmitAsync("B からの送信").GetAwaiter().GetResult();

        Check.Equal(VersionControlOutcome.Success, submit.Outcome,
                    $"B の送信の結末（{submit.Message} / {Join(submit.Details)}）");

        // 空コミットを作っていない＝本当に push まで通っていること。
        Check.True(!submit.Value!.CommittedButNotPushed,
                   "commit だけで終わらず push まで通っている");

        var fetch = _providerA.FetchLatestAsync().GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, fetch.Outcome,
                    $"A の取得の結末（{fetch.Message} / {Join(fetch.Details)}）");

        var arrived = Path.Combine(_dirA, FROM_B_FILE.Replace('/', Path.DirectorySeparatorChar));
        Check.True(File.Exists(arrived), $"B のファイルが A に届いている（{arrived}）");
        Check.Equal(FROM_B_CONTENT, File.ReadAllText(arrived, Encoding.UTF8).TrimEnd('\r', '\n'),
                    "届いた内容");
    }

    /// <summary>
    /// **この変更の 2 つめの要点。**
    /// `repository_creators` に載っている A が、**認証を有効にしたまま**
    /// 新しいリポジトリを作れて、そのまま owner になること。
    ///
    /// <para>
    /// 以前は `[environment.endpoint]` を外して再起動しないと作成できず、
    /// 作成後はループバックから `POST /v1/bootstrap` を叩いて owner を
    /// 登録する必要があった。いまは `ucs.auth.RebacApi/CreateResource` の中で
    /// 作成者を owner として台帳へ書くので、どちらも要らない。
    /// </para>
    ///
    /// <para>
    /// ★作った直後の**手元のトークンには新しいリポジトリが載っていない**。
    /// 窓口の招待・一覧・失効はトークンの `resources` を見る（契約 4 章）ので、
    /// 新しいリポジトリを管理するにはログインし直す必要がある。
    /// ここではその取り直しまで含めて確かめる（<see cref="_sessionA"/> は
    /// 他のテストと共有しているので触らず、使い捨てのセッションを作る）。
    /// </para>
    /// </summary>
    private static void Stage2_CreateRepositoryWhileAuthenticated()
    {
        var server   = Require(_server);
        var sessionA = Require(_sessionA);
        var accountA = Require(_accountA);

        Check.True(server.LoreAuthEnabled, "認証を有効にしたまま作れることを確かめる");

        var dir = server.CreateWorkingCopyDir(DIR_NAME_A_SECOND);
        var url = AccountsServerFixture.RepositoryUrl(REPOSITORY_NAME_SECOND);

        // 作成の可否は `[seed_auth] repository_creators` だけで決まるので、
        // 手元のトークン（1 つめのリポジトリしか載っていない）のままで作れる。
        CreateRepositoryWithToken(dir, url, RequireToken(sessionA));

        Check.True(VersionControlPaths.IsLoreWorkingCopy(dir), "2 つめが作業コピーになっている");

        _repositoryIdSecond = VersionControlPaths.ReadRepositoryId(dir);
        Check.Equal(REPOSITORY_ID_HEX_LENGTH, _repositoryIdSecond.Length,
                    "2 つめの repository_id の文字数");
        Check.True(!string.Equals(_repositoryIdSecond, _repositoryId, StringComparison.Ordinal),
                   "1 つめとは別のリポジトリ ID");

        // ★取り直したトークンに、2 つめのリポジトリが owner として載っていること。
        //   これが載る＝ CreateResource の中で台帳へ owner を書けている証拠。
        using var freshSessionA = SignIn(server, accountA);
        Check.True(freshSessionA.CurrentSession!.IsOwnerOf(_repositoryIdSecond),
                   "作成者が 2 つめのリポジトリの owner として載っている（bootstrap していない）");

        // 窓口から見ても owner が 1 人（作成者）だけ居ること。
        using var client = new AuthGatewayClient(server.AuthBaseAddress, AccountSettings.Default);
        var members = client.GetMembersAsync(RequireToken(freshSessionA), _repositoryIdSecond)
                            .GetAwaiter().GetResult();

        Check.Equal(EXPECTED_SECOND_MEMBER_COUNT, members.Count, "2 つめの参加者の人数");
        Check.Equal(NAME_A, members[0].Name, "2 つめの owner の名前");
        Check.Equal(AccountSettings.ROLE_OWNER, members[0].Role, "2 つめの owner の役割");
    }

    /// <summary>
    /// `repository_creators` に載っていない B は、新しいリポジトリを作れないこと。
    /// 判定は `ucs.auth.RebacApi/CreateResource` が行い、
    /// Lore 側は `PERMISSION_DENIED` を受けて作成を中止する。
    /// </summary>
    private static void Stage2_NonCreatorCannotCreateRepository()
    {
        var server   = Require(_server);
        var accountB = Require(_accountB);

        using var sessions = SignIn(server, accountB);

        var dir = server.CreateWorkingCopyDir(DIR_NAME_B_DENIED);
        var url = AccountsServerFixture.RepositoryUrl(REPOSITORY_NAME_DENIED);

        var error = CatchLoreError(
            () => CreateRepositoryWithToken(dir, url, RequireToken(sessions)));

        Check.True(error.Length > 0,
                   "作成を許されていない人の repository create が失敗する");
        Console.WriteLine($"  B の作成が拒否された理由: {error}");
    }

    /// <summary>
    /// **参加していないリポジトリは触れないこと。**
    /// C は 2 つめのリポジトリにだけ参加しているので、
    /// 1 つめのリポジトリはクローンできない（`CheckUserPermission` が拒否 →
    /// Lore 側で `RepositoryNotFound` へ畳まれて「Not found」になる）。
    ///
    /// <para>
    /// C が 2 つめへ参加できること自体が、
    /// 「自動登録された owner が本当に owner として振る舞える
    /// （＝招待を発行できる）」ことの確認にもなっている。
    /// </para>
    /// </summary>
    private static void Stage2_NonMemberCannotAccess()
    {
        var server   = Require(_server);
        var accountA = Require(_accountA);
        RequireText(_repositoryIdSecond, "2 つめの repository_id");

        using var client = new AuthGatewayClient(server.AuthBaseAddress, AccountSettings.Default);

        // 招待には「2 つめの owner」として載ったトークンが要るので、
        // 共有している _sessionA は触らず、使い捨てのセッションでログインし直す。
        using var ownerOfSecond = SignIn(server, accountA);

        // A（2 つめの owner）が C を 2 つめへ招待する。
        var invite = client.CreateInviteAsync(RequireToken(ownerOfSecond), new AuthInviteRequest
        {
            RepositoryId   = _repositoryIdSecond,
            Role           = AccountSettings.ROLE_MEMBER,
            ExpiresInHours = AccountSettings.Default.InviteExpiresInHours,
        }).GetAwaiter().GetResult();

        _accountC = CreateAndPersistAccount(NAME_C, TestPaths.NewDirectory("account_c"));

        var join = client.JoinAsync(new AuthJoinRequest
        {
            InviteCode = invite.InviteCode,
            Name       = _accountC.Name,
            PublicKey  = _accountC.PublicKeyBase64Url,
        }).GetAwaiter().GetResult();

        Check.Equal(_repositoryIdSecond, join.RepositoryId, "C が参加したのは 2 つめ");

        using var sessionC = SignIn(server, _accountC);
        var tokenC = RequireToken(sessionC);

        // ★1 つめのリポジトリはクローンできない。
        var destination = server.ReserveWorkingCopyDir(DIR_NAME_C_DENIED);
        var result = new LoreNativeCloner().Clone(
            new LoreCloneRequest(
                AccountsServerFixture.RepositoryUrl(REPOSITORY_NAME),
                destination,
                new LoreAccountCredential(tokenC, _accountC.Name),
                FallbackIdentity: string.Empty),
            CancellationToken.None);

        Check.True(!result.Succeeded,
                   "参加していないリポジトリはクローンできない");
        Check.True(result.MessagesContain(LORE_NOT_FOUND_MARKER),
                   $"落ち方は「Not found」＝存在を漏らさない（実際: {Join(result.Messages)}）");
    }

    /// <summary>
    /// A がロックを取り、A からは「自分」、B からは「他の人（A の名前）」に見えること。
    /// 認証が無い構成では所有者が <c>&lt;unknown&gt;</c> になり、この区別が成立しない。
    /// </summary>
    private static void Stage2_LockHolderIsOtherForB()
    {
        var providerA = Require(_providerA);
        var providerB = Require(_providerB);
        var server    = Require(_server);

        var acquire = providerA.Locks.AcquireAsync(new[] { LOCK_FILE }).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, acquire.Outcome,
                    $"A のロック取得の結末（{acquire.Message} / {Join(acquire.Details)}）");
        Check.True(acquire.Value![0].CanEdit,
                   $"A がロックを持てた（実際: {acquire.Value[0].Outcome} / 所有者 '{acquire.Value[0].Lock.Owner}'）");

        var mine = providerA.Locks.GetStatusAsync(new[] { LOCK_FILE }).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, mine.Outcome, $"A の照会（{mine.Message}）");
        Check.Equal(LockHolder.Self, mine.Value![0].Holder, "A から見た保持者");
        Check.Equal(NAME_A, mine.Value[0].Owner, "ロックの所有者名");

        var theirs = providerB.Locks.GetStatusAsync(new[] { LOCK_FILE }).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, theirs.Outcome, $"B の照会（{theirs.Message}）");
        Check.Equal(LockHolder.Other, theirs.Value![0].Holder, "B から見た保持者");
        Check.Equal(NAME_A, theirs.Value[0].Owner, "B から見た所有者名");

        // 永続化されたロックファイルにも実名が残る（認証が効いている証拠）。
        var locksJson = ReadTextIfExists(server.LocksJsonPath);
        Check.True(locksJson.Contains(NAME_A, StringComparison.Ordinal),
                   "seed_locks.json に所有者の名前が入っている");
    }

    /// <summary>
    /// 他人のロックは解放できず、owner は解放できること（契約 4 章の `permission`）。
    /// </summary>
    private static void Stage2_ReleaseOnlyByOwner()
    {
        var providerA = Require(_providerA);
        var providerB = Require(_providerB);

        var byMember = providerB.Locks.ReleaseAsync(new[] { LOCK_FILE }).GetAwaiter().GetResult();
        Check.True(byMember.Outcome != VersionControlOutcome.Success,
                   $"B（member）は他人のロックを解放できない（実際: {byMember.Outcome} / {byMember.Message}）");

        var byOwner = providerA.Locks.ReleaseAsync(new[] { LOCK_FILE }).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, byOwner.Outcome,
                    $"A（owner）は解放できる（{byOwner.Message} / {Join(byOwner.Details)}）");

        var after = providerA.Locks.GetStatusAsync(new[] { LOCK_FILE }).GetAwaiter().GetResult();
        Check.Equal(LockHolder.None, after.Value![0].Holder, "解放後はロックなし");
    }

    // ══════════════════════════════════════════════════════════
    //  ロックのゲート（保存・送信を実際に止める）
    //
    //  【なぜ実サーバで確かめるのか】
    //  判定表そのものは純関数として VersionControlTests で固定してある。
    //  ここで確かめたいのはその手前 ──
    //   ・サーバが本当に「他の人（A）のロック」として返すか
    //   ・LockGatekeeper がそれを引けて、保存と送信を止められるか
    //   ・解放したあと、止まらなくなるか
    //   ・開いたファイルのロックを自動で取り、閉じたときに外せるか
    //  偽物では「サーバが所有者名を返す」ところが再現できず、
    //  ロックの強制はまさにそこに依存している。
    // ══════════════════════════════════════════════════════════

    /// <summary>ゲートの検証で使う「B が保存しようとするファイル」の絶対パス。</summary>
    private static string GateTargetPathForB()
        => Path.Combine(_dirB, LOCK_FILE.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// A がロックしているあいだ、B の保存ゲートと送信ゲートが止まること。
    ///
    /// <para>
    /// 直前の <see cref="Stage2_ReleaseOnlyByOwner"/> で解放済みなので、
    /// ここで A が掛け直してから確かめる。
    /// </para>
    /// </summary>
    private static void Stage2_GateBlocksWhileOtherHolds()
    {
        var providerA = Require(_providerA);
        var providerB = Require(_providerB);
        var accountA  = Require(_accountA);

        // A が掛ける（＝他の人のロック）。
        var acquire = providerA.Locks.AcquireAsync(new[] { LOCK_FILE }).GetAwaiter().GetResult();
        Check.True(acquire.Value is { Count: > 0 } && acquire.Value[0].CanEdit,
                   $"A がロックを掛けられた（{acquire.Outcome} / {acquire.Message}）");

        UseGateAs(providerB, _accountB, _sessionB);
        try
        {
            // ── 保存ゲート ──
            var write = SEEDEditor.VersionControl.Locking.LockGatekeeper
                            .DecideForWriteAsync(GateTargetPathForB())
                            .GetAwaiter().GetResult();

            Check.Equal(SEEDEditor.VersionControl.Locking.LockGateAction.Block,
                        write.Action, $"B の保存ゲート（{write}）");
            Check.Equal(LockHolder.Other, write.Holder, "B から見た保持者");
            Check.Equal(accountA.Name, write.OwnerName, "B から見た所有者名");
            Check.True(write.Message.Contains(accountA.Name, StringComparison.Ordinal),
                       $"止めた文言に A の名前が入る（実際: {write.Message}）");

            // ── 送信ゲート ──
            // 送信しようとしている変更ファイルの一覧に、A がロック中のものが混ざっている想定。
            var submit = SEEDEditor.VersionControl.Locking.LockGatekeeper
                             .DecideForSubmitAsync(new[] { LOCK_FILE })
                             .GetAwaiter().GetResult();

            Check.Equal(SEEDEditor.VersionControl.Locking.LockGateAction.Block,
                        submit.Action, $"B の送信ゲート（{submit}）");
            Check.Equal(1, submit.BlockingLocks.Count, "止める原因になったロックの件数");
            Check.Equal(LOCK_FILE, submit.BlockingLocks[0].Path, "止める原因のパス");
            Check.True(submit.Message.Contains(LOCK_FILE, StringComparison.Ordinal),
                       $"止めた文言に対象パスが並ぶ（実際: {submit.Message}）");
        }
        finally
        {
            ReleaseGate();
        }
    }

    /// <summary>
    /// A が解放したあと、B の保存・送信が通ること。あわせて自動ロックの往復を確かめる。
    /// </summary>
    private static void Stage2_GateAllowsAfterRelease()
    {
        var providerA = Require(_providerA);
        var providerB = Require(_providerB);
        var accountB  = Require(_accountB);

        // A が解放する（前のテストが途中で失敗していても、ここで必ず外す）。
        var release = providerA.Locks.ReleaseAsync(new[] { LOCK_FILE }).GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, release.Outcome,
                    $"A の解放（{release.Message} / {Join(release.Details)}）");

        UseGateAs(providerB, _accountB, _sessionB);
        try
        {
            // ── 保存ゲート: 誰も持っていないので、その場で B が取って通る ──
            var write = SEEDEditor.VersionControl.Locking.LockGatekeeper
                            .DecideForWriteAsync(GateTargetPathForB())
                            .GetAwaiter().GetResult();

            Check.True(write.CanProceed, $"解放後は B が保存できる（{write}）");
            Check.Equal(SEEDEditor.VersionControl.Locking.LockGateReason.Acquired,
                        write.Reason, "その場でロックを取って通ったはず");

            var afterWrite = providerB.Locks.GetStatusAsync(new[] { LOCK_FILE })
                                            .GetAwaiter().GetResult();
            Check.Equal(LockHolder.Self, afterWrite.Value![0].Holder,
                        "保存ゲートを通ったあとは B がロックを持っている");
            Check.Equal(accountB.Name, afterWrite.Value[0].Owner, "ロックの所有者名");

            // ── 送信ゲート: 自分のロックは止める理由にならない ──
            var submit = SEEDEditor.VersionControl.Locking.LockGatekeeper
                             .DecideForSubmitAsync(new[] { LOCK_FILE })
                             .GetAwaiter().GetResult();
            Check.True(submit.CanProceed, $"自分のロックでは送信を止めない（{submit}）");
            Check.Equal(0, submit.BlockingLocks.Count, "止める原因は無いはず");

            // 保存ゲートが取ったロックは台帳に載る（＝閉じるときに自動で外れる対象）。
            Check.True(SEEDEditor.VersionControl.Locking.LockGatekeeper.IsAutoHeld(LOCK_FILE),
                       "保存ゲートが取ったロックは自動解放の対象として記録されるはず");

            // ── 自動ロック: まとめて解放して、サーバ側でも外れていること ──
            SEEDEditor.VersionControl.Locking.LockGatekeeper.ReleaseAllTracked();
            Check.True(!SEEDEditor.VersionControl.Locking.LockGatekeeper.IsAutoHeld(LOCK_FILE),
                       "解放後は台帳から消えるはず");

            var afterRelease = providerB.Locks.GetStatusAsync(new[] { LOCK_FILE })
                                              .GetAwaiter().GetResult();
            Check.Equal(LockHolder.None, afterRelease.Value![0].Holder,
                        "自動ロックの解放がサーバへ届いているはず");

            // ── 自動ロック: 開いたときの取得（本番と同じ入口を通す）──
            // TrackOpenedDocument は待たない（シーンの読み込みを止めないため）ので、
            // 台帳に載るまで短く待つ。
            SEEDEditor.VersionControl.Locking.LockGatekeeper
                      .TrackOpenedDocument(GateTargetPathForB());

            Check.True(
                WaitUntil(() => SEEDEditor.VersionControl.Locking.LockGatekeeper.IsAutoHeld(LOCK_FILE)),
                "開いたファイルのロックが自動で取得されるはず");

            var afterOpen = providerB.Locks.GetStatusAsync(new[] { LOCK_FILE })
                                           .GetAwaiter().GetResult();
            Check.Equal(LockHolder.Self, afterOpen.Value![0].Holder,
                        "自動ロックがサーバへ届いているはず");

            // ── 自動ロック: 閉じたときの解放 ──
            SEEDEditor.VersionControl.Locking.LockGatekeeper
                      .ReleaseTrackedDocument(GateTargetPathForB());

            Check.True(
                WaitUntil(() =>
                {
                    var status = providerB.Locks.GetStatusAsync(new[] { LOCK_FILE })
                                                .GetAwaiter().GetResult();
                    return status.Value is { Count: > 0 }
                           && status.Value[0].Holder == LockHolder.None;
                }),
                "閉じたら自動ロックが外れるはず");
        }
        finally
        {
            ReleaseGate();
        }
    }

    /// <summary>
    /// ゲートが使う「いまのプロバイダ」と「ログイン中のアカウント」を差し替える。
    ///
    /// <para>
    /// <see cref="SEEDEditor.VersionControl.Locking.LockGatekeeper"/> は本番と同じく
    /// <see cref="VersionControlService"/> 経由でプロバイダと資格情報を見る。
    /// テストでは実行中のエディタが無いので、検証用の差し込み口から据える。
    /// </para>
    /// </summary>
    /// <param name="provider">据えるプロバイダ。</param>
    /// <param name="account">ログイン中として扱うアカウント。</param>
    /// <param name="sessions">そのアカウントのセッション（トークンを持っている）。</param>
    private static void UseGateAs(
        LoreProvider provider, SeedAccount? account, AccountSessionManager? sessions)
    {
        var name  = account?.Name ?? string.Empty;
        var token = sessions?.TryGetAccessToken() ?? string.Empty;

        VersionControlService.UseProviderForVerification(provider);
        VersionControlService.CredentialProvider = () => new LoreAccountCredential(token, name);

        // 判定を既定（Enforce・自動ロックあり）に固定する。
        SEEDEditor.VersionControl.Locking.LockGatekeeper.UseSettingsForVerification(null);
        // 前のテストが残した照会結果・台帳を捨てる（寿命待ちをしないため）。
        SEEDEditor.VersionControl.Locking.LockGatekeeper.ResetForVerification();
    }

    /// <summary>
    /// ゲートの差し込みを元へ戻す。
    /// 戻さないと、後続のテスト（自動ログインなど）が
    /// このテストのプロバイダを掴んだままになる。
    /// </summary>
    private static void ReleaseGate()
    {
        SEEDEditor.VersionControl.Locking.LockGatekeeper.ResetForVerification();
        VersionControlService.UseProviderForVerification(null);
        VersionControlService.CredentialProvider = null;
    }

    /// <summary>
    /// 条件が満たされるまで短く待つ（自動ロックは待たない設計なので、テスト側で待つ）。
    /// </summary>
    /// <param name="condition">満たされてほしい条件。</param>
    /// <returns>期限内に満たされたら真。</returns>
    private static bool WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(GATE_WAIT_TIMEOUT_MS);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(GATE_WAIT_POLL_MS);
        }
        return condition();
    }

    /// <summary>
    /// 期限が来る前にトークンを取り直しても、新しいトークンで操作が通ること。
    ///
    /// <para>
    /// `token_ttl_hours` の下限は 1 時間なので、実サーバでは「期限切れ」そのものは試せない。
    /// 更新判定（<c>NeedsRefresh</c>）は時計を差し替えられる純関数として
    /// <see cref="GatewayTests"/> 側で固定してあるので、ここでは
    /// **取り直したトークンが本当に使える**ことだけを確かめる。
    /// </para>
    /// </summary>
    private static void Stage2_RefreshTokenKeepsWorking()
    {
        var sessionA  = Require(_sessionA);
        var providerA = Require(_providerA);

        var before = RequireToken(sessionA);

        // 期限の手前で自動更新が行うのと同じ処理を手で起こす。
        var refreshed = sessionA.RefreshNowAsync().GetAwaiter().GetResult();
        Check.True(refreshed, "トークンを取り直せた");

        var after = RequireToken(sessionA);
        Check.True(!string.Equals(before, after, StringComparison.Ordinal),
                   "取り直しで別のトークンになっている");

        // 期限（NeedsRefresh）の判定は純関数なので、ここで両側を確かめておく。
        var session = sessionA.CurrentSession!;
        Check.True(!session.NeedsRefresh(session.ExpiresAtUtc - TimeSpan.FromHours(1),
                                         AccountSettings.Default.TokenRefreshMargin),
                   "期限の 1 時間前は更新不要");
        Check.True(session.NeedsRefresh(session.ExpiresAtUtc,
                                        AccountSettings.Default.TokenRefreshMargin),
                   "期限ちょうどは更新が必要");

        var list = providerA.Locks.ListAsync().GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, list.Outcome,
                    $"新しいトークンで操作が通る（{list.Message} / {Join(list.Details)}）");
    }

    /// <summary>
    /// 参加者一覧に B が出ること、失効させると B が新しいトークンを取れなくなること、
    /// owner の権限は失効できないこと（契約 3 章）。
    ///
    /// <para>
    /// ★あわせて**失効がどこまで即座に効くか**を実測する。
    /// 権限サービスは毎回 `accounts.json` を見るので、
    /// そこを通る操作（送信 ＝ `RepositoryGet`、クローン）は
    /// **発行済みトークンが期限内でも失効した時点で拒否**される。
    /// 一方、ロックの照会や取得（`LockService`）と取得（QUIC のストレージ
    /// セッション）は、Lore 側が**トークンに載っている `resources`** だけを見るので、
    /// そのトークンの期限が切れるまでは通ってしまう。
    /// この差はここで固定しておく（契約 7 章に書いてある制約の根拠）。
    /// </para>
    /// </summary>
    private static void Stage2_MembersAndRevoke()
    {
        var server    = Require(_server);
        var sessionA  = Require(_sessionA);
        var accountB  = Require(_accountB);
        var providerB = Require(_providerB);

        var tokenA = RequireToken(sessionA);
        using var client = new AuthGatewayClient(server.AuthBaseAddress, AccountSettings.Default);

        var members = client.GetMembersAsync(tokenA, _repositoryId).GetAwaiter().GetResult();
        Check.Equal(EXPECTED_MEMBER_COUNT, members.Count, "参加者の人数");

        var memberB = members.FirstOrDefault(
            m => string.Equals(m.Name, NAME_B, StringComparison.Ordinal));
        Check.True(memberB is not null, "参加者一覧に B が出る");
        Check.Equal(AccountSettings.ROLE_MEMBER, memberB!.Role, "B の役割");
        Check.Equal(AccountSettings.MEMBER_STATUS_ACTIVE, memberB.Status, "B の状態");

        // owner の権限は失効できない（自分自身を含む）。
        var ownerRevoke = CatchGatewayError(() => client.RevokeMemberAsync(tokenA,
            new AuthRevokeRequest { RepositoryId = _repositoryId, Name = NAME_A })
            .GetAwaiter().GetResult());
        Check.Equal(AuthErrorCodes.OWNER_EXISTS, ownerRevoke.ErrorCode,
                    "owner を失効させようとしたときのエラーコード");

        // 失効の直前に、B の**期限内の**トークンで送信が通ることを確かめておく
        // （このあと同じトークンでどう変わるかを見るための基準）。
        WriteFile(_dirB, FROM_B_BEFORE_REVOKE_FILE, FROM_B_CONTENT);
        var beforeRevoke = providerB.SubmitAsync("失効前の送信").GetAwaiter().GetResult();
        Check.Equal(VersionControlOutcome.Success, beforeRevoke.Outcome,
                    $"失効前は B が送信できる（{beforeRevoke.Message} / {Join(beforeRevoke.Details)}）");

        var tokenBBeforeRevoke = RequireToken(Require(_sessionB));

        // B を失効させる。
        var revoke = client.RevokeMemberAsync(tokenA,
            new AuthRevokeRequest { RepositoryId = _repositoryId, Name = NAME_B })
            .GetAwaiter().GetResult();
        Check.Equal(NAME_B, revoke.Name, "失効させた名前");
        Check.Equal(AccountSettings.MEMBER_STATUS_REVOKED, revoke.Status, "失効後の状態");

        // ★ここが「失効が即座に効く」ことの確認。
        //   クローンはサーバの `RepositoryGet` を必ず通り、そこで権限サービスへ
        //   問い合わせが飛ぶ。台帳を毎回見るので、**トークンを取り直していなくても**
        //   失効した瞬間から拒否される。
        var reclone = new LoreNativeCloner().Clone(
            new LoreCloneRequest(
                AccountsServerFixture.RepositoryUrl(REPOSITORY_NAME),
                server.ReserveWorkingCopyDir(DIR_NAME_B_AFTER_REVOKE),
                new LoreAccountCredential(tokenBBeforeRevoke, NAME_B),
                FallbackIdentity: string.Empty),
            CancellationToken.None);
        Check.True(!reclone.Succeeded,
                   "失効した参加者は、手元のトークンが期限内でもクローンできない"
                   + $"（実際: {Join(reclone.Messages)}）");

        // ★一方、**既に繋がっているクライアントの送信**は通ってしまう（実測）。
        //   push はリポジトリのメタデータを手元に持っていれば `RepositoryGet` を
        //   呼ばないため、権限サービスに問い合わせが飛ばない。
        //   ロックの照会・取得と「最新を取得」も同様に、Lore 側は
        //   **トークンに載っている `resources`** だけを見る。
        //   つまり失効が効くのは「サーバへ問い合わせが飛ぶ操作」で、
        //   それ以外は発行済みトークンの期限（token_ttl_hours）まで残る。
        //   ここは結果を記録するだけにして、失敗にはしない
        //   （契約 `docs/seed_accounts.md` 7 章の既知の制約）。
        WriteFile(_dirB, FROM_B_AFTER_REVOKE_FILE, FROM_B_CONTENT);
        var afterRevoke = providerB.SubmitAsync("失効後の送信").GetAwaiter().GetResult();
        Console.WriteLine(
            "  [実測] 失効直後・トークンは取り直さずに送信した結果: "
            + $"{afterRevoke.Outcome}（{afterRevoke.Message}）");

        // 失効した参加者は新しいトークンを取れない。
        var challenge = client.StartLoginAsync(NAME_B).GetAwaiter().GetResult();
        var signature = accountB.SignLoginChallenge(challenge.ChallengeId, challenge.Nonce);
        var failure   = CatchGatewayError(
            () => client.CompleteLoginAsync(challenge.ChallengeId, signature)
                        .GetAwaiter().GetResult());

        Check.Equal(AuthErrorCodes.LOGIN_FAILED, failure.ErrorCode, "失効後の再ログインのエラーコード");
    }

    /// <summary>
    /// プロセスに 1 つの入口（<see cref="AccountService"/>）の自動ログイン経路が
    /// 実サーバで通り、バージョン管理層へ資格情報が配られること。
    /// </summary>
    private static void Stage2_AccountServiceAutoSignIn()
    {
        var server = Require(_server);
        RequireText(_accountDirA, "A のアカウントフォルダ");

        // 利用者設定は使い捨てフォルダへ書く（editor/settings/ には触れない）。
        var settingsDir = TestPaths.NewDirectory("editor_settings");
        var settings = new AccountEditorSettings
        {
            // 窓口は既定ポート（41350）ではないので、利用者設定の上書きで指す。
            AuthUrlOverride = server.AuthBaseAddress.ToString(),
            AutoSignIn      = true,
        };
        Check.True(settings.Save(settingsDir), "利用者設定を書けた");

        AccountService.Initialize(settingsDir, new AccountFileStore(_accountDirA));

        Check.True(AccountService.HasAccount, "保存済みのアカウントを読めた");
        Check.Equal(NAME_A, AccountService.Identity.Name, "読めたアカウント名");
        Check.True(VersionControlService.CredentialProvider is not null,
                   "バージョン管理層へ資格情報の窓口が差し込まれた");

        var remoteUrl = AccountsServerFixture.RepositoryUrl(REPOSITORY_NAME);
        var signedIn  = AccountService.AttachToProjectAsync(remoteUrl)
                                      .GetAwaiter().GetResult();

        Check.True(signedIn, $"自動ログインできた（状態: {AccountService.AuthState.Description}）");

        var credential = AccountService.GetLoreCredential();
        Check.True(credential.HasToken, "Lore へ渡す資格情報にトークンが載っている");
        Check.Equal(NAME_A, credential.AccountName, "資格情報の名前");

        // ★契約 6 章: IdentityToken と AccessToken に同じ JWT、Identity は空。
        var resolved = LoreCredentialResolver.Resolve(credential, "config-identity");
        Check.Equal(credential.AccessToken, resolved.IdentityToken, "IdentityToken に同じ JWT");
        Check.Equal(credential.AccessToken, resolved.AccessToken, "AccessToken に同じ JWT");
        Check.Equal(string.Empty, resolved.Identity, "Identity は空");

        AccountService.DetachFromProject();
        Check.True(!AccountService.GetLoreCredential().HasToken, "閉じたらトークンを捨てる");
    }

    /// <summary>
    /// 秘密がディスクへ残っていないこと（招待コードの平文・アクセストークン）。
    /// </summary>
    private static void Stage2_NoSecretsOnDisk()
    {
        var server   = Require(_server);
        var sessionA = Require(_sessionA);

        var accounts = ReadTextIfExists(server.AccountsJsonPath);
        Check.True(accounts.Length > 0, "accounts.json が読めた");

        // 招待コードは使い捨てなので平文は残らない（ハッシュで保存される契約）。
        // トークンも窓口は保存しない。
        var token = RequireToken(sessionA);
        Check.True(!accounts.Contains(token, StringComparison.Ordinal),
                   "accounts.json にアクセストークンが残っていない");

        var serverLog = server.ReadServerLog();
        Check.True(!serverLog.Contains(token, StringComparison.Ordinal),
                   "サーバのログにアクセストークンが出ていない");

        // JWKS は公開鍵なので存在してよい。alg と kid が入っていること（契約 2 章）。
        var jwks = ReadTextIfExists(server.JwksPath);
        Check.True(jwks.Contains("\"alg\"", StringComparison.Ordinal), "JWKS に alg がある");
        Check.True(jwks.Contains("\"kid\"", StringComparison.Ordinal), "JWKS に kid がある");
    }

    // ══════════════════════════════════════════════════════════
    //  後始末
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 起動したサーバと作ったオブジェクトを片付ける。2 回呼んでも安全。
    /// </summary>
    public static void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        SafeDispose(_providerA); _providerA = null;
        SafeDispose(_providerB); _providerB = null;
        SafeDispose(_sessionA);  _sessionA  = null;
        SafeDispose(_sessionB);  _sessionB  = null;
        SafeDispose(_accountA);  _accountA  = null;
        SafeDispose(_accountB);  _accountB  = null;
        SafeDispose(_accountC);  _accountC  = null;

        SafeDispose(_server);
        _server = null;
    }

    // ══════════════════════════════════════════════════════════
    //  ヘルパー
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// アカウントを作って保存し、**保存されたものを読み直して**返す
    /// （DPAPI の往復と保管の実装を実際に通す）。
    /// </summary>
    /// <param name="name">アカウント名。</param>
    /// <param name="accountDir">保管フォルダ（使い捨て）。</param>
    private static SeedAccount CreateAndPersistAccount(string name, string accountDir)
    {
        var store = new AccountFileStore(accountDir);

        using (var created = new SeedAccount(name, AccountKeyPair.Create()))
        {
            store.Save(created);
        }

        return store.Load()
            ?? throw new AssertionException($"保存したアカウントを読み直せませんでした: {name}");
    }

    /// <summary>
    /// 実クラス <see cref="AccountSessionManager"/> でログインする。
    /// </summary>
    /// <param name="server">使い捨てサーバ。</param>
    /// <param name="account">ログインするアカウント。</param>
    private static AccountSessionManager SignIn(AccountsServerFixture server, SeedAccount account)
    {
        var sessions = new AccountSessionManager(AccountSettings.Default);
        var client   = new AuthGatewayClient(server.AuthBaseAddress, AccountSettings.Default);

        var ok = sessions.SignInAsync(client, account, ownsClient: true)
                         .GetAwaiter().GetResult();

        if (!ok)
        {
            var state = sessions.State;
            sessions.Dispose();
            throw new AssertionException(
                $"'{account.Name}' がログインできませんでした"
                + $"（状態: {state.Description} / 理由: {state.Reason}）。");
        }

        return sessions;
    }

    /// <summary>
    /// セッションから Lore へ渡す資格情報を作る（未ログインなら空）。
    /// </summary>
    /// <param name="sessions">セッション管理。</param>
    private static LoreAccountCredential CredentialOf(AccountSessionManager sessions)
    {
        var session = sessions.CurrentSession;
        return session is null
            ? LoreAccountCredential.None
            : new LoreAccountCredential(session.AccessToken, session.Name);
    }

    /// <summary>
    /// 本物のバックエンドを使うプロバイダを作る。
    /// </summary>
    /// <param name="dir">作業コピー。</param>
    /// <param name="credentialProvider">資格情報の供給（null なら匿名）。</param>
    private static LoreProvider NewProvider(
        string dir, Func<LoreAccountCredential>? credentialProvider)
    {
        var settings  = VersionControlSettings.Default;
        var scheduler = new SerialWorkerScheduler(settings.ShutdownWait);
        return new LoreProvider(
            new LoreNativeBackend(dir, credentialProvider),
            scheduler, settings, ownsScheduler: true);
    }

    /// <summary>リポジトリを新規作成する（エディタの操作ではないので直接 Lore を呼ぶ）。</summary>
    /// <param name="dir">作業コピーにするフォルダ。</param>
    /// <param name="url">リポジトリ URL。</param>
    /// <param name="identity">作成時の identity。</param>
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

    /// <summary>
    /// 認証を有効にしたサーバに対して、トークン付きでリポジトリを新規作成する。
    /// </summary>
    /// <remarks>
    /// 契約 6 章のとおり <c>IdentityToken</c> と <c>AccessToken</c> の両方に
    /// 同じ JWT を入れ、<c>Identity</c> は空にする。
    /// <c>AccessToken</c> だけだと、リポジトリ ID が確定していない
    /// <c>repository create</c> では Authorization ヘッダが空になってしまう。
    /// </remarks>
    /// <param name="dir">作業コピーにするフォルダ。</param>
    /// <param name="url">リポジトリ URL。</param>
    /// <param name="token">アクセストークン（JWT）。</param>
    private static void CreateRepositoryWithToken(string dir, string url, string token)
    {
        using var globalArgs = new LoreGlobalArgs
        {
            RepositoryPath   = dir,
            WorkingDirectory = dir,
            IdentityToken    = token,
            AccessToken      = token,
        };
        using var args = new LoreRepositoryCreateArgs { RepositoryUrl = url };

        LoreApi.RepositoryCreate(globalArgs, args).Wait();
    }

    /// <summary>
    /// Lore の呼び出しが失敗することを期待し、その説明を 1 行で返す。
    /// 成功してしまったらテストを落とす。
    /// </summary>
    /// <param name="action">失敗するはずの呼び出し。</param>
    private static string CatchLoreError(Action action)
    {
        try
        {
            action();
        }
        catch (LoreError error)
        {
            return $"rc={error.ReturnCode}: "
                   + string.Join(" | ", error.Messages ?? Array.Empty<string>());
        }
        catch (AggregateException aggregate)
            when (aggregate.InnerException is LoreError inner)
        {
            return $"rc={inner.ReturnCode}: "
                   + string.Join(" | ", inner.Messages ?? Array.Empty<string>());
        }

        throw new AssertionException("失敗するはずの Lore 呼び出しが成功しました。");
    }

    /// <summary>
    /// ファイルを書く（親フォルダが無ければ作る）。
    /// </summary>
    /// <param name="root">作業コピーのルート。</param>
    /// <param name="relativePath">スラッシュ区切りの相対パス。</param>
    /// <param name="content">中身。</param>
    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>ファイルを読む（無ければ空文字）。</summary>
    /// <param name="path">ファイル。</param>
    private static string ReadTextIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 窓口の呼び出しが失敗することを期待し、その失敗を返す。
    /// 成功してしまったらテストを落とす。
    /// </summary>
    /// <param name="action">失敗するはずの呼び出し。</param>
    private static AuthGatewayException CatchGatewayError(Action action)
    {
        try
        {
            action();
        }
        catch (AuthGatewayException ex)
        {
            return ex;
        }

        throw new AssertionException("失敗するはずの呼び出しが成功しました。");
    }

    /// <summary>ログイン中のアクセストークンを取り出す（無ければテストを落とす）。</summary>
    /// <param name="sessions">セッション管理。</param>
    private static string RequireToken(AccountSessionManager sessions)
        => sessions.TryGetAccessToken()
           ?? throw new AssertionException("トークンがありません（前段のログインが失敗しています）。");

    /// <summary>前段で用意されているはずの値を取り出す。</summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="value">値。</param>
    private static T Require<T>(T? value) where T : class
        => value ?? throw new AssertionException(
               "前段の準備が失敗しているため実行できません（最初の [結合1] の失敗を見てください）。");

    /// <summary>前段で用意されているはずの文字列を確かめる。</summary>
    /// <param name="value">値。</param>
    /// <param name="what">何の値か。</param>
    private static void RequireText(string value, string what)
    {
        if (string.IsNullOrEmpty(value))
            throw new AssertionException($"前段で {what} が用意できていません。");
    }

    /// <summary>16 進小文字の 1 文字か。</summary>
    /// <param name="value">文字。</param>
    private static bool IsLowerHexDigit(char value)
        => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    /// <summary>詳細行を 1 行に畳む（診断用）。</summary>
    /// <param name="details">詳細。</param>
    private static string Join(IReadOnlyList<string>? details)
        => details is null ? string.Empty : string.Join(" | ", details);

    /// <summary>後始末での破棄（失敗は無視する）。</summary>
    /// <param name="disposable">破棄する対象。</param>
    private static void SafeDispose(IDisposable? disposable)
    {
        try { disposable?.Dispose(); } catch { /* 後始末の失敗はテスト結果に影響させない */ }
    }
}
