# seed-loreserver

SEED 用に拡張した [Lore](https://github.com/EpicGames/lore)（Epic Games 製 OSS バージョン管理、MIT）のサーバ実行ファイル。

upstream の `loreserver` バイナリをそのまま使わず自前でビルドしているのは、**プラグインとフックがコンパイル時にしか登録できない**ため。upstream は `lore-server/build.rs` が `src/plugins/` `src/hooks/` を走査して `register_all_plugins()` / `register_all_hooks()` を自動生成する仕組みで、外部から足すには `lore_server::server_config::ServerConfig` に詰めて `server_main()` へ渡す（＝自分でバイナリを持つ）しかない。

## 何を足しているか

| 追加物 | 種別 | 何のため |
| --- | --- | --- |
| `seed_file_lock_store` | `LockStore` プラグイン | upstream の `[lock_store] mode = "local"` はプロセス内メモリ（`DashMap`）なので、サーバを再起動するとロックが全部消える。JSON ファイルへ永続化する |
| `seed_push_guard` | `BranchPush` フック | push の内容をログに残し、設定フラグで push を拒否できるようにする。将来の「ロック中ファイルを含む push の拒否」の土台 |
| `seed_auth`（発行窓口） | 別スレッドの HTTP サーバ | SEED アカウント（参加者の登録・招待・ログイン）を管理し、Lore が受け取れる短寿命の JWT を発行する。`src/auth/` |
| `seed_auth`（権限サービス） | 別スレッドの gRPC サーバ | Lore 本体が `[environment.endpoint] auth_url` へ投げる権限の問い合わせ（`epic_urc.UrcAuthApi` / `ucs.auth.RebacApi`）に答える。これが無いと **auth_url を設定した時点で clone と `repository create` が「Not found」で落ちる**。`src/auth/permission/` |

それ以外は upstream のまま。将来 Epic の公式強制ロック（successor-locks LEP）が入ったら、ここの自作分を削って乗り換えられるよう薄く保つこと。

## ビルド

```bash
cd tools/seed-loreserver
cargo build              # デバッグ
cargo build --release    # リリース
```

- 初回はワークスペース全体（500 crate 超）をコンパイルするので **10〜30 分** かかる。
- メモリが厳しい環境では並列数を落とす: `CARGO_BUILD_JOBS=2 cargo build`（それでも落ちるなら `1`）。
- **`protoc` をシステムへ入れる必要は無い。** 本 crate の `build.rs` は
  `protoc-bin-vendored`（ビルド依存）が同梱する実行ファイルを使って `proto/*.proto` から
  権限サービスのサーバ側スタブを生成する。upstream の `lore-server/build.rs` は
  `protoc` が PATH に無ければ crate 内の生成済みソース（`src/legacy/generated/`）へフォールバックする。

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

## 発行窓口と権限サービス（SEED アカウント）

参加者の登録・招待・チャレンジ応答ログイン・JWT 発行を行う小さな HTTP サーバ（**発行窓口**、既定 41350）と、
Lore 本体からの権限の問い合わせに答える gRPC サーバ（**権限サービス**、既定 41352）。
**取り決めの正典は `docs/seed_accounts.md`**（鍵と署名の形式、HTTP API、トークンの中身、権限サービスが話す RPC）。

実装は `src/auth/`。

| ファイル | 役目 |
| --- | --- |
| `config` / `model` / `store` / `issuer` / `token` / `user_key` / `challenge` / `crypto` / `atomic_file` / `http` | 発行窓口 |
| `permission/mod.rs` | 権限サービスの起動（別スレッド＋別ランタイム＋別ポート） |
| `permission/proto.rs` | `build.rs` が生成した gRPC スタブの取り込み |
| `permission/decision.rs` | **判定そのもの**（gRPC に依存しない。単体テストはここ） |
| `permission/urc_auth.rs` | `epic_urc.UrcAuthApi` の実装（翻訳だけ） |
| `permission/rebac.rs` | `ucs.auth.RebacApi` の実装（翻訳だけ） |

権限サービスは**発行窓口と同じ `AccountStore` の実体**を見る。だから招待・失効が判定へ即座に反映される。
判定に使うのはトークンの `sub`（誰か）だけで、`resources` は見ない。

### 起動

`main()` が `server_main()` を呼ぶ**前に** `auth::start()` を呼ぶ（発行窓口と権限サービスの両方を起動する）。理由は 2 つ。

1. `server_main()` は同期関数で、内部で tokio ランタイムを作って呼び出しスレッドをブロックする。
   非同期タスクの内側から呼ぶと panic するので、窓口は先に別スレッド＋別ランタイムで起動しておく。
2. 窓口は起動時に `jwks.json` を書き出す。Lore 本体は `[server.auth.jwk] endpoint` を**起動時に読み**、
   読めないとサーバ自体が起動に失敗する（`lore-server/src/server.rs` の `fetch_new_keys(None).await?`）。

`[seed_auth] enabled = false`（または節が無い）なら何も起動しない。
設定・鍵・データファイル・ポートのいずれかに問題があれば **サーバ全体の起動を止める**
（窓口が動かないまま Lore だけ認証有効で起動すると、誰もトークンを取れず全員が締め出される）。

起動時のメッセージは `eprintln!`（stderr）で出す。tracing の購読は `server_main()` の中で
初期化されるため、それより前のログを tracing で出しても捨てられてしまう。
起動後のリクエストログは `tracing` の `info!`。

### 設定 `[seed_auth]`

| キー | 型 | 既定 | 意味 |
| --- | --- | --- | --- |
| `enabled` | bool | `false` | 窓口と権限サービスを起動するか |
| `host` | string | `127.0.0.1` | 待ち受けアドレス（両方に効く）。チームが繋ぐなら `0.0.0.0` |
| `port` | u16 | `41350` | 発行窓口（HTTP）の待ち受けポート |
| `permission_port` | u16 | `41352` | 権限サービス（gRPC）の待ち受けポート。`port` と同じ値なら起動を止める |
| `repository_creators` | 文字列配列 | `[]` | 新しいリポジトリを作ってよいアカウント名。**既定は空 ＝ 認証が有効な間は誰も作れない。** 照合は ASCII の大文字小文字を無視。規則に反する名前を書くと起動を止める |
| `data_dir` | string | （必須） | データファイルの置き場。`enabled = true` なら省略不可 |
| `issuer` | string | `seed-auth` | JWT の `iss`。`[server.auth] jwt_issuer` と一致させる |
| `audience` | string | `seed-lore` | JWT の `aud`。`[server.auth] jwt_audience` と一致させる |
| `token_ttl_hours` | u64 | `8` | トークンの有効期間（1〜24） |

`[environment.endpoint] auth_url` は **`http://<host>:<permission_port>`** を書く
（`host` が `0.0.0.0` なら `127.0.0.1` で自分を指す）。`https://` で始めなければ TLS は使われない。

読み込み規則は Lore 本体とまったく同じ（`--config <DIR>` / `LORE_CONFIG_PATH`、
`--env <ENV>` / `LORE_ENV`、`default.toml` → `<env>.toml` → `<env>_<region>.toml` → `local.toml` → `LORE__*`）。
`server_main()` とは別に同じファイルを読み直して `[seed_auth]` 節だけを取り出している。

> **未知の節は Lore 本体の設定読み込みを壊さない。**
> `lore-server/src/settings.rs` の `Settings` は `#[serde(deny_unknown_fields)]` が
> コメントアウトされており（同ファイル 31 行、933 行の TODO コメント）、
> `config` crate 側も `try_deserialize` を serde へ丸投げするだけなので、
> `[seed_auth]` は黙って読み捨てられる。実機でも確認済み。別ファイルへ逃がす必要は無い。

### データファイル（`data_dir` 直下）

| ファイル | 中身 | 注意 |
| --- | --- | --- |
| `accounts.json` | 参加者・公開鍵・リポジトリごとの権限・招待コードのハッシュ | 招待コードの**平文は保存しない** |
| `issuer_key.json` | サーバの署名鍵（PKCS#8）。初回起動時に生成 | **秘密鍵。共有フォルダに置かない** |
| `jwks.json` | 公開鍵（JWKS）。`alg` と `kid` を必ず書く | Lore 本体が `file://` で読む |

いずれも `<path>.tmp` へ書いて `sync_all()` してから `rename` で置換する（原子的置換、`src/auth/atomic_file.rs`）。
壊れた JSON を読んだ場合は**起動を失敗させる**（参加者やロックを無言で失うほうが危険）。

署名アルゴリズムは **EdDSA（Ed25519）**。`src/auth/issuer.rs` の `ISSUER_ALGORITHM` 定数で決まる。
ES256 も実装してあり、Lore v0.9.0 はどちらも受理する（JWK の `alg` から決まる）。
定数を変えて再ビルドすると、既存の鍵ファイルは起動時に作り直される。

### 有効化の手順（**順番が重要**）

1. `[seed_auth]` だけ書いて起動する。`jwks.json` が作られる。
2. エディタ（またはループバックからの `POST /v1/bootstrap`）でオーナーを登録する。
   `repository_id` は `.lore/id`（生 16 バイト）を 32 桁の 16 進小文字にしたもの。
   **`.lore/id` はリポジトリを作らないと存在しない**ので、リポジトリ作成が先。
3. `[server.auth]` / `[server.auth.jwk]` / `[environment.endpoint] auth_url` を足して再起動する。
4. 以後、すべての操作にトークンが要る。

**再起動はこの 1 回だけ。** `auth_url` を権限サービスへ向けてあれば、
clone・push・pull・ロック・新しいリポジトリの作成がこの 1 つの設定で通る。
2 つめ以降のリポジトリは、`repository_creators` に載っている人が作れば
**その人が自動で owner になる**ので、手順 1〜2 をやり直さなくてよい。

> **止めるときは Ctrl+C。** 強制終了すると mutable ストアの遅延書き出しが間に合わず、
> リポジトリ名 → ID の対応とブランチの先端が失われる（落とし穴表の最終行）。

### 運用上の注意

- **平文 HTTP／平文 h2c。** LAN 内の信頼が前提。チャレンジ署名は再利用できないが、発行済みトークンは盗聴され得る。
  LAN の外へ出すなら TLS を前段に置くこと。
- **失効が効く範囲（実測）。** 権限サービスと発行窓口は `accounts.json` を毎回見るので、
  そこを通る操作（**クローン・リポジトリの作成／削除・`repository list`・
  メタデータを手元に持っていない送信**・招待・一覧・失効・ログイン）は**即座に**拒否される。
  一方、**既に接続していてメタデータを持っているクライアントの送信**、ロックの取得・照会・解放、
  「最新を取得」は Lore 側が**トークンの `resources`** だけを見るため、
  そのトークンの期限（`token_ttl_hours`）までは通ってしまう。
- **認証は全部か無か。** `[server.auth]` を有効にすると匿名アクセスは一切できなくなる。
- **`repository_creators` は既定で空**（誰も新しいリポジトリを作れない）。
  オーナーの名前を書き忘れると、認証を有効にした瞬間から `repository create` が
  `permission denied` で落ちる。
- `/v1/bootstrap` はループバック接続からのみ受け付ける（招待も認証も無しにオーナーになれるため）。
- 招待コードの平文・秘密鍵・トークンはログに出していない。出さないこと。

### Lore v0.9.0 側の落とし穴（実測）

| 事象 | 原因 | 対処 |
| --- | --- | --- |
| `--access-token` だけでは `repository create` / `clone` が「authorization header required」で落ちる | `auth_exchange_for_identity`（`lore-transport/src/auth/exchange.rs:537`）は `repository.is_zero()` のとき authorization token を空にする。リポジトリ ID が確定していない呼び出しでは access token が使われない | **`--identity-token` と `--access-token` の両方に同じ JWT を渡す**（`--identity` は渡さない。トークンが identity を名乗るので排他エラーになる） |
| `pull` / `sync` が「Not authorized to access repository」で落ちる | QUIC のストレージセッション（`lore-transport/src/quic/storage_service/client.rs` の `session_start`）は、サーバから受け取った environment の `auth_url` が空だとトークンを載せない（判定は `lore-transport/src/connection.rs` の `if !auth_url.is_empty()`） | サーバ設定に `[environment.endpoint] auth_url = "http://<host>:<permission_port>"` を足す。クライアントは供給されたトークンで交換を短絡するので、その URL へ接続しにはいかない |
| **`auth_url` を設定すると `clone` が「Not found」（rc 13）で落ち、`repository create` も落ちる**（**解消済み**。権限サービスを止める／`auth_url` を別の場所へ向けると再発する） | `lore-server/src/grpc/repository/v1/repository_get.rs` の `repository_load_name` / `repository_load_id` が `auth_url` があるときだけ `check_repository_query_authorization` を呼び、`lore-server/src/authnz/repository_authorizer.rs` がその URL の gRPC `epic_urc.UrcAuthApi/CheckUserPermission` へ接続する。`repository create` は `repository_create.rs:293` で同じ URL の `ucs.auth.RebacApi/CreateResource` を呼ぶ。応答する相手が居ないと必ず失敗し、失敗は `RepositoryNotFound` に畳まれる | **`auth_url` を `seed-loreserver` 自身の権限サービス（既定 `http://127.0.0.1:41352`）へ向ける。** `[seed_auth] enabled = true` なら自動で起動している。起動ログ `[seed_auth/permission] 権限サービスを起動します: http://…` を確認すること |
| 認証を有効にしたら `repository create` が `permission denied` で落ちる | `[seed_auth] repository_creators` が空（既定）なので、権限サービスが誰にも作成を許していない | 作ってよい人の**アカウント名**を `repository_creators` に足して再起動する |
| 新しく作ったリポジトリで招待が 403 `forbidden` になる | 窓口の招待・一覧・失効はトークンの `resources` を見る（契約 4 章）。作成直後の手元のトークンには、そのリポジトリがまだ載っていない | **ログインし直す**（トークンを取り直す）。取り直したトークンには owner として載っている |
| **サーバを強制終了すると、次の起動からそのリポジトリを名前で引けなくなる** | ローカル mutable ストア（リポジトリ名 → ID の対応とブランチの先端が入る）は書き込みの `flush_delay_seconds` 秒後に別タスクでファイルへ落とす（`lore-storage/src/local/mutable_store.rs` の `flush_delayed`）。落ちる前に kill すると失われる。immutable 側は残るので「データはあるのにクローンだけできない」という分かりにくい壊れ方になる | Ctrl+C で止める。直前に push した場合は数秒待つ。結合テストは `AccountsServerFixture.StopProcess` で「mutable ストアのファイル数とサイズが落ち着くまで待つ」を実装している |
| 期限切れと権限なしを区別できない | `JWTInterceptor` が理由を潰して `PermissionDenied` にする（`lore-server/src/auth/jwt_interceptor.rs:26`） | エディタが `exp` を見て先回りで更新する |
| `lore lock query --path <path>` が「unsupported lock query combination」 | `Hash + Repository` の組合せ。upstream の `LocalLockStore` も `seed_file_lock_store` も未対応 | `lore lock status <path>` か `lore lock query --branch <name>` を使う |
| JWT の期限検証に 60 秒の猶予がある | jsonwebtoken の `Validation::leeway` 既定値。Lore は変更していない | 期限切れの確認をするときは 60 秒より大きく過去へずらす |

> `lore` CLI は相対パスを **プロセスのカレントディレクトリ**基準で解決する。
> `--repository` はリポジトリの場所を指すだけなので、ファイルを指す操作（`stage` / `lock`）は
> 作業コピーの中で実行するか、絶対パスを渡すこと。

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

## 発行窓口の動作確認済みのこと（2026-09-18、Windows 11 / rustc 1.98.0 / lore CLI 0.9.0+783 / .NET 9.0.318）

ポートは Lore 41347 / 41349、窓口 41351（すべて 127.0.0.1）。利用者の鍵と署名は .NET の `ECDsa` で生成した。

| 確認項目 | 結果 |
| --- | --- |
| `[seed_auth]` だけで起動（Lore は匿名） | `GET /v1/health` が `{"status":"ok","issuer":"seed-auth","audience":"seed-lore","api":1}` |
| `POST /v1/bootstrap`（ループバック） | `{"name":"tsubasa","role":"owner"}`。2 回目は 409 `owner_exists` |
| チャレンジ → .NET の P1363 署名 → `POST /v1/login/complete` | トークン発行。ヘッダ `{"typ":"JWT","alg":"EdDSA","kid":"…"}`、`resources=[{"resource_id":"urc-<repo>","permission":["owner"]}]` |
| .NET 既定の **DER 署名** | 401 `login_failed`（拒否） |
| 同じチャレンジの 2 回目 | 401（1 回限り） |
| 未登録の名前でのチャレンジ | 通常どおり `challenge_id` を返す（存在を漏らさない） |
| `POST /v1/invites` トークンなし／member のトークン | 401 `unauthorized` ／ 403 `forbidden` |
| `POST /v1/join` → 同じコードで 2 回目 | 成功 → 403 `invite_invalid` |
| Lore 認証あり・トークンなしの `repository create` | 拒否（`Not authenticated`） |
| `--identity-token` ＋ `--access-token` で `repository create` → `stage` → `commit` → `push` | すべて成功。`Pushed revision 1 -> … to branch main` |
| `lock acquire` → `lock status` | `hello.txt by tsubasa on …`。`seed_locks.json` の `owner` も `"tsubasa"` |
| 別の参加者のトークンで `lock release` | 拒否（`Failed to lock-release 1 batch(es) out of 1`） |
| owner のトークンで `lock release` | 成功。`"locks": []` になる |
| トークンに載っていないリポジトリへの `push` / `lock acquire` | どちらも拒否（`Not authorized to access repository`） |
| 期限切れトークン（−600 秒） | 拒否。同じ作りで +600 秒なら通る |
| `POST /v1/members/revoke` → 再ログイン | 401（失効した参加者は新しいトークンを取れない）。再招待すれば復活する |
| push フックのログ | `user="tsubasa"`、gRPC スパンも `user_id="tsubasa"` |
| `accounts.json` / `jwks.json` | 招待コードの平文が残っていないこと、JWKS に `alg` と `kid` が入っていることを確認 |

## 権限サービスの動作確認済みのこと（2026-09-18、`editor/tests/AccountsTests` の結合テストで自動確認）

ポートは Lore 41357 / 41359、窓口 41361、**権限サービス 41362**（すべて 127.0.0.1）。
`[environment.endpoint] auth_url = "http://127.0.0.1:41362"` を**付けっぱなし**の 1 設定で通した。

| 確認項目 | 結果 |
| --- | --- |
| `auth_url` を書いたままのクローン（参加） | **通る**（以前は必ず `Not found`） |
| `auth_url` を書いたままの送信 → 取得 | **通る**（以前は操作ごとに再起動が必要だった） |
| `auth_url` を書いたままのロック取得・照会・解放 | 通る |
| `repository_creators` に載っている人の `repository create` | **通る**。`bootstrap` 無しでその人が owner になり、取り直したトークンに載る |
| `repository_creators` に載っていない人の `repository create` | 拒否（`rc=7: Not authorized to access repository`。サーバログに `Create resource in auth failed - permission denied`） |
| 参加していないリポジトリのクローン | 拒否（`Not found`。存在を漏らさない） |
| 失効した参加者のクローン（トークンは期限内・取り直していない） | **拒否**（失効が即座に効く） |
| 失効した参加者の送信（**既に接続済み**・トークンは期限内） | **通ってしまう**（メタデータを手元に持っているので `RepositoryGet` を呼ばない。既知の制約） |
| サーバのログにトークンが出ていないこと | 確認済み（`eyJ` で始まる文字列は 0 件） |

`aws` / `consul` プラグインが `register_all_plugins()` で登録されている点は、config リファレンス（`docs/reference/lore-server-config.md` の「Reference plugins」節）の「neither is compiled into `loreserver`」という記述と食い違う。v0.9.0 のソースでは `lore-server/src/plugins/{aws,hashicorp}.rs` が存在し `register_all_plugins()` から呼ばれている。ドキュメントが古い可能性が高いので、`seed_` 接頭辞の名前規約は崩さないこと（衝突すると `register_*_plugin()` が panic する）。

## テスト

```bash
cargo test
```

`src/lock_store_file.rs` の単体テストで、永続化・再読み込み・所有者検証・all-or-nothing・壊れたファイルの扱いを確認している。`src/hooks/push_guard.rs` の単体テストで `reject_all` の挙動を確認している。

`src/auth/` の単体テストでは次を確認している（各ファイルの `mod tests`）。

| ファイル | 確認していること |
| --- | --- |
| `crypto.rs` | base64url の往復・パディング付き入力の受理・SHA-256 の既知ベクタ・定数時間比較 |
| `user_key.rs` | **.NET の `ECDsa` が実際に出力した公開鍵と署名**（P1363）を検証できること、同じ鍵の **DER 署名は拒否**されること、メッセージが 1 文字違えば失敗すること、公開鍵の形式検証、壊れた入力で panic しないこと |
| `model.rs` | 名前の規則（日本語可・記号と空白の拒否・32 文字）、一意判定が ASCII 大文字小文字を畳むこと、招待の期限と使い捨て、役割と状態の文字列 |
| `challenge.rs` | 1 回限り・60 秒の期限（境界の両側）・件数上限と期限切れの掃除・未登録の名前でも発行されること |
| `issuer.rs` | 鍵の生成と読み直しで `kid` が変わらないこと、JWKS に `alg` と `kid` が必ず入ること、**EdDSA と ES256 の両方が jsonwebtoken の `Jwk` として読めること**、壊れた鍵ファイルがエラーになること |
| `token.rs` | 契約 4 章のクレームが揃い `is_service_account` / `migrate` / `urc-*` が入らないこと、ヘッダの `kid` と `alg`、失効した権限が `resources` に載らないこと、期限切れ・issuer 違い・audience 違い・別鍵の署名が拒否されること |
| `store.rs` | bootstrap がオーナー 1 人だけを作ること、owner 以外が招待を作れないこと、招待コードの平文がファイルに残らないこと、招待の使い捨てと期限、名前衝突で招待が消費されないこと、同じ公開鍵なら権限だけ足すこと、失効と再招待、`accounts.json` の往復 |
| `config.rs` | 引数の 2 形式、既定値、`data_dir` 必須、値の範囲、他の節が混ざった実ファイルから `[seed_auth]` だけ読めること |
| `http.rs` | 長さ検証が文字数であること、役割の文字列変換、`StoreError` → HTTP ステータスの写像（**`CannotRevokeOwner` が 409 `owner_exists` になること**を含む）、ログイン失敗が区別できないこと |
| `atomic_file.rs` | 親ディレクトリの作成・上書き・一時ファイルが残らないこと・書き込み失敗の検出 |
| `permission/decision.rs` | トークンから名前が取れること、壊れた／方式違いの `authorization` がすべて同じ理由で拒否されること、参加していないリポジトリが拒否されること、**失効が即座に効くこと**、`urc-*` などの不正な `resource_id` が通らないこと、`repository_creators` の効き方（既定は誰も作れない・載っている人は owner になる・別人のリポジトリは奪えない・同じ人の作り直しは通る）、削除は owner だけ |

サーバを実際に起動した状態での確認は、**エディタ側の結合テスト**が自動で行う（下記）。
`lore` CLI での手作業の手順は `config/local.example.toml` のコメントを参照。

```powershell
# 実サーバ × エディタの実クラス（Lore 41357 / 41359、窓口 41361、権限サービス 41362）
$env:SEED_ACCOUNTS_TEST_SERVER = "1"
dotnet run --project editor/tests/AccountsTests
```
