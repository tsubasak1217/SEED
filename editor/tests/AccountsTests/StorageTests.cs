// ============================================================
//  StorageTests.cs — 保管（DPAPI）と書き出し／読み込み（パスフレーズ）
//
//  【何を守っているのか】
//  ・保存 → 読み込みで **同じ鍵が戻ること**。鍵が変わると参加中のプロジェクトへ
//    入れなくなるので、往復は「公開鍵が一致する」だけでなく
//    「読み込んだ鍵の署名が元の公開鍵で検証できる」ところまで確かめる。
//  ・書き込みが tmp → rename であること（途中で落ちても前の内容が残る）。
//  ・書き出しファイルは **パスフレーズが違えば必ず失敗する**こと。
//    ここが緩いと、盗まれたファイルから鍵が取れてしまう。
//
//  【本物の %APPDATA% には触れない】
//  すべてのテストが一時フォルダを明示的に渡す（TestPaths）。
// ============================================================

using System;
using System.IO;
using SEEDEditor.Accounts;
using SEEDEditor.Accounts.Crypto;
using SEEDEditor.Accounts.Model;
using SEEDEditor.Accounts.Storage;
using SpriteRigTests;

namespace SEEDEditor.Tests.Accounts;

/// <summary>
/// 保管と書き出し／読み込みのテスト。
/// </summary>
public static class StorageTests
{
    /// <summary>テストで使う名前。</summary>
    private const string TEST_NAME = "テスト太郎";

    /// <summary>テストで使う正しいパスフレーズ。</summary>
    private const string TEST_PASSPHRASE = "correct-horse-battery";

    /// <summary>テストで使う誤ったパスフレーズ。</summary>
    private const string WRONG_PASSPHRASE = "wrong-horse-battery";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("[保管] 保存 → 読み込みで同じ鍵が戻る（DPAPI の往復）", () =>
        {
            var dir   = TestPaths.NewDirectory("store_roundtrip");
            var store = new AccountFileStore(dir);

            Check.True(!store.Exists, "最初はアカウントが無いこと");
            Check.True(store.Load() is null, "未作成なら Load が null を返すこと");

            string publicKey;
            string signature;
            var payload = LoginSignaturePayload.Build("cid", "nonce");

            using (var created = new SeedAccount(TEST_NAME, AccountKeyPair.Create()))
            {
                publicKey = created.PublicKeyBase64Url;
                signature = created.SignLoginChallenge("cid", "nonce");
                store.Save(created);
            }

            Check.True(store.Exists, "保存後はファイルがあること");

            using var loaded = store.Load();
            Check.True(loaded is not null, "保存したアカウントを読めること");
            Check.Equal(TEST_NAME, loaded!.Name, "名前");
            Check.Equal(publicKey, loaded.PublicKeyBase64Url, "公開鍵");

            // 読み込んだ鍵で署名して、元の公開鍵で検証できること
            // （公開鍵が一致するだけでは秘密鍵が壊れていても気づけない）。
            var newSignature = loaded.SignLoginChallenge("cid2", "nonce2");
            Check.True(
                AccountKeyPair.Verify(publicKey,
                                      LoginSignaturePayload.Build("cid2", "nonce2"),
                                      newSignature),
                "読み込んだ秘密鍵が元の公開鍵と対になっていること");

            // 保存前の署名も同じ公開鍵で検証できる（前提の確認）。
            Check.True(AccountKeyPair.Verify(publicKey, payload, signature),
                       "保存前の署名が検証できること");
        });

        harness.Add("[保管] 平文の秘密鍵がファイルに残らない", () =>
        {
            var dir   = TestPaths.NewDirectory("store_encrypted");
            var store = new AccountFileStore(dir);

            string pkcs8Base64;
            using (var created = new SeedAccount(TEST_NAME, AccountKeyPair.Create()))
            {
                pkcs8Base64 = Convert.ToBase64String(created.ExportPkcs8PrivateKey());
                store.Save(created);
            }

            var json = File.ReadAllText(store.FilePath);
            Check.True(!json.Contains(pkcs8Base64, StringComparison.Ordinal),
                       "PKCS#8 の平文が account.json に含まれていないこと");
        });

        harness.Add("[保管] 上書き保存で一時ファイルが残らない（tmp → rename）", () =>
        {
            var dir   = TestPaths.NewDirectory("store_atomic");
            var store = new AccountFileStore(dir);

            using (var first = new SeedAccount("first", AccountKeyPair.Create()))
                store.Save(first);

            using (var second = new SeedAccount("second", AccountKeyPair.Create()))
                store.Save(second);

            Check.True(!File.Exists(store.FilePath + AccountSettings.TEMP_FILE_SUFFIX),
                       "一時ファイルが残っていないこと");

            using var loaded = store.Load();
            Check.Equal("second", loaded!.Name, "後から保存した方が残ること");
        });

        harness.Add("[保管] 壊れたファイルは理由つきで失敗する（黙って無視しない）", () =>
        {
            var dir   = TestPaths.NewDirectory("store_broken");
            var store = new AccountFileStore(dir);

            File.WriteAllText(store.FilePath, "{ これは JSON ではない");

            var thrown = false;
            try { store.Load(); }
            catch (AccountStoreException ex)
            {
                thrown = true;
                Check.True(ex.Message.Length > 0, "理由が付くこと");
            }
            Check.True(thrown, "壊れたファイルで例外になること");
        });

        harness.Add("[保管先] 環境変数で差し替えられる", () =>
        {
            var dir = TestPaths.NewDirectory("store_env");

            var saved = Environment.GetEnvironmentVariable(AccountSettings.ENV_ACCOUNT_DIR);
            try
            {
                Environment.SetEnvironmentVariable(AccountSettings.ENV_ACCOUNT_DIR, dir);

                Check.Equal(Path.GetFullPath(dir), AccountPaths.ResolveAccountDir(),
                            "環境変数で指した保管フォルダ");
                Check.Equal(Path.Combine(Path.GetFullPath(dir), AccountSettings.ACCOUNT_FILE_NAME),
                            AccountPaths.ResolveAccountFile(),
                            "環境変数で指したアカウントファイル");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AccountSettings.ENV_ACCOUNT_DIR, saved);
            }
        });

        harness.Add("[書き出し] 正しいパスフレーズで往復できる", () =>
        {
            var dir  = TestPaths.NewDirectory("export_roundtrip");
            var path = Path.Combine(dir, "me" + AccountSettings.EXPORT_FILE_EXTENSION);

            string publicKey;
            using (var original = new SeedAccount(TEST_NAME, AccountKeyPair.Create()))
            {
                publicKey = original.PublicKeyBase64Url;
                AccountExportFile.Export(original, TEST_PASSPHRASE, path);
            }

            Check.True(File.Exists(path), "書き出しファイルができること");

            using var imported = AccountExportFile.Import(path, TEST_PASSPHRASE);
            Check.Equal(TEST_NAME, imported.Name, "名前");
            Check.Equal(publicKey, imported.PublicKeyBase64Url, "公開鍵");

            var signature = imported.SignLoginChallenge("cid", "nonce");
            Check.True(
                AccountKeyPair.Verify(publicKey,
                                      LoginSignaturePayload.Build("cid", "nonce"), signature),
                "読み込んだ鍵が元の公開鍵と対になっていること");
        });

        harness.Add("[書き出し] 誤ったパスフレーズでは読めない", () =>
        {
            var dir  = TestPaths.NewDirectory("export_wrong_passphrase");
            var path = Path.Combine(dir, "me" + AccountSettings.EXPORT_FILE_EXTENSION);

            using (var original = new SeedAccount(TEST_NAME, AccountKeyPair.Create()))
                AccountExportFile.Export(original, TEST_PASSPHRASE, path);

            var thrown = false;
            try { AccountExportFile.Import(path, WRONG_PASSPHRASE).Dispose(); }
            catch (AccountStoreException)
            {
                thrown = true;
            }
            Check.True(thrown, "誤ったパスフレーズでは失敗すること");
        });

        harness.Add("[書き出し] 名前を書き換えたファイルは読めない（AAD で縛っている）", () =>
        {
            var dir  = TestPaths.NewDirectory("export_tampered");
            var path = Path.Combine(dir, "me" + AccountSettings.EXPORT_FILE_EXTENSION);

            using (var original = new SeedAccount("original", AccountKeyPair.Create()))
                AccountExportFile.Export(original, TEST_PASSPHRASE, path);

            // 平文で入っている名前だけを別人に差し替える。
            var json = File.ReadAllText(path).Replace("\"original\"", "\"someone-else\"",
                                                      StringComparison.Ordinal);
            File.WriteAllText(path, json);

            var thrown = false;
            try { AccountExportFile.Import(path, TEST_PASSPHRASE).Dispose(); }
            catch (AccountStoreException)
            {
                thrown = true;
            }
            Check.True(thrown, "名前を差し替えたファイルは読めないこと");
        });

        harness.Add("[書き出し] 短いパスフレーズでは書き出せない（平文の経路を作らない）", () =>
        {
            var dir  = TestPaths.NewDirectory("export_short_passphrase");
            var path = Path.Combine(dir, "me" + AccountSettings.EXPORT_FILE_EXTENSION);

            using var original = new SeedAccount(TEST_NAME, AccountKeyPair.Create());

            var thrown = false;
            try { AccountExportFile.Export(original, "short", path); }
            catch (AccountStoreException)
            {
                thrown = true;
            }
            Check.True(thrown, "短いパスフレーズでは失敗すること");
            Check.True(!File.Exists(path), "失敗したときにファイルを作らないこと");
        });

        harness.Add("[書き出し] 形式が違うファイルは形式違いとして弾く", () =>
        {
            var dir  = TestPaths.NewDirectory("export_format");
            var path = Path.Combine(dir, "other" + AccountSettings.EXPORT_FILE_EXTENSION);
            File.WriteAllText(path, "{\"format\":\"something-else\",\"version\":1}");

            var thrown = false;
            try { AccountExportFile.Import(path, TEST_PASSPHRASE).Dispose(); }
            catch (AccountStoreException ex)
            {
                thrown = true;
                Check.Equal(AccountMessages.IMPORT_FORMAT_MISMATCH, ex.Message, "形式違いの文言");
            }
            Check.True(thrown, "形式が違うファイルで例外になること");
        });

        harness.Add("[利用者設定] 読み書きできる・無ければ既定値", () =>
        {
            var dir = TestPaths.NewDirectory("editor_settings");

            var defaults = AccountEditorSettings.Load(dir);
            Check.Equal(string.Empty, defaults.AuthUrlOverride, "既定の窓口上書きは空");
            Check.True(defaults.AutoSignIn, "既定で自動ログインする");

            var changed = new AccountEditorSettings
            {
                AuthUrlOverride = "192.168.0.10",
                AutoSignIn      = false,
            };
            Check.True(changed.Save(dir), "保存できること");

            var reloaded = AccountEditorSettings.Load(dir);
            Check.Equal("192.168.0.10", reloaded.AuthUrlOverride, "窓口上書き");
            Check.True(!reloaded.AutoSignIn, "自動ログインの設定");
        });
    }
}
