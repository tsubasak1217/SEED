// ============================================================
//  engine/placement — ロジック配置（パターン生成）サブシステム
//
//  【何のためのモジュールか】
//  「円形に 12 個」「5×5 のグリッド」「範囲内にランダムで 30 個」のような
//  **規則的な配置**を 1 操作で作るための純粋なアルゴリズム層。
//
//  【層の切り分け（ECS 理念）】
//    ・spec.rs     … 「どう並べるか」のデータ（パターンとパラメータ）
//    ・generate.rs … データ → 点列の純関数（決定的・依存ゼロ）
//    ・rng.rs      … 決定的乱数（splitmix64）
//  シーンへの反映（アクタ生成・地形接地・Undo）は
//  `engine::core::app_base::app::logic_placement_ops` が担い、本モジュールは
//  ECS も IPC も知らない。点列の消費側（新規アクタ群 / ControlPoint の点列）を
//  増やしても、ここには手を入れなくてよい。
//
//  【エディタとの二重実装について】
//  ダイアログの俯瞰プレビューは C#（`editor/src/Placement/Patterns/`）が
//  同じアルゴリズムを写して描く。IPC 往復を挟むとパラメータ操作の即時性が
//  出せないためで、**正典は本モジュール**（実生成は必ずランタイムが行う）。
//  両者の一致は双方のユニットテストが同じ既知入力の期待値で固定する。
// ============================================================

pub mod generate;
pub mod rng;
pub mod spec;

#[cfg(test)]
mod tests;

pub use generate::{generate_points, MAX_PLACEMENT_POINTS};
pub use rng::PlacementRng;
pub use spec::{PlacementPattern, PlacementPoint, PlacementResult, PlacementSpec};
