// ============================================================
//  touch/ — 複数指のタッチ入力（段階A: docs/android.md「タッチ入力」）
//
//  【構成】
//    phase  … 指 1 本のこのフレームでの段階（Began / Moved / Stationary / Ended / Canceled）
//    state  … 複数指の状態機械（生イベント → フレーム単位の指の一覧。純ロジック）
//    bridge … マウス ⇔ タッチの相互変換と入力源の調停（プラットフォーム特性で ON/OFF）
//    test_sequence … 検証用の合成タッチ列（Android の debug.seed.touch_test=1 のときだけ流れる）
//
//  入力状態の持ち主は従来どおり `Input`（ECS のリソース相当。App が 1 つ保持する）。
//  `Input` がイベントを座標写像してから bridge へ渡し、bridge が TouchState / MouseState を更新する。
//  スクリプトからは `SEED.Input.TouchCount` / `GetTouch(i)` / `Touches` で読む（host_api.rs）。
// ============================================================

pub mod bridge;
pub mod phase;
pub mod state;
pub mod test_sequence;

pub use bridge::{PointerBridge, PointerBridgePolicy, MOUSE_FINGER_RAW_ID};
pub use phase::TouchPhase;
pub use state::{PrimaryFinger, TouchPoint, TouchState, MAX_TOUCHES};
