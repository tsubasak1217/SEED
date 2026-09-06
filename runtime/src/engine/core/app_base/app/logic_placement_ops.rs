// ============================================================
//  logic_placement_ops.rs — ロジック配置（LOGIC_PLACE）の実行層
//
//  【責務】
//  エディタのロジック配置ダイアログから届く 1 発の IPC
//  （`LOGIC_PLACE:{json}`）を受け、
//    ① 純粋層（`engine::placement`）でパターン点列を生成し
//    ② 必要なら地形へ接地させ
//    ③ 新規アクタ群を生成する / ControlPoint の点列へ追記する
//  までを **1 操作 = Undo 1 件**で行う。
//
//  【なぜランタイム側で一括処理するのか】
//  接地（地形の密度場サンプリング）とアクタ生成（ECS・プレハブ構築）は
//  ランタイムにしか存在しない情報を要する。エディタが点ごとに IPC を
//  往復させると、生成が Undo 多数件に割れ、途中で失敗した場合に半端な
//  状態が残る。「1 コマンド = 1 Undo」を守るためここへ集約する。
//
//  【エディタのプレビューとの関係】
//  C# 側はプレビューのためだけに同じアルゴリズムを写して持つが、
//  **実際に置かれる点は必ず本モジュールが生成したもの**である
//  （プレビューは接地前・基準点適用前の俯瞰図でしかない）。
//
//  【グループ配下に入れる理由】
//  数十〜数百体のアクタをヒエラルキー直下へ並べるとツリーが実用に耐えない。
//  地形のアクタ散布（terrain_scatter_actor_ops）と同じく専用フォルダへ収める。
//  ただしこちらは**ユーザーが自由に触ってよい通常のグループ**であり、
//  散布のようなマーカー（scatter_prop_id）は付けない。
//
//  【テスト可能性のための切り分け】
//  App（ウィンドウ・レンダラ・IPC を抱える）はユニットテストで組めないため、
//  判断を伴う処理は **App を引数に取らない自由関数**へ出してある:
//    ・`ground_positions_with`     … 接地（`ScatterField` 抽象越し）
//    ・`assemble_placement_group`  … グループとその子アクタの組み立て
//    ・`append_control_points`     … 制御点の追記と上限切り詰め
//  App のメソッドはこれらを呼ぶだけの薄い層に留める。
// ============================================================

use serde::Deserialize;

use crate::engine::components::control_point_component::{
    ControlPoint, ControlPointComponent, DEFAULT_TIME_STEP, MAX_CONTROL_POINTS,
};
use crate::engine::components::{CanvasTransform, Transform};
use crate::engine::core::app_base::scene::build_actor;
use crate::engine::core::app_base::undo::{
    ActorTreeSnapshotCommand, ComponentSlotsSnapshotCommand,
};
use crate::engine::core::transform_sync::set_actor_world_transform;
use crate::engine::ecs::World;
use crate::engine::placement::{generate_points, PlacementPoint, PlacementSpec};
use crate::engine::structs::objects::Actor;
use crate::engine::methods::gizmo_interact::mat4x4_inv;
use crate::engine::terrain::scatter::{surface_hit_down, ScatterField};

use super::actor_utils::dfs_ids_for_entities;
use super::control_point_ops::transform_point;
use super::placement_mode::placement_world_positions;
use super::prefab_ops::load_actor_data;
use super::terrain_scatter_ops::TerrainScatterField;
use super::{insert_group_actor, App};

// ============================================================
//  定数（マジックナンバー禁止）
// ============================================================

/// 生成アクタ名の連番書式に使う最小桁数（`Tree_01` のように 2 桁ゼロ詰め）。
const NAME_INDEX_WIDTH: usize = 2;

/// 地形接地の探索でレイを開始する、基準 Y からの上方向マージン [m]。
///
/// 基準点より高い地面（丘の上へ円を置いた等）にも当てられるだけの余裕を取る。
/// 大きすぎると 1 点あたりのサンプル数（＝コスト）が増えるため、
/// 「エディタで扱う地形の起伏」として現実的な高さに留める。
const GROUND_PROBE_UP: f32 = 200.0;

/// 地形接地の探索でレイを終える、基準 Y からの下方向マージン [m]。
const GROUND_PROBE_DOWN: f32 = 200.0;

/// 配置対象: 新規アクタ群を生成する。
const TARGET_ACTORS: &str = "actors";
/// 配置対象: ControlPoint の点列へ追記する。
pub(super) const TARGET_CONTROL_POINTS: &str = "control_points";

// ============================================================
//  リクエスト（IPC の JSON 表現）
// ============================================================

/// `LOGIC_PLACE:{json}` / `LOGIC_PLACE_BEGIN:{json}` の本体。
///
/// 全フィールドに `#[serde(default)]` を付け、エディタが一部を送らなくても
/// 既定値で動くようにする（IPC の後方互換の要）。
///
/// **基準点（ワールド原点）は含まない**。新規アクタ配置の基準点は
/// 「配置モード中のカーソル位置」で決まり（`placement_mode.rs`）、
/// ControlPoint への追記はアクタ相対座標なので基準点そのものが存在しない。
#[derive(Clone, Debug, Default, Deserialize)]
pub struct LogicPlaceRequest {
    /// 配置対象（`actors` / `control_points`）。
    #[serde(default)]
    pub target: String,
    /// 2D 配置かどうか（true なら CanvasTransform、false なら Transform）。
    #[serde(default)]
    pub is_2d: bool,
    /// 右クリック対象アクタの DFS id（グループの親／基準点の解決に使う）。
    #[serde(default)]
    pub parent_dfs: Option<u32>,
    /// 生成するグループフォルダ名。
    #[serde(default)]
    pub group_name: String,
    /// 生成アクタ名の接頭辞（`{prefix}_01` のように連番が付く）。
    #[serde(default)]
    pub name_prefix: String,
    /// 配置元アクタファイル（`assets://…/foo.actor`）。未指定なら空アクタ。
    #[serde(default)]
    pub source_path: Option<String>,
    /// 地形へ接地させるか（3D のみ有効。接地できない点は基準 Y のまま残す）。
    ///
    /// `control_points` でも効く。制御点はアクタローカルで保持されるが、
    /// 接地はワールドでしか意味を持たないので
    /// **ローカルで合成 → ワールド化 → 接地 → ローカルへ戻す**の順に通す
    /// （`control_point_local_positions`）。
    #[serde(default)]
    pub ground: bool,
    /// ControlPoint 追記時の対象アクタ DFS id。
    #[serde(default)]
    pub actor_dfs_id: u32,
    /// ControlPoint 追記時の対象スロット添字。
    #[serde(default)]
    pub slot_idx: u32,
    /// パターン指定。
    #[serde(default)]
    pub spec: PlacementSpec,
}

// ============================================================
//  自由関数（App に依存しない＝ユニットテスト可能な中核）
// ============================================================

/// 密度場へ真下方向のレイを飛ばし、点列を地表へ落とす。
///
/// 接地できなかった点は**基準 Y のまま残す**（勝手に消したり、
/// 見当違いの高さへ飛ばしたりしない）。戻り値は接地できなかった点数。
///
/// 探索範囲を基準 Y の上下に取るのは、基準点より高い地面（丘の上）にも
/// 低い地面（谷底）にも当てるため。地形が無い（＝密度場が全域 AIR の）
/// シーンでは全点が「接地できず」となり、呼び出し側が警告を出す。
pub(super) fn ground_positions_with(field: &impl ScatterField, positions: &mut [[f32; 3]]) -> usize {
    let mut missed = 0usize;
    for p in positions.iter_mut() {
        match surface_hit_down(field, p[0], p[2], p[1] + GROUND_PROBE_UP, p[1] - GROUND_PROBE_DOWN) {
            Some((hit, _normal)) => p[1] = hit[1],
            None => missed += 1,
        }
    }
    missed
}

/// **アクタローカル**の点列を、ワールドで接地してからローカルへ戻す。
///
/// 制御点は「対象アクタのローカル座標」で保持されるデータだが、地形接地は
/// ワールド空間でしか意味を持たない（アクタが回転・スケールしていても、
/// 地面はワールドの Y 方向にある）。そこで
/// **ローカルで合成 → ワールド化 → 接地 → ローカルへ戻す**の順に通す。
///
/// 逆行列は 1 回だけ求める（点ごとに `world_to_actor_local` を呼ぶと
/// 点数ぶん行列反転が走り、ドラッグ中の毎フレームコストになるため）。
/// 接地できなかった点はワールド Y が動かないので、往復して**元のローカルへ戻る**
/// （＝基準 Y のまま）。戻り値は接地できなかった点数。
pub(super) fn ground_local_positions_with(
    field:     &impl ScatterField,
    actor_mat: &[[f32; 4]; 4],
    locals:    &mut [[f32; 3]],
) -> usize {
    let inv = mat4x4_inv(*actor_mat);
    // ① ローカル → ワールド
    let mut worlds: Vec<[f32; 3]> =
        locals.iter().map(|p| transform_point(actor_mat, *p)).collect();
    // ② ワールドで接地（アクタ配置とまったく同じ関数を通す）
    let missed = ground_positions_with(field, &mut worlds);
    // ③ ワールド → ローカル
    for (l, w) in locals.iter_mut().zip(worlds.iter()) {
        *l = transform_point(&inv, *w);
    }
    missed
}

/// 生成アクタの名前を組み立てる（`{prefix}_01` 形式・1 始まり）。
fn placement_actor_name(name_prefix: &str, index: usize) -> String {
    format!("{name_prefix}_{:0width$}", index + 1, width = NAME_INDEX_WIDTH)
}

/// グループフォルダ（2D なら単位 CanvasTransform 付きのフォルダノード）を作る。
///
/// 2D フォルダに CanvasTransform を持たせないと `canvas_collect` などのキャンバス走査が
/// サブツリーを打ち切り、配下のスプライトが描画対象から外れる。そこで 2D だけは
/// 単位 CanvasTransform を必ず添える（`handle_create_group` と同じ設計）。
fn spawn_placement_group(world: &mut World, wl: u32, is_2d: bool, name: &str) -> Actor {
    let entity = world.spawn();
    let mut group = if is_2d {
        // 単位変換なので子のワールド変換には影響しない（透過ノードのまま）
        world.insert(entity, CanvasTransform::default());
        Actor::new_folder_2d(entity, name.to_string())
    } else {
        Actor::new_folder(entity, name.to_string())
    };
    group.world_line = wl;
    group
}

/// 空アクタ（配置元が「空アクタ」のときの 1 体）を作る。
fn spawn_empty_actor(world: &mut World, is_2d: bool) -> Actor {
    let entity = world.spawn();
    if is_2d {
        world.insert(entity, CanvasTransform::default());
        Actor::new_2d(entity, String::new())
    } else {
        world.insert(entity, Transform::default());
        Actor::new(entity, "")
    }
}

/// 生成済みアクタを配置点へ移す。
///
/// - 2D: パターンの XZ 平面をキャンバスの XY へ写す。スプライト描画は毎フレーム
///   CanvasTransform 階層から再計算されるため、ルートを書き換えるだけで整合する。
/// - 3D: アクタファイル由来のサブツリーはワールド空間の行列を持つため、ルートだけ
///   書き換えても追従しない。差分行列のサブツリー適用は `transform_sync` へ任せる
///   （地形のアクタ散布と同じ経路）。
fn apply_placement_transform(
    actor: &Actor,
    world: &mut World,
    is_2d: bool,
    point: &PlacementPoint,
    position: [f32; 3],
) {
    if is_2d {
        let mut ct = world.get::<CanvasTransform>(actor.entity).cloned().unwrap_or_default();
        ct.position = [position[0], position[2]];
        // ヨー（Y 軸回り）は 2D では Z 軸回りの回転として解釈する。
        ct.rotation += point.rotation[1];
        ct.scale = [ct.scale[0] * point.scale[0], ct.scale[1] * point.scale[2]];
        world.insert(actor.entity, ct);
    } else {
        let mut tf = world.get::<Transform>(actor.entity).cloned().unwrap_or_default();
        tf.position = position;
        for k in 0..3 { tf.rotation[k] += point.rotation[k]; }
        for k in 0..3 { tf.scale[k] *= point.scale[k]; }
        let _ = set_actor_world_transform(actor, world, tf, 0);
    }
}

/// グループフォルダと配下のアクタ群を組み立てて返す（ツリーへの挿入は呼び出し側）。
///
/// `make_actor` は 1 体ぶんのアクタを作るファクトリ。空アクタ生成と
/// アクタファイル（プレハブ）構築のどちらもここへ差し込めるようにして、
/// **組み立ての手順（命名・世界線・配置点適用・親子付け）を 1 本化**する。
/// ファクトリが `Err` を返した時点で打ち切る（同じ入力で繰り返し失敗するため）。
///
/// 戻り値は `(グループ, ファクトリが失敗したか)`。
fn assemble_placement_group(
    world:       &mut World,
    wl:          u32,
    is_2d:       bool,
    group_name:  &str,
    name_prefix: &str,
    points:      &[PlacementPoint],
    positions:   &[[f32; 3]],
    mut make_actor: impl FnMut(&mut World) -> Result<Actor, String>,
) -> (Actor, bool) {
    let mut group = spawn_placement_group(world, wl, is_2d, group_name);
    let mut failed = false;

    for (i, (point, position)) in points.iter().zip(positions.iter()).enumerate() {
        let mut actor = match make_actor(world) {
            Ok(a) => a,
            Err(e) => {
                eprintln!("[LogicPlace] アクタの生成に失敗したため打ち切ります: {e}");
                failed = true;
                break;
            }
        };
        actor.name = placement_actor_name(name_prefix, i);
        actor.set_world_line_recursive(wl);
        apply_placement_transform(&actor, world, is_2d, point, *position);
        group.children_mut().push(actor);
    }

    (group, failed)
}

/// 生成点列を既存の制御点列の末尾へ追記する。
///
/// 制御点は**アクタ相対**座標なので、`origin_local`（＝カーソル位置をアクタの
/// ローカル空間へ変換した基準点）にパターン座標を足したものがそのまま点の位置になる
/// 接地は行わない（接地込みの経路は `append_control_points_at` を使う）。
/// 配置モードを経由しない旧経路は `origin_local = [0,0,0]` を渡す。
/// 時刻は既存点の続きから `DEFAULT_TIME_STEP` 刻みで振る
/// （インスペクタの「＋ 制御点を追加」と同じ規則）。
///
/// 上限 `MAX_CONTROL_POINTS` を超えるぶんは追記せず、戻り値で件数を返す。
/// 戻り値は `(追記した数, 切り詰めた数)`。
fn append_control_points(
    existing:  &mut Vec<ControlPoint>,
    generated: &[PlacementPoint],
    origin_local: [f32; 3],
    start_time: f32,
) -> (usize, usize) {
    let positions = placement_world_positions(origin_local, generated, usize::MAX);
    append_control_points_at(existing, generated, &positions, start_time)
}

/// 位置を**外から与えて**制御点を追記する（接地済みの点列を入れる経路）。
///
/// `positions_local[i]` が `generated[i]` の最終的なローカル位置になる。
/// 回転・スケールはパターン側（`generated`）の値をそのまま使う。
/// 位置と点の対応は添字で取るので、`positions_local` は `generated` と
/// 同じ長さ以上であること（短ければそこで打ち切る）。
///
/// 上限 `MAX_CONTROL_POINTS` を超えるぶんは追記せず、戻り値で件数を返す。
fn append_control_points_at(
    existing:        &mut Vec<ControlPoint>,
    generated:       &[PlacementPoint],
    positions_local: &[[f32; 3]],
    start_time:      f32,
) -> (usize, usize) {
    let capacity = MAX_CONTROL_POINTS.saturating_sub(existing.len());
    let take = generated.len().min(capacity).min(positions_local.len());

    let mut t = start_time;
    for (p, position) in generated.iter().zip(positions_local.iter()).take(take) {
        existing.push(ControlPoint {
            position: *position,
            rotation: p.rotation,
            scale:    p.scale,
            time:     t,
            interp:   Default::default(),
        });
        t += DEFAULT_TIME_STEP;
    }
    (take, generated.len() - take)
}

// ============================================================
//  App 側（IPC 受け口・シーンと Undo への反映）
// ============================================================

impl App {
    /// `LOGIC_PLACE` を処理する。
    ///
    /// パース失敗・シーン未読込・点 0 個のいずれでも**何も変更しない**
    /// （半端な生成を残さない）。警告はエディタへ通知して処理は続ける。
    pub(super) fn handle_logic_place(&mut self, json: &str) {
        if self.scene.is_none() { return; }

        let req: LogicPlaceRequest = match serde_json::from_str(json) {
            Ok(r) => r,
            Err(e) => {
                self.notify_placement_error(&format!("ロジック配置の指定を解釈できません: {e}"));
                return;
            }
        };

        // ── 純粋層で点列を作る ──
        let result = generate_points(&req.spec);
        if let Some(w) = &result.warning {
            self.notify_placement_error(w);
        }
        if result.points.is_empty() {
            self.notify_placement_error("配置する点がありません（個数・行列数を確認してください）");
            return;
        }

        // 既定は新規アクタ生成。空文字・未知の値も `TARGET_ACTORS` として扱う
        //（エディタが古いままでも「アクタが置ける」側へ倒すほうが安全なため）。
        if req.target == TARGET_CONTROL_POINTS {
            // 基準点を伴わない直接追記（配置モードを経由しない旧経路・自動化用）。
            // 通常のエディタ操作は `LOGIC_PLACE_BEGIN` → 配置モード → カーソル位置確定
            // を通るので、ここへ来るのはアクタ原点基準で良い場合に限られる。
            self.place_control_points(&req, &result.points, [0.0, 0.0, 0.0]);
        } else {
            debug_assert!(req.target.is_empty() || req.target == TARGET_ACTORS);
            // 基準点を伴わない直接生成（配置モードを経由しない旧経路・自動化用）。
            // 通常のエディタ操作は `LOGIC_PLACE_BEGIN` → 配置モード → カーソル位置確定
            // を通るので、ここへ来るのはワールド原点基準で良い場合に限られる。
            self.place_actors(&req, &result.points, [0.0, 0.0, 0.0]);
        }
    }

    /// エディタへ警告・エラーを通知する（既存のトースト経路へ相乗り）。
    pub(super) fn notify_placement_error(&self, message: &str) {
        eprintln!("[LogicPlace] {message}");
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("LOAD_ERROR:{message}"));
        }
    }

    // ── ① 新規アクタ群の生成 ────────────────────────────────

    /// 点列にアクタを生成し、新しいグループフォルダ配下へ**シーンへ挿入するだけ**行う。
    ///
    /// Undo 記録・ヒエラルキー送信・`SCENE_MODIFIED`・選択のいずれも行わない。
    /// これらは呼び出し側の都合（確定なのか、配置モードの仮スポーンなのか）で
    /// 変わるためである。仮スポーンと確定生成で**まったく同じ物が出来る**ことを
    /// 構造的に保証するため、生成そのものは必ず本関数 1 か所に通す。
    ///
    /// 戻り値は `(グループのエンティティ, 生成した子アクタのエンティティ列)`。
    /// 1 体も作れなかった場合は `None`（シーンには何も残さない）。
    pub(super) fn spawn_placement_actors(
        &mut self,
        req:    &LogicPlaceRequest,
        points: &[PlacementPoint],
        origin: [f32; 3],
    ) -> Option<(crate::engine::ecs::Entity, Vec<crate::engine::ecs::Entity>)> {
        let wl = self.active_world_line;

        // ── ワールド位置を先に確定する（接地は地形を不変借用するため、
        //    シーンの可変借用より前に済ませる）──
        let mut positions: Vec<[f32; 3]> = points
            .iter()
            .map(|p| [
                origin[0] + p.position[0],
                origin[1] + p.position[1],
                origin[2] + p.position[2],
            ])
            .collect();

        // 接地は 3D のみ（2D は地形を持たない）。
        if req.ground && !req.is_2d {
            let missed = {
                let field = TerrainScatterField::from_state(&self.terrain);
                ground_positions_with(&field, &mut positions)
            };
            if missed > 0 {
                self.notify_placement_error(&format!(
                    "地形接地: {missed} 点で地表が見つからなかったため基準の高さのままにしました"
                ));
            }
        }

        // ── 配置元アクタファイルを 1 回だけ読む（点ごとに読み直さない）──
        let source = match req.source_path.as_deref().filter(|s| !s.is_empty()) {
            Some(path) => match load_actor_data(path) {
                Ok(data) => Some((path.to_string(), data)),
                Err(e) => {
                    self.notify_placement_error(&format!(
                        "配置元アクタファイルを読めません '{path}': {e}"
                    ));
                    return None;
                }
            },
            None => None,
        };
        // アクタファイルからの構築には描画コンテキストが要る（テクスチャ・メッシュ生成）。
        if source.is_some() && self.draw_ctx.is_none() {
            self.notify_placement_error("描画の初期化前はアクタファイルから配置できません");
            return None;
        }

        let host = self.scripting_host.clone();
        let is_2d = req.is_2d;
        // scene を先に取り出してから draw_ctx を借りる（self の可変借用と
        // 不変借用が重ならないようにするため。DrawContext は Clone できない）。
        let Some(mut scene) = self.scene.take() else { return None };
        let draw_ctx = self.draw_ctx.as_ref();

        let (group, build_failed) = assemble_placement_group(
            &mut scene.world,
            wl,
            is_2d,
            &req.group_name,
            &req.name_prefix,
            points,
            &positions,
            |world| match &source {
                // 配置元 = アクタファイル: プレハブとして構築し、リンクを張る。
                Some((path, data)) => {
                    let ctx = draw_ctx.as_ref().expect("直前に None を弾いてある");
                    build_actor(data.clone(), ctx, world, host.as_ref(), None)
                        .map(|mut a| { a.prefab_source = Some(path.clone()); a })
                        .map_err(|e| format!("アクタファイル '{path}' の構築に失敗: {e}"))
                }
                // 配置元 = 空アクタ。
                None => Ok(spawn_empty_actor(world, is_2d)),
            },
        );

        // ── 1 体も作れなかったなら、グループごと捨てて何も変えない ──
        if group.children().is_empty() {
            scene.world.despawn(group.entity);
            self.scene = Some(scene);
            if build_failed {
                self.notify_placement_error(
                    "配置元アクタファイルの構築に失敗したため何も生成しませんでした",
                );
            }
            return None;
        }

        // 生成した子アクタのエンティティを控えてからツリーへ挿入する
        // （挿入で group は move されるため、先に取り出しておく）。
        let group_entity = group.entity;
        let created_entities: Vec<_> = group.children().iter().map(|a| a.entity).collect();
        insert_group_actor(&mut scene.actors, wl, group, req.parent_dfs, &[]);
        self.scene = Some(scene);

        if build_failed {
            self.notify_placement_error(
                "配置元アクタファイルの構築に途中で失敗したため、一部の点を生成できませんでした",
            );
        }

        Some((group_entity, created_entities))
    }

    /// 点列にアクタを生成し、**確定操作として**シーンへ反映する。
    ///
    /// `origin` は点列を足し込むワールド基準点。
    /// Undo は `ActorTreeSnapshotCommand` 1 件（グループ生成ごと戻る）。
    /// 生成に成功したら**生成した全アクタを選択状態**にしてエディタへ通知する
    /// （置いた直後にそのままギズモで動かせるようにするため）。
    ///
    /// 配置モード（`placement_mode.rs`）は仮スポーンを先に済ませているので
    /// 本関数を通らず、`spawn_placement_actors` ＋ 独自の確定処理を使う。
    pub(super) fn place_actors(
        &mut self,
        req:    &LogicPlaceRequest,
        points: &[PlacementPoint],
        origin: [f32; 3],
    ) {
        let wl = self.active_world_line;
        let before_actors = self.snapshot_actors_for_wl(wl);
        let Some((_group, created_entities)) = self.spawn_placement_actors(req, points, origin)
        else { return };

        // ── Undo 1 件（グループ生成ごと戻る）──
        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }

        // ── 生成した全アクタを選択状態にする ──
        // ヒエラルキー送信の**後**に行う（エディタ側が先に行を作っていないと
        // SELECTED_MULTI の仮想 ID を解決できず、選択が反映されないため）。
        self.select_placed_actors(wl, &created_entities);
    }

    /// 一括生成したアクタ群をまとめて選択状態にし、エディタへ通知する。
    ///
    /// DFS id はツリー挿入後にしか確定しないので、エンティティから引き直す。
    /// プライマリ（インスペクタに出る 1 体）は**最後の 1 体**にする
    ///（`SelectMulti` の受け口と同じ規則。連番の末尾＝直感的に「最後に置いたもの」）。
    pub(super) fn select_placed_actors(&mut self, wl: u32, entities: &[crate::engine::ecs::Entity]) {
        if entities.is_empty() { return; }
        let Some(scene) = self.scene.as_ref() else { return };
        let dfs_ids: Vec<usize> = dfs_ids_for_entities(&scene.actors, wl, entities)
            .into_iter()
            .flatten()
            .map(|id| id as usize)
            .collect();
        if dfs_ids.is_empty() { return; }

        self.actor_virtual_selected_idx = dfs_ids.last().copied();
        self.selected_actor_dfs_ids     = dfs_ids;
        // MC インスタンス単位の選択とは排他（アクタ選択へ倒す）。
        self.selected_instances.clear();
        self.send_selected();
        if let Some(idx) = self.actor_virtual_selected_idx {
            self.send_actor_components(idx as u32, self.actor_virtual_selected_slot_idx);
        }
    }

    // ── ② ControlPoint 点列への追記 ─────────────────────────

    /// 制御点配置の**最終ローカル位置**を求める（接地込み）。
    ///
    /// 手順は `ローカルで合成 → ワールド化 → 接地 → ローカルへ戻す`。
    /// 接地はワールドでしか意味を持たないので、点列の合成（アクタローカル）と
    /// 接地（ワールド）の間に対象アクタの行列を挟むのが唯一の正しい順序である。
    ///
    /// 接地しない場合（`ground = false` / 2D / 対象アクタの行列が引けない）は
    /// 合成しただけのローカル点列をそのまま返す。
    /// 戻り値は `(ローカル点列, 接地できなかった点数)`。
    ///
    /// **プレビューと確定はこの関数 1 か所を共有する**ので、
    /// 「人型アイコンが立っている高さに必ず点が入る」が構造的に保証される。
    pub(super) fn control_point_local_positions(
        &self,
        req:          &LogicPlaceRequest,
        points:       &[PlacementPoint],
        origin_local: [f32; 3],
    ) -> (Vec<[f32; 3]>, usize) {
        let mut locals = placement_world_positions(origin_local, points, usize::MAX);
        if !req.ground || req.is_2d { return (locals, 0); }

        // 対象アクタの行列が引けなければ接地を諦める（誤った高さへ飛ばさない）。
        let Some(m) = self.control_point_actor_matrix(req.actor_dfs_id) else {
            return (locals, 0);
        };
        let field = TerrainScatterField::from_state(&self.terrain);
        let missed = ground_local_positions_with(&field, &m, &mut locals);
        (locals, missed)
    }

    /// 生成点列を既存の ControlPoint スロットの末尾へ追記する。
    ///
    /// `origin_local` は**対象アクタのローカル空間での基準点**。配置モードから来た
    /// 場合はカーソル着弾点をアクタローカルへ変換した値、旧経路は `[0,0,0]`。
    ///
    /// Undo は `ComponentSlotsSnapshotCommand` 1 件（既存の点編集と同じ分類）。
    pub(super) fn place_control_points(
        &mut self,
        req: &LogicPlaceRequest,
        points: &[PlacementPoint],
        origin_local: [f32; 3],
    ) {
        let Some(entity) = self.control_point_slot_entity(req.actor_dfs_id, req.slot_idx) else {
            self.notify_placement_error("対象の ControlPoint スロットが見つかりません");
            return;
        };

        let wl = self.active_world_line;
        let before_slots = self.snapshot_actor_slots(wl, req.actor_dfs_id);

        // ── 位置を先に確定する（接地は地形を不変借用するので、
        //    シーンの可変借用より前に済ませる）──
        let (positions, missed) = self.control_point_local_positions(req, points, origin_local);
        if missed > 0 {
            self.notify_placement_error(&format!(
                "地形接地: {missed} 点で地表が見つからなかったため基準の高さのままにしました"
            ));
        }

        let (added, dropped) = {
            let Some(scene) = &mut self.scene else { return };
            let Some(c) = scene.world.get_mut::<ControlPointComponent>(entity) else {
                self.notify_placement_error("ControlPointComponent を取得できません");
                return;
            };
            let start_time = c.next_default_time();
            append_control_points_at(&mut c.points, points, &positions, start_time)
        };

        if dropped > 0 {
            self.notify_placement_error(&format!(
                "制御点の上限（{MAX_CONTROL_POINTS} 点）に達したため {dropped} 点を切り詰めました"
            ));
        }
        if added == 0 { return; } // 1 点も入らなかったなら履歴も汚さない

        let after_slots = self.snapshot_actor_slots(wl, req.actor_dfs_id);
        self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
            world_line: wl,
            actor_dfs_id: req.actor_dfs_id,
            before_slots,
            after_slots,
        }));
        self.send_actor_components(req.actor_dfs_id, req.slot_idx as usize);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }
}

// ============================================================
//  テスト — App を組まずに中核の自由関数だけを検証する
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::app_base::undo::{ActorTreeSnapshotCommand, Command};
    use crate::engine::placement::{PlacementPattern, PlacementSpec};
    use crate::engine::terrain::settings::TerrainSettings;

    /// テスト用の世界線。
    const TEST_WL: u32 = 0;

    // ─── 接地用のテストダブル ─────────────────────────────

    /// 高さ `plane_y` の無限平面だけを持つ密度場。
    ///
    /// 密度規約は「density < iso_level なら SOLID」なので、
    /// `density = y - plane_y` とすれば平面より下が SOLID になる。
    /// `plane_y` が `None` のときは全域 AIR（＝地形が無いシーン）。
    struct FlatField {
        settings: TerrainSettings,
        plane_y:  Option<f32>,
    }

    impl ScatterField for FlatField {
        fn settings(&self) -> &TerrainSettings { &self.settings }
        fn density_at(&self, p: [f32; 3]) -> f32 {
            match self.plane_y {
                // iso_level を足して「平面より下＝ iso 未満」に揃える。
                Some(y) => p[1] - y + self.settings.iso_level,
                // 全域 AIR: iso より必ず大きい値を返す。
                None => self.settings.iso_level + 1.0,
            }
        }
        fn layer_weight_at(&self, _p: [f32; 3], _layer: &str) -> f32 { 0.0 }
    }

    /// 平面 `plane_y` を持つ密度場を作る（`None` で地形なし）。
    fn flat_field(plane_y: Option<f32>) -> FlatField {
        FlatField { settings: TerrainSettings::default(), plane_y }
    }

    // ─── 接地 ─────────────────────────────────────────────

    /// 平面地形では全点がその高さへ落ち、取りこぼしが 0 であること。
    #[test]
    fn grounding_snaps_points_to_surface() {
        let field = flat_field(Some(3.0));
        let mut positions = [[0.0, 0.0, 0.0], [5.0, 10.0, -5.0], [-2.0, -8.0, 1.0]];
        let missed = ground_positions_with(&field, &mut positions);
        assert_eq!(missed, 0, "平面地形なら全点が接地すること");
        for p in &positions {
            assert!((p[1] - 3.0).abs() < 0.05, "地表 Y=3 へ落ちること: {p:?}");
        }
    }

    /// **接地フォールバック**: 地形が無ければ Y を動かさず、取りこぼし数を返すこと。
    #[test]
    fn grounding_falls_back_to_base_height_without_terrain() {
        let field = flat_field(None);
        let mut positions = [[0.0, 7.0, 0.0], [1.0, -3.0, 2.0]];
        let missed = ground_positions_with(&field, &mut positions);
        assert_eq!(missed, 2, "接地できなかった点数を返すこと");
        assert_eq!(positions[0][1], 7.0, "基準 Y のまま残ること");
        assert_eq!(positions[1][1], -3.0);
    }

    /// 探索範囲の外にある地表は拾わないこと（暴走接地の防止）。
    #[test]
    fn grounding_ignores_surface_outside_probe_range() {
        // 基準 Y=0 に対し、地表は下方向マージンより遙かに下。
        let field = flat_field(Some(-(GROUND_PROBE_DOWN + 50.0)));
        let mut positions = [[0.0, 0.0, 0.0]];
        let missed = ground_positions_with(&field, &mut positions);
        assert_eq!(missed, 1, "探索範囲外の地表は拾わないこと");
        assert_eq!(positions[0][1], 0.0);
    }

    // ─── グループ組み立て ─────────────────────────────────

    /// **生成アクタがグループ配下に入る**こと（ルート直下へ散らばらないこと）。
    #[test]
    fn generated_actors_go_under_the_group() {
        let mut world = World::new();
        let spec = PlacementSpec {
            pattern: PlacementPattern::Grid,
            rows: 2, cols: 3, layers: 1,
            ..Default::default()
        };
        let points = generate_points(&spec).points;
        let positions: Vec<[f32; 3]> = points.iter().map(|p| p.position).collect();

        let (group, failed) = assemble_placement_group(
            &mut world, TEST_WL, false, "グリッド配置", "Tree",
            &points, &positions,
            |w| Ok(spawn_empty_actor(w, false)),
        );

        assert!(!failed);
        assert_eq!(group.name, "グリッド配置");
        assert_eq!(group.children().len(), 6, "行×列ぶんの子が入ること");
        assert_eq!(group.world_line, TEST_WL);
        // 連番命名（1 始まり・2 桁ゼロ詰め）。
        assert_eq!(group.children()[0].name, "Tree_01");
        assert_eq!(group.children()[5].name, "Tree_06");
        // 子の位置が配置点どおりであること。
        for (child, pos) in group.children().iter().zip(positions.iter()) {
            let tf = world.get::<Transform>(child.entity).expect("3D 子は Transform を持つ");
            for k in 0..3 {
                assert!((tf.position[k] - pos[k]).abs() < 1.0e-4,
                        "配置点に置かれること: {:?} vs {pos:?}", tf.position);
            }
        }
    }

    /// 2D 配置ではパターンの XZ がキャンバスの XY へ写ること。
    #[test]
    fn generated_2d_actors_use_canvas_transform() {
        let mut world = World::new();
        let points = vec![PlacementPoint {
            position: [3.0, 0.0, -4.0],
            rotation: [0.0, 30.0, 0.0],
            scale:    [2.0, 1.0, 2.0],
        }];
        let positions = vec![[3.0, 0.0, -4.0]];

        let (group, _) = assemble_placement_group(
            &mut world, TEST_WL, true, "2Dグループ", "Icon",
            &points, &positions,
            |w| Ok(spawn_empty_actor(w, true)),
        );

        let child = &group.children()[0];
        let ct = world.get::<CanvasTransform>(child.entity).expect("2D 子は CanvasTransform を持つ");
        assert_eq!(ct.position, [3.0, -4.0], "XZ → XY へ写ること");
        assert!((ct.rotation - 30.0).abs() < 1.0e-4, "ヨーが Z 回転として乗ること");
        assert_eq!(ct.scale, [2.0, 2.0], "スケールが乗ること");
    }

    /// ファクトリが失敗したら打ち切り、失敗フラグを返すこと。
    #[test]
    fn assembly_stops_when_factory_fails() {
        let mut world = World::new();
        let points = vec![PlacementPoint::default(); 5];
        let positions = vec![[0.0, 0.0, 0.0]; 5];

        let mut made = 0;
        let (group, failed) = assemble_placement_group(
            &mut world, TEST_WL, false, "G", "A", &points, &positions,
            |w| {
                made += 1;
                if made > 2 { Err("わざと失敗".into()) } else { Ok(spawn_empty_actor(w, false)) }
            },
        );
        assert!(failed, "失敗を報告すること");
        assert_eq!(group.children().len(), 2, "失敗する前までは生成されること");
    }

    // ─── Undo ─────────────────────────────────────────────

    /// **Undo でグループごと消える**こと（アクタツリーのスナップショット差し替え）。
    #[test]
    fn undo_removes_the_generated_group() {
        let mut world = World::new();
        let mut actors: Vec<Actor> = Vec::new();

        // 生成前のスナップショット（空のシーン）。
        let before: Vec<crate::engine::structs::objects::actor::ActorData> = actors
            .iter()
            .filter(|a| a.world_line == TEST_WL)
            .map(|a| a.to_data_recursive(&world, &mut None))
            .collect();

        let points = vec![PlacementPoint::default(); 3];
        let positions = vec![[0.0, 0.0, 0.0]; 3];
        let (group, _) = assemble_placement_group(
            &mut world, TEST_WL, false, "円形配置", "P", &points, &positions,
            |w| Ok(spawn_empty_actor(w, false)),
        );
        insert_group_actor(&mut actors, TEST_WL, group, None, &[]);
        assert_eq!(actors.len(), 1, "グループ 1 個がルートへ入ること");
        assert_eq!(actors[0].children().len(), 3);

        let after: Vec<_> = actors
            .iter()
            .filter(|a| a.world_line == TEST_WL)
            .map(|a| a.to_data_recursive(&world, &mut None))
            .collect();

        // ActorTreeSnapshotCommand が「戻す側のデータ」を持っていること
        // （実際の適用は App の rebuild_actors_for_wl が行うため、ここでは
        //   スナップショットの内容で契約を固定する）。
        let cmd = ActorTreeSnapshotCommand {
            world_line: TEST_WL,
            before_actors: before.clone(),
            after_actors: after.clone(),
        };
        assert_eq!(cmd.before_actors.len(), 0, "Undo 先は生成前＝アクタ 0 件");
        assert_eq!(cmd.after_actors.len(), 1, "Redo 先は生成後＝グループ 1 件");
        assert_eq!(cmd.after_actors[0].children.len(), 3, "グループ配下に 3 体");
        // UndoCommand として扱えること（履歴へ積める型であることの確認）。
        let _boxed: Box<dyn Command> = Box::new(cmd);
    }

    // ─── 制御点のローカル接地（ローカル → ワールド → 接地 → ローカル）───

    /// テスト用のアクタ行列（平行移動・回転・スケール）。
    fn actor_mat(position: [f32; 3], rotation: [f32; 3], scale: [f32; 3]) -> [[f32; 4]; 4] {
        Transform { position, rotation, scale, ..Default::default() }.to_mat4()
    }

    /// 2 点の各成分がほぼ等しいこと。
    fn assert_close(a: [f32; 3], b: [f32; 3], what: &str) {
        for k in 0..3 {
            assert!((a[k] - b[k]).abs() < 1.0e-3, "{what}: {a:?} != {b:?}");
        }
    }

    /// **接地 ON**: アクタが持ち上がっていても、各点はワールドの地表 Y に乗ること。
    ///
    /// アクタが Y = +10 に居るので、地表 Y = 3 のローカル Y は −7 になる。
    /// 「ローカルのまま接地する」実装だと 3 のままになるので、この値が順序を固定する。
    #[test]
    fn cp_grounding_lands_points_on_the_terrain_in_world_space() {
        let field = flat_field(Some(3.0));
        let m = actor_mat([0.0, 10.0, 0.0], [0.0; 3], [1.0; 3]);
        let mut locals = [[1.0, 0.0, 0.0], [-2.0, 5.0, 4.0]];
        let missed = ground_local_positions_with(&field, &m, &mut locals);
        assert_eq!(missed, 0, "平面地形なら全点が接地すること");
        for p in &locals {
            assert!((p[1] + 7.0).abs() < 0.05, "ワールド Y=3 ＝ ローカル Y=-7: {p:?}");
        }
        assert_close(locals[0], [1.0, -7.0, 0.0], "XZ は動かないこと");
    }

    /// **接地失敗**: 地形が無ければローカル座標は 1 mm も動かないこと（基準 Y のまま）。
    #[test]
    fn cp_grounding_keeps_local_position_without_terrain() {
        let field = flat_field(None);
        let m = actor_mat([4.0, 1.0, -2.0], [0.0, 30.0, 0.0], [2.0, 2.0, 2.0]);
        let before = [[1.0, 0.5, 0.0], [0.0, -3.0, 2.0]];
        let mut locals = before;
        let missed = ground_local_positions_with(&field, &m, &mut locals);
        assert_eq!(missed, 2, "接地できなかった点数を返すこと");
        assert_close(locals[0], before[0], "基準の高さのまま");
        assert_close(locals[1], before[1], "基準の高さのまま");
    }

    /// **ローカル変換の往復**: 回転・スケール付きのアクタでも、
    /// 接地後のローカル点をワールドへ戻すと地表 Y に一致すること。
    #[test]
    fn cp_grounding_round_trips_through_rotation_and_scale() {
        let field = flat_field(Some(-2.0));
        let m = actor_mat([3.0, 6.0, -1.0], [0.0, 45.0, 0.0], [2.0, 0.5, 2.0]);
        let mut locals = [[1.0, 0.0, 1.0], [-1.0, 2.0, 0.5], [0.0, -4.0, -2.0]];
        let missed = ground_local_positions_with(&field, &m, &mut locals);
        assert_eq!(missed, 0);
        for p in &locals {
            let world = transform_point(&m, *p);
            assert!((world[1] + 2.0).abs() < 0.05, "ワールドでは地表 Y=-2: {world:?}");
        }
    }

    /// **接地 OFF と同じ出力**: 接地を通さない点列は従来どおり「基準点 + パターン座標」。
    ///
    /// `append_control_points`（旧シグネチャの薄いラッパ）と
    /// `append_control_points_at`（接地済み位置を渡す新経路）が、
    /// 接地なしのとき**同じ結果**になることを固定する。
    #[test]
    fn cp_ungrounded_path_matches_the_legacy_origin_path() {
        let generated = vec![
            PlacementPoint { position: [1.0, 0.0, 0.0], ..Default::default() },
            PlacementPoint { position: [0.0, 2.0, 3.0], ..Default::default() },
        ];
        let origin = [10.0, -3.0, 5.0];

        let mut legacy: Vec<ControlPoint> = Vec::new();
        append_control_points(&mut legacy, &generated, origin, 0.0);

        let positions = placement_world_positions(origin, &generated, usize::MAX);
        let mut modern: Vec<ControlPoint> = Vec::new();
        append_control_points_at(&mut modern, &generated, &positions, 0.0);

        assert_eq!(legacy.len(), modern.len());
        for (a, b) in legacy.iter().zip(modern.iter()) {
            assert_eq!(a.position, b.position, "接地 OFF なら従来と同一出力");
            assert_eq!(a.time, b.time);
        }
    }

    /// `append_control_points_at` が**位置だけ**を差し替え、
    /// 回転・スケール・時刻はパターン側の値を保つこと（接地で姿勢が壊れない）。
    #[test]
    fn cp_append_at_uses_given_positions_and_keeps_pose() {
        let generated = vec![PlacementPoint {
            position: [1.0, 0.0, 0.0],
            rotation: [0.0, 90.0, 0.0],
            scale:    [2.0, 2.0, 2.0],
        }];
        let mut existing: Vec<ControlPoint> = Vec::new();
        let (added, dropped) =
            append_control_points_at(&mut existing, &generated, &[[7.0, -1.0, 4.0]], 2.0);
        assert_eq!((added, dropped), (1, 0));
        assert_eq!(existing[0].position, [7.0, -1.0, 4.0], "与えた位置がそのまま入る");
        assert_eq!(existing[0].rotation, [0.0, 90.0, 0.0], "回転は据え置き");
        assert_eq!(existing[0].scale, [2.0, 2.0, 2.0], "スケールは据え置き");
        assert_eq!(existing[0].time, 2.0);
    }

    /// **アクタ版の接地は不変**: ワールド点列の接地は行列を挟まない従来どおりの結果。
    #[test]
    fn actor_grounding_output_is_unchanged() {
        let field = flat_field(Some(1.5));
        let mut positions = [[0.0, 20.0, 0.0], [3.0, -20.0, 3.0]];
        let missed = ground_positions_with(&field, &mut positions);
        assert_eq!(missed, 0);
        for p in &positions {
            assert!((p[1] - 1.5).abs() < 0.05, "地表 Y=1.5 へ落ちること: {p:?}");
        }
    }

    // ─── 制御点の追記 ─────────────────────────────────────

    /// 空の点列へ追記すると、時刻が 0 から既定ステップで振られること。
    #[test]
    fn control_points_append_with_sequential_time() {
        let mut existing: Vec<ControlPoint> = Vec::new();
        let generated = vec![
            PlacementPoint { position: [1.0, 0.0, 0.0], ..Default::default() },
            PlacementPoint { position: [2.0, 0.0, 0.0], ..Default::default() },
        ];
        let (added, dropped) = append_control_points(&mut existing, &generated, [0.0; 3], 0.0);
        assert_eq!((added, dropped), (2, 0));
        assert_eq!(existing[0].time, 0.0);
        assert_eq!(existing[1].time, DEFAULT_TIME_STEP);
        assert_eq!(existing[0].position, [1.0, 0.0, 0.0]);
    }

    /// 既存点の**末尾へ**追記されること（先頭に割り込まないこと）。
    #[test]
    fn control_points_are_appended_to_the_tail() {
        let mut existing = vec![ControlPoint { position: [9.0, 9.0, 9.0], time: 5.0, ..Default::default() }];
        let generated = vec![PlacementPoint { position: [1.0, 0.0, 0.0], ..Default::default() }];
        append_control_points(&mut existing, &generated, [0.0; 3], 6.0);
        assert_eq!(existing.len(), 2);
        assert_eq!(existing[0].position, [9.0, 9.0, 9.0], "既存点が先頭のまま残ること");
        assert_eq!(existing[1].time, 6.0, "渡した開始時刻から振られること");
    }

    /// **上限を超えるぶんは切り詰め**、その件数を返すこと。
    #[test]
    fn control_points_truncate_at_max() {
        let mut existing = vec![ControlPoint::default(); MAX_CONTROL_POINTS - 2];
        let generated = vec![PlacementPoint::default(); 10];
        let (added, dropped) = append_control_points(&mut existing, &generated, [0.0; 3], 0.0);
        assert_eq!(added, 2, "空きぶんだけ入ること");
        assert_eq!(dropped, 8, "溢れた件数を返すこと（黙って捨てない）");
        assert_eq!(existing.len(), MAX_CONTROL_POINTS);
    }

    /// 既に満杯なら 1 点も追記しないこと（呼び出し側が Undo を積まない判断に使う）。
    #[test]
    fn control_points_add_nothing_when_full() {
        let mut existing = vec![ControlPoint::default(); MAX_CONTROL_POINTS];
        let generated = vec![PlacementPoint::default(); 3];
        let (added, dropped) = append_control_points(&mut existing, &generated, [0.0; 3], 0.0);
        assert_eq!((added, dropped), (0, 3));
        assert_eq!(existing.len(), MAX_CONTROL_POINTS);
    }

    /// **基準点が点の位置へ足される**こと（配置モードのカーソル位置が効く経路）。
    ///
    /// 基準点はアクタローカルなので、そのままパターン座標へ足せば点の位置になる。
    #[test]
    fn control_points_are_offset_by_the_local_origin() {
        let mut existing: Vec<ControlPoint> = Vec::new();
        let generated = vec![
            PlacementPoint { position: [1.0, 0.0, 0.0], ..Default::default() },
            PlacementPoint { position: [0.0, 0.0, 2.0], ..Default::default() },
        ];
        let (added, dropped) =
            append_control_points(&mut existing, &generated, [10.0, -3.0, 5.0], 0.0);
        assert_eq!((added, dropped), (2, 0));
        assert_eq!(existing[0].position, [11.0, -3.0, 5.0], "基準点 + パターン座標");
        assert_eq!(existing[1].position, [10.0, -3.0, 7.0]);
    }

    /// 基準点付きでも**末尾追記と上限切り詰め**の規則が変わらないこと。
    #[test]
    fn control_points_with_origin_still_append_and_truncate() {
        let mut existing = vec![
            ControlPoint { position: [0.0; 3], time: 1.0, ..Default::default() };
            MAX_CONTROL_POINTS - 1
        ];
        let generated = vec![PlacementPoint { position: [1.0, 1.0, 1.0], ..Default::default() }; 5];
        let (added, dropped) =
            append_control_points(&mut existing, &generated, [100.0; 3], 9.0);
        assert_eq!((added, dropped), (1, 4), "空き 1 点ぶんだけ入り、4 点は切り詰め");
        assert_eq!(existing.len(), MAX_CONTROL_POINTS);
        let last = existing.last().expect("末尾へ追記されていること");
        assert_eq!(last.position, [101.0; 3], "追記された 1 点にも基準点が効くこと");
        assert_eq!(last.time, 9.0, "時刻は渡した開始値から");
    }

    // ─── リクエストの JSON 互換 ───────────────────────────

    /// 最小限の JSON（ほぼ全フィールド欠落）でも既定値で復元できること。
    ///
    /// エディタとランタイムのバージョンがずれても IPC が丸ごと落ちないための契約。
    #[test]
    fn request_deserializes_from_minimal_json() {
        let req: LogicPlaceRequest = serde_json::from_str(r#"{"target":"actors"}"#)
            .expect("最小 JSON から復元できること");
        assert_eq!(req.target, "actors");
        assert!(!req.is_2d);
        assert!(req.parent_dfs.is_none());
        assert_eq!(req.spec.pattern, PlacementPattern::Circle, "spec も既定値で埋まること");
    }

    /// 名前の連番書式が仕様どおり（1 始まり・2 桁ゼロ詰め・3 桁以上は伸びる）。
    #[test]
    fn actor_names_are_one_based_and_zero_padded() {
        assert_eq!(placement_actor_name("Tree", 0), "Tree_01");
        assert_eq!(placement_actor_name("Tree", 9), "Tree_10");
        assert_eq!(placement_actor_name("Tree", 99), "Tree_100");
    }

    // ─── 生成物の選択（確定後に全件が選ばれること）─────────

    /// **確定後の選択集合が生成アクタ全件**になること。
    ///
    /// 選択は DFS id で送るので、ツリーへ挿入したあとに
    /// 「生成した子アクタのエンティティ → DFS id」を全件引けることが要になる。
    /// ここが 1 件でも欠けると、置いた直後にまとめて動かせない。
    #[test]
    fn placed_actors_all_resolve_to_dfs_ids_for_selection() {
        let mut world = World::new();
        let spec = PlacementSpec {
            pattern: PlacementPattern::Grid,
            rows: 2, cols: 3, layers: 1,
            ..Default::default()
        };
        let points = generate_points(&spec).points;
        let positions: Vec<[f32; 3]> = points.iter().map(|p| p.position).collect();

        let (group, _) = assemble_placement_group(
            &mut world, TEST_WL, false, "グリッド配置", "Tree",
            &points, &positions,
            |w| Ok(spawn_empty_actor(w, false)),
        );
        let created: Vec<_> = group.children().iter().map(|a| a.entity).collect();
        assert_eq!(created.len(), 6);

        // 既存アクタが 1 体いる（＝グループが DFS の先頭に来ない）状況で確かめる。
        let existing = world.spawn();
        let mut root = Actor::new(existing, "既存");
        root.world_line = TEST_WL;
        let mut actors = vec![root];
        insert_group_actor(&mut actors, TEST_WL, group, None, &[]);

        let ids = dfs_ids_for_entities(&actors, TEST_WL, &created);
        assert!(ids.iter().all(|id| id.is_some()), "生成した全アクタの DFS id が引けること: {ids:?}");
        let ids: Vec<u32> = ids.into_iter().flatten().collect();
        assert_eq!(ids.len(), created.len(), "選択集合が生成アクタ全件であること");
        // DFS 順は [既存(0), グループ(1), 子(2..8)]。子は連番になる。
        assert_eq!(ids, vec![2, 3, 4, 5, 6, 7], "挿入後の DFS id が走査順どおりであること");
    }

    /// 未知のエンティティは `None` になり、他の要素の解決を巻き添えにしないこと。
    #[test]
    fn dfs_id_lookup_reports_missing_entities_as_none() {
        let mut world = World::new();
        let present = world.spawn();
        let absent  = world.spawn();
        let mut root = Actor::new(present, "居る");
        root.world_line = TEST_WL;
        let actors = vec![root];

        let ids = dfs_ids_for_entities(&actors, TEST_WL, &[absent, present]);
        assert_eq!(ids, vec![None, Some(0)]);
    }
}
