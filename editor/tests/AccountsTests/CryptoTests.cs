// ============================================================
//  CryptoTests.cs — 鍵・署名・名前の規則を固定する
//
//  【何を守っているのか】
//  契約（docs/seed_accounts.md 2 章）の形式は、1 バイトでも違うと
//  **サーバ側の検証が必ず落ちる**。しかも「時々通る」ような壊れ方はしないので、
//  ここで形式そのものを固定しておけば、実サーバ結合の前に気づける。
//
//  【固定のテストベクタ】
//  既知の PKCS#8 秘密鍵を 1 つ埋め込み、そこから導かれる公開鍵の base64url を
//  固定値として突き合わせる。ECDSA の署名値そのものは乱数 k を含むので
//  固定できない（毎回変わるのが正しい）。代わりに
//  「長さ 64 バイト」「自前検証が通る」「改竄すると通らない」を固定する。
// ============================================================

using System;
using System.Text;
using SEEDEditor.Accounts;
using SEEDEditor.Accounts.Crypto;
using SpriteRigTests;

namespace SEEDEditor.Tests.Accounts;

/// <summary>
/// 鍵・署名・名前の規則のテスト。
/// </summary>
public static class CryptoTests
{
    /// <summary>
    /// 固定のテストベクタ: P-256 の秘密鍵（PKCS#8・標準 base64）。
    /// この鍵はテスト専用で、どのサーバにも登録されていない。
    /// </summary>
    private const string VECTOR_PKCS8_BASE64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgFAu1HF1sZDVDA0LJzZ0nNKC2zwqQv/WXOEooM/f/9QGhRANC"
        + "AASvEiKZ4avlZ6H3//oW7NOO+uxDS2XCXklmcEUQWNEh80ehejbmLaPCPeoXLI+DO35oa3qYGMlMBAbLHHUWAqRy";

    /// <summary>
    /// 固定のテストベクタ: 上の秘密鍵から導かれる公開鍵
    /// （SEC1 非圧縮点 65 バイトの base64url、パディングなし）。
    /// </summary>
    private const string VECTOR_PUBLIC_KEY_BASE64URL =
        "BK8SIpnhq-Vnoff_-hbs04767ENLZcJeSWZwRRBY0SHzR6F6NuYto8I96hcsj4M7fmhrepgYyUwEBsscdRYCpHI";

    /// <summary>テストで使うチャレンジ識別子。</summary>
    private const string TEST_CHALLENGE_ID = "abcdef0123456789";

    /// <summary>テストで使う nonce。</summary>
    private const string TEST_NONCE = "Zm9vYmFyLW5vbmNl";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("[鍵] 固定の秘密鍵から導く公開鍵が契約どおり（65 バイト・先頭 0x04・base64url）", () =>
        {
            using var keyPair = AccountKeyPair.FromPkcs8(
                Convert.FromBase64String(VECTOR_PKCS8_BASE64));

            Check.Equal(VECTOR_PUBLIC_KEY_BASE64URL, keyPair.PublicKeyBase64Url,
                        "固定の秘密鍵から導かれる公開鍵");

            var bytes = Base64Url.Decode(keyPair.PublicKeyBase64Url);
            Check.Equal(AccountSettings.PUBLIC_KEY_BYTE_LENGTH, bytes.Length, "公開鍵のバイト数");
            Check.Equal(AccountSettings.PUBLIC_KEY_UNCOMPRESSED_PREFIX, bytes[0],
                        "公開鍵の先頭バイト（SEC1 非圧縮点）");

            // base64url なのでパディングも + / も含まれない。
            Check.True(!keyPair.PublicKeyBase64Url.Contains('='), "公開鍵にパディングが無いこと");
            Check.True(!keyPair.PublicKeyBase64Url.Contains('+'), "公開鍵に + が無いこと");
            Check.True(!keyPair.PublicKeyBase64Url.Contains('/'), "公開鍵に / が無いこと");
        });

        harness.Add("[署名] P1363 固定長 64 バイトで、自前検証が通る", () =>
        {
            using var keyPair = AccountKeyPair.FromPkcs8(
                Convert.FromBase64String(VECTOR_PKCS8_BASE64));

            var payload   = LoginSignaturePayload.Build(TEST_CHALLENGE_ID, TEST_NONCE);
            var signature = keyPair.Sign(payload);

            var bytes = Base64Url.Decode(signature);
            Check.Equal(AccountSettings.SIGNATURE_BYTE_LENGTH, bytes.Length,
                        "署名のバイト数（DER だと 70 前後の可変長になる）");

            Check.True(AccountKeyPair.Verify(VECTOR_PUBLIC_KEY_BASE64URL, payload, signature),
                       "自分の署名を公開鍵で検証できること");
        });

        harness.Add("[署名] 署名対象を 1 文字でも変えると検証が通らない", () =>
        {
            using var keyPair = AccountKeyPair.FromPkcs8(
                Convert.FromBase64String(VECTOR_PKCS8_BASE64));

            var payload   = LoginSignaturePayload.Build(TEST_CHALLENGE_ID, TEST_NONCE);
            var signature = keyPair.Sign(payload);

            // challenge_id を差し替えた（= 別のチャレンジへの使い回し）。
            var other = LoginSignaturePayload.Build(TEST_CHALLENGE_ID + "0", TEST_NONCE);
            Check.True(!AccountKeyPair.Verify(VECTOR_PUBLIC_KEY_BASE64URL, other, signature),
                       "別のチャレンジでは検証が通らないこと");

            // 公開鍵を別の鍵にした。
            using var another = AccountKeyPair.Create();
            Check.True(!AccountKeyPair.Verify(another.PublicKeyBase64Url, payload, signature),
                       "別人の公開鍵では検証が通らないこと");
        });

        harness.Add("[署名] 毎回新しい鍵でも 65 バイト・64 バイトになる（先頭ゼロの取り扱い）", () =>
        {
            // 座標の先頭がゼロになる鍵は 256 回に 1 回程度しか出ないので、
            // 回数を稼いで「たまに長さが変わる」実装を炙り出す。
            const int TRIAL_COUNT = 64;

            for (var i = 0; i < TRIAL_COUNT; i++)
            {
                using var keyPair = AccountKeyPair.Create();

                Check.Equal(AccountSettings.PUBLIC_KEY_BYTE_LENGTH,
                            Base64Url.Decode(keyPair.PublicKeyBase64Url).Length,
                            $"{i} 回目の公開鍵のバイト数");

                var signature = keyPair.Sign("payload");
                Check.Equal(AccountSettings.SIGNATURE_BYTE_LENGTH,
                            Base64Url.Decode(signature).Length,
                            $"{i} 回目の署名のバイト数");
            }
        });

        harness.Add("[署名対象] 契約どおりの文字列になる", () =>
        {
            Check.Equal(
                "seed-auth-login:v1:cid-1:nonce-1",
                LoginSignaturePayload.Build("cid-1", "nonce-1"),
                "署名対象の文字列");

            // UTF-8 のバイト列としても一致すること（Encoding を取り違えていないか）。
            var expected = Encoding.UTF8.GetBytes("seed-auth-login:v1:cid-1:nonce-1");
            var actual   = LoginSignaturePayload.BuildBytes("cid-1", "nonce-1");
            Check.Equal(expected.Length, actual.Length, "署名対象のバイト数");
            for (var i = 0; i < expected.Length; i++)
                Check.Equal(expected[i], actual[i], $"署名対象の {i} バイト目");
        });

        harness.Add("[base64url] 往復できる・パディングを補って読める", () =>
        {
            // 長さ 1〜32 のすべてで往復を確かめる（パディングの 3 通りを網羅する）。
            for (var length = 1; length <= 32; length++)
            {
                var original = new byte[length];
                for (var i = 0; i < length; i++) original[i] = (byte)(i * 7 + length);

                var encoded = Base64Url.Encode(original);
                Check.True(!encoded.Contains('='), $"{length} バイトの符号化にパディングが無いこと");

                var decoded = Base64Url.Decode(encoded);
                Check.Equal(length, decoded.Length, $"{length} バイトの復号後の長さ");
                for (var i = 0; i < length; i++)
                    Check.Equal(original[i], decoded[i], $"{length} バイトの {i} バイト目");
            }

            // 空文字と null は空のバイト列（例外にしない）。
            Check.Equal(0, Base64Url.Decode(string.Empty).Length, "空文字の復号");
            Check.Equal(0, Base64Url.Decode(null).Length, "null の復号");

            // 読めない文字列は TryDecode が偽を返す（例外を投げない）。
            Check.True(!Base64Url.TryDecode("###", out _), "不正な文字列は読めないこと");
        });

        harness.Add("[名前] 契約どおりの規則で通す・弾く", () =>
        {
            // 通るもの。
            Check.True(AccountNameRule.IsValid("tsubasa"),      "英字");
            Check.True(AccountNameRule.IsValid("user_01"),      "英数字と _");
            Check.True(AccountNameRule.IsValid("a-b.c"),        "- と .");
            Check.True(AccountNameRule.IsValid("つばさ"),        "ひらがな");
            Check.True(AccountNameRule.IsValid("ツバサ"),        "カタカナ");
            Check.True(AccountNameRule.IsValid("翼"),            "漢字");
            Check.True(AccountNameRule.IsValid("データ班-01"),   "長音符つきの混在");
            Check.True(AccountNameRule.IsValid("a"),             "1 文字");
            Check.True(AccountNameRule.IsValid(new string('a', AccountSettings.NAME_MAX_LENGTH)),
                       "上限ちょうど");

            // 弾くもの。
            Check.True(!AccountNameRule.IsValid(null),           "null");
            Check.True(!AccountNameRule.IsValid(string.Empty),   "空文字");
            Check.True(!AccountNameRule.IsValid("   "),          "空白だけ");
            Check.True(!AccountNameRule.IsValid(" tsubasa"),     "前に空白");
            Check.True(!AccountNameRule.IsValid("tsubasa "),     "後ろに空白");
            Check.True(!AccountNameRule.IsValid("tsu basa"),     "途中の空白");
            Check.True(!AccountNameRule.IsValid("user@example"), "@ は使えない");
            Check.True(!AccountNameRule.IsValid("user/name"),    "/ は使えない");
            Check.True(!AccountNameRule.IsValid("ＡＢＣ"),        "全角英字は使えない");
            Check.True(!AccountNameRule.IsValid(new string('a', AccountSettings.NAME_MAX_LENGTH + 1)),
                       "上限超え");

            // 理由が空でないこと（画面にそのまま出すため）。
            var check = AccountNameRule.Check("user@example");
            Check.True(!check.IsValid, "@ を含む名前は不正");
            Check.True(check.Error.Length > 0, "不正な名前には理由が付くこと");
        });

        harness.Add("[名前] 長さはコードポイントで数える（サロゲートペアを 2 文字にしない）", () =>
        {
            // 𠮟（U+20B9F、CJK 拡張 B）は UTF-16 で 2 要素だが 1 文字。
            // 数え方を間違えると、上限ちょうどの名前が弾かれる／通ってしまう。
            const string SURROGATE_PAIR = "𠮟";
            Check.Equal(2, SURROGATE_PAIR.Length, "UTF-16 の要素数（前提の確認）");

            // 拡張 B は許可範囲に入れていないので弾かれる。
            // ここで確かめたいのは「長さ超過」ではなく「使えない文字」として弾かれること。
            var check = AccountNameRule.Check(SURROGATE_PAIR);
            Check.True(!check.IsValid, "許可していない漢字は弾くこと");
            Check.True(check.Error.Contains(SURROGATE_PAIR, StringComparison.Ordinal),
                       "理由にその 1 文字がそのまま出ること（サロゲートを割らない）");
        });
    }
}
