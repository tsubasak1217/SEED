// =============================================================================
// SEED アカウント発行窓口 : 設定 `[seed_auth]` の読み込み
// =============================================================================
// 窓口は `server_main()` を呼ぶ前に起動する必要があるため、
// Lore 本体の設定読み込み（`server_main()` の中で走る）とは別に、
// **同じ規則で同じファイルを読み直して `[seed_auth]` 節だけを取り出す**。
//
// upstream の実装（lore-server/src/settings.rs の `Settings::load`、
// lore-server/src/server.rs の `Cli`）を読んで確認した規則:
//
//   引数 `--config <DIR>`（環境変数 `LORE_CONFIG_PATH`）… 設定ディレクトリ
//   引数 `--env <ENV>`（環境変数 `LORE_ENV`）          … 環境名。既定 "local"
//
//   読み込み順（後勝ち・すべて任意）:
//     1. バイナリ埋め込みの default.toml   ← `[seed_auth]` は入っていないので無視
//     2. <config>/default.toml
//     3. <config>/<env>.toml
//     4. <config>/<env>_<region>.toml      ← 環境変数 LORE_PLATFORM_REGION 指定時のみ
//     5. <config>/local.toml
//     6. 環境変数 LORE__*（区切りは `__`）
//
//   `<env>` の既定が "local" なので、3 と 5 はどちらも local.toml を指す。
//
// **未知の節が Lore 本体の設定読み込みを壊さないこと**は upstream のソースで確認済み:
//   `Settings` 構造体の `#[serde(deny_unknown_fields)]` はコメントアウトされており
//   （settings.rs:31 と、同ファイル 933 行の TODO コメント）、
//   `config` crate 側も `try_deserialize` を serde へ丸投げするだけなので、
//   `[seed_auth]` は黙って読み捨てられる。別ファイルへ逃がす必要は無い。
//
// 引数の解析を clap でやらない理由:
//   `server_main()` が後から `Cli::parse()` を呼ぶ。こちらで clap を使うと
//   未知の引数でエラーにしてしまう可能性があるため、必要な 2 つだけを
//   自前で読み取り、argv には一切手を触れない。
// =============================================================================

use std::path::PathBuf;

use serde::Deserialize;

// -----------------------------------------------------------------------------
// 定数
// -----------------------------------------------------------------------------

/// 設定ファイル中の節名。
const SECTION_NAME: &str = "seed_auth";

/// `--config` を省略したときの設定ディレクトリ。
/// upstream の `DEFAULT_CONFIG_DIR`（lore-server/src/settings.rs）と同じ値にする。
const DEFAULT_CONFIG_DIR: &str = "lore-server/config";

/// `--env` を省略したときの環境名。upstream と同じ既定値。
const DEFAULT_ENVIRONMENT: &str = "local";

/// 設定ディレクトリを指すコマンドライン引数。
const ARG_CONFIG: &str = "--config";
/// 環境名を指すコマンドライン引数。
const ARG_ENV: &str = "--env";

/// 設定ディレクトリを指す環境変数。
const ENV_CONFIG_PATH: &str = "LORE_CONFIG_PATH";
/// 環境名を指す環境変数。
const ENV_ENVIRONMENT: &str = "LORE_ENV";
/// リージョン別設定を選ぶ環境変数。
const ENV_PLATFORM_REGION: &str = "LORE_PLATFORM_REGION";

/// 環境変数で設定を上書きするときの接頭辞と区切り（upstream と同じ）。
/// 例: `LORE__SEED_AUTH__PORT=41351`
const ENV_OVERRIDE_PREFIX: &str = "lore";
const ENV_OVERRIDE_SEPARATOR: &str = "__";

/// 設定ファイル名（拡張子は `config` crate が補う）。
const FILE_DEFAULT: &str = "default";
const FILE_LOCAL: &str = "local";

/// `host` の既定値。LAN へ出さない安全側に倒す。
const DEFAULT_HOST: &str = "127.0.0.1";

/// `port` の既定値（契約 3 章）。
const DEFAULT_PORT: u16 = 41350;

/// `issuer` の既定値（契約 5 章）。
const DEFAULT_ISSUER: &str = "seed-auth";

/// `audience` の既定値（契約 5 章）。
const DEFAULT_AUDIENCE: &str = "seed-lore";

/// `token_ttl_hours` の既定値（契約 5 章）。
const DEFAULT_TOKEN_TTL_HOURS: u64 = 8;

/// `token_ttl_hours` として受け付ける範囲。
/// 0 は「発行と同時に期限切れ」、長すぎる値は失効が効かなくなる。
const TOKEN_TTL_HOURS_MIN: u64 = 1;
const TOKEN_TTL_HOURS_MAX: u64 = 24;

// -----------------------------------------------------------------------------
// 設定
// -----------------------------------------------------------------------------

/// `[seed_auth]` 節をそのまま読むための構造体（未検証）。
///
/// 既定値は `#[serde(default = ...)]` で埋める。
/// `data_dir` だけは既定値を持たせない（勝手な場所へ鍵を書かないため）。
#[derive(Debug, Deserialize)]
struct RawSeedAuthConfig {
    /// 窓口を起動するか
    #[serde(default)]
    enabled: bool,
    /// 待ち受けアドレス
    #[serde(default = "default_host")]
    host: String,
    /// 待ち受けポート
    #[serde(default = "default_port")]
    port: u16,
    /// accounts.json / issuer_key.json / jwks.json を置くフォルダ
    data_dir: Option<String>,
    /// 発行する JWT の `iss`
    #[serde(default = "default_issuer")]
    issuer: String,
    /// 発行する JWT の `aud`
    #[serde(default = "default_audience")]
    audience: String,
    /// トークンの有効期間（時間）
    #[serde(default = "default_token_ttl_hours")]
    token_ttl_hours: u64,
}

fn default_host() -> String {
    DEFAULT_HOST.to_string()
}
fn default_port() -> u16 {
    DEFAULT_PORT
}
fn default_issuer() -> String {
    DEFAULT_ISSUER.to_string()
}
fn default_audience() -> String {
    DEFAULT_AUDIENCE.to_string()
}
fn default_token_ttl_hours() -> u64 {
    DEFAULT_TOKEN_TTL_HOURS
}

/// 設定ファイル全体のうち、この窓口が見る部分だけを写し取る型。
///
/// `[server]` `[lock_store]` などの他の節は
/// `deny_unknown_fields` を付けていないので黙って無視される。
#[derive(Debug, Default, Deserialize)]
struct ConfigRoot {
    seed_auth: Option<RawSeedAuthConfig>,
}

/// 検証済みの `[seed_auth]` 設定。
#[derive(Clone, Debug)]
pub struct SeedAuthConfig {
    /// 待ち受けアドレス
    pub host: String,
    /// 待ち受けポート
    pub port: u16,
    /// データフォルダ（accounts.json / issuer_key.json / jwks.json の置き場）
    pub data_dir: PathBuf,
    /// 発行する JWT の `iss`。Lore 側 `[server.auth] jwt_issuer` と一致させること
    pub issuer: String,
    /// 発行する JWT の `aud`。Lore 側 `[server.auth] jwt_audience` と一致させること
    pub audience: String,
    /// トークンの有効期間（時間）
    pub token_ttl_hours: u64,
}

impl SeedAuthConfig {
    /// 待ち受けアドレスを `host:port` の形で返す。
    pub fn listen_address(&self) -> String {
        format!("{}:{}", self.host, self.port)
    }
}

// -----------------------------------------------------------------------------
// 読み込み
// -----------------------------------------------------------------------------

/// 設定を読み込む。
///
/// 戻り値:
///   - `Ok(None)`   … `[seed_auth]` が無い、または `enabled = false`（窓口は起動しない）
///   - `Ok(Some(_))`… 窓口を起動する
///   - `Err(_)`     … 設定が壊れている（サーバ全体の起動を止めるべき状態）
///
/// 引数の `args` はテストのために渡せるようにしてある。
/// 本番では `std::env::args().collect()` を渡す。
pub fn load(args: &[String]) -> Result<Option<SeedAuthConfig>, String> {
    let config_dir = find_argument(args, ARG_CONFIG)
        .or_else(|| std::env::var(ENV_CONFIG_PATH).ok())
        .unwrap_or_else(|| DEFAULT_CONFIG_DIR.to_string());
    let environment = find_argument(args, ARG_ENV)
        .or_else(|| std::env::var(ENV_ENVIRONMENT).ok())
        .unwrap_or_else(|| DEFAULT_ENVIRONMENT.to_string());

    // upstream と同じ重ね順で読む。すべて任意ファイル。
    let mut builder = config::Config::builder()
        .add_source(config::File::with_name(&format!("{config_dir}/{FILE_DEFAULT}")).required(false))
        .add_source(config::File::with_name(&format!("{config_dir}/{environment}")).required(false));

    if let Ok(region) = std::env::var(ENV_PLATFORM_REGION) {
        builder = builder.add_source(
            config::File::with_name(&format!("{config_dir}/{environment}_{region}")).required(false),
        );
    }

    builder = builder
        .add_source(config::File::with_name(&format!("{config_dir}/{FILE_LOCAL}")).required(false))
        // 環境変数での上書き。upstream と同じ接頭辞・区切り。
        // upstream は try_parsing を使っていないが、こちらは port や
        // token_ttl_hours が数値なので、文字列からの変換を有効にしておく。
        .add_source(
            config::Environment::with_prefix(ENV_OVERRIDE_PREFIX)
                .separator(ENV_OVERRIDE_SEPARATOR)
                .try_parsing(true),
        );

    let built = builder
        .build()
        .map_err(|e| format!("[{SECTION_NAME}] の設定を読み込めません: {e}"))?;

    let root: ConfigRoot = built
        .try_deserialize()
        .map_err(|e| format!("[{SECTION_NAME}] の設定を解釈できません: {e}"))?;

    let Some(raw) = root.seed_auth else {
        return Ok(None);
    };
    if !raw.enabled {
        return Ok(None);
    }

    validate(raw).map(Some)
}

/// 読み取った値の妥当性を確かめ、検証済みの設定へ変換する。
fn validate(raw: RawSeedAuthConfig) -> Result<SeedAuthConfig, String> {
    let data_dir = raw.data_dir.ok_or_else(|| {
        format!("[{SECTION_NAME}] enabled = true のときは data_dir が必要です")
    })?;
    if data_dir.trim().is_empty() {
        return Err(format!("[{SECTION_NAME}] data_dir が空です"));
    }

    if raw.host.trim().is_empty() {
        return Err(format!("[{SECTION_NAME}] host が空です"));
    }
    if raw.port == 0 {
        return Err(format!("[{SECTION_NAME}] port に 0 は使えません"));
    }
    if raw.issuer.trim().is_empty() {
        return Err(format!("[{SECTION_NAME}] issuer が空です"));
    }
    if raw.audience.trim().is_empty() {
        return Err(format!("[{SECTION_NAME}] audience が空です"));
    }
    if !(TOKEN_TTL_HOURS_MIN..=TOKEN_TTL_HOURS_MAX).contains(&raw.token_ttl_hours) {
        return Err(format!(
            "[{SECTION_NAME}] token_ttl_hours は {TOKEN_TTL_HOURS_MIN}〜{TOKEN_TTL_HOURS_MAX} の範囲で指定してください: {}",
            raw.token_ttl_hours
        ));
    }

    Ok(SeedAuthConfig {
        host: raw.host,
        port: raw.port,
        data_dir: PathBuf::from(data_dir),
        issuer: raw.issuer,
        audience: raw.audience,
        token_ttl_hours: raw.token_ttl_hours,
    })
}

/// コマンドライン引数から `--name VALUE` または `--name=VALUE` を取り出す。
///
/// clap と同じ 2 つの書き方に対応する。argv 自体には手を触れない
/// （後から `server_main()` が同じ argv を clap で読み直すため）。
fn find_argument(args: &[String], name: &str) -> Option<String> {
    let mut iter = args.iter();
    while let Some(arg) = iter.next() {
        if arg == name {
            return iter.next().cloned();
        }
        if let Some(value) = arg.strip_prefix(name)
            && let Some(value) = value.strip_prefix('=')
        {
            return Some(value.to_string());
        }
    }
    None
}

// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 引数の 2 つの書き方に対応していること。
    #[test]
    fn argument_parsing_supports_both_forms() {
        let args = |v: &[&str]| v.iter().map(|s| s.to_string()).collect::<Vec<_>>();

        assert_eq!(
            find_argument(&args(&["exe", "--config", "C:/cfg"]), ARG_CONFIG),
            Some("C:/cfg".to_string())
        );
        assert_eq!(
            find_argument(&args(&["exe", "--config=C:/cfg"]), ARG_CONFIG),
            Some("C:/cfg".to_string())
        );
        assert_eq!(find_argument(&args(&["exe"]), ARG_CONFIG), None);
        // 値が無い `--config` 単体では None
        assert_eq!(find_argument(&args(&["exe", "--config"]), ARG_CONFIG), None);
        // 似た名前の別引数に反応しないこと
        assert_eq!(
            find_argument(&args(&["exe", "--config-other=x"]), ARG_CONFIG),
            None
        );
    }

    /// `enabled = false` なら、節が在っても窓口を起動しないこと。
    #[test]
    fn disabled_config_yields_none() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(
            dir.path().join("local.toml"),
            "[seed_auth]\nenabled = false\ndata_dir = \"C:/tmp/seed_auth\"\n",
        )
        .unwrap();

        let args = vec![
            "seed-loreserver.exe".to_string(),
            ARG_CONFIG.to_string(),
            dir.path().to_string_lossy().to_string(),
        ];
        assert!(load(&args).unwrap().is_none());
    }

    /// `enabled` を省略したときも起動しないこと（既定は false）。
    #[test]
    fn enabled_defaults_to_false() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(
            dir.path().join("local.toml"),
            "[seed_auth]\ndata_dir = \"C:/tmp/seed_auth\"\n",
        )
        .unwrap();

        let args = vec![
            "seed-loreserver.exe".to_string(),
            ARG_CONFIG.to_string(),
            dir.path().to_string_lossy().to_string(),
        ];
        assert!(load(&args).unwrap().is_none());
    }

    /// 既定値が契約どおりであること。
    #[test]
    fn defaults_match_the_contract() {
        let raw: RawSeedAuthConfig =
            toml::from_str("enabled = true\ndata_dir = \"C:/tmp/auth\"").unwrap();
        let config = validate(raw).unwrap();

        assert_eq!(config.host, DEFAULT_HOST);
        assert_eq!(config.port, DEFAULT_PORT);
        assert_eq!(config.issuer, DEFAULT_ISSUER);
        assert_eq!(config.audience, DEFAULT_AUDIENCE);
        assert_eq!(config.token_ttl_hours, DEFAULT_TOKEN_TTL_HOURS);
        assert_eq!(config.listen_address(), format!("{DEFAULT_HOST}:{DEFAULT_PORT}"));
    }

    /// `data_dir` が無ければエラーになること
    /// （既定値で勝手な場所へ秘密鍵を書き出さない）。
    #[test]
    fn data_dir_is_required() {
        let raw: RawSeedAuthConfig = toml::from_str("enabled = true").unwrap();
        let error = validate(raw).unwrap_err();
        assert!(error.contains("data_dir"), "説明が不十分: {error}");
    }

    /// 値の範囲が検証されること。
    #[test]
    fn values_are_validated() {
        let with = |extra: &str| -> Result<SeedAuthConfig, String> {
            let text = format!("enabled = true\ndata_dir = \"C:/tmp/auth\"\n{extra}");
            validate(toml::from_str(&text).unwrap())
        };

        assert!(with("port = 0").is_err());
        assert!(with("host = \"\"").is_err());
        assert!(with("issuer = \"\"").is_err());
        assert!(with("audience = \"\"").is_err());
        assert!(with("token_ttl_hours = 0").is_err());
        assert!(
            with(&format!("token_ttl_hours = {}", TOKEN_TTL_HOURS_MAX + 1)).is_err()
        );
        assert!(with(&format!("token_ttl_hours = {TOKEN_TTL_HOURS_MAX}")).is_ok());
        assert!(with(&format!("token_ttl_hours = {TOKEN_TTL_HOURS_MIN}")).is_ok());
    }

    /// 実際の TOML ファイルから読めること。
    /// `[server]` など他の節が混ざっていても無視されること
    /// （= Lore 本体の設定ファイルへそのまま `[seed_auth]` を足せる）。
    #[test]
    fn reads_seed_auth_section_from_a_real_config_dir() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(
            dir.path().join("local.toml"),
            r#"
[server.http]
enabled = true
port = 41349

[lock_store]
mode = "seed_file_lock_store"

[seed_auth]
enabled = true
host = "127.0.0.1"
port = 41351
data_dir = "C:/tmp/seed_auth"
issuer = "seed-auth"
audience = "seed-lore"
token_ttl_hours = 8
"#,
        )
        .unwrap();

        let args = vec![
            "seed-loreserver.exe".to_string(),
            ARG_CONFIG.to_string(),
            dir.path().to_string_lossy().to_string(),
        ];
        let config = load(&args).unwrap().expect("[seed_auth] が読めていない");

        assert_eq!(config.port, 41351);
        assert_eq!(config.data_dir, PathBuf::from("C:/tmp/seed_auth"));
        assert_eq!(config.token_ttl_hours, 8);
    }

    /// `[seed_auth]` が無い設定ディレクトリでは `None` になること
    /// （窓口を起動しないだけで、エラーにはしない）。
    #[test]
    fn missing_section_is_not_an_error() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(
            dir.path().join("local.toml"),
            "[server.http]\nenabled = true\n",
        )
        .unwrap();

        let args = vec![
            "seed-loreserver.exe".to_string(),
            format!("{ARG_CONFIG}={}", dir.path().to_string_lossy()),
        ];
        assert!(load(&args).unwrap().is_none());
    }

    /// 設定ディレクトリが存在しなくてもエラーにならないこと
    /// （すべてのファイルが任意なので、窓口を使わない人の邪魔をしない）。
    #[test]
    fn missing_config_dir_is_not_an_error() {
        let args = vec![
            "seed-loreserver.exe".to_string(),
            ARG_CONFIG.to_string(),
            "C:/this/path/does/not/exist".to_string(),
        ];
        assert!(load(&args).unwrap().is_none());
    }
}
