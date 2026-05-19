# SEED プロジェクト コードレビュー

**レビュー日**: 2026-05-19  
**対象ブランチ**: master（直近の変更ファイル群）  
**レビュー対象ファイル**:
- `runtime/src/engine/core/app_base/app/component_ops.rs`
- `runtime/src/engine/core/app_base/app/mod.rs`
- `runtime/src/engine/core/app_base/app/pick_2d.rs`
- `runtime/src/engine/core/app_base/app/render.rs`（冒頭部分）
- `runtime/src/engine/core/app_base/app/drag_state.rs`
- `runtime/src/engine/core/app_base/ipc.rs`
- `runtime/src/engine/core/app_base/scene.rs`
- `runtime/src/engine/core/renderer/mod.rs`
- `runtime/src/engine/core/renderer/pipeline.rs`

---

## 重要度の定義

| 記号 | レベル | 説明 |
|------|--------|------|
| 🔴 | **高** | バグ・データ不整合・セキュリティに関わる問題。早急に対応が必要 |
| 🟡 | **中** | 保守性・可読性・潜在的パフォーマンス問題。計画的に対応すべき |
| 🟢 | **低** | スタイル改善・軽微な設計問題。余裕があれば対応 |

---

## 🔴 高優先度

### 1. ScriptComponent の JSON キーが誤っている（バグ）

**ファイル**: `component_ops.rs:163`

```rust
ComponentData::ScriptComponent(d) => {
    let path_json = serde_json::to_string(&d.type_name).unwrap_or_default();
    ("ScriptComponent", format!(r#","model_path":{path_json}"#))
    //                                  ^^^^^^^^^^^
    //   ScriptComponent なのに model_path キーを送っている
}
```

**問題**: `ScriptComponent` のデータを `model_path` というキー名でエディタへ送信している。エディタ側が `type_name` を期待している場合、スクリプトパスがインスペクターに正しく表示されないバグが発生する。

**修正案**:
```rust
ComponentData::ScriptComponent(d) => {
    let path_json = serde_json::to_string(&d.type_name).unwrap_or_default();
    ("ScriptComponent", format!(r#","type_name":{path_json}"#))
}
```

---

### 2. `ipc.rs` が非 Windows でコンパイルエラーになる可能性

**ファイル**: `ipc.rs:349`

```rust
fn read_loop(file: std::fs::File, tx: mpsc::Sender<IpcCommand>) {
    use std::os::windows::io::AsRawHandle;  // ← 無条件インポート
    ...
}
```

`peek_pipe` 関数は `#[cfg(windows)]` でガードされているが、`read_loop` 内の `AsRawHandle` のインポートと `peek_pipe` の呼び出しは非 Windows でもコンパイルが通ろうとする。プロジェクトが Windows 専用であれば実害はないが、明示的に `#[cfg(target_os = "windows")]` でガードするかファイル先頭に `#![cfg(windows)]` を追加すべき。

---

## 🟡 中優先度

### 3. ModelComponent 構築ロジックが複数箇所に重複している

**ファイル**: `component_ops.rs` (L244-285, L628-677, L1290-1331)

以下の3箇所でほぼ同一の「キャッシュから取得 or ディスクからロード → GPU リソース構築」ロジックが書かれている:

- `handle_add_component_to_actor` の `ModelComponent` ケース
- `handle_duplicate_component` の `ModelComponent` ケース
- `rebuild_actor_slots` の `ModelComponent` ケース

**問題**: バグ修正や仕様変更が1箇所に入った際に他の箇所が追従し忘れる。

**修正案**: 共通ヘルパー関数を切り出す。

```rust
/// モデルパスから ModelComponent を構築するヘルパー。
/// キャッシュヒット時は Arc をクローンし、ミスの場合はディスクからロード。
fn build_model_component_from_path(
    ctx: &DrawContext,
    ipc: Option<&IpcClient>,
    path_str: &str,
    instance_mats: Vec<[[f32; 4]; 4]>,
    instance_meta: Vec<InstanceMeta>,
    group_meta: Vec<GroupMeta>,
    next_group_id: u32,
) -> Option<ModelComponent> { ... }
```

---

### 4. `handle_add_component_to_actor` の各コンポーネント種別でコードパターンが重複している

**ファイル**: `component_ops.rs:241-460`

`ModelComponent`, `ScriptComponent`, `CanvasComponent`, `SpriteComponent`, `InputMapComponent`, `CameraComponent` のそれぞれで以下の同じパターンが繰り返されている:

```rust
let slot_entity = scene.world.spawn();
scene.world.insert(slot_entity, XXXComponent::default());
let mut c = 0u32;
if let Some(actor) = find_actor_by_dfs_mut(...) {
    actor.add_slot_typed::<XXXComponent>(...);
    true
} else {
    scene.world.despawn(slot_entity);
    false
};
if found {
    let after_slots = self.snapshot_actor_slots(...);
    self.undo_history.record(...);
    // 同じ後処理 4行
}
```

**修正案**: スロット追加の共通部分をクロージャや内部ヘルパーに切り出す。少なくとも Undo 記録と後処理の4行は共通化できる。

---

### 5. `App` 構造体のフィールドが過多（単一責任原則）

**ファイル**: `mod.rs:325-463`

`App` 構造体に約45個のフィールドが詰め込まれている。関連するフィールドをサブ構造体に分割することで可読性と保守性が向上する。

**分割候補例**:
```rust
struct SelectionState {
    selected_instances: Vec<u32>,
    selected_actor_dfs_ids: Vec<usize>,
    actor_virtual_selected_idx: Option<usize>,
    actor_virtual_selected_slot_idx: usize,
}

struct CanvasState {
    canvas_world_lines: HashSet<u32>,
    actor_edit_canvas_wls: HashSet<u32>,
    canvas_cameras: HashMap<u32, CanvasCameraData>,
    canvas_screen_space_overlay: bool,
    canvas_overlay_camera_buf: Option<CameraBuffer>,
}
```

ただし Rust の借用規則により `&mut self` の分割はトレードオフが伴うため、段階的なリファクタリングを推奨。

---

### 6. `collect_canvas_actors_in_rect` で O(n²) の重複チェック

**ファイル**: `mod.rs:877`

```rust
if !result.contains(&dfs_id) { result.push(dfs_id); }
```

`result` が `Vec<usize>` のため `contains` が O(n) になる。アクター数が多い場合、矩形選択のたびに O(n²) コストが発生する。

**修正案**: ローカルで `HashSet` を使いつつ、戻り値は `Vec` で返す:
```rust
// 呼び出し元で HashSet を管理するか、result を HashSet に変更する
```

---

### 7. `find_actor_by_dfs` の引数型が `&Vec<Actor>` になっている

**ファイル**: `mod.rs:680`

```rust
fn find_actor_by_dfs<'a>(
    actors:  &'a Vec<Actor>,  // ← &[Actor] が Rust の慣例
    ...
```

Rust のベストプラクティスでは `&Vec<T>` ではなく `&[T]` を使う（スライス参照）。`clippy::ptr_arg` 警告が出る可能性がある。`find_actor_by_dfs_mut` と `collect_mcs_in_world_line` の引数も同様。

---

### 8. `scene.rs` のライフサイクルメソッドが空実装のまま残っている

**ファイル**: `scene.rs:271-277`

```rust
pub fn begin_frame(&self, _ctx: &FrameContext) {}
pub fn early_update(&self, _ctx: &FrameContext) {}
pub fn update(&self, _ctx: &FrameContext) {}
pub fn constant_update(&self, _ctx: &FrameContext) {}
pub fn late_update(&self, _ctx: &FrameContext) {}
pub fn render(&self, _ctx: &FrameContext) {}
pub fn end_frame(&self, _ctx: &FrameContext) {}
```

コメントに「System 移行前の暫定」とあるが、実装が進むにつれてデッドコードになるリスクがある。呼び出し元から削除するか、`#[deprecated]` を付与してトラッキングする。

---

### 9. `find_model_in_world_line` がルートアクターしか探索しない

**ファイル**: `scene.rs:207-216`

```rust
pub fn find_model_in_world_line(&self, wl: u32) -> Option<(Entity, &ModelComponent)> {
    for root in self.actors.iter().filter(|a| a.world_line == wl) {
        for slot in root.slots() {     // ← root のスロットのみ探索
            ...
        }
    }
    None
}
```

子アクターを持つシーンでは、ルートアクターがモデルを持たない場合に `None` を返してしまう。「後方互換 API」と注記はあるが、動作制限を関数ドキュメントに明示すべき。

---

### 10. `pipeline.rs` の `get_shader_source` でランタイムパニック

**ファイル**: `pipeline.rs:12-28`

```rust
fn get_shader_source(name: &str) -> &'static str {
    match name {
        "shader_common.wgsl" => include_str!("..."),
        ...
        other => panic!("unknown shader source: {other}"),
    }
}
```

シェーダー名のタイポはコンパイル時に検出できない。シェーダー名を `enum` で管理するか、少なくとも定数文字列を使うことでタイポリスクを減らせる。

---

## 🟢 低優先度

### 11. `DragState` が `Default` trait を実装していない

**ファイル**: `drag_state.rs:65-86`

```rust
impl DragState {
    pub fn new() -> Self { ... }  // new() があるが Default が未実装
}
```

Rust の慣例では `new()` が引数なしの場合は `Default` trait も実装する。`#[derive(Default)]` が使えない場合でも `impl Default for DragState` を追加すると統一性が上がる。

---

### 12. `pick_2d.rs` で `actor.children` に直接フィールドアクセスしている

**ファイル**: `pick_2d.rs:233`

```rust
walk_pick_2d(
    &actor.children, world, wl,  // ← メソッド経由でなくフィールド直接参照
```

他の箇所では `actor.children()` メソッドを使っているが、ここだけ直接フィールドアクセスになっている。カプセル化の一貫性のため `actor.children()` に統一すべき。

---

### 13. `pick_2d.rs` の `IDENTITY` 定数が `mod.rs` の `MAT4_IDENTITY` と重複

**ファイル**: `pick_2d.rs:49-54`

```rust
const IDENTITY: [[f32; 4]; 4] = [
    [1.0, 0.0, 0.0, 0.0],
    ...
];
```

`mod.rs` の `MAT4_IDENTITY` と同一定義。`pub(super)` で公開して使いまわすか、共通モジュールに移動する。

---

### 14. `handle_add_component` の「旧スタイル」に仮想 ID 境界値の定数未定義

**ファイル**: `component_ops.rs:35`

```rust
let actor_idx = if actor_id >= 999_000_000 {
    (actor_id - 999_000_000) as usize
```

`999_000_000` はマジックナンバー（コメントで「仮想 ID」と説明はある）。プロジェクトポリシー「マジックナンバー禁止」に従い、定数化すべき。

```rust
const VIRTUAL_ACTOR_ID_BASE: u32 = 999_000_000;
```

---

### 15. `walk_pick_2d` の引数が多い（`too_many_arguments` 警告を抑制中）

**ファイル**: `pick_2d.rs:132`

```rust
#[allow(clippy::too_many_arguments)]
fn walk_pick_2d(...) {
```

引数を構造体 `PickContext` にまとめることで、将来の引数追加も容易になる。

```rust
struct PickContext<'a> {
    actors: &'a [Actor],
    world: &'a World,
    wl: u32,
    canvas_x: f32,
    canvas_y: f32,
}
```

---

### 16. `ipc.rs` でパースエラーが無言でスキップされる

**ファイル**: `ipc.rs:731-733`

```rust
if let Some(cmd) = cmd {
    if tx.send(cmd).is_err() { break; }
}
// cmd が None の場合は黙って次のコマンドへ進む
```

不正フォーマットのコマンドが来た場合にデバッグログがない。開発中は `#[cfg(debug_assertions)]` ブロックで警告を出力すると問題診断が容易になる。

---

## ポジティブなポイント

以下の実装は特に評価できる点として挙げる。

1. **`DragState` の分離** (`drag_state.rs`): App 構造体から LMB ドラッグ関連状態を切り出した設計は単一責任原則に従っており、可読性が高い。

2. **IPC パースヘルパー群** (`ipc.rs:238-336`): `parse_nf`, `parse1u_nf`, `parse2u_tail` 等の汎用ヘルパーにより、各コマンドのパース処理が簡潔に書けている。const ジェネリクスを活用した型安全なパースは特に良い設計。

3. **Undo/Redo の一貫した記録** (`component_ops.rs`): コンポーネント追加・削除・複製の全操作で `before_slots` / `after_slots` をスナップショットして Undo コマンドを積んでいる。Undo の抜け漏れが少ない。

4. **ECS のボローパターン** (`scene.rs`, `component_ops.rs`): `actor.entity` を先取りして actors の借用を解放してから `world` を操作する「Entity の先取り」パターンが一貫して使われており、Rust の借用チェッカーとうまく協調している。

5. **定数化されたマジックナンバー** (`render.rs`, `mod.rs`): `CANVAS_WORLD_SCALE`, `CAMERA_PREVIEW_W/H`, `MAT4_IDENTITY`, `BLIT_RECT_BUFFER_SIZE` など、重要な数値が定数として管理されている。

6. **モジュール分割** (`app/mod.rs`): `component_ops`, `actor_ops`, `transform_ops`, `gizmo_handler` 等の機能別分割が徹底されており、単一ファイルへの処理詰め込みを回避できている。

---

## 優先対応一覧

| # | 重要度 | ファイル | 概要 |
|---|--------|---------|------|
| 1 | 🔴 高 | `component_ops.rs:163` | ScriptComponent の JSON キーが `model_path` になっているバグ |
| 2 | 🔴 高 | `ipc.rs:349` | 非 Windows でコンパイルエラーになる可能性 |
| 3 | 🟡 中 | `component_ops.rs` 複数箇所 | ModelComponent 構築ロジックの重複（3箇所以上） |
| 4 | 🟡 中 | `component_ops.rs:241-460` | コンポーネント追加処理の繰り返しパターン |
| 5 | 🟡 中 | `mod.rs:325-463` | App 構造体のフィールドが過多 |
| 6 | 🟡 中 | `mod.rs:877` | 矩形選択の O(n²) 重複チェック |
| 7 | 🟡 中 | `mod.rs:680` | `&Vec<Actor>` → `&[Actor]` への変更 |
| 8 | 🟡 中 | `scene.rs:271` | 空のライフサイクルメソッドの残存 |
| 9 | 🟡 中 | `scene.rs:207` | `find_model_in_world_line` の動作制限の明示 |
| 10 | 🟡 中 | `pipeline.rs:12` | シェーダー名のランタイムパニック |
| 11 | 🟢 低 | `drag_state.rs:65` | `Default` trait の未実装 |
| 12 | 🟢 低 | `pick_2d.rs:233` | `actor.children` への直接アクセス |
| 13 | 🟢 低 | `pick_2d.rs:49` | `IDENTITY` 定数の重複定義 |
| 14 | 🟢 低 | `component_ops.rs:35` | 仮想 ID 境界値 `999_000_000` の定数未定義 |
| 15 | 🟢 低 | `pick_2d.rs:132` | `too_many_arguments` の抑制 |
| 16 | 🟢 低 | `ipc.rs:731` | パースエラー時のデバッグログなし |
