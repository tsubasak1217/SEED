// ============================================================
//  structs/components/mod.rs — engine::components への後方互換エイリアス
//
//  コンポーネントの正規実装は src/engine/components/ を参照。
//  このモジュールは既存コードとの互換性のために re-export を提供する。
// ============================================================

pub use crate::engine::components::model_component;
pub use crate::engine::components::script_component;
pub use crate::engine::components::*;
