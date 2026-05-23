// ============================================================
//  components/collider2d_component.rs — 2D コライダー + リジッドボディ統合 ECS コンポーネント
//
//  【責務】
//    2D キャンバス上の Actor に衝突形状を定義する（Collider2dComponent）。
//    use_rigidbody フラグを true にすることで 2D 物理演算が有効になる。
//
//  【モード】
//    use_rigidbody = false : Static コライダー（移動しない床・壁など）
//    use_rigidbody = true  : 2D 物理演算対象
//      is_kinematic = false : Dynamic（重力・衝突力を受けて自動で動く）
//      is_kinematic = true  : Kinematic（スクリプト制御、衝突検出のみ）
//
//  【単位】
//    形状寸法・オフセット・初速度はキャンバスピクセル単位で保持する。
//    物理スレッドへ送信する際に PIXELS_PER_METER で除算してメートルに変換する。
// ============================================================

use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;
use crate::engine::physics::{ColliderShape2d, RigidBodyState2d, PIXELS_PER_METER};

// ─── コライダー形状のシリアライズ可能バリアント ──────────────────────────────

/// ColliderShape2d のシリアライズ用データ表現。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "type")]
pub enum ColliderShape2dData {
    /// 軸平行ボックス（各軸の半辺長、ピクセル単位）
    Box {
        #[serde(default = "default_half_extents_2d")]
        half_extents: [f32; 2],
    },
    /// 円（半径、ピクセル単位）
    Circle {
        #[serde(default = "default_radius_2d")]
        radius: f32,
    },
    /// カプセル（Y 軸方向、半径・半高さ、ピクセル単位）
    Capsule {
        #[serde(default = "default_radius_2d")]
        radius: f32,
        #[serde(default = "default_half_height_2d")]
        half_height: f32,
    },
    /// 凸包（頂点リスト [x, y]、ピクセル単位）
    ConvexHull {
        vertices: Vec<[f32; 2]>,
    },
}

fn default_half_extents_2d() -> [f32; 2] { [50.0, 50.0] }
fn default_radius_2d()        -> f32       { 50.0 }
fn default_half_height_2d()   -> f32       { 100.0 }

impl ColliderShape2dData {
    /// ピクセル単位の形状データを PIXELS_PER_METER で除算して
    /// メートル単位の `physics::ColliderShape2d` に変換する。
    pub fn to_physics_shape(&self) -> ColliderShape2d {
        let s = PIXELS_PER_METER;
        match self {
            Self::Box { half_extents } =>
                ColliderShape2d::Box { half_extents: [half_extents[0] / s, half_extents[1] / s] },
            Self::Circle { radius } =>
                ColliderShape2d::Circle { radius: radius / s },
            Self::Capsule { radius, half_height } =>
                ColliderShape2d::Capsule { radius: radius / s, half_height: half_height / s },
            Self::ConvexHull { vertices } =>
                ColliderShape2d::ConvexHull {
                    vertices: vertices.iter().map(|&[x, y]| [x / s, y / s]).collect(),
                },
        }
    }

    /// エディタ表示名を返す。
    pub fn display_name(&self) -> &'static str {
        match self {
            Self::Box { .. }         => "Box2d",
            Self::Circle { .. }      => "Circle",
            Self::Capsule { .. }     => "Capsule2d",
            Self::ConvexHull { .. }  => "ConvexHull2d",
        }
    }
}

// ─── Collider2dComponent ─────────────────────────────────────────────────────

/// 2D コライダー + リジッドボディ統合コンポーネント。
///
/// use_rigidbody = false の場合は Static コライダーとして機能する（床・壁など）。
/// use_rigidbody = true の場合は 2D 物理演算が有効になる。
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Collider2dComponent {
    // ─── コライダー設定 ──────────────────────────────────────────────────────

    /// コライダー形状（ピクセル単位）
    pub shape:         ColliderShape2dData,
    /// CanvasTransform からのオフセット（ローカル空間、ピクセル単位）
    #[serde(default)]
    pub offset:        [f32; 2],
    /// true なら衝突応答なし（衝突イベントのみ発火する）
    #[serde(default)]
    pub is_trigger:    bool,
    /// 物理レイヤービット（デフォルト: レイヤー 1）
    #[serde(default = "default_layer_2d")]
    pub physics_layer: u32,
    /// 衝突対象レイヤーマスク（0 = 全レイヤーと衝突）
    #[serde(default)]
    pub layer_mask:    u32,

    // ─── リジッドボディ設定（use_rigidbody = true の時のみ有効） ────────────

    /// true なら 2D 物理演算（重力・衝突力）を有効にする
    #[serde(default)]
    pub use_rigidbody: bool,
    /// 質量（kg）
    #[serde(default = "default_mass_2d")]
    pub mass:          f32,
    /// 反発係数（0 = 完全非弾性、1 = 完全弾性）
    #[serde(default = "default_restitution_2d")]
    pub restitution:   f32,
    /// 摩擦係数
    #[serde(default = "default_friction_2d")]
    pub friction:      f32,
    /// 線形減衰係数
    #[serde(default = "default_linear_damping_2d")]
    pub linear_damping:  f32,
    /// 角速度減衰係数
    #[serde(default = "default_angular_damping_2d")]
    pub angular_damping: f32,
    /// 重力倍率（0 = 無重力、1 = 標準重力）
    #[serde(default = "default_gravity_scale_2d")]
    pub gravity_scale:   f32,
    /// true なら物理による位置更新を行わない
    #[serde(default)]
    pub is_kinematic:    bool,
    /// 位置フリーズ [X, Y]
    #[serde(default)]
    pub freeze_position: [bool; 2],
    /// 回転フリーズ（2D は Z 軸のみ）
    #[serde(default)]
    pub freeze_rotation: bool,
    /// Play 開始直後に適用される初期移動速度（px/s）
    #[serde(default)]
    pub initial_linear_velocity:  [f32; 2],
    /// Play 開始直後に適用される初期回転速度（rad/s）
    #[serde(default)]
    pub initial_angular_velocity: f32,
}

// ─── デフォルト値ファクトリ ──────────────────────────────────────────────────

fn default_layer_2d()          -> u32 { 1   }
fn default_mass_2d()           -> f32 { 1.0 }
fn default_restitution_2d()    -> f32 { 0.3 }
fn default_friction_2d()       -> f32 { 0.5 }
fn default_linear_damping_2d() -> f32 { 0.01 }
fn default_angular_damping_2d()-> f32 { 0.05 }
fn default_gravity_scale_2d()  -> f32 { 1.0 }

impl Default for Collider2dComponent {
    fn default() -> Self {
        Self {
            shape:         ColliderShape2dData::Box { half_extents: [50.0, 50.0] },
            offset:        [0.0; 2],
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
            freeze_position:         [false; 2],
            freeze_rotation:         false,
            initial_linear_velocity:  [0.0; 2],
            initial_angular_velocity: 0.0,
        }
    }
}

impl Collider2dComponent {
    /// Collider2dComponent の設定から RigidBodyState2d を生成する。
    ///
    /// 速度はピクセル/秒 → メートル/秒に変換する（PIXELS_PER_METER で除算）。
    pub fn to_rigidbody_state(&self) -> RigidBodyState2d {
        let mut rb = RigidBodyState2d::new(self.mass);
        rb.restitution      = self.restitution;
        rb.friction         = self.friction;
        rb.linear_damping   = self.linear_damping;
        rb.angular_damping  = self.angular_damping;
        rb.gravity_scale    = self.gravity_scale;
        rb.is_kinematic     = self.is_kinematic;
        rb.freeze_position  = self.freeze_position;
        rb.freeze_rotation  = self.freeze_rotation;
        // 速度: px/s → m/s
        rb.linear_velocity  = [
            self.initial_linear_velocity[0] / PIXELS_PER_METER,
            self.initial_linear_velocity[1] / PIXELS_PER_METER,
        ];
        rb.angular_velocity = self.initial_angular_velocity;
        rb
    }
}

// ECS コンポーネントとして登録
impl Component for Collider2dComponent {}

// ─── Collider2dComponentData（シリアライズ用） ──────────────────────────────

/// シーンファイル保存・Undo スナップショット用のデータ型。
#[derive(Clone, Serialize, Deserialize)]
pub struct Collider2dComponentData {
    pub shape:         ColliderShape2dData,
    pub offset:        [f32; 2],
    pub is_trigger:    bool,
    pub physics_layer: u32,
    pub layer_mask:    u32,
    #[serde(default)]
    pub use_rigidbody: bool,
    #[serde(default = "default_mass_2d")]
    pub mass:          f32,
    #[serde(default = "default_restitution_2d")]
    pub restitution:   f32,
    #[serde(default = "default_friction_2d")]
    pub friction:      f32,
    #[serde(default = "default_linear_damping_2d")]
    pub linear_damping:  f32,
    #[serde(default = "default_angular_damping_2d")]
    pub angular_damping: f32,
    #[serde(default = "default_gravity_scale_2d")]
    pub gravity_scale:   f32,
    #[serde(default)]
    pub is_kinematic:    bool,
    #[serde(default)]
    pub freeze_position: [bool; 2],
    #[serde(default)]
    pub freeze_rotation: bool,
    #[serde(default)]
    pub initial_linear_velocity:  [f32; 2],
    #[serde(default)]
    pub initial_angular_velocity: f32,
}

impl From<&Collider2dComponent> for Collider2dComponentData {
    fn from(c: &Collider2dComponent) -> Self {
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

impl From<Collider2dComponentData> for Collider2dComponent {
    fn from(d: Collider2dComponentData) -> Self {
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
