// ============================================================
//  engine/interaction/mod.rs — インタラクションフィールド（Phase I）エンジン層
//
//  正典: docs/water_interaction_roadmap.md §1.3 / §2 I1。
//
//  ## この層の責務
//  「シーンから書き手（InteractionSource）を集め、**速度を持ったソース列**にする」までが
//  本モジュールの責務。GPU リソース・場テクスチャ・コンピュートパスは
//  `core::renderer::interaction` の責務であり、本モジュールは wgpu を一切知らない
//  （水系の `engine::water`（解決）と `renderer::water`（描画）の分離と同じ構造）。
//
//  ## 場の階層（設計の要）
//  ロードマップ §1.3 の通り、寿命の違いで場を 2 種類に分ける:
//    ・**瞬発場**   … カメラ追従の小窓。数秒で減衰。**I1 で実装済み。**
//    ・**永続変形** … 地形チャンクに紐づく蓄積テクスチャ（轍）。I3 で追加予定。
//  本モジュールのソース収集は両者で共有される（書き手は場の種類を知らない）。
// ============================================================

/// シーン走査（Actor ツリー → ワールド解決済みソース列）。
pub mod collect;
/// ワールド解決済みソースの中間表現。
pub mod resolved;
/// フレーム間の位置差分から速度を求めるトラッカー。
pub mod velocity;
/// 水面付近のソースへ波の注入量を割り当てる（Phase I2）。
pub mod water_wave;
/// 水域ごとの物性（粘度・波紋の減衰率）→ 波動方程式の係数（Phase I2.1）。
pub mod water_physics;

pub use collect::collect_interaction_sources;
pub use resolved::{source_key, ResolvedInteractionSource};
pub use velocity::{InteractionSourceVelocityTracker, MovingInteractionSource};
pub use water_wave::{apply_water_wave_injection, flow_event_wave_source};
pub use water_physics::{
    collect_water_physics_regions, sanitize_ripple_damping_rate,
    viscosity_stamp_scale, viscosity_wave_speed_scale,
    WaterPhysicsRegion, RIPPLE_DAMPING_RATE_MAX, RIPPLE_DAMPING_RATE_MIN,
    VISCOSITY_MAX, VISCOSITY_MIN,
};
