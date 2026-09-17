// =============================================================================
// SEED アカウント発行窓口 : 利用者の鍵と署名の検証
// =============================================================================
// 契約（docs/seed_accounts.md 2 章）が定める形式:
//
//   | 利用者の鍵   | ECDSA P-256                                               |
//   | 公開鍵       | SEC1 非圧縮点（65 バイト: 0x04 ‖ X ‖ Y）の base64url        |
//   | 署名         | SHA-256 / IEEE P1363 固定長（r ‖ s の 64 バイト）の base64url |
//   | 署名対象     | UTF-8 の `seed-auth-login:v1:<challenge_id>:<nonce>`        |
//
// **P1363（固定長 r‖s）であることが最重要。**
// .NET の `ECDsa.SignData()` は既定で ASN.1 DER（`Rfc3279DerSequence`）を返すため、
// エディタ側が `DSASignatureFormat.IeeeP1363FixedFieldConcatenation` を
// 明示し忘れると、このファイルの検証は必ず失敗する（長さで弾かれる）。
// 検証側は ring の `ECDSA_P256_SHA256_FIXED` を使う。これが P1363 に対応する
// アルゴリズム識別子で、DER 形式は `ECDSA_P256_SHA256_ASN1` の側（＝受け付けない）。
//
// このファイルは「鍵・署名が正しいか」だけを判定する。
// 誰がログインしてよいか（失効・権限）は上位（store.rs / http.rs）の責任。
// =============================================================================

use ring::signature;

use super::crypto::base64url_decode;

// -----------------------------------------------------------------------------
// 定数
// -----------------------------------------------------------------------------

/// 署名対象文字列の先頭に必ず付く固定文字列（版を含む）。
/// 署名の使い回しを防ぐドメイン分離子でもあるので、勝手に変えない。
/// 変えるときは契約とエディタ側を同時に更新すること。
const LOGIN_MESSAGE_PREFIX: &str = "seed-auth-login:v1";

/// 署名対象文字列の区切り文字。
const LOGIN_MESSAGE_SEPARATOR: char = ':';

/// SEC1 非圧縮点の全長（`0x04` 1 バイト + X 32 バイト + Y 32 バイト）。
pub const UNCOMPRESSED_POINT_LEN: usize = 65;

/// SEC1 非圧縮点であることを示す先頭バイト。
const UNCOMPRESSED_POINT_TAG: u8 = 0x04;

/// IEEE P1363 署名の全長（r 32 バイト + s 32 バイト）。
pub const P1363_SIGNATURE_LEN: usize = 64;

/// base64url 文字列として受け付ける公開鍵の最大文字数。
/// 65 バイトの base64url は 88 文字なので、余裕を見た上限で
/// 「巨大な文字列を投げられて CPU を焼かれる」のを防ぐ。
pub const PUBLIC_KEY_MAX_CHARS: usize = 128;

/// base64url 文字列として受け付ける署名の最大文字数。
/// 64 バイトの base64url は 86 文字。
pub const SIGNATURE_MAX_CHARS: usize = 128;

// -----------------------------------------------------------------------------
// エラー
// -----------------------------------------------------------------------------

/// 公開鍵の検証に失敗した理由。
///
/// **ログインの失敗理由はクライアントへ区別して返さない**（契約 3 章）が、
/// アカウント登録（bootstrap / join）では「鍵の形式が違う」ことを
/// はっきり伝えたほうが親切なので、理由を持つ型にしておく。
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PublicKeyError {
    /// base64url として解釈できない
    NotBase64Url,
    /// 長さが 65 バイトでない
    WrongLength,
    /// 先頭バイトが 0x04 でない（圧縮点や生の座標を送ってきている）
    NotUncompressedPoint,
}

impl PublicKeyError {
    /// 利用者へ返す日本語の説明。
    pub fn message(self) -> &'static str {
        match self {
            PublicKeyError::NotBase64Url => "公開鍵が base64url として解釈できません",
            PublicKeyError::WrongLength => {
                "公開鍵の長さが不正です（SEC1 非圧縮点の 65 バイトが必要です）"
            }
            PublicKeyError::NotUncompressedPoint => {
                "公開鍵が SEC1 非圧縮点ではありません（先頭が 0x04 である必要があります）"
            }
        }
    }
}

// -----------------------------------------------------------------------------
// 公開鍵
// -----------------------------------------------------------------------------

/// 公開鍵の文字列が契約どおりの形式かを検証し、生バイトを返す。
///
/// アカウント登録時にここで弾いておかないと、
/// 「登録はできたが絶対にログインできないアカウント」が出来上がる。
pub fn parse_public_key(public_key_b64url: &str) -> Result<Vec<u8>, PublicKeyError> {
    // 長さの上限は base64url へ渡す前に見る（デコードのコストを先に切る）。
    if public_key_b64url.len() > PUBLIC_KEY_MAX_CHARS {
        return Err(PublicKeyError::WrongLength);
    }

    let bytes = base64url_decode(public_key_b64url).ok_or(PublicKeyError::NotBase64Url)?;

    if bytes.len() != UNCOMPRESSED_POINT_LEN {
        return Err(PublicKeyError::WrongLength);
    }
    if bytes[0] != UNCOMPRESSED_POINT_TAG {
        return Err(PublicKeyError::NotUncompressedPoint);
    }

    Ok(bytes)
}

// -----------------------------------------------------------------------------
// 署名対象文字列
// -----------------------------------------------------------------------------

/// チャレンジ ID と nonce から、署名対象の文字列を組み立てる。
///
/// サーバ側とエディタ側で 1 文字でもずれると署名検証が通らないので、
/// **組み立てはこの関数 1 つに閉じ込める**（テストもここを叩く）。
pub fn login_message(challenge_id: &str, nonce: &str) -> String {
    format!(
        "{LOGIN_MESSAGE_PREFIX}{LOGIN_MESSAGE_SEPARATOR}{challenge_id}{LOGIN_MESSAGE_SEPARATOR}{nonce}"
    )
}

// -----------------------------------------------------------------------------
// 署名の検証
// -----------------------------------------------------------------------------

/// 利用者の署名を検証する。
///
/// # 引数
/// - `public_key_b64url`: 登録済みの公開鍵（base64url の SEC1 非圧縮点）
/// - `signature_b64url`: 利用者が送ってきた署名（base64url の P1363 64 バイト）
/// - `message`: [`login_message`] が組み立てた署名対象文字列
///
/// # 戻り値
/// 検証に成功したときだけ `true`。
/// **失敗の理由は返さない。** ログインの失敗理由を区別して返さない契約に合わせ、
/// 呼び出し側が誤って理由を漏らすことができないようにするため。
pub fn verify_login_signature(
    public_key_b64url: &str,
    signature_b64url: &str,
    message: &str,
) -> bool {
    // --- 公開鍵 ---------------------------------------------------------
    let Ok(public_key) = parse_public_key(public_key_b64url) else {
        return false;
    };

    // --- 署名 -----------------------------------------------------------
    if signature_b64url.len() > SIGNATURE_MAX_CHARS {
        return false;
    }
    let Some(signature_bytes) = base64url_decode(signature_b64url) else {
        return false;
    };
    // ここで長さを見ておくと、.NET 既定の DER 署名（可変長・先頭 0x30）を
    // 明確に弾ける。ring 側でも落ちるが、意図を残すために明示する。
    if signature_bytes.len() != P1363_SIGNATURE_LEN {
        return false;
    }

    // --- 検証 -----------------------------------------------------------
    // ECDSA_P256_SHA256_FIXED = P-256 / SHA-256 / 固定長 r‖s（= IEEE P1363）。
    signature::UnparsedPublicKey::new(&signature::ECDSA_P256_SHA256_FIXED, &public_key)
        .verify(message.as_bytes(), &signature_bytes)
        .is_ok()
}

// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use super::*;

    // -------------------------------------------------------------------------
    // .NET が実際に出力したテストベクタ
    // -------------------------------------------------------------------------
    // 生成方法（`dotnet` 9.0.318 / Windows 11 で実行）:
    //   using ECDsa e = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    //   公開鍵 = 0x04 ‖ Q.X ‖ Q.Y を base64url
    //   署名   = e.SignData(UTF8(message), SHA256,
    //                       DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
    //            を base64url
    //
    // これが通ることが、エディタ側（.NET）との相互運用の証拠になる。
    // 値を書き換えてはいけない（書き換えるとテストの意味が消える）。

    /// .NET が出力した公開鍵（SEC1 非圧縮点 65 バイトの base64url）
    const DOTNET_PUBLIC_KEY: &str =
        "BNSvKbg1XklzyjelcCAXzn4oH36qpHbwfbdccSwCu6rJNNX3fMVHxR1w2kbDbCYJLKHXULta_u4efO0j2yQS4Ys";
    /// 署名対象に使ったチャレンジ ID
    const DOTNET_CHALLENGE_ID: &str = "QzY3Nzg5YWJjZGVm";
    /// 署名対象に使った nonce
    const DOTNET_NONCE: &str = "bm9uY2VfZm9yX3Rlc3RfdmVjdG9y";
    /// .NET が出力した署名（IEEE P1363 の 64 バイトを base64url にしたもの）
    const DOTNET_SIGNATURE_P1363: &str =
        "vUOqhFEOShSxrS8caO3WdZ-g1EOeowYAvcWb4DcRCMU7-bENeeHXdtFh2JynRh6B4Z-DvgdJOOaaWCqHQ9_TwA";
    /// 同じ鍵・同じメッセージに対する **ASN.1 DER 形式**の署名。
    /// エディタ側が形式の指定を忘れるとこれが送られてくる。必ず拒否できること。
    const DOTNET_SIGNATURE_DER: &str =
        "MEYCIQCfd_wLUNU310W2pAGkwMxA83gB-cPK4rgdFaJnx4IGiQIhAJqmbZLD-4mLVmknQeqcj-q-PANaf_vfeqq244M98vdd";

    /// .NET が出した鍵・署名をそのまま検証できること（相互運用の本丸）。
    #[test]
    fn dotnet_generated_signature_verifies() {
        let message = login_message(DOTNET_CHALLENGE_ID, DOTNET_NONCE);
        assert!(
            verify_login_signature(DOTNET_PUBLIC_KEY, DOTNET_SIGNATURE_P1363, &message),
            ".NET が生成した P1363 署名を検証できていない"
        );
    }

    /// .NET 既定の DER 署名は拒否されること。
    /// （通ってしまうと「形式が違っても動く」と誤解され、後で必ず壊れる）
    #[test]
    fn dotnet_der_signature_is_rejected() {
        let message = login_message(DOTNET_CHALLENGE_ID, DOTNET_NONCE);
        assert!(
            !verify_login_signature(DOTNET_PUBLIC_KEY, DOTNET_SIGNATURE_DER, &message),
            "ASN.1 DER 署名が通ってしまっている（P1363 固定長でなければならない）"
        );
    }

    /// メッセージが 1 文字でも違えば検証に失敗すること。
    /// （チャレンジの使い回し・改竄が効かないことの確認）
    #[test]
    fn signature_is_bound_to_the_exact_message() {
        let wrong_nonce = login_message(DOTNET_CHALLENGE_ID, "differentnonce");
        assert!(!verify_login_signature(
            DOTNET_PUBLIC_KEY,
            DOTNET_SIGNATURE_P1363,
            &wrong_nonce
        ));

        let wrong_challenge = login_message("differentchallenge", DOTNET_NONCE);
        assert!(!verify_login_signature(
            DOTNET_PUBLIC_KEY,
            DOTNET_SIGNATURE_P1363,
            &wrong_challenge
        ));
    }

    /// 署名対象文字列の組み立てが契約どおりであること。
    /// エディタ側はこれと 1 バイト単位で一致させる必要がある。
    #[test]
    fn login_message_format_is_fixed() {
        assert_eq!(
            login_message("CHALLENGE", "NONCE"),
            "seed-auth-login:v1:CHALLENGE:NONCE"
        );
    }

    /// 公開鍵の形式チェック。
    #[test]
    fn public_key_format_is_validated() {
        // 正しい鍵は通る
        assert!(parse_public_key(DOTNET_PUBLIC_KEY).is_ok());

        // base64url でない
        assert_eq!(
            parse_public_key("!!!!"),
            Err(PublicKeyError::NotBase64Url)
        );

        // 長さ違い（32 バイト）
        let short = super::super::crypto::base64url_encode(&[0x04u8; 32]);
        assert_eq!(parse_public_key(&short), Err(PublicKeyError::WrongLength));

        // 長さは合っているが先頭が 0x04 でない（圧縮点 0x02/0x03 を 65 バイトに詰めた等）
        let mut wrong_tag = [0u8; UNCOMPRESSED_POINT_LEN];
        wrong_tag[0] = 0x02;
        let wrong_tag = super::super::crypto::base64url_encode(&wrong_tag);
        assert_eq!(
            parse_public_key(&wrong_tag),
            Err(PublicKeyError::NotUncompressedPoint)
        );

        // 上限を超える長さの文字列は、デコードする前に弾く
        let huge = "A".repeat(PUBLIC_KEY_MAX_CHARS + 1);
        assert_eq!(parse_public_key(&huge), Err(PublicKeyError::WrongLength));
    }

    /// 他人の鍵では検証が通らないこと（なりすまし防止の最低限）。
    #[test]
    fn signature_does_not_verify_with_another_key() {
        // 有効な曲線上の点だが別人の鍵、という状況を作るのは
        // このテストの範囲では過剰なので、「形の正しい別バイト列」で確認する。
        // （ring は曲線上に無い点を弾くので false になる経路が違うが、
        //   「別の鍵で通らない」という結論は同じ）
        let mut other = super::super::crypto::base64url_decode(DOTNET_PUBLIC_KEY).unwrap();
        // X 座標の末尾を 1 ビット反転させる
        let last = other.len() / 2;
        other[last] ^= 0x01;
        let other = super::super::crypto::base64url_encode(&other);

        let message = login_message(DOTNET_CHALLENGE_ID, DOTNET_NONCE);
        assert!(!verify_login_signature(
            &other,
            DOTNET_SIGNATURE_P1363,
            &message
        ));
    }

    /// 壊れた署名文字列で panic しないこと（入力はすべて外部由来）。
    #[test]
    fn malformed_signature_inputs_do_not_panic() {
        let message = login_message(DOTNET_CHALLENGE_ID, DOTNET_NONCE);
        for bad in ["", "!!!", "A", &"A".repeat(SIGNATURE_MAX_CHARS + 1)] {
            assert!(!verify_login_signature(DOTNET_PUBLIC_KEY, bad, &message));
        }
    }
}
