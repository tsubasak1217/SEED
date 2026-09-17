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

use super::model::normalize_name_for_uniqueness;
use super::model::validate_name;

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

/// 権限サービス（gRPC）が待ち受けるホスト。設定では変えられない（理由は `permission_listen_address`）。
const PERMISSION_SERVICE_HOST: &str = "127.0.0.1";

/// `port` の既定値（契約 3 章）。
const DEFAULT_PORT: u16 = 41350;

/// `permission_port` の既定値（契約 3 章の権限サービス）。
///
/// 発行窓口（41350）とは別のポートで待ち受ける。
/// Lore 本体が `[environment.endpoint] auth_url` として指す先がここ。
const DEFAULT_PERMISSION_PORT: u16 = 41352;

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
    /// 権限サービス（gRPC）の待ち受けポート
    #[serde(default = "default_permission_port")]
    permission_port: u16,
    /// 新しいリポジトリを作ってよいアカウント名の一覧。
    /// 省略時は空 ＝ 認証が有効な間は誰も作れない（安全側）。
    #[serde(default)]
    repository_creators: Vec<String>,
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
fn default_permission_port() -> u16 {
    DEFAULT_PERMISSION_PORT
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

/// `[environment.endpoint]` のうち、こちらが見る部分だけ。
///
/// この節は **Lore 本体の設定**で、窓口は書き換えない。
/// 読むのは「`auth_url` が権限サービスを指していないのでは」という
/// 起動時の助言を出すためだけ（設定ミスの筆頭がこれなので）。
#[derive(Debug, Default, Deserialize)]
struct RawEnvironmentEndpoint {
    #[serde(default)]
    auth_url: Option<String>,
}

/// `[environment]` のうち、こちらが見る部分だけ。
#[derive(Debug, Default, Deserialize)]
struct RawEnvironment {
    #[serde(default)]
    endpoint: Option<RawEnvironmentEndpoint>,
}

/// 設定ファイル全体のうち、この窓口が見る部分だけを写し取る型。
///
/// `[server]` `[lock_store]` などの他の節は
/// `deny_unknown_fields` を付けていないので黙って無視される。
#[derive(Debug, Default, Deserialize)]
struct ConfigRoot {
    seed_auth: Option<RawSeedAuthConfig>,
    #[serde(default)]
    environment: Option<RawEnvironment>,
}

/// 検証済みの `[seed_auth]` 設定。
#[derive(Clone, Debug)]
pub struct SeedAuthConfig {
    /// 待ち受けアドレス
    pub host: String,
    /// 待ち受けポート
    pub port: u16,
    /// 権限サービス（gRPC）の待ち受けポート。
    /// Lore 本体の `[environment.endpoint] auth_url` はここを指す。
    pub permission_port: u16,
    /// 新しいリポジトリを作ってよいアカウント名の一覧（空なら誰も作れない）。
    /// 照合は名前の一意判定と同じく **ASCII の大文字小文字を無視**する。
    pub repository_creators: Vec<String>,
    /// データフォルダ（accounts.json / issuer_key.json / jwks.json の置き場）
    pub data_dir: PathBuf,
    /// 発行する JWT の `iss`。Lore 側 `[server.auth] jwt_issuer` と一致させること
    pub issuer: String,
    /// 発行する JWT の `aud`。Lore 側 `[server.auth] jwt_audience` と一致させること
    pub audience: String,
    /// トークンの有効期間（時間）
    pub token_ttl_hours: u64,
    /// Lore 本体の `[environment.endpoint] auth_url`（書かれていれば）。
    ///
    /// 窓口はこの値を使わない。**起動時に「権限サービスを指していないのでは」と
    /// 助言するためだけに読む。** ここを取り違えると、
    /// クローンと `repository create` が「Not found」で落ちるのに
    /// 原因がどこにも出ない、という一番追いにくい壊れ方をする。
    pub configured_auth_url: Option<String>,
}

impl SeedAuthConfig {
    /// 発行窓口（HTTP）の待ち受けアドレスを `host:port` の形で返す。
    pub fn listen_address(&self) -> String {
        format!("{}:{}", self.host, self.port)
    }

    /// 権限サービス（gRPC）の待ち受けアドレスを `host:port` の形で返す。
    ///
    /// **常にループバックで待ち受ける**（`[seed_auth] host` には追随しない）。
    /// 権限サービスへ繋ぐのは同じプロセスの Lore 本体だけで、参加者の PC から届く必要が無い。
    /// トークンを受け取って権限を答える口を LAN へ晒さないための固定で、
    /// 発行窓口を `0.0.0.0` にしても権限サービスは外から見えない。
    pub fn permission_listen_address(&self) -> String {
        format!("{}:{}", PERMISSION_SERVICE_HOST, self.permission_port)
    }

    /// `[environment.endpoint] auth_url` が権限サービスを指していないと思われるとき、
    /// 起動時に出す助言を返す（問題が無ければ `None`）。
    ///
    /// **判定はポート番号だけ**で行う。ホスト名は逆プロキシや名前解決を挟んで
    /// 正当に違うことがあるので、そこで警告を出すと狼少年になる。
    /// 判定できない値（URL に見えない）は黙って通す。
    ///
    /// これは助言であって検証ではない（起動は止めない）。
    /// `auth_url` を読むのは Lore 本体であって窓口ではないため、
    /// ここで拒否すると「Lore は動くのに窓口が止める」という捻れた止まり方になる。
    pub fn auth_url_advice(&self) -> Option<String> {
        let auth_url = self.configured_auth_url.as_deref()?;
        if auth_url.trim().is_empty() {
            return None;
        }

        match port_in_url(auth_url) {
            // ポートが読めた かつ 権限サービスと違う → ほぼ確実に設定ミス
            Some(port) if port != self.permission_port => Some(format!(
                "[environment.endpoint] auth_url = \"{auth_url}\" のポートが \
                 permission_port ({}) と違います。\
                 権限サービス以外を指していると、クローンと repository create が \
                 「Not found」で落ちます",
                self.permission_port
            )),
            Some(_) => None,
            // ポートが書かれていない（http の 80 / https の 443 になる）
            None => Some(format!(
                "[environment.endpoint] auth_url = \"{auth_url}\" にポートがありません。\
                 権限サービス（permission_port = {}）を指しているか確認してください",
                self.permission_port
            )),
        }
    }

    /// `name` が「新しいリポジトリを作ってよい」一覧に載っているか。
    ///
    /// 照合はアカウント名の一意判定と同じ規則（ASCII の大文字小文字を畳む）。
    /// `alice` と `Alice` は同じ人なので、設定の書き方で挙動が変わらないようにする。
    pub fn may_create_repository(&self, name: &str) -> bool {
        let wanted = normalize_name_for_uniqueness(name);
        self.repository_creators
            .iter()
            .any(|allowed| normalize_name_for_uniqueness(allowed) == wanted)
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

    let configured_auth_url = root
        .environment
        .and_then(|environment| environment.endpoint)
        .and_then(|endpoint| endpoint.auth_url);

    let Some(raw) = root.seed_auth else {
        return Ok(None);
    };
    if !raw.enabled {
        return Ok(None);
    }

    validate(raw, configured_auth_url).map(Some)
}

/// 読み取った値の妥当性を確かめ、検証済みの設定へ変換する。
fn validate(
    raw: RawSeedAuthConfig,
    configured_auth_url: Option<String>,
) -> Result<SeedAuthConfig, String> {
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
    if raw.permission_port == 0 {
        return Err(format!(
            "[{SECTION_NAME}] permission_port に 0 は使えません"
        ));
    }
    // 同じポートで HTTP と gRPC の両方は待ち受けられない。
    // 設定ミスを起動時に落とす（動いてから「clone だけ通らない」を追うのは難しい）。
    if raw.permission_port == raw.port {
        return Err(format!(
            "[{SECTION_NAME}] permission_port は port と別の値にしてください: {}",
            raw.port
        ));
    }
    // 名前の規則に反する値を書かれたら起動時に落とす。
    // 通らない名前を黙って無視すると「なぜか作れない」になる。
    for creator in &raw.repository_creators {
        if let Err(e) = validate_name(creator) {
            return Err(format!(
                "[{SECTION_NAME}] repository_creators の名前 '{creator}' が規則に反します: {}",
                e.message()
            ));
        }
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
        permission_port: raw.permission_port,
        repository_creators: raw.repository_creators,
        data_dir: PathBuf::from(data_dir),
        issuer: raw.issuer,
        audience: raw.audience,
        token_ttl_hours: raw.token_ttl_hours,
        configured_auth_url,
    })
}

/// URL 文字列からポート番号を取り出す（`http://host:port/...` の `port`）。
///
/// URL crate を足さずに済ませるため、必要な形だけを自前で読む。
/// スキームが無い・ポートが無い・数字でない場合は `None`。
/// IPv6 リテラル（`http://[::1]:41352`）にも対応する。
fn port_in_url(url: &str) -> Option<u16> {
    // スキームを落とす
    let rest = url.split_once("://").map_or(url, |(_, rest)| rest);
    // パス・クエリ・フラグメントを落とす
    let authority = rest
        .split(['/', '?', '#'])
        .next()
        .unwrap_or_default();
    // 認証情報（user@host）を落とす
    let host_port = authority.rsplit_once('@').map_or(authority, |(_, h)| h);

    // IPv6 リテラルは `]` の後ろにだけポートが付く
    let port_text = match host_port.rsplit_once(']') {
        Some((_, after)) => after.strip_prefix(':')?,
        None => host_port.rsplit_once(':').map(|(_, port)| port)?,
    };

    port_text.parse::<u16>().ok()
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
        let config = validate(raw, None).unwrap();

        assert_eq!(config.host, DEFAULT_HOST);
        assert_eq!(config.port, DEFAULT_PORT);
        assert_eq!(config.permission_port, DEFAULT_PERMISSION_PORT);
        assert_eq!(config.issuer, DEFAULT_ISSUER);
        assert_eq!(config.audience, DEFAULT_AUDIENCE);
        assert_eq!(config.token_ttl_hours, DEFAULT_TOKEN_TTL_HOURS);
        assert_eq!(config.listen_address(), format!("{DEFAULT_HOST}:{DEFAULT_PORT}"));
        assert_eq!(
            config.permission_listen_address(),
            format!("{DEFAULT_HOST}:{DEFAULT_PERMISSION_PORT}")
        );
        // 既定では誰もリポジトリを作れない（安全側）。
        assert!(config.repository_creators.is_empty());
        assert!(!config.may_create_repository("tsubasa"));
    }

    /// `repository_creators` の照合が ASCII の大文字小文字を畳むこと。
    /// アカウント名の一意判定と同じ規則でないと、
    /// 設定の書き方次第で「作れるはずの人が作れない」が起きる。
    #[test]
    fn repository_creators_match_case_insensitively() {
        let raw: RawSeedAuthConfig = toml::from_str(
            "enabled = true\ndata_dir = \"C:/tmp/auth\"\nrepository_creators = [\"Alice\", \"つばさ\"]",
        )
        .unwrap();
        let config = validate(raw, None).unwrap();

        assert!(config.may_create_repository("alice"));
        assert!(config.may_create_repository("ALICE"));
        assert!(config.may_create_repository("つばさ"));
        assert!(!config.may_create_repository("bob"));
        // 日本語は大文字小文字の概念が無いので、そのまま一致するだけ。
        assert!(!config.may_create_repository("つばさ2"));
    }

    /// `repository_creators` に名前の規則を通らない値を書いたら起動を止めること。
    #[test]
    fn invalid_repository_creator_name_is_rejected() {
        let raw: RawSeedAuthConfig = toml::from_str(
            "enabled = true\ndata_dir = \"C:/tmp/auth\"\nrepository_creators = [\"ｔｓｕｂａｓａ\"]",
        )
        .unwrap();
        let error = validate(raw, None).unwrap_err();
        assert!(error.contains("repository_creators"), "説明が不十分: {error}");
    }

    /// URL からポートを読めること（助言の判定に使う）。
    #[test]
    fn port_is_read_from_a_url() {
        assert_eq!(port_in_url("http://127.0.0.1:41352"), Some(41352));
        assert_eq!(port_in_url("http://127.0.0.1:41352/"), Some(41352));
        assert_eq!(port_in_url("https://lore.example.com:8443/path?q=1"), Some(8443));
        assert_eq!(port_in_url("http://user@host:41352"), Some(41352));
        assert_eq!(port_in_url("http://[::1]:41352"), Some(41352));
        assert_eq!(port_in_url("127.0.0.1:41352"), Some(41352));

        // ポートが無い・読めない
        assert_eq!(port_in_url("https://seed-auth.invalid"), None);
        assert_eq!(port_in_url("http://[::1]"), None);
        assert_eq!(port_in_url("http://host:port"), None);
        assert_eq!(port_in_url(""), None);
    }

    /// `auth_url` が権限サービスを指していないときだけ助言を出すこと。
    #[test]
    fn auth_url_advice_fires_only_on_a_mismatch() {
        let with_auth_url = |auth_url: Option<&str>| -> Option<String> {
            let raw: RawSeedAuthConfig = toml::from_str(
                "enabled = true\ndata_dir = \"C:/tmp/auth\"\npermission_port = 41352",
            )
            .unwrap();
            validate(raw, auth_url.map(str::to_string))
                .unwrap()
                .auth_url_advice()
        };

        // 書いていない／空 → 助言なし（認証を有効にしていない段階）
        assert!(with_auth_url(None).is_none());
        assert!(with_auth_url(Some("   ")).is_none());
        // 正しく指している → 助言なし（ホストが違っても port が合えば黙る）
        assert!(with_auth_url(Some("http://127.0.0.1:41352")).is_none());
        assert!(with_auth_url(Some("http://lore.example.com:41352")).is_none());
        // 別のポート → 助言あり（発行窓口を指す取り違えが典型）
        assert!(with_auth_url(Some("http://127.0.0.1:41350")).is_some());
        // ポートが無い（以前の運用で使っていたダミー値）→ 助言あり
        assert!(with_auth_url(Some("https://seed-auth.invalid")).is_some());
    }

    /// 権限サービスのポートが発行窓口と同じなら起動を止めること。
    #[test]
    fn permission_port_must_differ_from_gateway_port() {
        let raw: RawSeedAuthConfig = toml::from_str(
            "enabled = true\ndata_dir = \"C:/tmp/auth\"\nport = 41350\npermission_port = 41350",
        )
        .unwrap();
        let error = validate(raw, None).unwrap_err();
        assert!(error.contains("permission_port"), "説明が不十分: {error}");
    }

    /// `data_dir` が無ければエラーになること
    /// （既定値で勝手な場所へ秘密鍵を書き出さない）。
    #[test]
    fn data_dir_is_required() {
        let raw: RawSeedAuthConfig = toml::from_str("enabled = true").unwrap();
        let error = validate(raw, None).unwrap_err();
        assert!(error.contains("data_dir"), "説明が不十分: {error}");
    }

    /// 値の範囲が検証されること。
    #[test]
    fn values_are_validated() {
        let with = |extra: &str| -> Result<SeedAuthConfig, String> {
            let text = format!("enabled = true\ndata_dir = \"C:/tmp/auth\"\n{extra}");
            validate(toml::from_str(&text).unwrap(), None)
        };

        assert!(with("port = 0").is_err());
        assert!(with("permission_port = 0").is_err());
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
permission_port = 41353
repository_creators = ["tsubasa"]
data_dir = "C:/tmp/seed_auth"
issuer = "seed-auth"
audience = "seed-lore"
token_ttl_hours = 8

[environment.endpoint]
auth_url = "http://127.0.0.1:41353"
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
        assert_eq!(config.permission_port, 41353);
        assert!(config.may_create_repository("tsubasa"));
        assert_eq!(config.data_dir, PathBuf::from("C:/tmp/seed_auth"));
        assert_eq!(config.token_ttl_hours, 8);
        // Lore 本体の節も読めて、助言が出ないこと（正しく指している設定）。
        assert_eq!(
            config.configured_auth_url.as_deref(),
            Some("http://127.0.0.1:41353")
        );
        assert!(config.auth_url_advice().is_none());
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
