// =============================================================================
// SEED push ガードフック
// =============================================================================
// 目的:
//   `HookPoint::BranchPush` の pre-handler で push を拒否できることを実証し、
//   将来「他人がロック中のファイルを含む push を拒否する」実装を載せるための
//   土台を作る。
//
// 現段階でやること（意図的にここまで）:
//   - push 要求の内容（リポジトリ / ブランチ / ユーザー / リビジョン等）を
//     ログへ出す。「フックから何が見えるか」を実機で確定させるのが目的。
//   - 設定 `[hooks.seed_push_guard] reject_all = true` のとき、すべての push を
//     `HookError::Rejected` で拒否する。拒否経路が本当に効くことの確認用。
//
// 現段階でやらないこと:
//   - ロック状態を見た拒否。理由は下の「フックから取れる情報」を参照。
//
// フックから取れる情報（lore-server v0.9.0 の `HookContext` を実測）:
//   correlation_id / hook_point / repository(RepositoryId) / user(String) /
//   branch(BranchId) / revision(Hash) / revision_number(Option<u64>) /
//   metadata(HashMap<String,String>)
//   BranchPush の呼び出し元
//   （lore-server/src/grpc/revision/v1/branch_push.rs:110 付近）が
//   metadata へ入れるのは `client_ip` だけ。
//   **push に含まれるファイル一覧は HookContext に入っていない。**
//   revision（ツリーのハッシュ）は取れるので、そこから木を辿れば
//   変更ファイルは算出できるが、そのためにはストアへのアクセスが要る。
//   `HookFactory::create` は TOML 設定しか受け取らず、
//   `HookRegistrationContext` も通知送信器しか持たないため、
//   サーバが使っているストアの参照はフックへ渡ってこない。
//   → 実装案は README の「ロック強制の設計案」を参照。
//
// pre-handler は同期・タイムアウト 200ms（DEFAULT_PRE_HANDLER_TIMEOUT）。
// ここでブロッキング I/O をすると push 全体が詰まるので注意すること。
// =============================================================================

use async_trait::async_trait;
use lore_server::hooks::Hook;
use lore_server::hooks::HookContext;
use lore_server::hooks::HookError;
use lore_server::hooks::HookFactory;
use lore_server::hooks::HookPoint;
use lore_server::hooks::HookRegistrationContext;
use lore_server::hooks::HookRegistry;
use lore_server::hooks::StatusCode;
use serde::Deserialize;
use tracing::info;
use tracing::warn;

// -----------------------------------------------------------------------------
// 定数（マジックナンバー禁止のためすべて名前を付ける）
// -----------------------------------------------------------------------------

/// このフックの名前。設定セクション `[hooks.seed_push_guard]` と対応する。
pub const HOOK_NAME: &str = "seed_push_guard";

/// このフックが反応する拡張ポイント。
/// push だけを見るので `BranchPush` のみ。
const HOOK_POINTS: &[HookPoint] = &[HookPoint::BranchPush];

/// `reject_all = true` のときにクライアントへ返すメッセージ。
///
/// このメッセージは gRPC の status message としてクライアントへ渡り
/// （`lore-server/src/grpc/mod.rs::hook_error_to_status`）、
/// Windows のコンソールにも出る。端末のコードページ次第で日本語が化けるので、
/// ここだけは ASCII に限定する（コメントは日本語のまま）。
/// ただし利用者に届くかどうかはステータスコード次第。下の `REJECT_STATUS` を参照。
const REJECT_ALL_MESSAGE: &str =
    "seed_push_guard: push rejected because reject_all = true in [hooks.seed_push_guard]";

/// 拒否時に使う gRPC ステータス。
///
/// **`PermissionDenied` を使ってはいけない。** クライアント側
/// （`lore-transport/src/error.rs:23`）は `tonic::Code::PermissionDenied` を
/// 引数なしの `NotAuthorized` へ畳んでしまうため、サーバが付けた message が
/// 捨てられ、利用者には「Not authorized to access repository」としか出ない。
/// 実測でもそうなった。
///
/// `FailedPrecondition` は同 From 実装の catch-all（`_` 腕）に落ちて
/// `ProtocolError::internal(status.to_string())` になるので、message が
/// そのまま利用者の画面へ届く。将来「このファイルは誰々がロック中」を
/// 伝えたいので、拒否理由が読めることを優先してこちらを選ぶ。
/// 意味的にも「今のリポジトリの状態／方針ではこの push は通せない」で合っている。
const REJECT_STATUS: StatusCode = StatusCode::FailedPrecondition;

// -----------------------------------------------------------------------------
// 設定
// -----------------------------------------------------------------------------

/// `[hooks.seed_push_guard]` の設定。
///
/// `enabled` は lore-server 側（`HookSettings`）が先に抜き取るので、
/// ここには現れない。
#[derive(Debug, Deserialize)]
struct SeedPushGuardConfig {
    /// true のとき、すべての push を拒否する。
    /// 動作確認およびメンテナンス時の緊急停止用。
    #[serde(default)]
    reject_all: bool,
}

// -----------------------------------------------------------------------------
// フック本体
// -----------------------------------------------------------------------------

/// push を監視し、必要に応じて拒否するフック。
pub struct SeedPushGuard {
    /// すべての push を拒否するかどうか
    reject_all: bool,
}

impl SeedPushGuard {
    /// 設定値からフックを作る。
    fn new(reject_all: bool) -> Self {
        Self { reject_all }
    }

    /// push 要求の内容をログへ出す。
    ///
    /// pre-handler と post-handler の両方から呼べるよう、
    /// 「どの段階か」を `phase` で受け取る。
    fn log_push(&self, phase: &'static str, ctx: &HookContext) {
        // branch / revision は Option。未設定時にログが欠けると
        // 「取れないのか、空なのか」が分からなくなるので "-" で埋める。
        let branch = ctx
            .branch()
            .map_or_else(|| "-".to_string(), |branch| branch.to_string());
        let revision = ctx
            .revision()
            .map_or_else(|| "-".to_string(), |revision| revision.to_string());
        let revision_number = ctx
            .revision_number()
            .map_or_else(|| "-".to_string(), |number| number.to_string());

        info!(
            hook = HOOK_NAME,
            phase,
            correlation_id = ctx.correlation_id(),
            repository = %ctx.repository(),
            branch = %branch,
            user = ctx.user().unwrap_or("-"),
            revision = %revision,
            revision_number = %revision_number,
            // metadata には現状 client_ip しか入らないが、
            // upstream が項目を増やしたときに気づけるよう丸ごと出す。
            metadata = ?ctx.metadata(),
            // 変更ファイル一覧は HookContext に含まれない（v0.9.0 実測）。
            // ロック強制を実装するときはここが課題になる。
            changed_files = "unavailable-in-hook-context",
            "BranchPush フックが呼ばれました"
        );
    }
}

#[async_trait]
impl Hook for SeedPushGuard {
    fn name(&self) -> &'static str {
        HOOK_NAME
    }

    fn hook_points(&self) -> &'static [HookPoint] {
        HOOK_POINTS
    }

    /// push 実行前に呼ばれる同期ハンドラ。
    /// `Err(HookError::Rejected)` を返すと push 自体が中止される。
    fn pre_handler(&self, ctx: &HookContext) -> Result<(), HookError> {
        self.log_push("pre", ctx);

        if self.reject_all {
            warn!(
                hook = HOOK_NAME,
                correlation_id = ctx.correlation_id(),
                user = ctx.user().unwrap_or("-"),
                "reject_all が有効なため push を拒否します"
            );
            return Err(HookError::rejected(
                HOOK_NAME,
                REJECT_ALL_MESSAGE,
                REJECT_STATUS,
            ));
        }

        Ok(())
    }

    /// push 成功後に非同期で呼ばれるハンドラ。
    /// ここでは監査ログ用に、確定したリビジョン番号込みで記録するだけ。
    /// エラーを返してもクライアントへは伝わらない（ログのみ）。
    async fn post_handler(&self, ctx: &HookContext) -> Result<(), HookError> {
        self.log_push("post", ctx);
        Ok(())
    }
}

// -----------------------------------------------------------------------------
// フックファクトリ
// -----------------------------------------------------------------------------

/// `SeedPushGuard` を設定から生成するファクトリ。
pub struct SeedPushGuardFactory;

impl HookFactory for SeedPushGuardFactory {
    fn name(&self) -> &'static str {
        HOOK_NAME
    }

    /// TOML 設定からフックを組み立てる。
    ///
    /// # Errors
    /// 設定を構造体へ落とせない場合 `HookError::ConfigError` を返す。
    /// この場合サーバは起動時に panic する
    /// （`create_enabled_hooks` の失敗は `expect` で扱われるため）。
    /// 設定ミスを黙って無視して「守っているつもり」にならないので、
    /// ガード用途としてはこの挙動で正しい。
    fn create(&self, config: &toml::Value) -> Result<Box<dyn Hook>, HookError> {
        let parsed: SeedPushGuardConfig = config.clone().try_into().map_err(|e| {
            HookError::config_error(HOOK_NAME, format!("設定を解釈できません: {e}"))
        })?;

        info!(
            hook = HOOK_NAME,
            reject_all = parsed.reject_all,
            "SEED push ガードフックを構成しました"
        );

        Ok(Box::new(SeedPushGuard::new(parsed.reject_all)))
    }
}

/// このモジュールが提供するフックをレジストリへ登録する。
///
/// `_ctx` は通知送信器などのランタイム依存。現状は未使用。
pub fn register(registry: &mut HookRegistry, _ctx: &HookRegistrationContext) {
    registry.register_hook(Box::new(SeedPushGuardFactory));
}

// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use lore_revision::lore::RepositoryId;

    use super::*;

    /// テスト用の最小 `HookContext` を作る。
    fn make_context() -> HookContext {
        HookContext::builder()
            .correlation_id("test-correlation")
            .hook_point(HookPoint::BranchPush)
            .repository(RepositoryId::default())
            .user("tester@example.com")
            .build()
    }

    /// 既定（reject_all 未指定）では push を通すこと。
    #[test]
    fn default_config_allows_push() {
        let config: toml::Value = toml::Value::Table(toml::map::Map::new());
        let hook = SeedPushGuardFactory.create(&config).unwrap();
        assert!(hook.pre_handler(&make_context()).is_ok());
    }

    /// `reject_all = true` のとき push を拒否すること。
    /// 返るのは `Rejected` で、status は `PermissionDenied`。
    #[test]
    fn reject_all_blocks_push() {
        let config: toml::Value = toml::from_str("reject_all = true").unwrap();
        let hook = SeedPushGuardFactory.create(&config).unwrap();

        let err = hook
            .pre_handler(&make_context())
            .expect_err("reject_all なのに push が通ってしまった");
        assert_eq!(err.hook_name(), HOOK_NAME);
        // PermissionDenied だとクライアント側で message が捨てられる
        // （REJECT_STATUS のコメント参照）。ここを守るためのテスト。
        assert_eq!(err.status_code(), Some(StatusCode::FailedPrecondition));
    }

    /// 反応する拡張ポイントが BranchPush だけであること。
    #[test]
    fn only_watches_branch_push() {
        let config: toml::Value = toml::Value::Table(toml::map::Map::new());
        let hook = SeedPushGuardFactory.create(&config).unwrap();
        assert_eq!(hook.hook_points(), &[HookPoint::BranchPush]);
    }
}
