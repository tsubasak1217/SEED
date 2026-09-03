// ============================================================
//  jointattach_ops.rs — ジョイントアタッチ（ソケット）の App 統合
//
//  【役割】
//  JointAttachComponent を持つアクターを、祖先アクターのモデルのジョイント
//  （ボーン）へ毎フレーム追従させる（ソケット機構）。ECS 理念に従い、追従の
//  ロジックはここ（システム側）に置き、コンポーネントはデータのみを保持する。
//
//  【毎フレームの流れ】update_joint_attachments()
//    1. アクター木を DFS し、各 JointAttach スロットについて「上方向へ辿り
//       最初に Model スロットを持つ祖先アクター」を解決してジョブ化する。
//    2. モデルごと・フレームごとに 1 回だけ、ノード階層のワールド行列
//       （モデル空間）を計算してキャッシュする（同一モデルへの複数アタッチで共有）。
//       時刻源は ModelComponent.anim_drive（Play 中の Animator 権威時刻）。
//       anim_drive が無ければ静止＝バインドポーズ（t0 相当）で解決する。
//    3. 描画とまったく同じ空間合成（compose_attached_world）で最終ワールド行列を作り、
//       自アクターの Transform と Model instance_mats[0] へ書き込む。
//    4. Play 中のみ、追従アクターの子孫アクターを「追従アクター基準の相対ローカル行列」で
//       絶対的に配置し直す（update_attached_descendants）。SEED の Transform はワールド保持・
//       親子行列合成なしのため、この処理が無いと「竿の先端に置いた子アクター」が取り残される。
//       相対ローカルは子孫ごとに 1 回だけ採取してキャッシュする（App::joint_attach_child_locals）。
//
//  【なぜ差分伝播ではなく絶対配置なのか（重要）】
//  以前は delta = new * inv(前フレームの追従ワールド) を子孫へ伝播していた。しかし
//  追従アクターの祖先（プレイヤー）がスクリプトで動くと、transform_sync が同じ移動量を
//  すでに子孫（竿・竿先の両方）へ伝播している。その直後に JointAttach が「前フレームの
//  竿ワールド（＝プレイヤー移動が乗る前）」を基準に delta を作って竿先へ適用するため、
//  プレイヤーの移動が竿先にだけ二重に効き、毎フレーム誤差が蓄積して竿先が飛んでいった。
//  絶対配置（child_world = new_world × local）なら、途中で外部から何度動かされようと
//  結果は local と new_world だけで決まるので、この蓄積が原理的に起こらない。
//
//  【1 フレーム遅延について】
//  スクリプト（プレイヤー移動）は本更新より前に走るため、子孫の配置は常にその
//  フレームの最新の追従行列に基づく。逆に本更新より後に祖先を動かす経路があれば
//  子孫は 1 フレーム遅れる（現状そのような経路は無い）。
//
//  【空間合成が描画と一致していなければならない理由】
//  ソケットは「画面に見えているボーンの位置」へ吸着しなければ意味がない。
//  スキンメッシュの頂点は頂点シェーダで
//      world = u_model（＝ アクタワールド × 描画オフセット × メッシュノードのバインドワールド）
//              × スキン行列（＝ ジョイントノードのワールド × 逆バインド行列）
//              × ローカル座標
//  として求まる（gbuffer_skinned_vertex.wgsl / gpu_resources::fill_chunk）。
//  したがってボーンの実姿勢は
//      アクタワールド × 描画オフセット × メッシュノードのバインドワールド × ジョイントワールド
//  であり、この 4 つを漏れなく掛けないとソケットはボーンから外れる。
//
//  【Edit / Play 両対応】
//  パーティクルの常時プレビューと同様、本更新は毎フレーム（モード非依存）で走る。
//  Edit では anim_drive が無いためモデルは静止し、アタッチもバインドポーズの
//  ジョイント位置へ吸着する。
//  ただし **子孫アクターの配置は Play 中のみ** 行う。Edit で配置し直すと、竿先マーカーを
//  エディタで編集している最中に毎フレーム引き戻されて編集できなくなるためである
//  （相対ローカルの採取も Play 中のみ＝ユーザーが Edit で置いた位置関係が正となる）。
// ============================================================

use crate::engine::components::{ComponentKind, JointAttachComponent, ModelComponent, Transform};
use crate::engine::core::loader::model::Model;
use crate::engine::core::renderer::animator::{
    compute_node_world_matrices_blend, identity, mat4_mul,
};
use crate::engine::core::transform_sync::Mat4;
use crate::engine::ecs::{Entity, World};
use crate::engine::methods::gizmo_interact::{mat4x4_inv, mat4x4_mul};
use crate::engine::structs::objects::Actor;
use std::collections::HashMap;

use super::App;

// ─── ジョイント名一覧（エディタ送信用ヘルパ）─────────────────────

/// モデルのジョイント（ボーン）名一覧を返す。
///
/// skin を持つモデルは skin ジョイント名を優先して返す（スケルタルアニメの
/// ボーン集合）。skin が無いモデルは全ノード名を返す（静的モデルでも任意ノードへ
/// アタッチできるようにするため）。無名要素は空文字のまま含める（解決キーと一致させる）。
pub fn model_joint_names(model: &Model) -> Vec<String> {
    if !model.skins.is_empty() {
        // 全 skin のジョイント名を順に収集する（重複はそのまま：名前一致で解決するため）
        model
            .skins
            .iter()
            .flat_map(|s| s.joints.iter().map(|j| j.name.clone()))
            .collect()
    } else {
        model.nodes.iter().map(|n| n.name.clone()).collect()
    }
}

/// 指定 DFS id のアクターのターゲットモデル（上方向へ辿り最初の Model スロットを
/// 持つ祖先アクター）のジョイント名一覧を返す。ターゲットが無ければ空 Vec。
///
/// インスペクタのジョイントドロップダウン（JointAttachComponent の選択肢）用。
pub fn collect_target_model_joints(
    actors: &[Actor],
    world: &World,
    wl: u32,
    dfs_id: u32,
) -> Vec<String> {
    let mut counter = 0u32;
    let mut found: Option<Vec<String>> = None;
    for root in actors.iter().filter(|a| a.world_line == wl) {
        if find_target_joints_recursive(root, world, None, dfs_id, &mut counter, &mut found) {
            break;
        }
    }
    found.unwrap_or_default()
}

/// DFS で dfs_id のアクターに到達したら、その祖先モデルのジョイント名一覧を `found` に格納する。
/// 戻り値 true = 目的アクターに到達済み（探索打ち切り）。
fn find_target_joints_recursive(
    actor: &Actor,
    world: &World,
    ancestor_model: Option<Entity>, // 祖先の最初の Model スロット entity
    dfs_id: u32,
    counter: &mut u32,
    found: &mut Option<Vec<String>>,
) -> bool {
    let current = *counter;
    *counter += 1;

    if current == dfs_id {
        // 祖先モデルのジョイント名一覧を解決する（モデル未ロード時は空）
        *found = Some(
            ancestor_model
                .and_then(|me| world.get::<ModelComponent>(me))
                .and_then(|mc| mc.model.as_ref())
                .map(|m| model_joint_names(m))
                .unwrap_or_default(),
        );
        return true;
    }

    // 子孫から見た「最寄りモデル」= このアクターが Model を持てばそれ、無ければ継承
    let this_model = first_enabled_model_slot(actor);
    let child_ancestor = this_model.or(ancestor_model);
    for child in actor.children() {
        if find_target_joints_recursive(child, world, child_ancestor, dfs_id, counter, found) {
            return true;
        }
    }
    false
}

/// アクターの最初の有効な Model スロットの entity を返す（無ければ None）。
fn first_enabled_model_slot(actor: &Actor) -> Option<Entity> {
    actor
        .slots()
        .iter()
        .find(|s| s.kind == ComponentKind::Model && s.enabled)
        .map(|s| s.entity)
}

// ─── 空間合成（純関数）───────────────────────────────────────

/// アタッチ対象アクターの最終ワールド行列を合成する【空間合成の正典】。
///
///   final = actor_world × render_offset × mesh_node_world × joint_world × attach_offset
///
/// - `actor_world`   : モデルを持つアクターのワールド行列（Transform 由来）
/// - `render_offset` : そのモデルの描画オフセット行列（`ModelComponent::offset_matrix`）。
///   描画インスタンス行列は `actor_world × offset` で作られる（`ModelComponent::render_matrix`）
///   ため、これを掛けないと「見えているモデル」と別の場所へ吸着してしまう。
/// - `mesh_node_world` : スキンメッシュノードのバインドポーズワールド行列（モデル空間）。
///   本エンジンの描画はスキンメッシュにもノードのワールド行列を掛ける
///   （`gpu_resources::fill_chunk` が `mesh_index` を持つ全ノードの行列を積む）ため、
///   ボーンの実姿勢にもこれが乗る。スキンに属さないノードへのアタッチでは単位行列を渡す。
/// - `joint_world`   : ジョイントノードのワールド行列（モデル空間・アニメ適用後）
/// - `attach_offset` : JointAttachComponent のオフセット行列（ジョイントローカル基準）
pub fn compose_attached_world(
    actor_world: &[[f32; 4]; 4],
    render_offset: &[[f32; 4]; 4],
    mesh_node_world: &[[f32; 4]; 4],
    joint_world: &[[f32; 4]; 4],
    attach_offset: &[[f32; 4]; 4],
) -> [[f32; 4]; 4] {
    let m = mat4_mul(actor_world, render_offset);
    let m = mat4_mul(&m, mesh_node_world);
    let m = mat4_mul(&m, joint_world);
    mat4_mul(&m, attach_offset)
}

/// `joint_node_idx` がスキンのジョイントである場合、そのスキンを使うメッシュノードの
/// インデックスを返す（スキンに属さないノードなら `None`）。
///
/// 描画はスキンメッシュノードのワールド行列をスキン行列へさらに左から掛けるため、
/// ソケットの合成でも同じノードの行列を挟む必要がある（`compose_attached_world` 参照）。
/// 同一スキンを複数のメッシュノードが共有する異形モデルでは先頭ノードを採用する
/// （描画はどちらのノードでも同じスキン行列を使うため、代表 1 個で十分）。
pub fn skinned_mesh_node_index(model: &Model, joint_node_idx: usize) -> Option<usize> {
    let skin_idx = model
        .skins
        .iter()
        .position(|s| s.joints.iter().any(|j| j.node_index == joint_node_idx))?;
    model
        .nodes
        .iter()
        .position(|n| n.mesh_index.is_some() && n.skin_index == Some(skin_idx))
}

// ─── アタッチジョブ ───────────────────────────────────────────

/// 1 件の追従ジョブ（借用フェーズで収集し、書き込みフェーズで消費する）。
struct AttachJob<'a> {
    /// 追従させる（書き込み先）アクター
    attached: &'a Actor,
    /// JointAttach スロットの entity（パラメータ読み出し・警告キー用）
    attach_slot: Entity,
    /// ターゲットモデルの Model スロット entity（ノード行列の取得元）
    model_slot: Entity,
    /// ターゲットモデルアクターの entity（ワールド行列 Transform の取得元）
    model_actor: Entity,
}

/// アクター木を DFS し、有効な JointAttach スロットについて追従ジョブを収集する。
/// ancestor_model = (最寄り祖先の Model スロット entity, そのアクターの entity)。
fn collect_attach_jobs<'a>(
    actor: &'a Actor,
    ancestor_model: Option<(Entity, Entity)>,
    out: &mut Vec<AttachJob<'a>>,
) {
    // 非アクティブなアクター配下はソケット追従させない（描画もされないため）
    if !actor.active {
        return;
    }

    // このアクターの JointAttach スロットは「祖先モデル」を対象にする（自分自身の Model ではない）
    for slot in actor.slots() {
        if slot.kind == ComponentKind::JointAttach && slot.enabled {
            if let Some((model_slot, model_actor)) = ancestor_model {
                out.push(AttachJob {
                    attached: actor,
                    attach_slot: slot.entity,
                    model_slot,
                    model_actor,
                });
            }
        }
    }

    // 子孫から見た最寄りモデル: このアクターが Model を持てばそれ、無ければ継承する
    let child_ancestor = first_enabled_model_slot(actor)
        .map(|e| (e, actor.entity))
        .or(ancestor_model);
    for child in actor.children() {
        collect_attach_jobs(child, child_ancestor, out);
    }
}

impl App {
    /// ジョイントアタッチの毎フレーム更新（Edit / Play 両対応・モード非依存）。
    ///
    /// frame_renderer のアニメーション評価後・描画インスタンス収集前に呼ばれる。
    pub(super) fn update_joint_attachments(&mut self) {
        let active_wl = self.active_world_line;
        // 子孫の配置（相対ローカルの採取と適用）は Play 中のみ行う。Edit の挙動は
        // 本機能導入前と同じに保ち、エディタでの子アクター編集を邪魔しない。
        let is_play = self.mode == super::RuntimeMode::Play;
        // self の別フィールドを個別に可変借用する（disjoint borrow）
        let warned = &mut self.joint_attach_warned;
        // 子孫アクターの相対ローカル行列キャッシュ。warned とは別フィールドなので
        // 同時に可変借用できる（disjoint borrow）。
        let child_locals = &mut self.joint_attach_child_locals;
        let Some(scene) = self.scene.as_mut() else {
            return;
        };
        // scene.actors（不変）と scene.world（可変）は別フィールドのため同時借用できる
        let actors = &scene.actors;
        let world = &mut scene.world;

        // ── ① ジョブ収集（active_world_line のアクター木のみ）──
        let mut jobs: Vec<AttachJob> = Vec::new();
        for root in actors.iter().filter(|a| a.world_line == active_wl) {
            collect_attach_jobs(root, None, &mut jobs);
        }
        if jobs.is_empty() {
            return;
        }

        // ── ② モデルごとのノードワールド行列キャッシュ（フレーム内 1 回計算）──
        // キー = Model スロット entity。値 = (モデル参照, ノードワールド行列列, 描画オフセット行列)。
        // None = モデル未ロード（この Model へのアタッチは今フレームはスキップ）。
        let mut node_world_cache: std::collections::HashMap<
            Entity,
            Option<(std::sync::Arc<Model>, Vec<[[f32; 4]; 4]>, [[f32; 4]; 4])>,
        > = std::collections::HashMap::new();

        // ── ③ 各ジョブを解決して書き込む ──
        for job in jobs {
            // JointAttach パラメータ（ジョイント名・オフセット行列）を読み出す。
            // joint_name 空＝無効（追従しない）。
            let (joint_name, offset_mat) = match world.get::<JointAttachComponent>(job.attach_slot)
            {
                Some(ja) if !ja.joint_name.is_empty() => {
                    let off = Transform {
                        position: ja.offset_pos,
                        rotation: ja.offset_rot_deg,
                        scale: ja.offset_scale,
                    }
                    .to_mat4();
                    (ja.joint_name.clone(), off)
                }
                _ => continue,
            };

            // モデルのノードワールド行列をキャッシュから取得（無ければ計算して格納）。
            let entry = node_world_cache.entry(job.model_slot).or_insert_with(|| {
                world.get::<ModelComponent>(job.model_slot).and_then(|mc| {
                    mc.model.as_ref().map(|m| {
                        // 時刻源: anim_drive（Play の Animator 権威時刻）。無ければ静止＝バインドポーズ。
                        // クロスフェード中は描画（GPU スキニング）と同じ 2 クリップ混合で解決する。
                        let (anim_a, time_a, anim_b, time_b, weight) = match mc.anim_drive {
                            Some(d) => {
                                let (a_idx, a_t) = d.fade_from.unwrap_or((d.anim_idx, d.time));
                                let w = if d.fade_from.is_some() { d.weight } else { 1.0 };
                                (a_idx, a_t, d.anim_idx, d.time, w)
                            }
                            // 無効 anim_idx → local_matrix（バインドポーズ）
                            None => (usize::MAX, 0.0, usize::MAX, 0.0, 1.0),
                        };
                        (
                            m.clone(),
                            compute_node_world_matrices_blend(m, anim_a, time_a, anim_b, time_b, weight),
                            // 描画オフセット（描画インスタンス行列に必ず掛かる補正）を
                            // 一緒に取り込む。合成漏れは「見えているモデルからのズレ」になる。
                            mc.offset_matrix(),
                        )
                    })
                })
            });
            let Some((model, node_world, model_offset)) = entry.as_ref() else {
                continue;
            }; // モデル未ロード

            // joint_name 一致ノードのインデックスを探す。
            let node_idx = match model.nodes.iter().position(|n| n.name == joint_name) {
                Some(i) => i,
                None => {
                    // 一致ジョイント無し: 1 回だけ警告して無効（追従しない）。
                    let key = (job.attach_slot, joint_name.clone());
                    if warned.insert(key) {
                        eprintln!(
                            "[SEED jointattach] ジョイント '{}' がターゲットモデルに見つかりません（ノード名を確認）。追従を無効化します。",
                            joint_name
                        );
                    }
                    continue;
                }
            };
            let joint_world_local = node_world[node_idx];

            // スキンジョイントなら、描画が掛けるメッシュノードのバインドワールド行列も挟む。
            // 静的ノードへのアタッチ（スキン無し）では単位行列＝従来どおりの合成になる。
            let mesh_node_world = skinned_mesh_node_index(model, node_idx)
                .and_then(|i| node_world.get(i).copied())
                .unwrap_or_else(identity);

            // モデルアクターのワールド行列（instance_mats[0] と同じ actor Transform 由来）。
            let model_world = match world.get::<Transform>(job.model_actor) {
                Some(tf) => tf.to_mat4(),
                None => continue,
            };

            // 最終ワールド行列（描画と同一の空間合成。compose_attached_world のコメント参照）。
            let final_mat = compose_attached_world(
                &model_world,
                model_offset,
                &mesh_node_world,
                &joint_world_local,
                &offset_mat,
            );

            // 【採取タイミング】自アクターを上書きする直前に、まだ相対ローカルを持たない
            // 子孫について `local = inv(上書き前の追従ワールド) × 子孫ワールド` を採取する。
            // 上書き後に採取すると basis が今フレームの行列になり、1 フレーム分ずれる。
            let attached_old_world = actor_world_matrix(world, job.attached);

            // 自アクターの Transform を更新（インスペクタ・他システム用に TRS 分解して保持）。
            if let Some(tf) = world.get_mut::<Transform>(job.attached.entity) {
                *tf = Transform::from_mat4(&final_mat);
            }
            // 自アクターの全 Model スロットの instance_mats[0] を最終行列そのものへ同期する
            //（from_mat4→to_mat4 の丸めを避けるため行列を直接書き込む。registry の
            //  sync_model_instance_mats と同方針）。
            sync_attached_instance_mats(world, job.attached, &final_mat);
            // 子孫アクター（竿先マーカー等）を相対ローカルで絶対配置し直す（Play のみ）。
            if is_play {
                update_attached_descendants(
                    world,
                    job.attached,
                    &attached_old_world,
                    &final_mat,
                    child_locals,
                );
            }
        }
    }

    /// インスペクタからの JointAttachComponent フィールド更新（SET_JOINTATTACH_FIELD IPC）。
    ///
    /// key: joint_name / offset_pos / offset_rot / offset_scale。
    /// offset_* は "x,y,z"（3 成分）。不正な key・value は無視する（handle_set_light_field 同流儀）。
    pub(super) fn handle_set_jointattach_field(
        &mut self,
        actor_dfs_id: u32,
        slot_idx: u32,
        key: &str,
        value: &str,
    ) {
        use super::find_actor_by_dfs;

        let wl = self.active_world_line;
        // 対象スロットのエンティティを解決する。
        let slot_entity = {
            let Some(scene) = &self.scene else { return };
            let mut c = 0u32;
            find_actor_by_dfs(&scene.actors, wl, actor_dfs_id, &mut c)
                .and_then(|a| a.slots().get(slot_idx as usize))
                .filter(|s| s.kind == ComponentKind::JointAttach)
                .map(|s| s.entity)
        };
        let Some(entity) = slot_entity else { return };
        let Some(scene) = &mut self.scene else { return };
        let Some(ja) = scene.world.get_mut::<JointAttachComponent>(entity) else {
            return;
        };

        // key ごとに値を解釈して反映する（パース失敗は無視）。
        match key {
            "joint_name" => {
                ja.joint_name = value.to_string();
                // ジョイント名変更で過去の警告を有効化し直す（新名で再度 1 回警告できるように）。
                self.joint_attach_warned.retain(|(e, _)| *e != entity);
            }
            "offset_pos" => {
                if let Some(v) = parse_vec3(value) {
                    ja.offset_pos = v;
                }
            }
            "offset_rot" => {
                if let Some(v) = parse_vec3(value) {
                    ja.offset_rot_deg = v;
                }
            }
            "offset_scale" => {
                if let Some(v) = parse_vec3(value) {
                    ja.offset_scale = v;
                }
            }
            _ => return,
        }

        self.send_actor_components(actor_dfs_id, self.actor_virtual_selected_slot_idx);
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }
}

// ─── 子孫アクターの絶対配置 ─────────────────────────────────────

/// 旧ワールド行列が特異（逆行列が不定）とみなす行列式の閾値。
/// スケール 0 の姿勢では相対ローカルを採取できない（子を原点へ吸い込むため）。
const ATTACH_SINGULAR_DET_EPS: f32 = 1e-8;

/// 特異行列で相対ローカルの採取をスキップした旨を 1 回だけ警告するためのフラグ。
static ATTACH_SINGULAR_WARNED: std::sync::Once = std::sync::Once::new();

/// 行列の上 3x3 成分の行列式（＝逆行列が存在するかの判定に使う）。
fn mat4_upper3_det(m: &Mat4) -> f32 {
    m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1])
        - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
        + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0])
}

/// アクターの「現在の厳密なワールド行列」を取り出す。
///
/// Model スロットを持つなら `instance_mats[0]` を最優先する。JointAttach が書き込む
/// 最終行列にはせん断（非一様スケール × 回転オフセット）が含まれ得るが、Transform は
/// TRS 分解で保持するためせん断が失われる。instance_mats[0] は分解を経ていない
/// 「実際に描画に使われた行列」なので、基準にはこちらが正しい。
/// Model を持たないアクター（空アクタ・カメラ等）は Transform から復元する。
fn actor_world_matrix(world: &World, actor: &Actor) -> Mat4 {
    if let Some(m) = actor
        .slots()
        .iter()
        .find(|s| s.kind == ComponentKind::Model)
        .and_then(|s| world.get::<ModelComponent>(s.entity))
        .and_then(|mc| mc.instance_mats.first().copied())
    {
        return m;
    }
    world
        .get::<Transform>(actor.entity)
        .map(|tf| tf.to_mat4())
        .unwrap_or(crate::engine::core::transform_sync::MAT4_IDENTITY)
}

/// アクターのワールド行列を **子孫へ伝播せずに** 絶対値で書き込む。
///
/// 子孫はそれぞれ独自の相対ローカルを持ち、この後の再帰で個別に絶対配置されるため、
/// ここで差分伝播すると二重適用になる（それが今回直したバグそのもの）。
fn set_actor_world_matrix_no_propagate(world: &mut World, actor: &Actor, mat: &Mat4) {
    if let Some(tf) = world.get_mut::<Transform>(actor.entity) {
        *tf = Transform::from_mat4(mat);
    }
    sync_attached_instance_mats(world, actor, mat);
}

/// アクターが有効な JointAttach スロットを持つか。
///
/// 持つ場合、そのアクターは自分自身のジョブで配置されるため、親アタッチ側からは
/// 配置しない（配置すると同一フレーム内で二重に書き合うことになる）。
/// その子孫も当該アクターのジョブが面倒を見るので、部分木ごとスキップする。
fn has_enabled_joint_attach(actor: &Actor) -> bool {
    actor
        .slots()
        .iter()
        .any(|s| s.kind == ComponentKind::JointAttach && s.enabled)
}

/// 追従アクターの子孫を「追従アクター基準の相対ローカル行列」で絶対配置する（Play 専用）。
///
///   local       = inv(old_world) × child_world   … 子孫ごとに初回 1 回だけ採取
///   child_world = new_world × local              … 毎フレーム絶対値で書き直す
///
/// # 初回 Play フレームで採取した local が正しい理由
/// Play 開始直後のフレームでは、本更新より前にスクリプトが走ってプレイヤーを動かし、
/// transform_sync が **追従アクター（竿）と子孫（竿先）の両方へ同じ差分 D** を
/// すでに適用している。したがって採取値は
///     inv(D × A) × (D × C) = inv(A) × inv(D) × D × C = inv(A) × C
/// となり、D に依存しない「Edit で作った本来の相対関係」に一致する。
/// この D 非依存性こそが、差分伝播方式に無かった性質である（テスト
/// `local_offset_is_independent_of_external_delta` が回帰として守る）。
///
/// # 引数
/// - `old_world`: 今フレームのアタッチ行列で **上書きする前** の追従アクターのワールド行列。
///   採取の基準になるため、上書き後の行列を渡してはならない。
/// - `new_world`: 今フレーム算出したアタッチ行列（追従アクターの新しいワールド行列）。
/// - `locals`:    (追従アクター entity, 子孫 entity) → 相対ローカル行列のキャッシュ。
///   破棄されたアクターのエントリは残り得るが、配置はアクター木を辿って行うため
///   参照されず無害である（Play 終了・シーン遷移・スクリプト再読込で一括破棄する）。
fn update_attached_descendants(
    world: &mut World,
    attached: &Actor,
    old_world: &Mat4,
    new_world: &Mat4,
    locals: &mut HashMap<(Entity, Entity), Mat4>,
) {
    // 子がいなければ何もしない（毎フレーム全アタッチで通るため最初に弾く）。
    if attached.children().is_empty() {
        return;
    }

    // 採取用の逆行列。特異（スケール 0 等）なら採取できないので、
    // 今フレームは「既に採取済みの子孫の配置」だけを行う。
    let inv_old = if mat4_upper3_det(old_world).abs() < ATTACH_SINGULAR_DET_EPS {
        ATTACH_SINGULAR_WARNED.call_once(|| {
            eprintln!(
                "[SEED jointattach] 追従アクターのワールド行列が特異（スケール 0 等）のため、子孫の相対位置を採取できません。"
            );
        });
        None
    } else {
        Some(mat4x4_inv(*old_world))
    };

    place_descendants_recursive(world, attached.entity, attached, inv_old.as_ref(), new_world, locals);
}

/// `node` の子を DFS で辿り、相対ローカルの採取（未採取なら）と絶対配置を行う。
fn place_descendants_recursive(
    world: &mut World,
    attached_entity: Entity,
    node: &Actor,
    inv_old: Option<&Mat4>,
    new_world: &Mat4,
    locals: &mut HashMap<(Entity, Entity), Mat4>,
) {
    for child in node.children() {
        // 自前の JointAttach を持つ子は、その子自身のジョブが配置する（部分木ごとスキップ）。
        if has_enabled_joint_attach(child) {
            continue;
        }

        let key = (attached_entity, child.entity);
        // 未採取なら今フレームの「上書き前」基準で採取する（後から生成された子にも対応）。
        if !locals.contains_key(&key) {
            if let Some(inv) = inv_old {
                let child_world = actor_world_matrix(world, child);
                locals.insert(key, mat4x4_mul(*inv, child_world));
            }
        }

        // 採取済みなら絶対配置する（採取できなかった子は今フレーム動かさない）。
        if let Some(local) = locals.get(&key).copied() {
            let child_world = mat4x4_mul(*new_world, local);
            set_actor_world_matrix_no_propagate(world, child, &child_world);
        }

        // 孫以下も同じ追従アクター基準の相対ローカルを持つ（各自を絶対配置するので二重適用にならない）。
        place_descendants_recursive(world, attached_entity, child, inv_old, new_world, locals);
    }
}

/// 追従アクターの全 Model スロットの instance_mats[0] を `mat` へ同期し、バッチを dirty 化する。
///
/// registry::sync_model_instance_mats と同方針だが、TRS 分解を経ず最終ワールド行列を
/// そのまま書き込む（ジョイントの回転・スケールを厳密に反映するため）。
fn sync_attached_instance_mats(world: &mut World, actor: &Actor, mat: &[[f32; 4]; 4]) {
    let model_slots: Vec<Entity> = actor
        .slots()
        .iter()
        .filter(|s| s.kind == ComponentKind::Model)
        .map(|s| s.entity)
        .collect();
    for slot_entity in model_slots {
        if let Some(mc) = world.get_mut::<ModelComponent>(slot_entity) {
            if let Some(m) = mc.instance_mats.first_mut() {
                *m = *mat;
            }
            if let Some(batch) = mc.instanced_batch.as_mut() {
                batch.mark_dirty();
            }
        }
    }
}

/// "x,y,z"（3 成分・カンマ区切り）を [f32;3] にパースする。失敗時は None。
fn parse_vec3(s: &str) -> Option<[f32; 3]> {
    let parts: Vec<&str> = s.split(',').collect();
    if parts.len() != 3 {
        return None;
    }
    let x = parts[0].trim().parse::<f32>().ok()?;
    let y = parts[1].trim().parse::<f32>().ok()?;
    let z = parts[2].trim().parse::<f32>().ok()?;
    Some([x, y, z])
}

// ============================================================
//  テスト（空間合成の正しさ）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::loader::model::{ModelNode, Skin, SkinJoint};

    /// 位置比較の許容誤差（行列積で蓄積する f32 誤差を吸収する）。
    const EPS: f32 = 1e-4;

    /// TRS から行優先行列を作る（Transform の回転規約を正典として使う）。
    fn trs(position: [f32; 3], rotation_deg: [f32; 3], scale: [f32; 3]) -> [[f32; 4]; 4] {
        Transform { position, rotation: rotation_deg, scale }.to_mat4()
    }

    /// 行列の平行移動成分（＝合成結果のワールド位置）。
    fn translation_of(m: &[[f32; 4]; 4]) -> [f32; 3] {
        [m[0][3], m[1][3], m[2][3]]
    }

    /// 平行移動のみのノードを作る。
    fn node(
        name: &str,
        t: [f32; 3],
        children: Vec<usize>,
        mesh_index: Option<usize>,
        skin_index: Option<usize>,
    ) -> ModelNode {
        ModelNode {
            name: name.to_string(),
            local_matrix: trs(t, [0.0; 3], [1.0, 1.0, 1.0]),
            translation: t,
            rotation: [0.0, 0.0, 0.0, 1.0],
            scale: [1.0, 1.0, 1.0],
            mesh_index,
            skin_index,
            children,
            parent: None,
        }
    }

    /// Blender 由来のスキンモデル相当のテストモデルを作る。
    ///
    /// ノード 0 = アーマチュア（原点補正 -0.2）/ 1 = スキンメッシュノード（ローカル恒等）/
    /// 2 = ボーン（アーマチュア相対 +3.0）。ボーンは skin 0 のジョイント。
    fn skinned_model() -> Model {
        Model {
            name: "test".into(),
            nodes: vec![
                node("Armature", [0.0, -0.2, 0.0], vec![1, 2], None, None),
                node("Mesh", [0.0, 0.0, 0.0], vec![], Some(0), Some(0)),
                node("ForeArm.R", [0.0, 3.0, 0.0], vec![], None, None),
            ],
            root_nodes: vec![0],
            meshes: Vec::new(),
            materials: Vec::new(),
            textures: Vec::new(),
            animations: Vec::new(),
            skins: vec![Skin {
                name: "skin".into(),
                joints: vec![SkinJoint {
                    node_index: 2,
                    name: "ForeArm.R".into(),
                    inverse_bind_matrix: identity(),
                }],
                root_joint: Some(0),
            }],
        }
    }

    /// 描画オフセット（アクタローカルの拡縮）を含む合成が、描画と同じ位置になること。
    ///
    /// アクタ: 位置 (10,0,0)・スケール 2 / 描画オフセット: スケール 0.5 /
    /// メッシュノード: (0,-1,0) / ジョイント: (0,4,0) の場合、
    /// ワールド位置は 10 + 2 * 0.5 * (-1 + 4) = (10, 3, 0) になる。
    #[test]
    fn compose_applies_render_offset_and_mesh_node() {
        let actor = trs([10.0, 0.0, 0.0], [0.0; 3], [2.0, 2.0, 2.0]);
        let offset = trs([0.0; 3], [0.0; 3], [0.5, 0.5, 0.5]);
        let mesh_node = trs([0.0, -1.0, 0.0], [0.0; 3], [1.0, 1.0, 1.0]);
        let joint = trs([0.0, 4.0, 0.0], [0.0; 3], [1.0, 1.0, 1.0]);

        let m = compose_attached_world(&actor, &offset, &mesh_node, &joint, &identity());
        let p = translation_of(&m);
        assert!(
            (p[0] - 10.0).abs() < EPS && (p[1] - 3.0).abs() < EPS && p[2].abs() < EPS,
            "描画と同じ空間合成になっていない: {p:?}"
        );
    }

    /// 描画オフセットを掛け忘れると（＝修正前の合成）位置が大きくずれること。
    ///
    /// 同じ入力で描画オフセットを単位行列にすると y = 2 * 4 = 8 になり、
    /// 正しい y = 3 から 5 単位ずれる。これが「プレイヤーには追従するが
    /// ボーンから離れて浮く」症状の正体である。
    #[test]
    fn omitting_render_offset_displaces_socket() {
        let actor = trs([10.0, 0.0, 0.0], [0.0; 3], [2.0, 2.0, 2.0]);
        let offset = trs([0.0; 3], [0.0; 3], [0.5, 0.5, 0.5]);
        let mesh_node = trs([0.0, -1.0, 0.0], [0.0; 3], [1.0, 1.0, 1.0]);
        let joint = trs([0.0, 4.0, 0.0], [0.0; 3], [1.0, 1.0, 1.0]);

        let correct = translation_of(&compose_attached_world(
            &actor, &offset, &mesh_node, &joint, &identity(),
        ));
        let buggy = translation_of(&compose_attached_world(
            &actor, &identity(), &identity(), &joint, &identity(),
        ));
        assert!(
            (buggy[1] - 8.0).abs() < EPS,
            "オフセット未適用時の y は 8 になるはず: {buggy:?}"
        );
        assert!(
            (buggy[1] - correct[1]).abs() > 1.0,
            "修正前後で位置が変わらないなら再現になっていない: {buggy:?} vs {correct:?}"
        );
    }

    /// 180 度の描画オフセット回転が、ソケットの向き・位置へ正しく反映されること。
    ///
    /// アクタ回転 0・描画オフセット Y180 のとき、ジョイントの +X 方向オフセットは
    /// ワールドでは -X へ向く（見えているモデルの向きに一致する）。
    #[test]
    fn compose_applies_offset_rotation() {
        let actor = trs([0.0; 3], [0.0; 3], [1.0, 1.0, 1.0]);
        let offset = trs([0.0; 3], [0.0, 180.0, 0.0], [1.0, 1.0, 1.0]);
        let joint = trs([1.0, 0.0, 0.0], [0.0; 3], [1.0, 1.0, 1.0]);

        let p = translation_of(&compose_attached_world(
            &actor, &offset, &identity(), &joint, &identity(),
        ));
        assert!(
            (p[0] + 1.0).abs() < EPS && p[1].abs() < EPS && p[2].abs() < EPS,
            "Y180 の描画オフセットが反映されていない: {p:?}"
        );
    }

    /// アタッチオフセットがジョイントローカル基準（最後に右から掛かる）であること。
    #[test]
    fn attach_offset_is_joint_local() {
        let actor = trs([0.0; 3], [0.0, 90.0, 0.0], [1.0, 1.0, 1.0]);
        let joint = trs([0.0, 1.0, 0.0], [0.0; 3], [1.0, 1.0, 1.0]);
        let attach = trs([0.0, 0.0, 2.0], [0.0; 3], [1.0, 1.0, 1.0]);

        // アクタが Y+90 度回っているので、ジョイントローカル +Z はワールド +X になる。
        let p = translation_of(&compose_attached_world(
            &actor, &identity(), &identity(), &joint, &attach,
        ));
        assert!(
            (p[0] - 2.0).abs() < EPS && (p[1] - 1.0).abs() < EPS && p[2].abs() < EPS,
            "アタッチオフセットがジョイントローカルで効いていない: {p:?}"
        );
    }

    /// 【回帰テスト】相対ローカル方式は、採取と適用の間に外部から加わった移動 D に
    /// 依存しないこと（旧 delta 伝播方式が壊れていた本質）。
    ///
    /// シナリオ: 竿 A（非一様スケール × 回転 ⇒ せん断あり）と竿先 C。
    /// プレイヤー移動により transform_sync が A と C の両方へ同じ D を適用したあと、
    /// 相対ローカルを採取すると inv(D·A)·(D·C) = inv(A)·C となり D が消える。
    /// よって新しいアタッチ行列 A1 に対する配置は常に A1 · inv(A)·C になる。
    #[test]
    fn local_offset_is_independent_of_external_delta() {
        // 竿の旧ワールド（せん断を含む姿勢）
        let a0 = mat4x4_mul(
            trs([1.0, 2.0, 3.0], [0.0, 0.0, 0.0], [2.78, 5.56, 2.78]),
            trs([0.0, 0.0, 0.0], [25.0, 40.0, 15.0], [1.0, 1.0, 1.0]),
        );
        // 竿先の旧ワールド
        let c0 = mat4x4_mul(a0, trs([0.0, 4.5, 0.0], [10.0, 0.0, -5.0], [1.0, 1.0, 1.0]));
        // 今フレームのアタッチ行列（腕の姿勢が変わった）
        let a1 = mat4x4_mul(
            trs([4.0, 1.0, -2.0], [0.0, 0.0, 0.0], [2.78, 5.56, 2.78]),
            trs([0.0, 0.0, 0.0], [70.0, -30.0, 5.0], [1.0, 1.0, 1.0]),
        );

        // 外部からの移動（プレイヤーの移動＋回転）が A と C の両方へ適用された場合
        let d = trs([12.0, -3.0, 7.5], [0.0, 33.0, 0.0], [1.0, 1.0, 1.0]);
        let local_plain = mat4x4_mul(mat4x4_inv(a0), c0);
        let local_moved = mat4x4_mul(mat4x4_inv(mat4x4_mul(d, a0)), mat4x4_mul(d, c0));

        for r in 0..4 {
            for c in 0..4 {
                assert!(
                    (local_plain[r][c] - local_moved[r][c]).abs() < EPS,
                    "相対ローカルが外部移動 D に依存している: {local_plain:?} vs {local_moved:?}"
                );
            }
        }

        // 配置結果も D に依存しない
        let placed_plain = mat4x4_mul(a1, local_plain);
        let placed_moved = mat4x4_mul(a1, local_moved);
        for i in 0..3 {
            assert!(
                (translation_of(&placed_plain)[i] - translation_of(&placed_moved)[i]).abs() < EPS,
                "配置結果が外部移動に依存している"
            );
        }
    }

    /// 相対ローカル方式で、子孫が実際に `new_world × local` の位置へ絶対配置されること
    /// （＋同じ new_world で 2 回呼んでも結果が変わらない＝冪等であること）。
    ///
    /// さらに、1 回目と 2 回目の間に外部から子を動かしても（＝プレイヤー移動の
    /// 二重適用に相当）、2 回目の配置で正しい位置へ引き戻されることを確認する。
    #[test]
    fn descendant_is_placed_absolutely_and_is_idempotent() {
        let a0 = mat4x4_mul(
            trs([1.0, 2.0, 3.0], [0.0, 0.0, 0.0], [2.78, 5.56, 2.78]),
            trs([0.0, 0.0, 0.0], [25.0, 40.0, 15.0], [1.0, 1.0, 1.0]),
        );
        let a1 = mat4x4_mul(
            trs([4.0, 1.0, -2.0], [0.0, 0.0, 0.0], [2.78, 5.56, 2.78]),
            trs([0.0, 0.0, 0.0], [70.0, -30.0, 5.0], [1.0, 1.0, 1.0]),
        );

        // 竿（Transform = a0 相当）と竿先を組む
        let mut world = World::new();
        let parent_e = world.spawn();
        world.insert(parent_e, Transform::from_mat4(&a0));
        let mut parent = Actor::new(parent_e, "sao");

        let child_pos = [7.0, -1.5, 2.5];
        let child_e = world.spawn();
        world.insert(
            child_e,
            Transform { position: child_pos, rotation: [0.0; 3], scale: [1.0; 3] },
        );
        parent.children_mut().push(Actor::new(child_e, "RodTip"));

        // 期待値: local = inv(a0) × child_world、配置後 = a1 × local
        let child_world0 = Transform {
            position: child_pos,
            rotation: [0.0; 3],
            scale: [1.0; 3],
        }
        .to_mat4();
        let expected = translation_of(&mat4x4_mul(a1, mat4x4_mul(mat4x4_inv(a0), child_world0)));

        let mut locals: HashMap<(Entity, Entity), Mat4> = HashMap::new();
        update_attached_descendants(&mut world, &parent, &a0, &a1, &mut locals);

        let got = world.get::<Transform>(child_e).unwrap().position;
        for i in 0..3 {
            assert!(
                (got[i] - expected[i]).abs() < EPS,
                "子が new_world × local へ絶対配置されていない: {got:?} 期待 {expected:?}"
            );
        }

        // 外部から子だけを大きく動かしても（誤った二重適用の再現）、
        // 次の呼び出しで正しい位置へ戻ること＝絶対配置なので状態を持ち越さない。
        if let Some(tf) = world.get_mut::<Transform>(child_e) {
            tf.position = [100.0, -50.0, 30.0];
        }
        update_attached_descendants(&mut world, &parent, &a1, &a1, &mut locals);
        let got2 = world.get::<Transform>(child_e).unwrap().position;
        for i in 0..3 {
            assert!(
                (got2[i] - expected[i]).abs() < EPS,
                "冪等でない（外部移動が残っている）: {got2:?} 期待 {expected:?}"
            );
        }
    }

    /// 自前の JointAttach を持つ子は、親アタッチ側から配置されないこと
    /// （その子自身のジョブで配置されるため。二重書き込みの防止）。
    #[test]
    fn child_with_own_joint_attach_is_skipped() {
        use crate::engine::components::JointAttachComponent;
        use std::any::TypeId;

        let a0 = trs([0.0, 0.0, 0.0], [0.0; 3], [1.0, 1.0, 1.0]);
        let a1 = trs([10.0, 0.0, 0.0], [0.0; 3], [1.0, 1.0, 1.0]);

        let mut world = World::new();
        let parent_e = world.spawn();
        world.insert(parent_e, Transform::from_mat4(&a0));
        let mut parent = Actor::new(parent_e, "sao");

        let child_e = world.spawn();
        world.insert(
            child_e,
            Transform { position: [1.0, 0.0, 0.0], rotation: [0.0; 3], scale: [1.0; 3] },
        );
        let mut child = Actor::new(child_e, "OtherAttached");
        let slot_e = world.spawn();
        world.insert(slot_e, JointAttachComponent::default());
        child.add_slot(
            "JointAttach",
            ComponentKind::JointAttach,
            TypeId::of::<JointAttachComponent>(),
            slot_e,
        );
        parent.children_mut().push(child);

        let mut locals: HashMap<(Entity, Entity), Mat4> = HashMap::new();
        update_attached_descendants(&mut world, &parent, &a0, &a1, &mut locals);

        assert!(locals.is_empty(), "JointAttach 持ちの子は採取対象にならない");
        assert_eq!(
            world.get::<Transform>(child_e).unwrap().position,
            [1.0, 0.0, 0.0],
            "JointAttach 持ちの子は親アタッチから動かされない"
        );
    }

    /// スキンジョイントからは、そのスキンを使うメッシュノードが引けること。
    #[test]
    fn skinned_mesh_node_is_resolved_for_joint() {
        let model = skinned_model();
        assert_eq!(skinned_mesh_node_index(&model, 2), Some(1), "ボーン → メッシュノード");
        assert_eq!(skinned_mesh_node_index(&model, 0), None, "スキン外ノードは None");
    }

    /// スキンモデルの実姿勢が「アクタ × オフセット × メッシュノード × ジョイント」で求まること。
    ///
    /// 描画（gpu_resources::fill_chunk ＋ gbuffer_skinned_vertex.wgsl）は
    /// メッシュノードのバインドワールド行列をスキン行列へ左から掛けるため、
    /// アーマチュアの原点補正 (-0.2) はボーン姿勢に 2 回効く。
    #[test]
    fn skinned_joint_world_matches_render_chain() {
        use crate::engine::core::renderer::animator::compute_node_world_matrices;

        let model = skinned_model();
        let node_world = compute_node_world_matrices(&model, usize::MAX, 0.0);
        let mesh_node = node_world[skinned_mesh_node_index(&model, 2).unwrap()];
        let joint = node_world[2];

        // アクタ: スケール 4・描画オフセット: スケール 0.25 ⇒ 実効スケール 1。
        let actor = trs([0.0, 0.0, 0.0], [0.0; 3], [4.0, 4.0, 4.0]);
        let offset = trs([0.0; 3], [0.0; 3], [0.25, 0.25, 0.25]);
        let p = translation_of(&compose_attached_world(
            &actor, &offset, &mesh_node, &joint, &identity(),
        ));

        // メッシュノードワールド = -0.2、ジョイントワールド = -0.2 + 3.0 = 2.8。
        let expected_y = -0.2 + 2.8;
        assert!(
            (p[1] - expected_y).abs() < EPS,
            "描画の合成順と一致していない: {p:?} (期待 y={expected_y})"
        );
    }
}
