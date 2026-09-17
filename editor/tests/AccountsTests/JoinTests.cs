// ============================================================
//  JoinTests.cs — 「プロジェクトに参加」の段取り
//
//  【何を守っているのか】
//  join → ログイン → **トークン付きでクローン** → .seedproj を探す、という流れと、
//  失敗したときに利用者へ何と言うか。GUI を起動せずに全分岐を踏む。
//
//  【クローンは偽物】
//  ILoreCloner を偽物に差し替える。実サーバにも Lore にも触らない
//  （本番ポート 41337 へは接続しない）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Accounts;
using SEEDEditor.Accounts.Crypto;
using SEEDEditor.Accounts.Http;
using SEEDEditor.Accounts.Model;
using SEEDEditor.VersionControl.Lore.Backend;
using SpriteRigTests;

namespace SEEDEditor.Tests.Accounts;

/// <summary>
/// 参加の段取りのテスト。
/// </summary>
public static class JoinTests
{
    /// <summary>テストで使うプロジェクトファイルの拡張子。</summary>
    private const string PROJECT_FILE_EXTENSION = ".seedproj";

    /// <summary>テストで使う名前。</summary>
    private const string TEST_NAME = "joiner";

    /// <summary>
    /// 偽のクローン。依頼内容を覚えるだけで、実際には空のプロジェクトを置く。
    /// </summary>
    private sealed class FakeCloner : ILoreCloner
    {
        /// <summary>最後に受け取った依頼（null なら一度も呼ばれていない）。</summary>
        public LoreCloneRequest? LastRequest { get; private set; }

        /// <summary>クローンを失敗させるか。</summary>
        public bool ShouldFail { get; set; }

        /// <summary>成功時に置くプロジェクトファイル名（空なら置かない）。</summary>
        public string ProjectFileName { get; set; } = "Cloned" + PROJECT_FILE_EXTENSION;

        /// <summary>クローンする（ふりをする）。</summary>
        /// <param name="request">依頼内容。</param>
        /// <param name="cancellationToken">中断用。</param>
        public LoreCallResult Clone(LoreCloneRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;

            if (ShouldFail)
            {
                return LoreCallResult.Failure(
                    LoreCallResult.RETURN_CODE_INTERNAL_FAILURE,
                    new[] { "接続できませんでした" });
            }

            Directory.CreateDirectory(request.DestinationDir);
            if (ProjectFileName.Length > 0)
            {
                File.WriteAllText(
                    Path.Combine(request.DestinationDir, ProjectFileName), "{}");
            }
            return LoreCallResult.Success;
        }
    }

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("[参加] 招待コード → ログイン → トークン付きクローン → .seedproj を返す", () =>
        {
            RunSync(async () =>
            {
                using var gateway = new FakeAuthGateway();
                using var account = new SeedAccount(TEST_NAME, AccountKeyPair.Create());
                var cloner        = new FakeCloner();
                var destination   = Path.Combine(TestPaths.NewDirectory("join_ok"), "project");

                var result = await ProjectJoinService.JoinAsync(
                    new JoinProjectRequest(HostOf(gateway), gateway.InviteCode, destination),
                    account, cloner, PROJECT_FILE_EXTENSION).ConfigureAwait(false);

                Check.True(result.Success, "参加できること: " + result.Message);
                Check.Equal(gateway.ProjectName, result.ProjectName, "プロジェクト名");
                Check.Equal(gateway.RepositoryId, result.RepositoryId, "リポジトリ ID");
                Check.True(result.ProjectFilePath.EndsWith(PROJECT_FILE_EXTENSION,
                                                           StringComparison.OrdinalIgnoreCase),
                           "見つかったプロジェクトファイル");

                // ★クローンにトークンが渡っていること。
                Check.True(cloner.LastRequest is not null, "クローンが呼ばれること");
                var request = cloner.LastRequest!.Value;
                Check.True(request.Account.HasToken, "クローンへトークンが渡ること");
                Check.Equal(TEST_NAME, request.Account.AccountName, "クローンへ渡る名前");
                Check.Equal(
                    "lore://" + AuthEndpointResolver.ExtractHost(HostOf(gateway))
                    + ":" + AccountSettings.DEFAULT_LORE_PORT + "/" + gateway.ProjectName,
                    request.RemoteUrl, "クローン元 URL");

                // そのトークンを共通引数へ載せると、2 つのトークンに同じ JWT が入り
                // identity は空になる。★clone は IdentityToken が無いと
                // `authorization header required` で失敗する（契約 6 章）。
                var credentials = LoreCredentialResolver.Resolve(
                    request.Account, request.FallbackIdentity);
                Check.True(credentials.AccessToken.Length > 0, "AccessToken が入ること");
                Check.Equal(credentials.AccessToken, credentials.IdentityToken,
                            "IdentityToken にも同じ JWT が入ること（clone に要る）");
                Check.Equal(string.Empty, credentials.Identity, "Identity は空にすること");
            });
        });

        harness.Add("[参加] 招待コードが違えば分かる文言で失敗する", () =>
        {
            RunSync(async () =>
            {
                using var gateway = new FakeAuthGateway();
                using var account = new SeedAccount(TEST_NAME, AccountKeyPair.Create());
                var cloner        = new FakeCloner();
                var destination   = Path.Combine(TestPaths.NewDirectory("join_bad_invite"), "p");

                var result = await ProjectJoinService.JoinAsync(
                    new JoinProjectRequest(HostOf(gateway), "WRONG-CODE", destination),
                    account, cloner, PROJECT_FILE_EXTENSION).ConfigureAwait(false);

                Check.True(!result.Success, "失敗すること");
                Check.Equal(AccountMessages.JOIN_INVITE_INVALID, result.Message, "文言");
                Check.True(cloner.LastRequest is null, "クローンまで進まないこと");
            });
        });

        harness.Add("[参加] 名前が衝突したら、その名前を添えて知らせる", () =>
        {
            RunSync(async () =>
            {
                using var gateway = new FakeAuthGateway();
                using var other   = new SeedAccount(TEST_NAME, AccountKeyPair.Create());
                using var account = new SeedAccount(TEST_NAME, AccountKeyPair.Create());

                // 同じ名前・別の鍵が先に登録されている状態を作る。
                gateway.Register(other.Name, other.PublicKeyBase64Url);

                var cloner      = new FakeCloner();
                var destination = Path.Combine(TestPaths.NewDirectory("join_name_taken"), "p");

                var result = await ProjectJoinService.JoinAsync(
                    new JoinProjectRequest(HostOf(gateway), gateway.InviteCode, destination),
                    account, cloner, PROJECT_FILE_EXTENSION).ConfigureAwait(false);

                Check.True(!result.Success, "失敗すること");
                Check.True(result.Message.Contains(TEST_NAME, StringComparison.Ordinal),
                           "文言に名前が入ること");
            });
        });

        harness.Add("[参加] 保存先が空でなければ、サーバへ行く前に止める", () =>
        {
            RunSync(async () =>
            {
                using var gateway = new FakeAuthGateway();
                using var account = new SeedAccount(TEST_NAME, AccountKeyPair.Create());
                var cloner        = new FakeCloner();

                var destination = TestPaths.NewDirectory("join_not_empty");
                File.WriteAllText(Path.Combine(destination, "existing.txt"), "大事なファイル");

                var result = await ProjectJoinService.JoinAsync(
                    new JoinProjectRequest(HostOf(gateway), gateway.InviteCode, destination),
                    account, cloner, PROJECT_FILE_EXTENSION).ConfigureAwait(false);

                Check.True(!result.Success, "失敗すること");
                Check.True(result.Message.Contains(destination, StringComparison.OrdinalIgnoreCase),
                           "文言にフォルダ名が入ること");
                Check.True(cloner.LastRequest is null, "クローンを呼ばないこと");
                Check.True(File.Exists(Path.Combine(destination, "existing.txt")),
                           "既存のファイルを消さないこと");
            });
        });

        harness.Add("[参加] 取得できても .seedproj が無ければ失敗として知らせる", () =>
        {
            RunSync(async () =>
            {
                using var gateway = new FakeAuthGateway();
                using var account = new SeedAccount(TEST_NAME, AccountKeyPair.Create());
                var cloner        = new FakeCloner { ProjectFileName = string.Empty };
                var destination   = Path.Combine(TestPaths.NewDirectory("join_no_project"), "p");

                var result = await ProjectJoinService.JoinAsync(
                    new JoinProjectRequest(HostOf(gateway), gateway.InviteCode, destination),
                    account, cloner, PROJECT_FILE_EXTENSION).ConfigureAwait(false);

                Check.True(!result.Success, "失敗すること");
                Check.Equal(AccountMessages.JOIN_PROJECT_FILE_MISSING, result.Message, "文言");
                Check.Equal(gateway.ProjectName, result.ProjectName, "プロジェクト名は分かること");
            });
        });

        harness.Add("[参加] クローンが失敗したら理由を添えて知らせる", () =>
        {
            RunSync(async () =>
            {
                using var gateway = new FakeAuthGateway();
                using var account = new SeedAccount(TEST_NAME, AccountKeyPair.Create());
                var cloner        = new FakeCloner { ShouldFail = true };
                var destination   = Path.Combine(TestPaths.NewDirectory("join_clone_fail"), "p");

                var result = await ProjectJoinService.JoinAsync(
                    new JoinProjectRequest(HostOf(gateway), gateway.InviteCode, destination),
                    account, cloner, PROJECT_FILE_EXTENSION).ConfigureAwait(false);

                Check.True(!result.Success, "失敗すること");
                Check.True(result.Message.Contains("接続できませんでした", StringComparison.Ordinal),
                           "Lore のメッセージが伝わること");
            });
        });

        harness.Add("[参加] 入力が足りなければ、それぞれの案内を出す", () =>
        {
            RunSync(async () =>
            {
                using var account = new SeedAccount(TEST_NAME, AccountKeyPair.Create());
                var cloner        = new FakeCloner();

                var noHost = await ProjectJoinService.JoinAsync(
                    new JoinProjectRequest("", "code", "C:/tmp/x"),
                    account, cloner, PROJECT_FILE_EXTENSION).ConfigureAwait(false);
                Check.Equal(AccountMessages.JOIN_HOST_REQUIRED, noHost.Message, "アドレス未入力");

                var noInvite = await ProjectJoinService.JoinAsync(
                    new JoinProjectRequest("host", "", "C:/tmp/x"),
                    account, cloner, PROJECT_FILE_EXTENSION).ConfigureAwait(false);
                Check.Equal(AccountMessages.JOIN_INVITE_REQUIRED, noInvite.Message, "招待コード未入力");

                var noDestination = await ProjectJoinService.JoinAsync(
                    new JoinProjectRequest("host", "code", ""),
                    account, cloner, PROJECT_FILE_EXTENSION).ConfigureAwait(false);
                Check.Equal(AccountMessages.JOIN_DESTINATION_REQUIRED, noDestination.Message,
                            "保存先未入力");

                Check.True(cloner.LastRequest is null, "どの場合もクローンを呼ばないこと");
            });
        });

        harness.Add("[参加] 長すぎる招待コードは送る前に弾く", () =>
        {
            RunSync(async () =>
            {
                using var account = new SeedAccount(TEST_NAME, AccountKeyPair.Create());
                var cloner        = new FakeCloner();

                // 貼り付けで余計な文字が混ざった想定（サーバは 400 で断る）。
                var tooLong = new string('A', AuthInputLimits.INVITE_CODE_MAX_LENGTH + 1);

                var result = await ProjectJoinService.JoinAsync(
                    new JoinProjectRequest("127.0.0.1:41399", tooLong, "C:/tmp/x"),
                    account, cloner, PROJECT_FILE_EXTENSION).ConfigureAwait(false);

                Check.True(!result.Success, "失敗すること");
                Check.True(result.Message.Contains(
                               AuthInputLimits.INVITE_CODE_MAX_LENGTH.ToString(
                                   System.Globalization.CultureInfo.CurrentCulture),
                               StringComparison.Ordinal),
                           "上限の文字数を伝えること");
                Check.True(cloner.LastRequest is null, "クローンを呼ばないこと");
            });
        });

        harness.Add("[参加] .seedproj は直下と 1 階層下まで探す", () =>
        {
            var root = TestPaths.NewDirectory("find_project");

            // 直下にある場合。
            var direct = Path.Combine(root, "direct");
            Directory.CreateDirectory(direct);
            File.WriteAllText(Path.Combine(direct, "A" + PROJECT_FILE_EXTENSION), "{}");
            Check.True(
                ProjectJoinService.FindProjectFile(direct, PROJECT_FILE_EXTENSION)
                                  .EndsWith("A" + PROJECT_FILE_EXTENSION, StringComparison.Ordinal),
                "直下のプロジェクトファイル");

            // 1 階層下にある場合。
            var nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(Path.Combine(nested, "inner"));
            File.WriteAllText(Path.Combine(nested, "inner", "B" + PROJECT_FILE_EXTENSION), "{}");
            Check.True(
                ProjectJoinService.FindProjectFile(nested, PROJECT_FILE_EXTENSION)
                                  .EndsWith("B" + PROJECT_FILE_EXTENSION, StringComparison.Ordinal),
                "1 階層下のプロジェクトファイル");

            // 2 階層下は探さない（別プロジェクトを拾わないため）。
            var deep = Path.Combine(root, "deep");
            Directory.CreateDirectory(Path.Combine(deep, "a", "b"));
            File.WriteAllText(Path.Combine(deep, "a", "b", "C" + PROJECT_FILE_EXTENSION), "{}");
            Check.Equal(string.Empty,
                        ProjectJoinService.FindProjectFile(deep, PROJECT_FILE_EXTENSION),
                        "2 階層下は探さない");
        });
    }

    // ── ヘルパー ────────────────────────────────────────────

    /// <summary>偽の窓口を指す「ホスト:ポート」表記を作る。</summary>
    /// <param name="gateway">偽の窓口。</param>
    private static string HostOf(FakeAuthGateway gateway)
        => gateway.BaseAddress.Host + ":" + gateway.BaseAddress.Port;

    /// <summary>非同期のテスト本体を同期的に走らせる。</summary>
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
