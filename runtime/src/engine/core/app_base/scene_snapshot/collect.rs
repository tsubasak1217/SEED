// ============================================================
//  scene_snapshot/collect.rs — 写しに書き出すアクタの集め方と、メインカメラの姿勢（純粋な処理。GPU 不要）
//
//  【集め方】
//  ・シーンの世界線（world_line == 0）のトップレベルアクタを、いまの World の値（現在の Transform・コンポーネントの値）で
//    ActorData にする（Actor::to_data。保存〈SAVE_SCENE〉と同じ変換）。スクリプトが Instantiate で生成したアクタも
//    シーンのツリーに入っているので、そのまま含まれる。アクター編集タブ（world_line > 0）は .scene の形式が world_line を
//    持たないので含めない（端末には無い。PC の埋め込み Play では編集タブがありうる）。
//  ・「保存できないもの」は飛ばして続ける: アクタ 1 体ずつ（子を外した本体だけ）を JSON にして読み戻し、読み戻せないもの
//    （Transform 等に NaN・無限大が入ると JSON では null になり、読み込めない）はその子孫ごと飛ばす。
//    子の 1 体が壊れていても親や兄弟は残る（親だけを確かめてから子を 1 体ずつ確かめるため）。
//  【メインカメラ】シーンの is_main のカメラ（Scene::find_main_camera。Transform はワールド空間）の位置と YXZ オイラー角。
//  書き出しの本体（ファイル・応答）は app/scene_snapshot_ops.rs。
// ============================================================

use crate::engine::components::Transform;
use crate::engine::core::app_base::scene::DebugCameraData;
use crate::engine::ecs::World;
use crate::engine::structs::objects::actor::ActorData;
use crate::engine::structs::objects::Actor;

use super::wire::CameraPose;

/// シーンの世界線（アクター編集タブでない、シーン本体のアクタ）。
const SCENE_WORLD_LINE: u32 = 0;

/// 飛ばしたアクタの名前をログへ出す上限（壊れたアクタが大量にあっても 1 行を短く保つ）。
pub const MAX_SKIPPED_NAMES: usize = 8;

/// 集めた結果。
#[derive(Default)]
pub struct CollectedActors {
    /// 書き出すトップレベルアクタ（子を含む。飛ばしたものは入っていない）。
    pub actors: Vec<ActorData>,
    /// 書き出すアクタの数（子を含む）。
    pub saved: usize,
    /// 飛ばしたアクタの数（子を含む）。
    pub skipped: usize,
    /// 飛ばしたアクタ（子孫ごと飛ばしたものの根）の名前（先頭 MAX_SKIPPED_NAMES 件）。
    pub skipped_names: Vec<String>,
}

/// 写しに書き出すアクタを集める（保存できないアクタは子孫ごと飛ばす）。
///
/// # 引数
/// * `actors` - シーンのトップレベルアクタ（`Scene::actors`）
/// * `world` - コンポーネントの値（`Scene::world`）
pub fn collect_snapshot_actors(actors: &[Actor], world: &World) -> CollectedActors {
    let mut out = CollectedActors::default();
    for root in actors.iter().filter(|a| a.world_line == SCENE_WORLD_LINE) {
        if let Some(data) = keep_serializable(root.to_data(world), &mut out) {
            out.actors.push(data);
        }
    }
    out
}

/// アクタ 1 体（子を外した本体）が保存できるかを確かめ、できれば子を 1 体ずつ確かめて残す。
/// できなければ子孫ごと飛ばして数える。
fn keep_serializable(mut data: ActorData, out: &mut CollectedActors) -> Option<ActorData> {
    let children = std::mem::take(&mut data.children);
    if !round_trips(&data) {
        out.skipped += 1 + children.iter().map(count_nodes).sum::<usize>();
        if out.skipped_names.len() < MAX_SKIPPED_NAMES {
            out.skipped_names.push(data.name.clone());
        }
        return None;
    }
    out.saved += 1;
    data.children = children.into_iter().filter_map(|child| keep_serializable(child, out)).collect();
    Some(data)
}

/// JSON にして読み戻せるか（読み込み側 `Scene::from_json` と同じ型で確かめる）。
fn round_trips(data: &ActorData) -> bool {
    serde_json::to_value(data)
        .and_then(serde_json::from_value::<ActorData>)
        .is_ok()
}

/// アクタの数（自分と子孫）。
fn count_nodes(data: &ActorData) -> usize {
    1 + data.children.iter().map(count_nodes).sum::<usize>()
}

/// メインカメラの Transform から、応答に載せる姿勢を作る（有限でなければ None）。
pub fn camera_pose(transform: &Transform) -> Option<CameraPose> {
    CameraPose::new_finite(transform.position, transform.rotation)
}

/// メインカメラの Transform から、写しのファイルに埋めるエディタ視点（debug_camera 節）を作る。
///
/// 写しは共有されないファイル（端末のキャッシュと PC のプロジェクトの cache/）なので、Play 用一時シーンと同じく
/// 視点を埋めてよい（scene.rs の SceneDataRef::debug_camera の規約）。エディタの編集用ランタイムで読み込むと、
/// デバッグカメラがメインカメラの位置と向きから始まる（エディタは加えて CAM_TRANSFORM でも当てる）。
/// 向きは Pause 時の同期（camera_ops.rs の sync_debug_camera_to_main_camera）と同じく前方向から yaw / pitch を出す。
///
/// # 引数
/// * `transform` - メインカメラの Transform（ワールド空間）
/// * `fov_deg` - メインカメラの縦の画角（度）
/// * `far` - 遠クリップ（いまのデバッグカメラの値を引き継ぐ）
/// * `speed` - 移動速度（いまのデバッグカメラの値を引き継ぐ）
pub fn debug_camera_from_main(transform: &Transform, fov_deg: f32, far: f32, speed: f32) -> Option<DebugCameraData> {
    let pose = camera_pose(transform)?;
    let [fx, fy, fz] = transform.forward();
    let pitch = (-fy).clamp(-1.0, 1.0).asin();
    let yaw = fx.atan2(fz);
    Some(DebugCameraData { position: pose.position, yaw, pitch, fov_deg, far, speed })
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::app_base::scene::Scene;

    /// 指定位置の Transform を持つアクタを作る。
    fn spawn(scene: &mut Scene, name: &str, position: [f32; 3], world_line: u32) -> Actor {
        let entity = scene.world.spawn();
        let mut transform = Transform::default();
        transform.position = position;
        scene.world.insert(entity, transform);
        let mut actor = Actor::new(entity, name);
        actor.world_line = world_line;
        actor
    }

    /// シーン本体のアクタ（スクリプトが生成したものを含む）は現在の Transform のまま入り、編集タブは入らないこと。
    #[test]
    fn collects_scene_actors_with_current_transform_and_excludes_edit_tabs() {
        let mut scene = Scene::new("snap");
        let mut group = spawn(&mut scene, "Group", [0.0, 0.0, 0.0], 0);
        group.add_child(spawn(&mut scene, "Child", [1.0, 2.0, 3.0], 0));
        scene.actors.push(group);
        // スクリプトの Instantiate はシーンのツリーへ world_line 0 で足される（script_scene_ops.rs）
        let spawned = spawn(&mut scene, "SpawnedFish", [5.0, -1.0, 7.5], 0);
        scene.actors.push(spawned);
        let edit_tab = spawn(&mut scene, "EditTab", [9.0, 9.0, 9.0], 1);
        scene.actors.push(edit_tab);

        // 生成した後に動いた（いまの値が書かれること）
        let fish = scene.actors[1].entity;
        scene.world.get_mut::<Transform>(fish).unwrap().position = [6.0, -1.0, 8.0];

        let out = collect_snapshot_actors(&scene.actors, &scene.world);
        let names: Vec<&str> = out.actors.iter().map(|a| a.name.as_str()).collect();
        assert_eq!(names, vec!["Group", "SpawnedFish"], "編集タブ（world_line > 0）は入らない");
        assert_eq!(out.saved, 3, "Group・Child・SpawnedFish");
        assert_eq!(out.skipped, 0);
        assert_eq!(out.actors[1].transform.as_ref().unwrap().position, [6.0, -1.0, 8.0], "いまの Transform が書かれる");
        assert_eq!(out.actors[0].children.len(), 1, "子もそのまま入る");
    }

    /// 保存できない（NaN の Transform）アクタは子孫ごと飛ばし、親や兄弟は残すこと。
    #[test]
    fn skips_unserializable_actor_but_keeps_parent_and_siblings() {
        let mut scene = Scene::new("snap");
        let mut group = spawn(&mut scene, "Group", [0.0, 0.0, 0.0], 0);
        let mut broken = spawn(&mut scene, "Broken", [f32::NAN, 0.0, 0.0], 0);
        broken.add_child(spawn(&mut scene, "UnderBroken", [0.0, 0.0, 0.0], 0));
        group.add_child(broken);
        group.add_child(spawn(&mut scene, "Sibling", [1.0, 1.0, 1.0], 0));
        scene.actors.push(group);

        let out = collect_snapshot_actors(&scene.actors, &scene.world);
        assert_eq!(out.actors.len(), 1, "親は残る");
        let children: Vec<&str> = out.actors[0].children.iter().map(|c| c.name.as_str()).collect();
        assert_eq!(children, vec!["Sibling"], "壊れたアクタだけが子孫ごと抜ける");
        assert_eq!(out.saved, 2, "Group・Sibling");
        assert_eq!(out.skipped, 2, "Broken とその子");
        assert_eq!(out.skipped_names, vec!["Broken".to_string()]);

        // 集めた結果はシーンの形式として書けて、JSON として読み戻せる（NaN は残っていない）
        let json = scene.to_json_with_actors(None, &out.actors).expect("書ける");
        let back: serde_json::Value = serde_json::from_str(&json).expect("読める");
        assert_eq!(back["actors"][0]["name"], "Group");
    }

    /// メインカメラの姿勢は位置と YXZ オイラー角（度）をそのまま載せ、エディタ視点は前方向から yaw / pitch を出すこと。
    #[test]
    fn camera_pose_and_debug_camera_follow_main_camera_transform() {
        let mut transform = Transform::default();
        transform.position = [1.0, 2.0, 3.0];
        transform.rotation = [30.0, 90.0, 0.0];
        let pose = camera_pose(&transform).expect("有限");
        assert_eq!(pose.position, [1.0, 2.0, 3.0]);
        assert_eq!(pose.rotation, [30.0, 90.0, 0.0]);

        let debug = debug_camera_from_main(&transform, 60.0, 500.0, 5.0).expect("有限");
        assert_eq!(debug.position, [1.0, 2.0, 3.0]);
        assert!((debug.yaw.to_degrees() - 90.0).abs() < 1e-3, "yaw = 90°: {}", debug.yaw.to_degrees());
        assert!((debug.pitch.to_degrees() - 30.0).abs() < 1e-3, "pitch = 30°: {}", debug.pitch.to_degrees());
        assert_eq!(debug.fov_deg, 60.0);

        transform.position = [f32::NAN, 0.0, 0.0];
        assert!(camera_pose(&transform).is_none(), "NaN の位置は載せない");
    }
}
