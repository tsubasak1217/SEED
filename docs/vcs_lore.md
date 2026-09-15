# ゲームプロジェクトのバージョン管理（Lore）

エンジン（この SEED リポジトリ）は git、ゲームプロジェクト（`<project>/` = `.seedproj` + `assets/` + `plugins/`）は
Epic Games 製 OSS の **Lore**（MIT、Rust）で管理する。2026-09-15 の調査・実機検証・決定をここに集約する。
サーバの自前ビルドは `tools/seed-loreserver/README.md`、Lore 自体の公式資料は
https://epicgames.github.io/lore/ と https://github.com/EpicGames/lore を参照。

## 1. 決定事項

| 項目 | 決定 |
|---|---|
| 対象 | エンジン → git。各ゲームプロジェクト → Lore（プロジェクトごとに 1 リポジトリ） |
| サーバ | 自前運用。当面はユーザーの PC 上で `seed-loreserver`（`tools/seed-loreserver/`）を動かし、後で NAS／クラウド VM／S3 へ移す |
| ストレージ切替 | `loreserver` の設定（`[immutable_store] mode` / `[mutable_store] mode`）で吸収。v0.9.0 は AWS（S3+DynamoDB）プラグインを同梱 |
| ロック | Lore のロックは通知のみなので、SEED 側で強制する。境界を切り、Epic 公式の強制ロック（successor-locks LEP、承認済み）が出たら差し替える |
| エディタ | Version Control パネルを新設。**アーティストも使う**ので stage / rebase などの概念は出さない |
| 語彙 | Lore コア（git 系）に合わせる: commit / push / sync / branch / lock。UI の日本語は「送信」「最新を取得」「ブランチ」「ロック」 |
| 版 | Lore v0.9.0（pre-1.0）。データ形式は維持されるが API は変わり得るため、エディタ側はプロバイダ境界で隔離する |

## 2. 前提として済ませたこと（段階 0）

- ゲームプロジェクトを SEED リポジトリの追跡から外した（`projects/` は `.gitignore` で丸ごと除外）。
- エディタ視点（デバッグカメラの位置・向き）を `.scene` から `<project>/cache/editor/view/` のサイドカーへ分離
  （`.scene` に人ごとの値が入り、保存のたびに衝突していたため）。
- `.seedproj` の `engine_version` がエディタと食い違うときの確認ダイアログ。
- `<project>/.loreignore` を作成（cache / save / logs / build / `.backup/` / `*.tmp` / `*.bak` / `*.blend1` / `_unreferenced/`）。
  Lore の ignore は **stage / commit / status にだけ効く送り出し側のフィルタ**で、コミット済みのものは遡って外れない。

## 3. 実機検証の結果（v0.9.0、プロジェクト複製 470 ファイル / 88 MiB）

| 操作 | 所要 |
|---|---|
| `repository create` | 0.5 s |
| `commit`（初回 88 MiB） | 2.4 s |
| `push`（初回） | 1.0 s |
| `clone` | 2.3 s |
| `sync`（差分あり） | 1.0〜1.5 s |
| `status`（サーバ往復あり） | 0.65 s |
| `status --offline` / `status --scan --offline` | 0.09 s / 0.11 s |

- サーバストアは 60 MB（圧縮＋重複排除）。ローカルの `.lore/` は 1 MB 未満で、作業ツリーが実体（git のように 2 倍にならない）。
- JSON シーン（16,000 行）の離れた編集は行ベース 3-way で自動マージ。同じ行は diff3 マーカー＋ `<file>~base/~mine/~theirs`。
  バイナリも `~base/~theirs` が出て、mine / theirs で解決できる。
- C# バインディング NuGet `LoreVcs` 0.9.0 は `net9.0` + `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` でそのまま動作
  （`lorelib.dll` は自動解決）。

### 3.1 CLI の罠（エディタ統合で必ず吸収する）

| 罠 | 対処 |
|---|---|
| `lore stage .` は何も stage しない | `lore stage . --scan` |
| 未 stage で `commit` すると空リビジョンができる（exit 0） | commit 前に staged 件数を確認する |
| `sync` は競合しても exit 0 | `status --json` の `flagConflict*` で判定 |
| `resolve mine` = **リモート側**、`resolve theirs` = **ローカル側**（ファイル内マーカーの ours/theirs と逆） | UI は「リモートを採用」「自分の変更を残す」と言い換え、内部で対応付ける |
| 素のファイル移動は D + A で履歴が切れる | リネームは `lore stage move <from> <to>` |
| 既定の `status` はファイルシステムを見ない | エディタが保存時に `dirty` を通知し、起動時だけ `--scan` |
| `history --offline` は失敗 | 履歴はサーバ接続時のみ |
| `lock acquire` の二重取得は exit 0 | `--json` の結果で判定 |
| 認証無しだとロック所有者・push ユーザーが `<unknown>` | 4 章 |

## 4. 認証と identity（ロック強制の前提）

`[server.auth]`（JWT / OIDC）を書かなければサーバは認証しない。そのとき **ロックの所有者も push ユーザーも
`<unknown>`** になり、「他人のロック」という概念が成立しない（commit の identity は `.lore/config.toml` の値が残る）。
ロック強制（クライアント側の保存ゲートも、サーバ側の push フックも）には JWT による identity が必要。

候補:
1. **チーム共有鍵で自己発行する JWT**（LAN 向け。SEED 側で鍵と JWKS を配り、エディタがトークンを作る）
2. 外部の OIDC プロバイダ（クラウド公開時）

どちらにするかは次のスパイクで決める。決まるまでロック UI は「表示と取得・解放」に留め、強制は入れない。

## 5. サーバ運用（当面）

- 実行ファイル: `tools/seed-loreserver/`（`cargo build`。ビルド 8 分・メモリ 2 GB 程度、`CARGO_BUILD_JOBS=2` 推奨）。
- 置き場（案）: `C:\Users\<user>\SEED_lore\server\{config,certs,store}`。設定は `config/local.example.toml` を基に、
  ポートを既定（41337 / 41339）へ、`[server.*] host` は当面 `127.0.0.1`（チームが繋ぐ段階で `0.0.0.0` にし、
  Windows ファイアウォールの受信規則を追加する）。
- 起動時は `RUST_LOG=info` を付けないとログが出ない。
- **リポジトリはオフラインで作ってはいけない**（後からサーバへ繋げない）。最初から
  `lore repository create lore://<host>:41337/<Project> --identity <name>` で作る。
- ロックは `seed_file_lock_store`（JSON）に永続化。バックアップはストア 2 フォルダ＋ロック JSON をコピーするだけ。

### 5.1 本番プロジェクトの初期化手順（案）

```powershell
$env:RUST_LOG = "info"
seed-loreserver.exe --config C:\Users\<user>\SEED_lore\server\config      # 常駐

cd D:\SEED_projects\WarashibeFishing
lore repository create lore://127.0.0.1:41337/WarashibeFishing --identity <name>
lore status --scan            # cache 等が出ないことを目視
lore stage . --scan
lore status                   # Staged 件数を確認（空コミット防止）
lore commit "Initial import"
lore push
```

## 6. エディタ統合の設計（段階 3 以降）

- `editor/src/VersionControl/` に **プロバイダ境界** `IVersionControlProvider`（状態・stage・commit・push・sync・ブランチ・履歴・
  リネーム通知・dirty 通知・競合解決）と **ロック境界** `ILockService`（取得・解放・一覧・照会）を置く。
  最初の実装は `LoreProvider`（NuGet `LoreVcs`。cherry-pick だけ CLI）。VCS 無しの `NullProvider` でパネルは非表示。
- `LoreVcs` の呼び出しは **必ずワーカースレッド**（`.Wait()` は同期ブロッキングで gRPC 往復 350 ms が入る）。
- 常時表示の状態は `status --scan --offline` 相当（0.1 s）。サーバに触るのは push / sync / lock / 履歴のときだけ。
- 保存経路（`safe_write` 完了、プロジェクトパネルの作成・削除・リネーム）から dirty / move を通知する。
- 競合 UI はファイル単位で「自分の変更を残す（= `theirs`）」「リモートを採用（= `mine`）」の 2 択。テキストは行マージ結果をそのまま採用できる。
- パネル構成: 変更一覧（バッジ M/A/D/競合/ロック）、メッセージ欄、「送信」（stage + commit + push）、「最新を取得」（sync）、
  ブランチ（切替・作成）、履歴、ロック一覧。
- 将来: プロジェクトパネルのバッジ、シーン／アクターを開いたときの自動ロック、engine_version と同様の「開く前の確認」。

## 7. 参考

- 実機検証の作業物: `C:\Users\k023g\.claude\jobs\434062fd\tmp\lore_spike\`（プロジェクト複製、サーバ設定、C# プローブ）
- Lore バイナリ: `C:\Users\k023g\SEED_lore\bin\v0.9.0\`
- 残件は `docs/backlog.md` の「バージョン管理（Lore）」節
