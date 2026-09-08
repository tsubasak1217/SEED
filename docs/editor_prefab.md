# プレハブ（アクタファイル参照リンク）とシーンへの反映

正典コード:

| 場所 | 役割 |
| --- | --- |
| `runtime/src/engine/core/app_base/app/prefab_ops.rs` | 再展開・版ずれ検出のコア。**設計方針はこのファイル冒頭のコメントが正典** |
| `runtime/src/engine/structs/objects/actor/mod.rs` | `prefab_source` / `prefab_hash`（`Actor` と `ActorData` の両方） |
| `runtime/src/engine/core/app_base/ipc.rs` | `PREFAB_*` コマンドのワイヤ形式 |
| `editor/src/MainWindow.Prefab.cs` | 保存時の自動反映・版ずれバナー・設定トグル |
| `editor/src/AI/Tools/EditorCommandExecutor.Visual.cs` | MCP `prefab_reapply` の実装 |

---

## 1. プレハブとは何か

`.actor` / `.actor2d` ファイルから作ったアクタは、その参照パスを
`prefab_source`（`assets://` 仮想パス）としてルートに持つ。これが Unity の
プレハブインスタンスに相当する。

- **`prefab_source` を持つのはインスタンスのルートだけ**。子アクタは常に持たない。
- `.actor` ファイル自身（テンプレート）には `prefab_source` を書き出さない
  （自己参照・二重リンクの混入を防ぐため）。
- ネストプレハブは **1 段のみ**展開する（インスタンスの配下へは降りない）。

インスタンスが生まれる経路は 4 つあり、いずれも `prefab_source` と
後述の `prefab_hash` の両方を設定する:

| 経路 | 実装 |
| --- | --- |
| プロジェクトパネルから `.actor` をビューポートへドロップ | `actor_ops.rs`（3D / 2D の 2 か所） |
| ヒエラルキーからプロジェクトへドラッグして書き出し（＝プレハブ化） | `actor_ops.rs::handle_export_actor` |
| ロジック配置（配置元＝アクタファイル） | `logic_placement_ops.rs` |
| 地形のアクタ散布（`kind=actor` プロップ） | `terrain_scatter_actor_ops.rs` |

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
