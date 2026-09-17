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
| 名前 | 1〜32 文字。英数字・`_`・`-`・`.` と日本語可、前後の空白なし。**サーバ内で一意・作成後は変更不可**（JWT の `sub` とロック所有者の表示に使う） |

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
| `POST /v1/members/revoke` | Bearer（owner） | `{"repository_id","name"}` | `{"name","status":"revoked"}`（オーナー自身は失効できない） |
| `GET /jwks.json` | なし | — | サーバの公開鍵（JWKS）。Lore 本体は同じファイルを `file://` で読む |

- Bearer は `Authorization: Bearer <access_token>`（この窓口が発行した JWT。署名・期限・`resources` を検証）。
- チャレンジは 1 回限り・60 秒。ログイン失敗の理由は区別して返さない。
- 失効した参加者は新しいトークンを取れない。発行済みトークンは期限（既定 8 時間）まで有効（Lore に失効の仕組みが無いため）。

## 4. トークン（JWT）の中身（Lore v0.9.0 が要求する形）

```json
{
  "sub": "<名前>", "name": "<名前>", "preferred_username": "<名前>",
  "iss": "<設定の issuer>", "aud": ["<設定の audience>"],
  "iat": 0, "exp": 0, "env": "seed", "idp": "seed-auth",
  "resources": [ { "resource_id": "urc-<repository_id>", "permission": ["owner"] } ]
}
```

- 参加しているリポジトリごとに `resources` を 1 件。`permission` は owner が `["owner"]`、member が `["member"]`
  （Lore はエントリの有無でアクセスを判定し、`owner` は他人のロックの解放に効く）。
- **`is_service_account` と `migrate` は入れない。** ワイルドカード `urc-*` も使わない。
- ヘッダに `kid` 必須。トークンの組み立ては 1 か所に閉じ込める（Lore v0.10 でクレームの構造が変わる見込み）。

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
```

- `[seed_auth]` だけ有効にして `[server.auth]` を書かなければ、窓口は動くが Lore は匿名のまま（移行期間の確認用）。
- `accounts.json`（参加者・権限・招待コードのハッシュ）は tmp → rename で原子的に書く。

## 6. エディタ側

- **アカウント**（Hub）: 作成（名前 → 鍵ペア生成）、表示、書き出し／読み込み。
- **プロジェクトを守る**（オーナー）: 開いているプロジェクトのリポジトリ ID（`.lore/id`）で `bootstrap` → 以後「招待コードを発行」「参加者の一覧と失効」。
- **プロジェクトに参加**（Hub）: サーバのアドレス・招待コード・保存先 → `join` → ログイン → トークン付きでクローン → 開く。
- **ログイン**: プロジェクトを開いたとき、窓口（既定はリモートと同じホストの 41350。利用者設定で上書き可）が応答すれば
  チャレンジ応答でトークンを取り、期限の手前で自動更新する。秘密鍵が手元にあるので操作は要らない。
- **Lore への受け渡し**: `LoreNativeBackend` の共通引数でトークンを `AccessToken` に入れ、`Identity` は空にする（トークンの `sub` が採用される）。
- ロックの保持者が実名になるので、パネルの「自分／他の人」が正しく出る。**保存ゲート（ロックの強制）は次の段階。**

## 7. 既知の制約

- 平文 HTTP。LAN 内の信頼が前提（チャレンジ署名は再利用できないが、発行済みトークンは盗聴され得る）。LAN の外へ出すなら TLS を前段に置く。
- Lore v0.9.0 では、有効なトークンがあれば誰でもリポジトリの作成と一覧ができる（中身は参加者だけ）。
- 期限切れと権限なしを Lore の応答から区別できない（どちらも「Not authorized」）。エディタは `exp` を見て先回りで更新する。
