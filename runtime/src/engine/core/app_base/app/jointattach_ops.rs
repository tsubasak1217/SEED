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
//    3. `モデルアクタのワールド行列 × ジョイントのワールド行列 × オフセット行列`
//       を自アクターの Transform と Model instance_mats[0] へ書き込む。
//
//  【Edit / Play 両対応】
//  パーティクルの常時プレビューと同様、本更新は毎フレーム（モード非依存）で走る。
//  Edit では anim_drive が無いためモデルは静止し、アタッチもバインドポーズの
//  ジョイント位置へ吸着する。
// ============================================================

use crate::engine::components::{ComponentKind, JointAttachComponent, ModelComponent, Transform};
use crate::engine::core::loader::model::Model;
use crate::engine::core::renderer::animator::{compute_node_world_matrices, mat4_mul};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

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
        // self の別フィールドを個別に可変借用する（disjoint borrow）
        let warned = &mut self.joint_attach_warned;
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
        // キー = Model スロット entity。値 = (モデル参照, ノードワールド行列列)。
        // None = モデル未ロード（この Model へのアタッチは今フレームはスキップ）。
        let mut node_world_cache: std::collections::HashMap<
            Entity,
            Option<(std::sync::Arc<Model>, Vec<[[f32; 4]; 4]>)>,
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
                        let (anim_idx, time) = match mc.anim_drive {
                            Some(d) => (d.anim_idx, d.time),
                            None => (usize::MAX, 0.0), // 無効 anim_idx → local_matrix（バインドポーズ）
                        };
                        (m.clone(), compute_node_world_matrices(m, anim_idx, time))
                    })
                })
            });
            let Some((model, node_world)) = entry.as_ref() else {
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

            // モデルアクターのワールド行列（instance_mats[0] と同じ actor Transform 由来）。
            let model_world = match world.get::<Transform>(job.model_actor) {
                Some(tf) => tf.to_mat4(),
                None => continue,
            };

            // 最終ワールド行列 = モデルワールド × ジョイントワールド(モデル空間) × オフセット。
            let world_joint = mat4_mul(&model_world, &joint_world_local);
            let final_mat = mat4_mul(&world_joint, &offset_mat);

            // 自アクターの Transform を更新（インスペクタ・他システム用に TRS 分解して保持）。
            if let Some(tf) = world.get_mut::<Transform>(job.attached.entity) {
                *tf = Transform::from_mat4(&final_mat);
            }
            // 自アクターの全 Model スロットの instance_mats[0] を最終行列そのものへ同期する
            //（from_mat4→to_mat4 の丸めを避けるため行列を直接書き込む。registry の
            //  sync_model_instance_mats と同方針）。
            sync_attached_instance_mats(world, job.attached, &final_mat);
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
