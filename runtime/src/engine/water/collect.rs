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
//  【川の制御点の出どころ】**明示参照方式**（W4.1）。
//  `WaterVolumeComponent.control_point_ref` にアクタ名を設定すると、そのアクタの
//  0 番目の `ControlPointComponent` を評価した折れ線が川になる。
//  切替表:
//    control_point_ref が空                                  → spline_points
//    参照名のアクタが見つからない                            → spline_points
//    見つかったが ControlPoint 無し / enabled=false / 2 点未満 → spline_points
//    上記以外                                                → 参照先の ControlPoint の点列
//
//  **同一アクタの ControlPointComponent を自動優先する挙動は廃止した**（W4.1）。
//  自動優先は「コンポーネントを足しただけで川の形が変わる」「どちらが効いているか
//  UI から見えない」という二重の分かりにくさがあり、実際に混乱を生んだ。
//
//  参照先は**別アクタでもよい**。その場合、点列のワールド解決には
//  **参照先アクタの Transform** を使う（点はそのアクタの相対座標なので当然）。
//  ＝制御点を持つアクタを動かせば川が動き、水アクタを動かしても川は動かない。
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
        collect_in_actor(root, actors, world, world_line, &mut out, &mut dfs_counter, true);
    }
    out
}

/// 指定世界線のアクタツリーを DFS で走査し、**名前が一致する最初のアクタ**を返す。
///
/// 実体は汎用の参照解決（`engine::binding::resolve::find_actor_by_name`）にある。
/// 「アクタ名で引く」規則は川の制御点参照・`@ref` バインド・スクリプトの参照
/// フィールドが**同一でなければならない**（別々に実装すると、同名アクタが
/// あるシーンで参照ごとに違う相手を指してしまう）ため、実装を 1 つに束ねている。
pub use crate::engine::binding::resolve::find_actor_by_name;

/// `control_point_ref` を解決し、参照先の制御点列をワールド空間の折れ線にして返す。
///
/// 解決できない（参照が空・アクタが見つからない・ControlPoint スロットが無い／無効）
/// 場合は None を返し、呼び出し側は `spline_points` へフォールバックする。
///
/// ワールド解決には**参照先アクタの Transform**（位置・回転・スケール）を使う。
/// 水ボリューム側のアクタ Transform は一切関与しない。
fn resolve_control_polyline(
    actors:     &[Actor],
    world:      &World,
    world_line: u32,
    ref_name:   &str,
) -> Option<Vec<[f32; 3]>> {
    let target = find_actor_by_name(actors, world_line, ref_name)?;
    // 1 アクタに ControlPoint スロットが複数ある場合は **0 番目（最初の有効スロット）**を使う。
    // 「何番目か」まで参照に持たせるとユーザーが指定すべき情報が増えるため、
    // 当面はスロット単位の enabled トグルで選ばせる設計にしてある。
    let slot = target.slots().iter()
        .find(|s| s.kind == ComponentKind::ControlPoint && s.enabled)?;
    let comp = world.get::<ControlPointComponent>(slot.entity)?;
    let tf   = world.get::<Transform>(target.entity).cloned().unwrap_or_default();
    Some(PathEval::from_component(comp, &tf).sample_polyline(PATH_DEFAULT_STEP_M))
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
///
/// `all_actors` は**世界線のルート一覧**（`control_point_ref` の名前解決に使う）。
/// 参照先は自分の子孫とは限らないため、再帰の各段でツリー全体へ触れる必要がある。
fn collect_in_actor(
    actor:         &Actor,
    all_actors:    &[Actor],
    world:         &World,
    world_line:    u32,
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

        for slot in actor.slots() {
            // 無効スロットは描画・問い合わせともに対象外
            if slot.kind != ComponentKind::WaterVolume || !slot.enabled { continue; }
            let Some(wv) = world.get::<WaterVolumeComponent>(slot.entity) else { continue };

            // ── 川の制御点の出どころ（W4.1: 明示参照方式）──────────
            // 参照名が設定されているときだけ、その名前のアクタの ControlPointComponent を
            // ワールド解決して折れ線にする。空 or 解決不能なら None（＝spline_points 経路）。
            // **水ボリュームごと**に評価するのは、同じアクタに複数の川スロットが載って
            // それぞれ別のパスを参照する構成を許すため。
            let control_polyline: Option<Vec<[f32; 3]>> = if wv.kind == WaterVolumeKind::Spline {
                resolve_control_polyline(all_actors, world, world_line, &wv.control_point_ref)
            } else {
                None
            };

            let mut resolved = ResolvedWaterVolume::from_component_with_path(
                wv, pos, dfs_id, control_polyline.as_deref());

            // ── シェーダパラメータの `@ref` バインド（W8.3）──────────
            // 解決できたバインドの実値で**パラメータ値を上書き**する。
            // 上書きするのはこの中間表現（毎フレーム作り直される複製）だけなので、
            // シーンの保存値は一切変わらない（バインドを外せば元の値に戻る）。
            // 解決できなかったバインドは差し込まれず、保存値／アセット既定値のまま。
            for (name, value) in super::bindings::resolve_bindings_for_asset(
                all_actors, world, world_line, &resolved.surface_shader, &wv.bindings)
            {
                resolved.shader_params.insert(name, value);
            }

            // 川（Spline。W4）は制御点が 2 点未満だと折れ線が作れず、
            // 描画も問い合わせも定義できない。収集しない（下流が誤って参照しないように）。
            if wv.kind == WaterVolumeKind::Spline && resolved.river.is_none() { continue; }
            out.push(resolved);
        }
    }

    // 子孫へは**常に**再帰する。非アクティブなサブツリーの水は収集されないが
    //（active フラグが伝播するため）、DFS 連番だけは進める必要がある。
    for child in actor.children() {
        collect_in_actor(child, all_actors, world, world_line, out, dfs_counter, active);
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

        /// 指定名・指定位置に ControlPointComponent だけを持つアクターを作って返す
        ///（川の参照先として使う。まだツリーへは追加しない）。
        fn make_path_actor(
            &mut self,
            name:   &str,
            pos:    [f32; 3],
            points: &[[f32; 3]],
        ) -> Actor {
            let entity = self.world.spawn();
            let mut tf = Transform::default();
            tf.position = pos;
            self.world.insert(entity, tf);
            let mut actor = Actor::new(entity, name);
            self.add_control_points(&mut actor, points, true);
            actor
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
                // テストの川は開いたパス（閉ループの検証は path::eval 側で行う）。
                closed: false,
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

    /// **同一アクタの ControlPointComponent を自動優先しないこと**（W4.1 の主変更）。
    /// 参照を設定していないので、点が同居していても spline_points が使われる。
    #[test]
    fn river_ignores_same_actor_control_points_without_ref() {
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
        assert_eq!(river.nodes[0].pos[2], SPLINE_MARKER_Z,
            "参照未設定なら同居する ControlPoint は使われないこと");
    }

    /// 参照名を**自分自身**に設定すると、そのアクタの ControlPoint が使われること。
    #[test]
    fn river_uses_control_points_when_ref_points_to_self() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor_with(
            [0.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |w| {
                w.spline_points = vec![
                    [0.0, 0.0, SPLINE_MARKER_Z], [10.0, 0.0, SPLINE_MARKER_Z]];
                w.control_point_ref = "water".to_string();
            },
        );
        s.add_control_points(&mut a, &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]], true);
        s.actors.push(a);

        let vols = s.collect();
        let river = vols[0].river.as_ref().expect("川が成立する");
        assert_eq!(river.nodes[0].pos[2], 0.0, "spline_points の目印 Z が出たら誤り");
    }

    /// 参照先が**別アクタ**でも解決でき、そのときのワールド解決には
    /// **参照先アクタの Transform** が使われること（水アクタの位置は効かない）。
    #[test]
    fn river_resolves_ref_to_other_actor_with_that_actors_transform() {
        let mut s = TestScene::new();
        // 水アクタは X = 500（この位置が川に効いてはいけない）
        let water = s.make_water_actor_with(
            [500.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |w| w.control_point_ref = "RiverPath".to_string(),
        );
        // パスアクタは X = 100。ローカル (0,0,0) → ワールド (100,0,0) になるはず。
        let path = s.make_path_actor(
            "RiverPath", [100.0, 0.0, 0.0], &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]]);
        s.actors.push(water);
        s.actors.push(path);

        let vols = s.collect();
        let river = vols[0].river.as_ref().expect("別アクタ参照で川が成立する");
        assert_eq!(river.nodes[0].pos[0], 100.0,
            "参照先アクタの Transform が使われること（500 や 600 なら誤り）");
    }

    /// 参照名が解決できない（そんなアクタが居ない）ときは spline_points へ落ちること。
    #[test]
    fn river_falls_back_when_ref_actor_not_found() {
        let mut s = TestScene::new();
        let a = s.make_water_actor_with(
            [0.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |w| {
                w.spline_points = vec![
                    [0.0, 0.0, SPLINE_MARKER_Z], [10.0, 0.0, SPLINE_MARKER_Z]];
                w.control_point_ref = "NoSuchActor".to_string();
            },
        );
        s.actors.push(a);

        let vols = s.collect();
        let river = vols[0].river.as_ref().expect("spline_points 側で川が成立する");
        assert_eq!(river.nodes[0].pos[2], SPLINE_MARKER_Z);
    }

    /// 参照先の点が 1 点以下なら川にならないので、spline_points へフォールバックすること。
    #[test]
    fn river_falls_back_when_referenced_points_too_few() {
        let mut s = TestScene::new();
        let a = s.make_water_actor_with(
            [0.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |w| {
                w.spline_points = vec![
                    [0.0, 0.0, SPLINE_MARKER_Z], [10.0, 0.0, SPLINE_MARKER_Z]];
                w.control_point_ref = "RiverPath".to_string();
            },
        );
        let path = s.make_path_actor("RiverPath", [0.0, 0.0, 0.0], &[[0.0, 0.0, 0.0]]);
        s.actors.push(a);
        s.actors.push(path);

        let vols = s.collect();
        let river = vols[0].river.as_ref().expect("spline_points 側で川が成立する");
        assert_eq!(river.nodes[0].pos[2], SPLINE_MARKER_Z, "spline_points が使われること");
    }

    /// 参照先の ControlPoint スロットが enabled=false なら spline_points へ落ちること
    ///（＝スロットのトグルで参照を一時的に切れる）。
    #[test]
    fn river_falls_back_when_referenced_slot_disabled() {
        let mut s = TestScene::new();
        let a = s.make_water_actor_with(
            [0.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |w| {
                w.spline_points = vec![
                    [0.0, 0.0, SPLINE_MARKER_Z], [10.0, 0.0, SPLINE_MARKER_Z]];
                w.control_point_ref = "RiverPath".to_string();
            },
        );
        // enabled = false で ControlPoint を持つパスアクタを組む
        let path_entity = s.world.spawn();
        s.world.insert(path_entity, Transform::default());
        let mut path = Actor::new(path_entity, "RiverPath");
        s.add_control_points(&mut path, &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]], false);
        s.actors.push(a);
        s.actors.push(path);

        let vols = s.collect();
        let river = vols[0].river.as_ref().unwrap();
        assert_eq!(river.nodes[0].pos[2], SPLINE_MARKER_Z, "無効スロットは無視されること");
    }

    /// 参照経路でも surface_height が Y に効くこと。
    #[test]
    fn river_from_ref_applies_surface_height() {
        let mut s = TestScene::new();
        let a = s.make_water_actor_with(
            [0.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |w| {
                w.surface_height    = 3.0;
                w.control_point_ref = "RiverPath".to_string();
            },
        );
        let path = s.make_path_actor(
            "RiverPath", [0.0, 0.0, 0.0], &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]]);
        s.actors.push(a);
        s.actors.push(path);

        let vols = s.collect();
        let river = vols[0].river.as_ref().unwrap();
        assert_eq!(river.nodes[0].pos[1], 3.0, "制御点 Y(0) + surface_height(3)");
    }

    /// 参照経路でアクタ位置が二重に足されないこと。
    /// PathEval が Transform を適用済みなので、resolved 側で足すと 200 になってしまう。
    #[test]
    fn river_from_ref_does_not_double_apply_actor_position() {
        let mut s = TestScene::new();
        let mut a = s.make_water_actor_with(
            [100.0, 0.0, 0.0],
            WaterVolumeKind::Spline,
            |w| w.control_point_ref = "water".to_string(),
        );
        // ローカル (0,0,0) → ワールド (100,0,0) になるはず
        s.add_control_points(&mut a, &[[0.0, 0.0, 0.0], [10.0, 0.0, 0.0]], true);
        s.actors.push(a);

        let vols = s.collect();
        let river = vols[0].river.as_ref().unwrap();
        assert_eq!(river.nodes[0].pos[0], 100.0, "200 なら二重加算");
    }

    /// 参照先アクタは**子アクタでも**名前で見つかること（DFS 探索の契約）。
    #[test]
    fn find_actor_by_name_searches_descendants() {
        let mut s = TestScene::new();
        let child = s.make_path_actor("DeepPath", [0.0, 0.0, 0.0], &[[0.0; 3], [10.0, 0.0, 0.0]]);
        let parent_entity = s.world.spawn();
        s.world.insert(parent_entity, Transform::default());
        let mut parent = Actor::new(parent_entity, "parent");
        parent.add_child(child);
        s.actors.push(parent);

        assert!(find_actor_by_name(&s.actors, 0, "DeepPath").is_some(), "子孫まで探すこと");
        assert!(find_actor_by_name(&s.actors, 0, "parent").is_some());
        assert!(find_actor_by_name(&s.actors, 0, "missing").is_none());
        // 空文字（未設定）は常に見つからない扱い
        assert!(find_actor_by_name(&s.actors, 0, "").is_none());
        // 世界線が違えば見つからない
        assert!(find_actor_by_name(&s.actors, 7, "DeepPath").is_none());
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
