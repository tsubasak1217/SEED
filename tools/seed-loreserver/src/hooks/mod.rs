// =============================================================================
// SEED 独自のサーバ側フック群
// =============================================================================
// upstream（lore-server）の `src/hooks/` は build.rs がディレクトリを走査して
// `register_all_hooks()` を自動生成する仕組みだが、それは lore-server crate の
// 中にファイルを置ける場合にしか使えない。
// こちらは自前バイナリ側なので、`main()` から明示的に register() を呼ぶ。
//
// フックを追加するときの手順:
//   1. このディレクトリに `<名前>.rs` を足す
//   2. `Hook` と `HookFactory` を実装する
//   3. このファイルに `pub mod <名前>;` を足す
//   4. `register()` に 1 行足す
//   5. 設定 `[hooks.<名前>] enabled = true` を書く
// =============================================================================

use lore_server::hooks::HookRegistrationContext;
use lore_server::hooks::HookRegistry;

pub mod push_guard;

/// SEED が提供するフックをすべてレジストリへ登録する。
///
/// `ctx` には通知送信器など、サーバ側のランタイム依存が入っている。
/// 現状のフックは使っていないが、将来通知を飛ばすフックを足すときに使う。
pub fn register(registry: &mut HookRegistry, ctx: &HookRegistrationContext) {
    push_guard::register(registry, ctx);
}
