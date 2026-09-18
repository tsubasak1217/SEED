# エディタのアカウント（SEED アカウントのエディタ側）

`editor/src/Accounts/` に置いた、SEED アカウントのエディタ側実装。
**取り決め（鍵と署名の形式・HTTP API・トークンの中身）の正典は `docs/seed_accounts.md`**、
背景と Lore の制約は `docs/vcs_lore.md` 4.1 節。ここは「エディタ側でどう組んだか」だけを書く。

---

## 1. 全体像

```
 Hub（スタート画面）              Version Control パネル
   アカウント欄 / 参加            ヘッダーの identity / オーナー向けダイアログ
        │                                  │
        ▼                                  ▼
   AccountService  … プロセスに 1 つの静的な入口（VersionControlService と同じ流儀）
        │
        ├─ AccountFileStore      … %APPDATA%\SEED\account\account.json（DPAPI・tmp → rename）
        ├─ AccountExportFile     … 書き出し／読み込み（PBKDF2 + AES-GCM）
        ├─ AccountSessionManager … ログインとトークンの自動更新（メモリのみ）
        │      └─ AuthGatewayClient … 発行窓口の HTTP API（契約 3 章）
        └─ ProjectJoinService    … join → ログイン → トークン付きクローン
                                     └─ ILoreCloner（LoreNativeCloner）

   AccountService.GetLoreCredential
        │  VersionControlService.CredentialProvider に差し込む唯一の関数
        ▼
   LoreNativeBackend … LoreCredentialResolver で AccessToken / Identity を決める
```

### ファイル一覧

| パス | 役割 |
|---|---|
| `AccountSettings.cs` | ポート・タイムアウト・反復回数・固定文字列の唯一の置き場 |
| `AccountMessages.cs` | 利用者に見せる日本語文言の唯一の置き場 |
| `AccountEditorSettings.cs` | 利用者設定（`editor/settings/accounts.json`。窓口 URL の上書き・自動ログインの可否） |
| `AccountService.cs` | プロセスに 1 つの静的な入口（読み込み・作成・書き出し・参加・資格情報の配布） |
| `AccountSessionManager.cs` | ログインとトークンの保持・自動更新・状態の配布 |
| `ProjectJoinService.cs` | 「プロジェクトに参加」の段取り（WPF 非依存） |
| `Crypto/Base64Url.cs` | base64url（パディングなし）の相互変換 |
| `Crypto/AccountNameRule.cs` | 名前の規則（純関数） |
| `Crypto/AccountKeyPair.cs` | ECDSA P-256 の生成・公開鍵 SEC1・署名（P1363） |
| `Crypto/LoginSignaturePayload.cs` | 署名対象文字列の組み立て（1 か所） |
| `Model/SeedAccount.cs` | 秘密鍵を持つアカウントと、持たない `AccountIdentity` |
| `Model/AccountAuthState.cs` | ログイン状態（購読できる 1 つの不変値） |
| `Storage/AccountPaths.cs` | 保管場所の解決（`SEED_ACCOUNT_DIR` で差し替え可） |
| `Storage/AccountRecord.cs` | `account.json` の形 |
| `Storage/ISecretProtector.cs` / `DpapiSecretProtector.cs` | DPAPI をこの 1 ファイルへ閉じ込める |
| `Storage/IAccountStore.cs` / `AccountFileStore.cs` | 保管の境界と実装 |
| `Storage/AccountExportFile.cs` | 別 PC へ移すための書き出し／読み込み |
| `Http/AuthContracts.cs` | 契約 3 章の要求・応答の形（JSON のキー名はここだけ） |
| `Http/IAuthGatewayClient.cs` / `AuthGatewayClient.cs` | 発行窓口の境界と HTTP 実装 |
| `Http/AuthEndpointResolver.cs` | 窓口 URL とクローン元 URL の組み立て（純関数） |
| （配色・寸法・部品） | `editor/src/Theme/SeedDialogTheme.cs` へ移した。エディタの他のダイアログ（`TextInputWindow` / `AudioSilenceTrimWindow`）と共用。ボタンの見た目は `docs/editor_ui_style.md` が正典 |
| `Views/AccountCreateWindow.cs` | アカウントの作成 |
| `Views/PassphraseWindow.cs` | パスフレーズ入力（書き出し／読み込み共用） |
| `Views/JoinProjectWindow.cs` | プロジェクトに参加 |
| `Views/ProjectAccountsWindow.cs` | オーナー向け（有効化・招待・参加者） |

バージョン管理側に足したもの:

| パス | 役割 |
|---|---|
| `VersionControl/Lore/Backend/LoreCredentialResolver.cs` | **AccessToken と Identity の決め方（純関数）** |
| `VersionControl/Lore/Backend/ILoreCloner.cs` / `LoreNativeCloner.cs` | クローン（作業コピーが無い状態で走る） |

テストに足したもの:

| パス | 役割 |
|---|---|
| `editor/tests/AccountsTests/AccountsServerFixture.cs` | 使い捨ての実サーバ（`[seed_auth]` のみ ↔ `[server.auth]` を切り替えられる） |
| `editor/tests/AccountsTests/ServerIntegrationTests.cs` | 実サーバ × 実クラスの筋書き（4.0 節） |

---

## 2. 設計で外せない点

### 2.1 秘密は型で守る

`SeedAccount`（秘密鍵を持つ）と `AccountIdentity`（名前と公開鍵だけ）を型として分ける。
画面・ログ・DTO へ渡すのは後者だけ。`AccountService` は `SeedAccount` を
**外へ出さない**（署名が要る操作は `SignLoginChallenge` / `JoinProjectAsync` として
サービス側に生やす）。うっかり秘密鍵ごと渡す経路をコンパイル時に作れなくする。

### 2.2 トークンはディスクへ書かない

盗まれたトークンは期限（既定 8 時間）まで有効で、失効させる手段が無い（契約 7 章）。
`AccountSessionManager` がメモリにだけ持ち、エディタを閉じれば消える。
`editor/settings/accounts.json` にも `account.json` にも書かない。

### 2.3 匿名で動くことを壊さない

窓口が無い・アカウントが無い・参加していない、のいずれでも
**例外を投げず匿名のまま進む**。`AccountService.AttachToProjectAsync` は
`GET /v1/health` を短いタイムアウト（既定 3 秒）で試し、応答しなければ
そのまま戻る。ログインはあくまで上乗せの機能で、
`VersionControlService.CredentialProvider` を差し込まなければ従来どおり動く。

### 2.4 依存の向きは Accounts → VersionControl の一方通行

バージョン管理の層が知っているのは
`VersionControlService.CredentialProvider`（`Func<LoreAccountCredential>`）だけで、
アカウントの型を一切参照しない。差し込みは `App.OnStartup` の
`AccountService.Initialize` の中で 1 回だけ行う。

### 2.5 トークンは 2 つ渡し、Identity は空にする（間違えると全操作が失敗する）

契約 6 章のとおり、**`IdentityToken` と `AccessToken` の両方に同じ JWT** を入れ、
`Identity` は空にする。

- **`AccessToken` だけでは足りない。** Lore v0.9.0 の `auth_exchange_for_identity` は、
  リポジトリ ID が確定していない呼び出し（`repository create` / `repository list` /
  **`clone`**）で authorization token を空にするため、`AccessToken` が
  Authorization ヘッダに載らず `authorization header required` で失敗する。
  その経路は `IdentityToken` 由来の authentication token を使う。
  **「プロジェクトに参加」のクローンがまさにこの経路**なので、ここを落とすと参加できない。
- **`Identity` を同時に渡すと弾かれる。** LoreVcs の XML ドキュメントにも
  「Supplying either token puts the call in external-credential mode:
  `identity` must be left empty, since it is read from the token.」とある。

この規則を `LoreNativeBackend`（LoreVcs 依存のファイル）の中だけに書くと、
LoreVcs を参照しないテストから固定できない。そこで純関数
`LoreCredentialResolver.Resolve` へ出し、`LoreNativeBackend.NewGlobalArgs` と
`LoreNativeCloner.Clone` の両方がそれを使う。

### 2.5.1 リポジトリ ID は生のバイト列（テキストではない）

`.lore/id` は **生の 16 バイト**で、そのまま文字列として読むと化ける。
API へ渡す `repository_id` は **32 桁の 16 進小文字**なので、
`VersionControlPaths.ReadRepositoryId` が必ず変換する。
長さが 16 バイトでないファイルは「読めなかった」（空文字）として扱う
── 中途半端な ID を返すと、サーバ側の権限と一致せず
「オーナーなのにオーナーとして扱われない」という分かりにくい失敗になる。

同じファイルにある `ResolveDisplayIdentity` は**別物**で、
「自分は誰か」を表示・比較するための identity を返す。
ログイン中はアカウント名（サーバがロックの所有者にその名前を記録するため）、
未ログインなら `.lore/config.toml` の identity。
ここを取り違えると、**自分で取ったロックが「他の人」に見える**。

### 2.5.2 参加時の 2 つのポートは別物

`JoinProjectRequest` は `Host` のほかに `AuthPort` と `LorePort` を持つ（0 なら契約の既定）。

- `Host` に `host:port` と書いた場合、そのポートは **窓口（既定 41350）** として使う。
- **Lore 本体のポート（既定 41337）は `LorePort` でしか指定できない。**
  `AuthEndpointResolver.BuildLoreRemoteUrl` は `Host` に付いているポートを必ず落とす。

こうしてあるのは、1 つの入力欄に 2 つのポートの意味を持たせると
「参加はできるのにクローンだけ別のサーバへ行く」という壊れ方をするため。
既定ポート以外で動かしているサーバに参加するには `LorePort` を渡すこと
（参加画面はまだ既定のままで、指定する UI は無い）。

### 2.6 クローンが `ILoreBackend` に無い理由

`ILoreBackend` は「既にある作業コピー 1 つ」に対する操作の束で、生成時に
作業コピーのルートを要求する。クローンはまだ作業コピーが無い状態で走るので
その型に収まらない。無理に押し込むと「ルートが空でも壊れないバックエンド」という
例外的な状態を全メソッドが気にすることになるため、`ILoreCloner` を別に立てた。

LoreVcs の clone の癖（XML ドキュメントと既存の結合テストで確認）:

- **クローン先は `LoreRepositoryCloneArgs` に無い。`LoreGlobalArgs.RepositoryPath` がクローン先**
- `LoreRepositoryCloneArgs.RepositoryUrl` がクローン元。リポジトリ名は URL の末尾に含める
- ブランチ指定のプロパティは無い（最も近いのは `Revision`）

### 2.7 名前の規則で、契約に書かれていないことを決めた

契約 2 章は「1〜32 文字。英数字・`_`・`-`・`.` と日本語可」とだけ書いてある。
実装するには 2 つ決める必要があったので、`AccountNameRule` のコメントに明記した:

- **長さは Unicode コードポイントで数える**（Rust の `chars().count()` と同じ）。
  UTF-16 の要素数で数えるとサロゲートペアが 2 文字に見える。
- **使える日本語の範囲**: ひらがな（U+3041–U+309F）・カタカナ（U+30A0–U+30FF、長音符を含む）・
  CJK 統合漢字（U+4E00–U+9FFF）・拡張 A（U+3400–U+4DBF）・繰り返し記号（U+3005–U+3007）。
  半角カナ・全角英数は入れない（見た目が同じで別の文字になる「なりすまし」を減らすため）。

なお**一意判定はサーバ側で ASCII の大文字小文字を無視**する（`alice` と `Alice` は同じ人）。
エディタは表示を入力どおりに保ち、衝突は `name_taken` の文言でそのことを伝える。

### 2.8 エラーコードの言い換えは 1 か所

契約 3 章のコード（`invalid_request` / `login_failed` / `unauthorized` / `forbidden` /
`invite_invalid` / `loopback_only` / `not_found` / `name_taken` / `owner_exists` /
`too_many_requests` / `internal`）→ 日本語 1 行の対応は `Http/AuthFailureText.cs` だけが持つ。
同じコードを 3 か所（参加画面・オーナー向けダイアログ・自動ログイン）で解釈すると、
片方だけ言い回しが古くなる。

`name_taken` と `owner_exists` は**操作によって意味が変わる**ので、
呼び出し側が上書きする（`owner_exists` は「既にオーナーが居る」と
「owner の権限を失効させようとした」の両方で返る）。

### 2.9 owner の権限は失効できない

契約 3 章のとおり、失効は**アカウント単位ではなくリポジトリ権限（grant）単位**で、
**owner ロールの権限は失効できない**（自分自身を含む。サーバは 409 `owner_exists`）。
参加者一覧では owner の行を選んでいる間、取り消しボタンを押せなくする
── 押せるように見せてサーバに断られるより分かりやすい。

---

## 3. 画面

### 3.1 Hub（スタート画面）のアカウント欄

`editor/src/Startup/StartWindow.Accounts.cs`（partial。`StartWindow.xaml.cs` は触らない）。

- **未作成** … 「アカウントを作成...」＋「読み込み...」。書き出すものが無いので「書き出し」は出さない。
- **作成済み** … 名前を出し、「書き出し...」「読み込み...」。作成ボタンは隠す。
- **読み込みに失敗** … 名前の代わりに理由を赤字で出す（別 PC で作ったものを開いた場合など）。
- 「プロジェクトに参加...」 … アカウントが無ければ押した時点で案内して止める。

### 3.2 参加の流れ

1. サーバのアドレス（ホスト名か IP）・招待コード・保存先フォルダを入力
2. `POST /v1/join` で自分の公開鍵を登録
3. チャレンジ応答でログインし、トークンを得る
4. **トークン付きで** `lore://<host>:41337/<プロジェクト名>` からクローン
5. クローン先から `.seedproj` を探し（直下 → 1 階層下）、見つかればそのまま開く

判断は全部 `ProjectJoinService`（WPF 非依存）にあり、画面は入力を集めて
結果を 1 行出すだけ。失敗の言い分けは次のとおり:

| 起きたこと | 出す文言 |
|---|---|
| 保存先が空でない | 「保存先フォルダが空ではありません: …」（**サーバへ行く前に止める**） |
| 窓口へ繋がらない | 「アカウントのサーバに繋がりません。…」 |
| 招待コードが不正・期限切れ・使用済み | 「招待コードが使えません（…）」 |
| 名前の衝突（`name_taken`） | 「名前『…』はこのサーバで既に使われています。…」 |
| クローンが失敗 | 「プロジェクトを取得できませんでした: <Lore のメッセージ>」 |
| 取得できたが `.seedproj` が無い | 「取得できましたが、フォルダに .seedproj が見つかりません。…」 |

### 3.3 オーナー向け（Version Control パネル →「アカウント」）

`Views/ProjectAccountsWindow.cs`。開いたときのログイン状態で節を出し分ける。

- **オーナーでない** … 「このプロジェクトでアカウントを有効にする」だけ。
  ループバックからしか通らないことと、**サーバでリポジトリを作ったあと・
  Lore の認証を有効にする前に行う手順**であることを先に案内する
  （契約 5 章「有効化の順番」。認証を有効にしたサーバでは新しいリポジトリを作れない）。
  成功したら権限入りのトークンを取り直し（`AttachToProjectAsync`）、画面を作り直す。
- **オーナー** … 「招待コードを発行」＋コピー、参加者の一覧・更新・失効。
  **招待コードはサーバが 1 回しか返さない**ので、その場で表示してコピーさせ、
  「この画面を閉じると二度と表示できません」と書く。ログには出さない。
- **owner の行を選んでいる間は取り消しボタンを押せない**（2.9）。

### 3.4 パネルのヘッダー

ログイン中は identity を「名前（ログイン中）」にする
（`VersionControlDisplay.ToConnectionText(identity, remoteUrl, isSignedIn)`）。
匿名との違いが見えないと、ロックの「自分／他の人」が成立しているのかが分からない。
identity が不明なときは「ログイン中」とは出さない（矛盾した表示になるため）。

---

## 4. テスト

`editor/tests/AccountsTests/`（既存のコンソールハーネス形式、`SpriteRigTests/TestHarness.cs` 共用）。

```bash
dotnet run --project editor/tests/AccountsTests
```

**実サーバは要らない。** 発行窓口は `HttpListener` で立てた偽物（`FakeAuthGateway`）で、
本番ポート（41337 / 41339 / 41350）には一切接続しない（偽物は 41371 以降の空きを使う）。

固定しているもの:

- 鍵と署名の形式（**固定のテストベクタ**。公開鍵 65 バイト・先頭 `0x04`・署名 64 バイト）
- 新しい鍵 64 本ぶんの長さ（座標の先頭ゼロで長さが変わる実装を炙り出す）
- 署名対象の文字列（`seed-auth-login:v1:<cid>:<nonce>` の UTF-8 バイト列まで）
- base64url の往復（長さ 1〜32 の全パターン）
- 名前の規則（通る／弾く、コードポイントでの長さ）
- 保管の往復（DPAPI）、平文が残らないこと、tmp が残らないこと、壊れたファイルの扱い
- 書き出し／読み込み（正しい／誤ったパスフレーズ、名前の改竄、短いパスフレーズ、形式違い）
- 窓口 URL の決め方（リモートと同じホストの 41350／利用者設定の上書き）
- 偽の窓口に対する join → ログイン → **期限前の自動更新** → 失効時の失敗
- Bearer ヘッダの形と `repository_id` クエリ名
- 契約のエラーコードごとの言い換え、招待コードの有効時間の丸め（1〜720）
- 参加の段取りの全分岐（トークン付きクローン・失敗時の言い分け・長すぎる招待コード）
- **トークンがあると `IdentityToken` と `AccessToken` の両方に同じ JWT が入り
  `Identity` が空になること**（実物の `LoreNativeBackend` で）
- `.lore/id` の生 16 バイト → 32 桁の 16 進小文字への変換

**偽の窓口は署名を本当に検証する**（登録された公開鍵で P1363 の 64 バイトを確かめる）。
署名対象の組み立て・base64url・P1363 のどれかを取り違えたら必ず落ちる。

`editor/tests/VersionControlTests` の `PanelStateTests` にも
「ログイン中は identity に（ログイン中）が付く」を足してある。

### 4.0 実サーバとの結合テスト（`SEED_ACCOUNTS_TEST_SERVER`）

偽の窓口・偽のクローンでは「エディタの中だけ」しか固定できない。
**トークン付きのクローン**と、認証を有効にしたサーバに対する送信・取得・ロックを
実際に動かすのが `ServerIntegrationTests.cs` ＋ `AccountsServerFixture.cs`。

```powershell
# 先に seed-loreserver をビルドしておく（debug の exe を使う）
cd tools/seed-loreserver ; $env:CARGO_BUILD_JOBS="2" ; cargo build ; cd ../..

$env:SEED_ACCOUNTS_TEST_SERVER = "1"
dotnet run --project editor/tests/AccountsTests
```

| 環境変数 | 意味 |
|---|---|
| `SEED_ACCOUNTS_TEST_SERVER` | これが空でないときだけ結合テストを登録する（既定では 1 件も走らない） |
| `SEED_ACCOUNTS_TEST_TMP` | 一時フォルダの親を差し替える（既定は `%TEMP%`）。ストアが数百 MB になるので逃がせる |
| `SEED_ACCOUNTS_TEST_KEEP` | 失敗を追うとき用。一時フォルダを消さずに残す（**証明書とサーバの署名鍵が残るので、見終わったら消すこと**） |

使うポートは **Lore 41357 / 41359、発行窓口 41361、権限サービス 41362**（すべて 127.0.0.1）。
本番（41337 / 41339 / 41350 / 41352）には一切繋がない。
`VersionControlTests` の `LoreServerFixture` も 41357 / 41359 を使うので、
**2 つの結合テストを同時に走らせないこと**（順に走らせれば衝突しない）。

通す筋書きは契約 5 章「有効化の順番」そのままで、
第 1 段（`[seed_auth]` のみ）→ 第 2 段（`[server.auth]` と `[environment.endpoint]` を足して
**1 回だけ**再起動）と進む。**以後は設定を一切書き換えない。**
アカウントは A（`つばさ-01`＝日本語を含む名前・`repository_creators` に載せる）、
B（`bob-02`）、C（`carol-03`＝2 つめのリポジトリにだけ参加）の 3 人を別フォルダで持つ。

#### 実機で分かった注意点（ここを踏むと必ずハマる）

1. **`[environment.endpoint] auth_url` は `seed-loreserver` 自身の権限サービスを指す。**
   以前は「付けると clone と `repository create` が落ち、外すと pull が落ちる」ため
   操作ごとに再起動して付け外ししていたが、権限サービスができたので**付けっぱなしで全部通る**。
   対応表と根拠は契約 5 章「`auth_url` の二律背反（解消済み）」。
   別の URL（到達できない値）を指すと、以前の壊れ方に戻る。
2. **サーバを強制終了する前に、mutable ストアの遅延書き出しを待つ。**
   待たずに止めると**リポジトリ名 → ID の対応が消え**、次の起動から clone が
   「Not found」で落ちる。`AccountsServerFixture.StopProcess` が
   「ファイル数と合計サイズが落ち着くまで待つ」を実装している。
3. **参加のポート。** `JoinProjectRequest` の `Host` に書いたポートは**窓口のもの**。
   Lore 本体のポートは `LorePort` で明示する。**これを間違えると、
   既定ポート（41337）で動いている別のサーバ＝本番へクローンしにいく。**
4. **`GET /v1/members` の `added_at` は数値（Unix ミリ秒）。**
   文字列で受けると一覧が丸ごと「サーバの応答を解釈できませんでした」になる。
5. トークンはサーバを再起動しても有効（署名鍵 `issuer_key.json` が残るため）。
   テストが再起動を挟んでもログインし直す必要は無い。
6. **新しいリポジトリを作った直後は、手元のトークンにそれが載っていない。**
   窓口の招待・一覧・失効はトークンの `resources` を見るので、
   2 つめのリポジトリを管理するテストは**その場でログインし直している**
   （共有している `_sessionA` は触らず、使い捨ての `AccountSessionManager` を作る）。
7. **`RefreshNowAsync` の前後でトークンが変わるとは限らない。**
   `iat` / `exp` は秒単位なので、同じ秒に 2 回ログインすると JWT はバイト単位で同一になる
   （EdDSA は決定的）。「取り直すと別のトークンになる」を確かめるテストの直前に
   別のログインを挟まないこと。

### 4.1 本物のアカウントを壊さないための仕掛け

既定の保管先は `%APPDATA%\SEED\account\account.json`。テストがそこへ書くと
**利用者の本物の鍵を上書きする**（鍵を失うと参加中のプロジェクトへ入れなくなる）。
`AccountsTests` は `Main` の最初で `SEED_ACCOUNT_DIR` を `%TEMP%` 配下へ向け、
個々のテストも明示的に一時フォルダを渡す（二重の防御）。

---

## 5. 今後の注意

- `AuthContracts.cs` が契約との唯一の接点。サーバ側の綴りが変わったらここだけを直す。
  **綴りだけでなく型も合わせること。** 時刻はすべて **Unix ミリ秒の数値**で、
  文字列で受けているものは 1 つも無い（`added_at` を文字列にしていて、
  実サーバに繋いだ途端に参加者一覧が丸ごと読めなくなった実績がある）。
  偽の窓口（`FakeAuthGateway`）も**実サーバと同じ型**で返すこと ──
  偽物だけ通るテストは、通らないより悪い。
- JWT の検証はエディタでは行っていない（`exp` を見て先回りで更新するだけ）。
  Lore 本体がサーバの JWKS で検証するため、二重に検証しても意味が無い。
- `AccountSettings.EXPORT_KDF_ITERATIONS` を増やすときは、
  書き出しファイルに反復回数が記録されているので**古いファイルは読めるまま**になる。
- `DPAPI_ENTROPY_SEED` を変えると、それ以前に保存したアカウントが読めなくなる。
