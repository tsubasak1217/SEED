// ============================================================
//  physics/types.rs — SEED エンジン物理システム共通型定義
//
//  【責務】
//    物理スレッドとアプリ層の間でやり取りする型を定義する。
//    外部クレート（rapier3d 等）への依存なし。
//    すべて純粋な Rust 型（配列・enum・struct）で構成する。
//
//  【設計方針】
//    位置・スケールは [f32; 3] 配列、クォータニオンは [x, y, z, w] 順の [f32; 4] 配列を使用する。
//    これにより、エンジン内の数学型・rapier3d 型への依存を断ち切る。
// ============================================================

// ─── タイムステップ定数 ──────────────────────────────────────────────────────

/// 物理シミュレーションの固定タイムステップ間隔（秒）
pub const PHYSICS_FIXED_STEP: f64 = 1.0 / 60.0;

/// デフォルト重力加速度（m/s²、Y 軸下向き）
pub const DEFAULT_GRAVITY: [f32; 3] = [0.0, -9.81, 0.0];

// ─── コライダー形状 ──────────────────────────────────────────────────────────

/// 物理コライダーの形状バリアント。
#[derive(Clone, Debug)]
pub enum ColliderShape {
    /// 軸平行ボックス（各軸の半辺長）
    Box { half_extents: [f32; 3] },
    /// 球（半径）
    Sphere { radius: f32 },
    /// カプセル（Y 軸方向・半径・半高さ）
    Capsule { radius: f32, half_height: f32 },
    /// シリンダー（Y 軸方向・半径・半高さ）
    Cylinder { radius: f32, half_height: f32 },
    /// コーン（Y 軸方向・底面半径・半高さ、頂点が +Y 側）
    Cone { radius: f32, half_height: f32 },
    /// 凸包（頂点リスト [x, y, z]）
    ConvexHull { vertices: Vec<[f32; 3]> },
    /// 三角形メッシュ（Static コライダー専用）
    TriangleMesh { triangles: Vec<[[f32; 3]; 3]> },
}

// ─── リジッドボディ状態 ──────────────────────────────────────────────────────

/// Dynamic / Kinematic リジッドボディのパラメータ。
///
/// `ColliderComponent.use_rigidbody == true` のとき `PhysicsObject.rigidbody` に設定される。
#[derive(Clone, Debug)]
pub struct RigidBodyState {
    /// 質量（kg）
    pub mass:             f32,
    /// 反発係数（0 = 完全非弾性、1 = 完全弾性）
    pub restitution:      f32,
    /// 摩擦係数（大きいほど減速する）
    pub friction:         f32,
    /// 移動速度の線形減衰係数（0 = 減衰なし）
    pub linear_damping:   f32,
    /// 回転速度の角速度減衰係数
    pub angular_damping:  f32,
    /// 重力の倍率（0 = 無重力、1 = 標準重力）
    pub gravity_scale:    f32,
    /// true なら Kinematic（外部で位置を制御）、false なら Dynamic（重力・衝突力を受ける）
    pub is_kinematic:     bool,
    /// 各軸の位置フリーズフラグ [X, Y, Z]
    pub freeze_position:  [bool; 3],
    /// 各軸の回転フリーズフラグ [X, Y, Z]
    pub freeze_rotation:  [bool; 3],
    /// Play 開始時の初期移動速度（m/s）[x, y, z]
    pub linear_velocity:  [f32; 3],
    /// Play 開始時の初期回転速度（rad/s）[x, y, z]
    pub angular_velocity: [f32; 3],
}

impl RigidBodyState {
    /// 質量を指定してデフォルトパラメータの RigidBodyState を生成する。
    pub fn new(mass: f32) -> Self {
        Self {
            mass,
            restitution:     0.3,
            friction:        0.5,
            linear_damping:  0.01,
            angular_damping: 0.05,
            gravity_scale:   1.0,
            is_kinematic:    false,
            freeze_position: [false; 3],
            freeze_rotation: [false; 3],
            linear_velocity:  [0.0; 3],
            angular_velocity: [0.0; 3],
        }
    }
}

// ─── 物理オブジェクト ────────────────────────────────────────────────────────

/// 物理スレッドへ登録するオブジェクトのデータ。
///
/// `entity_id` は DFS 順カウンタで、メインスレッドとのトランスフォーム同期に使用する。
pub struct PhysicsObject {
    /// Actor の DFS 順識別 ID（シーン内で一意）
    pub entity_id:       u64,
    /// ワールド座標位置 [x, y, z]
    pub position:        [f32; 3],
    /// クォータニオン回転 [x, y, z, w]（SEED 規約）
    pub rotation:        [f32; 4],
    /// ワールドスケール [x, y, z]
    pub scale:           [f32; 3],
    /// コライダー形状
    pub collider:        ColliderShape,
    /// コライダーの Actor Transform からのローカルオフセット [x, y, z]
    pub collider_offset: [f32; 3],
    /// リジッドボディ設定（None の場合は Static コライダー）
    pub rigidbody:       Option<RigidBodyState>,
    /// true なら衝突応答なし（トリガー）
    pub is_trigger:      bool,
    /// 物理レイヤービット
    pub physics_layer:   u32,
    /// 衝突対象レイヤーマスク（0 = 全レイヤーと衝突）
    pub layer_mask:      u32,
}

// ─── 物理スレッドコマンド ────────────────────────────────────────────────────

/// メインスレッドから物理スレッドへ送るコマンド。
pub enum PhysicsCommand {
    /// オブジェクトを物理ワールドへ追加する
    AddObject(PhysicsObject),
    /// オブジェクトを物理ワールドから削除する
    RemoveObject { entity_id: u64 },
    /// Kinematic オブジェクトの目標 Transform を設定する（次ステップで適用）
    UpdateKinematic {
        entity_id: u64,
        position:  [f32; 3],
        rotation:  [f32; 4],
    },
    /// 重力ベクトルを変更する
    SetGravity { gravity: [f32; 3] },
    /// 力を加える（継続的）
    ApplyForce { entity_id: u64, force: [f32; 3] },
    /// トルクを加える（継続的）
    ApplyTorque { entity_id: u64, torque: [f32; 3] },
    /// 速度変化（インパルス）を即時加える
    ApplyImpulse { entity_id: u64, impulse: [f32; 3] },
    /// ボディタイプを動的 ↔ キネマティックに一時切り替えする。
    /// ギズモドラッグ中に重力・衝突力を一時停止するために使用する。
    ///
    /// `is_kinematic: false`（Dynamic 復帰）のとき `final_position` が Some なら、
    /// その座標を Rapier に即時セットしてから速度をゼロリセットする。
    /// これにより「pending な set_next_kinematic_position が未適用のまま Dynamic 化
    /// されて元の場所に戻る」問題を回避する。
    SetBodyKinematic {
        entity_id: u64,
        is_kinematic: bool,
        /// Dynamic 復帰時に適用する最終座標 (position, rotation)。
        /// None なら Rapier の現在位置をそのまま維持する。
        final_position: Option<([f32; 3], [f32; 4])>,
        /// true の場合、この kinematic ボディへの UpdateKinematic を「目標位置」として
        /// 扱い、物理スレッドが各ステップで最大速度クランプ付きに目標へ追従させる
        /// （スムーズドラッグ）。編集時 RigidBody 有効モードのドラッグで使用し、
        /// 接触相手の Dynamic ボディへ伝わる速度を物理的に妥当な範囲へ抑える。
        ///
        /// false の場合は従来どおり目標を即時 set_next_kinematic_position する
        /// （1 ステップで到達 = 伝達速度 Δ/dt に上限なし）。RigidBody 無効の
        /// 押し戻しモードでは接触相手が動かず伝達の問題がないため即時のままにする。
        /// is_kinematic=false のときは無視される。
        smooth: bool,
    },
    /// 物理シミュレーションを一時停止する（速度・内部状態を保持したまま）。
    /// タイムライン停止時に使用する。Resume で再開できる。
    Pause,
    /// 物理シミュレーションを再開する（Pause の解除）。
    Resume,
    /// 指定ボディが「提案位置」で他の非 Dynamic・非センサーコライダーと
    /// 重なる（めり込む）かを判定し、結果を reply チャンネルへ返す（同期問い合わせ）。
    ///
    /// 編集時のドラッグ押し戻しに使用する。メインスレッドはギズモが動かした
    /// 「今フレームの提案位置そのもの」を毎フレーム検証できるため、
    /// 非同期な結果ストリーム（recv_latest）に依存した判定で生じていた
    /// 「ステップ未実行フレームで判定がスキップされる」「1 ステップ遅れで
    /// 位置対応がズレる」という間欠的な取りこぼしが構造的に発生しない。
    ///
    /// Dynamic ボディとの重なりは対象外（RigidBody 有効モードでは kinematic の
    /// ドラッグボディが Dynamic を押しのけるため、一時的な重なりは正常）。
    CheckKinematicOverlap {
        /// 判定対象のボディ（ドラッグ中のオブジェクト）
        entity_id: u64,
        /// 提案ワールド位置 [x, y, z]
        position:  [f32; 3],
        /// 提案クォータニオン回転 [x, y, z, w]（SEED 規約）
        rotation:  [f32; 4],
        /// 結果送信チャンネル（true = めり込みあり＝押し戻しが必要）
        reply:     crossbeam_channel::Sender<bool>,
    },
    /// レイキャストを実行し、結果を reply チャンネルへ返す（同期問い合わせ）。
    /// スクリプトの Physics.Raycast 用。物理スレッドはコマンドドレイン間隔
    /// （約 1ms）で応答するため、メインスレッドは短いタイムアウト付きで待機する。
    Raycast {
        /// レイの始点（ワールド座標）
        origin:       [f32; 3],
        /// レイの方向（正規化推奨）
        direction:    [f32; 3],
        /// 最大距離
        max_distance: f32,
        /// 結果送信チャンネル（ヒットなし = None）
        reply:        crossbeam_channel::Sender<Option<RaycastHit>>,
    },
    /// スレッドを停止する
    Stop,
}

// ─── 物理スレッド結果 ────────────────────────────────────────────────────────

/// 物理スレッドからメインスレッドへ 1 ステップごとに送る結果。
pub struct PhysicsResult {
    /// Dynamic Rigidbody の更新後 Transform: (entity_id, position [x,y,z], rotation [x,y,z,w])
    pub transform_updates: Vec<(u64, [f32; 3], [f32; 4])>,
    /// 衝突イベントリスト
    pub collision_events:  Vec<CollisionEvent>,
    /// トリガーイベントリスト
    pub trigger_events:    Vec<TriggerEvent>,
    /// NarrowPhase から直接収集したアクティブ接触中のエンティティ ID リスト。
    /// CollisionEvent が発火しないケース（kinematic vs static 等）でも正確に検出できる。
    /// 編集時コライダーのみモードのドラッグ押し戻し判定に使用する。
    pub active_contact_entity_ids: Vec<u64>,
    /// 現在オーバーラップ中のトリガーに関与するエンティティ ID リスト（毎フレーム送信）。
    /// Enter イベントは最初の 1 フレームしか来ないため、触れている間も色変更を継続する
    /// 用途にはこのリストを使用する。
    pub active_trigger_entity_ids: Vec<u64>,
    /// 全 Dynamic ボディの並進速度の最大の大きさ（m/s）。
    /// 編集時タイムラインの収束停止判定を「実際に静止したか（速度≈0）」で行うために使う。
    /// Dynamic ボディが無い場合は 0.0。
    pub max_linear_speed:  f32,
    /// 全 Dynamic ボディの角速度の最大の大きさ（rad/s）。
    pub max_angular_speed: f32,
}

// ─── 衝突イベント ────────────────────────────────────────────────────────────

/// 2 オブジェクト間の衝突イベント。
pub struct CollisionEvent {
    /// 衝突した一方の entity_id
    pub entity_a: u64,
    /// 衝突した他方の entity_id
    pub entity_b: u64,
    /// 衝突フェーズ
    pub phase:    CollisionPhase,
}

/// 衝突フェーズ。
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum CollisionPhase {
    /// 衝突開始（このフレームで接触が始まった）
    Enter,
    /// 衝突継続（前フレームから継続して接触中）
    Stay,
    /// 衝突終了（このフレームで接触が終わった）
    Exit,
}

// ─── トリガーイベント ────────────────────────────────────────────────────────

/// トリガーコライダーへの進入・退出イベント。
pub struct TriggerEvent {
    /// トリガーコライダー側の entity_id
    pub trigger_entity: u64,
    /// 相手側の entity_id
    pub other_entity:   u64,
    /// トリガーフェーズ
    pub phase:          TriggerPhase,
}

/// トリガーフェーズ。
#[derive(Clone, Copy, PartialEq, Eq)]
pub enum TriggerPhase {
    /// トリガーへ進入
    Enter,
    /// トリガーから退出
    Exit,
}

// ─── レイキャスト ────────────────────────────────────────────────────────────

/// レイキャストのヒット情報。
pub struct RaycastHit {
    /// ヒットしたオブジェクトの entity_id
    pub entity_id: u64,
    /// ヒット点のワールド座標 [x, y, z]
    pub point:     [f32; 3],
    /// ヒット点の法線ベクトル [x, y, z]
    pub normal:    [f32; 3],
    /// レイ始点からヒット点までの距離
    pub distance:  f32,
}
