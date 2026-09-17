# SEED アカウント（プロジェクト参加者の identity）

Hub（スタート画面）でアカウントを作ってログインし、オーナーが出す招待コードでプロジェクトに参加する。
参加者の名前は、バージョン管理（Lore）のロック所有者や送信者として表示される。
設計の背景と Lore 側の制約は `docs/vcs_lore.md` 4.1 節。ここは**サーバ（`tools/seed-loreserver`）とエディタの取り決め**の正典。

## 1. 考え方

- 中央のアカウントサーバは持たない。**プロジェクトのサーバ（オーナーの PC で動く `seed-loreserver`）が参加者を管理する。**
- アカウント = **利用者ごとの鍵ペア ＋ 名前**。パスワードは無い。秘密鍵はその PC から出さない。
- 参加 = オーナーが発行した**使い捨ての招待コード**で、自分の公開鍵をプロジェクトへ登録すること。オーナーは個別に失効できる。
- ログイン = サーバのチャレンジに秘密鍵で署名 → サーバが**自分の鍵で署名した短寿命のトークン（JWT）**を返す。
  利用者の鍵で直接 JWT を作らせない（参加者同士のなりすましを防ぐ）。
- エディタはトークンを Lore の呼び出しごとに渡す（`LoreGlobalArgs.AccessToken`）。期限はエディタが見て先回りで取り直す。

## 2. 鍵と署名の形式

| 項目 | 形式 |
|---|---|
| 利用者の鍵 | **ECDSA P-256**（.NET 標準の `ECDsa` で依存なしに扱えるため） |
| 公開鍵の表現 | SEC1 非圧縮点（65 バイト: `0x04 ‖ X ‖ Y`）の **base64url（パディングなし）** |
| 署名 | SHA-256、**IEEE P1363 固定長（r ‖ s の 64 バイト）**の base64url |
| 署名する文字列 | UTF-8 の `seed-auth-login:v1:<challenge_id>:<nonce>`（`nonce` はサーバが返した base64url 文字列そのまま） |
| 秘密鍵の保管（エディタ） | `%APPDATA%\SEED\account\account.json`。PKCS#8 を DPAPI（CurrentUser）で暗号化して保存。別 PC へは「書き出し／読み込み」で移す |
| サーバの署名鍵 | サーバが初回起動時に生成（EdDSA か ES256。`jwks.json` に `alg` と `kid` を必ず書く）。秘密側はサーバのデータフォルダから出さない |
| 名前 | 1〜32 文字（**Unicode のコードポイント数**で数える）。使える文字は、ASCII の英数字と `_` `-` `.`、および日本語の次の範囲だけ: U+3005–U+3007（々〆〇）、U+3041–U+309F（ひらがな）、U+30A0–U+30FF（カタカナ。長音符 ー を含む）、U+3400–U+4DBF と U+4E00–U+9FFF（漢字）。**全角英数字・半角カナ・他言語の文字・空白・記号は不可**（ロックの所有者表示で他人に似た名前を作らせないため。サーバ `model.rs` とエディタ `AccountNameRule.cs` は同じ表を持つ）。**サーバ内で一意・作成後は変更不可**（JWT の `sub` に使う）。一意判定は **ASCII の大文字小文字を無視**する（`alice` と `Alice` は同じ人として扱い、後から来たほうを `name_taken` で断る。表示は入力どおり） |
| リポジトリ ID | `.lore/id` は**生の 16 バイト**。API で渡す `repository_id` はそれを **32 桁の 16 進小文字**にしたもの |

## 3. 発行窓口（HTTP API）と権限サービス（gRPC）

`seed-loreserver` は同じプロセスの中で、**別スレッド・別ランタイム**の小さなサーバを 2 つ動かす。

| 窓口 | 種別 | 既定ポート | 相手 | 役目 |
|---|---|---|---|---|
| 発行窓口 | HTTP/JSON | **41350** | エディタ（利用者） | 参加者の登録・招待・チャレンジ応答ログイン・JWT 発行 |
| 権限サービス | gRPC（平文 h2c） | **41352** | **Lore 本体（同じプロセス内のサーバ）** | 「この人はこのリポジトリを触ってよいか」に答える |

権限サービスは**常に 127.0.0.1 で待ち受ける**（`[seed_auth] host` を `0.0.0.0` にしても外からは見えない。繋ぐのは同じプロセスの Lore 本体だけなので、LAN へ晒さない）。
権限サービスは Lore の `[environment.endpoint] auth_url` が指す先。詳しくは 5 章の
「`auth_url` の二律背反（解消済み）」。以下の表は発行窓口の API。
JSON（UTF-8）。エラーは HTTP ステータス ＋ `{"error":"<コード>","message":"<日本語の説明>"}`。

| メソッド／パス | 認証 | 入力 | 出力 |
|---|---|---|---|
| `GET /v1/health` | なし | — | `{"status":"ok","issuer":"…","audience":"…","api":1}` |
| `POST /v1/bootstrap` | **ループバック接続のみ** | `{"name","public_key","repository_id","project_name"}` | `{"name","role":"owner"}`。そのリポジトリにオーナーがまだ居ないときだけ成功（居れば 409 `owner_exists`） |
| `POST /v1/login/challenge` | なし | `{"name"}` | `{"challenge_id","nonce","expires_in":60}`（未登録・失効でも同じ形を返し、存在を漏らさない） |
| `POST /v1/login/complete` | なし | `{"challenge_id","signature"}` | `{"access_token","expires_at":<Unix ミリ秒>,"name","grants":[{"repository_id","project_name","role"}]}`。失敗は 401 `login_failed` |
| `POST /v1/invites` | Bearer（そのリポジトリの owner） | `{"repository_id","role":"member","expires_in_hours":72}` | `{"invite_code","expires_at"}`（コードは 1 回だけ返す。サーバにはハッシュで保存） |
| `POST /v1/join` | なし（招待コードが資格） | `{"invite_code","name","public_key"}` | `{"name","repository_id","project_name","role"}`。同じ公開鍵の既存アカウントなら権限を足すだけ。名前の衝突は 409 `name_taken`、コード不正・期限切れ・使用済みは 403 `invite_invalid` |
| `GET /v1/members?repository_id=…` | Bearer（owner） | — | `{"members":[{"name","role","status","added_at"}]}`。**`added_at` は Unix ミリ秒の数値**（文字列ではない） |
| `POST /v1/members/revoke` | Bearer（owner） | `{"repository_id","name"}` | `{"name","status":"revoked"}`（**owner ロールの権限は失効できない。自分自身を含む** → 409） |
| `GET /jwks.json` | なし | — | サーバの公開鍵（JWKS）。`{"keys":[…]}` 形式。Lore 本体は同じファイルを `file://` で読む |

- Bearer は `Authorization: Bearer <access_token>`（この窓口が発行した JWT。署名・期限・`resources` を検証）。
  方式名は `Bearer `（大文字小文字を区別する）。
- チャレンジは 1 回限り・60 秒。ログイン失敗の理由は区別して返さない。
- 失効した参加者は新しいトークンを取れない。
  **窓口側の操作（招待発行・一覧・失効）と、権限サービスを通る操作（クローン・リポジトリの作成／削除・
  メタデータ未取得の送信）は毎回 `accounts.json` を見るので、失効が即座に効く。**
  残るのは「既に接続していてメタデータを持っているクライアントの送信」「ロックの取得・照会・解放」「最新を取得」で、
  これらは Lore 側が**トークンに載っている `resources`** だけを見るため、そのトークンの期限（既定 8 時間）までは通る。
  実測の詳細は 7 章「既知の制約」。
- 失効（`status`）は**アカウント単位ではなくリポジトリ権限（grant）単位**。
  `/v1/login/complete` は「有効な権限が 1 件も無い」アカウントを 401 `login_failed` で断る。
- 時刻の単位: チャレンジの `expires_in` は**秒**、`expires_at`（`/v1/invites` と `/v1/login/complete`）と
  `added_at`（`/v1/members`）は **Unix ミリ秒の数値**。
  **時刻を JSON の文字列で返すものは 1 つも無い**（エディタ側を文字列で受けると一覧ごと読めなくなる）。
- `owner_exists`（409）は「既にオーナーが居る」（bootstrap）と「owner の権限を失効させようとした」（revoke）の両方で返る。呼び出した操作で言い分けること。
  `expires_in_hours` は 1〜720（30 日）の範囲。省略時は 72。`role` の省略時は `member`。
- 時刻・件数以外の入力にも上限がある: 本体 16 KiB、名前 32 文字、公開鍵 128 文字、署名 128 文字、
  招待コード 128 文字、`challenge_id` 64 文字、`project_name` 128 文字、アクセストークン 8192 文字。

エラーコードの一覧（`{"error":"…","message":"…"}` の `error`）。

| コード | HTTP | いつ |
| --- | --- | --- |
| `invalid_request` | 400 | JSON が壊れている、名前・公開鍵・リポジトリ ID の形式が不正、長さ超過 |
| `login_failed` | 401 | ログイン失敗（理由は一切区別しない） |
| `unauthorized` | 401 | Bearer が無い／不正／期限切れ |
| `forbidden` | 403 | owner でない |
| `invite_invalid` | 403 | 招待コードが不正・期限切れ・使用済み |
| `loopback_only` | 403 | `/v1/bootstrap` をループバック以外から呼んだ |
| `not_found` | 404 | 失効させようとした参加者が居ない |
| `name_taken` | 409 | その名前は別の公開鍵で使われている |
| `owner_exists` | 409 | そのリポジトリには既にオーナーが居る／owner の権限を失効させようとした |
| `too_many_requests` | 429 | 発行中のチャレンジが上限（1024 件）に達している |
| `internal` | 500 | サーバ内部の失敗（詳細はサーバのログにだけ残る） |

## 4. トークン（JWT）の中身（Lore v0.9.0 が要求する形）

```json
{
  "sub": "<名前>", "name": "<名前>", "preferred_username": "<名前>",
  "iss": "<設定の issuer>", "aud": ["<設定の audience>"],
  "iat": 0, "exp": 0, "env": "seed", "idp": "seed-auth",
  "resources": [ { "resource_id": "urc-<repository_id>", "permission": ["owner"] } ]
}
```

- 参加しているリポジトリごとに `resources` を 1 件（**失効した権限は載せない**）。
  `permission` は owner が `["owner"]`、member が `["member"]`
  （Lore はエントリの有無でアクセスを判定し、`owner` は他人のロックの解放に効く）。
- **`is_service_account` と `migrate` は入れない。** ワイルドカード `urc-*` も使わない。
- ヘッダに `kid` 必須。トークンの組み立ては 1 か所に閉じ込める（Lore v0.10 でクレームの構造が変わる見込み）。
- 署名アルゴリズムは **EdDSA（Ed25519）**。Lore v0.9.0 は JWK の `alg` からアルゴリズムを決めるので
  EdDSA と ES256 のどちらでも受理される（両方を実機で確認済み）。EdDSA を選んだのは鍵が短く署名が決定的なため。
- **Lore 側の期限検証には 60 秒の猶予がある**（jsonwebtoken の `Validation::leeway` 既定値）。
  エディタの先回り更新はこれより十分早く行うこと。

## 5. サーバの設定とデータ

```toml
# seed-loreserver の local.toml
[seed_auth]
enabled = true
host = "127.0.0.1"          # チームが繋ぐ段階で 0.0.0.0
port = 41350                # 発行窓口（HTTP）
permission_port = 41352     # 権限サービス（gRPC）。auth_url がここを指す
repository_creators = ["つばさ"]   # 新しいリポジトリを作ってよい人。既定は空 = 誰も作れない
data_dir = "C:/Users/<user>/SEED_lore/server/auth"   # accounts.json / issuer_key.json / jwks.json
issuer = "seed-auth"
audience = "seed-lore"
token_ttl_hours = 8

# Lore 本体の認証（有効にすると匿名アクセスは一切できなくなる）
[server.auth]
jwt_issuer = "seed-auth"
jwt_audience = ["seed-lore"]
[server.auth.jwk]
endpoint = "file:///C:/Users/<user>/SEED_lore/server/auth/jwks.json"

# 2 つの意味を持つ。どちらも必須。
#   クライアント側: push / pull（QUIC ストレージセッション）にトークンを載せる合図
#   サーバ側      : リポジトリの可否を問い合わせる先（= 上の permission_port）
# ホストは [seed_auth] host と同じものを書く（0.0.0.0 なら 127.0.0.1 で自分を指す）。
[environment.endpoint]
auth_url = "http://127.0.0.1:41352"
```

- `[seed_auth]` だけ有効にして `[server.auth]` を書かなければ、窓口と権限サービスは動くが Lore は匿名のまま（移行期間の確認用）。
- `accounts.json`（参加者・権限・招待コードのハッシュ）は tmp → rename で原子的に書く。
- `data_dir` には `accounts.json` / `issuer_key.json` / `jwks.json` が置かれる。
  **`issuer_key.json` にはサーバの秘密鍵が入る**ので、共有フォルダやバージョン管理に入れないこと。
- `token_ttl_hours` は 1〜24。`enabled = true` のとき `data_dir` は省略できない。
- `permission_port` は `port` と違う値でなければならない（同じなら起動時に落とす）。
- `repository_creators` の照合は名前の一意判定と同じく **ASCII の大文字小文字を無視**する。
  規則に反する名前を書くと起動時に落とす（黙って無視すると「なぜか作れない」になるため）。
- 設定の読み込み規則は Lore 本体と同じ（`--config` / `LORE_CONFIG_PATH`、`--env` / `LORE_ENV`、
  `default.toml` → `<env>.toml` → `local.toml` → `LORE__*`）。未知の節は Lore 本体の読み込みを壊さない（実機で確認済み）。

### 有効化の順番

1. `[seed_auth]` だけで起動する（`jwks.json` ができる）。
2. リポジトリを作る（`.lore/id` が無いと `bootstrap` に渡す `repository_id` が決まらない）。
3. `POST /v1/bootstrap` でオーナーを登録し、招待コードで参加者を入れる。
4. `[server.auth]` / `[server.auth.jwk]` / `[environment.endpoint]` を足して再起動する。

**再起動はこの 1 回だけ。** 以後は設定を触らずにクローン・送信・取得・ロック・
新しいリポジトリの作成がすべて通る（次節）。

2 つめ以降のリポジトリでは 2〜3 が要らない。`repository_creators` に載っている人が
`repository create` すると、権限サービスがその人を owner として台帳へ登録する。

**サーバを止める前に、書き込みがディスクへ落ちるのを待つこと。**
Lore のローカル mutable ストアは書き込みの `flush_delay_seconds` 秒後に別タスクで
ファイルへ落とす。mutable ストアには**リポジトリ名 → ID の対応とブランチの先端**が入るので、
落ちる前に強制終了すると、次の起動で**そのリポジトリを名前で引けなくなる**
（`RepositoryGet` が NOT_FOUND を返し、clone が「Not found」で失敗する）。
immutable 側は残るので「データはあるのにクローンだけできない」という分かりにくい壊れ方になる。
止めるときは Ctrl+C（graceful shutdown）を使い、直前に push した場合は数秒待つこと。
**実測で確認済み**（結合テスト `editor/tests/AccountsTests/AccountsServerFixture.cs` は
止める前に mutable ストアのファイル数とサイズが落ち着くのを待っている）。

### `auth_url` の二律背反（**解消済み**）

`[environment.endpoint] auth_url` は **1 つの値で 2 つの意味を持つ**。

| | クライアント側 | サーバ側 |
|---|---|---|
| `auth_url` **あり** | QUIC のストレージセッションにトークンを載せる（`session_start`）。**pull に必須** | `RepositoryGet` などの認可を `auth_url` の gRPC へ委譲する |
| `auth_url` **なし** | 「認証していないサーバ」とみなしてトークンを載せない → **pull が「Not authorized to access repository」で失敗** | 認可チェックを飛ばす（AllowAll） |

以前は SEED に「委譲される側」が無かったため、`auth_url` を書くとサーバ側の問い合わせが
必ず失敗し、失敗が `RepositoryNotFound` に畳まれて **clone と `repository create` が
「Not found」で落ちていた**。逆に外すと pull が落ちる ── どちらの設定でも全部は通らなかった。

**`seed-loreserver` が委譲される側を自前で実装したので、この二律背反は解消した。**
`auth_url` を自分の権限サービス（既定 `http://127.0.0.1:41352`）へ向ければ、
**1 つの設定のまま全部の操作が通る**（結合テスト `editor/tests/AccountsTests` で実測）。

| 操作 | 結果 | サーバが権限サービスへ問い合わせるか |
|---|---|---|
| clone（参加） | **通る** | する（`CheckUserPermission`。参加していなければ `Not found`） |
| `repository create` | **通る**（`repository_creators` に載っている人だけ） | する（`CreateResource`） |
| push（送信） | **通る** | メタデータを手元に持っていなければ `CheckUserPermission` |
| pull / sync（取得） | **通る** | しない（トークンの `resources` を見る） |
| ロック（取得・照会・解放） | **通る** | しない（トークンの `resources` を見る） |
| `repository list` | 参加しているものだけ出る | する（`LookupUserPermissions`） |
| 発行窓口（`[seed_auth]`） | 影響なし | ─ |

#### 権限サービスが話す gRPC（Lore v0.9.0 が呼ぶもの）

| RPC | いつ呼ばれるか | 引数 | SEED の答え方 |
|---|---|---|---|
| `epic_urc.UrcAuthApi/CheckUserPermission` | `RepositoryGet` / `RepositoryQuery` / `RepositoryMetadataGet` / `RepositoryMetadataSet` | `resource_id = ["urc-<32桁hex>"]`、`target_user` なし、`authorization` メタデータに `Bearer <JWT>` | 台帳にその人の**有効な**権限があれば `allowed_resource_permission` に 1 件返す。無ければ `PERMISSION_DENIED` |
| `epic_urc.UrcAuthApi/LookupUserPermissions` | `repository list` | `resource_filter = "urc"` | その人の有効な権限をすべて `urc-<id>` で返す |
| `ucs.auth.RebacApi/CreateResource` | `RepositoryCreate` | `resource_id`、`resource_name`（リポジトリ名） | `repository_creators` に載っていれば許可し、**その人を owner として台帳へ登録**。載っていなければ `PERMISSION_DENIED` |
| `ucs.auth.RebacApi/DeleteResource` | `RepositoryDelete` | `resource_id` | そのリポジトリの owner だけ許可（台帳は書き換えない） |

- **判定の根拠は台帳（`accounts.json`）で、トークンの `resources` ではない。**
  トークンから使うのは「誰か」（`sub`）だけ。だから**この表の操作については、
  失効がトークンの期限を待たずに効く**（問い合わせが飛ばない操作は効かない。7 章）。
- 接続は **平文 h2c**。`lore-server/src/authnz/auth.rs` と `rebac.rs` は
  `auth_url.starts_with("https://")` のときだけ TLS を設定するので、`http://` なら証明書は要らない
  （OS の証明書ストアに何も登録しなくてよい）。
- **クライアントはこの URL へ接続しない。** エディタと CLI はトークンを供給しているので
  トークン交換を短絡する（`lore-transport/src/auth/exchange.rs` の `exchange()` 冒頭、
  `lore-credential/src/token_store.rs:606`）。`127.0.0.1` を書いても他の PC の参加者は困らない。
- 利用者に見える文言（実測）:
  リポジトリを引く操作の拒否は **`Not found`**（Lore が `RepositoryNotFound` へ畳むので
  **リポジトリの存在は漏れない**）。`repository create` の拒否は
  **`Not authorized to access repository`（rc 7）**。
  どちらも理由の詳細はサーバのログにだけ残る。

根拠（Lore v0.9.0 のソース）:
`lore-server/src/grpc/repository/v1/repository_get.rs` の `repository_load_name` / `repository_load_id` が
`if let Some(auth_url)` のときだけ `check_repository_query_authorization` を呼び、
`lore-server/src/authnz/repository_authorizer.rs` がその URL へ gRPC 接続する。
`lore-server/src/grpc/repository/v1/repository_create.rs:293` が **同じ URL** の
`ucs.auth.RebacApi/CreateResource` を呼ぶ。
クライアント側は `lore-transport/src/connection.rs` の `if !auth_url.is_empty()` で交換の有無を決める。
**`RepositoryService` は JWT の `resources` を一切見ない**（`JWTAuthnInterceptor` は署名とクレームだけ検証）ので、
可否はすべて権限サービスの答えで決まる。

## 6. エディタ側

- **アカウント**（Hub）: 作成（名前 → 鍵ペア生成）、表示、書き出し／読み込み。
- **プロジェクトを守る**（オーナー）: 開いているプロジェクトのリポジトリ ID（`.lore/id`）で `bootstrap` → 以後「招待コードを発行」「参加者の一覧と失効」。
- **プロジェクトに参加**（Hub）: サーバのアドレス・招待コード・保存先 → `join` → ログイン → トークン付きでクローン → 開く。
  アドレスに `host:port` と書いた場合、そのポートは **発行窓口のもの**として扱う。
  Lore 本体のポートは別の値（既定 41337）なので、既定以外で動かしているサーバに参加するには
  `JoinProjectRequest` の `LorePort` を明示する必要がある（画面からはまだ指定できない）。
- **ログイン**: プロジェクトを開いたとき、窓口（既定はリモートと同じホストの 41350。利用者設定で上書き可）が応答すれば
  チャレンジ応答でトークンを取り、期限の手前で自動更新する。秘密鍵が手元にあるので操作は要らない。
- **Lore への受け渡し**: `LoreNativeBackend` の共通引数で、**`IdentityToken` と `AccessToken` の両方に同じ JWT** を入れ、
  `Identity` は空にする（トークンの `sub` が identity として採用される。`Identity` も渡すと「トークンが identity を名乗っている」
  という排他エラーで弾かれる）。CLI なら `--identity-token <JWT> --access-token <JWT>`。
  **`AccessToken` だけでは足りない**: Lore v0.9.0 の `auth_exchange_for_identity` は
  リポジトリ ID が確定していない呼び出し（`repository create` / `repository list` / `clone`）で
  authorization token を空にするため、`AccessToken` が Authorization ヘッダに載らない。
  そちらの経路は `IdentityToken` 由来の authentication token を使う。
- ロックの保持者が実名になるので、パネルの「自分／他の人」が正しく出る。**保存ゲート（ロックの強制）は次の段階。**

## 7. 既知の制約

- 平文 HTTP／平文 h2c。LAN 内の信頼が前提（チャレンジ署名は再利用できないが、発行済みトークンは盗聴され得る）。LAN の外へ出すなら TLS を前段に置く。
- **失効が即座に効く範囲は「サーバへ問い合わせが飛ぶ操作」まで**（実測）。
  効くもの: クローン、リポジトリの作成・削除、`repository list`、メタデータを手元に持っていない送信、
  および発行窓口の操作（招待・一覧・失効・ログイン）。
  効かないもの: **既に接続していてメタデータを持っているクライアントの送信**、ロックの取得・照会・解放、「最新を取得」。
  これらは Lore 側がトークンの `resources` だけを見るため、**そのトークンの期限（`token_ttl_hours`）までは通る**。
  急いで締め出したいときは `token_ttl_hours` を短くするか、サーバを再起動して接続を切ること。
- 新しいリポジトリを作った直後は、**手元のトークンにそのリポジトリが載っていない**。
  招待の発行・参加者一覧・失効はトークンの `resources` を見るので、
  管理するにはログインし直す（トークンを取り直す）必要がある。
- `repository_creators` は名前の一覧なので、**同じ名前のアカウントを別サーバから持ち込めるわけではない**
  （名前はサーバ内で一意・鍵とひも付く）。
- 期限切れと権限なしを Lore の応答から区別できない（どちらも「Not authorized」か「Not found」）。エディタは `exp` を見て先回りで更新する。
- **サーバの強制終了でリポジトリ名 → ID の対応とブランチの先端が失われる**（上記「有効化の順番」）。
- owner の権限は失効できない（自分自身を含む）。オーナーの交代・追加は未対応。
- `lore lock query --path <path>` は Lore 側が未対応の組合せ（`Hash + Repository`）。
  パスで引くときは `lore lock status <path>`、ブランチ単位なら `lore lock query --branch <name>` を使う。
- `lore` CLI は相対パスを**プロセスのカレントディレクトリ**基準で解決する（`--repository` は基準にならない）。
