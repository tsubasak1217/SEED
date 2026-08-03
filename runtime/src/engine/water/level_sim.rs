// ============================================================
//  water/level_sim.rs — 水位グラフの ECS 結線（Phase W2.5）
//
//  正典: docs/water_interaction_roadmap.md §1.2「連通ボリューム方式（水位グラフ）」。
//
//  ## 役割（単一責任）
//  シーン（Actor ツリー＋World）から水位グラフを組み立て、
//  固定タイムステップで `level_graph::step_level_graph` を回し、
//  結果の水位を `WaterVolumeComponent.sim_level_y`（揮発フィールド）へ書き戻す。
//  **数値計算は一切書かない**（すべて `level_graph` にある）。
//
//  ## 水位はゲーム状態である（Play 中だけ動く）
//  水位は「扉を開けたら流れ込む」というゲームプレイの結果であり、見た目の演出ではない。
//  したがって波（`ambient_time` で Edit 中も動く）とは扱いが異なり、
//  **Play かつ非ポーズのときだけ時間発展する**。Play を止めた瞬間に
//  `sim_level_y` を全消去し、インスペクタで設定した `surface_height` の静止水面へ戻す
//  （＝Edit 中に見えている水面が、シーンに保存されている値と常に一致する）。
//
//  ## ノードになれるのは kind = Region だけ
//  水位グラフのノードは「底面積を持つ有限の水塊」でなければならない。
//    ・Ocean  … XZ 無限。底面積が定義できない（水位を動かすと世界中の海面が動く）
//    ・Spline … 川。水面 Y が地点ごとに違うので「水位スカラー 1 個」で表せない
//  よって Region のみを対象とし、その中でも `simulate_level = true` の水域だけを扱う
//  （既定 false ＝ **既存シーンの挙動は 1 ビットも変わらない**）。
//
//  ## 底面積・底・天井の求め方
//  Region は「アクタ位置を中心とする軸平行 AABB」なので、
//    底面積 = (2·halfX) × (2·halfZ)     … 水位 1m ぶんの体積
//    底 Y   = 中心Y − halfY
//    天井 Y = 中心Y + halfY             … これを超えた水は溢れて捨てられる
//  で一意に決まる。初期水位は `surface_height`（アクタ原点からの相対）を
//  ワールド Y へ直したもの＝Edit 中に見えている水面そのものである。
//
//  ## 演出接続（流量 → 波源）
//  substep で得たリンクごとの流量のうち、閾値を超えたものを
//  `WaterFlowEvent`（ワールド位置＋流量）として公開する。
//  呼び出し側（frame_renderer）がこれをインタラクションフィールドの波源へ変換する。
//  本モジュールは wgpu もインタラクションフィールドも知らない。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::water_link_component::WaterLinkComponent;
use crate::engine::components::water_volume_component::{
    WaterVolumeComponent, WaterVolumeKind,
};
use crate::engine::components::{ComponentKind, Transform};
use crate::engine::core::clock::FIXED_DELTA;
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::level_graph::{step_level_graph, LevelLink, LevelNode};

// ─── 調整定数（マジックナンバー禁止）─────────────────────────

/// 1 フレームで実行する substep の上限。
///
/// アキュムレータ方式は「1 フレームが極端に長い」とき（シーンロード直後・
/// ブレークポイント復帰後など）に substep を大量に積み、さらにフレームが伸びて
/// 積み増す“死のスパイラル”に入る。上限を切って余剰を捨てることで、
/// 最悪でも「水位の進みが遅れる」だけで済ませる（描画は止まらない）。
/// 8 ステップ ＝ 約 133ms ぶんで、7.5fps まで落ちても実時間に追随できる。
const MAX_SUBSTEPS_PER_FRAME: u32 = 8;

/// 波源を発生させる流量のしきい値（m³/s）。
///
/// これ未満の流量は「じわじわ染みている」程度なので水面を波立たせない。
/// 0.05m³/s ＝ 毎秒 50 リットルで、家庭の蛇口全開（約 0.2L/s）の 250 倍。
/// 扉ごしに部屋が浸水するような流れだけが拾われる。
pub const FLOW_WAVE_THRESHOLD: f32 = 0.05;

// ─── WaterFlowEvent ──────────────────────────────────────────

/// 「このリンクにこれだけの水が流れている」という 1 件の報告（演出接続用）。
///
/// 生成するのは本モジュール、消費するのは frame_renderer（波源への変換）。
/// パーティクル・音を足すときも同じイベントを読めばよい。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct WaterFlowEvent {
    /// 開口のワールド座標（リンクアクタ位置 ＋ 開口下端の相対 Y）。
    pub world_pos: [f32; 3],
    /// 流量の大きさ（m³/s。**常に非負**。向きは演出に不要なので落としてある）。
    pub flow: f32,
}

// ─── WaterLevelSim ───────────────────────────────────────────

/// 水位グラフの時間発展を担う状態（App が 1 個だけ保持する）。
///
/// フレーム間で持ち越すのは「固定ステップのアキュムレータ」と
/// 「前フレームが Play 中だったか（停止の検出用）」と
/// 「直近フレームの流量イベント（演出接続用）」だけである。
/// 水位そのものは `WaterVolumeComponent.sim_level_y` に置く
/// （ECS 理念：状態はコンポーネントにあり、システムは状態を持たない）。
#[derive(Default)]
pub struct WaterLevelSim {
    /// 固定タイムステップのアキュムレータ（秒）。
    accumulator: f32,
    /// 前フレームでシミュレーションが走っていたか（Play → Edit の遷移検出）。
    was_running: bool,
    /// 直近フレームで観測された流量イベント（毎フレーム作り直す）。
    flow_events: Vec<WaterFlowEvent>,
}

impl WaterLevelSim {
    /// 直近フレームの流量イベント（演出接続用）。Play 中でなければ空。
    pub fn flow_events(&self) -> &[WaterFlowEvent] { &self.flow_events }

    /// 水位グラフを 1 フレームぶん進める（Play 中のみ）。
    ///
    /// - `actors` / `world`: シーン本体。`world` はコンポーネントへの書き戻しで可変借用する。
    /// - `world_line`: アクター編集タブとの分離用（0 = 通常シーン）。
    /// - `delta_time`: フレームの経過時間（秒）。substep アキュムレータへ積む。
    /// - `running`: **Play かつ非ポーズ**なら true。false ならこのフレームは時間を進めない。
    ///
    /// `running` が true → false へ落ちた最初のフレームで、全ボリュームの
    /// `sim_level_y` を消去して設定水位へ戻す（Play 停止で水位がリセットされる）。
    pub fn update(
        &mut self,
        actors:     &[Actor],
        world:      &mut World,
        world_line: u32,
        delta_time: f32,
        running:    bool,
    ) {
        // 流量イベントは毎フレーム作り直す（前フレームの波源が残り続けないように）。
        self.flow_events.clear();

        if !running {
            // Play を抜けた最初のフレームだけ後始末をする。
            // 毎フレーム走査しないのは、Edit 中の常時コストを 0 にするため。
            if self.was_running {
                clear_simulated_levels(actors, world, world_line);
                self.accumulator = 0.0;
                self.was_running = false;
            }
            return;
        }
        self.was_running = true;

        // ── ① シーンから水位グラフを組む ─────────────────────
        let Some(mut graph) = build_graph(actors, world, world_line) else {
            // 対象ボリュームが 1 つも無ければ何もしない（アキュムレータも積まない）。
            self.accumulator = 0.0;
            return;
        };

        // ── ② 固定タイムステップで進める ────────────────────
        // 物理と同じ 1/60 固定ステップ。フレームレートが変わっても
        // 水位の進み方が変わらない（＝可変 dt で挙動がぶれない）。
        self.accumulator += delta_time.max(0.0);
        let mut steps = 0u32;
        // 最後の substep の流量だけを演出へ渡す（複数 substep ぶんを重ねると
        // 同じ波源が 1 フレームで何度も焼かれてしまうため）。
        let mut last_flows = Vec::new();
        while self.accumulator >= FIXED_DELTA && steps < MAX_SUBSTEPS_PER_FRAME {
            self.accumulator -= FIXED_DELTA;
            last_flows = step_level_graph(&mut graph.nodes, &graph.links, FIXED_DELTA);
            steps += 1;
        }
        // 上限に達したぶんの余剰は捨てる（死のスパイラル防止。冒頭の定数コメント参照）。
        if steps >= MAX_SUBSTEPS_PER_FRAME { self.accumulator = 0.0; }

        // ── ③ 水位をコンポーネントへ書き戻す ──────────────────
        for (i, entity) in graph.node_entities.iter().enumerate() {
            if let Some(w) = world.get_mut::<WaterVolumeComponent>(*entity) {
                w.sim_level_y = Some(graph.nodes[i].level_y);
            }
        }

        // ── ④ 流量イベントを演出用に公開する ─────────────────
        for flow in &last_flows {
            let magnitude = flow.volume_per_sec.abs();
            if magnitude < FLOW_WAVE_THRESHOLD { continue; }
            self.flow_events.push(WaterFlowEvent {
                world_pos: graph.link_positions[flow.link],
                flow:      magnitude,
            });
        }
    }
}

// ─── グラフ構築 ──────────────────────────────────────────────

/// シーンから組み立てた水位グラフ（1 フレーム限りの作業データ）。
struct SceneLevelGraph {
    /// 計算対象のノード列（`level_graph` へ渡す）。
    nodes: Vec<LevelNode>,
    /// ノードに対応する WaterVolumeComponent のスロットエンティティ（書き戻し用）。
    node_entities: Vec<Entity>,
    /// 計算対象のリンク列（`level_graph` へ渡す）。
    links: Vec<LevelLink>,
    /// リンクに対応する開口のワールド座標（演出接続用。`links` と同じ添字）。
    link_positions: Vec<[f32; 3]>,
}

/// Actor ツリーを走査して水位グラフを組む。対象ボリュームが 0 個なら None。
///
/// ノード（Region かつ `simulate_level = true`）を先に集め、
/// 「アクタ名 → ノード添字」の索引を作ってからリンクを解決する。
/// 名前が解決できないリンクは**捨てずに範囲外添字のまま**渡してもよいが、
/// ここで捨てておく方が `level_graph` の入力が素直になるので捨てている。
fn build_graph(actors: &[Actor], world: &World, world_line: u32) -> Option<SceneLevelGraph> {
    let mut nodes: Vec<LevelNode> = Vec::new();
    let mut node_entities: Vec<Entity> = Vec::new();
    // アクタ名 → ノード添字。**同名アクタは最初に見つかったものを採る**
    //（`collect::find_actor_by_name` と同じ決定的規則）。
    let mut index_by_name: HashMap<String, usize> = HashMap::new();
    // リンクは「ノードが全部揃ってから」解決したいので、走査中は素材だけ溜める。
    let mut raw_links: Vec<(WaterLinkComponent, [f32; 3])> = Vec::new();

    for root in actors.iter().filter(|a| a.world_line == world_line) {
        visit_actor(root, world, true, &mut |actor, actor_pos| {
            for slot in actor.slots() {
                if !slot.enabled { continue; }
                match slot.kind {
                    // ── 水ボリューム → ノード候補 ──
                    ComponentKind::WaterVolume => {
                        let Some(w) = world.get::<WaterVolumeComponent>(slot.entity) else { return };
                        // Region かつシミュレーション対象のものだけ（冒頭の設計判断）。
                        if w.kind != WaterVolumeKind::Region || !w.simulate_level { return; }
                        let half_x = w.region_half_extents[0].abs();
                        let half_y = w.region_half_extents[1].abs();
                        let half_z = w.region_half_extents[2].abs();
                        let idx = nodes.len();
                        nodes.push(LevelNode {
                            // 初期水位は「今フレームの見た目の水面」。すでに sim が動いていれば
                            // その値を引き継ぐ（毎フレーム設定値へ戻ってしまわないように）。
                            level_y:   w.sim_level_y
                                .unwrap_or(actor_pos[1] + w.surface_height),
                            floor_y:   actor_pos[1] - half_y,
                            ceiling_y: actor_pos[1] + half_y,
                            // 底面積 = 全幅 × 全奥行き（半径の 2 倍どうしの積）。
                            base_area: (half_x * 2.0) * (half_z * 2.0),
                        });
                        node_entities.push(slot.entity);
                        // 同名アクタが複数ある場合は最初の 1 つだけを索引に載せる。
                        index_by_name.entry(actor.name.clone()).or_insert(idx);
                    }
                    // ── 開口 → リンク候補（名前解決は後段）──
                    ComponentKind::WaterLink => {
                        let Some(l) = world.get::<WaterLinkComponent>(slot.entity) else { return };
                        raw_links.push((l.clone(), actor_pos));
                    }
                    _ => {}
                }
            }
        });
    }

    if nodes.is_empty() { return None; }

    // アクタ名参照を添字へ解決する。片方でも見つからないリンクは無効として捨てる
    //（未接続のリンクを置いたままでも水が壊れない ＝ 編集途中でも安全）。
    let mut links = Vec::new();
    let mut link_positions = Vec::new();
    for (l, pos) in raw_links {
        let (Some(&a), Some(&b)) =
            (index_by_name.get(&l.volume_a), index_by_name.get(&l.volume_b)) else { continue };
        if a == b { continue; }   // 自分自身とつないでも意味が無い
        links.push(LevelLink {
            a, b,
            // 開口下端はアクタ相対なのでワールドへ出す。
            opening_bottom_y: pos[1] + l.opening_bottom,
            opening_height:   l.opening_height,
            opening_width:    l.opening_width,
            openness:         l.openness,
            coefficient:      l.flow_coefficient,
        });
        // 演出の波源はアクタ位置の「開口下端の高さ」に置く（水が噴き出す所）。
        link_positions.push([pos[0], pos[1] + l.opening_bottom, pos[2]]);
    }

    Some(SceneLevelGraph { nodes, node_entities, links, link_positions })
}

/// 全 WaterVolume の `sim_level_y` を消去し、設定水位（`surface_height`）へ戻す。
///
/// Play を抜けた最初のフレームにだけ呼ばれる。非アクティブなアクタも対象にする
/// （Play 中に非アクティブ化された水域が、次の Play で古い水位を引き継がないように）。
fn clear_simulated_levels(actors: &[Actor], world: &mut World, world_line: u32) {
    // 走査（&Actor）と書き込み（&mut World）を同時に借りられないので、
    // 先に対象エンティティを集めてから書き戻す。
    let mut targets: Vec<Entity> = Vec::new();
    for root in actors.iter().filter(|a| a.world_line == world_line) {
        collect_water_slot_entities(root, &mut targets);
    }
    for e in targets {
        if let Some(w) = world.get_mut::<WaterVolumeComponent>(e) {
            w.sim_level_y = None;
        }
    }
}

/// サブツリーの WaterVolume スロットエンティティを再帰的に集める
/// （アクティブ状態・enabled を問わない。後始末は取りこぼしがない方が安全）。
fn collect_water_slot_entities(actor: &Actor, out: &mut Vec<Entity>) {
    for slot in actor.slots() {
        if slot.kind == ComponentKind::WaterVolume { out.push(slot.entity); }
    }
    for child in actor.children() {
        collect_water_slot_entities(child, out);
    }
}

/// アクティブなアクタだけを DFS で辿り、`(アクタ, ワールド位置)` を訪問する。
///
/// スキップ規則は `collect_water_volumes` と揃える（active=false のサブツリーは
/// 丸ごと対象外）。ここが食い違うと「描かれているのに水位が動かない」水域が生まれる。
/// **アクタの回転は無視する**（Region が軸平行 AABB である以上、回転は意味を持たない）。
fn visit_actor(
    actor:         &Actor,
    world:         &World,
    parent_active: bool,
    visit:         &mut impl FnMut(&Actor, [f32; 3]),
) {
    let active = parent_active && actor.active;
    if !active { return; }
    let pos = world.get::<Transform>(actor.entity)
        .map(|t| t.position)
        .unwrap_or([0.0, 0.0, 0.0]);
    visit(actor, pos);
    for child in actor.children() {
        visit_actor(child, world, active, visit);
    }
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用シーン（World ＋ ルートアクタ列）。
    struct TestScene {
        world:  World,
        actors: Vec<Actor>,
    }

    impl TestScene {
        fn new() -> Self { Self { world: World::new(), actors: Vec::new() } }

        /// シミュレーション対象の Region 水域アクタを作ってツリーへ追加する。
        /// AABB 半径は [半幅, 半高, 半奥]、水面は「アクタ Y + surface_height」。
        fn add_pool(
            &mut self,
            name:           &str,
            pos:            [f32; 3],
            half:           [f32; 3],
            surface_height: f32,
        ) {
            let entity = self.world.spawn();
            let mut tf = Transform::default();
            tf.position = pos;
            self.world.insert(entity, tf);
            let mut actor = Actor::new(entity, name);

            let slot_entity = self.world.spawn();
            let mut w = WaterVolumeComponent::default();
            w.kind                = WaterVolumeKind::Region;
            w.simulate_level      = true;
            w.region_half_extents = half;
            w.surface_height      = surface_height;
            self.world.insert(slot_entity, w);
            actor.add_slot_typed::<WaterVolumeComponent>(
                "WaterVolumeComponent", ComponentKind::WaterVolume, slot_entity);
            self.actors.push(actor);
        }

        /// 開口（リンク）アクタを作ってツリーへ追加する。
        fn add_link(&mut self, name: &str, pos: [f32; 3], edit: impl FnOnce(&mut WaterLinkComponent)) {
            let entity = self.world.spawn();
            let mut tf = Transform::default();
            tf.position = pos;
            self.world.insert(entity, tf);
            let mut actor = Actor::new(entity, name);

            let slot_entity = self.world.spawn();
            let mut l = WaterLinkComponent::default();
            edit(&mut l);
            self.world.insert(slot_entity, l);
            actor.add_slot_typed::<WaterLinkComponent>(
                "WaterLinkComponent", ComponentKind::WaterLink, slot_entity);
            self.actors.push(actor);
        }

        /// 名前で水域の現在水位（sim_level_y）を引く。
        fn sim_level(&self, name: &str) -> Option<f32> {
            let actor = self.actors.iter().find(|a| a.name == name)?;
            let slot  = actor.slots().iter().find(|s| s.kind == ComponentKind::WaterVolume)?;
            self.world.get::<WaterVolumeComponent>(slot.entity)?.sim_level_y
        }

        /// 1 フレーム進める。
        fn step(&mut self, sim: &mut WaterLevelSim, dt: f32, running: bool) {
            sim.update(&self.actors, &mut self.world, 0, dt, running);
        }
    }

    /// 高い部屋から低い部屋へ、扉ごしに水が流れ込むこと（W2.5 の基本シナリオ）。
    #[test]
    fn water_flows_between_rooms_through_a_door() {
        let mut s = TestScene::new();
        // 部屋A: 中心 Y=0・半高 5m（床 -5m / 天井 +5m）・6m×6m。水面 +2m
        s.add_pool("RoomA", [0.0, 0.0, 0.0], [3.0, 5.0, 3.0], 2.0);
        // 部屋B: 同じ形。水面 -4m（ほぼ空）
        s.add_pool("RoomB", [20.0, 0.0, 0.0], [3.0, 5.0, 3.0], -4.0);
        // 扉: 床（Y=-5m）から高さ 2m。全開。
        s.add_link("Door", [10.0, -5.0, 0.0], |l| {
            l.volume_a = "RoomA".to_string();
            l.volume_b = "RoomB".to_string();
        });

        // 釣り合いへの接近は指数的なので十分な時間を与える
        //（6m×6m の部屋・幅 1m の扉なら時定数は約 9 秒。6000 フレーム ＝ 100 秒ぶん）。
        let mut sim = WaterLevelSim::default();
        for _ in 0..6000 { s.step(&mut sim, 1.0 / 60.0, true); }

        let a = s.sim_level("RoomA").expect("A の水位が計算されていること");
        let b = s.sim_level("RoomB").expect("B の水位が計算されていること");
        assert!(a < 2.0, "A は下がる: {a}");
        assert!(b > -4.0, "B は上がる: {b}");
        // 同じ底面積なので釣り合いは初期水位の平均 (2 + -4)/2 = -1m
        assert!((a - b).abs() < 1.0e-2, "釣り合うこと（{a} vs {b}）");
        assert!((a - (-1.0)).abs() < 1.0e-2, "釣り合い水位は平均 -1m: {a}");
    }

    /// **バルブを閉じれば水位は動かないこと**（データ差し替えで挙動が変わる契約）。
    #[test]
    fn closed_valve_keeps_levels_untouched() {
        let mut s = TestScene::new();
        s.add_pool("RoomA", [0.0, 0.0, 0.0], [3.0, 5.0, 3.0], 2.0);
        s.add_pool("RoomB", [20.0, 0.0, 0.0], [3.0, 5.0, 3.0], -4.0);
        s.add_link("Valve", [10.0, -5.0, 0.0], |l| {
            l.volume_a = "RoomA".to_string();
            l.volume_b = "RoomB".to_string();
            l.openness = 0.0;   // 全閉
        });

        let mut sim = WaterLevelSim::default();
        for _ in 0..600 { s.step(&mut sim, 1.0 / 60.0, true); }

        assert_eq!(s.sim_level("RoomA"), Some(2.0), "全閉なら初期水位のまま");
        assert_eq!(s.sim_level("RoomB"), Some(-4.0));
        assert!(sim.flow_events().is_empty(), "全閉では波源も出ない");
    }

    /// **Play を止めたら水位が設定値へ戻ること**（水位はゲーム状態）。
    #[test]
    fn stopping_play_resets_levels_to_authored_values() {
        let mut s = TestScene::new();
        s.add_pool("RoomA", [0.0, 0.0, 0.0], [3.0, 5.0, 3.0], 2.0);
        s.add_pool("RoomB", [20.0, 0.0, 0.0], [3.0, 5.0, 3.0], -4.0);
        s.add_link("Door", [10.0, -5.0, 0.0], |l| {
            l.volume_a = "RoomA".to_string();
            l.volume_b = "RoomB".to_string();
        });

        let mut sim = WaterLevelSim::default();
        for _ in 0..120 { s.step(&mut sim, 1.0 / 60.0, true); }
        assert!(s.sim_level("RoomA").is_some(), "Play 中は水位が入る");

        // Play 停止
        s.step(&mut sim, 1.0 / 60.0, false);
        assert_eq!(s.sim_level("RoomA"), None, "停止で揮発水位が消える");
        assert_eq!(s.sim_level("RoomB"), None);
        assert!(sim.flow_events().is_empty());
    }

    /// Edit 中（running=false）は一度も動かないこと。
    #[test]
    fn does_nothing_while_not_running() {
        let mut s = TestScene::new();
        s.add_pool("RoomA", [0.0, 0.0, 0.0], [3.0, 5.0, 3.0], 2.0);
        s.add_pool("RoomB", [20.0, 0.0, 0.0], [3.0, 5.0, 3.0], -4.0);
        s.add_link("Door", [10.0, -5.0, 0.0], |l| {
            l.volume_a = "RoomA".to_string();
            l.volume_b = "RoomB".to_string();
        });

        let mut sim = WaterLevelSim::default();
        for _ in 0..600 { s.step(&mut sim, 1.0 / 60.0, false); }
        assert_eq!(s.sim_level("RoomA"), None, "Edit では水位が発生しない");
    }

    /// `simulate_level = false` の水域は対象外（既存シーンの挙動が変わらない保証）。
    #[test]
    fn volumes_without_simulate_flag_are_ignored() {
        let mut s = TestScene::new();
        s.add_pool("RoomA", [0.0, 0.0, 0.0], [3.0, 5.0, 3.0], 2.0);
        s.add_pool("RoomB", [20.0, 0.0, 0.0], [3.0, 5.0, 3.0], -4.0);
        // B だけフラグを落とす
        {
            let actor = s.actors.iter().find(|a| a.name == "RoomB").unwrap();
            let slot  = actor.slots()[0].entity;
            s.world.get_mut::<WaterVolumeComponent>(slot).unwrap().simulate_level = false;
        }
        s.add_link("Door", [10.0, -5.0, 0.0], |l| {
            l.volume_a = "RoomA".to_string();
            l.volume_b = "RoomB".to_string();
        });

        let mut sim = WaterLevelSim::default();
        for _ in 0..120 { s.step(&mut sim, 1.0 / 60.0, true); }
        // B はノードにならないのでリンクが解決できず、A も動かない。
        assert_eq!(s.sim_level("RoomA"), Some(2.0));
        assert_eq!(s.sim_level("RoomB"), None);
    }

    /// 参照名が解決できないリンクは無視され、パニックしないこと（編集途中の安全性）。
    #[test]
    fn unresolved_link_is_ignored() {
        let mut s = TestScene::new();
        s.add_pool("RoomA", [0.0, 0.0, 0.0], [3.0, 5.0, 3.0], 2.0);
        s.add_link("Door", [10.0, -5.0, 0.0], |l| {
            l.volume_a = "RoomA".to_string();
            l.volume_b = "NoSuchRoom".to_string();
        });

        let mut sim = WaterLevelSim::default();
        s.step(&mut sim, 1.0 / 60.0, true);
        assert_eq!(s.sim_level("RoomA"), Some(2.0), "未接続なら水位はそのまま");
    }

    /// 流量が閾値を超えると波源イベントが開口位置に出ること（演出接続）。
    #[test]
    fn strong_flow_emits_wave_event_at_opening() {
        let mut s = TestScene::new();
        // 大きな水位差（+4m と -5m）＋広い扉で、閾値を確実に超える流量を作る。
        s.add_pool("RoomA", [0.0, 0.0, 0.0], [10.0, 5.0, 10.0], 4.0);
        s.add_pool("RoomB", [50.0, 0.0, 0.0], [10.0, 5.0, 10.0], -5.0);
        s.add_link("Door", [25.0, -5.0, 7.0], |l| {
            l.volume_a       = "RoomA".to_string();
            l.volume_b       = "RoomB".to_string();
            l.opening_width  = 3.0;
            l.opening_height = 3.0;
        });

        let mut sim = WaterLevelSim::default();
        s.step(&mut sim, 1.0 / 60.0, true);
        let events = sim.flow_events();
        assert_eq!(events.len(), 1, "流量イベントが 1 件出ること");
        assert_eq!(events[0].world_pos, [25.0, -5.0, 7.0], "開口のワールド位置");
        assert!(events[0].flow >= FLOW_WAVE_THRESHOLD);
    }

    /// 非アクティブなアクタの水域は対象外（描画のスキップ規則と一致させる契約）。
    #[test]
    fn inactive_actor_volume_is_excluded() {
        let mut s = TestScene::new();
        s.add_pool("RoomA", [0.0, 0.0, 0.0], [3.0, 5.0, 3.0], 2.0);
        s.actors[0].active = false;

        let mut sim = WaterLevelSim::default();
        s.step(&mut sim, 1.0 / 60.0, true);
        assert_eq!(s.sim_level("RoomA"), None, "非アクティブは計算されない");
    }

    /// フレームレートが違っても、同じ実時間ぶん進めれば同じ水位になること
    /// （固定タイムステップの意義そのもの）。
    #[test]
    fn result_is_framerate_independent() {
        let run = |dt: f32, frames: u32| {
            let mut s = TestScene::new();
            s.add_pool("RoomA", [0.0, 0.0, 0.0], [3.0, 5.0, 3.0], 2.0);
            s.add_pool("RoomB", [20.0, 0.0, 0.0], [3.0, 5.0, 3.0], -4.0);
            s.add_link("Door", [10.0, -5.0, 0.0], |l| {
                l.volume_a = "RoomA".to_string();
                l.volume_b = "RoomB".to_string();
            });
            let mut sim = WaterLevelSim::default();
            for _ in 0..frames { s.step(&mut sim, dt, true); }
            s.sim_level("RoomA").unwrap()
        };
        // 60fps で 120 フレーム ＝ 2 秒／30fps で 60 フレーム ＝ 2 秒
        let a = run(1.0 / 60.0, 120);
        let b = run(1.0 / 30.0, 60);
        assert!((a - b).abs() < 1.0e-4, "フレームレート非依存（{a} vs {b}）");
    }
}
