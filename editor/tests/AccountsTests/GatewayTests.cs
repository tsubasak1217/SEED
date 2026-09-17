// ============================================================
//  GatewayTests.cs — 窓口のアドレス決めと、偽の窓口に対するログイン
//
//  【何を守っているのか】
//  ・窓口 URL の決め方（リモートと同じホストの 41350／利用者設定の上書き）。
//    ここを取り違えると「参加はできるのにログインできない」状態になる。
//  ・本物の HTTP で join → ログイン → **期限前の自動更新** → 失効時の失敗。
//    偽の窓口は署名を本当に検証するので、署名の形式が違えば必ず落ちる。
//  ・Bearer ヘッダとクエリ名（契約 3 章）。サーバ担当が並行実装しているので、
//    エディタ側が何を送るかをここで固定しておく。
//
//  【使うポート】
//  偽の窓口は 41371 以降の空きポート。**本番（41337 / 41339 / 41350）には触れない。**
// ============================================================

using System;
using System.Threading.Tasks;
using SEEDEditor.Accounts;
using SEEDEditor.Accounts.Crypto;
using SEEDEditor.Accounts.Http;
using SEEDEditor.Accounts.Model;
using SpriteRigTests;

namespace SEEDEditor.Tests.Accounts;

/// <summary>
/// 窓口のアドレス決めとログインのテスト。
/// </summary>
public static class GatewayTests
{
    /// <summary>テストで使う名前。</summary>
    private const string TEST_NAME = "member-a";

    /// <summary>自動更新を待つ上限 [ms]（これを超えたら失敗とする）。</summary>
    private const int REFRESH_WAIT_LIMIT_MS = 8_000;

    /// <summary>待ち合わせの 1 回あたりの間隔 [ms]。</summary>
    private const int POLL_INTERVAL_MS = 50;

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        // ── アドレスの決め方（純粋な関数）──

        harness.Add("[窓口] リモートと同じホストの 41350 を既定にする", () =>
        {
            var gateway = AuthEndpointResolver.ResolveGateway(
                "lore://192.168.0.5:41337/MyProject", userOverride: null);

            Check.True(gateway is not null, "窓口の URL を決められること");
            Check.Equal("192.168.0.5", gateway!.Host, "ホスト");
            Check.Equal(AccountSettings.DEFAULT_AUTH_PORT, gateway.Port, "ポート");
            Check.Equal(AccountSettings.AUTH_URL_SCHEME, gateway.Scheme, "スキーム");
        });

        harness.Add("[窓口] ポートなしのリモートでもホストを取り出せる", () =>
        {
            Check.Equal("example.local",
                        AuthEndpointResolver.ExtractHost("lore://example.local/MyProject"),
                        "ポートなし");
            Check.Equal("127.0.0.1",
                        AuthEndpointResolver.ExtractHost("lore://127.0.0.1:41337/MyProject"),
                        "ポートあり");
            Check.Equal(string.Empty, AuthEndpointResolver.ExtractHost(null), "null");
            Check.Equal(string.Empty, AuthEndpointResolver.ExtractHost("  "), "空白だけ");
        });

        harness.Add("[窓口] 利用者設定の上書きが最優先", () =>
        {
            // ホストだけ → 既定ポートを足す。
            var hostOnly = AuthEndpointResolver.ResolveGateway(
                "lore://192.168.0.5:41337/P", "10.0.0.2");
            Check.Equal("10.0.0.2", hostOnly!.Host, "上書きのホスト");
            Check.Equal(AccountSettings.DEFAULT_AUTH_PORT, hostOnly.Port, "上書き時の既定ポート");

            // ホスト:ポート → そのまま。
            var hostPort = AuthEndpointResolver.ResolveGateway(null, "10.0.0.2:9000");
            Check.Equal(9000, hostPort!.Port, "上書きのポート");

            // 完全な URL → そのまま。
            var full = AuthEndpointResolver.ResolveGateway(null, "http://10.0.0.2:9100/");
            Check.Equal(9100, full!.Port, "完全な URL のポート");

            // リモートも上書きも無ければ決められない（匿名で動く）。
            Check.True(AuthEndpointResolver.ResolveGateway(null, null) is null,
                       "手がかりが無ければ null");
        });

        harness.Add("[窓口] クローン元 URL を組み立てる", () =>
        {
            Check.Equal(
                "lore://192.168.0.5:" + AccountSettings.DEFAULT_LORE_PORT + "/MyProject",
                AuthEndpointResolver.BuildLoreRemoteUrl("192.168.0.5", "MyProject"),
                "クローン元 URL");

            // ホストだけを入力されたとき（利用者はポートを書かない）。
            Check.Equal(
                "lore://example.local:" + AccountSettings.DEFAULT_LORE_PORT + "/P",
                AuthEndpointResolver.BuildLoreRemoteUrl("example.local", "P"),
                "ホスト名だけのとき");

            Check.Equal(string.Empty,
                        AuthEndpointResolver.BuildLoreRemoteUrl("", "P"), "ホストが空");
            Check.Equal(string.Empty,
                        AuthEndpointResolver.BuildLoreRemoteUrl("host", ""), "プロジェクト名が空");
        });

        harness.Add("[エラー] 契約のコードが利用者向けの 1 行になる", () =>
        {
            // 窓口へ繋がらないのはコードの問題ではない（匿名で動く構成）。
            Check.Equal(
                AccountMessages.GATEWAY_UNREACHABLE,
                AuthFailureText.Describe(new AuthGatewayException("x", isUnreachable: true)),
                "繋がらないとき");

            // 契約 3 章の表に載っているコードは、すべて言い換えを持つこと
            // （サーバの生のメッセージだけに頼らない）。
            var mapped = new[]
            {
                (AuthErrorCodes.LOGIN_FAILED,      AccountMessages.AUTH_REJECTED),
                (AuthErrorCodes.UNAUTHORIZED,      AccountMessages.OWNER_SESSION_EXPIRED),
                (AuthErrorCodes.FORBIDDEN,         AccountMessages.OWNER_PERMISSION_REQUIRED),
                (AuthErrorCodes.INVITE_INVALID,    AccountMessages.JOIN_INVITE_INVALID),
                (AuthErrorCodes.LOOPBACK_ONLY,     AccountMessages.OWNER_BOOTSTRAP_LOOPBACK_REQUIRED),
                (AuthErrorCodes.NOT_FOUND,         AccountMessages.OWNER_MEMBER_NOT_FOUND),
                (AuthErrorCodes.TOO_MANY_REQUESTS, AccountMessages.GATEWAY_TOO_MANY_REQUESTS),
                (AuthErrorCodes.INTERNAL,          AccountMessages.GATEWAY_INTERNAL_ERROR),
            };

            foreach (var (code, expected) in mapped)
            {
                Check.Equal(
                    expected,
                    AuthFailureText.Describe(
                        new AuthGatewayException("サーバの説明", code, 400)),
                    $"{code} の言い換え");
            }

            // 言い換えを持たないコードは、サーバの日本語をそのまま出す（握りつぶさない）。
            Check.Equal(
                "サーバの説明",
                AuthFailureText.Describe(
                    new AuthGatewayException("サーバの説明", AuthErrorCodes.NAME_TAKEN, 409)),
                "name_taken は呼び出し側が言い換える");
        });

        harness.Add("[招待] 有効時間は契約の範囲（1〜720）へ丸める", () =>
        {
            Check.Equal(AccountSettings.MIN_INVITE_EXPIRES_IN_HOURS,
                        new AccountSettings(inviteExpiresInHours: 0).InviteExpiresInHours,
                        "下限未満");
            Check.Equal(AccountSettings.MAX_INVITE_EXPIRES_IN_HOURS,
                        new AccountSettings(inviteExpiresInHours: 10_000).InviteExpiresInHours,
                        "上限超え");
            Check.Equal(AccountSettings.DEFAULT_INVITE_EXPIRES_IN_HOURS,
                        AccountSettings.Default.InviteExpiresInHours,
                        "既定値");
        });

        // ── 偽の窓口に対する本物の HTTP ──

        harness.Add("[ログイン] 偽の窓口で join → ログインできる（署名が実際に検証される）", () =>
        {
            RunSync(async () =>
            {
                using var gateway = new FakeAuthGateway();
                using var account = new SeedAccount(TEST_NAME, AccountKeyPair.Create());
                using var client  = new AuthGatewayClient(gateway.BaseAddress);

                var health = await client.GetHealthAsync().ConfigureAwait(false);
                Check.Equal("ok", health.Status, "health の応答");

                var joined = await client.JoinAsync(new AuthJoinRequest
                {
                    InviteCode = gateway.InviteCode,
                    Name       = account.Name,
                    PublicKey  = account.PublicKeyBase64Url,
                }).ConfigureAwait(false);
                Check.Equal(gateway.ProjectName, joined.ProjectName, "参加したプロジェクト名");

                using var sessions = new AccountSessionManager(NewFastSettings(gateway));
                var ok = await sessions.SignInAsync(client, account, ownsClient: false)
                                       .ConfigureAwait(false);

                Check.True(ok, "ログインできること");
                Check.Equal(AccountAuthPhase.SignedIn, sessions.State.Phase, "状態");
                Check.Equal(TEST_NAME, sessions.SignedInName, "ログイン中の名前");
                Check.True(!string.IsNullOrEmpty(sessions.TryGetAccessToken()), "トークンを持つこと");

                var session = sessions.CurrentSession;
                Check.True(session is not null, "セッションがあること");
                Check.Equal(AccountSettings.ROLE_MEMBER,
                            session!.RoleFor(gateway.RepositoryId), "そのリポジトリでの役割");
                Check.True(!session.IsOwnerOf(gateway.RepositoryId), "member はオーナーではない");
            });
        });

        harness.Add("[ログイン] 登録していない名前では失敗する", () =>
        {
            RunSync(async () =>
            {
                using var gateway = new FakeAuthGateway();
                using var account = new SeedAccount("unknown-user", AccountKeyPair.Create());
                using var client  = new AuthGatewayClient(gateway.BaseAddress);
                using var sessions = new AccountSessionManager(NewFastSettings(gateway));

                var ok = await sessions.SignInAsync(client, account, ownsClient: false)
                                       .ConfigureAwait(false);

                Check.True(!ok, "ログインできないこと");
                Check.Equal(AccountAuthPhase.Failed, sessions.State.Phase, "状態");
                Check.True(sessions.TryGetAccessToken() is null, "トークンを持たないこと");
            });
        });

        harness.Add("[ログイン] 窓口が無ければ匿名（失敗ではない）として扱う", () =>
        {
            RunSync(async () =>
            {
                // 立てていないポートを指す（接続できない）。
                var dead = new Uri("http://127.0.0.1:41399/");
                using var account  = new SeedAccount(TEST_NAME, AccountKeyPair.Create());
                using var client   = new AuthGatewayClient(dead, NewShortTimeoutSettings());
                using var sessions = new AccountSessionManager(AccountSettings.Default);

                var ok = await sessions.SignInAsync(client, account, ownsClient: false)
                                       .ConfigureAwait(false);

                Check.True(!ok, "ログインできないこと");
                Check.Equal(AccountAuthPhase.NotSignedIn, sessions.State.Phase,
                            "窓口が無いのは失敗ではなく匿名");
                Check.Equal(AccountMessages.AUTH_GATEWAY_ABSENT, sessions.State.Reason, "理由");
            });
        });

        harness.Add("[自動更新] 期限の手前でトークンを取り直す", () =>
        {
            RunSync(async () =>
            {
                using var gateway = new FakeAuthGateway { TokenTtlSeconds = 2 };
                using var account = new SeedAccount(TEST_NAME, AccountKeyPair.Create());
                gateway.Register(account.Name, account.PublicKeyBase64Url);

                using var client = new AuthGatewayClient(gateway.BaseAddress);

                // 期限 2 秒・猶予 1.5 秒 → 約 0.5 秒後に自動更新が走るはず。
                using var sessions = new AccountSessionManager(NewFastSettings(gateway));

                Check.True(await sessions.SignInAsync(client, account, ownsClient: false)
                                         .ConfigureAwait(false),
                           "最初のログイン");
                Check.Equal(1, gateway.LoginCount, "ログイン回数（この時点では 1 回）");

                var firstToken = sessions.TryGetAccessToken();

                // タイマーによる自動更新を待つ（呼び出し側は何もしない）。
                var refreshed = await WaitUntilAsync(() => gateway.LoginCount >= 2)
                    .ConfigureAwait(false);

                Check.True(refreshed, "期限の手前で自動的に取り直されること");
                Check.Equal(AccountAuthPhase.SignedIn, sessions.State.Phase, "更新後も ログイン中");
                Check.True(sessions.TryGetAccessToken() != firstToken,
                           "新しいトークンに差し替わっていること");
            });
        });

        harness.Add("[失効] 失効した後は取り直せず、期限が切れたらトークンを捨てる", () =>
        {
            RunSync(async () =>
            {
                // 期限を 1 秒にして、切れた後の振る舞いを短時間で確かめる。
                using var gateway = new FakeAuthGateway { TokenTtlSeconds = 1 };
                using var account = new SeedAccount(TEST_NAME, AccountKeyPair.Create());
                gateway.Register(account.Name, account.PublicKeyBase64Url);

                using var client = new AuthGatewayClient(gateway.BaseAddress);

                // 自動更新が割り込まないよう、猶予を 0 にして更新間隔を長く取る。
                using var sessions = new AccountSessionManager(new AccountSettings(
                    tokenRefreshMargin:   TimeSpan.Zero,
                    tokenRetryInterval:   TimeSpan.FromMinutes(10),
                    minTokenRefreshDelay: TimeSpan.FromMinutes(10)));

                Check.True(await sessions.SignInAsync(client, account, ownsClient: false)
                                         .ConfigureAwait(false),
                           "最初のログイン");

                // オーナーが参加を取り消した。
                gateway.Revoke(account.Name);

                // 期限が切れるまで待つ（切れた時点で CurrentSession は null になる）。
                var expired = await WaitUntilAsync(() => sessions.TryGetAccessToken() is null)
                    .ConfigureAwait(false);
                Check.True(expired, "期限切れでトークンが使えなくなること");

                // 取り直そうとしても失効しているので失敗する。
                var refreshed = await sessions.RefreshNowAsync().ConfigureAwait(false);
                Check.True(!refreshed, "失効後は取り直せないこと");
                Check.Equal(AccountAuthPhase.Failed, sessions.State.Phase, "状態");
                Check.True(sessions.TryGetAccessToken() is null, "トークンを持たないこと");
            });
        });

        harness.Add("[オーナー] bootstrap → 招待発行 → 一覧 → 失効（Bearer とクエリの形）", () =>
        {
            RunSync(async () =>
            {
                using var gateway = new FakeAuthGateway();
                using var owner   = new SeedAccount("owner-a", AccountKeyPair.Create());
                using var client  = new AuthGatewayClient(gateway.BaseAddress);

                // オーナー登録（この時点ではトークンを持っていない）。
                var bootstrap = await client.BootstrapAsync(new AuthBootstrapRequest
                {
                    Name         = owner.Name,
                    PublicKey    = owner.PublicKeyBase64Url,
                    RepositoryId = gateway.RepositoryId,
                    ProjectName  = gateway.ProjectName,
                }).ConfigureAwait(false);
                Check.Equal(AccountSettings.ROLE_OWNER, bootstrap.Role, "役割");

                // 2 回目は 409 owner_exists。
                var conflicted = false;
                try
                {
                    await client.BootstrapAsync(new AuthBootstrapRequest
                    {
                        Name         = "owner-b",
                        PublicKey    = owner.PublicKeyBase64Url,
                        RepositoryId = gateway.RepositoryId,
                        ProjectName  = gateway.ProjectName,
                    }).ConfigureAwait(false);
                }
                catch (AuthGatewayException ex)
                {
                    conflicted = ex.Is(AuthErrorCodes.OWNER_EXISTS);
                }
                Check.True(conflicted, "2 人目のオーナー登録は owner_exists で弾かれること");

                // オーナーとしてログインし、トークンを得る。
                using var sessions = new AccountSessionManager(NewFastSettings(gateway));
                Check.True(await sessions.SignInAsync(client, owner, ownsClient: false)
                                         .ConfigureAwait(false),
                           "オーナーのログイン");
                Check.True(sessions.CurrentSession!.IsOwnerOf(gateway.RepositoryId),
                           "そのリポジトリのオーナーと判定できること");

                var token = sessions.TryGetAccessToken()!;

                // 招待コードの発行。
                var invite = await client.CreateInviteAsync(token, new AuthInviteRequest
                {
                    RepositoryId   = gateway.RepositoryId,
                    Role           = AccountSettings.ROLE_MEMBER,
                    ExpiresInHours = AccountSettings.DEFAULT_INVITE_EXPIRES_IN_HOURS,
                }).ConfigureAwait(false);

                Check.Equal(gateway.LastIssuedInviteCode, invite.InviteCode, "招待コード");
                Check.Equal("Bearer " + token, gateway.LastAuthorizationHeader,
                            "Authorization ヘッダの形");

                // 参加者の一覧（クエリ名が契約どおりか）。
                var members = await client.GetMembersAsync(token, gateway.RepositoryId)
                                          .ConfigureAwait(false);
                Check.True(members.Count >= 1, "参加者が 1 人以上いること");
                Check.True(gateway.LastMembersQuery.Contains("repository_id=", StringComparison.Ordinal),
                           "クエリ名が repository_id であること");

                // 失効。
                var revoked = await client.RevokeMemberAsync(token, new AuthRevokeRequest
                {
                    RepositoryId = gateway.RepositoryId,
                    Name         = "someone",
                }).ConfigureAwait(false);
                Check.Equal(AccountSettings.MEMBER_STATUS_REVOKED, revoked.Status, "失効後の状態");
            });
        });

        harness.Add("[権限] Bearer が無いと拒否される", () =>
        {
            RunSync(async () =>
            {
                using var gateway = new FakeAuthGateway();
                using var client  = new AuthGatewayClient(gateway.BaseAddress);

                var rejected = false;
                try
                {
                    await client.GetMembersAsync("not-a-real-token", gateway.RepositoryId)
                                .ConfigureAwait(false);
                }
                catch (AuthGatewayException ex)
                {
                    rejected = ex.StatusCode == 401;
                }
                Check.True(rejected, "無効なトークンでは 401 になること");
            });
        });
    }

    // ── ヘルパー ────────────────────────────────────────────

    /// <summary>
    /// 自動更新が短時間で走る設定を作る。
    /// 期限は偽の窓口の <see cref="FakeAuthGateway.TokenTtlSeconds"/> に合わせる。
    /// </summary>
    /// <param name="gateway">偽の窓口。</param>
    private static AccountSettings NewFastSettings(FakeAuthGateway gateway)
        => new(
            tokenRefreshMargin:   TimeSpan.FromSeconds(gateway.TokenTtlSeconds * 0.75),
            tokenRetryInterval:   TimeSpan.FromMilliseconds(200),
            minTokenRefreshDelay: TimeSpan.FromMilliseconds(100));

    /// <summary>窓口が無いときに長く待たない設定。</summary>
    private static AccountSettings NewShortTimeoutSettings()
        => new(httpTimeout: TimeSpan.FromSeconds(2));

    /// <summary>
    /// 条件が満たされるまで待つ（上限つき）。
    /// </summary>
    /// <param name="condition">待つ条件。</param>
    /// <returns>満たされたら真、時間切れなら偽。</returns>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var waited = 0;
        while (waited < REFRESH_WAIT_LIMIT_MS)
        {
            if (condition()) return true;
            await Task.Delay(POLL_INTERVAL_MS).ConfigureAwait(false);
            waited += POLL_INTERVAL_MS;
        }
        return condition();
    }

    /// <summary>
    /// 非同期のテスト本体を同期的に走らせる。
    /// TestHarness は Action しか受けないので、ここで橋渡しする
    /// （例外は AggregateException を剥がしてそのまま上げる）。
    /// </summary>
    /// <param name="body">テスト本体。</param>
    private static void RunSync(Func<Task> body)
    {
        try
        {
            body().GetAwaiter().GetResult();
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
