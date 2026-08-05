# エディタの Undo / Redo

SEED の Undo/Redo は **ランタイム（Rust）側が唯一の履歴を持つ**。
エディタ（WPF）は Ctrl+Z / Ctrl+Y を `UNDO` / `REDO` の IPC として送るだけで、
履歴もシーンの状態も持たない。

- 履歴本体: `runtime/src/engine/core/app_base/undo.rs`（`Command` トレイト / `UndoHistory`）
- 保持者: `App::undo_history`（`app/mod.rs`）
- 受信: `app/ipc_handler.rs` の `IpcCommand::Undo` / `Redo`
- 上限: `MAX_HISTORY = 100`

地形（ボクセル）だけは ECS 外のデータのため別スタック
（`terrain_ops.rs` の `TerrainState::undo_stack`、`TERRAIN_UNDO` / `TERRAIN_REDO`）で管理する。

---

## 1. インスペクタのフィールド編集（汎用機構）

### 背景

インスペクタから値を書き換える IPC は 60 種類以上ある
（`SET_WATER_FIELD` / `SET_LIGHT_FIELD` / `SET_AUDIO_FIELD` / `SET_SCRIPT_FIELD` /
カメラ・スプライト・キャンバス・コライダー・パーティクル…）。
ハンドラごとに Undo コマンドを書く方式では対応漏れが必ず出る。
実際、以前は **Transform 系とプラグインフィールド以外はすべて Ctrl+Z が効かなかった**。

### 仕組み

実装: `runtime/src/engine/core/app_base/app/field_edit.rs`

IPC ディスパッチの入口（`ipc_handler.rs::process_ipc` のループ先頭・末尾）で、
コマンド 1 件ごとに次を行う。**個別ハンドラには一切手を入れない**。

1. `field_edit_target(&cmd)` でコマンドを分類し、書き換え対象を得る
   （スロット / CanvasTransform / アクターの active / 対象外）
2. 対象の **現在値をスナップショット**する
   （スロットなら `ComponentSlotData` = 名前 + enabled + コンポーネント値）
3. 既存ハンドラをそのまま呼ぶ
4. 適用後の値と比べ、**差分があれば Undo 履歴へ積む**（`SlotFieldEditCommand`）

Undo/Redo 時は `App::apply_slot_data` が値を書き戻す。
まず **既存の ECS エンティティを維持したまま**その場で上書きし
（`apply_component_data_in_place`）、それが無理な場合だけ
`rebuild_actor_slots`（スロット再構築）へフォールバックする。

エンティティを維持することが重要で、パーティクルの生存状態・スカイボックスの GPU 資源・
再生中の音声は entity をキーに管理されているため、despawn を伴う再構築では失われる。

フォールバックする条件は 2 つだけ:

| 条件 | 理由 |
|---|---|
| ModelComponent の `model_path` / マテリアルオーバーライドが変わった | glTF 再ロードと GPU 再アップロードが要る |
| ScriptComponent の型名が変わった | CLR インスタンスの作り直しが要る |

### 対応漏れが構造的に出ない仕掛け

1. **網羅 match**: `field_edit_target` は `IpcCommand` に対する網羅 match で
   `_ =>` を書かない。コマンドを追加すると **コンパイルエラー**になり、
   「Undo に載せるか否か」を決めるまでビルドが通らない。
2. **契約テスト**: `all_set_field_commands_are_registered_in_slot_table` が
   `ipc.rs` の `Set*Field` 系 variant を走査し、分類表
   （`// <FIELD-EDIT-TABLE:SLOT>` 〜 `// </FIELD-EDIT-TABLE:SLOT>`）に
   全部載っていることを検査する。「網羅 match の対象外側へ流し込んで黙って無効化した」
   ケースを検出する。
3. **ComponentData の網羅 match**: `apply_component_data_in_place` も網羅 match。
   コンポーネント種別を追加すると、復元側の実装を書くまでビルドが通らない。

### 連続編集のマージ規則

スライダーや数値ドラッグは 1 ドラッグで数百回 IPC が飛ぶ。
次の 3 条件を **すべて** 満たすとき、直前に積んだコマンドを取り下げて 1 件に統合する
（`can_merge_field_edit`）。

1. 同じ対象・同じフィールドであること（マージキー = コマンド名 + アクタ + スロット + フィールド名）
2. 前回の記録から `FIELD_EDIT_MERGE_WINDOW`（400ms）以内であること
3. その間に他の操作が Undo 履歴へ積まれていないこと（履歴長で検証）

結果として **スライダー 1 ドラッグ = Undo 1 回**、**テキスト 1 確定 = Undo 1 回**になる。
Undo / Redo 自体を実行するとマージセッションは打ち切られる。

1 ドラッグの中で元の値まで戻した場合（1.0 → 5.0 → 1.0）は、
途中まで積んであったコマンドを取り下げて履歴に何も残さない
（残すと Redo がユーザーの確定していない中間値を復活させてしまう）。

エディタ側に BEGIN/END のプロトコルは追加していない
（Transform ドラッグの `BEGIN_TRANSFORM_DRAG` / `END_TRANSFORM_DRAG` は従来どおり別機構）。

### Undo に載せないもの

| 種別 | 理由 |
|---|---|
| Play モード中の変更 | シーンの編集ではなく実行時の一時状態 |
| Transform 系 / 制御点ドラッグ / ツリー・スロットの構造変更 | 既に専用の Undo コマンドを持つ（二重記録の防止） |
| 地形ブラシ・ペイント | ECS 外データ。専用スタックでストローク単位に管理 |
| エディタカメラ・ツールモード・表示トグル | シーンに永続しないセッション状態 |
| シーン設定・プロジェクト設定（`SET_SCENE_SETTINGS` 等） | インスペクタではなく設定パネルの管轄。**現状 Undo 非対応** |
| 水位シミュレーションの現在水位（`sim_level_y`） | 揮発値。保存対象外なので Undo でも巻き込まない |
| ModelComponent のインスタンス配列 | 配置は `TransformCommand` 側の管轄。フィールド編集の Undo で巻き戻さない |

---

## 1.5 コンポーネント行の「⟲ デフォルトに戻す」（汎用リセット機構）

インスペクタの各パラメータ行の右端にある ⟲ ボタンは、
**その 1 フィールドだけを Rust の `Default` 由来の既定値へ戻す**。
コンポーネントごとの個別実装は無く、1 本の汎用 IPC で全コンポーネントを賄う。

### IPC

```
RESET_COMPONENT_FIELD:{actor_dfs_id},{slot_idx},{field_path}
```

`field_path` は **Rust の `*ComponentData` の serde フィールド名**であって、
`SET_*_FIELD` のキー名ではない（両者は別体系で、実際に食い違うものがある。
例: ライトの角度は SET キー `inner_angle` / serde 名 `inner_angle_deg`、
JointAttach は SET キー `offset_rot` / serde 名 `offset_rot_deg`）。
入れ子は `/` 区切りで指定する（例 `material_overrides/0/kind/roughness`）。

### ランタイム側の仕組み（`app/component_reset_ops.rs`）

リセットを「**コンポーネントの JSON 表現のうち 1 パスだけを既定値で差し替える**」
という、コンポーネントに依存しない操作へ還元してある。

1. 対象スロットの現在値を `slot_to_data` で `ComponentData` にする
2. `ComponentData` は `#[serde(tag = "type", content = "data")]` なので、
   現在値と「同じ variant の既定値」を両方 `serde_json::Value` 化する
3. `data` 以下を `field_path` で辿り、**その 1 箇所だけ**を既定値で置き換える
4. `from_value` で `ComponentData` へ戻し、`field_edit.rs::apply_slot_data` で適用する

したがって**フィールドが増えてもこのファイルは変更不要**。
唯一の追従点は既定値を作る `default_component_data` の**網羅 match**で、
`ComponentData` に variant を足すとビルドが落ちて気付ける。

| 規則 | 内容 |
|---|---|
| 既定値の正典 | 各コンポーネントの Rust `Default`（`XxxComponent::default().to_data()`） |
| 既定値側でパスが解決できないとき | JSON `null` を既定とみなす。マテリアルのインライン上書きは `Option<T>` で `None` = 「モデル埋め込みの値を使う」なので、これが正しい「既定へ戻す」になる |
| 既に既定値のとき | **何もしない**（無意味な `SCENE_MODIFIED` で未保存印を付けない＝ボタンが無反応に見えるのが正常） |
| 現在値側でパスが解決できない／戻せない JSON | 何もしない（不正なリセットでシーンを壊さない） |
| リセット非対応の種別 | `ScriptComponent`（既定値は C# の型定義側にある）／`PluginComponent`（既定値はプラグイン DLL のフィールド定義が持つ）／`LegacyRigidbodyComponent`（読み込み専用の旧フォーマット） |

### Undo

`field_edit_target` が `IpcCommand::ResetComponentField` を `Slot` へ分類しているので、
**1.（汎用機構）にそのまま乗る**。ハンドラ側に Undo 記録を書いてはならない（二重記録になる）。
マージキーのコマンド名を `SET_*` と分けてあるため、
「スライダーを動かす → ⟲」は 1 手に潰れず、Ctrl+Z 1 回でリセット直前の値へ戻る。

### エディタ側

| ファイル | 役割 |
|---|---|
| `editor/src/Controls/ResetButtonFactory.cs` | ⟲ ボタンの見た目と包み方（スクリプトの `[ResetButton]`・シェーダアセットの `@reset` と共通） |
| `editor/src/Panels/InspectorPanel.FieldReset.cs` | `RESET_COMPONENT_FIELD` を送る共通入口。「値域が表にあればスライダー行、無ければ数値行」を選び、⟲ を添えて返す |
| `editor/src/Controls/ComponentFieldRanges.cs` | **値域表（1 箇所集約）**。キーは `"{型名}.{serde フィールド名}"` |

各コンポーネントのビルダはローカルの行ヘルパから共通入口を呼ぶだけで、
行ごとの個別対応は書かない。

**⟲ を付ける/付けないの判断基準は「値の調整」か「構造の定義」か**:

| 付ける（値の調整） | 付けない（構造の定義・参照） |
|---|---|
| 色・強度・不透明度・粘度・減衰・角度・速度・寿命・質量・摩擦・マテリアル値 | Transform、サイズ／half extents／offset 位置、キャンバスサイズ、コライダー形状寸法 |
| | 参照・パス・名前系（アセットパス、アクタ名参照、ジョイント名、クリップ名） |
| | kind・種別 enum、モードフラグ（`simulate_level`・Is Main・キネマティック 等） |

### 値域表とランタイム clamp の同期

スライダーの上下限は**ランタイムの clamp が正典**。UI 側で勝手に狭めると
ランタイムが受け付ける値に手が届かず、広げるとスライダーの端が
「動かせるのに反映されない死に領域」になる。
水の 0..1 系については `water_ops.rs` の契約テストモジュール `field_range_contract`
（`table_entries_are_clamped_in_runtime` / `runtime_clamps_are_listed_in_table`）が
C# の表を `include_str!` で読み、**双方向**
（表にあるものは clamp されている／clamp されているものは表にある）に固定する。
走査は CRLF 非依存（`lines()` ベース）にすること。

上限の無い `v.max(0.0)` 系はスライダーにしない（表に載せない）。

---

## 2. 個別の Undo コマンド（従来からある経路）

`undo.rs` に定義。

| コマンド | 対象 |
|---|---|
| `SceneSnapshotCommand` | ModelComponent のインスタンス追加・削除 |
| `TransformCommand` / `MultiTransformCommand` | インスタンス変換行列 |
| `ActorTransformCommand` / `ActorGroupTransformCommand` | アクターの Transform |
| `CanvasTransformCommand` | 2D アクターの CanvasTransform（アンカー・スケールモード含む） |
| `ActorTreeSnapshotCommand` | アクターの追加・削除・並べ替え・リネーム・プレハブ操作 |
| `ComponentSlotsSnapshotCommand` | コンポーネントの追加・削除・複製、制御点リスト |
| `SelectionCommand` / `ActorDfsSelectionCommand` | 選択状態 |
| `CompositeCommand` | 複数コマンドを 1 操作にまとめる |
| **`SlotFieldEditCommand`** | **インスペクタのフィールド編集（汎用。上記 1.）** |
| **`ActorActiveCommand`** | **アクターの active フラグ** |

インスペクターフィールドのドラッグ（軸ラベルドラッグ）は
`BEGIN_TRANSFORM_DRAG` → 連続更新（記録なし）→ `END_TRANSFORM_DRAG` で
1 コマンドにまとめる（`App::inspector_transform_drag`）。

---

## 3. エディタ側の注意点

- Ctrl+Z / Ctrl+Y は WPF の KeyBinding ではなく **グローバルキーボードフック**
  （`editor/src/MainWindow.Input.cs`）で拾う。
- **TextBox にキーボードフォーカスがある間は送信しない**（テキスト入力中とみなすため）。
  そのため、インスペクタの数値フィールドは
  **Enter による確定後にフォーカスを外す**（`InspectorPanel.AttachAutoSelectBehavior`）。
  外さないと、値を確定した直後の Ctrl+Z がランタイムへ届かず「Undo が効かない」ように見える。
  数値ドラッグ確定時に `NumericDragBehavior` がフォーカスを外しているのと同じ扱いである。
- Undo/Redo 後の UI 追随は `ACTOR_COMPONENTS` の再送信で行う。
  `SlotFieldEditCommand` は `actor_inspect_notify()` を返すため、
  ランタイムが自動でインスペクタへ再送信する。

---

## 4. 動作確認手順

Edit モードで、各フィールドを編集 → Ctrl+Z で戻る → Ctrl+Y で進むことを確認する。

1. **水**: WaterVolume の `wave_amplitude` スライダーを 1 回ドラッグ →
   Ctrl+Z **1 回**でドラッグ開始前の値へ戻ること（数百回に分かれないこと）
2. **ライト**: 強度・色・種別（コンボ）を変更 → Ctrl+Z
3. **スクリプト**: `[SerializeField]` の数値フィールドを Enter 確定 → Ctrl+Z
4. **オーディオ / スカイボックス / パーティクル / カメラ / スプライト / コライダー**: 同様
5. **スロット名変更・コンポーネントの有効チェック・アクターのアクティブチェック**: Ctrl+Z で戻ること
6. **二重記録が無いこと**: プラグインフィールドを 1 回変更 → Ctrl+Z **1 回**で戻ること
7. **巻き込みが無いこと**: モデルの `cast_shadows` を切り替え → インスタンスをギズモで移動 →
   Ctrl+Z で **移動だけ**が戻ること（cast_shadows は戻らない）
8. **Play 中は積まれないこと**: Play 中に値を変えて Exit Play → Ctrl+Z が
   Play 中の変更を戻さないこと
9. **⟲ リセット（1.5）**: 値を変えたパラメータ行の ⟲ を押す → 既定値へ戻ること。
   続けて Ctrl+Z **1 回**でリセット直前の値へ戻ること
   （スライダーのドラッグと 1 手にまとまらないこと）
10. **⟲ の無反応が正常なケース**: 既に既定値の行で ⟲ を押しても何も起きず、
    タイトルバーに「未保存」印が付かないこと
