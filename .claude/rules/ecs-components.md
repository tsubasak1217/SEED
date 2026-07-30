---
paths:
  - "runtime/src/engine/components/**"
  - "runtime/src/engine/systems/**"
  - "runtime/src/engine/core/app_base/**"
---

# ECSコンポーネント領域のルール

ECS の理念（**データ**＝コンポーネント／**ロジック**＝システムを分離）でコンポーネントを作る。
コンポーネントにメソッドロジックを詰めない。1 ファイル 1 責務・マジックナンバー禁止・コメント必須。

- **serde 互換を必ず保つ**。シリアライズ用データ型の全フィールドに `#[serde(default)]`（非ゼロ既定値は
  `#[serde(default = "fn")]`）を付ける。付け忘れると、そのフィールドが無い旧 `.scene` の読み込みが丸ごと失敗する。
  旧名互換は `#[serde(rename = "...")]`。
- **`ComponentKind` に variant を追加したら、関連する全 match の腕を漏れなく更新する**。
  更新先: `display_name()` / `Actor::to_data_recursive()`（保存）/ `rebuild_actor_slots()`（読込）/
  `handle_remove_component_slot()` / `handle_duplicate_component()` / `handle_add_component_to_actor()` /
  `send_actor_components()` / `rename_refs.rs` の `rewrite_refs_in_slots()`（アクタ名参照を持つ種別か判断して腕を足す。
  こちらは網羅 match なのでコンパイルエラーで気付ける）。
  既存コンポーネント名（例 `AudioComponent`）で **grep して足すべき箇所を全部洗い出す**。
  非網羅の match は `cargo build` が落として守ってくれるが、非 match の取りこぼしはコンパイルを通ってしまうので注意。
- **スロット専用 `entity` を `spawn` してから `insert` する**。`actor.entity` へ直接 insert すると同型コンポーネントの
  複数持ちが壊れる。ルート直付けは Transform / CanvasTransform のみ。
- 詳細な追加手順（ファイル配置・World 登録・エディタ連携・IPC）は **add-ecs-component Skill**。
  そのコンポーネントをスクリプト／AI 補完へ公開する手順は **add-script-api Skill** を使う。
