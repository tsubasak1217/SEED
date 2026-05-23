// ============================================================
//  components/collider_component.rs — コライダー + リジッドボディ統合 ECS コンポーネント
//
//  【責務】
//    Actor に衝突形状を定義する（ColliderComponent）。
//    use_rigidbody フラグを true にすることで物理演算（重力・衝突応答）が有効になる。
//    以前は独立していた RigidbodyComponent をこのコンポーネントに統合した。
//
//  【モード】
//    use_rigidbody = false : Static コライダー（移動しない床・壁など）
//    use_rigidbody = true  : 物理演算対象
//      is_kinematic = false : Dynamic（重力・衝突力を受けて自動で動く）
//      is_kinematic = true  : Kinematic（スクリプト制御、衝突検出のみ）
// ============================================================

use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;
use crate::engine::physics::{ColliderShape, RigidBodyState};

// ─── コライダー形状のシリアライズ可能バリアント ──────────────────────────────

/// ColliderShape のシリアライズ用データ表現。
///
/// physics::ColliderShape は Vec<Vector3<f32>> を持つため
/// serde derive が直接適用できないケースを考慮してラップする。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "type")]
pub enum ColliderShapeData {
    /// 軸平行ボックス
    Box {
        #[serde(default = "default_half_extents")]
        half_extents: [f32; 3],
    },
    /// 球
    Sphere {
        #[serde(default = "default_radius")]
        radius: f32,
    },
    /// カプセル（Y 軸方向）
    Capsule {
        #[serde(default = "default_radius")]
        radius: f32,
        #[serde(default = "default_half_height")]
        half_height: f32,
    },
    /// シリンダー（Y 軸方向）
    Cylinder {
        #[serde(default = "default_radius")]
        radius: f32,
        #[serde(default = "default_half_height")]
        half_height: f32,
    },
    /// コーン（Y 軸方向、頂点が +Y 側、底面が -Y 側）
    Cone {
        #[serde(default = "default_radius")]
        radius: f32,
        #[serde(default = "default_half_height")]
        half_height: f32,
    },
    /// 凸包（頂点リスト、最大 CONVEX_HULL_MAX_VERTICES）
    ConvexHull {
        vertices: Vec<[f32; 3]>,
    },
    /// 三角形メッシュ（Static 専用、最大 TRIANGLE_MESH_MAX_TRIANGLES）
    TriangleMesh {
        triangles: Vec<[[f32; 3]; 3]>,
    },
}

fn default_half_extents() -> [f32; 3] { [0.5, 0.5, 0.5] }
fn default_radius()       -> f32       { 0.5 }
fn default_half_height()  -> f32       { 1.0 }

impl ColliderShapeData {
    /// physics::ColliderShape に変換する。
    pub fn to_physics_shape(&self) -> ColliderShape {
        match self {
            Self::Box { half_extents }  => ColliderShape::Box { half_extents: *half_extents },
            Self::Sphere { radius }     => ColliderShape::Sphere { radius: *radius },
            Self::Capsule { radius, half_height } =>
                ColliderShape::Capsule { radius: *radius, half_height: *half_height },
            Self::Cylinder { radius, half_height } =>
                ColliderShape::Cylinder { radius: *radius, half_height: *half_height },
            Self::Cone { radius, half_height } =>
                ColliderShape::Cone { radius: *radius, half_height: *half_height },
            Self::ConvexHull { vertices }    =>
                ColliderShape::ConvexHull { vertices: vertices.clone() },
            Self::TriangleMesh { triangles } =>
                ColliderShape::TriangleMesh { triangles: triangles.clone() },
        }
    }

    /// 表示名を返す。
    pub fn display_name(&self) -> &'static str {
        match self {
            Self::Box { .. }          => "Box",
            Self::Sphere { .. }       => "Sphere",
            Self::Capsule { .. }      => "Capsule",
            Self::Cylinder { .. }     => "Cylinder",
            Self::Cone { .. }         => "Cone",
            Self::ConvexHull { .. }   => "ConvexHull",
            Self::TriangleMesh { .. } => "TriangleMesh (Static only)",
        }
    }
}

// ─── ColliderComponent ───────────────────────────────────────────────────────

/// コライダー + リジッドボディ統合コンポーネント。
///
/// use_rigidbody = false の場合は Static コライダーとして機能する（床・壁など）。
/// use_rigidbody = true の場合は物理演算が有効になる。
///   - is_kinematic = false: Dynamic（重力・衝突力を受けて自動で動く）
///   - is_kinematic = true:  Kinematic（スクリプトで位置を制御し、衝突は検出）
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ColliderComponent {
    // ─── コライダー設定 ──────────────────────────────────────────────────────

    /// コライダー形状
    pub shape:         ColliderShapeData,
    /// Transform からのオフセット（ローカル空間、メートル）
    #[serde(default)]
    pub offset:        [f32; 3],
    /// true なら衝突応答なし（衝突イベントのみ発火する）
    #[serde(default)]
    pub is_trigger:    bool,
    /// 物理レイヤービット（デフォルト: レイヤー 1）
    #[serde(default = "default_layer")]
    pub physics_layer: u32,
    /// 衝突対象レイヤーマスク（0 = 全レイヤーと衝突）
    #[serde(default)]
    pub layer_mask:    u32,

    // ─── リジッドボディ設定（use_rigidbody = true の時のみ有効） ────────────

    /// true なら物理演算（重力・衝突力）を有効にする
    #[serde(default)]
    pub use_rigidbody: bool,
    /// 質量（kg）。0 以下なら無限大（Static）扱い
    #[serde(default = "default_mass")]
    pub mass:          f32,
    /// 反発係数（0 = 完全非弾性・跳ねない、1 = 完全弾性・完全に跳ね返る）
    #[serde(default = "default_restitution")]
    pub restitution:   f32,
    /// 摩擦係数（大きいほど摩擦で減速）
    #[serde(default = "default_friction")]
    pub friction:      f32,
    /// 移動速度の減衰係数（0 = 減衰なし、毎フレーム velocity *= (1 - damping)）
    #[serde(default = "default_linear_damping")]
    pub linear_damping:  f32,
    /// 回転速度の減衰係数
    #[serde(default = "default_angular_damping")]
    pub angular_damping: f32,
    /// 重力の倍率（0 = 無重力、1 = 通常重力、2 = 2倍重力）
    #[serde(default = "default_gravity_scale")]
    pub gravity_scale:   f32,
    /// true なら物理による位置更新を行わない（スクリプトで直接移動する）
    #[serde(default)]
    pub is_kinematic:    bool,
    /// 位置のフリーズ（X/Y/Z 軸個別）
    #[serde(default)]
    pub freeze_position: [bool; 3],
    /// 回転のフリーズ（X/Y/Z 軸個別）
    #[serde(default)]
    pub freeze_rotation: [bool; 3],
    /// Play 開始直後に適用される初期移動速度（m/s）
    #[serde(default)]
    pub initial_linear_velocity:  [f32; 3],
    /// Play 開始直後に適用される初期回転速度（rad/s）
    #[serde(default)]
    pub initial_angular_velocity: [f32; 3],
}

// ─── デフォルト値ファクトリ ──────────────────────────────────────────────────

fn default_layer()          -> u32 { 1   }
fn default_mass()           -> f32 { 1.0 }
fn default_restitution()    -> f32 { 0.3 }
fn default_friction()       -> f32 { 0.5 }
fn default_linear_damping() -> f32 { 0.01 }
fn default_angular_damping()-> f32 { 0.05 }
fn default_gravity_scale()  -> f32 { 1.0 }

impl Default for ColliderComponent {
    fn default() -> Self {
        Self {
            shape:         ColliderShapeData::Box { half_extents: [0.5, 0.5, 0.5] },
            offset:        [0.0; 3],
            is_trigger:    false,
            physics_layer: 1,
            layer_mask:    0,
            use_rigidbody: false,
            mass:                    1.0,
            restitution:             0.3,
            friction:                0.5,
            linear_damping:          0.01,
            angular_damping:         0.05,
            gravity_scale:           1.0,
            is_kinematic:            false,
            freeze_position:         [false; 3],
            freeze_rotation:         [false; 3],
            initial_linear_velocity:  [0.0; 3],
            initial_angular_velocity: [0.0; 3],
        }
    }
}

impl ColliderComponent {
    /// ColliderComponent の設定から RigidBodyState を生成する。
    ///
    /// use_rigidbody = true の Actor が物理スレッドに登録される際に呼ばれる。
    /// 慣性テンソルは Rapier が形状と質量から自動算出するため手動設定は不要。
    pub fn to_rigidbody_state(&self) -> RigidBodyState {
        let mut rb = RigidBodyState::new(self.mass);
        rb.restitution      = self.restitution;
        rb.friction         = self.friction;
        rb.linear_damping   = self.linear_damping;
        rb.angular_damping  = self.angular_damping;
        rb.gravity_scale    = self.gravity_scale;
        rb.is_kinematic     = self.is_kinematic;
        rb.freeze_position  = self.freeze_position;
        rb.freeze_rotation  = self.freeze_rotation;
        rb.linear_velocity  = self.initial_linear_velocity;
        rb.angular_velocity = self.initial_angular_velocity;
        rb
    }
}

// ECS コンポーネントとして登録
impl Component for ColliderComponent {}

// ─── ColliderComponentData（シリアライズ用） ────────────────────────────────

/// シーンファイル保存・Undo スナップショット用のデータ型。
#[derive(Clone, Serialize, Deserialize)]
pub struct ColliderComponentData {
    pub shape:         ColliderShapeData,
    pub offset:        [f32; 3],
    pub is_trigger:    bool,
    pub physics_layer: u32,
    pub layer_mask:    u32,
    // リジッドボディ設定（use_rigidbody が false なら無視）
    #[serde(default)]
    pub use_rigidbody: bool,
    #[serde(default = "default_mass")]
    pub mass:          f32,
    #[serde(default = "default_restitution")]
    pub restitution:   f32,
    #[serde(default = "default_friction")]
    pub friction:      f32,
    #[serde(default = "default_linear_damping")]
    pub linear_damping:  f32,
    #[serde(default = "default_angular_damping")]
    pub angular_damping: f32,
    #[serde(default = "default_gravity_scale")]
    pub gravity_scale:   f32,
    #[serde(default)]
    pub is_kinematic:    bool,
    #[serde(default)]
    pub freeze_position: [bool; 3],
    #[serde(default)]
    pub freeze_rotation: [bool; 3],
    #[serde(default)]
    pub initial_linear_velocity:  [f32; 3],
    #[serde(default)]
    pub initial_angular_velocity: [f32; 3],
}

impl From<&ColliderComponent> for ColliderComponentData {
    fn from(c: &ColliderComponent) -> Self {
        Self {
            shape:         c.shape.clone(),
            offset:        c.offset,
            is_trigger:    c.is_trigger,
            physics_layer: c.physics_layer,
            layer_mask:    c.layer_mask,
            use_rigidbody: c.use_rigidbody,
            mass:                    c.mass,
            restitution:             c.restitution,
            friction:                c.friction,
            linear_damping:          c.linear_damping,
            angular_damping:         c.angular_damping,
            gravity_scale:           c.gravity_scale,
            is_kinematic:            c.is_kinematic,
            freeze_position:         c.freeze_position,
            freeze_rotation:         c.freeze_rotation,
            initial_linear_velocity:  c.initial_linear_velocity,
            initial_angular_velocity: c.initial_angular_velocity,
        }
    }
}

impl From<ColliderComponentData> for ColliderComponent {
    fn from(d: ColliderComponentData) -> Self {
        Self {
            shape:         d.shape,
            offset:        d.offset,
            is_trigger:    d.is_trigger,
            physics_layer: d.physics_layer,
            layer_mask:    d.layer_mask,
            use_rigidbody: d.use_rigidbody,
            mass:                    d.mass,
            restitution:             d.restitution,
            friction:                d.friction,
            linear_damping:          d.linear_damping,
            angular_damping:         d.angular_damping,
            gravity_scale:           d.gravity_scale,
            is_kinematic:            d.is_kinematic,
            freeze_position:         d.freeze_position,
            freeze_rotation:         d.freeze_rotation,
            initial_linear_velocity:  d.initial_linear_velocity,
            initial_angular_velocity: d.initial_angular_velocity,
        }
    }
}
