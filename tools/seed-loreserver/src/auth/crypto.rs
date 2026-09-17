// =============================================================================
// SEED アカウント発行窓口 : 低レベルの暗号まわりの道具
// =============================================================================
// base64url（パディングなし）の変換、暗号論的乱数、SHA-256 ハッシュ。
// 「どのライブラリを使うか」をこのファイルに閉じ込めて、
// 上位のモジュール（招待コード・チャレンジ・鍵の検証）が
// ring や base64 crate を直接触らなくて済むようにする。
//
// base64url を選ぶ理由:
//   JWT / JWKS / .NET 側の `Base64Url` と同じ表現に揃えるため。
//   パディング `=` を付けないのは JOSE（RFC 7515）の規定に合わせるため。
//   ただし**デコードはパディング付きも受け付ける**（エディタ側の実装差で
//   `=` が付いて送られてきても落ちないように）。
// =============================================================================

use base64::Engine;
use base64::engine::general_purpose::URL_SAFE;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use ring::digest;
use ring::rand::SecureRandom;
use ring::rand::SystemRandom;

// -----------------------------------------------------------------------------
// base64url
// -----------------------------------------------------------------------------

/// バイト列を base64url（パディングなし）へ変換する。
pub fn base64url_encode(bytes: &[u8]) -> String {
    URL_SAFE_NO_PAD.encode(bytes)
}

/// base64url 文字列をバイト列へ戻す。
///
/// パディング `=` の有無どちらでも受け付ける。
/// 不正な文字が含まれていた場合は `None`（理由は呼び出し側で使わないため捨てる）。
pub fn base64url_decode(text: &str) -> Option<Vec<u8>> {
    // まずパディングなしとして試し、駄目ならパディング付きとして試す。
    // 逆順にすると、パディングなしの正しい入力が
    // `URL_SAFE`（パディング必須）で弾かれてしまう。
    if let Ok(bytes) = URL_SAFE_NO_PAD.decode(text) {
        return Some(bytes);
    }
    URL_SAFE.decode(text).ok()
}

// -----------------------------------------------------------------------------
// 乱数
// -----------------------------------------------------------------------------

/// 暗号論的に安全な乱数バイト列を生成し、base64url で返す。
///
/// チャレンジ ID・nonce・招待コードの生成に使う共通経路。
/// OS の乱数源が使えない環境はそもそもサーバとして成立しないので、
/// 失敗は `Err` として上へ返す（黙って弱い乱数へ落ちない）。
pub fn random_token(byte_length: usize) -> Result<String, String> {
    let mut buffer = vec![0u8; byte_length];
    SystemRandom::new()
        .fill(&mut buffer)
        .map_err(|_| "OS の乱数源から値を取得できませんでした".to_string())?;
    Ok(base64url_encode(&buffer))
}

// -----------------------------------------------------------------------------
// ハッシュ
// -----------------------------------------------------------------------------

/// 文字列の SHA-256 ハッシュを base64url で返す。
///
/// 招待コードの保存に使う。**平文は保存しない**ため、
/// 照合はこの関数で作ったハッシュ同士の比較で行う。
pub fn sha256_base64url(text: &str) -> String {
    let digest = digest::digest(&digest::SHA256, text.as_bytes());
    base64url_encode(digest.as_ref())
}

/// 2 つの文字列を定数時間で比較する。
///
/// 招待コードのハッシュ照合に使う。ハッシュ同士の比較なので
/// 早期 return からコードを復元するのは現実的ではないが、
/// 「秘密に関わる比較は定数時間」という規律を崩さないために用意する。
pub fn constant_time_eq(a: &str, b: &str) -> bool {
    let (a, b) = (a.as_bytes(), b.as_bytes());
    if a.len() != b.len() {
        // 長さの違いは隠せない（隠す意味も無い。ハッシュは固定長なので
        // 長さが違う時点で「そもそも別物」であり、秘密は漏れない）。
        return false;
    }

    // 全バイトの差分を OR で畳み込む。早期 return しないので、
    // 一致するバイト数によって処理時間が変わらない。
    // （ring の verify_slices_are_equal は 0.17 で deprecated になったため自前で持つ）
    let mut difference = 0u8;
    for (x, y) in a.iter().zip(b.iter()) {
        difference |= x ^ y;
    }
    difference == 0
}

// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// base64url の往復。
    #[test]
    fn base64url_round_trip() {
        let original: Vec<u8> = (0u8..=255).collect();
        let encoded = base64url_encode(&original);
        // URL 安全な文字だけで構成され、パディングが付かないこと
        assert!(!encoded.contains('='), "パディングが付いている: {encoded}");
        assert!(!encoded.contains('+'));
        assert!(!encoded.contains('/'));
        assert_eq!(base64url_decode(&encoded).unwrap(), original);
    }

    /// パディング付きの入力も受け付けること
    /// （.NET 側の実装によっては `=` が付く可能性があるため）。
    #[test]
    fn base64url_decode_accepts_padding() {
        // 1 バイト → base64 では 2 文字 + パディング 2 文字
        let with_pad = URL_SAFE.encode([0xABu8]);
        assert!(with_pad.ends_with("=="), "前提が崩れている: {with_pad}");
        assert_eq!(base64url_decode(&with_pad).unwrap(), vec![0xABu8]);
    }

    /// 不正な入力は None になること。
    #[test]
    fn base64url_decode_rejects_garbage() {
        assert!(base64url_decode("!!!not base64!!!").is_none());
        // 標準 base64 の `+` `/` は URL-safe アルファベットに無いので弾かれる
        assert!(base64url_decode("ab+/").is_none());
    }

    /// 乱数が毎回違い、指定した長さになること。
    #[test]
    fn random_token_is_unique_and_sized() {
        const BYTE_LENGTH: usize = 24;
        let a = random_token(BYTE_LENGTH).unwrap();
        let b = random_token(BYTE_LENGTH).unwrap();
        assert_ne!(a, b, "乱数が同じ値を返している");
        assert_eq!(base64url_decode(&a).unwrap().len(), BYTE_LENGTH);
    }

    /// SHA-256 が既知のテストベクタと一致すること。
    /// （空文字の SHA-256 = e3b0c442... を base64url にしたもの）
    #[test]
    fn sha256_matches_known_vector() {
        assert_eq!(
            sha256_base64url(""),
            "47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU"
        );
        // 同じ入力からは同じハッシュ、違う入力からは違うハッシュ
        assert_eq!(sha256_base64url("abc"), sha256_base64url("abc"));
        assert_ne!(sha256_base64url("abc"), sha256_base64url("abd"));
    }

    /// 定数時間比較が普通の比較と同じ結果を返すこと。
    #[test]
    fn constant_time_eq_behaves_like_eq() {
        assert!(constant_time_eq("hello", "hello"));
        assert!(!constant_time_eq("hello", "hellp"));
        assert!(!constant_time_eq("hello", "hell"));
        assert!(constant_time_eq("", ""));
    }
}
