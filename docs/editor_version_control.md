# エディタのバージョン管理（中核層）

`editor/src/VersionControl/` に置いた、**UI を持たない** バージョン管理の中核層の設計。
決定事項と Lore そのものの調査結果は `docs/vcs_lore.md` が正典で、ここは
「エディタ側でどう組んだか」だけを書く。Version Control パネル（UI）は次段で別途作る。

---

## 1. 全体像

```
 パネル（次段）
      │  IVersionControlProvider / ILockService だけを見る
      ▼
 VersionControlService        … 生成・破棄・状態の配布・デバウンス
      │
      ├─ VersionControlProviderFactory  … .lore/ の有無で実装を決める
      │
      ├─ LoreProvider  ←  IVersionControlScheduler（専用スレッド 1 本）
      │      │             LoreLockService も同じスケジューラを共有する
      │      ▼
      │   ILoreBackend                 … Lore のコマンドを 1 対 1 で並べた細い境界
      │      ▼
      │   LoreNativeBackend            ★ LoreVcs に依存する唯一のファイル
      │      ▼
      │   NuGet LoreVcs 0.9.0 → lorelib.dll
      │
      └─ NullVersionControlProvider    … .lore/ が無いときは全部「利用不可」
```

### ファイル一覧

| パス | 役割 |
|---|---|
| `VersionControlSettings.cs` | タイムアウト・デバウンス等の数値の唯一の置き場 |
| `VersionControlMessages.cs` | 利用者に見せる日本語文言の唯一の置き場 |
| `VersionControlPaths.cs` | 絶対パス ⇄ リポジトリ相対パス、`.lore/` の有無判定 |
| `VersionControlService.cs` | ライフサイクル・状態配布・デバウンス（プロセスに 1 つ） |
| `VersionControlProviderFactory.cs` | Lore / Null の振り分け |
| `Abstractions/IVersionControlProvider.cs` | プロバイダ境界 |
| `Abstractions/ILockService.cs` | ロック境界（差し替え可能） |
| `Model/*.cs` | 変更ファイル・ブランチ・リビジョン・ロック・競合・操作結果 |
| `Null/*.cs` | VCS 無しの実装 |
| `Scheduling/*.cs` | 直列ワーカー（本番）とその場実行（テスト） |
| `Lore/Backend/ILoreBackend.cs` | Lore コマンドの抽象（テストの継ぎ目） |
| `Lore/Backend/LoreBackendRows.cs` | Lore の生イベントを写す DTO |
| `Lore/Backend/LoreCallResult.cs` | Lore 呼び出し 1 回分の結末 |
| `Lore/Backend/LoreNativeBackend.cs` | **LoreVcs に依存する唯一のファイル** |
| `Lore/Backend/LoreShutdownGuard.cs` | `Lore.Shutdown()` をプロセスで 1 回だけ呼ぶ |
| `Lore/LoreConflictResolutionMap.cs` | 競合解決の対応表（1 か所に固定） |
| `Lore/LoreStatusTranslator.cs` | status の生データ → モデル |
| `Lore/LoreBranchTranslator.cs` | LOCAL / REMOTE の統合 |
| `Lore/LoreLockTranslator.cs` | ロック行 → モデル（所有者不明の扱い） |
| `Lore/LorePushDiagnosis.cs` | push 拒否 → NeedsSync の判定 |
| `Lore/LoreConnectionDiagnosis.cs` | 失敗が接続起因かの判定 |
| `Lore/LoreProvider.cs` | 境界の実装（LoreVcs 非依存） |
| `Lore/LoreLockService.cs` | ロック境界の実装（LoreVcs 非依存） |
| `Lore/Backend/LoreCredentialResolver.cs` | **トークンと identity の決め方**（4.8） |
| `Lore/Backend/ILoreCloner.cs` | クローンの抽象（作業コピーが無い状態で走る） |
| `Lore/Backend/LoreNativeCloner.cs` | **LoreVcs に依存する 2 つ目のファイル**（clone だけ） |

---

## 2. 上の層が使う語彙

アーティストも使うので、**stage / rebase / detached HEAD は境界の外へ出さない**。
`IVersionControlProvider` が公開するのは次だけ。

| メソッド | 意味 | 内部で起きること |
|---|---|---|
| `GetStatusAsync(mode)` | 状態の取得 | 既定は `status --scan --offline` 相当（サーバ往復なし） |
| `NotifyChangedAsync(絶対パス[])` | 変更の通知 | `file dirty` |
| `NotifyMovedAsync(from, to)` | 移動・改名の通知 | `file stage move` |
| `SubmitAsync(message)` | **送信** | 走査つき stage → 空判定 → commit → push |
| `FetchLatestAsync()` | **最新を取得** | sync → 状態を引き直して競合検出 |
| `ResolveConflictsAsync(paths, choice)` | 競合の解決 | `merge resolve mine/theirs` → 残り 0 ならマージを commit |
| `GetBranchesAsync()` / `CreateBranchAsync` / `SwitchBranchAsync` | ブランチ | LOCAL/REMOTE は統合して返す |
| `GetHistoryAsync(maxCount)` | 履歴 | サーバ必須 |
| `Locks` | ロック | 一覧・取得・解放・照会 |

戻り値は必ず `VersionControlResult` / `VersionControlResult<T>`。**例外は境界の外へ出ない**。
`Outcome` の意味は次のとおりで、UI の分岐はこれだけ見ればよい。

| Outcome | 意味 | UI の出し方 |
|---|---|---|
| `Success` | 成功 | そのまま |
| `NothingToDo` | 送るものが無い等。失敗ではない | 情報表示 |
| `NeedsSync` | push が分岐で弾かれた。手元のコミットは残っている | 「最新を取得」を促す |
| `Conflicted` | 未解決の競合がある | 競合一覧＋2 択を出す |
| `Unavailable` | VCS 無し | パネルを隠す |
| `RequiresConnection` | サーバ必須の操作でオフライン | 「今は取れません」 |
| `Canceled` | 中断・期限切れ | 静かに戻す |
| `Failed` | それ以外 | エラー表示。`Details` にログ用の原文 |

---

## 3. スレッドモデル

- **`LoreVcs` の `Wait()` は同期ブロッキング**で、gRPC 往復（実測 350 ms）を含む。
  UI スレッドで呼ぶとエディタが固まる。
- 加えて、同じ作業コピーへ 2 つの Lore 呼び出しが並行すると `.lore/` の状態が壊れ得る。

そこで `SerialWorkerScheduler`（**専用スレッド 1 本の FIFO キュー**）を 1 プロバイダにつき 1 つ持ち、
`LoreProvider` と `LoreLockService` がそれを共有する。呼び出し側は UI スレッドから
`await` するだけでよい。

```
UI スレッド ── await ──▶ Task
                          ▲
   専用スレッド: [status] → [stage] → [commit] → [push] → …   （重ならない・投入順）
```

### 中断とタイムアウトの正直な限界

`LoreVcs` には **実行中の操作を止める API が無い**（`Cancel` / `Abort` に相当するものが
アセンブリ全体に存在しない）。したがって:

- キャンセルは **キューで待っている間にだけ効く**。
- タイムアウトすると呼び出し側には `Canceled` が返るが、**ワーカーは走り続ける**。
  「UI を無限に待たせない」ための保険であって、「Lore を止める」機能ではない。

### イベントは必ずその場でコピーする

Lore の FFI イベントデータ（`Lore*EventDataFFI`）は **コールバックの中でだけ有効**。
`LoreNativeBackend` はコールバック内で `record struct`（`Lore*Row`）へ値を写し、
外へはその写しだけを渡す。

### `StatusChanged` は UI スレッドではない

`VersionControlService.StatusChanged` は**ワーカースレッドから発火し得る**。
UI を触る購読側が自分で `Dispatcher` へ移すこと（サービスは WPF に依存しない）。

### `Lore.Shutdown()`

プロセスで **1 回だけ**。`LoreShutdownGuard.Shutdown()` に一本化してあり、
`editor/src/App.xaml.cs` の `OnExit` から呼ぶ。作業コピー単位の `Dispose` では呼ばない。

---

## 4. Lore の罠と、どこで吸収しているか

`docs/vcs_lore.md` 3.1 の表に、実装中に新たに判明したものを足した完全版。

| 罠 | 吸収している場所 |
|---|---|
| `stage .` は走査しないと何も stage しない | `LoreProvider.SubmitCore` が `FileStage(".", scan: true)` |
| 未 stage の commit が空リビジョンを作る | stage 直後の status で staged 件数を数え、0 なら commit しない |
| `sync` は競合しても成功（rc=0）を返す | sync 後の status の `flagConflict*` で判定（`ExtractUnresolvedConflicts`） |
| `resolve mine` / `theirs` が直感と逆 | `LoreConflictResolutionMap`（下記 4.1） |
| 素のファイル移動は履歴が切れる | `NotifyMovedAsync` → `file stage move` |
| 既定の status はファイルシステムを見ない | 保存時に `file dirty`、開いた直後だけ `scan` |
| `history --offline` は失敗 | `LoreConnectionDiagnosis` で `RequiresConnection` へ畳む |
| `lock acquire` は二重取得でも成功を返す | acquire の前後で `lock status` を取り、所有者から結末を決める |
| 認証無しだと所有者が `<unknown>` | `LockHolder` を 3 値（Self/Other/Unknown）にして「自分のもの」と決めつけない |
| **`LoreFileAction` に `MODIFY` は無い。`KEEP` が「変更」** | `LoreStatusTranslator.ToChangeKind`（4.2） |
| **相対パスは `WorkingDirectory` から解決される** | `LoreNativeBackend.NewGlobalArgs`（4.3） |
| **Lore は一般的な失敗に `-1` を返す** | `RETURN_CODE_CANCELED = int.MinValue`（4.4） |
| **`resolve mine` はオフラインだと実体を取れず失敗する** | `MergeResolve` だけ `offline: false`（4.5） |
| **`REVISION_HISTORY_ENTRY` にメッセージ・作者・日時が無い** | `RevisionMetadata` で 1 件ずつ補う（4.6） |

### 4.1 競合解決の向き（取り違えると利用者の作業が消える）

```
利用者の選択            Lore のコマンド          実際に採られる内容
「自分の変更を残す」 →  resolve theirs      →  ローカル（自分）の内容
「リモートを採用」   →  resolve mine        →  リモート（相手）の内容
```

根拠（Lore v0.9.0 のソース）:

- `lore-revision/src/commit.rs`：sync のマージは
  「`Merge of divergent branch history, other parent is current revision`」
  → `parent_other`（= `parents()[1]`）が**手元の現リビジョン**、
    `parent_self`（= `parents()[0]`）が**取り込んだ側（リモート）**。
- `lore-revision/src/stage.rs`：`MergeParent::Mine => parents()[0]`、`Theirs => parents()[1]`。
- `lore/src/branch.rs`：`merge_resolve_mine → MergeParent::Mine`、`_theirs → Theirs`。

Lore 自身の doc コメントは `merge_resolve_mine` を "accepting the local (mine) version" と
書いているが、sync マージの並びでは上記のとおり逆になる。**実サーバ結合テストで
実際のファイル内容を突き合わせて確認済み**（`ResolveKeepMineKeepsLocal` /
`ResolveTakeRemoteTakesRemote`）。

対応付けは `LoreConflictResolutionMap` の 1 か所だけで行い、単体テストで固定している。

### 4.2 `KEEP` が「変更」

`LoreFileAction` は `KEEP / ADD / DELETE / MOVE / COPY` の 5 値しかない。
「内容が変わった」は `KEEP`（ノードは残る）で表され、Lore の CLI も
`Keep => "M"` と描いている（`lore-revision/src/interface.rs`）。
`MODIFY` を探すと**変更されたファイルが全部「不明」になって一覧から消える**。

### 4.3 `WorkingDirectory` の指定は必須

`LoreGlobalArgs.WorkingDirectory` が空だと、引数の**相対パスは呼び出したプロセスの
カレントディレクトリから解決される**。エディタの CWD は作業コピーと無関係なので:

- `lock acquire` が `invalid path: <エディタの起動フォルダ>/assets/a.png` で失敗する
- `merge resolve` が**どのファイルにも一致せず、しかも rc=0（成功）を返す**
  → 「解決しました」と表示しながら競合が残る

`NewGlobalArgs` で必ず `WorkingDirectory = WorkingCopyRoot` を設定すること。

さらに保険として、`ResolveConflictsAsync` は解決後に status を引き直し、
**頼んだパスがまだ未解決なら `Failed` を返す**（Lore の rc を信用しきらない）。

### 4.4 `-1` を内部コードに使わない

Lore は一般的な失敗に `-1` を返す（分岐による push 拒否も `-1` だった）。
内部の「中断」印を `-1` にすると、`NeedsSync` が `Canceled` に化けて
「最新を取得してください」と案内できなくなる。`int.MinValue` を使う。

### 4.5 `resolve mine` はオンラインで実行する

「リモートを採用」は取り込んだ側の実体を復元するが、それがローカルストアに
無いことがある。`offline` を立てると
`Unable to restore path to selected state: Address not found: ...` で失敗する。
「自分の変更を残す」側は実体が手元にあるのでオフラインでも通るため、
**片方だけ壊れることに気づきにくい**。`MergeResolve` は `offline: false` で呼ぶ。

### 4.6 履歴にメッセージ・作者・日時が無い（メタデータで補う）

`LoreRevisionHistoryEntryEventDataFFI` は `Revision` / `RevisionNumber` / `Parent` しか
持たない（リフレクションで確認）。コミットメッセージ等はリビジョンの**メタデータ**として
別に保存されており、`Lore.RevisionMetadataList` で 1 リビジョンずつ引く必要がある。

実サーバ（loreserver v0.9.0）で観測した実際のキーは次のとおり:

| キー | 型 | 内容 |
|---|---|---|
| `message` | STRING | コミットメッセージ |
| `committed-by` | STRING | コミットした人（identity） |
| `created-by` | STRING | 作った人（identity） |
| `timestamp` | NUMERIC | **Unix ミリ秒**（例 1789661083806） |
| `branch` | （SEED では未使用の型） | ブランチ |

吸収している場所:

- `ILoreBackend.RevisionMetadata(revisionId)` … `revision metadata list` の 1 対 1 の窓口
- `LoreRevisionMetadataTranslator` … キー名の対応表（**候補キーの配列はここだけ**）と
  時刻の単位推定（秒 / ミリ / マイクロ / ナノを桁で判定し、
  現実的でない値は「不明」にして **でたらめな日付を出さない**）
- `LoreProvider.FillRevisionMetadata` … 履歴の各行へ 1 件ずつ補う

1 リビジョンにつき 1 往復かかるため、補うのは
`VersionControlSettings.HistoryMetadataLimit`（既定 30）件まで。
超えた分と、メタデータを引けなかった行は**一覧から消さず**番号だけ残す
（「1 件引けなかったから履歴全体が出ない」より良い）。

### 4.7 フォルダの移動（実機で確認済み）

`file stage move` に**フォルダ**を渡すと、Lore は中のファイルを
「移動」として記録する（削除 + 追加にはならない）。
したがってプロジェクトパネルのフォルダ D&D でも、ファイル単位に展開せず
フォルダのパスをそのまま `NotifyMovedAsync` へ渡せばよい。
結合テスト `[結合] フォルダの移動も移動として記録される` が、
移動先が全件 `Moved` であること・移動元に `Deleted` が残らないことを固定している。

### 4.8 SEED アカウントのトークンの渡し方（`LoreCredentialResolver`）

エディタ側のアカウント機能は `docs/editor_accounts.md`、取り決めの正典は
`docs/seed_accounts.md`。バージョン管理の層が持つのは次の 2 つだけ。

- `VersionControlService.CredentialProvider`（`Func<LoreAccountCredential>`）
  … アプリ起動時に 1 回差し込まれる。**アカウントの型は参照しない**（依存は一方通行）。
  未設定なら常に匿名で、従来どおりの動作になる。
- `LoreCredentialResolver` … 共通引数に載せる値を決める純関数。

決め方（**間違えると全操作が失敗する**）:

| ログイン中か | `IdentityToken` | `AccessToken` | `Identity` |
|---|---|---|---|
| している | JWT | **同じ JWT** | **空** |
| していない | 空 | 空 | `.lore/config.toml` の identity |

- **`AccessToken` だけでは足りない**（実サーバで確認済み）。Lore v0.9.0 の
  `auth_exchange_for_identity` は、リポジトリ ID が確定していない呼び出し
  （`repository create` / `repository list` / **`clone`**）で authorization token を
  空にするため、`AccessToken` が Authorization ヘッダに載らず
  `authorization header required` で失敗する。そちらは `IdentityToken` 由来を使う。
- **`Identity` を同時に渡すと排他エラーで弾かれる**（identity はトークンの `sub` から読まれる）。

表示・比較用の identity は別物で、`ResolveDisplayIdentity` が返す
（ログイン中はアカウント名、未ログインなら config の identity）。
ここを取り違えると **自分で取ったロックが「他の人」に見える**。

`.lore/id` は**生の 16 バイト**であってテキストではない。
`VersionControlPaths.ReadRepositoryId` が 32 桁の 16 進小文字へ変換して返す。

---

## 4.5 Version Control パネル（UI）

手本は **Visual Studio の「Git 変更」パネル**。並び（ブランチのコンボ → 未送信/未取得の 1 行 →
メッセージ欄 → 主操作 → 折りたたみ節）と「変更をフォルダー階層のツリーで見せる」ところを
そのまま借りている。借りなかったところは 4.5.4 に理由つきで書く。

### 4.5.1 画面の並び

| 位置 | 中身 |
|---|---|
| 1 | **ヘッダー** … 分岐アイコン ＋ ブランチのコンボ（末尾に「新しいブランチ…」）／ identity — リモート ／ 取得 ↓・送信 ↑・更新 ⟳・その他 … のアイコンボタン |
| 2 | **未送信・未取得** … 「↑ 未送信… ↓ 未取得…」＋「すべての履歴を表示する」（履歴の節を開いて先頭へ） |
| 3 | **メッセージ欄** … 複数行。プレースホルダは「メッセージを入力してください &lt;必須&gt;」。Enter は改行、**Ctrl+Enter で送信** |
| 4 | **主操作** … 「送信」（`Seed.Button.Primary`）と「最新を取得」（通常）。`NeedsSync` のときは 2 つの書式が入れ替わる |
| 5 | 実行中の不確定プログレス（中断ボタンは出さない） |
| 6 | 結果の 1 行メッセージ（重大度で色分け） |
| 7 | **折りたたみ節** … 競合 (n) / 変更 (n) / ロック (n) / 履歴 |

「その他 …」メニューは「フォルダーで表示」「ロックをすべて解除」「アカウント…」。
**アカウントの入口はここに移した**（ヘッダーに並ぶのは手本と同じ 4 つのアイコンだけにするため）。

### 4.5.2 ファイル一覧

| パス | 役割 |
|---|---|
| `editor/src/Panels/VersionControlPanel.xaml` | 画面（ダークテーマ・節の見出し書式・ツリーの行テンプレート） |
| `editor/src/Panels/VersionControlPanel.xaml.cs` | 生成・購読・状態の反映・変更ツリー・右クリック |
| `editor/src/Panels/VersionControlPanel.Sections.cs` | 折りたたみ節の開閉・見出し・高さ配分・保存 |
| `editor/src/Panels/VersionControlPanel.Operations.cs` | 取得 / 送信 / 競合解決 / ブランチ / 履歴 / ロック |
| `editor/src/Panels/VersionControlPanel.Accounts.cs` | identity 表示とアカウントのダイアログ |
| `editor/src/Panels/VersionControl/ChangeTreeRowItem.cs` | 変更ツリーの行（インデント・アイコン・状態の 1 文字） |
| `editor/src/Panels/VersionControl/ConflictRowItem.cs` | 競合節の行（2 択ボタンつき） |
| `editor/src/Panels/VersionControl/HistoryRowItem.cs` | 履歴節の行 |
| `editor/src/Panels/VersionControl/LockRowItem.cs` | ロック節の行 |
| `editor/src/VersionControl/Presentation/VersionControlPanelState.cs` | **状態機械**（WPF 非依存・単体テスト済み） |
| `editor/src/VersionControl/Presentation/VersionControlNotice.cs` | Outcome → 1 行メッセージ + 重大度 |
| `editor/src/VersionControl/Presentation/ChangeListGroup.cs` | 競合と変更を分けるグループ分け |
| `editor/src/VersionControl/Presentation/ChangeTreeNode.cs` | **パス群 → フォルダー階層のツリー** |
| `editor/src/VersionControl/Presentation/ChangeTreeFlattener.cs` | ツリー → 見えている行（仮想化リスト用） |
| `editor/src/VersionControl/Presentation/VersionControlSections.cs` | 節の見出し・表示可否・既定の開閉・保存キー |
| `editor/src/VersionControl/Presentation/VersionControlSyncSummary.cs` | 前後関係 → 「未送信 / 未取得」の 1 行 |
| `editor/src/VersionControl/Presentation/VersionControlDisplay.cs` | 値 → 表示名・アイコンキー・状態の 1 文字 |
| `editor/src/VersionControl/VersionControlPanelStateStore.cs` | 節の開閉の保存（`editor/settings/version_control_panel_state.json`） |
| `editor/src/VersionControl/WorkingCopyWatcher.cs` | 作業コピーの見張り → `RequestRefresh` |
| `editor/src/Dialogs/TextInputWindow.cs` | 1 行入力のモーダル（ブランチ名） |
| `editor/tests/VersionControlPanelPreviewProbe/` | 画面をオフスクリーン描画して PNG に落とす検証用コンソール |

### 4.5.3 ビューとロジックを分ける理由

ボタンの有効条件（利用可能か / 実行中か / メッセージが空か / 競合が残っているか）、
ツリーの組み立て、見出しの件数、状態の 1 文字は分岐が多いのに、
**間違えてもビルドが通り、GUI を起動しないと見えない**。
そこで判断は全部 `Presentation/`（WPF 非依存）へ出し、
`editor/tests/VersionControlTests/PanelStateTests.cs` と `ChangeTreeTests.cs` で固定する。
ビューがやるのは「状態を読んでコントロールへ写す」ことだけ（`SyncControls()`）。

### 4.5.4 手本から意図的に変えたところ

| 手本（VS） | SEED | 理由 |
|---|---|---|
| 「↑↓ 3 / 1」と**件数**を出す | 「未送信あり / 未取得なし / 未確認」 | Lore が返すのは `is_local_ahead` / `is_remote_ahead` の **真偽値だけ**で件数を持たない（`LoreStatusRevisionRow`）。数を書けないのに書くと嘘になる |
| 件数は常に出ている | オフライン取得中は「未確認」 | 前後関係は `ScanOnline` でしか分からない。「なし」と書くと送り忘れる |
| 「修正（amend）」「スタッシュ」「関連する項目」 | 置かない | Lore に対応する概念が無い |
| 「すべてをコミット ▾」 | 「送信」＋「最新を取得」の 2 つ | 中核層の語彙（2 章）をそのまま出す |
| 画面全体が 1 本のスクロール | 節ごとに中身がスクロール | 全体スクロールだと「変更」の一覧が伸び切って**仮想化が効かなくなる**（数百〜数千件で固まる）。開いている節が高さを分け合う形にした |
| ツリーは `TreeView` | **平坦化した仮想化 ListBox** | 同上。`ChangeTreeFlattener` が「見えている行」だけを作り、インデントと開閉ハンドルで木に見せる |

### 4.5.5 画面の約束

- **利用不可**（`.lore` が無い）… 操作 UI を出さず、案内だけを出す
- **語彙** … stage / commit / push / sync を出さない
- **モーダル** … 取り返しのつかない操作の確認だけ（「リモートを採用」／未送信ありのブランチ切替）。
  必ず `Headless/EditorDialogs` 経由（ヘッドレスで UI スレッドが止まらないように）
- **中断ボタンを出さない** … LoreVcs に実行中の操作を止める API が無い（3 章）。
  出しても止まらないので不確定プログレスだけにする
- **競合** … 常に最上部の独立した節。**0 件のときは節ごと隠す**（本当に競合したときの目立ち方を鈍らせない）。
  2 択の表示名は `LoreConflictResolutionMap.ToDisplayName` から取り、文字列を直書きしない
- **ロックの「不明」** … サーバ認証なしの構成では普通に起こる。異常扱いせず淡々と出し、
  **不明なロックには解除ボタンを出さない**（他人の編集権を黙って奪わないため）。
  「ロックをすべて解除」も自分のロックだけを対象にする
- **状態の 1 文字** … 行の右端に `A`（追加）/ `M`（変更）/ `D`（削除）/ `R`（移動）/ `C`（複製）/ `!`（競合）。
  日本語の表示名はツールチップに回す（名前が長い行で列がガタつかないように）
- **ボタンの色を画面側で決めない** … アイコンは `Seed.Button.Icon`、主操作は `Seed.Button.Primary`、
  リンクは `Seed.Button.Link`（`docs/editor_ui_style.md`）。
  `NeedsSync` の強調も**色を塗らずに書式を入れ替える**ことで表す
- **開閉ハンドルはベクター** … 節の見出しもツリーも `App.xaml` の
  `TreeExpanderClosedGeometry` / `TreeExpanderOpenGeometry`（白抜き三角）を使い回す。
  `▷` `▽` のような記号文字は書かない（`.claude/rules/editor-icons.md`）

### 4.5.6 節の開閉と、サーバ往復の抑え方

- 既定は 競合＝開く / 変更＝開く / **ロックと履歴＝閉じる**。
  ロックと履歴は開いたときにしかサーバへ問い合わせない
  （パネルを出しただけで gRPC 往復が積み上がらないようにする）。
- 開閉はプロジェクトごとに `editor/settings/version_control_panel_state.json` へ保存する。
  保存キー（`conflicts` / `changes` / `locks` / `history`）は **変更禁止**
  （変えると利用者の開閉状態が黙って失われる）。
  書き込みはデバウンスし、終了時に `MainWindow` が `FlushSectionState()` で確実に書き出す。
- 履歴はまず直近 `PANEL_HISTORY_PAGE_SIZE` 件だけを引き、「さらに読み込む」で伸ばす。
  Lore は「残り何件か」を返さないので、**要求した上限ちょうど返ってきたとき**だけ
  「さらに読み込む」を出す。
- ツリーの畳み状態はセッション内だけで保存しない。畳んだ集合で持つので、
  取得のたびに現れる新しいフォルダーは既定で開いた状態になる。

### 4.5.7 状態取得のモード（どこがサーバに聞くか）

| きっかけ | モード | 理由 |
|---|---|---|
| 保存・ファイル監視（自動） | `ScanOffline` | 通信しない。上段の未送信・未取得は「未確認」のまま |
| ヘッダーの更新 ⟳ | **`ScanOnline`** | 未送信・未取得の有無はサーバに聞かないと分からない。利用者が明示的に押したときだけ払う |
| 送信 / 最新を取得 / 競合解決 / ブランチ切替の直後 | **`ScanOnline`** | どれもサーバと往復した直後で、結果を上段へ正しく映すため |

### 4.5.8 自動更新

`WorkingCopyWatcher` がプロジェクトルートを監視し、変化があれば
`VersionControlService.RequestRefresh(ScanOffline)` を呼ぶ（デバウンスはサービス側が持つ）。
無視するのは `.lore/` `cache/` `save/` `logs/` `build/` `.backup/`。
**`.lore/` を無視しないと、Lore 自身の書き込みで無限ループになる**。

プロジェクトパネルにも FileSystemWatcher はあるが、`assets/` 限定で
内容の書き換え（LastWrite）を拾わないため相乗りできない。
あちらの `NotifyFilter` を広げるとファイルグリッドが毎回再構築されて挙動が変わる。

### 4.5.9 ドッキング

`ContentId = "version_control"`（**変更禁止**）。既定では Project / Output と同じ下段。
旧 `layout.xml` にはこのパネルが無いため `EnsureAnchorable` が補完するが、
既定の「最初に見つかったペイン」では左ペインに入ってしまうので、
`siblingContentId: "output"` を渡して**下段へ入れている**。

### 4.5.10 見た目の自動確認（PNG）

パネルは GUI を起動しないと見えないので、`editor/tests/VersionControlPanelPreviewProbe` が
**ウィンドウを出さずに**実物の XAML を組み立て、各状態を PNG へ書き出す。

```bash
dotnet run --project editor/tests/VersionControlPanelPreviewProbe -- --out <出力先>
# changes / conflicts / locks / history / clean / unavailable の 6 枚
```

- 偽のプロバイダは `VersionControlService.UseProviderForVerification`（**検証専用の入口**）で据える。
  Lore にもサーバにも一切触れない。
- `Loaded` は「表示されたとき」に飛ぶ routed event なので、プローブが手で発火させて
  パネル本来の初期化経路を通す。
- プローブが起動アセンブリになるため、`App.xaml` の中の相対 pack URI
  （`resources/icons/Icons.xaml` など）の探索先を本体アセンブリへ向け直している
  （`ResourceAssemblyInitializer`）。向け直しに失敗すると
  「アイコンも共通書式も無い PNG」になるので、必ず警告を出すようにしてある。

---

## 5. csproj の注意

`editor/SEEDEditor.csproj` に次を入れてある。

```xml
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
<SelfContained>false</SelfContained>
...
<PackageReference Include="LoreVcs" Version="0.9.0" />
```

- `RuntimeIdentifier` が無いと、ネイティブ本体 `lorelib.dll`（別パッケージ
  `LoreVcs.runtime.win-x64` にある）が出力へ展開されず、実行時に落ちる。
- ただし RID を付けると既定では出力が `bin/Debug/net9.0-windows/win-x64/` へ 1 階層深くなる。
  SEED は出力パスに依存している箇所が多い（エディタのログ位置・ランタイム exe の探索・
  スタートメニューのショートカット・`.seedproj` の関連付け・`SeedMcpServer` のコピー先
  `$(OutputPath)`）ため、`AppendRuntimeIdentifierToOutputPath=false` で従来どおりに保つ。
- `SelfContained=false` は RID 指定で自己完結発行（数百 MB）へ倒れるのを防ぐ明示指定。

確認コマンド:

```bash
dotnet msbuild editor/SEEDEditor.csproj -getProperty:OutputPath -getProperty:TargetPath
# OutputPath = bin\Debug\net9.0-windows\   のままであること
```

---

## 6. 別のプロバイダを足すには

1. `Abstractions/IVersionControlProvider.cs` と `ILockService.cs` を実装する。
   **境界は変えない**（変えるとパネルが道連れになる）。
2. `VersionControlProviderFactory.Create` に判定を足す（例: `.p4config` があれば Perforce）。
3. Lore 固有の変換（`Lore/` 配下の Translator）は触らない。新実装は自分の変換を持つ。
4. 単体テストは新実装のバックエンド抽象に対して書く。

**ロックだけを差し替える**場合（Epic 公式の強制ロックが出たとき）は
`ILockService` の別実装を作り、`LoreProvider` の `Locks` が返すものを変えるだけでよい。
パネルもサービスも書き換えなくて済むよう、境界を分けてある。

---

## 7. テスト

`editor/tests/VersionControlTests/`（既存のコンソールハーネス形式、`SpriteRigTests/TestHarness.cs` 共用）。

### 7.1 純粋ロジック（既定で常に実行）

```bash
dotnet run --project editor/tests/VersionControlTests
```

`FakeLoreBackend`（`ILoreBackend` の偽物）を差し、Lore も実サーバも無しで
判断ロジックを固定する。固定しているのは主に:

- 競合選択の対応表（両方向・往復）
- 空コミット防止（staged 0 件で `commit` を呼ばないこと）
- stage が空でも手元が進んでいれば push すること（解決後のマージを取り残さない）
- push 拒否 → `NeedsSync`、無関係な失敗は `Failed` のまま
- sync 成功 + 競合あり → `Conflicted`
- LOCAL / REMOTE ブランチの統合（順序・現在ブランチ・未知の location）
- `<unknown>` 所有者の扱い、二重取得、行が返らないパス
- 状態フラグ → モデル変換（`KEEP` = 変更、競合 4 フラグの畳み込み、リモート比較）
- パス変換（作業コピー外を弾く・重複除去・区切り正規化）
- `NullProvider` が全部 `Unavailable` を返すこと
- 直列ワーカーが重ならない・順序を保つ・破棄後は中断を返す

### 7.2 実サーバ結合（環境変数があるときだけ）

```powershell
$env:SEED_LORE_TEST_SERVER = "1"
dotnet run --project editor/tests/VersionControlTests
```

`LoreServerFixture` が一時フォルダに自己署名証明書（Git 付属の openssl）と設定を作り、
素の `C:\Users\<user>\SEED_lore\bin\v0.9.0\loreserver.exe` を起動する。

- ポートは **41357 / 41359**（本番の 41337 / 41339 とは別）
- `host` は `127.0.0.1` 固定
- ストア・証明書・作業コピーはすべて `%TEMP%` 配下の使い捨てフォルダ
- 止めるのは自分が起動したプロセスだけ。最後の登録テストが必ず停止・削除する

通すシナリオ: リポジトリ作成 → クローン → 送信 → 取得 → 同じ行の競合 →
`KeepMine`（ローカルが残ることをファイル内容で確認）→ 解決後の送信 →
`TakeRemote`（リモートになることを確認）→ ロック取得・照会・一覧・解放 →
改名（`stage move`）→ **フォルダごとの移動**（4.7）→ **履歴のメタデータ**（4.6）。

履歴メタデータのテストは、失敗すると**観測した生のキー・値をそのまま出す**。
Lore の版が上がってキー名が変わったら、その出力を見て
`LoreRevisionMetadataTranslator` の候補配列を直せばよい。

### 7.3 パネルのロジック（既定で常に実行）

`PanelStateTests.cs`。ボタンの有効条件・Outcome ごとの見せ方・競合のグルーピング・
表示名の対応表を固定する。ビューを起動せずに全分岐を踏める。

**この結合テストが実際に 5 件のバグを見つけた**（4.3 / 4.4 / 4.5 と、
解決後のマージが取り残される問題）。境界の設計だけでは防げない種類なので、
Lore を触る変更を入れたら必ず一度は回すこと。

---

## 8. 現状の配線

- `editor/src/Project/ProjectContext.cs` の `Open`
  （プロジェクトを開く 2 経路が合流する唯一の場所）で
  `VersionControlService.Open(paths.RootDir)` を呼ぶ。
  検出結果がエディタのログへ 1 行出る:
  `バージョン管理: Lore（<root> remote=... identity=...）` または `バージョン管理: なし（<root>）`
- `editor/src/App.xaml.cs` の `OnExit` で
  `VersionControlService.Close()` → `LoreShutdownGuard.Shutdown()`。
- `VersionControlPanel` が `StatusChanged` を購読し、`Dispatcher` へ移して反映する。
- `WorkingCopyWatcher` が作業コピーの変化を見て `RequestRefresh` を呼ぶ（4.5.4）。
- プロジェクトパネルの**改名（インライン編集）とドラッグ＆ドロップ移動**の成功直後に
  `NotifyMovedAsync` を呼ぶ（`ProjectPanel.NotifyVersionControlMoved`）。
  素のファイル移動は Lore 上で「削除 + 追加」になり履歴が切れるため、この通知が要。
  削除・新規作成は走査（`ScanOffline`）で拾えるので通知は要らない。

まだ繋いでいないもの:

- 保存経路（`safe_write` 完了）からの `NotifyChangedAsync`。
  今は `WorkingCopyWatcher` が拾って `ScanOffline` で取り直しているので一覧には出るが、
  保存のたびに走査が走る。保存経路から直接 `file dirty` を打てば `TrackedOnly` で済む。
