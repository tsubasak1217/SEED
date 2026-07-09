// ============================================================
//  physics/types2d.rs — SEED エンジン 2D 物理システム共通型定義
//
//  【責務】
//    2D 物理スレッドとアプリ層の間でやり取りする型を定義する。
//    外部クレート（rapier2d 等）への依存なし。
//    すべて純粋な Rust 型（配列・enum・struct）で構成する。
//
//  【座標系】
//    キャンバス座標（ピクセル）を物理座標（メートル）に変換して使用する。
//    変換: physics_m = canvas_px / PIXELS_PER_METER
//    Y 軸: キャンバスと同方向（正 = 画面下方向）。重力は +Y 方向。
//
//  【設計方針】
//    2D 位置は [f32; 2] 配列、回転は f32（ラジアン、反時計回り正）を使用する。
// ============================================================

// ─── 物理定数 ────────────────────────────────────────────────────────────────

/// 2D 物理シミュレーションの固定タイムステップ間隔（秒）
pub const PHYSICS_2D_FIXED_STEP: f64 = 1.0 / 60.0;

/// キャンバスピクセル ↔ 物理メートルの変換係数（1m = 100px）
pub const PIXELS_PER_METER: f32 = 100.0;

/// デフォルト 2D 重力加速度（m/s²、Y 軸下向き = キャンバス下方向）
pub const DEFAULT_GRAVITY_2D: [f32; 2] = [0.0, 9.81];

// ─── コライダー形状 ──────────────────────────────────────────────────────────

/// 2D 物理コライダーの形状バリアント。
#[derive(Clone, Debug)]
pub enum ColliderShape2d {
    /// 軸平行ボックス（各軸の半辺長、ピクセル単位）
    Box { half_extents: [f32; 2] },
    /// 円（半径、ピクセル単位）
    Circle { radius: f32 },
    /// カプセル（Y 軸方向・半径・半高さ、ピクセル単位）
    Capsule { radius: f32, half_height: f32 },
    /// 凸包（頂点リスト [x, y]、ピクセル単位）
    ConvexHull { vertices: Vec<[f32; 2]> },
}

// ─── リジッドボディ状態 ──────────────────────────────────────────────────────

/// 2D Dynamic / Kinematic リジッドボディのパラメータ。
///
/// `Collider2dComponent.use_rigidbody == true` のとき `PhysicsObject2d.rigidbody` に設定される。
#[derive(Clone, Debug)]
pub struct RigidBodyState2d {
    /// 質量（kg）
    pub mass:             f32,
    /// 反発係数（0 = 完全非弾性、1 = 完全弾性）
    pub restitution:      f32,
    /// 摩擦係数
    pub friction:         f32,
    /// 線形減衰係数（0 = 減衰なし）
    pub linear_damping:   f32,
    /// 角速度減衰係数
    pub angular_damping:  f32,
    /// 重力倍率（0 = 無重力、1 = 標準重力）
    pub gravity_scale:    f32,
    /// true なら Kinematic（外部で位置を制御）、false なら Dynamic
    pub is_kinematic:     bool,
    /// 位置フリーズ [X, Y]
    pub freeze_position:  [bool; 2],
    /// 回転フリーズ（2D は Z 軸のみ）
    pub freeze_rotation:  bool,
    /// Play 開始時の初期移動速度（m/s）[x, y]
    pub linear_velocity:  [f32; 2],
    /// Play 開始時の初期回転速度（rad/s）
    pub angular_velocity: f32,
}

impl RigidBodyState2d {
    /// 質量を指定してデフォルトパラメータの RigidBodyState2d を生成する。
    pub fn new(mass: f32) -> Self {
        Self {
            mass,
            restitution:     0.3,
            friction:        0.5,
            linear_damping:  0.01,
            angular_damping: 0.05,
            gravity_scale:   1.0,
            is_kinematic:    false,
            freeze_position: [false; 2],
            freeze_rotation: false,
            linear_velocity:  [0.0; 2],
            angular_velocity: 0.0,
        }
    }
}

// ─── 2D 物理オブジェクト ─────────────────────────────────────────────────────

/// 物理スレッドへ登録する 2D オブジェクトのデータ。
///
/// `entity_id` は DFS 順カウンタで、メインスレッドとのトランスフォーム同期に使用する。
/// 座標はメートル単位（PIXELS_PER_METER で変換済み）。
pub struct PhysicsObject2d {
    /// Actor の DFS 順識別 ID（シーン内で一意）
    pub entity_id:       u64,
    /// ワールド座標位置 [x, y]（メートル）
    pub position:        [f32; 2],
    /// 回転角度（ラジアン、反時計回り正）
    pub rotation:        f32,
    /// ワールドスケール [x, y]
    pub scale:           [f32; 2],
    /// コライダー形状（メートル単位で設定済み）
    pub collider:        ColliderShape2d,
    /// コライダーのローカルオフセット [x, y]（メートル）
    pub collider_offset: [f32; 2],
    /// リジッドボディ設定（None の場合は Static コライダー）
    pub rigidbody:       Option<RigidBodyState2d>,
    /// true なら衝突応答なし（トリガー）
    pub is_trigger:      bool,
    /// 物理レイヤービット
    pub physics_layer:   u32,
    /// 衝突対象レイヤーマスク（0 = 全レイヤーと衝突）
    pub layer_mask:      u32,
}

// ─── 物理スレッドコマンド ────────────────────────────────────────────────────

/// メインスレッドから 2D 物理スレッドへ送るコマンド。
pub enum PhysicsCommand2d {
    /// オブジェクトを物理ワールドへ追加する
    AddObject(PhysicsObject2d),
    /// オブジェクトを物理ワールドから削除する
    RemoveObject { entity_id: u64 },
    /// Kinematic オブジェクトの目標 Transform を設定する（次ステップで適用）
    UpdateKinematic {
        entity_id: u64,
        position:  [f32; 2],
        rotation:  f32,
    },
    /// 重力ベクトルを変更する
    SetGravity { gravity: [f32; 2] },
    /// 力を加える（継続的）
    ApplyForce { entity_id: u64, force: [f32; 2] },
    /// 速度変化（インパルス）を即時加える
    ApplyImpulse { entity_id: u64, impulse: [f32; 2] },
    /// ボディタイプを動的 ↔ キネマティックに一時切り替えする。
    /// `is_kinematic: false`（Dynamic 復帰）のとき `final_position` が Some なら、
    /// その座標を Rapier に即時セットしてから速度をゼロリセットする。
    SetBodyKinematic {
        entity_id: u64,
        is_kinematic: bool,
        /// Dynamic 復帰時に適用する最終座標 (position, rotation)。
        final_position: Option<([f32; 2], f32)>,
        /// true の場合、この kinematic ボディへの UpdateKinematic を「目標位置」として
        /// 扱い、物理スレッドが各ステップで最大速度クランプ付きに目標へ追従させる
        /// （スムーズドラッグ。3D 版 PhysicsCommand::SetBodyKinematic と同じ意味）。
        /// is_kinematic=false のときは無視される。
        smooth: bool,
    },
    /// リジッドボディに付属するコライダーの形状・スケール・オフセットを差し替える。
    ///
    /// ウィンドウリサイズ時に scale_size=true のアクターのコライダーサイズを更新するために使用する。
    /// リジッドボディの位置・速度・回転は保持される。
    /// Static コライダー（use_rigidbody=false）は RemoveObject + AddObject で再登録するため対象外。
    RescaleCollider {
        /// Actor の DFS 順識別 ID
        entity_id:       u64,
        /// 新しいコライダー形状（メートル単位で設定済み）
        shape:           ColliderShape2d,
        /// ワールドスケール [x, y]（build_collider_shape_2d に渡す）
        scale:           [f32; 2],
        /// コライダーのローカルオフセット [x, y]（メートル）
        collider_offset: [f32; 2],
    },
    /// 物理シミュレーションを一時停止する（速度・内部状態を保持したまま）。
    Pause,
    /// 物理シミュレーションを再開する（Pause の解除）。
    Resume,
    /// 指定ボディが「提案位置」で他の非 Dynamic・非センサーコライダーと
    /// 許容値を超えて重なる（めり込む）かを判定し、結果を reply チャンネルへ返す
    /// （同期問い合わせ）。3D 版 `PhysicsCommand::CheckKinematicOverlap` と同じ意味。
    ///
    /// 編集時のドラッグ押し戻し（トンネリング防止）に使用する。メインスレッドは
    /// ギズモが動かした「今フレームの提案位置そのもの」を毎フレーム検証できるため、
    /// 非同期な結果ストリーム（recv_latest）に依存した判定で生じる取りこぼしが
    /// 構造的に発生しない。
    ///
    /// Dynamic ボディとの重なりは対象外（RigidBody 有効モードでは kinematic の
    /// ドラッグボディが Dynamic を押しのけるため、一時的な重なりは正常）。
    CheckKinematicOverlap2d {
        /// 判定対象のボディ（ドラッグ中のオブジェクト）
        entity_id: u64,
        /// 提案ワールド位置 [x, y]（メートル）
        position:  [f32; 2],
        /// 提案回転角度（ラジアン）
        rotation:  f32,
        /// 結果送信チャンネル（true = めり込みあり＝押し戻しが必要）
        reply:     crossbeam_channel::Sender<bool>,
    },
    /// スレッドを停止する
    Stop,
}

// ─── 物理スレッド結果 ────────────────────────────────────────────────────────

/// 2D 物理スレッドからメインスレッドへ 1 ステップごとに送る結果。
pub struct PhysicsResult2d {
    /// Dynamic Rigidbody の更新後 Transform: (entity_id, position [x,y], rotation rad)
    pub transform_updates: Vec<(u64, [f32; 2], f32)>,
    /// 衝突イベントリスト
    pub collision_events:  Vec<CollisionEvent2d>,
    /// トリガーイベントリスト
    pub trigger_events:    Vec<TriggerEvent2d>,
    /// NarrowPhase から直接収集したアクティブ接触中のエンティティ ID リスト。
    pub active_contact_entity_ids: Vec<u64>,
    /// 現在オーバーラップ中のトリガーに関与するエンティティ ID リスト（毎フレーム送信）。
    pub active_trigger_entity_ids: Vec<u64>,
    /// 全 Dynamic ボディの並進速度の最大の大きさ（m/s）。収束停止判定用（3D と同じ）。
    pub max_linear_speed:  f32,
    /// 全 Dynamic ボディの角速度の最大の大きさ（rad/s）。
    pub max_angular_speed: f32,
    /// 全 Dynamic ボディの現在速度: (entity_id, linvel [x,y], angvel スカラー)。
    /// タブごとの物理状態退避（続きから再開）で唯一失われる「速度」を復元するために使う。
    /// entity_id は DFS 順識別 ID（メインスレッド側で ECS Entity へ変換する）。
    pub body_velocities:   Vec<(u64, [f32; 2], f32)>,
}

// ─── 衝突イベント ────────────────────────────────────────────────────────────

/// 2D の 2 オブジェクト間の衝突イベント。
pub struct CollisionEvent2d {
    pub entity_a: u64,
    pub entity_b: u64,
    pub phase:    CollisionPhase2d,
}

/// 衝突フェーズ（2D・3D 共通の意味）。
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum CollisionPhase2d {
    Enter,
    Stay,
    Exit,
}

// ─── トリガーイベント ────────────────────────────────────────────────────────

/// 2D トリガーコライダーへの進入・退出イベント。
pub struct TriggerEvent2d {
    pub trigger_entity: u64,
    pub other_entity:   u64,
    pub phase:          TriggerPhase2d,
}

/// トリガーフェーズ（2D）。
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum TriggerPhase2d {
    Enter,
    Exit,
}
