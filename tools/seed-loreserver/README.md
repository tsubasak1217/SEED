# seed-loreserver

SEED 用に拡張した [Lore](https://github.com/EpicGames/lore)（Epic Games 製 OSS バージョン管理、MIT）のサーバ実行ファイル。

upstream の `loreserver` バイナリをそのまま使わず自前でビルドしているのは、**プラグインとフックがコンパイル時にしか登録できない**ため。upstream は `lore-server/build.rs` が `src/plugins/` `src/hooks/` を走査して `register_all_plugins()` / `register_all_hooks()` を自動生成する仕組みで、外部から足すには `lore_server::server_config::ServerConfig` に詰めて `server_main()` へ渡す（＝自分でバイナリを持つ）しかない。

## 何を足しているか

| 追加物 | 種別 | 何のため |
| --- | --- | --- |
| `seed_file_lock_store` | `LockStore` プラグイン | upstream の `[lock_store] mode = "local"` はプロセス内メモリ（`DashMap`）なので、サーバを再起動するとロックが全部消える。JSON ファイルへ永続化する |
| `seed_push_guard` | `BranchPush` フック | push の内容をログに残し、設定フラグで push を拒否できるようにする。将来の「ロック中ファイルを含む push の拒否」の土台 |

それ以外は upstream のまま。将来 Epic の公式強制ロック（successor-locks LEP）が入ったら、ここの自作分を削って乗り換えられるよう薄く保つこと。

## ビルド

```bash
cd tools/seed-loreserver
cargo build              # デバッグ
cargo build --release    # リリース
```

- 初回はワークスペース全体（500 crate 超）をコンパイルするので **10〜30 分** かかる。
- メモリが厳しい環境では並列数を落とす: `CARGO_BUILD_JOBS=2 cargo build`（それでも落ちるなら `1`）。
- `protoc` は不要。`lore-server/build.rs` は `protoc` が PATH に無ければ、crate 内の生成済みソース（`src/legacy/generated/`）へフォールバックする。

### ビルド設定で注意が必要な 4 点

いずれも「upstream のワークスペース root にあるものは依存元には継承されない」ことに起因する。壊れたら真っ先にここを疑うこと。

1. **`.cargo/config.toml` の rustflags**
   upstream は `--cfg tokio_unstable`（tokio-metrics に必須）と `--cfg uuid_unstable` をワークスペース root の `.cargo/config.toml` で渡している。`.cargo/config.toml` は「ビルドを起動したディレクトリ」のものしか効かないので、本 crate 側で同じものを再宣言している。
   `[target.<triple>]` の `rustflags` は `[build]` と**マージされず置き換え**になるので、Windows 用ブロックにも共通フラグを重複して書く必要がある。

2. **`[patch.crates-io]` の `quinn-proto`**
   upstream は `vendor/quinn-proto`（`TransportConfig::max_rtt()` を足した独自版）へ差し替えており、その API を `lore-server/src/quic/quinn/quinn_server.rs` と `lore-transport/src/quic/client.rs` が実際に呼んでいる。`[patch]` もワークスペース root でしか効かないので、本 crate の `Cargo.toml` で同じ差し替えを再宣言している。これが無いと crates.io 版 0.11.13 が選ばれて `no method named max_rtt` でビルドが落ちる。

3. **空の `[workspace]`**
   SEED リポジトリの root にも Cargo ワークスペースがあるため、空の `[workspace]` テーブルでこの crate を切り離している。これが無いと *"current package believes it's in a workspace when it's not"* で resolve すらできない。root 側の `Cargo.toml` は触らない方針なので、こちらで対処する。

4. **`Cargo.lock` は upstream のものを種にする**
   `Cargo.lock` も継承されない。真っさらに resolve すると AWS SDK 系が最新へ上がり、`aws-smithy-types 1.7.0` と `aws-smithy-json 0.63.0` の組み合わせが**コンパイルできない**（`Document::Object` が `HashMap` から `DocumentObject` へ変わった破壊的変更）。
   本 crate の `Cargo.lock` は upstream の `Cargo.lock`（tag v0.9.0）をコピーしてから `cargo fetch` で最小限だけ書き換えたもの。**コミットして共有すること。** upstream タグを上げるときも同じ手順を踏む:

   ```bash
   # lore のソースアーカイブから Cargo.lock を持ってくる
   cp <lore-src>/Cargo.lock tools/seed-loreserver/Cargo.lock
   cargo fetch      # lore-* と quinn-proto の source を git 版へ差し替えてくれる
   cargo build
   ```

   `cargo update` を単体で叩くとこの固定が壊れる。上げたいときは上の手順ごとやり直すこと。

## 起動

```bash
# Windows (PowerShell)
$env:RUST_LOG = "info"
.\target\debug\seed-loreserver.exe --config C:\loreserver\config
```

引数と設定の読み込みは upstream の `server_main()` がそのまま処理するので、素の `loreserver` と完全に同じ。

| 引数 | 環境変数 | 意味 |
| --- | --- | --- |
| `--config <DIR>` | `LORE_CONFIG_PATH` | TOML 設定ディレクトリ |
| `--env <ENV>` | `LORE_ENV` | 環境名。既定 `local` |

設定ディレクトリからは `default.toml` → `<env>.toml` → `<env>_<region>.toml` → `local.toml` の順に重ねて読まれる（すべて任意）。`<env>` の既定が `local` なので、**普段書くファイル名は `local.toml`**。

> **ログが出ないときはまず `RUST_LOG`**
> lore-server のログ購読は `EnvFilter::from_default_env()` なので、`RUST_LOG` 未設定だと `ERROR` 以外は一切出力されない。フックのログを見たいときは `RUST_LOG=info` が必須。

`config/local.example.toml` が動作確認用のサンプル。**ポートを 41347 / 41349 にずらしてある**ので、本番用に持っていくときは既定（41337 / 41339）へ戻すこと。

## 設定キー

### `[lock_store]` — ロックストアの選択

```toml
[lock_store]
mode = "seed_file_lock_store"
```

`mode` に書いた名前でプラグインが引かれる。upstream 組み込みの値は `local`（プロセス内メモリ）と `aws`（DynamoDB）。

### `[plugins.seed_file_lock_store]` — ファイルロックストアの設定

| キー | 型 | 既定 | 意味 |
| --- | --- | --- | --- |
| `path` | string | `seed_locks.json` | ロックを保存する JSON ファイル。相対パスはサーバプロセスのカレント基準なので絶対パス推奨 |

設定の探索順は `[plugins.<mode>.lock_store]` → `[plugins.<mode>]`（`lore-server/src/store/configuration.rs` の `resolve_plugin_config_with_fallback`）。

保存形式:

```json
{
  "version": 1,
  "locks": [
    {
      "repository": "<32桁hex>",
      "branch": "<32桁hex>",
      "hash": "<64桁hex>",
      "description": "assets/mainGame/MainGame.scene",
      "owner": "someone@example.com",
      "locked_at": 1789000000000
    }
  ]
}
```

書き込みは `<path>.tmp` へ書いて `sync_all()` してから `rename` で置換する（原子的置換）。壊れた JSON を読んだ場合は**起動を失敗させる**。ロック情報を黙って失うほうが危険なため。

### `[hooks.seed_push_guard]` — push ガードフック

| キー | 型 | 既定 | 意味 |
| --- | --- | --- | --- |
| `enabled` | bool | `false` | フックの有効・無効。lore-server 側（`HookSettings`）が抜き取る共通キー |
| `reject_all` | bool | `false` | `true` にすると全 push を `PERMISSION_DENIED` で拒否する |

`enabled = true` を書き忘れるとフックは生成されない。逆に、**未登録のフック名を書くとサーバは起動時に panic する**（`create_enabled_hooks` の失敗が `expect` で扱われるため）。

拒否時に返す gRPC ステータスは `FailedPrecondition`。`PermissionDenied` ではない。クライアント（`lore-transport/src/error.rs:23`）は `tonic::Code::PermissionDenied` を引数なしの `NotAuthorized` へ畳むので、サーバが付けた拒否理由が捨てられ、利用者には「Not authorized to access repository」としか出ない（実測）。`FailedPrecondition` は同 `From` 実装の catch-all に落ちて message がそのまま届く。実測の差:

```text
# PermissionDenied のとき（理由が消える）
[Error] Not authorized to access repository

# FailedPrecondition のとき（理由が届く）
[Error] pushing branch to remote: code: 'The system is not in a state required for the operation's execution',
        message: "seed_push_guard: push rejected because reject_all = true in [hooks.seed_push_guard]"
```

## プラグイン／フックの足し方

### ロックストア以外のプラグイン（immutable / mutable / topology / notification）

1. `src/` に実装ファイルを足し、対応するファクトリ trait を実装する
   （`lore_server::plugins::{ImmutableStorePluginFactory, MutableStorePluginFactory, LockStorePluginFactory, TopologyPluginFactory, NotificationPluginFactory}`）。
   いずれも `fn validate_config(&self, &toml::Value)` / `fn create(&self, &toml::Value)` / `fn name(&self) -> &'static str` を持つ。
2. `pub fn register(registry: &mut PluginRegistry)` を書き、`registry.register_<種別>_plugin(Box::new(...))` を呼ぶ。
3. `src/main.rs` の `PluginRegistry` 組み立て部分から呼ぶ。

**名前は必ず `seed_` 接頭辞を付けること。** upstream 側も `async_main()` の中で `register_all_plugins()`（`aws` / `hashicorp`）を重ねて登録するので、名前が衝突すると `register_*_plugin()` が panic する。

### フック

1. `src/hooks/` に実装ファイルを足し、`lore_server::hooks::{Hook, HookFactory}` を実装する。
2. `pub fn register(registry: &mut HookRegistry, ctx: &HookRegistrationContext)` を書く。
3. `src/hooks/mod.rs` に `pub mod` と `register()` の呼び出しを足す。
4. 設定に `[hooks.<名前>] enabled = true` を書く。

`Hook` は 3 段構成。

| 段 | シグネチャ | 性質 |
| --- | --- | --- |
| pre | `fn pre_handler(&self, &HookContext) -> Result<(), HookError>` | 同期・操作前・`Err(Rejected)` で拒否できる。既定タイムアウト 200ms |
| response | `fn response_handler(&self, &HookContext) -> Result<HookResponse, HookError>` | 同期・成功後・クライアント応答へメッセージを添えられる。エラーは操作を失敗させない |
| post | `async fn post_handler(&self, &HookContext) -> Result<(), HookError>` | 非同期・別タスク・応答をブロックしない。エラーはログのみ |

## フックから取れる情報（v0.9.0 実測）

`HookContext` が公開しているのは次だけ。

| 取得子 | 型 | BranchPush で入るもの |
| --- | --- | --- |
| `correlation_id()` | `&str` | リクエスト相関 ID |
| `hook_point()` | `HookPoint` | `BranchPush` |
| `repository()` | `RepositoryId` | リポジトリ ID |
| `user()` | `Option<&str>` | push したユーザー ID |
| `branch()` | `Option<BranchId>` | 対象ブランチ ID |
| `revision()` | `Option<Hash>` | push しようとしているリビジョンのハッシュ |
| `revision_number()` | `Option<u64>` | pre では `None`、post で確定値が入る |
| `metadata()` | `&HashMap<String,String>` | `client_ip` のみ |

> **`user()` は `[server.auth]` を設定しないと常に `"<unknown>"`。**
> 値は JWT の `AuthorizationToken` から来る（`lore-server/src/util/mod.rs:51` の `get_user_id_from_token_ref`）。
> JWT 検証が無効（＝`[server.auth]` 不在、shipped config はすべてこれ）だと `"<unknown>"` が返る。
> **ロックの owner も同じ経路**（`grpc/lock_service.rs` も `get_user_id()` を使う）なので、
> 認証を入れないと全員が `<unknown>` になり「他人のロック」という概念が成立しない。
> ロック強制を本気でやるなら `[server.auth.jwk]` の整備が前提条件。実測ログ:
> `user="<unknown>"` / `hello.txt by <unknown> on branch ...`

**push に含まれるファイル一覧は入っていない。** pre-handler の呼び出し元は `lore-server/src/grpc/revision/v1/branch_push.rs:110` 付近（および legacy 用の `grpc/handlers/branch_push.rs:130` 付近）で、`client_ip` 以外の metadata は積まれない。

なお **response-handler** 段（`grpc/handlers/branch_push.rs` の `dispatch_response_message`）だけは metadata が厚く、`repository_name` / `default_branch_name` / `is_default_branch` / `branch_name` が入る。ただしこの段は push が成功した後に走るので、拒否には使えない。

ロック照合に必要なキーの作り方は分かっている。`lore-revision/src/lock/util.rs` の `assemble_resource_for_path()` が

```rust
LockResource { branch, hash: hash(path.as_bytes()), description: path.to_string() }
```

を作っているので、**変更ファイルのパス文字列さえ手に入れば**、同じ計算で `LockResource` を組み立てて `LockStore::check_locks_status()` に問い合わせられる。足りないのはパスの一覧だけ。

ハッシュ関数は blake3（`lore_storage::hash::hash_slice()` = `blake3::hash(data)`、`lore-storage/src/hash.rs:75`）。`lore_revision::hash` 側の再エクスポートは `pub(crate)` なので、使うときは `lore-storage` を直接依存に足すこと。

### ロック強制を実装するときの設計案

`revision()` で得られるハッシュから木を辿れば変更ファイルは算出できるが、そのためにはストア（`ImmutableStore` / `MutableStore`）とロックストアへの参照が要る。ところが `HookFactory::create()` は TOML 設定しか受け取らず、`HookRegistrationContext` も `notification_sender` しか持たないため、サーバが使っているインスタンスはフックへ渡ってこない。取りうる道は 3 つ。

1. **フックが自前でストアを開く** — `[hooks.seed_push_guard]` にストアのパスとロックファイルのパスを重複して書かせ、フック側で読み取り専用に開く。upstream を一切変えずに済むが、設定の二重管理になり、ロックストアの実体が 2 つになる（本 crate の `SeedFileLockStore` を `Arc` で共有する形に直せば実体は 1 つにできる）。
2. **`main()` で共有インスタンスを作って両方へ渡す** — `SeedFileLockStore` を `main()` で 1 つ作り、`Arc` のクローンをプラグインファクトリとフックファクトリの両方にキャプチャさせる。ロックストアは確実に同一実体になる。ストア（差分算出用）は依然として別途開く必要がある。**現時点ではこれが本命。**
3. **upstream へ PR** — `HookRegistrationContext` にストアとロックストアを足す。本筋だが待ち時間が読めない。

**pre-handler の 200ms 制限は打ち切りではない**点に注意。`HookDispatcher::execute_pre_handler_with_isolation`（`lore-server/src/hooks/dispatch.rs:287`）は handler を最後まで走らせてから経過時間を見て、超えていたら `HookError::Timeout` にする。つまり遅い pre-handler は「push を待たせた上で失敗させる」。ロック照合に I/O を挟むなら、200ms に収まる保証が要る（ロックはメモリ上にあるので照合自体は一瞬。重いのは差分算出のほう）。

差分算出そのものが重いなら、pre-handler の 200ms 制限に収まらない可能性がある。その場合は「クライアント側 `lore` がロック中ファイルを stage できないようにする」方向と併用し、サーバ側は `revision` 単位の粗いチェック（ロックが 1 件でもあれば拒否、など）に留めるのが現実的。

## upstream 更新時（v0.9.x → 次）の手順

1. `Cargo.toml` の 4 箇所の `tag = "v0.9.0"` を新しいタグへ書き換える（`lore-server` / `lore-base` / `lore-revision` / `[patch.crates-io] quinn-proto`）。
2. upstream の `.cargo/config.toml` を読み直し、`--cfg` フラグが増減していないか確認して本 crate の `.cargo/config.toml` へ反映する。
3. upstream の root `Cargo.toml` の `[patch.crates-io]` を読み直し、差し替え対象が増減していないか確認する。
4. `cargo update && cargo build && cargo test`。
5. 壊れやすい順に確認する: `ServerConfig` のフィールド構成 → `LockStore` trait のメソッド追加 → `HookContext` の取得子 → `PluginRegistry::register_*` の名前。

Epic 公式の強制ロック（successor-locks LEP）が入ったら、`seed_push_guard` と `seed_file_lock_store` を残す意味があるか再評価すること。公式実装があるならそちらへ寄せる。

## 動作確認済みのこと（2026-09-15、Windows 11 / rustc 1.98.0 / lore CLI 0.9.0+783）

| 確認項目 | 結果 |
| --- | --- |
| 起動時のプラグイン登録 | `lock stores: ["seed_file_lock_store", "aws"]`（upstream の `aws` と共存） |
| 起動時のフック登録 | `Created enabled hook hook_name="seed_push_guard" hook_points=[BranchPush]` |
| `repository create` → `stage` → `commit` → `push` | 成功。pre / post 両方でフックのログが出る |
| `reject_all = true` での push | `FailedPrecondition` で拒否され、拒否理由が CLI に出る |
| `lock acquire` → **サーバ再起動** → `lock query` | ロックが残る（起動ログ `lock_count=1`） |
| `lock release` | JSON から消え、`"locks": []` になる |

`aws` / `consul` プラグインが `register_all_plugins()` で登録されている点は、config リファレンス（`docs/reference/lore-server-config.md` の「Reference plugins」節）の「neither is compiled into `loreserver`」という記述と食い違う。v0.9.0 のソースでは `lore-server/src/plugins/{aws,hashicorp}.rs` が存在し `register_all_plugins()` から呼ばれている。ドキュメントが古い可能性が高いので、`seed_` 接頭辞の名前規約は崩さないこと（衝突すると `register_*_plugin()` が panic する）。

## テスト

```bash
cargo test
```

`src/lock_store_file.rs` の単体テストで、永続化・再読み込み・所有者検証・all-or-nothing・壊れたファイルの扱いを確認している。`src/hooks/push_guard.rs` の単体テストで `reject_all` の挙動を確認している。

サーバを実際に起動した状態での確認（`lore` CLI で repository create → push → ロック取得 → 再起動 → ロックが残っている）は手作業。手順は `config/local.example.toml` のコメントを参照。
