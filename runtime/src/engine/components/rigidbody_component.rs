// ============================================================
//  components/rigidbody_component.rs — レガシー互換用データ型
//
//  【注意】
//    RigidbodyComponent は ColliderComponent に統合された。
//    このファイルは旧フォーマット（Rigidbody が独立コンポーネント）の
//    シーンファイルを読み込む際の後方互換デシリアライズのためだけに残す。
//
//    新しいコードでは ColliderComponent の use_rigidbody フラグを使うこと。
// ============================================================

use serde::{Deserialize, Serialize};

// ─── RigidbodyComponentData（レガシー互換 シリアライズ用） ──────────────────

/// 旧フォーマットのシーンファイルに含まれる Rigidbody データを読み込むための型。
///
/// scene.rs の build_actor 関数が旧シーンをロードする際に使用し、
/// データは ColliderComponent のリジッドボディフィールドに移行される。
#[derive(Clone, Serialize, Deserialize)]
pub struct RigidbodyComponentData {
    pub mass:                     f32,
    pub restitution:              f32,
    pub friction:                 f32,
    pub linear_damping:           f32,
    pub angular_damping:          f32,
    pub gravity_scale:            f32,
    pub is_kinematic:             bool,
    #[serde(default)]
    pub freeze_position:          [bool; 3],
    #[serde(default)]
    pub freeze_rotation:          [bool; 3],
    #[serde(default)]
    pub initial_linear_velocity:  [f32; 3],
    #[serde(default)]
    pub initial_angular_velocity: [f32; 3],
}
