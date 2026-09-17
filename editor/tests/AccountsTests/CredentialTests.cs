// ============================================================
//  CredentialTests.cs — Lore への資格情報の受け渡し
//
//  【何を守っているのか】
//  契約（docs/seed_accounts.md 6 章）と LoreVcs の XML ドキュメントより:
//    ・**IdentityToken と AccessToken の両方に同じ JWT** を入れる。
//      AccessToken だけだと、リポジトリ ID が確定していない呼び出し
//      （repository create / repository list / clone）で Authorization ヘッダが
//      空になり `authorization header required` で失敗する（実サーバで確認済み）。
//    ・**Identity は空**。渡すと「トークンが identity を名乗っている」という
//      排他エラーで弾かれる。
//  この判断は LoreNativeBackend（LoreVcs 依存）の中にあるため、
//  規則そのものは LoreCredentialResolver（純関数）へ出して固定する。
//
//  加えて、実物の LoreNativeBackend を一時フォルダの `.lore/config.toml` の上に作り、
//  共通引数へ載る値が期待どおりかを確かめる（Lore の呼び出しは行わない）。
//
//  【ロックの「自分／他の人」】
//  ログイン中はサーバがロックの所有者にアカウント名を記録する。
//  比較に使う identity がアカウント名にならないと、
//  **自分で取ったロックが「他の人」に見える**ので、そこも固定する。
// ============================================================

using System;
using System.IO;
using SEEDEditor.Accounts.Http;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Lore.Backend;
using SpriteRigTests;

namespace SEEDEditor.Tests.Accounts;

/// <summary>
/// 資格情報の受け渡しのテスト。
/// </summary>
public static class CredentialTests
{
    /// <summary>テストで使うアクセストークン（本物ではない）。</summary>
    private const string TEST_TOKEN = "test.access.token";

    /// <summary>テストで使うアカウント名。</summary>
    private const string TEST_ACCOUNT_NAME = "tsubasa";

    /// <summary>`.lore/config.toml` に書く identity。</summary>
    private const string CONFIG_IDENTITY = "anonymous-identity";

    /// <summary>`.lore/config.toml` に書くリモート URL。</summary>
    private const string CONFIG_REMOTE_URL = "lore://127.0.0.1:41337/TestProject";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("[資格情報] トークンがあれば 2 つのトークンへ同じ値が入り、Identity は空になる", () =>
        {
            var account     = new LoreAccountCredential(TEST_TOKEN, TEST_ACCOUNT_NAME);
            var credentials = LoreCredentialResolver.Resolve(account, CONFIG_IDENTITY);

            Check.Equal(TEST_TOKEN, credentials.AccessToken, "AccessToken");
            // ★clone（リポジトリ ID が未確定の呼び出し）はこちらしか見ない。
            Check.Equal(TEST_TOKEN, credentials.IdentityToken, "IdentityToken");
            Check.Equal(credentials.AccessToken, credentials.IdentityToken,
                        "2 つのトークンは同じ JWT であること");
            Check.Equal(string.Empty, credentials.Identity,
                        "Identity（トークンと併用してはいけない）");
        });

        harness.Add("[資格情報] トークンが無ければ config の identity を使う", () =>
        {
            var credentials = LoreCredentialResolver.Resolve(
                LoreAccountCredential.None, CONFIG_IDENTITY);

            Check.Equal(string.Empty, credentials.AccessToken, "AccessToken は空");
            Check.Equal(string.Empty, credentials.IdentityToken, "IdentityToken も空");
            Check.Equal(CONFIG_IDENTITY, credentials.Identity, "Identity は config の値");
        });

        harness.Add("[資格情報] 空白だけのトークンはトークンとして扱わない", () =>
        {
            var blank       = new LoreAccountCredential("   ", TEST_ACCOUNT_NAME);
            var credentials = LoreCredentialResolver.Resolve(blank, CONFIG_IDENTITY);

            Check.True(!blank.HasToken, "空白だけならトークン無しと判定すること");
            Check.Equal(string.Empty, credentials.AccessToken, "AccessToken は空");
            Check.Equal(string.Empty, credentials.IdentityToken, "IdentityToken も空");
            Check.Equal(CONFIG_IDENTITY, credentials.Identity, "Identity は config の値");
        });

        harness.Add("[資格情報] config の identity が無くても落ちない", () =>
        {
            var credentials = LoreCredentialResolver.Resolve(LoreAccountCredential.None, null);
            Check.Equal(string.Empty, credentials.AccessToken, "AccessToken は空");
            Check.Equal(string.Empty, credentials.IdentityToken, "IdentityToken も空");
            Check.Equal(string.Empty, credentials.Identity, "Identity も空");
        });

        harness.Add("[表示 identity] ログイン中はアカウント名、匿名なら config の identity", () =>
        {
            Check.Equal(
                TEST_ACCOUNT_NAME,
                LoreCredentialResolver.ResolveDisplayIdentity(
                    new LoreAccountCredential(TEST_TOKEN, TEST_ACCOUNT_NAME), CONFIG_IDENTITY),
                "ログイン中はアカウント名");

            Check.Equal(
                CONFIG_IDENTITY,
                LoreCredentialResolver.ResolveDisplayIdentity(
                    LoreAccountCredential.None, CONFIG_IDENTITY),
                "匿名なら config の identity");

            // トークンはあるが名前が空（サーバの応答が欠けている）なら、
            // 名前を捏造せず config の値へ落とす。
            Check.Equal(
                CONFIG_IDENTITY,
                LoreCredentialResolver.ResolveDisplayIdentity(
                    new LoreAccountCredential(TEST_TOKEN, string.Empty), CONFIG_IDENTITY),
                "名前が無ければ config の identity");
        });

        harness.Add("[バックエンド] 実物の LoreNativeBackend でも同じ結果になる", () =>
        {
            var root = NewWorkingCopy();

            // 未ログイン: config の identity を使い、トークンは載せない。
            using (var anonymous = new LoreNativeBackend(root))
            {
                Check.Equal(CONFIG_REMOTE_URL, anonymous.RemoteUrl, "リモート URL");
                Check.Equal(CONFIG_IDENTITY, anonymous.ConfigIdentity, "config の identity");
                Check.Equal(CONFIG_IDENTITY, anonymous.Identity, "表示に使う identity");
                Check.Equal(string.Empty, anonymous.CurrentCredentials.AccessToken,
                            "AccessToken は空");
                Check.Equal(string.Empty, anonymous.CurrentCredentials.IdentityToken,
                            "IdentityToken も空");
                Check.Equal(CONFIG_IDENTITY, anonymous.CurrentCredentials.Identity,
                            "Identity は config の値");
            }

            // ログイン中: 2 つのトークンに同じ JWT、Identity は空。表示はアカウント名。
            using (var signedIn = new LoreNativeBackend(
                root, () => new LoreAccountCredential(TEST_TOKEN, TEST_ACCOUNT_NAME)))
            {
                Check.Equal(TEST_ACCOUNT_NAME, signedIn.Identity, "表示に使う identity");
                Check.Equal(TEST_TOKEN, signedIn.CurrentCredentials.AccessToken, "AccessToken");
                Check.Equal(TEST_TOKEN, signedIn.CurrentCredentials.IdentityToken, "IdentityToken");
                Check.Equal(string.Empty, signedIn.CurrentCredentials.Identity,
                            "Identity（トークンと併用してはいけない）");
                Check.Equal(CONFIG_IDENTITY, signedIn.ConfigIdentity,
                            "config の identity は保持したままであること");
            }
        });

        harness.Add("[バックエンド] 資格情報の取得が失敗しても匿名で動き続ける", () =>
        {
            var root = NewWorkingCopy();

            using var backend = new LoreNativeBackend(
                root, () => throw new InvalidOperationException("取得に失敗"));

            // 例外がバージョン管理の層へ漏れると、送信も取得もできなくなる。
            Check.Equal(CONFIG_IDENTITY, backend.Identity, "匿名として config の identity を使う");
            Check.Equal(string.Empty, backend.CurrentCredentials.AccessToken, "AccessToken は空");
        });

        harness.Add("[バックエンド] ログイン後に取り直すと新しいトークンが載る", () =>
        {
            var root    = NewWorkingCopy();
            var current = LoreAccountCredential.None;

            using var backend = new LoreNativeBackend(root, () => current);

            // プロジェクトを開いた直後はまだ匿名。
            Check.Equal(string.Empty, backend.CurrentCredentials.AccessToken, "最初は匿名");

            // その後ログインが完了した（バックエンドを作り直さずに反映されること）。
            current = new LoreAccountCredential(TEST_TOKEN, TEST_ACCOUNT_NAME);
            Check.Equal(TEST_TOKEN, backend.CurrentCredentials.AccessToken,
                        "後からログインしても反映されること");
            Check.Equal(TEST_ACCOUNT_NAME, backend.Identity, "表示 identity も切り替わること");
        });

        harness.Add("[リポジトリ ID] `.lore/id` の生 16 バイトを 32 桁の 16 進小文字にする", () =>
        {
            var root   = NewWorkingCopy();
            var idPath = Path.Combine(root, VersionControlSettings.LORE_METADATA_DIR_NAME,
                                      VersionControlSettings.LORE_ID_FILE_NAME);

            Check.Equal(string.Empty, VersionControlPaths.ReadRepositoryId(root),
                        "`.lore/id` が無ければ空");

            // ★中身はテキストではなく生のバイト列。
            //   0x00 や 0xFF を含む値でも、化けずに 16 進へ変換できること。
            var raw = new byte[VersionControlSettings.REPOSITORY_ID_BYTE_LENGTH]
            {
                0x00, 0x01, 0x0A, 0x0D, 0x1F, 0x20, 0x7F, 0x80,
                0x90, 0xA5, 0xBE, 0xEF, 0xC0, 0xDE, 0xFE, 0xFF,
            };
            File.WriteAllBytes(idPath, raw);

            var id = VersionControlPaths.ReadRepositoryId(root);
            Check.Equal("00010a0d1f207f8090a5beefc0defeff", id, "16 進小文字への変換");
            Check.Equal(AuthInputLimits.REPOSITORY_ID_LENGTH, id.Length, "リポジトリ ID の文字数");

            // 長さが違うファイルは「読めなかった」にする。
            // 中途半端な ID を返すとサーバ側の権限と一致せず、原因が分かりにくい。
            File.WriteAllBytes(idPath, new byte[] { 0x01, 0x02, 0x03 });
            Check.Equal(string.Empty, VersionControlPaths.ReadRepositoryId(root),
                        "16 バイトでなければ空");

            Check.Equal(string.Empty, VersionControlPaths.ReadRepositoryId(null), "null");
        });
    }

    /// <summary>
    /// `.lore/config.toml` を持つ一時的な作業コピーを作る。
    /// </summary>
    /// <returns>作業コピーのルート。</returns>
    private static string NewWorkingCopy()
    {
        var root = TestPaths.NewDirectory("working_copy");
        var lore = Path.Combine(root, VersionControlSettings.LORE_METADATA_DIR_NAME);
        Directory.CreateDirectory(lore);

        File.WriteAllText(
            Path.Combine(lore, VersionControlSettings.LORE_CONFIG_FILE_NAME),
            $"remote_url = \"{CONFIG_REMOTE_URL}\"\n"
            + $"identity = \"{CONFIG_IDENTITY}\"\n"
            + "\n[store]\n"
            + "identity = \"これはセクション内なので読まれない\"\n");

        return root;
    }
}
