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
| **`REVISION_HISTORY_ENTRY` にメッセージ・作者・日時が無い** | 4.6（未解決。バックログ） |

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

### 4.6 履歴にメッセージ・作者・日時が無い（未解決）

`LoreRevisionHistoryEntryEventDataFFI` は `Revision` / `RevisionNumber` / `Parent` しか
持たない（リフレクションで確認）。コミットメッセージ等はリビジョンのメタデータ
（`Lore.RevisionMetadataGet`）として 1 件ずつ引く必要がある。
現状 `RevisionInfo.Author` / `Message` / `TimestampUtc` は空・0 のまま。
型は残してあるので、メタデータ取得を足せばそのまま埋まる。

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
改名（`stage move`）。

**この結合テストが実際に 5 件のバグを見つけた**（4.3 / 4.4 / 4.5 と、
解決後のマージが取り残される問題）。境界の設計だけでは防げない種類なので、
Lore を触る変更を入れたら必ず一度は回すこと。

---

## 8. 現状の配線

パネルがまだ無いので、配線は最小限。

- `editor/src/Project/ProjectContext.cs` の `Open`
  （プロジェクトを開く 2 経路が合流する唯一の場所）で
  `VersionControlService.Open(paths.RootDir)` を呼ぶ。
  検出結果がエディタのログへ 1 行出る:
  `バージョン管理: Lore（<root> remote=... identity=...）` または `バージョン管理: なし（<root>）`
- `editor/src/App.xaml.cs` の `OnExit` で
  `VersionControlService.Close()` → `LoreShutdownGuard.Shutdown()`。

まだ繋いでいないもの（パネル側と合わせて次段で行う）:

- 保存経路（`safe_write` 完了）からの `NotifyChangedAsync` / `RequestRefresh`
- プロジェクトパネルの作成・削除・リネームからの `NotifyMovedAsync`
- `StatusChanged` の購読と `Dispatcher` への移送
