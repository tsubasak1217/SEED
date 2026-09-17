// =============================================================================
// SEED アカウント発行窓口 : サーバの署名鍵と JWKS
// =============================================================================
// 窓口は「利用者の鍵で直接 JWT を作らせない」方式なので、
// サーバ自身が署名鍵を 1 つ持つ。これはそのファイル。
//
//   issuer_key.json … 秘密鍵（PKCS#8）。サーバのデータフォルダから出さない
//   jwks.json       … 公開鍵（JWKS）。Lore 本体が `file://` で読む
//
// **`jwks.json` には `alg` と `kid` を必ず書く。**
// jsonwebtoken 9.3.1（Lore v0.9.0 が使う版）の `Jwk::is_supported()` は
// `self.common.key_algorithm.unwrap()` を呼ぶため、`alg` の無い JWK は
// 扱われない。`kid` が無いと JWT ヘッダの `kid` から鍵を引けない。
//
// アルゴリズムは EdDSA(Ed25519) と ES256(P-256) の両方を実装してあるが、
// 実際に使うのは `ISSUER_ALGORITHM` 定数が指すほう 1 つだけ。
// 設定ファイルには出さない（契約 5 章の `[seed_auth]` にキーを増やさない）。
// 切り替えたいときは定数を変えて再ビルドする。既存の鍵ファイルが別方式なら
// 起動時に作り直す（トークンは最長 8 時間しか生きないので実害は無い）。
// =============================================================================

use std::path::Path;
use std::path::PathBuf;

use jsonwebtoken::DecodingKey;
use jsonwebtoken::EncodingKey;
use ring::rand::SystemRandom;
use ring::signature::Ed25519KeyPair;
use ring::signature::EcdsaKeyPair;
use ring::signature::KeyPair;
use serde::Deserialize;
use serde::Serialize;

use super::atomic_file::read_if_exists;
use super::atomic_file::write_atomic;
use super::crypto::base64url_decode;
use super::crypto::base64url_encode;
use super::crypto::sha256_base64url;

// -----------------------------------------------------------------------------
// 定数
// -----------------------------------------------------------------------------

/// 秘密鍵を保存するファイル名（`data_dir` 直下）。
pub const ISSUER_KEY_FILE_NAME: &str = "issuer_key.json";

/// 公開鍵（JWKS）を書き出すファイル名（`data_dir` 直下）。
/// Lore 本体の `[server.auth.jwk] endpoint = "file:///…/jwks.json"` が指す先。
pub const JWKS_FILE_NAME: &str = "jwks.json";

/// `issuer_key.json` のスキーマバージョン。
const ISSUER_KEY_SCHEMA_VERSION: u32 = 1;

/// 実際に使う署名アルゴリズム。
///
/// **EdDSA を選ぶ理由**: Lore v0.9.0 の JWT 検証は jsonwebtoken 9.3.1 で、
/// JWK の `alg` からアルゴリズムを決める。EdDSA(Ed25519) と ES256 の
/// どちらも受理されることを実機で確認したうえで、
/// 鍵が短く（32 バイト）署名が決定的な EdDSA を既定にする。
/// （ES256 は署名のたびに乱数が要るため、同じ入力でも毎回値が変わる）
pub const ISSUER_ALGORITHM: IssuerAlgorithm = IssuerAlgorithm::EdDsa;

/// JWK の `use`（用途）。署名検証用であることを示す。
const JWK_USE_SIGNATURE: &str = "sig";

/// JWK の `kty`（鍵種別）: Octet Key Pair（Ed25519 など）。
const JWK_KTY_OKP: &str = "OKP";

/// JWK の `kty`（鍵種別）: 楕円曲線（P-256 など）。
const JWK_KTY_EC: &str = "EC";

/// JWK の `crv`（曲線名）: Ed25519。
const JWK_CRV_ED25519: &str = "Ed25519";

/// JWK の `crv`（曲線名）: NIST P-256。
const JWK_CRV_P256: &str = "P-256";

/// P-256 の座標 1 つ分のバイト数（X と Y それぞれ）。
const P256_COORDINATE_LEN: usize = 32;

/// SEC1 非圧縮点の先頭タグ（`0x04`）を読み飛ばす長さ。
const SEC1_TAG_LEN: usize = 1;

// -----------------------------------------------------------------------------
// アルゴリズム
// -----------------------------------------------------------------------------

/// 窓口が発行する JWT の署名アルゴリズム。
#[derive(Clone, Copy, Debug, Deserialize, Eq, PartialEq, Serialize)]
pub enum IssuerAlgorithm {
    /// Ed25519（JWS の `EdDSA`）
    #[serde(rename = "EdDSA")]
    EdDsa,
    /// ECDSA P-256 / SHA-256（JWS の `ES256`）
    #[serde(rename = "ES256")]
    Es256,
}

impl IssuerAlgorithm {
    /// JWT ヘッダと JWK の `alg` に載る文字列。
    pub fn as_str(self) -> &'static str {
        match self {
            IssuerAlgorithm::EdDsa => "EdDSA",
            IssuerAlgorithm::Es256 => "ES256",
        }
    }

    /// jsonwebtoken 側の列挙へ変換する。
    pub fn to_jsonwebtoken(self) -> jsonwebtoken::Algorithm {
        match self {
            IssuerAlgorithm::EdDsa => jsonwebtoken::Algorithm::EdDSA,
            IssuerAlgorithm::Es256 => jsonwebtoken::Algorithm::ES256,
        }
    }
}

// -----------------------------------------------------------------------------
// 永続化フォーマット
// -----------------------------------------------------------------------------

/// `issuer_key.json` の中身。
///
/// **このファイルには秘密鍵が入る。** 中身をログへ出さないこと。
#[derive(Debug, Deserialize, Serialize)]
struct IssuerKeyDocument {
    /// スキーマバージョン
    version: u32,
    /// 署名アルゴリズム
    algorithm: IssuerAlgorithm,
    /// 鍵 ID（公開鍵から決まるので、同じ鍵なら毎回同じ値）
    kid: String,
    /// 秘密鍵（PKCS#8 DER を base64url にしたもの）
    pkcs8: String,
    /// 公開鍵（EdDSA なら生の 32 バイト、ES256 なら SEC1 非圧縮点 65 バイト）
    public_key: String,
    /// 生成時刻（UNIX epoch ミリ秒）
    created_at: u64,
}

// -----------------------------------------------------------------------------
// 署名鍵
// -----------------------------------------------------------------------------

/// 窓口の署名鍵。JWT の署名と、自分が発行したトークンの検証に使う。
pub struct IssuerKey {
    /// 署名アルゴリズム
    algorithm: IssuerAlgorithm,
    /// 鍵 ID（JWT ヘッダの `kid` と JWK の `kid`）
    kid: String,
    /// 署名用の鍵（jsonwebtoken 形式）
    encoding_key: EncodingKey,
    /// 検証用の鍵（jsonwebtoken 形式）。Bearer トークンの検証に使う
    decoding_key: DecodingKey,
    /// 公開鍵の生バイト（JWKS の組み立てに使う）
    public_key: Vec<u8>,
}

impl IssuerKey {
    /// データフォルダから鍵を読み込む。無ければ生成して保存する。
    ///
    /// 既存の鍵が `ISSUER_ALGORITHM` と違う方式だった場合は作り直す
    /// （方式を変えた直後の再起動。発行済みトークンは無効になるが、
    ///  最長でも `token_ttl_hours` しか生きないので実害は無い）。
    ///
    /// # Errors
    /// ファイルが壊れている／鍵の生成に失敗した／書き込めない場合。
    pub fn load_or_create(data_dir: &Path, now_ms: u64) -> Result<Self, String> {
        let path = Self::key_path(data_dir);

        if let Some(raw) = read_if_exists(&path)? {
            let document: IssuerKeyDocument = serde_json::from_str(&raw)
                .map_err(|e| format!("署名鍵ファイルを解釈できません {path:?}: {e}"))?;

            if document.version != ISSUER_KEY_SCHEMA_VERSION {
                return Err(format!(
                    "署名鍵ファイルのスキーマバージョンが未対応です {path:?}: \
                     file={} expected={ISSUER_KEY_SCHEMA_VERSION}",
                    document.version
                ));
            }

            if document.algorithm == ISSUER_ALGORITHM {
                return Self::from_document(&document);
            }

            // 方式が変わっている。作り直す（秘密鍵の中身はログに出さない）。
            eprintln!(
                "[seed_auth] 署名鍵の方式が変わったため作り直します: {} -> {}",
                document.algorithm.as_str(),
                ISSUER_ALGORITHM.as_str()
            );
        }

        let document = Self::generate_document(ISSUER_ALGORITHM, now_ms)?;
        let encoded = serde_json::to_string_pretty(&document)
            .map_err(|e| format!("署名鍵ファイルの JSON 生成に失敗しました: {e}"))?;
        write_atomic(&path, &encoded)?;

        Self::from_document(&document)
    }

    /// 鍵ファイルのパス。
    fn key_path(data_dir: &Path) -> PathBuf {
        data_dir.join(ISSUER_KEY_FILE_NAME)
    }

    /// JWKS ファイルのパス。
    pub fn jwks_path(data_dir: &Path) -> PathBuf {
        data_dir.join(JWKS_FILE_NAME)
    }

    /// 新しい鍵ペアを作り、永続化用の構造体を組み立てる。
    fn generate_document(
        algorithm: IssuerAlgorithm,
        now_ms: u64,
    ) -> Result<IssuerKeyDocument, String> {
        let rng = SystemRandom::new();

        // (秘密鍵 PKCS#8, 公開鍵の生バイト) を作る。
        let (pkcs8, public_key) = match algorithm {
            IssuerAlgorithm::EdDsa => {
                let pkcs8 = Ed25519KeyPair::generate_pkcs8(&rng)
                    .map_err(|_| "Ed25519 の鍵を生成できませんでした".to_string())?;
                let pair = Ed25519KeyPair::from_pkcs8(pkcs8.as_ref())
                    .map_err(|e| format!("生成した Ed25519 鍵を読み込めません: {e}"))?;
                (
                    pkcs8.as_ref().to_vec(),
                    pair.public_key().as_ref().to_vec(),
                )
            }
            IssuerAlgorithm::Es256 => {
                let alg = &ring::signature::ECDSA_P256_SHA256_FIXED_SIGNING;
                let pkcs8 = EcdsaKeyPair::generate_pkcs8(alg, &rng)
                    .map_err(|_| "P-256 の鍵を生成できませんでした".to_string())?;
                let pair = EcdsaKeyPair::from_pkcs8(alg, pkcs8.as_ref(), &rng)
                    .map_err(|e| format!("生成した P-256 鍵を読み込めません: {e}"))?;
                (
                    pkcs8.as_ref().to_vec(),
                    pair.public_key().as_ref().to_vec(),
                )
            }
        };

        Ok(IssuerKeyDocument {
            version: ISSUER_KEY_SCHEMA_VERSION,
            algorithm,
            // 鍵 ID は公開鍵から決める（同じ鍵なら毎回同じ値になる）。
            kid: sha256_base64url(&base64url_encode(&public_key)),
            pkcs8: base64url_encode(&pkcs8),
            public_key: base64url_encode(&public_key),
            created_at: now_ms,
        })
    }

    /// 永続化された構造体から、署名・検証に使える形へ展開する。
    fn from_document(document: &IssuerKeyDocument) -> Result<Self, String> {
        let pkcs8 = base64url_decode(&document.pkcs8)
            .ok_or_else(|| "署名鍵（PKCS#8）が base64url として解釈できません".to_string())?;
        let public_key = base64url_decode(&document.public_key)
            .ok_or_else(|| "公開鍵が base64url として解釈できません".to_string())?;

        // jsonwebtoken の EncodingKey は PKCS#8 DER をそのまま受ける。
        // DecodingKey は「生の公開鍵バイト」を受ける（DER ではない点に注意）。
        let (encoding_key, decoding_key) = match document.algorithm {
            IssuerAlgorithm::EdDsa => (
                EncodingKey::from_ed_der(&pkcs8),
                DecodingKey::from_ed_der(&public_key),
            ),
            IssuerAlgorithm::Es256 => (
                EncodingKey::from_ec_der(&pkcs8),
                DecodingKey::from_ec_der(&public_key),
            ),
        };

        Ok(Self {
            algorithm: document.algorithm,
            kid: document.kid.clone(),
            encoding_key,
            decoding_key,
            public_key,
        })
    }

    /// 署名アルゴリズム。
    pub fn algorithm(&self) -> IssuerAlgorithm {
        self.algorithm
    }

    /// 鍵 ID。
    pub fn kid(&self) -> &str {
        &self.kid
    }

    /// 署名用の鍵。
    pub fn encoding_key(&self) -> &EncodingKey {
        &self.encoding_key
    }

    /// 検証用の鍵。
    pub fn decoding_key(&self) -> &DecodingKey {
        &self.decoding_key
    }

    /// JWKS（`{"keys":[…]}`）の JSON を組み立てる。
    ///
    /// Lore 本体が `file://` で読むのと、`GET /jwks.json` で返すのとで
    /// 同じ文字列を使う（食い違いが起きないように 1 か所で作る）。
    pub fn jwks_json(&self) -> Result<String, String> {
        let jwk = match self.algorithm {
            IssuerAlgorithm::EdDsa => serde_json::json!({
                "kty": JWK_KTY_OKP,
                "crv": JWK_CRV_ED25519,
                "x": base64url_encode(&self.public_key),
                "alg": self.algorithm.as_str(),
                "kid": self.kid,
                "use": JWK_USE_SIGNATURE,
            }),
            IssuerAlgorithm::Es256 => {
                // SEC1 非圧縮点（0x04 ‖ X ‖ Y）を X と Y に割る。
                let expected = SEC1_TAG_LEN + P256_COORDINATE_LEN * 2;
                if self.public_key.len() != expected {
                    return Err(format!(
                        "P-256 公開鍵の長さが不正です: len={} expected={expected}",
                        self.public_key.len()
                    ));
                }
                let x = &self.public_key[SEC1_TAG_LEN..SEC1_TAG_LEN + P256_COORDINATE_LEN];
                let y = &self.public_key[SEC1_TAG_LEN + P256_COORDINATE_LEN..];
                serde_json::json!({
                    "kty": JWK_KTY_EC,
                    "crv": JWK_CRV_P256,
                    "x": base64url_encode(x),
                    "y": base64url_encode(y),
                    "alg": self.algorithm.as_str(),
                    "kid": self.kid,
                    "use": JWK_USE_SIGNATURE,
                })
            }
        };

        serde_json::to_string_pretty(&serde_json::json!({ "keys": [jwk] }))
            .map_err(|e| format!("JWKS の JSON 生成に失敗しました: {e}"))
    }

    /// JWKS をデータフォルダへ書き出す。
    ///
    /// **`server_main()` を呼ぶ前に必ず済ませること。**
    /// Lore 本体は起動時に JWKS を読み、読めないと起動に失敗する。
    pub fn write_jwks(&self, data_dir: &Path) -> Result<PathBuf, String> {
        let path = Self::jwks_path(data_dir);
        write_atomic(&path, &self.jwks_json()?)?;
        Ok(path)
    }
}

// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// テスト内の基準時刻。
    const T0: u64 = 1_800_000_000_000;

    /// 生成した鍵が保存され、読み直しても同じ鍵 ID になること。
    #[test]
    fn key_is_generated_once_and_reloaded() {
        let dir = tempfile::tempdir().unwrap();

        let first = IssuerKey::load_or_create(dir.path(), T0).unwrap();
        assert!(!first.kid().is_empty());
        assert_eq!(first.algorithm(), ISSUER_ALGORITHM);
        assert!(IssuerKey::key_path(dir.path()).exists());

        // 2 回目は生成せず読み込むので、鍵 ID が一致する
        let second = IssuerKey::load_or_create(dir.path(), T0).unwrap();
        assert_eq!(first.kid(), second.kid());
    }

    /// JWKS に `alg` と `kid` が必ず入ること。
    /// （どちらかが欠けると Lore v0.9.0 は鍵を黙って捨てる）
    #[test]
    fn jwks_always_contains_alg_and_kid() {
        let dir = tempfile::tempdir().unwrap();
        let key = IssuerKey::load_or_create(dir.path(), T0).unwrap();

        let jwks: serde_json::Value = serde_json::from_str(&key.jwks_json().unwrap()).unwrap();
        let entry = &jwks["keys"][0];

        assert_eq!(entry["alg"].as_str().unwrap(), ISSUER_ALGORITHM.as_str());
        assert_eq!(entry["kid"].as_str().unwrap(), key.kid());
        assert_eq!(entry["use"].as_str().unwrap(), JWK_USE_SIGNATURE);
    }

    /// JWKS がファイルへ書き出されること。
    #[test]
    fn jwks_is_written_to_disk() {
        let dir = tempfile::tempdir().unwrap();
        let key = IssuerKey::load_or_create(dir.path(), T0).unwrap();

        let path = key.write_jwks(dir.path()).unwrap();
        assert!(path.exists());
        assert_eq!(path, IssuerKey::jwks_path(dir.path()));

        let written = std::fs::read_to_string(&path).unwrap();
        assert_eq!(written, key.jwks_json().unwrap());
    }

    /// EdDSA / ES256 の両方で JWK を正しく組み立てられること。
    /// （`ISSUER_ALGORITHM` をどちらへ倒しても壊れていないことの保証）
    #[test]
    fn both_algorithms_produce_valid_jwk() {
        for algorithm in [IssuerAlgorithm::EdDsa, IssuerAlgorithm::Es256] {
            let document = IssuerKey::generate_document(algorithm, T0).unwrap();
            let key = IssuerKey::from_document(&document).unwrap();
            let jwks: serde_json::Value = serde_json::from_str(&key.jwks_json().unwrap()).unwrap();
            let entry = &jwks["keys"][0];

            assert_eq!(entry["alg"].as_str().unwrap(), algorithm.as_str());
            match algorithm {
                IssuerAlgorithm::EdDsa => {
                    assert_eq!(entry["kty"].as_str().unwrap(), JWK_KTY_OKP);
                    assert_eq!(entry["crv"].as_str().unwrap(), JWK_CRV_ED25519);
                    // x は Ed25519 の公開鍵 32 バイト
                    let x = base64url_decode(entry["x"].as_str().unwrap()).unwrap();
                    assert_eq!(x.len(), 32);
                    assert!(entry["y"].is_null(), "OKP に y は無い");
                }
                IssuerAlgorithm::Es256 => {
                    assert_eq!(entry["kty"].as_str().unwrap(), JWK_KTY_EC);
                    assert_eq!(entry["crv"].as_str().unwrap(), JWK_CRV_P256);
                    let x = base64url_decode(entry["x"].as_str().unwrap()).unwrap();
                    let y = base64url_decode(entry["y"].as_str().unwrap()).unwrap();
                    assert_eq!(x.len(), P256_COORDINATE_LEN);
                    assert_eq!(y.len(), P256_COORDINATE_LEN);
                }
            }

            // jsonwebtoken 側でも JWK として読めること
            // （Lore v0.9.0 が使うのと同じ crate・同じ経路）
            let jwk: jsonwebtoken::jwk::Jwk = serde_json::from_value(entry.clone()).unwrap();
            assert!(jwk.is_supported(), "jsonwebtoken が扱えない JWK になっている");
            assert!(jsonwebtoken::DecodingKey::from_jwk(&jwk).is_ok());
        }
    }

    /// 壊れた鍵ファイルは黙って作り直さず、エラーにすること。
    /// （黙って新しい鍵を作ると、発行済みトークンが全部無効になった理由が分からなくなる）
    #[test]
    fn broken_key_file_is_an_error() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(IssuerKey::key_path(dir.path()), "{ not json").unwrap();

        assert!(IssuerKey::load_or_create(dir.path(), T0).is_err());
    }

    /// 方式が変わっていたら作り直すこと。
    #[test]
    fn key_is_regenerated_when_algorithm_changes() {
        let dir = tempfile::tempdir().unwrap();

        // 現在の設定とは違う方式の鍵ファイルを先に置く
        let other = match ISSUER_ALGORITHM {
            IssuerAlgorithm::EdDsa => IssuerAlgorithm::Es256,
            IssuerAlgorithm::Es256 => IssuerAlgorithm::EdDsa,
        };
        let document = IssuerKey::generate_document(other, T0).unwrap();
        std::fs::create_dir_all(dir.path()).unwrap();
        std::fs::write(
            IssuerKey::key_path(dir.path()),
            serde_json::to_string(&document).unwrap(),
        )
        .unwrap();

        let key = IssuerKey::load_or_create(dir.path(), T0).unwrap();
        assert_eq!(key.algorithm(), ISSUER_ALGORITHM);
        assert_ne!(key.kid(), document.kid, "鍵が作り直されていない");
    }
}
