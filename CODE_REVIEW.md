# SEED エンジン コードレビュー

レビュー実施日: 2026-05-19  
対象: runtime (Rust/wgpu) + editor (C#/WPF)

---

## 総評

ECS の基礎設計（SparseSet・World・Component）は堅固で、ファイル分割の方向性も概ね正しい。
コメントも充実しており、設計意図は読める。
ただし `App` 構造体が神オブジェクト化しており、IPC パーサーの重複が深刻。
以下に重要度別で改善点を列挙する。

---

## 🔴 重要度 HIGH — 早期に対処すべき設計問題

---

### H-1. App 構造体の神オブジェクト化 (God Object)

**ファイル:** `runtime/src/engine/core/app_base/app/mod.rs:309-475`

App 構造体に **65 フィールド以上** が詰め込まれている。
エディタ状態・カメラ・ピッキング・Undo・ドラッグ・世界線・クリップボード・FPS 計測・
キャンバス・カメラプレビュー・ギズモが全て 1 つの型に混在している。

```rust
// 現状: 単一構造体に全責務を集中
pub struct App {
    // ── カメラ ──
    camera: DebugCamera,
    camera_buf: Option<CameraBuffer>,
    canvas_overlay_camera_buf: Option<CameraBuffer>,
    cam_input: CameraInput,
    cam_grab_screen_pos: Option<(i32, i32)>,
    // ── ピッキング ──
    id_buffer: Option<IdBuffer>,
    selected_instances: Vec<u32>,
    pending_pick: Option<(u32, u32)>,
    // ── ドラッグ ──
    drag_root_starts: Vec<(u32, [[f32; 4]; 4])>,
    drag_child_starts: Vec<(u32, [[f32; 4]; 4])>,
    actor_child_drag_starts: Vec<(u32, [[f32; 4]; 4])>,
    actor_extra_mc_drag_starts: Vec<(usize, Vec<[[f32; 4]; 4]>)>,
    multi_actor_drag_starts: Vec<(u32, [[f32; 4]; 4])>,
    // ── Undo ──
    undo_history: UndoHistory,
    // ── 世界線 ──
    active_world_line: u32,
    saved_cameras: HashMap<u32, DebugCameraData>,
    canvas_world_lines: HashSet<u32>,
    actor_edit_canvas_wls: HashSet<u32>,
    canvas_cameras: HashMap<u32, CanvasCameraData>,
    // ─ 以下40フィールド以上続く ─
}
```

**改善案:** 責務ごとにサブ構造体に分割する。

```rust
pub struct App {
    editor_state:    EditorState,    // ツールモード・選択・Undo
    drag_state:      DragState,      // ドラッグ開始行列・矩形選択
    camera_state:    CameraState,    // デバッグカメラ・カメラ入力
    world_lines:     WorldLineManager, // 世界線・キャンバス情報
    render_state:    RenderState,    // renderer・draw_ctx・camera_buf
    scene:           Option<Scene>,
    ipc:             Option<IpcClient>,
    // ...
}
```

App::new() の初期化リスト (494-568) が 75 行になっているのも
この問題の直接的な症状である。Default トレイトを実装して省略できる。

---

### H-2. IPC パーサーの重複コード (550 行超の match 文)

**ファイル:** `runtime/src/engine/core/app_base/ipc.rs:245-795`

`read_loop` 内の 1 つの `match` 式に全コマンドのパース処理を直書きしている。
各コマンドは「文字列を splitn → parse::<u32>() → parse::<f32>()」という
**ほぼ同一のパターン**を繰り返しており、50 回以上重複している。

```rust
// 現状: 同一パターンが各コマンドにコピーされている
s if s.starts_with("SET_TRANSFORM:") => {
    let parts: Vec<&str> = rest.split(',').collect();
    if parts.len() == 10 {
        if let Ok(id) = parts[0].parse::<u32>() {
            let floats: Vec<f32> = parts[1..].iter()
                .filter_map(|x| x.parse::<f32>().ok()).collect();
            // ...
        }
    }
}
s if s.starts_with("SET_ACTOR_TRANSFORM:") => {
    // 上と全く同じ構造
}
s if s.starts_with("SET_CANVAS_TRANSFORM:") => {
    // 上と全く同じ構造（フィールド数だけ違う）
}
```

**改善案:** パースヘルパー関数を切り出す。

```rust
// 共通ヘルパー
fn parse_u32(s: &str) -> Option<u32> { s.parse().ok() }
fn parse_f32(s: &str) -> Option<f32> { s.parse().ok() }

fn parse_id_and_floats<const N: usize>(rest: &str) -> Option<(u32, [f32; N])> {
    let parts: Vec<&str> = rest.splitn(N + 1, ',').collect();
    if parts.len() != N + 1 { return None; }
    let id = parse_u32(parts[0])?;
    let mut floats = [0.0f32; N];
    for (i, p) in parts[1..].iter().enumerate() {
        floats[i] = parse_f32(p)?;
    }
    Some((id, floats))
}
```

またコマンド文字列とその解釈が 1 か所に集中しているため、
C# 側と Rust 側で **プロトコルのズレが生じても気づきにくい**。
将来的には MessagePack や Protocol Buffers などの型付きシリアライズを検討すること。

---

### H-3. DFS ツリー走査の重複実装

**ファイル:** `runtime/src/engine/core/app_base/app/mod.rs:641-1100`

不変版・可変版で **ほぼ同一コード** が二重実装されている。
さらに「ルートレベル」と「子ノード再帰」を分けた関数ペアが何組もある。

| 不変版 | 可変版 | 目的 |
|--------|--------|------|
| `find_actor_by_dfs` | `find_actor_by_dfs_mut` | DFS ID でアクターを取得 |
| `find_actor_child_by_dfs` | `find_actor_child_by_dfs_mut` | 同上の再帰実装 |
| `remove_actor_by_dfs` (削除) | `extract_actor_by_dfs` (取り出し) | 削除と取り出しで類似構造 |
| `collect_mcs_in_world_line` | `update_all_mc_batches_for_wl` | 収集と更新で類似走査 |

DFS カウンターを手動インクリメントするパターンが全体に散在しており、
カウント漏れバグを引き起こしやすい。

**改善案:** ジェネリックな DFS ウォーカーを実装する。

```rust
/// DFS 順に全アクターを訪問し、クロージャに (dfs_id, actor) を渡す。
fn walk_actors_dfs<F>(actors: &[Actor], wl: u32, mut f: F)
where
    F: FnMut(u32, &Actor),
{
    let mut counter = 0u32;
    fn walk<F: FnMut(u32, &Actor)>(actor: &Actor, c: &mut u32, f: &mut F) {
        f(*c, actor);
        *c += 1;
        for child in actor.children() { walk(child, c, f); }
    }
    for root in actors.iter().filter(|a| a.world_line == wl) {
        walk(root, &mut counter, &mut f);
    }
}
```

---

### H-4. render.rs の `window_event` が肥大化

**ファイル:** `runtime/src/engine/core/app_base/app/render.rs`

`ApplicationHandler::window_event` の LMB ドラッグ開始処理だけで
100 行近くを占めている。イベントハンドラに複雑なロジックが直書きされており、
テストも困難な状態。

**改善案:**
- `handle_lmb_press(&mut self, cx: f32, cy: f32)` などのハンドラメソッドに抽出する
- `gizmo_handler.rs` に既に分離されているので、同様のパターンで
  `drag_handler.rs` / `pick_handler.rs` に分割する

---

## 🟠 重要度 MEDIUM — 品質向上のために対応推奨

---

### M-1. マジックナンバーの残存

以下の箇所にマジックナンバーが残っている。

| ファイル | 行 | 数値 | 問題 |
|----------|-----|------|------|
| `app/mod.rs` | 169 | `size: 16` | BlitRect = [f32;4] の意味だがコメントのみ |
| `renderer/mod.rs` | 694 | `Some(256)` | COPY_BYTES_PER_ROW_ALIGNMENT だがコメント止まり |
| `ipc.rs` | 818 | `0..20`, `from_millis(100)` | 接続リトライ回数・待機時間 |
| `render.rs` | 179 | `Vector3::new(0.0, 2.0, -10.0)` | カメラ初期位置のハードコード |

```rust
// 改善例
const BLIT_RECT_BUFFER_SIZE: u64 = 16; // [f32; 4]
const COPY_ROW_ALIGNMENT: u32 = 256;   // wgpu の COPY_BYTES_PER_ROW_ALIGNMENT
const PIPE_CONNECT_RETRIES: u32 = 20;
const PIPE_CONNECT_RETRY_MS: u64 = 100;
const DEFAULT_CAMERA_POSITION: [f32; 3] = [0.0, 2.0, -10.0];
```

---

### M-2. 手動行列乗算 — Mat4x4 型が活用されていない

**ファイル:** `runtime/src/engine/core/app_base/app/mod.rs:1170-1180`

`world_to_screen` 関数は配列インデックスで行列乗算を直書きしている。
`Mat4x4` 型と `Vector3` 型がプロジェクト内に存在するにも関わらず使われていない。

```rust
// 現状: 生の配列アクセスで行列乗算
let vx = view[0][0]*wx + view[0][1]*wy + view[0][2]*wz + view[0][3];
// ...（8行続く）

// 改善: 型のメソッドを使う
let view_mat  = Mat4x4::from_cols(view);
let proj_mat  = Mat4x4::from_cols(proj);
let clip_pos  = proj_mat * view_mat * Vector3::new(wx, wy, wz).extend(1.0);
```

`apply_delta_to_actor_children` (1241-1245) の単位行列もマジックナンバー同様。
`Mat4x4::IDENTITY` のような定数で置き換えること。

---

### M-3. pipeline.rs の bgls.pop().unwrap() 順序依存

**ファイル:** `runtime/src/engine/core/renderer/pipeline.rs`

```rust
// 現状: インデックス順に pop() — 並び順を変えるとサイレントに壊れる
let material_bgl = bgls.pop().unwrap();
let model_bgl    = bgls.pop().unwrap();
let camera_bgl   = bgls.pop().unwrap();
```

TOML 設定でバインドグループレイアウトの順序が変わると
`pop()` の結果も変わり、ランタイムでクラッシュ or 描画バグになる。

**改善案:** 名前付きの Map で取り出す、または TOML から group 番号を読んで対応させる。

---

### M-4. build_hierarchy_json の手動 JSON 構築

**ファイル:** `runtime/src/engine/core/app_base/app/mod.rs:620-639`

```rust
// 現状: 手動で JSON を組み立てている
fn build_hierarchy_json(nodes: &[(u32, String, Option<u32>)]) -> String {
    let mut json  = String::from("[");
    // ...
    json.push_str(&format!(r#"{{"id":{},"name":{},...}}"#, ...));
}
```

serde_json は既にプロジェクトに存在する。手動 JSON 構築はエスケープ漏れのリスクがある
（名前に `"` が入ると壊れる可能性）。

```rust
// 改善: serde_json::to_string で安全に生成
#[derive(Serialize)]
struct HierarchyNode { id: u32, name: String, parent: Option<u32>, is_group: bool }

fn build_hierarchy_json(nodes: &[(u32, String, Option<u32>)]) -> String {
    let items: Vec<HierarchyNode> = nodes.iter()
        .map(|(id, name, parent)| HierarchyNode { id: *id, name: name.clone(), parent: *parent, is_group: false })
        .collect();
    serde_json::to_string(&items).unwrap_or_default()
}
```

---

### M-5. C# エディタの P/Invoke が MainWindow に直書き

**ファイル:** `editor/src/MainWindow.xaml.cs`

Win32 API の P/Invoke 定義（`GetCursorPos`, `SetWindowLong`, `RegisterDragDrop` 等）が
UI クラスである `MainWindow` に直接記述されている。
単一責任原則に反し、他のウィンドウやパネルで同じ API が必要になったときに重複が発生する。

**改善案:** `Native/NativeInterop.cs` のような専用クラスに集約する。

---

### M-6. DFS ID の脆弱性 — ツリー変更で全 ID が変化する

**全体設計の問題**

DFS ID（ツリーのトポロジカル順インデックス）はアクターの挿入・削除・並び替えで
全ノードの ID が変化する「不安定な ID」である。
Undo コマンド・IPC メッセージ・インスペクター通知など、システム全体がこの ID に依存しており、
操作順序によっては誤ったアクターを指してしまうリスクがある。

**改善案:** アクター生成時に安定した UUID (または単調増加する u64) を割り当て、
エディタとランタイム間の通信はこの安定 ID を使う。
DFS ID はヒエラルキー表示の順序計算にのみ使用する。

---

## 🟡 重要度 LOW — 余裕があれば対応

---

### L-1. HashMap / HashSet の完全修飾

**ファイル:** `app/mod.rs:557-560`

```rust
// 現状
saved_cameras: std::collections::HashMap::new(),
canvas_world_lines: std::collections::HashSet::new(),
```

`use std::collections::{HashMap, HashSet};` を追加すれば読みやすくなる。
（構造体定義部では型名だけが使われており、new() 呼び出し時だけ完全修飾になっている）

---

### L-2. コメントスタイルの混在

一部ファイルで英語コメントと日本語コメントが混在している。
また `// ──` 区切り線のスタイルが `// ====` と `// ──` で混在している箇所がある。
プロジェクト全体で統一ルールを設けること。

---

### L-3. `try_open` のリトライ上限がマジックナンバー

**ファイル:** `ipc.rs:817-825`

```rust
fn try_open(path: &str) -> std::io::Result<std::fs::File> {
    for _ in 0..20 {  // ← 20 と 100ms はなぜこの値？
        match OpenOptions::new().read(true).write(true).open(path) {
            Ok(f)  => return Ok(f),
            Err(_) => thread::sleep(Duration::from_millis(100)),
        }
    }
    OpenOptions::new().read(true).write(true).open(path)
}
```

定数化 + コメントでリトライ設計の意図を明示すること。

---

### L-4. `ScriptComponent` / `PlaceholderScriptSlot` の二重型

**ファイル:** `runtime/src/engine/components/script_component.rs`

CLR 有効時は `ScriptComponent`、無効時は `PlaceholderScriptSlot` を使う設計だが、
エディタで表示名も `"ScriptComponent (placeholder)"` と紛らわしい。
将来スクリプト機能が拡充された際に整理すること。

---

## ✅ 評価が高い箇所

問題点だけでなく、良い設計も記録しておく。

| 箇所 | 評価ポイント |
|------|-------------|
| `ecs/storage.rs` | SparseSet の O(1) insert/get/remove が正しく実装されている |
| `ecs/world.rs` | TypeId ベースの型消去で型安全なクエリを実現 |
| `renderer/mod.rs` | アダプター選択・パイプラインキャッシュ・プレゼントモードの選択ロジックが適切 |
| `renderer/pipeline.rs` | TOML 設定 + WGSL リフレクションでデータドリブンなパイプライン定義 |
| `app/mod.rs` のサブモジュール構成 | ipc_handler / actor_ops / transform_ops など責務分割の方向性は正しい |
| コメントの充実度 | クラス・関数・処理コメントが全体的に丁寧に記述されている |
| `draw_ctx.model_cache` | 同一パスの CPU モデル再利用でディスク読み込みを省略 |

---

## 改善優先ランキング

| 優先度 | 項目 | 工数感 |
|--------|------|--------|
| 🔴 1 | H-1: App 構造体分割 | 大 |
| 🔴 2 | H-2: IPC パーサー共通化 | 中 |
| 🔴 3 | H-3: DFS ウォーカー統一 | 中 |
| 🔴 4 | H-4: render.rs ハンドラ分割 | 中 |
| 🟠 5 | M-1: マジックナンバー定数化 | 小 |
| 🟠 6 | M-2: Mat4x4 型の活用 | 小 |
| 🟠 7 | M-4: build_hierarchy_json を serde_json に | 小 |
| 🟠 8 | M-5: P/Invoke 分離 | 小 |
| 🟠 9 | M-6: 安定アクター ID 設計 | 大 |
| 🟡 10 | L-1〜L-4: その他細部 | 極小 |
