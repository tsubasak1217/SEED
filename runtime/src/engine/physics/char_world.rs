// ============================================================
//  physics/char_world.rs — メインスレッド常駐のキャラクター衝突ミラー（KCC その場解決）
//
//  【なぜメインスレッド解決なのか（この設計の核心）】
//    キャラクターコントローラー（`is_character_controller=ON` のアクターを、スクリプトの
//    `transform.Position` 直書きだけで地形へ押し戻す機能）は、以前は物理スレッドとの
//    毎フレーム往復で実装していたが、次の 2 つの根本問題で破綻した:
//
//      (1) 物理スレッドの飢餓: 描画負荷（草生成の rayon が全コア飽和）で物理スレッドが
//          OS に 15〜140ms スケジュールされない瞬間がある。同期往復はその間まるごと
//          メインスレッドをブロックして激重になり、非同期往復は「古い解決結果」と
//          「最新の希望位置」がレースしてラバーバンド・入力食いを起こした。
//      (2) フィードバックループ: メイン→物理→メインの往復自体が、上記スケジューリング
//          遅延を増幅する構造だった。
//
//    一方、衝突解決の実処理（rapier KCC `move_shape`）は実測 0.03ms と極めて速く、
//    計算負荷はゼロに等しい。重かったのは往復のスケジューリングであって計算ではない。
//
//    そこで本モジュールは、物理スレッドと同一形状のコライダー集合（ミラー）を
//    **メインスレッドに常駐**させ、キャラの押し戻しをその場（同一スレッド・同一フレーム）で
//    完結させる。スレッド間フィードバックループを完全に排除するため、飢餓もレースも起きない。
//    物理スレッドへはキャラ位置を**一方通行**で通知するだけ（Raycast や他物体との衝突検出用）。
//
//  【動く物体同士の押し戻し要件】
//    ミラーには kinematic/dynamic/他キャラのコライダーも載せ、毎フレーム ECS の現在姿勢へ
//    追従させる（`set_position`）。これにより「動いている床の上のキャラ」「キャラ同士」の
//    押し戻しも解決できる。接触した dynamic には別途インパルスを送って押せるようにする
//    （`resolve_character` が接触 dynamic を返す → 呼び出し側が ApplyImpulse）。
//
//  【motion の基準（単一情報源 = ECS）】
//    各キャラは「前回解決済みワールド位置」`char_last_pos` を保持する。
//    `motion = desired − char_last_pos`。解決後に `char_last_pos` を補正後位置へ更新し、
//    その補正後位置を ECS へ書き戻す。次フレームのスクリプトは `Position += move` を足すので
//    `desired = corrected + move` となり、`motion = move` がちょうど成立する。
// ============================================================

use std::collections::HashMap;

use rapier3d::prelude::*;
use rapier3d::control::{CharacterAutostep, CharacterLength, KinematicCharacterController};

use super::shape::{build_collider_shape, to_isometry};
use super::types::PhysicsObject;

// ─── キャラクターコントローラー（KCC）パラメータ定数 ──────────────────────────
//
//  KCC の設定値。rapier3d の既定値をベースに、SEED のメートル単位（1 unit = 1m）に
//  合わせて段差・スロープ・スナップの各しきい値を絶対値で固定する（将来コンポーネント化して
//  データドリブンに差し替える想定で、まずは定数化する）。物理スレッド旧実装と同値。

/// キャラクターと周囲の間に保つ微小な隙間（メートル・スキン幅）。
/// 0 だと数値的に不安定になるため小さな正の値を絶対指定する（1cm）。
const KCC_SKIN_OFFSET_M: Real = 0.01;

/// 登れる最大スロープ角（度）。これを超える斜面は壁として扱い、登らず滑る。
/// 45 度は rapier 既定（π/4）と同じで、一般的なキャラ移動の妥当値。
const KCC_MAX_SLOPE_CLIMB_DEG: Real = 45.0;

/// 自動で滑り落ち始める最小スロープ角（度）。これ未満の斜面には立てる。
/// climb と同じ 45 度にし、「登れる限界＝立てる限界」で挙動を一貫させる。
const KCC_MIN_SLOPE_SLIDE_DEG: Real = 45.0;

/// 自動で乗り越えられる段差（階段）の最大高さ（メートル・絶対値）。
/// 0.3m は一般的な階段 1 段ぶんに相当し、キャラが引っかからず登れる。
const KCC_AUTOSTEP_MAX_HEIGHT_M: Real = 0.3;

/// 段差を登った先に必要な最小の平地幅（メートル・絶対値）。
/// これ未満の狭い足場には段差昇降しない（縁に爪先立ちして揺れるのを防ぐ）。
const KCC_AUTOSTEP_MIN_WIDTH_M: Real = 0.05;

/// 地面から足がこの距離以内なら接地状態へスナップする（メートル・絶対値）。
/// 下り坂・階段を降りるときにキャラが浮かず地面に吸着する。0.3m は段差高さと同程度。
const KCC_SNAP_TO_GROUND_M: Real = 0.3;

/// キャラ → dynamic 物体を押すインパルスの係数（実効質量係数・kg 相当）。
///
/// `resolve_character` は接触 dynamic ごとに「押し出し方向 × キャラ水平速度[m/s]」を返す。
/// 呼び出し側はそれへこの係数を掛けて `ApplyImpulse`（単位 kg·m/s）を送る。
/// すなわち impulse = 方向 × 速度[m/s] × CHAR_PUSH_IMPULSE_SCALE[kg]。
///
/// 【値の根拠】厳密な力学（キャラ質量・接触面・貫入深さ）ではなく「軽い箱なら歩いて押せる」
/// 体感を優先した v1 の簡易値。標準的な箱（質量 1kg 前後・restitution 低め）を等速で押すと
/// 目に見えて動く程度に選んだ。重すぎる箱は押し負ける（意図どおり）。将来コンポーネントの
/// 押し出し力パラメータへ移す前提で、まずは定数化する。
pub(crate) const CHAR_PUSH_IMPULSE_SCALE: f32 = 4.0;

/// SEED のキャラクターコントローラー設定を構築する。
///
/// 上記の KCC_* 定数から `KinematicCharacterController` を組み立てる純粋関数。
/// `CharacterWorld` 生成時に 1 度だけ作って毎フレームの解決で使い回す（状態を持たない）。
fn make_character_controller() -> KinematicCharacterController {
    KinematicCharacterController {
        up:     Vector::y_axis(),
        slide:  true,
        offset: CharacterLength::Absolute(KCC_SKIN_OFFSET_M),
        // 段差の自動乗り越え（階段）を有効化する。1 フレーム 1 回の解決なので autostep のコストは許容。
        autostep: Some(CharacterAutostep {
            max_height:             CharacterLength::Absolute(KCC_AUTOSTEP_MAX_HEIGHT_M),
            min_width:              CharacterLength::Absolute(KCC_AUTOSTEP_MIN_WIDTH_M),
            include_dynamic_bodies: false,
        }),
        max_slope_climb_angle: (KCC_MAX_SLOPE_CLIMB_DEG as Real).to_radians(),
        min_slope_slide_angle: (KCC_MIN_SLOPE_SLIDE_DEG as Real).to_radians(),
        snap_to_ground:        Some(CharacterLength::Absolute(KCC_SNAP_TO_GROUND_M)),
        ..KinematicCharacterController::default()
    }
}

// ─── ミラーエントリ ──────────────────────────────────────────────────────────

/// ミラー内 1 コライダーのメタ情報。
struct MirrorEntry {
    /// ミラー内 Rapier コライダーハンドル。
    handle:     ColliderHandle,
    /// キャラクターコントローラーか（押し戻し解決の対象か）。
    is_character: bool,
    /// 元オブジェクトが Dynamic 剛体か（キャラ接触時にインパルスを送る対象か）。
    is_dynamic: bool,
    /// コライダーの Actor Transform からのローカルオフセット [x, y, z]。
    offset:     [f32; 3],
    /// キャラの「前回解決済みワールド位置」[x, y, z]（`is_character` のときのみ Some）。
    /// `motion = desired − char_last_pos`。解決後に補正後位置へ更新する。
    char_last_pos: Option<[f32; 3]>,
}

// ─── 事前構築ミラーコライダー（並列構築用）────────────────────────────────────

/// `CharacterWorld::build_collider` が返す、**まだミラーへ挿入していない**構築済みコライダー。
///
/// 【なぜ分離するか（性能）】
///   ミラーコライダーの実体構築で支配的に重いのは、地形トライメッシュの Rapier 内部
///   QBVH 構築（`ColliderBuilder::trimesh`）である。`add_object` はこれを `&mut self` の
///   直列ループ内で行うため、地形 322 チャンクを登録する `register_all_terrain_colliders`
///   では 322 個ぶんの QBVH 構築がメインスレッドを直列に占有し、Play 開始直後（物理起動）に
///   数秒の凍結を生む。
///
///   そこで「QBVH を含む純粋な構築」を `&self` 不要の関連関数 `build_collider` に切り出し、
///   `Send` なこの型で受け渡せるようにする。呼び出し側は rayon で **並列に** 構築してから、
///   `insert_prebuilt` で **直列に** ミラーへ差し込む（挿入自体は HashMap 追加＋ハンドル採番の
///   軽処理）。これによりミラーは従来どおり「first resolve より前に完全構築される」ため、
///   KCC の正しさ（構築途中でキャラが素通りする等）には一切影響しない。純粋に構築の並列化のみ。
pub(crate) struct PrebuiltMirrorCollider {
    /// 構築済み Rapier コライダー（QBVH 込み）。ワールド姿勢 × オフセットを反映済み。
    collider:     Collider,
    /// キャラクターコントローラーか（押し戻し解決の対象か）。
    is_character: bool,
    /// 元オブジェクトが Dynamic 剛体か（キャラ接触時にインパルスを送る対象か）。
    is_dynamic:   bool,
    /// コライダーの Actor Transform からのローカルオフセット [x, y, z]。
    offset:       [f32; 3],
    /// 登録時のワールド位置（キャラなら `char_last_pos` の初期値に使う）。
    initial_pos:  [f32; 3],
}

// ─── 解決結果 ────────────────────────────────────────────────────────────────

/// `resolve_character` の解決結果。
///
/// 【設計メモ】監督指示のシグネチャは `Option<(corrected, grounded, Vec<(hit_id, 法線)>)>` の
/// タプルだったが、返す 3 要素それぞれに明確な意味があり、かつ「接触 dynamic への押し出し量」は
/// 生の接触法線ではなく**押し出しインパルス方向 × キャラ水平速度**として渡す方が呼び出し側が
/// 単純になるため、名前付きフィールドの struct にした（可読性・単一責任のため）。
pub(crate) struct CharacterResolve {
    /// 衝突解決後のキャラワールド位置 [x, y, z]。
    pub corrected: [f32; 3],
    /// 解決後に接地しているか（KCC の grounded）。
    pub grounded:  bool,
    /// 接触した dynamic コライダーへの押し出し: (entity_id, インパルス方向 × キャラ水平速度[m/s])。
    /// 呼び出し側は各要素へ `CHAR_PUSH_IMPULSE_SCALE` を掛けて `ApplyImpulse` する。
    pub dynamic_pushes: Vec<(u64, [f32; 3])>,
}

// ─── CharacterWorld ──────────────────────────────────────────────────────────

/// メインスレッド常駐の衝突ミラー。物理スレッドと同一形状のコライダー集合を保持し、
/// キャラクターの押し戻しを KCC でその場解決する（モジュール冒頭の設計解説参照）。
pub struct CharacterWorld {
    /// ミラーコライダー集合。
    collider_set:   ColliderSet,
    /// 剛体集合（本ミラーでは剛体を使わず全コライダーを「位置を直接動かす固定コライダー」
    /// として扱うため常に空。`move_shape`/`update` の API が `&RigidBodySet` を要求するため保持する）。
    rigid_body_set: RigidBodySet,
    /// レイ・シェイプクエリ用パイプライン。コライダーの位置更新後に `update` する。
    query_pipeline: QueryPipeline,
    /// KCC 設定（生成時に 1 度だけ構築して使い回す）。
    character_controller: KinematicCharacterController,
    /// entity_id → ミラーエントリ。
    entities:      HashMap<u64, MirrorEntry>,
    /// ColliderHandle → entity_id（接触相手の逆引き用）。
    col_to_entity: HashMap<ColliderHandle, u64>,
}

impl CharacterWorld {
    /// 空のミラーを生成する。
    pub fn new() -> Self {
        Self {
            collider_set:         ColliderSet::new(),
            rigid_body_set:       RigidBodySet::new(),
            query_pipeline:       QueryPipeline::new(),
            character_controller: make_character_controller(),
            entities:             HashMap::new(),
            col_to_entity:        HashMap::new(),
        }
    }

    /// 物理スレッドへ送るのと同じ `PhysicsObject` からミラーコライダーを構築して登録する。
    ///
    /// センサー（`is_trigger`）は衝突応答なし＝押し戻し対象外のためミラーに入れない。
    /// 剛体は使わず、常にワールド姿勢 × オフセットへ置いた「位置直接制御のコライダー」として
    /// 登録する（毎フレーム `set_position` で ECS 姿勢へ追従させる）。
    /// 同一 entity_id が既に登録済みなら一旦削除してから入れ直す（再登録の取りこぼし防止）。
    pub fn add_object(&mut self, obj: &PhysicsObject) {
        // 純粋構築（QBVH 込み）→ 直列挿入の 2 段に分けて呼ぶ。
        // センサー（trigger）は build_collider が None を返すため、既存を消すだけにする。
        match Self::build_collider(obj) {
            Some(pre) => self.insert_prebuilt(obj.entity_id, pre),
            // trigger へ切り替わった再登録に備え、既存があれば消す
            None      => self.remove(obj.entity_id),
        }
    }

    /// `PhysicsObject` からミラー用コライダー（QBVH 込み）を**純粋に**構築する（`&self` 不要）。
    ///
    /// センサー（`is_trigger`）は衝突応答なし＝押し戻し対象外なので `None` を返す。
    /// 返り値 `PrebuiltMirrorCollider` は `Send` なので、地形 322 チャンクのような大量登録では
    /// rayon で **並列に** この関数を回してから `insert_prebuilt` で直列に差し込める
    /// （支配的コストの QBVH 構築を並列化するため）。`add_object` の内部実装でもある。
    pub fn build_collider(obj: &PhysicsObject) -> Option<PrebuiltMirrorCollider> {
        // センサーはミラーに入れない
        if obj.is_trigger {
            return None;
        }
        let world_iso  = to_isometry(obj.position, obj.rotation);
        let offset_iso = Isometry::translation(
            obj.collider_offset[0], obj.collider_offset[1], obj.collider_offset[2],
        );
        // ここで Rapier トライメッシュの内部 QBVH が構築される（地形登録時の支配的コスト）。
        let collider = build_collider_shape(&obj.collider, &obj.scale)
            .position(world_iso * offset_iso)
            .build();
        let is_dynamic = obj.rigidbody.as_ref().map_or(false, |rb| !rb.is_kinematic);
        Some(PrebuiltMirrorCollider {
            collider,
            is_character: obj.is_character_controller,
            is_dynamic,
            offset:      obj.collider_offset,
            initial_pos: obj.position,
        })
    }

    /// 構築済みミラーコライダー（`build_collider` の結果）を entity_id 指定で挿入する。
    ///
    /// 同一 id が既に登録済みなら先に削除してから入れ直す（再登録の取りこぼし防止）。
    /// 挿入自体は ColliderSet へのハンドル採番＋2 つの HashMap 追加のみで軽い（重い QBVH 構築は
    /// `build_collider` 側で済んでいる）。並列構築 → 直列挿入という使い方を想定した公開点。
    pub fn insert_prebuilt(&mut self, entity_id: u64, pre: PrebuiltMirrorCollider) {
        // 同一 id の再登録: 先に消す
        self.remove(entity_id);

        let is_character = pre.is_character;
        let initial_pos  = pre.initial_pos;
        let handle = self.collider_set.insert(pre.collider);

        self.col_to_entity.insert(handle, entity_id);
        self.entities.insert(entity_id, MirrorEntry {
            handle,
            is_character,
            is_dynamic: pre.is_dynamic,
            offset:     pre.offset,
            // キャラは登録時のワールド位置を前回解決済み位置の初期値にする。
            char_last_pos: if is_character { Some(initial_pos) } else { None },
        });
    }

    /// entity_id のコライダーをミラーから削除する（未登録なら何もしない）。
    pub fn remove(&mut self, entity_id: u64) {
        if let Some(entry) = self.entities.remove(&entity_id) {
            self.col_to_entity.remove(&entry.handle);
            // 剛体は使っていないので rb_set/joints は空で渡す
            self.collider_set.remove(
                entry.handle,
                &mut IslandManager::new(),
                &mut self.rigid_body_set,
                false,
            );
        }
    }

    /// ミラーを空にする（インスタンスを保持したまま中身だけ破棄したいとき用）。
    /// 現状 stop_physics は `character_world = None` でインスタンスごと Drop するため未使用だが、
    /// 将来シーン遷移でミラーを作り直さず再利用する余地を残して提供する。
    #[allow(dead_code)]
    pub fn clear(&mut self) {
        self.collider_set   = ColliderSet::new();
        self.rigid_body_set = RigidBodySet::new();
        self.query_pipeline = QueryPipeline::new();
        self.entities.clear();
        self.col_to_entity.clear();
    }

    /// 動く物体（kinematic/dynamic/他キャラ）のミラー位置を ECS のワールド姿勢へ更新する。
    ///
    /// コライダーのワールド位置 = ワールド姿勢 × ローカルオフセット。
    /// キャラ自身の `char_last_pos`（解決の基準）はここでは触らない（`resolve_character` が更新する）。
    pub fn set_position(&mut self, entity_id: u64, pos: [f32; 3], rot: [f32; 4]) {
        let Some(entry) = self.entities.get(&entity_id) else { return };
        let offset_iso = Isometry::translation(entry.offset[0], entry.offset[1], entry.offset[2]);
        let world = to_isometry(pos, rot) * offset_iso;
        if let Some(col) = self.collider_set.get_mut(entry.handle) {
            col.set_position(world);
        }
    }

    /// キャラの「前回解決済み位置」を指定位置へ強制セットする（テレポート用）。
    ///
    /// テレポートでは motion を巨大化させないため、解決基準（`char_last_pos`）を移動先へ直接
    /// 合わせる。コライダー位置も即反映する。次の `resolve_character` で `motion ≈ 0` となり
    /// 移動先で押し戻されない。`is_character` でないエンティティには何もしない。
    pub fn teleport_character(&mut self, entity_id: u64, pos: [f32; 3], rot: [f32; 4]) {
        if let Some(entry) = self.entities.get_mut(&entity_id) {
            if entry.is_character {
                entry.char_last_pos = Some(pos);
            }
        }
        self.set_position(entity_id, pos, rot);
    }

    /// クエリパイプラインを現在のコライダー集合で更新する。
    ///
    /// コライダーの `set_position` 後・`resolve_character` 前に呼ぶこと（KCC が参照する空間索引を
    /// 最新化する）。トップレベル索引はコライダー 1 個につき 1 葉（トライメッシュ内部 BVH は
    /// 生成時に一度だけ構築され再利用される）なので、地形数百コライダーでも軽い（計測は本モジュールの
    /// テスト `query_update_cost_benchmark` 参照）。
    pub fn update_query(&mut self) {
        self.query_pipeline.update(&self.collider_set);
    }

    /// キャラクターの希望位置を KCC で衝突解決し、補正後位置・接地・接触 dynamic を返す。
    ///
    /// - `motion = desired − char_last_pos`（前回解決済み位置との差分）を KCC が地形・他コライダーと
    ///   衝突解決する（壁ずり・段差・スロープは KCC 標準機能）。
    /// - 自己コライダー・センサーは衝突候補から除外する（センサーはそもそもミラーに無い）。
    /// - 解決後に `char_last_pos` を補正後位置へ更新し、ミラーコライダー位置も追従させる。
    /// - 接触した dynamic コライダーには「押し出し方向 × キャラ水平速度」を積んで返す。
    ///
    /// 対象が未登録・キャラクター指定でない・コライダーが引けない場合は `None`。
    pub fn resolve_character(
        &mut self,
        entity_id: u64,
        desired:   [f32; 3],
        rotation:  [f32; 4],
        dt:        Real,
    ) -> Option<CharacterResolve> {
        // 前回解決済み位置・自己ハンドル・オフセットを取得（キャラでなければ None）
        let (last, own_handle, offset) = {
            let entry = self.entities.get(&entity_id)?;
            if !entry.is_character { return None; }
            (entry.char_last_pos?, entry.handle, entry.offset)
        };
        let shape = self.collider_set.get(own_handle)?.shared_shape().clone();

        let offset_iso = Isometry::translation(offset[0], offset[1], offset[2]);
        let char_pos   = to_isometry(last, rotation) * offset_iso;
        let motion     = vector![
            desired[0] - last[0],
            desired[1] - last[1],
            desired[2] - last[2]
        ];

        // 自己コライダー・センサーを除外する
        let filter = QueryFilter::default()
            .exclude_sensors()
            .exclude_collider(own_handle);

        // 接触コライダーのハンドルを収集する（マップ逆引きは move_shape 後に行い借用衝突を避ける）
        let mut hit_handles: Vec<ColliderHandle> = Vec::new();
        let movement = self.character_controller.move_shape(
            dt, &self.rigid_body_set, &self.collider_set, &self.query_pipeline,
            &*shape, &char_pos, motion, filter,
            |collision| { hit_handles.push(collision.handle); },
        );

        let t = movement.translation;
        let corrected = [last[0] + t.x, last[1] + t.y, last[2] + t.z];

        // ── 接触 dynamic への押し出しベクトルを組み立てる ──
        // 【方向・速度とも「キャラが押し込もうとした水平移動（motion = desired − last）」から取る】
        //  - 方向: parry のシェイプキャスト法線は貫入時に信頼できない旨がドキュメント化されている
        //    ため使わず、キャラが押し込もうとした水平方向を使う（法線の信頼性に依存しない）。
        //  - 速度: **補正後の実移動ではなく希望移動から**求める。KCC は箱に押し当たると実移動を
        //    ほぼ 0 に潰すため、実移動で速度を測ると「箱に密着して押し続けても押せない」バグになる。
        //    希望移動 |motion|/dt はプレイヤーが箱へ歩き込む入力が続く限り非ゼロに保たれる。
        let dir_len = (motion.x * motion.x + motion.z * motion.z).sqrt();
        let has_dir = dir_len > 1e-6;
        let speed   = dir_len / dt; // 希望水平速度[m/s]（押し込み入力の強さ）
        let mut push_dir = [motion.x, 0.0, motion.z];
        if has_dir {
            push_dir[0] /= dir_len;
            push_dir[2] /= dir_len;
        }

        let mut dynamic_pushes: Vec<(u64, [f32; 3])> = Vec::new();
        if has_dir && speed > 1e-4 {
            for h in hit_handles {
                let Some(&eid) = self.col_to_entity.get(&h) else { continue };
                let Some(entry) = self.entities.get(&eid) else { continue };
                if !entry.is_dynamic { continue; }
                if dynamic_pushes.iter().any(|(e, _)| *e == eid) { continue; }
                dynamic_pushes.push((eid, [
                    push_dir[0] * speed,
                    0.0,
                    push_dir[2] * speed,
                ]));
            }
        }

        // 前回位置を更新し、ミラーコライダー位置も補正後位置へ同期する
        if let Some(entry) = self.entities.get_mut(&entity_id) {
            entry.char_last_pos = Some(corrected);
        }
        let world = to_isometry(corrected, rotation) * offset_iso;
        if let Some(col) = self.collider_set.get_mut(own_handle) {
            col.set_position(world);
        }

        Some(CharacterResolve { corrected, grounded: movement.grounded, dynamic_pushes })
    }

    /// 登録済みコライダー総数（診断ログ用）。
    pub fn collider_count(&self) -> usize {
        self.collider_set.len()
    }
}

impl Default for CharacterWorld {
    fn default() -> Self { Self::new() }
}

// ─── テスト ──────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::physics::types::{ColliderShape, PhysicsObject, RigidBodyState, PHYSICS_FIXED_STEP};

    /// 平坦な床（共有頂点＋インデックスのトライメッシュ）の PhysicsObject を作る。
    fn floor_object(entity_id: u64) -> PhysicsObject {
        PhysicsObject {
            entity_id,
            position: [0.0, 0.0, 0.0],
            rotation: [0.0, 0.0, 0.0, 1.0],
            scale:    [1.0, 1.0, 1.0],
            collider: ColliderShape::TriangleMeshIndexed {
                vertices: vec![
                    [-5.0, 0.0, -5.0], [5.0, 0.0, -5.0], [5.0, 0.0, 5.0], [-5.0, 0.0, 5.0],
                ],
                indices: vec![[0, 1, 2], [0, 2, 3]],
            },
            collider_offset: [0.0, 0.0, 0.0],
            rigidbody:       None,
            is_trigger:      false,
            physics_layer:   0,
            layer_mask:      0,
            is_character_controller: false,
        }
    }

    /// 中心 `center` のカプセルキャラ PhysicsObject を作る（半径 0.4・半高 0.5）。
    fn capsule_char(entity_id: u64, center: [f32; 3]) -> PhysicsObject {
        PhysicsObject {
            entity_id,
            position: center,
            rotation: [0.0, 0.0, 0.0, 1.0],
            scale:    [1.0, 1.0, 1.0],
            collider: ColliderShape::Capsule { radius: 0.4, half_height: 0.5 },
            collider_offset: [0.0, 0.0, 0.0],
            rigidbody:       None,
            is_trigger:      false,
            physics_layer:   0,
            layer_mask:      0,
            is_character_controller: true,
        }
    }

    const CAP_HALF_HEIGHT: f32 = 0.5;
    const CAP_RADIUS:      f32 = 0.4;

    /// 床の直上のキャラを床へ押し込もうとしても、床上へ押し戻され・接地判定が立つこと。
    #[test]
    fn resolve_pushes_back_from_floor_and_grounds() {
        let mut cw = CharacterWorld::new();
        cw.add_object(&floor_object(1));
        cw.add_object(&capsule_char(2, [0.0, 1.0, 0.0])); // 下端 = 1.0 - 0.9 = 0.1（床上）
        cw.update_query();

        // 床を大きく貫く希望位置 y=-1.0 を与える
        let r = cw.resolve_character(2, [0.0, -1.0, 0.0], [0.0, 0.0, 0.0, 1.0], PHYSICS_FIXED_STEP as Real)
            .expect("キャラ解決は Some を返す");
        let foot_y = r.corrected[1] - (CAP_HALF_HEIGHT + CAP_RADIUS);
        assert!(foot_y > -0.05, "床に押し戻されていない: foot_y={foot_y} corrected_y={}", r.corrected[1]);
        assert!(r.grounded, "床へ押し付けたのに grounded=false");
    }

    /// 壁へ斜めに突っ込むと、壁法線方向は止まり接線方向（壁ずり）が残ること。
    #[test]
    fn resolve_slides_along_wall() {
        let mut cw = CharacterWorld::new();
        // 床
        cw.add_object(&floor_object(1));
        // x=+1.0 に薄い壁（法線 ±X）。ボックス half_extent x=0.1、z 方向に広い。
        cw.add_object(&PhysicsObject {
            entity_id: 3,
            position: [1.5, 1.0, 0.0],
            rotation: [0.0, 0.0, 0.0, 1.0],
            scale:    [1.0, 1.0, 1.0],
            collider: ColliderShape::Box { half_extents: [0.1, 2.0, 5.0] },
            collider_offset: [0.0, 0.0, 0.0],
            rigidbody: None,
            is_trigger: false,
            physics_layer: 0,
            layer_mask: 0,
            is_character_controller: false,
        });
        // キャラを壁のすぐ左（x=0.9）に置く。カプセル半径 0.4 なので右端 1.3、壁左面 1.4 の直前。
        cw.add_object(&capsule_char(2, [0.9, 1.0, 0.0]));
        cw.update_query();

        // 壁へ向かって斜め（+X かつ +Z）に動かす。+X は壁で止まり、+Z は残るはず。
        let r = cw.resolve_character(2, [1.4, 1.0, 0.5], [0.0, 0.0, 0.0, 1.0], PHYSICS_FIXED_STEP as Real)
            .expect("Some");
        let dx = r.corrected[0] - 0.9;
        let dz = r.corrected[2] - 0.0;
        assert!(dx < 0.45, "壁法線(+X)方向へ壁を貫いた: dx={dx}");
        assert!(dz > 0.3,  "接線(+Z)方向の壁ずりが残っていない: dz={dz}");
    }

    /// add / remove / set_position の基本動作。
    #[test]
    fn add_remove_set_position() {
        let mut cw = CharacterWorld::new();
        cw.add_object(&floor_object(1));
        assert_eq!(cw.collider_count(), 1, "床 1 個が登録される");

        // set_position で床を持ち上げる（衝突には影響しない範囲の健全性チェック）
        cw.set_position(1, [0.0, 2.0, 0.0], [0.0, 0.0, 0.0, 1.0]);

        cw.remove(1);
        assert_eq!(cw.collider_count(), 0, "削除後は 0 個");
        // 未登録 id への操作は無害
        cw.remove(1);
        cw.set_position(999, [0.0, 0.0, 0.0], [0.0, 0.0, 0.0, 1.0]);
    }

    /// センサー（is_trigger）はミラーに入らないこと。
    #[test]
    fn sensor_not_mirrored() {
        let mut cw = CharacterWorld::new();
        let mut trig = floor_object(1);
        trig.is_trigger = true;
        cw.add_object(&trig);
        assert_eq!(cw.collider_count(), 0, "センサーはミラーに入れない");
    }

    /// 非キャラ・未登録の resolve は None を返すこと。
    #[test]
    fn resolve_none_for_non_character() {
        let mut cw = CharacterWorld::new();
        cw.add_object(&floor_object(1)); // 非キャラ
        cw.update_query();
        assert!(cw.resolve_character(1, [0.0, 0.0, 0.0], [0.0, 0.0, 0.0, 1.0], PHYSICS_FIXED_STEP as Real).is_none());
        assert!(cw.resolve_character(999, [0.0, 0.0, 0.0], [0.0, 0.0, 0.0, 1.0], PHYSICS_FIXED_STEP as Real).is_none());
    }

    /// テレポートで基準位置が移り、直後の解決で押し戻されない（motion≈0）こと。
    #[test]
    fn teleport_resets_base() {
        let mut cw = CharacterWorld::new();
        cw.add_object(&floor_object(1));
        cw.add_object(&capsule_char(2, [0.0, 1.0, 0.0]));
        cw.update_query();
        // 遠方へテレポート
        cw.teleport_character(2, [10.0, 1.0, 10.0], [0.0, 0.0, 0.0, 1.0]);
        // desired = テレポート先 → motion≈0 → corrected≈テレポート先
        let r = cw.resolve_character(2, [10.0, 1.0, 10.0], [0.0, 0.0, 0.0, 1.0], PHYSICS_FIXED_STEP as Real)
            .expect("Some");
        assert!((r.corrected[0] - 10.0).abs() < 0.05 && (r.corrected[2] - 10.0).abs() < 0.05,
            "テレポート先で保持されるべき: {:?}", r.corrected);
    }

    /// 接触した dynamic 物体が dynamic_pushes に載ること（キャラが箱へ歩き込む）。
    #[test]
    fn resolve_returns_dynamic_push() {
        let mut cw = CharacterWorld::new();
        cw.add_object(&floor_object(1));
        // dynamic な箱を x=1.0 に置く
        cw.add_object(&PhysicsObject {
            entity_id: 3,
            position: [1.0, 0.5, 0.0],
            rotation: [0.0, 0.0, 0.0, 1.0],
            scale:    [1.0, 1.0, 1.0],
            collider: ColliderShape::Box { half_extents: [0.5, 0.5, 0.5] },
            collider_offset: [0.0, 0.0, 0.0],
            rigidbody: Some(RigidBodyState::new(1.0)), // dynamic
            is_trigger: false,
            physics_layer: 0,
            layer_mask: 0,
            is_character_controller: false,
        });
        cw.add_object(&capsule_char(2, [-0.1, 1.0, 0.0])); // 箱の左
        cw.update_query();

        // 箱へ向かって +X に大きく踏み込む
        let r = cw.resolve_character(2, [0.6, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0], PHYSICS_FIXED_STEP as Real)
            .expect("Some");
        assert!(r.dynamic_pushes.iter().any(|(e, v)| *e == 3 && v[0] > 0.0),
            "dynamic 箱(id=3)への +X 押し出しが返るべき: {:?}", r.dynamic_pushes);
    }

    /// 箱に密着して押し当たり実移動が 0 になっても、押し込み入力が続く限り押し出しが返ること。
    /// （速度を実移動でなく希望移動から取る修正の回帰テスト。）
    #[test]
    fn resolve_pushes_dynamic_even_when_blocked() {
        let mut cw = CharacterWorld::new();
        cw.add_object(&floor_object(1));
        // dynamic 箱を x=1.0（左面 x=0.5）に置く
        cw.add_object(&PhysicsObject {
            entity_id: 3,
            position: [1.0, 0.5, 0.0],
            rotation: [0.0, 0.0, 0.0, 1.0],
            scale:    [1.0, 1.0, 1.0],
            collider: ColliderShape::Box { half_extents: [0.5, 0.5, 0.5] },
            collider_offset: [0.0, 0.0, 0.0],
            rigidbody: Some(RigidBodyState::new(1.0)),
            is_trigger: false,
            physics_layer: 0,
            layer_mask: 0,
            is_character_controller: false,
        });
        // キャラを箱左面へほぼ密着（中心 0.1 で右端 0.5 が左面に接する）させて登録
        cw.add_object(&capsule_char(2, [0.1, 1.0, 0.0]));
        cw.update_query();

        // 密着状態から更に +X へ押し込む（実移動はほぼ 0 に潰れるが希望移動は +0.1）
        let r = cw.resolve_character(2, [0.2, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0], PHYSICS_FIXED_STEP as Real)
            .expect("Some");
        // 実移動がほぼ 0（箱で堰き止め）でも押し出しが返る
        assert!((r.corrected[0] - 0.1).abs() < 0.15, "箱で堰き止められているはず: {:?}", r.corrected);
        assert!(r.dynamic_pushes.iter().any(|(e, v)| *e == 3 && v[0] > 0.0),
            "密着押し込みでも dynamic 箱への +X 押し出しが返るべき: {:?}", r.dynamic_pushes);
    }

    /// ★Query 更新コストの実測（監督指示の設計判断用）★
    ///
    /// 地形相当の静的トライメッシュ約 300 個＋動体 4 個を構築し、動体を 1 個動かして
    /// `update_query` を反復し、1 回あたりの平均時間を stderr へ出す。
    /// CI フレーキー回避のため時間そのものは assert しない（`--nocapture` で数値を読む）。
    #[test]
    fn query_update_cost_benchmark() {
        use std::time::Instant;

        let mut cw = CharacterWorld::new();
        // 300 個の小さなトライメッシュ床タイル（地形チャンク相当）を格子状に並べる。
        const N: i32 = 300;
        let side = (N as f32).sqrt().ceil() as i32; // 18
        let mut id = 1u64;
        for i in 0..N {
            let gx = (i % side) as f32 * 2.0;
            let gz = (i / side) as f32 * 2.0;
            cw.add_object(&PhysicsObject {
                entity_id: id,
                position: [gx, 0.0, gz],
                rotation: [0.0, 0.0, 0.0, 1.0],
                scale:    [1.0, 1.0, 1.0],
                collider: ColliderShape::TriangleMeshIndexed {
                    vertices: vec![[0.0, 0.0, 0.0], [2.0, 0.0, 0.0], [2.0, 0.0, 2.0], [0.0, 0.0, 2.0]],
                    indices:  vec![[0, 1, 2], [0, 2, 3]],
                },
                collider_offset: [0.0, 0.0, 0.0],
                rigidbody: None,
                is_trigger: false,
                physics_layer: 0,
                layer_mask: 0,
                is_character_controller: false,
            });
            id += 1;
        }
        // 動体 4 個
        for k in 0..4 {
            cw.add_object(&PhysicsObject {
                entity_id: id,
                position: [k as f32, 1.0, 0.0],
                rotation: [0.0, 0.0, 0.0, 1.0],
                scale:    [1.0, 1.0, 1.0],
                collider: ColliderShape::Box { half_extents: [0.5, 0.5, 0.5] },
                collider_offset: [0.0, 0.0, 0.0],
                rigidbody: Some(RigidBodyState::new(1.0)),
                is_trigger: false,
                physics_layer: 0,
                layer_mask: 0,
                is_character_controller: false,
            });
            id += 1;
        }

        // 初回 update（索引構築）を計測から除外するためウォームアップ
        cw.update_query();

        const ITERS: u32 = 200;
        let mut moving = id - 1; // 最後の動体
        let start = Instant::now();
        for f in 0..ITERS {
            let x = (f as f32) * 0.01;
            cw.set_position(moving, [x, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0]);
            cw.update_query();
        }
        let _ = &mut moving;
        let per = start.elapsed().as_secs_f64() / ITERS as f64 * 1000.0;
        eprintln!(
            "[char_world bench] colliders={} update_query 平均 = {:.4} ms/回（{} 回計測・動体 1 個/回移動）",
            cw.collider_count(), per, ITERS,
        );
    }
}
