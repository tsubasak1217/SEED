---
name: add-ecs-component
description: 新しいECSコンポーネント（＋必要ならシステム）をSEEDエンジン本体（Rust）に追加するときに使用。ファイル配置・World登録・serdeシリアライズ・エディタのインスペクタUI/コンポーネント選択・IPCまで、全連携点を漏れなく通すための手順書。
---

# ECSコンポーネント／システムを追加する

SEEDでは「コンポーネント＝データ（`World`のSparseSetに格納）」「システム＝ロジック（`World`を横断処理）」を厳格に分離する（ECS理念）。コンポーネントにメソッドロジックを詰めないこと。1ファイル1責務・マジックナンバー禁止・クラス/関数/処理コメント必須（`.claude/CLAUDE.md`参照）。

## 模倣テンプレート

**`AudioComponent`** を最も標準的な雛形として全面的に模倣する。以下は既存の全連携点を持つ最小構成の実例で、新コンポーネントはこの各ステップに1エントリずつ足すだけで動く状態を保つ。ファイル: `runtime/src/engine/components/audio_component.rs`。

---

## Rust側の手順

### 1. コンポーネントファイルを作る

`runtime/src/engine/components/<name>_component.rs` を新規作成する。`AudioComponent` に倣い、**実データ型**（例 `AudioComponent`）と**シリアライズ用データ型**（例 `AudioComponentData`）を分ける。データ型に serde、実体型に `impl Component for XxxComponent {}` を付ける。

```rust
use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;

fn default_volume() -> f32 { 1.0 }   // ← マジックナンバーはデフォルト値関数へ

#[derive(Clone, Serialize, Deserialize)]
pub struct AudioComponentData {
    #[serde(default)]               // 旧シーン互換: 欠落フィールドは Default 補完
    pub audio_path: String,
    #[serde(default = "default_volume")]  // 非ゼロ既定値は関数で指定
    pub volume: f32,
    // ...
}
```

**serde `#[serde(default)]` は全フィールド必須**。これが旧 `.scene` 互換の要。既存シーンに存在しない新フィールドを追加しても、`default` があれば読み込み時に既定値で補完される。逆に付け忘れると「そのフィールドが無い旧シーンの読み込みが丸ごと失敗する」。列挙リネームや旧名互換は `#[serde(rename = "...")]`（`looped` を `"loop"` で保存している例あり）。

実体型 `AudioComponent` は同じフィールド構成にし、`from_data(data)` / `to_data(&self)` の相互変換と `impl Default` を用意する（`AudioComponent` の実装をそのままなぞる）。`Component` トレイトは `runtime/src/engine/ecs/storage.rs` の `pub trait Component: Any + Send + Sync + 'static {}`。

### 2. `components/mod.rs` に登録（複数か所）

`runtime/src/engine/components/mod.rs` に以下を**すべて**追加する（1つでも漏れるとコンパイルエラーか実行時の取りこぼし）。

- `pub mod <name>_component;`
- `pub use <name>_component::{XxxComponent, XxxComponentData};`
- `enum ComponentKind` に variant（例 `Audio`）を追加
- `ComponentKind::display_name()` の match に1行
- `enum ComponentData`（`#[serde(tag="type", content="data")]`）に `XxxComponent(XxxComponentData)` を追加

`ComponentKind` は「型消去された ComponentSlot がどの型か」を識別するための列挙。`ComponentData` は `.scene` 保存とUndoスナップショットのシリアライズ表現。

### 3. `World` API を知る

`runtime/src/engine/ecs/world.rs`。エンティティごとに各型1インスタンス。使うのは主に:

- `world.spawn() -> Entity` / `world.despawn(entity)`
- `world.insert::<T>(entity, comp)` / `world.remove::<T>(entity) -> bool`
- `world.get::<T>(entity) -> Option<&T>` / `world.get_mut::<T>(entity) -> Option<&mut T>`
- `world.contains::<T>(entity) -> bool`
- 横断処理は `world.query::<T>()` / `world.query_mut::<T>()` / `world.query2::<A,B>()`

**重要な構造**: SEEDでは同じActorに同型コンポーネントを複数持てるよう、**各スロットが専用の `Entity` を持つ**（`actor.entity` とは別に `world.spawn()` する）。`ComponentSlot`（`runtime/src/engine/structs/objects/actor/mod.rs`）が `name / kind / type_id / entity / enabled` を保持し、実データは `slot.entity` に格納される。スロット追加は `actor.add_slot_typed::<T>(name, ComponentKind::Xxx, slot_entity)`。

### 4. Actor のシリアライズ／デシリアライズ経路に足す（各1か所）

同ファイル `actor/mod.rs` の `Actor::to_data_recursive()` 内の `match slot.kind { ... }` に、新 `ComponentKind` の腕を追加（`world.get::<XxxComponent>(slot.entity).map(|c| ComponentData::XxxComponent(c.to_data()))`）。**これがセーブ（`.scene`書き出し）側**。

逆のロード側は `runtime/src/engine/core/app_base/app/slot_ops.rs` の `rebuild_actor_slots()` の `match slot_data.component { ... }`（`ComponentData::XxxComponent(d) => { let e = world.spawn(); world.insert(e, XxxComponent::from_data(d)); new_slots.push(ComponentSlot::new::<XxxComponent>(...)); }`）。同ファイルの `handle_remove_component_slot()` の match と `handle_duplicate_component()` の match にも同型の腕を足す（削除・複製対応）。`AudioComponent` を grep すると足すべき箇所が全部見える。

**`.scene` の保存・読み込み自体は自動**。上記のシリアライズ両経路を通せば、シーンファイルの読み書きに追加作業は不要（ファイルフォーマットにはメタ登録が要らない）。

### 5. 「コンポーネント追加」ハンドラに足す

`runtime/src/engine/core/app_base/app/component_ops.rs` の `handle_add_component_to_actor()` の `match component_type { ... }` に `"XxxComponent" => { ... world.spawn(); world.insert(e, XxxComponent::default()); actor.add_slot_typed::<XxxComponent>(...); }` を追加（`"AudioComponent"` の腕が雛形）。同ファイルの `send_actor_components()` 側の `ComponentData::XxxComponent(d) => (...)` にも、インスペクタへ送るJSONフィールド文字列を組み立てる腕を足す。

### 6. システムを追加する（ロジックが必要な場合のみ）

データを毎フレーム動かすなら `runtime/src/engine/systems/` にシステムを置く。`System` トレイト（`runtime/src/engine/ecs/system.rs`）は `name()` と `run(&mut World, &FrameContext)`。関数を包む `FnSystem::new("my_system", |world, ctx| { for (e, c) in world.query_mut::<XxxComponent>() { ... } })` が定番。

登録は `runtime/src/engine/systems/mod.rs` の `register_default_systems(schedule)` に自作 `register(schedule)` を1行追加する（`script_system::register(schedule)` が唯一の既存例）。フェーズは `schedule.add_system(Phase::Update, sys)`。`Phase` は `BeginFrame → EarlyUpdate → Update → ConstantUpdate → LateUpdate → Render → EndFrame` の順（`runtime/src/engine/ecs/schedule.rs`）。実行は Play モード・非ポーズ時に `Scene::run_phase` 経由。

（注: `AudioComponent` は毎フレームのECSシステムを持たず再生処理を別経路で行う例。単なるデータ保持だけなら本ステップは不要。）

---

## エディタ連携（C#側・複数か所）

インスペクタ表示とコンポーネント追加メニューはRustと別に手当てが必要。**ここを飛ばすと「コンポーネントは追加できるがインスペクタに何も出ない／選択メニューに現れない」**。

1. **コンポーネント選択メニュー**: `editor/src/ComponentSelectorWindow.xaml.cs` の `Categories` リストの該当カテゴリに `new("XxxComponent", "表示名", "説明", ActorTarget.Common)` を追加（`ActorTarget` は `Common / Actor3D / Actor2D`）。「サウンド」カテゴリの `AudioComponent` 行が雛形。カテゴリが無ければ新規カテゴリを足す。

2. **インスペクタUI**: `editor/src/Panels/InspectorPanel.xaml.cs`。`SlotInfo` レコードに新フィールドを追加 → `BuildActorComponentList()` のJSONパースでそれらを読む → `case "XxxComponent" => BuildXxxSlotContent(info)` の分岐 → `BuildXxxSlotContent()` で行UIを組む（`AudioComponent` は `BuildAudioSlotContent`、`AddFloatRow`/`AddCheckRow` などの共通ヘルパを使用）。表示名・ヘッダ色のマップにも1行ずつ追加。

3. **フィールド編集のIPC**: 値変更をRustへ返す。`runtime/src/engine/core/app_base/ipc.rs` の `enum IpcCommand` に `SetXxxField { actor_dfs_id, slot_idx, key, value }` を足し、同ファイルのテキストコマンドパーサ（`SET_AUDIO_FIELD:` の分岐が雛形）に対応させ、`ipc_handler.rs` の dispatch で `world.get_mut::<XxxComponent>(slot_entity)` を更新するハンドラを呼ぶ。C#側は対応する `SET_XXX_FIELD:` 文字列を送る。

4. **種別アイコンの登録**: `editor/src/Controls/ComponentIcons.cs` の `IconKeyByTypeId` に `["XxxComponent"] = "Icon.Component.Xxx"` を追加し、`editor/gen_icons.py` の `CATALOG` に `("Icon.Component.Xxx", "<mdi名>")` を足して `cd editor && python gen_icons.py` を実行する（詳細は `.claude/rules/editor-icons.md` / `docs/editor_icons.md`）。漏れると汎用の `Icon.Component.Unknown` で表示される。

5. 新規コンポーネント追加コマンド自体は既存の `ADD_COMPONENT` 経路（`ipc_handler.rs` → `handle_add_component_to_actor`）を流用するので、C#は選択メニューの `TypeId` 文字列を送るだけでよい（ステップ5と対応）。

---

## 検証手順

1. `cd runtime && cargo build`（Rust側コンパイルが通ることを確認。`mod.rs`・`ComponentKind`・各 match の腕が揃っていないとここで落ちる）。
   その後 `cd editor && python check_icons.py` でアイコンキーの未定義参照が 0 件であることも確認する。
2. エディタを起動 → アクター選択 → インスペクタの「コンポーネント追加」で新コンポーネントが選択肢に出るか。
3. 追加 → インスペクタに全フィールドが表示され、値を編集できるか（IPC往復）。
4. シーンを保存 → `.scene` を開いて `"type":"XxxComponent"` ブロックが書かれているか目視。
5. シーンを再読み込み（別シーンへ切替→戻す）→ 値が復元されるか。
6. **旧シーン互換**: 新フィールドを足したコンポーネントを含む旧 `.scene` を開き、読み込みが失敗しないこと（`#[serde(default)]` 漏れの検出）。

## よくある失敗（構造上起こり得るもの）

- `components/mod.rs` の `pub mod` / `pub use` 忘れ → コンパイルエラー。
- `ComponentKind` に variant を足したが `display_name()` / `to_data_recursive` / `rebuild_actor_slots` / `handle_remove_component_slot` の match 更新漏れ → 非網羅で `cargo build` が落ちる（Rustが守ってくれる）。
- serde `#[serde(default)]` 漏れ → **旧 `.scene` の読み込みが丸ごと失敗**（そのアクターやシーン全体が読めない）。非ゼロ既定値は `default = "fn"` を使う。
- エディタ側のどれか漏れ → 追加はできるがインスペクタ非表示／選択メニュー非掲載／編集がRustへ届かない（C#はコンパイル時に守られないので特に注意）。
- `ComponentIcons` へのアイコン登録漏れ → ビルドは通るが、インスペクタと追加メニューで種別が汎用アイコンのまま区別できない。
- スロット専用 `entity` を `spawn` せず `actor.entity` に直接 `insert` → 同型コンポーネント複数持ちが壊れる。

---

## スクリプトへの公開は別Skill

このコンポーネントを **C#スクリプト／AIインライン補完から使えるようにする**（`host_api.rs` レジストリ登録、`scripting/src/Api/` の薄いラッパー、`docs/scripting_api.md` 追記）手順は別Skill **`add-script-api`** の担当。エンジン本体への追加が済んだらそちらを参照すること。
