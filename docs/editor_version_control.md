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
| `Model/MergeOrigin.cs` | 進行中のマージが sync 由来か branch merge 由来か（**mine/theirs の向きがこれで変わる**） |
| `Model/BranchOperationRules.cs` | ブランチのマージ元・削除（アーカイブ）候補の除外規則（4.5.6） |
| `Null/*.cs` | VCS 無しの実装 |
| `Scheduling/*.cs` | 直列ワーカー（本番）とその場実行（テスト） |
| `Lore/Backend/ILoreBackend.cs` | Lore コマンドの抽象（テストの継ぎ目） |
| `Lore/Backend/LoreBackendRows.cs` | Lore の生イベントを写す DTO |
| `Lore/Backend/LoreCallResult.cs` | Lore 呼び出し 1 回分の結末 |
| `Lore/Backend/LoreNativeBackend.cs` | **LoreVcs に依存する唯一のファイル** |
| `Lore/Backend/LoreShutdownGuard.cs` | `Lore.Shutdown()` をプロセスで 1 回だけ呼ぶ |
| `Lore/LoreConflictResolutionMap.cs` | 競合解決の対応表（1 か所に固定。出どころで向きが変わる） |
| `Lore/LoreMergeOriginStore.cs` | 進行中のマージの出どころを `cache/editor/vcs/merge_origin` へ記録 |
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
| `Locking/*.cs` | **ロックのゲート**（保存・送信を止める／自動ロック）。4.6 |
| `Locking/Presentation/LockGateNotifier.cs` | ゲートの提示（**この層で唯一 WPF に依存する**） |

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
| `MergeBranchAsync(sourceBranch)` | **ブランチのマージ** | `branch merge <元>`（競合なしならコミットまで自動）→ 状態を引き直して競合検出 |
| `ArchiveBranchAsync(name)` | **ブランチの削除（アーカイブ）** | `branch archive`。現在のブランチと既定ブランチは拒否 |
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
| **`branch merge` も競合を戻り値で伝えない**（成功を返すことも、失敗を返しつつ競合を残すこともある） | マージ後の status の `flagConflict*` で判定（`MergeBranchCore`。成否にかかわらず引き直す） |
| **ブランチの「削除」が無い**（`archive` が一覧から隠す操作） | `ArchiveBranchAsync` を「削除（アーカイブ）」として出す（4.5.6） |
| `resolve mine` / `theirs` が直感と逆 **（ただし逆になるのは sync のマージだけ）** | `LoreConflictResolutionMap` ＋ `LoreMergeOriginStore`（下記 4.1） |
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

**★向きは「進行中のマージがどちら由来か」で入れ替わる。** 実サーバ結合テストで
ファイルの中身を突き合わせて確認した結果:

| 進行中のマージ | 利用者の選択 | Lore のコマンド | 実際に採られる内容 |
|---|---|---|---|
| sync（最新を取得） | 自分の変更を残す | `resolve theirs` | ローカル（自分）の内容 |
| sync（最新を取得） | リモートを採用 | `resolve mine` | リモート（相手）の内容 |
| **branch merge（ブランチのマージ）** | 自分の変更を残す | **`resolve mine`** | **現在のブランチ（取り込み先）の内容** |
| **branch merge（ブランチのマージ）** | リモートを採用 | **`resolve theirs`** | **取り込み元のブランチの内容** |

つまり **sync のマージだけが逆**で、`branch merge` は Lore の語のとおり
（mine = 自分 = 現在のブランチ）になる。

根拠（Lore v0.9.0 のソース）:

- `lore-revision/src/commit.rs`：sync のマージは
  「`Merge of divergent branch history, other parent is current revision`」
  → `parent_other`（= `parents()[1]`）が**手元の現リビジョン**、
    `parent_self`（= `parents()[0]`）が**取り込んだ側（リモート）**。
  この並べ替えは sync の経路にだけ入る。
- `lore-revision/src/stage.rs`：`MergeParent::Mine => parents()[0]`、`Theirs => parents()[1]`。
- `lore/src/branch.rs`：`merge_resolve_mine → MergeParent::Mine`、`_theirs → Theirs`。

Lore 自身の doc コメントは `merge_resolve_mine` を "accepting the local (mine) version" と
書いており、`branch merge` ではそのとおりだが sync では逆になる。
どちらも**実サーバ結合テストで実際のファイル内容を突き合わせて確認済み**
（`ResolveKeepMineKeepsLocal` / `ResolveTakeRemoteTakesRemote` /
`MergeConflictKeepMineKeepsCurrentBranch` / `MergeConflictTakeRemoteTakesSourceBranch`）。

対応付けは `LoreConflictResolutionMap` の 1 か所だけで行い、単体テストで固定している。

#### 進行中のマージの出どころをどう覚えているか

向きを選ぶには「いま未解決のマージが sync 由来か branch merge 由来か」が要る。
これは `LoreMergeOriginStore` が作業コピーの **`cache/editor/vcs/merge_origin`** に記録する。

- `MergeBranchAsync` が競合を返したとき … `branch-merge` と書く
- 競合を全部解決してマージをコミットしたとき／競合なしで取り込めたとき … 消す
- `FetchLatestAsync`（sync）が通ったとき … 消す（進行中のマージが sync 由来へ入れ替わる）
- **ファイルが無い／読めないときは sync 扱い**（従来どおりの、検証済みの挙動へ倒す）

メモリではなくファイルに置いているのは、**マージを未解決のまま残してエディタを
閉じ、開き直してから解決する**ことが普通に起こるため。プロセス内の変数で覚えていると、
その場合に黙って逆の側を採ってしまう。`.lore/` は Lore のメタデータ用フォルダで
status の走査に出てこないので、作業コピーと一緒に持ち回る印の置き場所として使っている。

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

「その他 …」メニューは「フォルダーで表示」「ロックをすべて解除」／
「ブランチをマージ…」「ブランチを削除（アーカイブ）…」／「アカウント…」。
**アカウントの入口はここに移した**（ヘッダーに並ぶのは手本と同じ 4 つのアイコンだけにするため）。
ブランチのマージ・削除は、ここ（選択ダイアログ経由）に加えて **ブランチのコンボの中で
対象のブランチを右クリック**しても行える（2026-09-19 要望）。右クリックメニューは
「〜を『現在のブランチ』へマージ」「〜を削除（アーカイブ）…」の 2 項目で、
可否と押せない理由（現在のブランチ自身・既定ブランチ）は `BranchOperationRules` から引く。
コンボの左クリックは従来どおり「切り替える」「作る」だけ。
★右クリック時はドロップダウンを先に閉じてからメニューを出す（コンボのポップアップと
メニューがマウスの捕捉を取り合い、どちらかが勝手に閉じるため）。

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

### 4.5.6 ブランチのマージと削除（アーカイブ）

入口はどちらもヘッダーの「その他 …」メニュー。ブランチのコンボは
「切り替える」「作る」だけに保つ（取り返しのつかない操作を同じ場所に混ぜない）。

#### 「ブランチをマージ…」

別のブランチを**現在のブランチへ取り込む**（`IVersionControlProvider.MergeBranchAsync`）。

1. **未送信の変更が 1 件でもあれば始めない。** 1 行メッセージで
   「先に『送信』するか、変更を元に戻してから」と案内して終わる。
   取り込みは作業コピーのファイルを書き換えるので、手元の変更と混ざると
   「どちらが自分の変更か」が分からなくなる。
   判定は画面の一覧ではなく **その場の走査**（`ScanOffline`）で行う。
   ここで止めたときは 1 行メッセージ**に加えてモーダル**（OK / 警告）を出す
   （`VersionControlDisplay.BuildUnsubmittedMergeWarning`）。本文は
   **「マージは実行されていません。」で始まり**、未送信のファイルを
   最大 `UNSUBMITTED_WARNING_MAX_FILES` 件まで `・<パス>（<種類>）` で並べ、
   超えた分は「ほか n 件」に畳み、最後に次にすべきことを書く。
   ヘッドレスでは `EditorDialogs` がログへ流すだけなので止まらない。
   → **モーダルを足した理由は 4.6.6 の 2026-09-19 の事故**。1 行メッセージだけだと
   「止まった」ことが伝わらず、利用者は邪魔していたロックファイルを送信してしまった。
2. ブランチ一覧を取り直し、**現在のブランチを除いた**候補をダイアログに並べる
   （`BranchOperationRules.MergeSourceCandidates`）。候補 0 件なら案内して終わる。
3. 選ばれたら取り込む。競合が無ければ **Lore がマージのコミットまで自動で打つ**ので、
   結果は「取り込みました。『送信』で共有されます」。**取り込みは手元だけの操作**であり、
   ほかの人へ渡すには続けて「送信」が要る。
4. 競合が出たら結末は `Conflicted`。**既存の競合の節がそのまま使える**
   （2 択 → 全部解決したところでマージのコミットまで行う）。
   このとき採られる側は 4.1 の表のとおり `sync` と**逆**になるので、
   出どころを `cache/editor/vcs/merge_origin` に記録してから競合 UI へ渡している。
5. 分岐で弾かれたら `NeedsSync`（「先に『最新を取得』してください」）、
   サーバへ届かなければ `RequiresConnection`。

#### 「ブランチを削除（アーカイブ）…」

**Lore v0.9.0 にブランチの「削除」は無い。** あるのは `branch archive` で、
これは**一覧から隠す**操作（`branch list` は既定の `Archived = false` で返さない）。
コミットそのものはサーバに残るが、**このエディタからは元に戻せない**。
そのため利用者向けの語彙は「削除（アーカイブ）」で統一し、
選択ダイアログの補足と確認ダイアログの 2 段でその意味を伝える。

1. ブランチ一覧を取り直し、**現在のブランチと既定ブランチ**
   （`VersionControlSettings.DefaultBranchName`、既定は `main`）を除いた候補を並べる。
2. 選んだあとに確認ダイアログ（「元に戻せません」）。ヘッドレスでは必ず「いいえ」。
3. 実行後はコンボを取り直す（消えたブランチを残さない）。

除外規則は `BranchOperationRules` の 1 か所だけが持ち、
**パネル（一覧の絞り込み）とプロバイダ（実行直前の拒否）の両方が同じ規則を呼ぶ**。
片方だけに置くと「一覧に出るのに必ず失敗する」「一覧に出ないのに実行できる」が生まれる。
プロバイダ側の拒否は UI 以外から呼ばれたときの最後の砦なので消さないこと。
名前の比較は大文字小文字を区別しない（`Main` を消せてしまうより、消せない側へ倒す）。

### 4.5.7 節の開閉と、サーバ往復の抑え方

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

### 4.5.8 状態取得のモード（どこがサーバに聞くか）

| きっかけ | モード | 理由 |
|---|---|---|
| 保存・ファイル監視（自動） | `ScanOffline` | 通信しない。上段の未送信・未取得は「未確認」のまま |
| ヘッダーの更新 ⟳ | **`ScanOnline`** | 未送信・未取得の有無はサーバに聞かないと分からない。利用者が明示的に押したときだけ払う |
| 送信 / 最新を取得 / 競合解決 / ブランチ切替の直後 | **`ScanOnline`** | どれもサーバと往復した直後で、結果を上段へ正しく映すため |

### 4.5.9 自動更新

`WorkingCopyWatcher` がプロジェクトルートを監視し、変化があれば
`VersionControlService.RequestRefresh(ScanOffline)` を呼ぶ（デバウンスはサービス側が持つ）。
無視するのは `.lore/` `cache/` `save/` `logs/` `build/` `.backup/`。
**`.lore/` を無視しないと、Lore 自身の書き込みで無限ループになる**。

プロジェクトパネルにも FileSystemWatcher はあるが、`assets/` 限定で
内容の書き換え（LastWrite）を拾わないため相乗りできない。
あちらの `NotifyFilter` を広げるとファイルグリッドが毎回再構築されて挙動が変わる。

### 4.5.10 ドッキング

`ContentId = "version_control"`（**変更禁止**）。既定では Project / Output と同じ下段。
旧 `layout.xml` にはこのパネルが無いため `EnsureAnchorable` が補完するが、
既定の「最初に見つかったペイン」では左ペインに入ってしまうので、
`siblingContentId: "output"` を渡して**下段へ入れている**。

### 4.5.11 見た目の自動確認（PNG）

パネルは GUI を起動しないと見えないので、`editor/tests/VersionControlPanelPreviewProbe` が
**ウィンドウを出さずに**実物の XAML を組み立て、各状態を PNG へ書き出す。

```bash
dotnet run --project editor/tests/VersionControlPanelPreviewProbe -- --out <出力先>
# changes / conflicts / locks / history / clean / unavailable の 6 枚
```

PNG のほかに、**描画に写らない 2 つ**も同じ実行で確かめている（どれか落ちたら終了コード 1）:

- **「その他 …」メニューの項目**（`MenuShowWorkingCopy` / `MenuReleaseAllLocks` /
  `MenuMergeBranch` / `MenuArchiveBranch` / `MenuAccounts`）が揃っていて、文言が空でないこと。
  メニューはポップアップなので PNG に写らず、文言はコードで差し込んでいるため
  差し込み漏れが**空の項目**として黙って出る。
- **ブランチ選択ダイアログ**（`BranchPickerWindow`）が例外なく組み立てられること。
  共通スタイルのキー間違いは実際に開くまで分からないので、ここで 1 度組み立てておく（表示はしない）。

- 偽のプロバイダは `VersionControlService.UseProviderForVerification`（**検証専用の入口**）で据える。
  Lore にもサーバにも一切触れない。
- `Loaded` は「表示されたとき」に飛ぶ routed event なので、プローブが手で発火させて
  パネル本来の初期化経路を通す。
- プローブが起動アセンブリになるため、`App.xaml` の中の相対 pack URI
  （`resources/icons/Icons.xaml` など）の探索先を本体アセンブリへ向け直している
  （`ResourceAssemblyInitializer`）。向け直しに失敗すると
  「アイコンも共通書式も無い PNG」になるので、必ず警告を出すようにしてある。

---

## 4.6 ロックのゲート（保存・送信を実際に止める）

置き場: `editor/src/VersionControl/Locking/`。
**書き込み口ごとに判定を書かない**。すべて `LockGatekeeper` を通す。

| ファイル | 役割 |
|---|---|
| `LockEnforcementPolicy.cs` | 方針（`Enforce` / `WarnOnly`）の語彙 |
| `LockGateSettings.cs` | `editor/settings/locking.json` と、期限・寿命などの調整値 |
| `LockGateVerdict.cs` | 判定結果（行動 + 理由 + 文言）。保存用と送信用の 2 種 |
| `LockGatePolicy.cs` | **判定表そのもの（純関数）**。通信もログもしない |
| `AutoLockLedger.cs` | 「自動で取ったロック」の台帳（手動ロックと区別する唯一の記憶） |
| `LockGatekeeper.cs` | 実行役。照会・取得・解放・キャッシュ・提示の振り分け |
| `ILockGateNotifier.cs` | 提示の境界（WPF をこの層へ入れないため） |
| `Presentation/LockGateNotifier.cs` | 唯一の WPF 依存。止め＝モーダル、注意＝トースト |

### 4.6.1 判定表（保存ゲート）

上から順に見て、最初に当てはまった行で決まる。

| # | バージョン管理 | サーバ到達 | ログイン | 保持者 | 方針 | 結果 |
|---|---|---|---|---|---|---|
| 1 | 無し | - | - | - | - | 通す（無言） |
| 2 | 有り | ✕ | - | 不明 | 両方 | 通す＋注意 |
| 3 | 有り | ○ | 匿名 | なし | 両方 | 通す（無言） |
| 4 | 有り | ○ | 匿名 | 誰か | 両方 | 通す＋注意 |
| 5 | 有り | ○ | 済 | 自分 | 両方 | 通す（無言） |
| 6 | 有り | ○ | 済 | 不明 | 両方 | 通す＋注意 |
| 7 | 有り | ○ | 済 | 他人 | `Enforce` | **止める** |
| 8 | 有り | ○ | 済 | 他人 | `WarnOnly` | 通す＋注意 |
| 9 | 有り | ○ | 済 | なし | 両方 | その場で取得 → 下表へ |

9 行目の前に 2 つ例外がある（どちらも `LockGatekeeper` 側）:

- **まだ存在しないファイル**（新規保存・名前を付けて保存）は取りに行かず、そのまま通す。
  誰も持っていないと分かっている以上は守る相手がおらず、逆に「まだ無いパスのロック」を
  Lore が拒むと新規保存そのものが止まってしまう。
- **取得がサーバへ届かなかった**（`RequiresConnection` / 期限切れ）ときは
  「取れなかった」ではなく「確かめられなかった」として 2 行目と同じ扱いにする。

取得できたあと（9 行目の続き）:

| 取得の結末 | `Enforce` | `WarnOnly` |
|---|---|---|
| `Acquired` / `AlreadyMine` | 通す | 通す |
| `HeldByOther`（割り込まれた） | **止める** | 通す＋注意 |
| `HeldByUnknown` | 通す＋注意 | 通す＋注意 |
| `Failed` | **止める** | 通す＋注意 |

送信ゲート（`SubmitAsync` の前）は同じ語彙で、変更ファイルのうち
**保持者が `Other` のものが 1 件でもあれば止める**。止めるときは全件を並べる。
`Unknown` は止める理由に数えない。送信ゲートは**ロックを取りに行かない**
（送信は編集済みのものを送る操作で、いまさら編集権を取っても意味が無い）。

対象のファイル一覧は**その場で走査し直した結果**（`ScanOffline`。サーバ往復なし）を使う。
パネルに出ている一覧は最後に更新した時点のもので、そのあとに保存されたファイルが
抜けている。抜けたファイルは確かめられないまま送られてしまう。

### 4.6.2 なぜ「分からないときは通す」のか（設計の芯）

サーバに繋がらない・ログインしていない・所有者が `<unknown>` — どれも
「他の人のものだと**確かめられない**」状態。確かめられないことを根拠に保存を
止めると、サーバが落ちている間じゅう誰も作業できない。ロックは事故防止であって
作業を人質に取る仕組みではないので、通して注意だけ残す。

`<unknown>` を止めない理由はもう 1 つある。解除ボタンは自分のロックにしか
出ない（`VersionControlDisplay.CanRelease`）ので、`<unknown>` のロックで止めると
**誰にも外せない＝永久に保存できないファイル**が生まれる。

逆に「取得に失敗した」は *分からない* ではなく *取れていない* なので、
`Enforce` では止める。

### 4.6.3 匿名ではロックを取らない

匿名で `lock acquire` すると、サーバは所有者を `<unknown>` として記録する。
つまり 4.6.2 の「誰にも外せないロック」を自分で作ることになる。
自動ロックも保存ゲートの取得も、**ログイン中のときだけ**行う。

### 4.6.4 UI を固めない

保存経路は同期（`void`）なので、ゲートにも同期の入口（`EnsureWritable`）がある。
ただしサーバ往復には `LockGateSettings.GateTimeout`（5 秒）の**短い期限**を掛け、
超えたら「サーバに繋がらない」へ倒して通す。ロック操作の既定の期限
（`RemoteOperationTimeout` = 2 分）をそのまま使うと、サーバ無応答のとき
Ctrl+S でエディタが 2 分固まる。

連続保存（シーン本体 → シーン設定の自動保存）で往復を繰り返さないよう、
照会結果は `StatusCacheTtl`（3 秒）だけ使い回す。同じ注意も
`WarningRepeatInterval`（60 秒）は出し直さない（毎回出すと読まれなくなる）。

### 4.6.5 自動ロック（「編集中」）

シーン（`.scene`）とアクター（`.actor` / `.actor2d`）を**開いたとき**にロックを取り、
別のシーンへ切り替えたとき・タブを閉じたとき・プロジェクトを閉じたときに外す。
「送信」しても、開いているあいだは保持したまま（＝「編集中」の表示として使う）。

- 取得は**待たない**（`TrackOpenedDocument`）。シーンの読み込みをサーバ往復で
  止めない。取れなくても開けるし、保存しようとした時点でゲートが正しく判断する。
- ★**ログイン前に開いたぶんは「保留」に積み、ログインできたら取り直す**
  （`RetryPendingAutoLocks`。配線は `App.xaml.cs` の `AuthStateChanged`）。
  プロジェクトを開く → シーンを読む → 自動ログインが終わる、の順で進むため、
  これが無いと**起動時に開くシーンのロックはほぼ必ず取れない**。
  サーバへ届かなかったぶんも保留に戻る。
- 外すのは**自分が自動で取ったものだけ**。`AutoLockLedger` に載っているパスに限る。
  取得結果が `AlreadyMine`（もともと持っていた＝手で掛けた／前回の残り）なら
  台帳へ入れない。入れると、利用者が意図して掛けたロックが
  シーンを閉じた拍子に外れる。
- パネルから手でロックを掛けると台帳から外れる（自動 → 手動への格上げ）。
- まとめて外すのは `VersionControlService.Close()` の**先頭**。プロバイダを
  捨てたあとでは解放の呼び出し先が無く、他の人から見て掛かりっぱなしになる。
  待つのは `ReleaseWait`（3 秒）まで。外せなくても終了は止めない。
- パネルのロック節に「編集中」の印が出る（`LockRowItem.IsAutoHeld`）。

### 4.6.6 シーンロックと Lore のロックの関係（決定事項）

**シーンロックは従来どおり維持し、その上に Lore のロックを重ねる。**
片方の成否をもう片方の条件にしない。

| | シーンロック（`Scene/SceneLock.cs`） | Lore のロック |
|---|---|---|
| 守る範囲 | **同じ PC の中**（プロセス間） | **チームの中**（利用者間） |
| 判定材料 | PID・マシン名 | サーバに記録された所有者名 |
| 効く条件 | 常に（オフライン・未ログインでも） | ログイン中 かつ サーバへ繋がる |
| 効き方 | シーンを読み取り専用で開く | 保存・送信を止める |
| 対象 | `.scene` のみ | 作業コピー内の全ファイル |

同じ利用者が対話エディタと AI のヘッドレスエディタで同じシーンを開いた場合、
Lore から見ると**どちらも同じ所有者**なので「自分のロック」になり、区別できない。
これを止められるのは PID を見るシーンロックだけ。
逆に、Lore のロックをシーンロックの前提条件にすると、サーバが落ちている間は
機械内の多重編集が素通りしてしまう。だから独立に掛ける。

#### 置き場: `cache/editor/scene_locks/`（2026-09-19 に移動）

```
<プロジェクトルート>/cache/editor/scene_locks/<ルートからの相対パス>.lock
  例) assets/mainGame/MainGame.scene
      → cache/editor/scene_locks/assets/mainGame/MainGame.scene.lock
```

`cache/` は初期コミットから `.loreignore` 済みで、視点サイドカー（`cache/editor/view/`）や
VCS の状態（`cache/editor/vcs/`）と同じ「利用者別の状態」の置き場。
ロックも利用者別の状態なのでここへ置く。プロジェクトルートが未確定、または
シーンがルートの外にあるときだけ、旧位置（`<scene>.lock`）へ落ちる。

**なぜ移したか（2026-09-19 の事故）。**
旧位置はシーンの隣＝アセットの中だった。シーンを開いている間ずっと
`MainGame.scene.lock` が「未追跡の追加 1 件」として status に出て、
その状態でマージ前の確認（`EnsureNoUnsubmittedChangesForMergeAsync`）が
1 行メッセージだけで止まった。利用者はそれを「送信しろ」という指示と読み、
メッセージ "marge" でロックファイルを送信 → **マージは実行されないまま**
「test を main にマージしたのにアクタが反映されない」状態になった。
さらに悪いのは、リポジトリへ入ったロックは別マシンのものとして扱われるため、
クローンした共同作業者のエディタでそのシーンが**永久に読み取り専用**になること。

`.loreignore` の `*.lock` 行は**旧位置の保険として残す**
（`docs/vcs_lore.md` のセットアップ手順）。新位置は `cache/` ごと無視対象なので
二重に守られるが、旧位置のファイルが残っているプロジェクトのために消さない。
`ProjectPanelVisibilityRules` / `PackagingRules` の `.lock` 除外も同じ理由で残す。

#### 旧位置（`<scene>.lock`）の後始末

取得時に旧位置も読み、`SceneLock.ClassifyLegacyLock`（純関数・単体テスト済み）で
3 通りに分ける。**新位置とは規則が違う**点が要点。

| 旧位置のロック | 処置 | 理由 |
|---|---|---|
| 同じマシン・生きている別 PID | 尊重する（読み取り専用） | 移行前のエディタが実際に開いている |
| 同じマシン・無効（PID 死亡／自分／壊れて読めない） | **削除する** | 残骸。追跡されていれば「削除」の変更として現れ、送信すればリポジトリから消える |
| 別マシン（マシン名が空も含む） | 無視する（消さない） | バージョン管理経由の汚染である可能性が高い。尊重すると共同作業者が永久に読み取り専用になる。本物の共有フォルダかもしれないので削除もしない |

掃除は取得時（`TryAcquire`）だけでなく、**同じシーンの読み直しのたび**にも行う
（`SceneLock.SweepLegacy`。`MainWindow.FileOps.cs` の `ApplyCurrentScenePath`）。
シーンを開いたままブランチを切り替えると、追跡されてしまっている旧位置のロックが
作業コピーへ書き戻されるが、シーンのパスは変わらないのでロックは取り直されない。
自動再読込の流れで片付ければ、切り替えた直後に「削除」の変更として見える。

実機確認（2026-09-19）: 本番と同じ形（main に同マシン・死んだ PID のロックが追跡されている）の
使い捨てプロジェクトを実物のエディタで開き、①ロックが
`cache/editor/scene_locks/assets/scenes/main.scene.lock` に出来る ②旧位置の残骸が消える
③`lore status --scan` に `D assets/scenes/main.scene.lock` だけが出る（新しいロックは出ない）
ことを確かめた。

新位置のロックの stale 判定（`SceneLock.IsStale`）は従来どおりで、
**別マシンのロックは有効扱い**のまま。新位置はバージョン管理に入らないので、
そこに別マシンのロックがあるのは「プロジェクトフォルダをネットワーク共有している」
ときだけであり、その場合は尊重するのが正しい。

### 4.6.7 ゲートを通している書き込み口

| 対象 | ゲートを呼ぶ場所 |
|---|---|
| シーン上書き保存（`SAVE_SCENE`） | `MainWindow.Scene.cs` `ExecuteSave` |
| シーン別名保存（`SAVE_SCENE_AS`） | `MainWindow.Scene.cs` `ExecuteSaveAs`（**パスを差し替える前**） |
| シーン設定の自動保存 | 上の `ExecuteSave` を通るので自動的に含まれる |
| アクター保存（`SAVE_ACTOR`） | `MainWindow.Scene.cs` `ExecuteActorSave` |
| `.anim` | `Panels/AnimationTimelinePanel.xaml.cs` `OnSaveClip` |
| `.inputmap` | `InputMap/InputMapEditorWindow.xaml.cs` `OnSave` |
| `.sprite_mesh` | `Panels/SpriteRig/SpriteRigPanel.xaml.cs` `TrySave` |
| 地形（`layers` / `chunk_config` / `props`） | `Terrain/TerrainSettingsWindow.xaml.cs` `OnApply`（3 件まとめて判定） |
| `project_settings.json` | `ProjectSettings/ProjectSettingsWindow.xaml.cs` `OnSave` |
| スクリプト・シェーダー・テキスト | `Panels/ScriptEditorPanel.cs` `Save(DocTab)` |
| AI ツールの書き込み | `AI/Tools/EditorCommandExecutor.cs` `ExecuteWriteAssetFile` |
| 送信 | `Panels/VersionControlPanel.Operations.cs` `OnSubmitClick` |

ゲートは**書き手（`AnimClipIO.Save` など）の中ではなく、その操作の入口**に置いてある。
書き手は `void` で失敗を返せないこと、データ入出力のクラスへバージョン管理の依存を
持ち込みたくないこと、単体テストへリンクされている書き手があること（`SafeFileWriter` /
`AnimClipIO` / `ProjectSettingsData`）が理由。**新しい保存経路を足したら、
この表に 1 行足してゲートを通すこと。**

`SAVE_SCENE_COPY`（Play 用の一時シーン）は作業コピーの外へ書くのでゲートに掛からない
（パス変換が `null` を返し、1 行目の「バージョン管理下に無い」扱いになる）。

`EditorCommandExecutor` だけはモーダルを出さない。AI ツールは UI スレッド以外から
呼ばれ、誰も見ていない画面にダイアログを出しても閉じられないため、判定だけ使って
理由をツールの戻り値として AI へ返す。

### 4.6.8 設定（`editor/settings/locking.json`）

```json
{
  "enforcement": "enforce",
  "auto_lock_opened_documents": true
}
```

- `enforcement`: `"enforce"`（既定・止める）か `"warn_only"`（注意だけ）。
  綴り違いや未知の値は `enforce` へ倒す（黙って強制が外れないように）。
- `auto_lock_opened_documents`: 開いたシーン・アクターのロックを自動で取るか。
  切ると「編集中」の表示も出なくなる（手動ロックは従来どおり使える）。

### 4.6.9 ランタイム側は守られていない（既知の限界）

シーン・アクターの `.scene` / `.actor` を実際にディスクへ書くのは Rust のランタイム
（`runtime/src/engine/core/app_base/app/scene_save_ops.rs`）で、エディタは IPC で
依頼するだけ。ここで止めているのは**依頼を出す前**なので、
ランタイムへ直接 `SAVE_SCENE` を送る経路（あれば）はゲートを通らない。
現状エディタ以外に送り手は無く、実害は無い。

---

## 4.7 マージエディタ（競合の中身を並べて解決する）

2 択（自分の変更を残す / リモートを採用）はどちらかの作業を丸ごと捨てる。
**同じシーンに 2 人が別々のアクタを足した**ような競合では、どちらを選んでも
片方の作業が消えるので、中身を見て 1 ブロックずつ選べる画面が要る。
手本は Visual Studio の 3-way マージエディタ。

### 4.7.1 なぜ専用ウィンドウなのか

`.scene` / `.actor` / `.actor2d` は**スクリプトエディタで開くことを意図的に禁止**している
（`editor/config/text_editable_extensions.json` の `_note_forbidden`）。
手で編集させるための画面ではないので、
**競合の解決のためだけに中身を読めて、解決以外の編集経路を持たない**窓を別に用意した。

### 4.7.2 向きの定義（これだけは絶対にぶらさない）

Lore は競合したファイルへ diff3 形式の印を書き、脇に `~base` / `~mine` / `~theirs` を置く。

| 印 | このエディタでの呼び名 | sync のとき | ブランチのマージのとき |
|---|---|---|---|
| `<<<<<<<` 側 | **現在**（Current） | 自分の作業コピー | 取り込み先＝現在のブランチ |
| `\|\|\|\|\|\|\|` 節 | **元**（Base） | 分岐前の共通の祖先（**節ごと無いことがある**） | 同左 |
| `>>>>>>>` 側 | **取り込み元**（Incoming） | リモート | 取り込み元のブランチ |

**sync でもブランチのマージでもこの並びは同じ**（実機で確認済み）。
4.1 の `resolve mine` / `theirs` の入れ替わりとは**別の軸**なので混ぜないこと
（マージエディタは mine / theirs を一切使わない）。

印の判定は**先頭の記号 7 文字だけ**で行う。ラベル（`ours` / `original` / `theirs`）は
Lore の版や経路で変わり得るので見ない（`ConflictMarkerDocument.IsMarkerLine`）。

### 4.7.3 画面の構成

```
[ 取り込み元: リモート ／ 現在: 自分の変更 ]            ← 見出し（出どころ）
[ すべて取り込み元 ][ すべて現在 ][ すべて両方 ] [ 前の競合 ][ 次の競合 ] [ マージを確定 ][ キャンセル ]
┌───────────── 取り込み元 ─────────────┬───────────── 現在 ─────────────┐
│ ☑ 行を揃えて同期スクロール           │ ☑                              │
└──────────────────────────────────────┴────────────────────────────────┘
┌──────────────────────── 結果（編集できる）────────────────────────────┐
└───────────────────────────────────────────────────────────────────────┘
未解決のブロック 1 / 3　結果はチェックの操作で作り直されます（…）      ← 状態行
```

- 上段 2 面は**ブロック単位で行数を揃えて**並べる（`MergeAlignedView`）。
  短い側には斜線の詰め物が入る。行が揃っているので、縦スクロールの同期は
  オフセットを写すだけでよい。
- 色は「元に無い行＝緑（追加）」「元にあって消えた行＝赤（削除）」。
  削除行はその側の表示に**差し込んで**見せる（見えないと消えたことが分からない）。
  競合ブロック全体には薄い帯と左端の縦線を出す。色の定数は
  `editor/src/Theme/SeedColorTable.cs`（`MERGE_*`）で、コントラスト検査に載せてある。
- 各ブロックの先頭行の左マージンにチェックボックス（両側それぞれ）。
  既定は**両側とも挿入だけのブロック（4.7.4）だけ両方チェック**、
  元の行が書き換わっているブロックは未チェック（＝必ず利用者に選ばせる）。
- ウィンドウは**非モーダル**で、同じファイルは 1 つしか開かない
  （`MergeEditorWindows`）。2 つ開くと、後から確定した方が前の結果を巻き戻す。

### 4.7.4 「両方を取り込む」の可否と合成

#### 可否の条件は「両側とも **挿入だけ**」（2026-09-19 に実測で作り直し）

**「元（base）の節が空」を条件にしてはいけない。** 実物に近いシーンで確かめると、
2 人が配列末尾へアクターを足しただけでも diff3 は**直前のアクターの閉じ行を巻き込む**ので、
元の節が空にならない:

```
      },
<<<<<<< ours
      "components": []        ← 元にもある
    },                        ← ours が挿し込んだ
    { …AddedByMe… }
||||||| original
      "components": []
    }                         ← 元にもある
=======
      "components": []
    },
    { …AddedByOwner… }
>>>>>>> theirs
  ]
}
```

利用者の主用途がこの形なので、「元が空」を条件にすると
**「両方を取り込む」が一度も使えない**。正しい条件は

> **どちらの側も元の行を 1 行も消していない**（＝ 既存の `MergeLineDiff` の結果に
> `Removed` が 1 行も無い）

で、元の節が空なのはその特別な場合。片側でも元の行が消えている／書き換わっているなら
不可（並べると「同じアクタが 2 体」「同じキーが 2 回」になる）。
判定は `MergeTakeBothRule.IsUnionable`、位置表の計算は `MergeInsertionMap`。

#### 合成は連結ではなく「元を骨格にした union」

元の行が残っている以上、単純に両側を連結すると**元の行が 2 回出て JSON が壊れる**。
そこで元を骨格にして、両側の挿入を**元の同じ位置へ差し込む**:

```
元の i 行目の手前の挿入（両側）→ 元の i 行目 → … → 元の末尾より後ろの挿入（両側）
```

上の例の結果（sync）:
`"components": []` / `},` / `{` …AddedByOwner… `"components": []` / `},` / `{` …AddedByMe… `"components": []` / `}`
— アクターは 4 体、キーの重複なし。

#### 同じ位置に両側の挿入があるときの並び順

**既に共有されていた側を先** に置く。

| 進行中のマージ | 並び順 | 理由 |
|---|---|---|
| sync（最新を取得） | 取り込み元 → 現在 | 取り込み元＝サーバに既にある内容 |
| ブランチのマージ | 現在 → 取り込み元 | 現在＝取り込み先のブランチに既にある内容 |

こうしておくと「元々あった並びの後ろへ足された」形になり、次の差分が最小になる。
判断は `MergeComposer.Compose` の 1 か所（`MergeOrigin` で分岐）。

#### 挿入だけでないブロックを両方チェックしたら

パネルのボタンは押せない状態になる（ツールチップに理由）。ただし**マージエディタでは
利用者が手で両方にチェックできる**。その場合だけ従来どおりの連結へ落ちる
（元を残しようが無いため）。壊れていれば 4.7.5 の検査が書き戻す前に止める。

マージエディタの既定チェック（この形のブロックは最初から両方を採る）と
ツールバーの「すべて両方」の有効条件も、**同じ `IsUnionable` を使う**。
別々の条件にすると「既定で入っているのにボタンは押せない」が起きる。

### 4.7.5 書き戻す前の検査（2 段）

1. **印が残っていないこと**。★印が残ったまま解決を頼むと、Lore は
   `[Warn] Cannot resolve path with conflict markers still present` を出して
   **何もしないのに成功（rc=0）を返す**（実機で確認済み）。
   「解決しました」と言いながら何も変わらない、が最悪なので、
   `LoreProvider` は Lore を呼ぶ前に落とす。
2. **JSON として読めること＋同一オブジェクト内でキーが重複しないこと**。
   対象は `MergeValidation.JSON_EXTENSIONS`
   （`.json` `.scene` `.actor` `.actor2d` `.anim` `.mat` `.postfx` `.inputmap` `.sprite_mesh`）。
   構文は `System.Text.Json`（コメント・末尾カンマは許さない）、
   キーの重複は `Utf8JsonReader` で自前に数える。
   ★**構文解析だけでは重複を見つけられない**（`System.Text.Json` は後勝ちで読む）。
   「両方を取り込む」で最も起きやすい事故がこれで、放置すると
   「エディタでは開けるがゲームが読めない `.scene`」ができる。

`ResolveConflictsAsync`（2 択）と同じく、解決を頼んだあとは**必ず status を引き直し**、
頼んだファイルがまだ競合なら失敗にする。全部片付いたときだけマージをコミットする
（`LoreProvider.FinalizeResolveCore` に 1 か所へまとめてある）。

★この引き直しで見る status の形（2026-09-19 実測、v0.9.0）:

| 段階 | `flagConflict` | `flagConflictUnresolved` | `mine` / `theirs` | エディタの状態 |
|---|---|---|---|---|
| 未解決 | true | **true** | false / false | `Unresolved` |
| `resolve <path>`（中身のまま）直後 | true | false | false / false | **`ResolvedWithContent`** |
| `resolve theirs` / `mine` 直後 | true | false | 片方 true | `ResolvedKeepMine` / `ResolvedTakeRemote` |
| マージのコミット後 | （行が消える） | | | `None` |

「競合フラグだけ立って他が全部偽」は**解決済み**であって未解決ではない
（`LoreStatusTranslator.ToConflictState`）。ここを未解決へ倒すと、マージエディタと
「両方を取り込む」の直後に「まだ競合している」と誤判定してマージのコミットが永久に行われない
（実機で再現した不具合。テスト `ResolvedWithContentIsDetected` /
`ProviderTakeBothResolvesAndCommits` が実測の形で固定している）。

さらに、**書き込む前に「まだ競合しているか」を確かめる**。マージエディタは非モーダルなので、
開いたままパネルの 2 択で解決されていることがある。その状態で確定すると
解決済みのファイルを古い合成結果で黙って上書きしてしまう
（`LoreProvider.EnsureStillConflicted`）。

### 4.7.6 改行と BOM は保持する

競合の解決は**ファイルを丸ごと書き戻す**操作なので、触っていない部分の改行を変えると
バージョン管理上は「全行が変わった」ことになる。
`MergeLine` は**行ごとに自分の改行**を持ち、BOM の有無は書き戻す直前に
実ファイルから読み直して合わせる（`MergeFileText`）。
CRLF と LF が混在したファイルでも、触っていない行はバイト単位でそのまま戻る。

### 4.7.7 脇ファイル（`~base` / `~mine` / `~theirs`）の後始末

**要らない。** 解決した後も残るが、**マージのコミット時に Lore が消す**（実機で確認済み）。
status にも出ないので、エディタ側から消さないこと。

### 4.7.8 パネルからの入口

競合の 1 行に並ぶ手段（左から）:

| ボタン | 何をするか | 使えない条件 |
|---|---|---|
| 比較… | マージエディタを開く（**行のダブルクリック**でも同じ） | バイナリ・印が無い・印が壊れている（理由をダイアログで出す） |
| 自分の変更を残す / リモートを採用 | 従来の 2 択 | — |
| 両方を取り込む | 両側の追加を元へ差し込んで残す（union） | 元の行を消している側があるブロックがある／バイナリ |
| 編集した内容で解決 | いまのファイルの中身のまま解決済みにする | 印が残っている（ツールチップに行番号） |

「すべて両方を取り込む」は**全件で成り立つときだけ**押せる。1 件でも無理なものが
混ざったまま押せると、まとめて失敗して「どれが駄目だったのか」が分からなくなる。
`ResolveConflictsTakingBothAsync` も、1 件でも弾かれたら**何も書き込まない**
（半端に書き換わった作業コピーを作らない）。

### 4.7.9 開いているシーンが競合したとき（読まない・上書きしない）

シーンを開いたままマージや「最新を取得」で競合すると、`.scene` に印が入って JSON として
読めなくなる。何もしないと、①自動再読込が印つきのファイルを読みに行って
「シーンの読み込みに失敗しました」のダイアログが出る（ランタイムは元のシーンを保持するので
表示はマージ前のまま）②その状態で Ctrl+S すると、メモリ上の古いシーンで印つきのファイルを
上書きし、相手側の変更が黙って消える。そこで `Scene/SceneConflictGuard.cs`
（行頭の `<<<<<<<` の有無だけを見る。プロバイダやサーバには依存しない）で次のようにしている。

| 場面 | 挙動 |
|---|---|
| 自動再読込・手動の再読込 | 見送る。上部のステータスに「シーンが競合中のため再読込しません」 |
| 上書き保存（Ctrl+S / AI からの保存） | 拒否してダイアログで理由を出す。**別名で保存は止めない**（逃がす手段） |
| 競合を解決した後 | 解決でファイルが書き換わるので、`SceneAutoReloader` がそのまま読み直す |

実機確認（2026-09-19）: 使い捨てプロジェクトのシーンを開いたまま CLI でブランチをマージして競合させ、
ログに「競合中のため再読込しません」だけが出て `LOAD_ERROR` が出ないこと、
解決後に `SCENE_LOADED` →「シーンを再読込しました」が出ることを確かめた。

**競合を試す手順**（一人・作業コピー 1 つで可）: ブランチを作る →
そのブランチと main の両方で**同じアクターを動かして**それぞれ送信 → main でそのブランチを
右クリックしてマージ。両方で**新しいアクターを追加**すれば「両方を取り込む」を試せる。

### 4.7.10 ファイルの置き場

| 役割 | 置き場 |
|---|---|
| 純粋ロジック（WPF / LoreVcs 非依存） | `editor/src/VersionControl/Merge/` |
| ウィンドウ・描画・チェック余白 | `editor/src/Panels/VersionControl/MergeEditor/` |
| 単体テスト | `editor/tests/VersionControlTests/MergeEditorTests.cs` |

ウィンドウのコンストラクタは
`(絶対パス, MergeOrigin, 取り込み元の表示名, 現在の表示名, 確定時のコールバック)` で、
**Lore を一切知らない**。サンプルの印つきファイルを置いて開けば、
バージョン管理下に無くても動きを確かめられる。

**ヘッドレス起動でも窓は出す**（非モーダルなので UI スレッドを止めない）。
`EditorDialogs` が抑止するのは返事を待つモーダルだけで、この窓は対象にしない。
AI の実機確認は「使い捨て loreserver + 競合状態の使い捨てプロジェクト」を
ヘッドレスのエディタで開き、UI Automation で『比較…』→『すべて取り込み元』→『マージを確定』を
押して、Lore 側にマージのコミットができることまで確かめた（2026-09-19）。

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

- 競合選択の対応表（**sync / branch merge の両方**・両方向・往復）と、
  進行中のマージの出どころの記録・消去（`cache/editor/vcs/merge_origin`）
- ブランチのマージ（競合検出が戻り値でなく status であること・`NeedsSync` /
  `RequiresConnection` / 自分自身の拒否・自動コミットの文言）
- ブランチの削除（アーカイブ）の拒否規則（現在のブランチ・既定ブランチ・
  設定で差し替えた既定ブランチ）と、候補の絞り込みが同じ規則を使っていること
- 空コミット防止（staged 0 件で `commit` を呼ばないこと）
- stage が空でも手元が進んでいれば push すること（解決後のマージを取り残さない）
- push 拒否 → `NeedsSync`、無関係な失敗は `Failed` のまま
- sync 成功 + 競合あり → `Conflicted`
- LOCAL / REMOTE ブランチの統合（順序・現在ブランチ・未知の location）
- `<unknown>` 所有者の扱い、二重取得、行が返らないパス
- **ロックのゲートの判定表**（4.6.1 の全行 + 取得後 + 送信 + 設定 + 台帳。`LockGateTests.cs`）
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
改名（`stage move`）→ **フォルダごとの移動**（4.7）→ **履歴のメタデータ**（4.6）→
**ブランチのマージ**（別ブランチで追加したファイルが取り込まれる）→
**マージの競合で `KeepMine` が現在のブランチ側・`TakeRemote` が取り込み元側になること**
（4.1 の向きの実機確認。ここが崩れると利用者の変更が黙って消える）→
**削除（アーカイブ）したブランチが一覧から消えること**・**現在のブランチは削除できないこと**。

> 実行前に、前回の中断で残った `loreserver.exe` が居ないか確かめること。
> ポートが固定（41357 / 41359）なので、残っていると新しい fixture がそれに繋いでしまい、
> 別のストアの上でテストが走って原因不明の失敗になる。

履歴メタデータのテストは、失敗すると**観測した生のキー・値をそのまま出す**。
Lore の版が上がってキー名が変わったら、その出力を見て
`LoreRevisionMetadataTranslator` の候補配列を直せばよい。

### 7.2.1 ロックのゲートの実サーバ結合（`AccountsTests` 側）

ロックの強制は「サーバが所有者名を返すこと」に依存するので、
偽物では再現できない。認証つきのサーバを立てる
`editor/tests/AccountsTests` の結合群へ 2 件足してある。

```powershell
cd tools/seed-loreserver ; cargo build     # 先にサーバを建てる
$env:SEED_ACCOUNTS_TEST_SERVER = "1"
dotnet run --project editor/tests/AccountsTests
```

- `[結合2] A がロック中は B の保存ゲート・送信ゲートが止まる`
  … A が掛ける → B の `DecideForWriteAsync` が `Block`（保持者 `Other`・名前は A、
  文言に A の名前が入る）→ B の `DecideForSubmitAsync` も `Block`（内訳にパスが並ぶ）
- `[結合2] A が解放すると B は保存・送信でき、自動ロックも往復する`
  … A が解放 → B の保存ゲートが**その場でロックを取って**通る（`Acquired`、
  サーバ上の所有者が B）→ 送信ゲートも通る → `ReleaseAllTracked` でサーバ上も外れる →
  `TrackOpenedDocument` で取れる → `ReleaseTrackedDocument` で外れる

プロバイダと資格情報は `VersionControlService.UseProviderForVerification` と
`CredentialProvider`（どちらも検証用の差し込み口）から据える。
照会には数秒の寿命があるため、段の切り替えで
`LockGatekeeper.ResetForVerification()`（テスト専用）を呼んで捨てている。

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
- `editor/src/App.xaml.cs` の `OnStartup` で
  `LockGatekeeper.Log` と `LockGatekeeper.Configure(EditorPaths.SettingsDir)`
  （ロックの方針を読む）。提示の窓口（`Notifier`）は `MainWindow` の構築時に差し込む。
- `editor/src/App.xaml.cs` の `OnExit` で
  `VersionControlService.Close()` → `LoreShutdownGuard.Shutdown()`。
  `Close()` の先頭で自動ロックをまとめて解放する（4.6.5）。
- `VersionControlPanel` が `StatusChanged` を購読し、`Dispatcher` へ移して反映する。
- パネルの操作はすべて結末がエディタのログへ 1 行残る
  （`RunAsync` → `LogOperationResult`。`[VCS] <操作名>: <結末> — <メッセージ>`。
  診断メッセージは先頭 3 件まで）。**自動更新（Refresh）の成功だけは出さない**
  （数秒おきに積もって「利用者が押した操作」が埋まるため。失敗は出す）。
  パネルの 1 行メッセージは次の操作で消えるので、後から経緯を追える場所はログだけ
  ── 2026-09-19 の事故で実際に「何を押して何が起きたか」を再現できなかった。
- `WorkingCopyWatcher` が作業コピーの変化を見て `RequestRefresh` を呼ぶ（4.5.4）。
- 保存経路（11 か所）と「送信」が `LockGatekeeper` を通る（4.6.7 の表）。
- シーン・アクターを開くと自動でロックを取り、閉じると外す
  （`MainWindow.FileOps.cs` の `AcquireSceneLock` / `ReleaseSceneLock` /
  `OnActorFileOpened` / `CloseActorTab`）。
- プロジェクトパネルの**改名（インライン編集）とドラッグ＆ドロップ移動**の成功直後に
  `NotifyMovedAsync` を呼ぶ（`ProjectPanel.NotifyVersionControlMoved`）。
  素のファイル移動は Lore 上で「削除 + 追加」になり履歴が切れるため、この通知が要。
  削除・新規作成は走査（`ScanOffline`）で拾えるので通知は要らない。

まだ繋いでいないもの:

- 保存経路（`safe_write` 完了）からの `NotifyChangedAsync`。
  今は `WorkingCopyWatcher` が拾って `ScanOffline` で取り直しているので一覧には出るが、
  保存のたびに走査が走る。保存経路から直接 `file dirty` を打てば `TrackedOnly` で済む。
