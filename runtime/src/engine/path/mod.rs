// ============================================================
//  path/mod.rs — 汎用パス（コントロールポイント列）のエンジン層
//
//  `ControlPointComponent`（点列のデータ）と、それを使う側
//  （川スプライン・キャラクターの巡回・カメラパス・ビューポート描画）の
//  あいだに置く中間層。water モジュールと同じ構図で、
//  **点列のワールド解決と補間をここ 1 箇所に集約**する。
//
//    Actor + ControlPointComponent
//              │  PathEval::from_points（アクタ Transform でワールド解決）
//              ▼
//          [PathEval]  ←─ 唯一の中間表現
//        │        │        │
//        │        │        └─ ビューポート描画（点キューブ＋区間ライン）
//        │        └─ 川（次フェーズ: sample_polyline → RiverPath::build）
//        └─ 巡回・カメラパス（position_at_time / position_at_progress）
//
//  補間の基本演算（Catmull-Rom 等）は `interp`、点列の評価は `eval` に置く。
// ============================================================

// 汎用パスの公開 API は「点を置く側」より先に揃えてあるため、
// 現時点では消費側（川の統合・巡回・カメラパス）が未実装で、
// 一部の関数・定数がどこからも呼ばれず dead_code 警告になる。
// API は確定済みで後続フェーズがそのまま使うため、モジュール単位で警告を抑止する
//（water モジュールと同じ扱い）。
#![allow(dead_code)]
// 同じ理由で、まだ消費側の無い再輸出（PathSample / 各定数など）が
// unused_imports として報告される。API の一覧性を保つため再輸出は残す。
#![allow(unused_imports)]

pub mod interp;
pub mod eval;

pub use interp::{catmull_rom, distance3, lerp3, CATMULL_ROM_TENSION, PATH_EPSILON};
pub use eval::{
    PathEval, PathSample, ResolvedControlPoint,
    PATH_DEFAULT_STEP_M, PATH_MAX_POLYLINE_SEGMENTS, PATH_MIN_POINTS_FOR_SEGMENT, PATH_MIN_STEP_M,
};
