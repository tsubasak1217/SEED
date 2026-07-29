// ============================================================
//  water/collect.rs — シーンからの水ボリューム収集
//
//  Actor ツリーを DFS で走査し、有効な WaterVolumeComponent を
//  ワールド空間の ResolvedWaterVolume へ解決して集める。
//
//  スキップ規則は他の収集処理（app/actor_utils.rs の collect_mcs_in_world_line /
//  app/audio_ops.rs の collect_audio_sources）と揃える:
//    ・world_line が一致しないルートは対象外
//    ・active=false のアクターはサブツリーごとスキップ（祖先の非アクティブも伝播）
//    ・enabled=false のスロットはスキップ
//    ・Spline（川）で制御点が 2 点未満のものはスキップ（折れ線が作れないため）
//
//  【川の制御点の出どころ】同一アクタに有効な ControlPointComponent があれば、
//  そちらを評価した折れ線を **WaterVolumeComponent.spline_points より優先**して使う。
//  切替表:
//    ControlPoint スロット無し / enabled=false / 点が 2 点未満 → spline_points
//    上記以外                                                  → ControlPoint の点列
//
//  【W1 の制限】アクタのワールド行列は Transform.position のみ使う
//  （Transform はワールド空間）。**回転は無視する ＝ Region は軸平行 AABB。**
//  回転した水塊への対応は W4 以降。
// ============================================================

use crate::engine::components::water_volume_component::{
    WaterVolumeComponent, WaterVolumeKind,
};
use crate::engine::components::control_point_component::ControlPointComponent;
use crate::engine::components::{ComponentKind, Transform};
use crate::engine::ecs::World;
use crate::engine::path::{PathEval, PATH_DEFAULT_STEP_M};
use crate::engine::structs::objects::Actor;

use super::resolved::ResolvedWaterVolume;

/// Transform を持たないアクター（フォルダノード・2D アクター等）の既定位置。
const FALLBACK_ACTOR_POSITION: [f32; 3] = [0.0, 0.0, 0.0];

/// シーンの全アクタを再帰走査し、有効な WaterVolume をワールド空間へ解決して集める。
/// active でないアクタ・enabled でないスロット・Spline(未実装) はスキップする。
///
/// `world_line` はアクター編集タブとの分離用（0=通常シーン）。
pub fn collect_water_volumes(
    actors:     &[Actor],
    world:      &World,
    world_line: u32,
) -> Vec<ResolvedWaterVolume> {
    let mut out = Vec::new();
    // DFS 連番カウンタ。ピッキングの ID 採番（キャンバス／MC と共有する
    // 「アクタ DFS インデックス」）と一致させるため、収集をスキップしたアクタでも
    // 必ず進める（下記 collect_in_actor を参照）。
    let mut dfs_counter = 0u32;
    // ルートは world_line が一致するものだけを対象にする
    //（collect_mcs_in_world_line と同じフィルタ条件）
    for root in actors.iter().filter(|a| a.world_line == world_line) {
        collect_in_actor(root, world, &mut out, &mut dfs_counter, true);
    }
    out
}

/// collect_water_volumes の再帰実装。
///
/// `parent_active` は祖先のアクティブ状態。自身または祖先が active=false の
/// アクターは水ボリュームを収集しない。
///
/// `dfs_counter` はアクタの DFS 連番（0 始まり）。**収集対象外のアクタでも必ず加算し、
/// 非アクティブなサブツリーへも再帰する**。これは `collect_mcs_in_world_line` と
/// キャンバスピックの採番規則に合わせるためで、ここでカウントを飛ばすと
/// 水面クリックで「別のアクタ」が選択されるズレが起きる。
fn collect_in_actor(
    actor:         &Actor,
    world:         &World,
    out:           &mut Vec<ResolvedWaterVolume>,
    dfs_counter:   &mut u32,
    parent_active: bool,
) {
    let dfs_id = *dfs_counter;
    *dfs_counter += 1;
    let active = parent_active && actor.active;

    if active {
        // アクターのワールド位置（Transform はワールド空間。回転は W1 では無視）
        let pos = world.get::<Transform>(actor.entity)
            .map(|t| t.position)
            .unwrap_or(FALLBACK_ACTOR_POSITION);

        // ── 同一アクタの ControlPointComponent を川の折れ線として先に評価する ──
        //
        // 「点を置く汎用コンポーネント（ControlPoint）」と「点を使う機能（川）」を
        // 分離してあるので、川は**同じアクタに載っている点列**を探して使う。
        // ここでは Transform の位置だけでなく**回転・スケールを含む完全な Transform**を
        // 使う（`from_component` に渡す actor_pos が位置のみなのと異なる点）。
        // アクタを回したら川も一緒に回るのが当然の挙動であり、PathEval がその変換を担う。
        //
        // enabled=false の ControlPoint スロットは**見つからなかった扱い**にする。
        // これにより「制御点で編集する／従来の spline_points を使う」を
        // スロットのトグル 1 つで切り替えられる（併用時の優先順位をユーザーが握れる）。
        let control_polyline: Option<Vec<[f32; 3]>> = actor.slots().iter()
            .find(|s| s.kind == ComponentKind::ControlPoint && s.enabled)
            .and_then(|s| world.get::<ControlPointComponent>(s.entity))
            .map(|cp| {
                let tf = world.get::<Transform>(actor.entity).cloned().unwrap_or_default();
                PathEval::from_points(&cp.points, &tf).sample_polyline(PATH_DEFAULT_STEP_M)
            });

        for slot in actor.slots() {
            // 無効スロットは描画・問い合わせともに対象外
            if slot.kind != ComponentKind::WaterVolume || !slot.enabled { continue; }
            let Some(wv) = world.get::<WaterVolumeComponent>(slot.entity) else { continue };
            let resolved = ResolvedWaterVolume::from_component_with_path(
                wv, pos, dfs_id, control_polyline.as_deref());
            // 川（Spline。W4）は制御点が 2 点未満だと折れ線が作れず、
            // 描画も問い合わせも定義できない。収集しない（下流が誤って参照しないように）。
            if wv.kind == WaterVolumeKind::Spline && resolved.river.is_none() { continue; }
            out.push(resolved);
        }
    }

    // 子孫へは**常に**再帰する。非アクティブなサブツリーの水は収集されないが
    //（active フラグが伝播するため）、DFS 連番だけは進める必要がある。
    for child in actor.children() {
        collect_in_actor(child, world, out, dfs_counter, active);
    }
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::control_point_component::ControlPoint;

    /// テスト用シーンビルダの戻り値: (World, ルートアクター列)
    struct TestScene {
        world:  World,
        actors: Vec<Actor>,
    }

    impl TestScene {
        fn new() -> Self {
            Self { world: World::new(), actors: Vec::new() }
        }

        /// 指定位置に WaterVolume スロットを 1 つ持つアクターを作って返す
        /// （まだツリーへは追加しない）。
        fn make_water_actor(
            &mut self,
            pos:  [f32; 3],
            kind: WaterVolumeKind,
        ) -> Actor {
            self.make_water_actor_with(pos, kind, |_| {})
        }

        /// 上と同じだが、生成した WaterVolumeComponent を `edit` で追加設定できる
        /// （川の制御点など、種別ごとの追加パラメータを与えるため）。
        fn make_water_actor_with(
            &mut self,
            pos:  [f32; 3],
            kind: WaterVolumeKind,
            edit: impl FnOnce(&mut WaterVolumeComponent),
        ) -> Actor {
            let entity = self.world.spawn();
            let mut tf = Transform::default();
            tf.position = pos;
            self.world.insert(entity, tf);

            let mut actor = Actor::new(entity, "water");
            let slot_entity = self.world.spawn();
            let mut wv = WaterVolumeComponent::default();
            wv.kind = kind;
            edit(&mut wv);
            self.world.insert(slot_entity, wv);
            actor.add_slot_typed::<WaterVolumeComponent>(
                "WaterVolumeComponent", ComponentKind::WaterVolume, slot_entity);
            actor
        }

        /// 収集を実行する（world_line 0）。
        fn collect(&self) -> Vec<ResolvedWaterVolume> {
            collect_water_volumes(&self.actors, &self.world, 0)
        }

        /// 既存アクターに ControlPointComponent スロットを 1 つ足す
        ///（点はアクタ相対座標で与える）。川との統合テスト用。
        fn add_control_points(
            &mut self,
            actor:   &mut Actor,
            points:  &[[f32; 3]],
            enabled: bool,
        ) {
            let slot_entity = self.world.spawn();
            let comp = ControlPointComponent {
                points: points.iter()
                    .map(|&p| ControlPoint { position: p, ..Default::default() })
                    .collect(),
            };
            self.world.insert(slot_entity, comp);
            actor.add_slot_typed::<ControlPointComponent>(
                "ControlPointComponent", ComponentKind::ControlPoint, slot_entity);
            // 追加したスロット（＝末尾）の有効・無効を設定する
            let last = actor.slots().len() - 1;
            actor.slots_mut()[last].enabled = enabled;
        }
    }

    /// テスト用: 直線の川になる spline_points（Z = 目印値）。
    /// ControlPoint 側が採用されたかどうかを Z 座標だけで判別できるようにする。
    const SPLINE_MARKER_Z: f32 = 777.0;

    /// 同一アクタに ControlPointComponent があれば、spline_points ではなく
    /// そちらから川が組まれること（優先度の契約）。
    #[test]
    fn river_prefers_control_points_over_spline_points() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor_with(
            [0.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |w| w.spline_points = vec![
                [0.0, 0.0, SPLINE_MARKER_Z], [10.0, 0.0, SPLINE_MARKER_Z]],
        );
        s.add_control_points(&mut a, &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]], true);
        s.actors.push(a);

        let vols = s.collect();
        assert_eq!(vols.len(), 1);
        let river = vols[0].river.as_ref().expect("川が成立する");
        assert_eq!(river.nodes[0].pos[2], 0.0, "spline_points の目印 Z が出たら誤り");
    }

    /// ControlPointComponent の点が 1 点以下なら川にならないので、
    /// spline_points へフォールバックすること。
    #[test]
    fn river_falls_back_when_control_points_too_few() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor_with(
            [0.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |w| w.spline_points = vec![
                [0.0, 0.0, SPLINE_MARKER_Z], [10.0, 0.0, SPLINE_MARKER_Z]],
        );
        s.add_control_points(&mut a, &[[0.0, 0.0, 0.0]], true);
        s.actors.push(a);

        let vols = s.collect();
        assert_eq!(vols.len(), 1);
        let river = vols[0].river.as_ref().expect("spline_points 側で川が成立する");
        assert_eq!(river.nodes[0].pos[2], SPLINE_MARKER_Z, "spline_points が使われること");
    }

    /// ControlPoint スロットが enabled=false なら spline_points へフォールバックすること
    ///（＝スロットのトグルで「どちらの点列を使うか」を切り替えられる）。
    #[test]
    fn river_falls_back_when_control_point_slot_disabled() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor_with(
            [0.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |w| w.spline_points = vec![
                [0.0, 0.0, SPLINE_MARKER_Z], [10.0, 0.0, SPLINE_MARKER_Z]],
        );
        s.add_control_points(&mut a, &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]], false);
        s.actors.push(a);

        let vols = s.collect();
        assert_eq!(vols.len(), 1);
        let river = vols[0].river.as_ref().unwrap();
        assert_eq!(river.nodes[0].pos[2], SPLINE_MARKER_Z, "無効スロットは無視されること");
    }

    /// ControlPoint 経路でも surface_height が Y に効くこと。
    #[test]
    fn river_from_control_points_applies_surface_height() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor_with(
            [0.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |w| w.surface_height = 3.0,
        );
        s.add_control_points(&mut a, &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]], true);
        s.actors.push(a);

        let vols = s.collect();
        let river = vols[0].river.as_ref().unwrap();
        assert_eq!(river.nodes[0].pos[1], 3.0, "制御点 Y(0) + surface_height(3)");
    }

    /// ControlPoint 経路でアクタ位置が二重に足されないこと。
    /// PathEval が Transform を適用済みなので、resolved 側で足すと 200 になってしまう。
    #[test]
    fn river_from_control_points_does_not_double_apply_actor_position() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor_with(
            [100.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |_| {},
        );
        // ローカル (0,0,0) → ワールド (100,0,0) になるはず
        s.add_control_points(&mut a, &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]], true);
        s.actors.push(a);

        let vols = s.collect();
        let river = vols[0].river.as_ref().unwrap();
        assert_eq!(river.nodes[0].pos[0], 100.0, "200 なら二重加算");
    }

    /// 通常のアクター 1 個から 1 ボリュームが収集される。
    #[test]
    fn collects_active_enabled_volume() {
        let mut s = TestScene::new();
        let a = s.make_water_actor([0.0, 4.0, 0.0], WaterVolumeKind::Region);
        s.actors.push(a);

        let vols = s.collect();
        assert_eq!(vols.len(), 1);
        // Region の水面 = アクタ Y + surface_height(既定 0.0)
        assert_eq!(vols[0].surface_y, 4.0);
        assert_eq!(vols[0].center, [0.0, 4.0, 0.0]);
    }

    /// active=false のアクターはスキップされる。
    #[test]
    fn skips_inactive_actor() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor([0.0, 0.0, 0.0], WaterVolumeKind::Region);
        a.active = false;
        s.actors.push(a);

        assert!(s.collect().is_empty());
    }

    /// 祖先が非アクティブなら子の水もスキップされる（サブツリー伝播）。
    #[test]
    fn skips_subtree_under_inactive_ancestor() {
        let mut s = TestScene::new();
        let child = s.make_water_actor([0.0, 1.0, 0.0], WaterVolumeKind::Region);
        let parent_entity = s.world.spawn();
        s.world.insert(parent_entity, Transform::default());
        let mut parent = Actor::new(parent_entity, "parent");
        parent.active = false;
        parent.add_child(child);
        s.actors.push(parent);

        assert!(s.collect().is_empty());
    }

    /// アクティブな親の下の子アクターの水は収集される。
    #[test]
    fn collects_child_under_active_parent() {
        let mut s = TestScene::new();
        let child = s.make_water_actor([0.0, 1.0, 0.0], WaterVolumeKind::Region);
        let parent_entity = s.world.spawn();
        s.world.insert(parent_entity, Transform::default());
        let mut parent = Actor::new(parent_entity, "parent");
        parent.add_child(child);
        s.actors.push(parent);

        assert_eq!(s.collect().len(), 1);
    }

    /// enabled=false のスロットはスキップされる。
    #[test]
    fn skips_disabled_slot() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor([0.0, 0.0, 0.0], WaterVolumeKind::Region);
        a.slots_mut()[0].enabled = false;
        s.actors.push(a);

        assert!(s.collect().is_empty());
    }

    /// 制御点の無い Spline（＝川として成立していない）は収集されない。
    #[test]
    fn skips_spline_without_control_points() {
        let mut s = TestScene::new();
        let a = s.make_water_actor([0.0, 0.0, 0.0], WaterVolumeKind::Spline);
        s.actors.push(a);

        assert!(s.collect().is_empty());
    }

    /// 制御点が 2 点以上ある Spline（川）は収集され、折れ線を持つ。
    #[test]
    fn collects_spline_with_control_points() {
        let mut s = TestScene::new();
        let a = s.make_water_actor_with(
            [0.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |w| w.spline_points = vec![[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]],
        );
        s.actors.push(a);

        let vols = s.collect();
        assert_eq!(vols.len(), 1);
        assert!(vols[0].river.is_some(), "川の折れ線が構築されている");
    }

    /// world_line が一致しないルートは対象外。
    #[test]
    fn skips_other_world_line() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor([0.0, 0.0, 0.0], WaterVolumeKind::Region);
        a.world_line = 7;
        s.actors.push(a);

        // world_line 0 では収集されない
        assert!(collect_water_volumes(&s.actors, &s.world, 0).is_empty());
        // world_line 7 では収集される
        assert_eq!(collect_water_volumes(&s.actors, &s.world, 7).len(), 1);
    }

    /// DFS 連番は「親 → 子」の順で、水を持たないアクタも 1 つとして数える。
    /// （ピッキングの ID 採番規則 = collect_mcs_in_world_line と一致させるため）
    #[test]
    fn assigns_dfs_id_counting_all_actors() {
        let mut s = TestScene::new();
        // ルート0: 水なし親（dfs 0）＋ 水を持つ子（dfs 1）
        let child = s.make_water_actor([0.0, 1.0, 0.0], WaterVolumeKind::Region);
        let parent_entity = s.world.spawn();
        s.world.insert(parent_entity, Transform::default());
        let mut parent = Actor::new(parent_entity, "parent");
        parent.add_child(child);
        s.actors.push(parent);
        // ルート1: 水を持つアクタ（dfs 2）
        let second = s.make_water_actor([0.0, 2.0, 0.0], WaterVolumeKind::Region);
        s.actors.push(second);

        let vols = s.collect();
        assert_eq!(vols.len(), 2);
        assert_eq!(vols[0].actor_dfs_id, 1, "水なし親を 1 つ数えた次が子");
        assert_eq!(vols[1].actor_dfs_id, 2, "次のルートは兄弟サブツリー全体の後ろ");
    }

    /// 非アクティブなサブツリーも DFS 連番だけは消費する
    /// （数え落とすと後続アクタの ID がズレて別アクタが選択されてしまう）。
    #[test]
    fn inactive_subtree_still_consumes_dfs_ids() {
        let mut s = TestScene::new();
        // ルート0: 非アクティブ親（dfs 0）＋ その子（dfs 1）。どちらも収集されない。
        let hidden_child = s.make_water_actor([0.0, 0.0, 0.0], WaterVolumeKind::Region);
        let parent_entity = s.world.spawn();
        s.world.insert(parent_entity, Transform::default());
        let mut parent = Actor::new(parent_entity, "inactive_parent");
        parent.active = false;
        parent.add_child(hidden_child);
        s.actors.push(parent);
        // ルート1: 収集される水アクタ（dfs 2）
        let visible = s.make_water_actor([0.0, 3.0, 0.0], WaterVolumeKind::Region);
        s.actors.push(visible);

        let vols = s.collect();
        assert_eq!(vols.len(), 1);
        assert_eq!(vols[0].actor_dfs_id, 2);
    }

    /// Ocean はアクタ位置に依存せず、surface_height をワールド Y として使う。
    #[test]
    fn ocean_ignores_actor_position() {
        let mut s = TestScene::new();
        let a = s.make_water_actor([10.0, 99.0, -5.0], WaterVolumeKind::Ocean);
        s.actors.push(a);

        let vols = s.collect();
        assert_eq!(vols.len(), 1);
        // surface_height 既定 0.0 がそのままワールド水面 Y になる
        assert_eq!(vols[0].surface_y, 0.0);
    }
}
