// =============================================================================
// seed-loreserver エントリポイント
// =============================================================================
// 素の `loreserver` バイナリ（lore-server/src/bin/loreserver/main.rs）は
//
//     fn main() -> Result<()> { server_main(ServerConfig::default()) }
//
// の 2 行しかない。プラグインとフックは lore-server crate の build.rs が
// `src/plugins/` `src/hooks/` を走査して自動登録するため、
// 外部から増やすには `ServerConfig` に詰めて渡すしかない。
// このファイルはまさにそれだけを行う。
//
// コマンドライン引数と設定の読み込み方は `server_main()` の中で行われるため、
// 素の loreserver と完全に同一:
//     --config <DIR>  (環境変数 LORE_CONFIG_PATH)
//     --env    <ENV>  (環境変数 LORE_ENV、既定 "local")
// 設定ディレクトリからは default.toml → <env>.toml → local.toml の順に
// 重ねて読まれる（いずれも任意）。
//
// ログについての注意:
//   lore-server のログ購読は `EnvFilter::from_default_env()` を使うので、
//   環境変数 RUST_LOG を設定しないと ERROR 以外は一切出ない。
//   フックのログを見たいときは RUST_LOG=info を付けて起動すること。
// =============================================================================

mod auth;
mod hooks;
mod lock_store_file;

use anyhow::Result;
use anyhow::anyhow;
use lore_server::hooks::HookRegistrationContext;
use lore_server::hooks::HookRegistry;
use lore_server::plugins::PluginRegistry;
use lore_server::server::server_main;
use lore_server::server_config::HookRegistrationCallback;
use lore_server::server_config::ServerConfig;

/// SEED 用に拡張した Lore サーバを起動する。
///
/// 追加しているのは次の 3 点だけ。それ以外は upstream のまま動かす。
///   1. ロックストアプラグイン `seed_file_lock_store`
///      （`[lock_store] mode = "seed_file_lock_store"` で選択）
///   2. push ガードフック `seed_push_guard`
///      （`[hooks.seed_push_guard] enabled = true` で有効化）
///   3. SEED アカウント発行窓口（`[seed_auth] enabled = true` で有効化）
fn main() -> Result<()> {
    // --- SEED アカウント発行窓口を先に起動する ---------------------------
    // `server_main()` は同期関数で、内部で tokio ランタイムを作り
    // 呼び出しスレッドをブロックする。そのため窓口は**その前に**
    // 別スレッド＋別ランタイムで立ち上げておく必要がある。
    //
    // もう 1 つの理由: 窓口は起動時に `jwks.json` を書き出す。
    // Lore 本体は `[server.auth.jwk] endpoint` を起動時に読み、
    // 読めないとサーバ自体が起動に失敗するため、順序を逆にできない。
    //
    // ここで失敗したらサーバ全体を止める。窓口が動かないまま
    // Lore だけ認証有効で起動すると、誰もトークンを取れず全員が締め出される。
    auth::start().map_err(|e| anyhow!(e))?;

    // --- プラグインレジストリを組み立てる --------------------------------
    // `ServerConfig::plugin_registry` は「サーバ起動時にまず使われる」レジストリ。
    // lore-server 側は async_main() の中で、この上に
    // `plugins::register_all_plugins()`（aws / hashicorp）を重ねて登録する
    // （lore-server/src/server.rs:1655 付近）。
    // つまりここで登録した SEED 独自プラグインと、upstream 同梱のプラグインは
    // 共存する。名前が衝突すると register_*_plugin() が panic するので、
    // SEED 側のプラグイン名には必ず `seed_` 接頭辞を付けること。
    let mut plugin_registry = PluginRegistry::new();
    lock_store_file::register(&mut plugin_registry);

    // --- フック登録コールバックを積む ------------------------------------
    // フックは通知システムの初期化後でないと登録できない（HookRegistrationContext が
    // 通知送信器を持つため）。そのためクロージャとして渡し、
    // lore-server 側が適切なタイミングで呼ぶ。
    // 型を明示しているのは、クロージャ引数の高階ライフタイム推論を
    // コンパイラ任せにしないため（Box<dyn FnOnce(&mut _, &_)> は推論が落ちやすい）。
    let hook_callback: HookRegistrationCallback =
        Box::new(|registry: &mut HookRegistry, ctx: &HookRegistrationContext| {
            hooks::register(registry, ctx);
        });

    let config = ServerConfig {
        plugin_registry,
        hook_registration_callbacks: vec![hook_callback],
        // 環境検出（AWS / Nomad など）のテレメトリ用プロバイダ。
        // ローカル運用では不要なので None のまま。
        resource_detector_provider: None,
    };

    server_main(config)
}
