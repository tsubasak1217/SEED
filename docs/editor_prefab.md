# プレハブ（アクタファイル参照リンク）とシーンへの反映

正典コード:

| 場所 | 役割 |
| --- | --- |
| `runtime/src/engine/core/app_base/app/prefab_ops.rs` | 再展開・版ずれ検出のコア。**設計方針はこのファイル冒頭のコメントが正典** |
| `runtime/src/engine/structs/objects/actor/mod.rs` | `prefab_source` / `prefab_hash`（`Actor` と `ActorData` の両方） |
| `runtime/src/engine/core/app_base/ipc.rs` | `PREFAB_*` コマンドのワイヤ形式 |
| `editor/src/MainWindow.Prefab.cs` | 保存時の自動反映・版ずれバナー・設定トグル |
| `editor/src/AI/Tools/EditorCommandExecutor.Visual.cs` | MCP `prefab_reapply` の実装 |
| `runtime/src/engine/core/app_base/app/prefab_live_patch/` | **Play 中**の当て直し・書き戻し（8 章。設計は `mod.rs` 冒頭） |
| `editor/src/Reload/PrefabPlayReapplyQueue.cs` | Play 中に変わったプレハブを覚えて停止後に Edit へ反映する判定（純粋。テスト `editor/tests/PrefabPlayReapplyTests`） |

---

## 1. プレハブとは何か

`.actor` / `.actor2d` ファイルから作ったアクタは、その参照パスを
`prefab_source`（`assets://` 仮想パス）としてルートに持つ。これが Unity の
プレハブインスタンスに相当する。

- **`prefab_source` を持つのはインスタンスのルートだけ**。子アクタは常に持たない。
- `.actor` ファイル自身（テンプレート）には `prefab_source` を書き出さない
  （自己参照・二重リンクの混入を防ぐため）。
- ネストプレハブは **1 段のみ**展開する（インスタンスの配下へは降りない）。

インスタンスが生まれる経路は 5 つあり、いずれも `prefab_source` と
後述の `prefab_hash` の両方を設定する:

| 経路 | 実装 |
| --- | --- |
| プロジェクトパネルから `.actor` をビューポートへドロップ | `actor_ops.rs`（3D / 2D の 2 か所） |
| ヒエラルキーからプロジェクトへドラッグして書き出し（＝プレハブ化） | `actor_ops.rs::handle_export_actor` |
| ロジック配置（配置元＝アクタファイル） | `logic_placement_ops.rs` |
| 地形のアクタ散布（`kind=actor` プロップ） | `terrain_scatter_actor_ops.rs` |
| **Play 中のスクリプトの `GameObject.Instantiate`**（ScreenStack が積む画面など。2026-10-02〜） | `script_scene_ops.rs::apply_script_instantiate` |

5 番目は Play 中だけの経路で、根に実行時だけの印 `Actor::spawned_by_script`（保存されない）も付く。
Play 中の当て直し（8 章）がスクリプトの積んだ画面も対象にできるようにするため、2026-10-02 に
`prefab_source` / `prefab_hash` を付けるようにした（それまでは付いておらず、ヒエラルキーでもプレハブに見えなかった）。

アクタファイル化（`handle_export_actor`）と Play 中の書き戻し（8 章）は `actor_ops.rs::prepare_prefab_template` を共有し、
根の `prefab_source` と `prefab_hash` を書かない（`prefab_hash` を外すのは 2026-10-02 から。それまではインスタンスを
書き出すと取り込み済みの版がファイルへ混ざっていた）。

---

## 2. いつシーンへ反映されるか（伝播の規則）

### 大原則: 自動で上書きはしない

**ランタイムは、シーンロード時にもプレハブ保存時にも、自分の判断で再展開しない。**
再展開は必ずエディタからの明示コマンドで起きる。

これは過去のデータ損失事故への対策である。かつては
(1) シーンロードのたびに全インスタンスを再展開し、
(2) `.actor` を保存した瞬間に同じ参照パスの全インスタンスを再展開していた。
その結果、シーン側でインスタンスに加えた変更（コンポーネントの追加・値の変更・
子の追加）が**何の予告もなく消えていた**。以来「シーンに保存された内容を正とする」
方針に切り替えてある。

### 現在の 4 つの入口

| 操作 | IPC | 対象 | Undo |
| --- | --- | --- | --- |
| プレハブを保存したときの自動反映（既定オン） | `PREFAB_REAPPLY_PATH:{path}` | その `.actor` を参照する全インスタンス | 1 操作 |
| 版ずれバナーの［更新する］ | `PREFAB_REAPPLY_PATH:{path}` × 対象パス数 | 同上 | パスごとに 1 操作 |
| ヒエラルキー右クリック「プレハブから更新」 | `PREFAB_REAPPLY:{actor_dfs}` | そのアクタ配下のインスタンス | 1 操作 |
| ヒエラルキー右クリック「シーン内の全プレハブを更新」 | `PREFAB_REAPPLY_ALL` | シーン内の全インスタンス | 1 操作 |

いずれも**破壊的**（インスタンス側の変更がファイル内容で上書きされる）。
そのぶん、以下の 3 条件で「黙って消える」状況を作らないようにしてある:

1. 自動反映は設定でオフにできる
2. すべて Undo（Ctrl+Z）1 操作で戻せる
   （ただしアクタータブを表示中に再展開したときは、シーンのタブへ移ると `SET_ACTIVE_WORLD_LINE` が履歴を作り直すので戻せない。
   トーストはそのときだけ Ctrl+Z を約束しない文言にし、Play 停止後の自動の反映はバナーに落とす。8 章「エディタの流れ」。2026-10-03）
3. 反映件数を必ずトーストで知らせる

再展開で**維持される値**はルートの Transform / CanvasTransform・`name`・`active`・
`visible`・`world_line`・`prefab_source`。それ以外（子ツリー・コンポーネント）は
ファイル内容で丸ごと差し替わる。

### プレハブ保存時の自動反映

アクタータブで `.actor` を保存（Ctrl+S → `SAVE_ACTOR`）すると、保存成功
（`SAVE_OK`）に続けてエディタが `PREFAB_REAPPLY_PATH` を送る。

```
エディタ            ランタイム
  │ SAVE_ACTOR:{path}   │
  │ ──────────────────> │  .actor を書き出す
  │ <────────────────── │  SAVE_OK
  │ PREFAB_REAPPLY_PATH:{path}
  │ ──────────────────> │  そのパスのインスタンスだけ再展開（Undo 1 操作）
  │ <────────────────── │  PREFAB_REAPPLY_DONE:{件数},{仮想パス}
  │                     │  HIERARCHY / ACTOR_COMPONENTS / SCENE_MODIFIED
  │ トースト＋シーンを未保存扱いに
```

- 反映結果はシーンを保存して初めて残るので、エディタ側でシーンを未保存（`*`）にする。
- 0 件（そのプレハブのインスタンスがこのシーンに無い）のときは何も表示しない。
- 設定: **「表示 > シーン > プレハブ保存時にシーンのインスタンスへ自動反映」**
  （`EditorPreferences.PrefabAutoPropagateOnSave`、既定オン）。
  インスタンスごとに手を入れて使い分けている場合はオフにする。

---

## 3. 版ずれ検出（シーンを開いたとき）

### `prefab_hash` — 「シーンへ取り込んだプレハブの版」

インスタンスのルートは `prefab_source` に加えて `prefab_hash` を持つ。
これは**再展開・生成のときに読んだ `.actor` の内容ハッシュ**（16 桁の 16 進数）で、
「このインスタンスはプレハブのどの版から作られたか」を表す。

- アルゴリズムは **FNV-1a 64bit**（`prefab_ops::content_hash`）。暗号強度は不要
  （衝突しても「更新に気付かない」だけで破壊は起きない）で、外部クレートを増やさず
  C# 側でも数行で再現できることを優先した。
- 対象は `asset_fs::read_string` を通したテキスト（＝ BOM の有無に影響されない）。
- `.scene` へは `prefab_hash` として書き出す。`None` のときは書き出さない
  （旧 `.scene` とのバイト互換を維持）。

### シーンロード後の流れ

シーンの読み込み完了（`SCENE_LOADED`）を受けて、エディタが `PREFAB_STATUS` を投げる。
これは**読み取り専用**のコマンドで、シーンには一切触れない。

応答 `PREFAB_STATUS:{json}` は参照パスごとの集計:

```json
[{"source":"assets://zukan/actors/ZukanCard.actor",
  "total":4,"stale":4,"unknown":0,"missing":false}]
```

| フィールド | 意味 |
| --- | --- |
| `total` | その参照パスを持つインスタンスの総数 |
| `stale` | 取り込んだ版がファイルの現在の版と食い違う数（＝更新が来ている） |
| `unknown` | `prefab_hash` を持たない数（版が不明。**バナーの対象にしない**） |
| `missing` | 参照先ファイルが読めない（消された・移動した） |

`stale` が 1 件以上のときだけ、上部に非モーダルのバナーを出す:

```
プレハブが更新されています: ZukanCard.actor（インスタンス 4 個）  [更新する] [無視]
```

- **［更新する］** — 検出した参照パスごとに `PREFAB_REAPPLY_PATH` を送る。
- **［無視］** — バナーを閉じるだけ。シーンには触れない。
- ロード処理自体は一切変わらない（自動上書きはしない）。

### 旧シーン（`prefab_hash` が無いインスタンス）の扱い

版が不明なので **stale とは数えず、バナーも出さない**。勝手に「更新しますか」と
促すと、その promptに従ったユーザーがインスタンス側の変更を失うことになるため。
一度でも再展開（自動反映・バナー・右クリックのいずれか）を通せば `prefab_hash` が
書き込まれ、次回から版ずれ検出の対象になる。

---

## 4. IPC 一覧

| コマンド | 引数 | 応答 | 種別 |
| --- | --- | --- | --- |
| `PREFAB_REAPPLY:{actor_dfs}` | アクタの DFS ID | （`HIERARCHY` 等の通常通知のみ） | 変更 |
| `PREFAB_REAPPLY_ALL` | なし | 同上 | 変更 |
| `PREFAB_REAPPLY_PATH:{path}` | 絶対パス or `assets://` 仮想パス | `PREFAB_REAPPLY_DONE:{件数},{仮想パス}` | 変更 |
| `PREFAB_STATUS` | なし | `PREFAB_STATUS:{json}` | 読み取り |
| `UNLINK_PREFAB:{actor_dfs}` | アクタの DFS ID | （通常通知のみ） | 変更 |
| `PREFAB_LIVE_PATCH_PATH:{path}` | 絶対パス or `assets://` 仮想パス | `PREFAB_LIVE_PATCH_DONE:{件数},{仮想パス}`（0 件でも返す）／`PREFAB_LIVE_PATCH_ERROR:{理由}` | 変更（**Play 中だけ**。8 章） |
| `PREFAB_WRITE_BACK:{actor_dfs}` | インスタンスの根の DFS ID | `PREFAB_WRITE_BACK_DONE:{当て直した件数},{仮想パス}`／`PREFAB_WRITE_BACK_ERROR:{理由}` | ファイルを上書き（**Play 中だけ**。8 章） |

`*_DONE` の仮想パスにはカンマが入り得るので、エディタは最初のカンマだけで区切る。

`HIERARCHY:` の各ノードには `prefab_source`（非プレハブは `null`）と
`prefab_hash`（版が不明なら `null`）が載っている。

---

## 5. MCP から操作する

```
seed_hierarchy()
  → 各ノードに prefab_source / prefab_hash が入っている

seed_prefab_reapply(prefab_path: "assets://zukan/actors/ZukanCard.actor")
seed_prefab_reapply(actor_dfs_id: 12)
seed_prefab_reapply(name: "ZukanCard0")
seed_prefab_reapply(all: true)
```

変更系なので束縛インスタンス（`seed_launch` / `seed_attach`）が必要。
`seed_batch` からは `{"cmd":"prefab_reapply", ...}` で呼べる。

---

## 6. リンクを切りたいとき

ヒエラルキー右クリックの「リンク解除」（`UNLINK_PREFAB`）で `prefab_source` を
外すと、以後そのアクタは再展開・自動反映・版ずれ検出のいずれの対象にもならず、
独立したツリーとしてシーンに保存・維持される。
「このインスタンスだけは元のプレハブと別物として育てたい」ときはこれを使う。

---

## 7. プレビュー（保存されないインスタンス。2026-10-02）

ヒエラルキー右クリックの「プレハブをプレビュー」・`ScreenStack` / `ModalHost` のインスタンスの案内で、プレハブを
**シーンへ保存されないプレビュー**として差し込める（正典: [editor_screen_preview.md](editor_screen_preview.md)）。
インスタンスとの違い:

| | インスタンス（§1） | プレビュー |
|---|---|---|
| シーンへの保存 | される | **されない**（`.scene`・`.actor` の書き出し・コピーから除く） |
| `prefab_source` / `prefab_hash` | 根が持つ | 持たない（印 `editor_preview` を根が持つ。再展開・版ずれ検出の対象外） |
| シーン上での編集 | できる（シーンの内容を正とする） | **できない**（インスペクタは読み取り専用。直すのはプレハブ） |
| プレハブを保存したとき | 設定がオンなら自動反映（`PREFAB_REAPPLY_PATH`。シーンを未保存にする） | 常に作り直す（`PREVIEW_REFRESH_PATH`。シーンを未保存にしない） |
| Play | そのまま動く | 開始で外れ、止めると戻る |

---

## 8. Play 中のホットリロード（当て直し・書き戻し。2026-10-02）

正典コード: `runtime/src/engine/core/app_base/app/prefab_live_patch/`（設計は `mod.rs` 冒頭）・
`editor/src/MainWindow.Prefab.cs`・`editor/src/Reload/PrefabPlayReapplyQueue.cs`。

> **重要（2026-10-03）**: **書き戻し（`PREFAB_WRITE_BACK`）はエディタで既定無効**にしてある
> （`editor_preferences.json` の `prefab_write_back_enabled`、既定 false。メニューとボタンは出るが押すと案内だけ）。
> レビュー `docs/reviews/2026-10-03_code_review.md` の #1〜#4 で、(1) ScreenStack が枠へ移したファイル由来の画面
> （置いてある根・`Push(GameObject)` の中身）を刈って書く、(2) 古い版のインスタンスからプレハブを巻き戻す、
> (3) 対象を DFS 番号だけで決めるので確認の間に木がずれると別のファイルを上書きする、(4) スクリプトが足した
> コンポーネント・スクリプト・表示の切り替えをそのまま焼く、と分かったため。根本の直し（元の版 `base_cache` との
> 差分だけを今のファイルへ当てて書く・`PREFAB_WRITE_BACK:{dfs},{仮想パス}` で対象を確かめる・実行時に足した
> スロットに印を付ける）が入るまで、承知のうえで使う人だけ有効にする。当て直し（`PREFAB_LIVE_PATCH_PATH`）は影響なし。

### なぜ丸ごとの再展開ではいけないか

2 章の再展開（`PREFAB_REAPPLY_PATH`）はインスタンスを作り直す。Play 中にこれをやると、スクリプトの CLR インスタンスも
作り直されて **OnStart が走り直し、画面が初期化される**（進行・入力中の値・スクリプトが積んだ行が消え、
スクリプトが持っていたハンドルも無効になる）。そこで Play 中は「動いているインスタンスを壊さず、ファイルが変えた
ところだけを当てる」別の経路を使う。

### 当て直し（`PREFAB_LIVE_PATCH_PATH:{path}`）

Play 中のシーン（世界線 0）で `prefab_source == path` のインスタンス**全部**（スクリプトの `Instantiate` で
Play 中に作られたものを含む）へ、ファイルの今の中身を当てる。インスタンスの中に入れ子になった同じパスのインスタンスへは降りない。

| 対象 | 扱い |
| --- | --- |
| ノード | ルートからの**名前のパス**で突き合わせる（同名の兄弟は出現順）。ファイルに増えたノードは対応する親の下へ組み立てる（スクリプト込み。その部分木だけ OnStart が走る）。ファイルから消えたノードは破棄する（OnDestroy が走る）。並びはファイルの順に揃える |
| スロット | （種別, 同種内の順番）で突き合わせ、`field_edit::apply_component_data_in_place`（Undo の復元と同じ関数）で**その場で**差し替える。`NeedsRebuild` のスロットだけ作り直す。値が今と同じスロットは触らない（アニメーター・音などの実行中の状態を作り直さないため） |
| スクリプト | **CLR インスタンスを作り直さない・`[SerializeField]` も触らない**（Play 中にスクリプトが書いた値を正とする）。型名の並びが変わったノードだけ、そのノードのスクリプトを全部作り直す（消して、ファイルの並びで末尾へ作る） |
| 根 | 名前・active・visible・Transform / CanvasTransform は触らない（配置側の持ち物。再展開が維持する値と同じ）。スロットと子は当てる |
| 2D の子 | CanvasTransform も当てる（欄ごとに合成） |
| 3D の子 | Transform は当てない（ワールド空間で持つため）。新しく作るノードだけ根の配置へ移す（再展開と同じ delta）。モデルのインスタンス行列は今の値を残す |
| スクリプトが生成した部分木（`spawned_by_script`）・プレビュー | 突き合わせない。消さず・触らず・直前にあったファイル由来の兄弟の後ろに付いていく |

**3 方向と 2 方向**: インスタンスが作られた版（`prefab_hash`）のファイルの中身が分かれば、「元の版 → 新しい版」で
変わった欄だけを当てる（文字の大きさを変えたなら、スクリプトが書いた本文はそのまま）。ノード・スロットも
「ファイルが足した／消した」だけを作る／消し、実行中に消されたノードは作り直さない。元の版は Play の間だけ
メモリへ控える（`base_cache.rs`。Play の開始・スクリプトの Instantiate・当て直し・書き戻しの直前）。
控えが無いインスタンス（`prefab_hash` の無い旧シーン・シーンのインスタンスが古い版のまま）は 2 方向
（ファイルの値を当てる。ただし消すことはしない）になる。ログ `[PrefabLivePatch] ...（3 方向 N / 2 方向 M）` で分かる。

当て直しの後は描く理由（`RedrawReason::HotReload`）を立てる（レイアウト・テキストは次のフレームで測り直す）。
`HIERARCHY` と選択中なら `ACTOR_COMPONENTS` を送る。**Undo は積まず `SCENE_MODIFIED` も送らない**
（Play の世界は停止で Play 前の写しへ戻る）。Edit で届いたら `PREFAB_LIVE_PATCH_ERROR` で断る。

### 書き戻し（`PREFAB_WRITE_BACK:{actor_dfs}`）

Play 中にインスペクタで詰めた見た目は、停止で消える。インスタンスの根を選んで、その部分木を `.actor` へ書き戻す。

- 直列化はアクタファイル化と同じ（`to_data` → `prepare_prefab_template` → `actor_file::save`。形式の版の刻印・
  `.backup/` への世代退避・プレビューの濾過）。入れ子のプレハブは 1 段の参照のまま。
- **スクリプトが Play 中に生成した部分木は書かない**（根自身は印があっても書く。ScreenStack が積んだ画面を詰めて書き戻すのが主な使い道）。
- 根の名前・active・visible と 2D の根の CanvasTransform は**今のファイルの値を保つ**（シーンの "CardPlaced" という
  名前や、出入りの動きの途中の位置・大きさを焼かない）。3D の根はアクタファイル化の規則（位置だけ原点）。
- 書いた後、同じパスの Play 中のインスタンス全部へ当て直す（各インスタンスの `prefab_hash` も新しい版へ揃う）。
- 利用者のファイルを上書きするので、エディタは確認ダイアログを出してから送る（「元に戻せません」。実際には
  `actor_file::save` が直前の版を `.backup/` へ残す）。

入口はヒエラルキーの右クリック「Play 中の変更をプレハブへ書き戻す」と、インスペクタのプレハブの帯の［書き戻す］
（どちらもプレハブのインスタンスの根を選んでいて、Play / Pause 中だけ出る）。

### エディタの流れ

```
保存（Play / Pause 中）        書き戻し（Play / Pause 中）
  │ SAVE_OK                      │ 確認ダイアログ → PREFAB_WRITE_BACK:{dfs}
  │ PREFAB_LIVE_PATCH_PATH       │ <- PREFAB_WRITE_BACK_DONE:{件数},{仮想パス}
  │ <- PREFAB_LIVE_PATCH_DONE    │    そのファイルのアクタータブに「読み直し」の印
  │ トースト（0 件なら出さない）  │    トースト
  └──── どちらもパスを覚える（PrefabPlayReapplyQueue）────┘
Play 停止（OnStateChanged → Edit）
  ① 表示中のアクタータブに印があれば、まず OPEN_ACTOR で読み直す（未保存の編集があれば確かめる。下の「読み直しの印」）
  ② 設定オン・シーンのタブを表示中: 覚えたパスごとに PREFAB_REAPPLY_PATH（Undo はパスごとに 1 操作・件数のトースト・シーンは未保存に）
     設定オン・アクタータブを表示中: PREFAB_STATUS だけ（バナー）＋見送った理由のトースト（Ctrl+Z を約束できないため）
     設定オフ: PREFAB_STATUS だけ（版ずれのバナーで知らせる。シーンには触れない）
  どれでも: 画面プレビューを作り直す（ほかのアクタータブは次に表示したときに読み直す）
```

- **送る順と、アクタータブを表示中の扱い**（2026-10-03。`docs/reviews/2026-10-03_code_review.md` #16）: `OPEN_ACTOR` と
  `SET_ACTIVE_WORLD_LINE`（タブの切り替え）はどちらも Undo の履歴を作り直す。以前は再展開の**後に** `OPEN_ACTOR` を送っていたので、
  再展開の Undo を消したうえで「Ctrl+Z で戻せます」と出していた。今は読み直しを先に送り、さらにアクタータブの表示中は
  自動では再展開しない（シーンのタブへ移った時点で履歴が消えるため）。保存に続く自動反映（アクタータブで保存したとき）は
  従来どおり再展開するが、結果のトーストは「シーンのタブへ移ると Ctrl+Z では戻せません」と書き、Ctrl+Z を約束しない。
  根本は世界線ごとの Undo の履歴（docs/backlog.md）。
- **読み直しの印**（書き戻しで古くなったアクタータブ。`editor/src/Reload/StaleActorTabs.cs`。2026-10-03 のレビュー #14）:
  - タブを閉じたとき・同じファイルを新しいタブで開いたときは印を消す（以前は残り、閉じた後に開き直して編集したタブを
    次に表示したとき確認なしで読み直して、編集と Undo を消していた）。
  - 印のあるタブを表示するとき（停止時に表示中・タブの切り替え・タブを閉じて隣へ移る・ファイルのダブルクリック）、
    未保存の編集があれば読み直す前に確かめる（［はい］読み直す／［いいえ］今の中身のまま表示。どちらでも印は消え、聞くのは 1 回だけ）。
    **未保存の判定はタブ単位の印が無いので、エディタ全体の未保存の印（シーンとタブで共通）で代用する**。シーンだけを編集したときも
    確かめる（読み直してもシーンの編集は消えない。区別できないので安全側）。

- 設定「プレハブ保存時にシーンのインスタンスへ自動反映」（`PrefabAutoPropagateOnSave`）は **Edit のシーンへの反映**
  だけに効く。Play 中の当て直しは Play の表示だけを変えて停止で消えるので、設定に関わらず行う。
- **Play 中にアクタータブで保存する操作は無い**（Ctrl+S は Edit 以外では何もしない・タブ切り替えも Edit だけ）。
  代わりに、エディタの外（テキストエディタ・AI・別ツール）で `.actor` / `.actor2d` を書き換えると、監視が拾って
  Play 中なら `PREFAB_LIVE_PATCH_PATH` を送る（2026-10-03。Edit なら `PREFAB_REAPPLY_PATH` か `PREFAB_STATUS`）。
  エディタ自身の保存・書き戻し・アクタファイル化は自己書き込みとして除外する。正典は docs/editor_auto_reload.md §7.1。
  ほかに書き戻しの続き・IPC（MCP の `seed_send_ipc` など）からも当て直しが走る。

### MCP 化の候補（別レーンでまとめて行う）

- `seed_prefab_live_patch(prefab_path)` → `PREFAB_LIVE_PATCH_PATH`（応答 `PREFAB_LIVE_PATCH_DONE` / `_ERROR`）
- `seed_prefab_write_back(actor_dfs_id | name)` → `PREFAB_WRITE_BACK`（ファイルを上書きするので confirm 必須）
