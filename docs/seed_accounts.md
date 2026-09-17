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

## 3. 発行窓口（HTTP API）

`seed-loreserver` と同じプロセスの別スレッド・別ランタイムで動く小さな HTTP サーバ。既定ポート **41350**。
JSON（UTF-8）。エラーは HTTP ステータス ＋ `{"error":"<コード>","message":"<日本語の説明>"}`。

| メソッド／パス | 認証 | 入力 | 出力 |
|---|---|---|---|
| `GET /v1/health` | なし | — | `{"status":"ok","issuer":"…","audience":"…","api":1}` |
| `POST /v1/bootstrap` | **ループバック接続のみ** | `{"name","public_key","repository_id","project_name"}` | `{"name","role":"owner"}`。そのリポジトリにオーナーがまだ居ないときだけ成功（居れば 409 `owner_exists`） |
| `POST /v1/login/challenge` | なし | `{"name"}` | `{"challenge_id","nonce","expires_in":60}`（未登録・失効でも同じ形を返し、存在を漏らさない） |
| `POST /v1/login/complete` | なし | `{"challenge_id","signature"}` | `{"access_token","expires_at":<Unix ミリ秒>,"name","grants":[{"repository_id","project_name","role"}]}`。失敗は 401 `login_failed` |
| `POST /v1/invites` | Bearer（そのリポジトリの owner） | `{"repository_id","role":"member","expires_in_hours":72}` | `{"invite_code","expires_at"}`（コードは 1 回だけ返す。サーバにはハッシュで保存） |
| `POST /v1/join` | なし（招待コードが資格） | `{"invite_code","name","public_key"}` | `{"name","repository_id","project_name","role"}`。同じ公開鍵の既存アカウントなら権限を足すだけ。名前の衝突は 409 `name_taken`、コード不正・期限切れ・使用済みは 403 `invite_invalid` |
| `GET /v1/members?repository_id=…` | Bearer（owner） | — | `{"members":[{"name","role","status","added_at"}]}` |
| `POST /v1/members/revoke` | Bearer（owner） | `{"repository_id","name"}` | `{"name","status":"revoked"}`（**owner ロールの権限は失効できない。自分自身を含む** → 409） |
| `GET /jwks.json` | なし | — | サーバの公開鍵（JWKS）。`{"keys":[…]}` 形式。Lore 本体は同じファイルを `file://` で読む |

- Bearer は `Authorization: Bearer <access_token>`（この窓口が発行した JWT。署名・期限・`resources` を検証）。
  方式名は `Bearer `（大文字小文字を区別する）。
- チャレンジは 1 回限り・60 秒。ログイン失敗の理由は区別して返さない。
- 失効した参加者は新しいトークンを取れない。発行済みトークンは期限（既定 8 時間）まで有効（Lore に失効の仕組みが無いため）。
  一方、**窓口側の操作（招待発行・一覧・失効）は毎回 `accounts.json` を見るので失効が即座に効く**。
- 失効（`status`）は**アカウント単位ではなくリポジトリ権限（grant）単位**。
  `/v1/login/complete` は「有効な権限が 1 件も無い」アカウントを 401 `login_failed` で断る。
- 時刻の単位: チャレンジの `expires_in` は**秒**、`expires_at`（`/v1/invites` と `/v1/login/complete`）は **Unix ミリ秒**。
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
port = 41350
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

# push / pull（QUIC ストレージセッション）を通すのに必須。理由は下の「有効化の順番」。
[environment.endpoint]
auth_url = "https://seed-auth.invalid"
```

- `[seed_auth]` だけ有効にして `[server.auth]` を書かなければ、窓口は動くが Lore は匿名のまま（移行期間の確認用）。
- `accounts.json`（参加者・権限・招待コードのハッシュ）は tmp → rename で原子的に書く。
- `data_dir` には `accounts.json` / `issuer_key.json` / `jwks.json` が置かれる。
  **`issuer_key.json` にはサーバの秘密鍵が入る**ので、共有フォルダやバージョン管理に入れないこと。
- `token_ttl_hours` は 1〜24。`enabled = true` のとき `data_dir` は省略できない。
- 設定の読み込み規則は Lore 本体と同じ（`--config` / `LORE_CONFIG_PATH`、`--env` / `LORE_ENV`、
  `default.toml` → `<env>.toml` → `local.toml` → `LORE__*`）。未知の節は Lore 本体の読み込みを壊さない（実機で確認済み）。

### 有効化の順番（重要）

1. `[seed_auth]` だけで起動する（`jwks.json` ができる）。
2. リポジトリを作る（`.lore/id` が無いと `bootstrap` に渡す `repository_id` が決まらない）。
3. `POST /v1/bootstrap` でオーナーを登録し、招待コードで参加者を入れる。
4. `[server.auth]` / `[server.auth.jwk]` / `[environment.endpoint]` を足して再起動する。

**`auth_url` を設定したあとは、そのサーバで新しいリポジトリを作れなくなる**
（Lore v0.9.0 は `auth_url` があると `repository create` を外部 ReBAC サービスへ委譲しようとして失敗する。
既存リポジトリの再作成は早期 return で通る）。新規プロジェクトを足すときだけ
`[environment.endpoint]` を一時的に外して起動すること。

## 6. エディタ側

- **アカウント**（Hub）: 作成（名前 → 鍵ペア生成）、表示、書き出し／読み込み。
- **プロジェクトを守る**（オーナー）: 開いているプロジェクトのリポジトリ ID（`.lore/id`）で `bootstrap` → 以後「招待コードを発行」「参加者の一覧と失効」。
- **プロジェクトに参加**（Hub）: サーバのアドレス・招待コード・保存先 → `join` → ログイン → トークン付きでクローン → 開く。
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

- 平文 HTTP。LAN 内の信頼が前提（チャレンジ署名は再利用できないが、発行済みトークンは盗聴され得る）。LAN の外へ出すなら TLS を前段に置く。
- Lore v0.9.0 では、有効なトークンがあれば誰でもリポジトリの作成と一覧ができる（中身は参加者だけ）。
- 期限切れと権限なしを Lore の応答から区別できない（どちらも「Not authorized」）。エディタは `exp` を見て先回りで更新する。
- `auth_url` を設定している間は新規リポジトリを作れない（上記「有効化の順番」）。
- owner の権限は失効できない（自分自身を含む）。オーナーの交代・追加は未対応。
- `lore lock query --path <path>` は Lore 側が未対応の組合せ（`Hash + Repository`）。
  パスで引くときは `lore lock status <path>`、ブランチ単位なら `lore lock query --branch <name>` を使う。
- `lore` CLI は相対パスを**プロセスのカレントディレクトリ**基準で解決する（`--repository` は基準にならない）。
