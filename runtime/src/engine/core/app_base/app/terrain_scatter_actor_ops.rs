// ============================================================
//  terrain_scatter_actor_ops.rs — アクタ散布（kind=Actor プロップ）の統合層
//
//  【機能】
//  地形散布プロップに kind=Actor（プレハブ参照）を指定すると、散布点に
//  GPU インスタンスではなく**シーンの実アクタ**（コライダー付きモデル・
//  アイテム等）を生成する。
//
//  【設計上の割り切り（第 1 段）】
//  - 散布点は .tscatter に**保存しない**。生成されたアクタが .scene に永続化
//    される（二重生成を構造的に防ぐ）。ルール散布・ブラシ散布の生成直後に
//    kind=Actor のインスタンスを横取りして、アクタ生成へ回す。
//  - 生成アクタは専用グループフォルダ「散布アクタ」配下へ入れる
//    （Hierarchy の汚染防止）。フォルダは scatter_prop_id にマーカー値を持ち、
//    生成アクタは生成元プロップ ID を持つ（再散布時の置き換え対象の特定用）。
//  - ルール散布 = そのプロップの既存生成アクタを**全消しして敷き直す**
//    （草・モデルの「ルールで敷き直す」意味論と同じ）。Undo 可能。
//  - ブラシ散布 = **追加**。消去ブラシは半径内の全散布アクタを削除する
//    （草・モデルの「半径内の全プロップを消す」意味論と同じ）。
//    ブラシは 1 ストロークで何十回も飛んでくるため Undo 記録は行わない
//    （草・モデル散布ブラシが Undo 対象外であることと整合させる）。
//  - 地形編集後の再接地（restick）は行わない。地形を変えたら再散布で追従する。
//  - 上限 SCATTER_ACTOR_MAX_PER_PROP を超えたぶんは生成せず、切り捨て件数を
//    警告ログに出す（黙って切らない）。
//
//  【プレハブとの関係】
//  生成アクタは prefab_source を持つ通常のプレハブインスタンスになる。
//  したがってプレハブ本体を編集して「全プレハブ更新」を掛ければ、
//  散布済みアクタにも内容が反映される（既存機構への相乗り）。
// ============================================================

use std::collections::BTreeMap;

use crate::engine::components::Transform;
use crate::engine::core::app_base::scene::build_actor;
use crate::engine::core::app_base::undo::ActorTreeSnapshotCommand;
use crate::engine::core::transform_sync::set_actor_world_transform;
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;
use crate::engine::terrain::scatter::{PropKind, ScatterInstance, TerrainPropSet};

use super::prefab_ops::load_actor_data;
use super::{despawn_actor_recursive, App};

// ============================================================
//  定数（マジックナンバー禁止）
// ============================================================

/// 散布アクタを収める専用グループフォルダの表示名。
const SCATTER_ACTOR_GROUP_NAME: &str = "散布アクタ";

/// グループフォルダ自身を識別するための scatter_prop_id マーカー値。
/// プロップ ID には使われない前提の予約値（先頭 "__" で衝突を避ける）。
const SCATTER_ACTOR_GROUP_MARKER: &str = "__scatter_group__";

/// 1 プロップあたりの散布アクタ数の上限。
/// アクタは ECS エンティティ・物理コライダー・ヒエラルキー行を伴い、さらに現状は
/// アクタごとにモデルの GPU バッファが複製される（GpuModel の共有キャッシュが無い）
/// ため、VRAM を考慮して控えめに抑える。共有キャッシュ導入後に引き上げを検討する。
const SCATTER_ACTOR_MAX_PER_PROP: usize = 512;

impl App {
    /// 生成済みインスタンス列から kind=Actor プロップのぶんを抜き取り、
    /// プロップ添字ごとにまとめて返す。残った列（草・モデル）は呼び出し元が
    /// 従来どおり .tscatter / GPU 描画へ回す。
    ///
    /// `rescatter_indices` に含まれる kind=Actor プロップは、抜き取り結果が
    /// 0 件でも空エントリとして返す（ルール散布の「敷き直し」で、ルールが
    /// 何も撒かない設定でも既存生成アクタの全消しだけは走らせるため）。
    /// ブラシ経路など敷き直しが不要な呼び出しでは空スライスを渡す。
    pub(super) fn extract_actor_scatter_instances(
        &self,
        lists: &mut [&mut Vec<ScatterInstance>],
        rescatter_indices: &[usize],
    ) -> Vec<(usize, Vec<ScatterInstance>)> {
        partition_actor_instances(&self.terrain.props, lists, rescatter_indices)
    }

    /// kind=Actor プロップの散布インスタンスをシーンの実アクタとして生成する。
    ///
    /// - `replace = true`（ルール散布）: そのプロップの既存生成アクタを全消しして
    ///   から生成し、前後スナップショットで Undo 記録する。
    /// - `replace = false`（ブラシ散布）: 既存を残して追加のみ。Undo 記録しない
    ///   （ブラシは高頻度で飛んでくるため。草・モデル散布ブラシと同じ扱い）。
    ///
    /// 戻り値は実際に生成したアクタ数。
    pub(super) fn apply_actor_scatter(
        &mut self,
        per_prop: Vec<(usize, Vec<ScatterInstance>)>,
        replace: bool,
    ) -> usize {
        if per_prop.is_empty() || self.draw_ctx.is_none() {
            return 0;
        }

        // プロップ情報（ID・プレハブパス）を添字から引いて 1 パスで束ねる
        let jobs: Vec<(String, Option<String>, Vec<ScatterInstance>)> = per_prop
            .into_iter()
            .map(|(idx, insts)| {
                let p = &self.terrain.props.props[idx];
                (p.id.clone(), p.prefab_path.clone(), insts)
            })
            .collect();

        // ルール散布のみ Undo 記録する（前スナップショット）
        let wl = self.active_world_line;
        let before_actors = replace.then(|| self.snapshot_actors_for_wl(wl));

        let host = self.scripting_host.clone();
        let Some(mut scene) = self.scene.take() else { return 0 };
        let mut spawned_total = 0usize;
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            let group_idx = ensure_scatter_group(&mut scene, wl);

            for (prop_id, prefab_path, instances) in jobs {
                // ── プレハブパス未設定は生成できない ──
                let Some(prefab_path) = prefab_path.filter(|p| !p.is_empty()) else {
                    let msg = format!(
                        "TERRAIN_SCATTER_ERROR:prop '{prop_id}' はプレハブ未設定のため散布できません"
                    );
                    if let Some(ipc) = &self.ipc { ipc.send(&msg); }
                    continue;
                };

                // ── プレハブを読む（キャッシュ経由。ブラシ 1 ストロークで
                //    何十回も呼ばれるため、同じファイルの再読込・再パースを避ける）──
                let data = if let Some(d) = self.scatter_prefab_cache.get(&prefab_path) {
                    d.clone()
                } else {
                    match load_actor_data(&prefab_path) {
                        Ok(d) => {
                            self.scatter_prefab_cache.insert(prefab_path.clone(), d.clone());
                            d
                        }
                        Err(e) => {
                            let msg = format!(
                                "TERRAIN_SCATTER_ERROR:プレハブ読み込み失敗 '{prefab_path}' ({e})"
                            );
                            eprintln!("[ScatterActor] {msg}");
                            if let Some(ipc) = &self.ipc { ipc.send(&msg); }
                            continue;
                        }
                    }
                };

                // ── ルール散布: このプロップの既存生成アクタを全消しする ──
                if replace {
                    remove_scatter_children_where(
                        &mut scene.actors[group_idx],
                        &mut scene.world,
                        |a| a.scatter_prop_id.as_deref() == Some(prop_id.as_str()),
                    );
                }

                // ── 上限判定（既存 + 新規）。超過ぶんは黙って切らずログへ ──
                let group = &scene.actors[group_idx];
                let existing = group
                    .children()
                    .iter()
                    .filter(|a| a.scatter_prop_id.as_deref() == Some(prop_id.as_str()))
                    .count();
                let capacity = SCATTER_ACTOR_MAX_PER_PROP.saturating_sub(existing);
                if instances.len() > capacity {
                    eprintln!(
                        "[ScatterActor] prop '{prop_id}': 上限 {SCATTER_ACTOR_MAX_PER_PROP} 件を \
                         超えるため {} 件を生成しません（生成 {} / 既存 {existing}）",
                        instances.len() - capacity,
                        capacity,
                    );
                }

                // ── 散布点ごとにプレハブからアクタを構築する ──
                for (n, inst) in instances.into_iter().take(capacity).enumerate() {
                    let mut new_actor =
                        match build_actor(data.clone(), ctx, &mut scene.world, host.as_ref(), None) {
                            Ok(a) => a,
                            Err(e) => {
                                eprintln!(
                                    "[ScatterActor] プレハブ構築失敗 '{prefab_path}' err={e}"
                                );
                                // 同じデータで繰り返し失敗するため、このプロップは打ち切る
                                break;
                            }
                        };

                    new_actor.name = format!("{prop_id}_{:04}", existing + n);
                    new_actor.set_world_line_recursive(wl);
                    new_actor.prefab_source   = Some(prefab_path.clone());
                    new_actor.scatter_prop_id = Some(prop_id.clone());

                    // ── ルート Transform を散布点へ移す ──
                    // プレハブファイルは「ルート位置基準」でメッシュ行列・子 Transform を
                    // ワールド空間で持つため、ルートだけ書き換えても見た目が動かない。
                    // 差分行列のサブツリー適用（特異スケールのガード込み）は
                    // transform_sync の集約関数に任せる。Undo 記録は不要なので
                    // child_dfs_start は 0、戻り値の変更記録は捨てる。
                    let mut target = scene
                        .world
                        .get::<Transform>(new_actor.entity)
                        .cloned()
                        .unwrap_or_default();
                    target.position = inst.pos;
                    // 散布インスタンスの yaw（ラジアン）を YXZ オイラーの Y（度）へ加算
                    target.rotation[1] += inst.yaw.to_degrees();
                    // スケール倍率はプレハブ自身のスケールへ乗算する
                    for s in &mut target.scale { *s *= inst.scale; }
                    let _ = set_actor_world_transform(&new_actor, &mut scene.world, target, 0);

                    scene.actors[group_idx].children_mut().push(new_actor);
                    spawned_total += 1;
                }
            }
        }
        self.scene = Some(scene);

        // ── Undo 記録（ルール散布のみ）とエディタ通知 ──
        if let Some(before_actors) = before_actors {
            let after_actors = self.snapshot_actors_for_wl(wl);
            self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
                world_line: wl,
                before_actors,
                after_actors,
            }));
        }
        self.send_hierarchy();
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }

        spawned_total
    }

    /// 消去ブラシ: 半径内の散布アクタ（全プロップ）を削除する。
    ///
    /// 草・モデル散布の消去ブラシが「半径内の全プロップを消す」意味論なので、
    /// 散布アクタも同じ意味論で消す。1 件でも消したら true。
    /// ブラシ経路のため Undo 記録は行わない（生成側と同じ割り切り）。
    pub(super) fn erase_scatter_actors_in_radius(
        &mut self,
        center: [f32; 3],
        radius: f32,
    ) -> bool {
        let wl = self.active_world_line;
        let Some(scene) = self.scene.as_mut() else { return false };
        let Some(group_idx) = find_scatter_group(&scene.actors, wl) else { return false };

        // 判定（World の不変借用）と削除（World の可変借用）を分けるため、
        // 先に「半径内にいる散布アクタ」の entity 集合を作ってから削除する。
        // グループ直下は全て散布生成アクタ（マーカー保持）だが、ユーザーが手動で
        // 移入したアクタを巻き込まないようマーカーの有無も確認する。
        let r2 = radius * radius;
        let hits: std::collections::HashSet<_> = scene.actors[group_idx]
            .children()
            .iter()
            .filter(|a| a.scatter_prop_id.is_some())
            .filter(|a| {
                scene.world.get::<Transform>(a.entity).is_some_and(|t| {
                    let d = [
                        t.position[0] - center[0],
                        t.position[1] - center[1],
                        t.position[2] - center[2],
                    ];
                    d[0] * d[0] + d[1] * d[1] + d[2] * d[2] <= r2
                })
            })
            .map(|a| a.entity)
            .collect();
        if hits.is_empty() {
            return false;
        }

        let removed_any = remove_scatter_children_where(
            &mut scene.actors[group_idx],
            &mut scene.world,
            |a| hits.contains(&a.entity),
        );

        if removed_any {
            self.send_hierarchy();
            if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
        }
        removed_any
    }
}

// ============================================================
//  グループフォルダと子削除のヘルパ（述語・規約を 1 箇所に集約）
// ============================================================

/// 散布グループフォルダ（世界線 wl・マーカー付き）のルート添字を探す。
fn find_scatter_group(actors: &[Actor], wl: u32) -> Option<usize> {
    actors.iter().position(|a| {
        a.world_line == wl
            && a.scatter_prop_id.as_deref() == Some(SCATTER_ACTOR_GROUP_MARKER)
    })
}

/// 散布グループフォルダを探し、無ければシーンルートへ作ってその添字を返す。
fn ensure_scatter_group(scene: &mut crate::engine::core::app_base::scene::Scene, wl: u32) -> usize {
    if let Some(idx) = find_scatter_group(&scene.actors, wl) {
        return idx;
    }
    let entity = scene.world.spawn();
    let mut folder = Actor::new_folder(entity, SCATTER_ACTOR_GROUP_NAME);
    folder.world_line = wl;
    folder.scatter_prop_id = Some(SCATTER_ACTOR_GROUP_MARKER.to_string());
    scene.actors.push(folder);
    scene.actors.len() - 1
}

/// グループ直下の子のうち述語が真のものを despawn して取り除く。
/// 1 件でも取り除いたら true。
///
/// `Vec::remove` の逐次シフト（O(n²)）を避けるため、1 パスの振り分けで実装する。
fn remove_scatter_children_where(
    group: &mut Actor,
    world: &mut World,
    pred: impl Fn(&Actor) -> bool,
) -> bool {
    let children = group.children_mut();
    let before = children.len();
    let mut kept = Vec::with_capacity(before);
    for child in children.drain(..) {
        if pred(&child) {
            despawn_actor_recursive(&child, world);
        } else {
            kept.push(child);
        }
    }
    *children = kept;
    children.len() != before
}

// ============================================================
//  インスタンスの分配（純粋関数。App 非依存でテスト可能）
// ============================================================

/// 生成済みインスタンス列から kind=Actor プロップのぶんを抜き取り、
/// プロップ添字ごとにまとめて返す。抜かれた側の各 `lists` 要素には
/// 草・モデルのインスタンスだけが残る。
///
/// `rescatter_indices` に含まれる kind=Actor プロップは 0 件でも空エントリで返す
/// （「敷き直し」の全消しだけを走らせるため）。それ以外の空エントリは返さない。
fn partition_actor_instances(
    props: &TerrainPropSet,
    lists: &mut [&mut Vec<ScatterInstance>],
    rescatter_indices: &[usize],
) -> Vec<(usize, Vec<ScatterInstance>)> {
    // プロップ添字 → kind=Actor かどうかの判定表
    let is_actor: Vec<bool> = props
        .props
        .iter()
        .map(|p| p.kind == PropKind::Actor)
        .collect();
    if !is_actor.iter().any(|&b| b) {
        return Vec::new();
    }

    // 敷き直し対象の kind=Actor プロップは空バケツを先に用意しておく
    // （BTreeMap なので結果は常に添字昇順）。
    let mut buckets: BTreeMap<usize, Vec<ScatterInstance>> = rescatter_indices
        .iter()
        .copied()
        .filter(|&i| is_actor.get(i).copied().unwrap_or(false))
        .map(|i| (i, Vec::new()))
        .collect();

    for list in lists.iter_mut() {
        let mut kept = Vec::with_capacity(list.len());
        for inst in list.drain(..) {
            let idx = inst.prop_id as usize;
            if is_actor.get(idx).copied().unwrap_or(false) {
                buckets.entry(idx).or_default().push(inst);
            } else {
                kept.push(inst);
            }
        }
        **list = kept;
    }

    buckets.into_iter().collect()
}

// ============================================================
//  テスト
//
//  アクタ生成そのもの（apply_actor_scatter）は DrawContext / GPU を要するため
//  単体テストでは扱えない（実機確認手順は docs/terrain.md §15.13 参照）。
//  ここでは App 非依存の分配ロジックを検証する。
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::terrain::scatter::TerrainProp;

    /// grass / model / actor が混在するプロップ集合を作る。
    fn props_with_actor() -> TerrainPropSet {
        TerrainPropSet {
            props: vec![
                TerrainProp { id: "g".into(), kind: PropKind::Grass, ..TerrainProp::default() },
                TerrainProp {
                    id: "item".into(),
                    kind: PropKind::Actor,
                    prefab_path: Some("assets://actors/item.actor".into()),
                    ..TerrainProp::default()
                },
                TerrainProp { id: "m".into(), kind: PropKind::Model, ..TerrainProp::default() },
            ],
        }
    }

    /// prop_id 指定でダミーインスタンスを作る。
    fn inst(prop_id: u32) -> ScatterInstance {
        ScatterInstance {
            pos: [0.0; 3], normal: [0.0, 1.0, 0.0],
            yaw: 0.0, scale: 1.0, prop_id, seed: 0,
        }
    }

    /// kind=Actor のインスタンスだけが抜き取られ、草・モデルは残ること。
    #[test]
    fn partition_extracts_only_actor_kind() {
        let props = props_with_actor();
        let mut list = vec![inst(0), inst(1), inst(2), inst(1)];
        let mut lists: Vec<&mut Vec<ScatterInstance>> = vec![&mut list];

        let per_prop = partition_actor_instances(&props, &mut lists, &[]);

        assert_eq!(per_prop.len(), 1, "actor プロップは 1 種");
        assert_eq!(per_prop[0].0, 1, "抜かれたのは添字 1（item）");
        assert_eq!(per_prop[0].1.len(), 2, "actor インスタンスは 2 件");
        assert_eq!(list.len(), 2, "草・モデルの 2 件は残る");
        assert!(list.iter().all(|i| i.prop_id != 1), "残りに actor が混ざらない");
    }

    /// actor プロップが無い場合は何も抜かれないこと（早期リターン経路）。
    #[test]
    fn partition_noop_without_actor_props() {
        let props = TerrainPropSet {
            props: vec![TerrainProp { id: "g".into(), ..TerrainProp::default() }],
        };
        let mut list = vec![inst(0), inst(0)];
        let mut lists: Vec<&mut Vec<ScatterInstance>> = vec![&mut list];

        let per_prop = partition_actor_instances(&props, &mut lists, &[]);

        assert!(per_prop.is_empty());
        assert_eq!(list.len(), 2, "リストは無傷であること");
    }

    /// 複数リスト（チャンクごとの生成結果）を跨いで抜き取れること。
    #[test]
    fn partition_spans_multiple_lists() {
        let props = props_with_actor();
        let mut a = vec![inst(1), inst(0)];
        let mut b = vec![inst(2), inst(1), inst(1)];
        let mut lists: Vec<&mut Vec<ScatterInstance>> = vec![&mut a, &mut b];

        let per_prop = partition_actor_instances(&props, &mut lists, &[]);

        assert_eq!(per_prop[0].1.len(), 3, "両リスト合計 3 件が抜かれる");
        assert_eq!(a.len(), 1);
        assert_eq!(b.len(), 1);
    }

    /// 敷き直し対象（rescatter_indices）の actor プロップは、生成 0 件でも
    /// 空エントリで返り、actor 以外の添字は無視されること。
    #[test]
    fn partition_keeps_empty_entry_for_rescatter_targets() {
        let props = props_with_actor();
        let mut list: Vec<ScatterInstance> = Vec::new();
        let mut lists: Vec<&mut Vec<ScatterInstance>> = vec![&mut list];

        // 添字 1 = actor（空でも返る）、添字 0 = grass（無視される）
        let per_prop = partition_actor_instances(&props, &mut lists, &[0, 1]);

        assert_eq!(per_prop.len(), 1);
        assert_eq!(per_prop[0].0, 1, "actor 添字だけが敷き直し対象になる");
        assert!(per_prop[0].1.is_empty(), "生成 0 件でも空エントリで返る");
    }
}
