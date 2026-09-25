// ============================================================
//  scene_snapshot_ops.rs — SNAPSHOT_SCENE: いまの世界を既存のシーン形式で書き出す（docs/android.md §20.17・§21.13）
//
//  【何をするか】
//  Android の端末で一時停止したゲーム（Play 中。PC の Play・Edit でも動く）の「いまの状態」を .scene として書く:
//    ・スクリプトが生成したアクタを含む全アクタ・現在の Transform とコンポーネントの値（scene_snapshot/collect.rs）
//    ・保存できないアクタ（値が NaN 等）は子孫ごと飛ばして続け、数を応答に載せる（名前はログ [SEED SNAPSHOT]）
//    ・メインカメラの位置と向きを応答（cam=）とファイルの debug_camera 節に載せる（エディタのデバッグカメラの始点）
//  【しないこと】
//    ・読み込み中のシーンのパス（loaded_scene_path）は変えない（SAVE_SCENE_COPY と同じ「複製」）
//    ・地形のファイル（.tvox 等）は書かない（SAVE_SCENE_COPY と違い flush_dirty_terrain を呼ばない。端末のファイルも
//      プロジェクトのファイルも触らない。写しの地形は読み込む側がプロジェクトの地形フォルダから読む）
//    ・書き先は .scene の絶対パスだけ（ほかのファイルを取り違えて上書きしない。scene_snapshot/wire.rs）
//  応答の書式は scene_snapshot/wire.rs。写しを PC で閲覧専用に出す側は app/snapshot_view_ops.rs。
// ============================================================

use std::path::Path;
use std::time::Instant;

use crate::engine::core::app_base::scene_snapshot::{collect, wire, SnapshotCommand};

use super::App;

/// ログの印（logcat の SEED タグ・PC の標準エラーで探しやすくする）。
const SNAPSHOT_LOG_PREFIX: &str = "[SEED SNAPSHOT]";

/// 書きかけのファイルの拡張子（書き切ってから名前を変える＝読む側が途中のファイルを読まない）。
const TEMP_EXTENSION: &str = "tmp";

/// 書き出した写しのまとめ（応答とログ用）。
struct SnapshotSummary {
    /// 書いたアクタの数（子を含む）。
    saved: usize,
    /// 飛ばしたアクタの数（子を含む）。
    skipped: usize,
    /// 飛ばしたアクタの名前（先頭の数件）。
    skipped_names: Vec<String>,
    /// メインカメラの姿勢（無ければ None）。
    camera: Option<wire::CameraPose>,
    /// 書いたバイト数。
    bytes: usize,
}

impl App {
    /// 写しの命令（SNAPSHOT_SCENE / SNAPSHOT_VIEW_BEGIN / SNAPSHOT_VIEW_END）を振り分ける（ipc_handler.rs から）。
    pub(super) fn handle_snapshot_command(&mut self, command: SnapshotCommand) {
        match command {
            SnapshotCommand::Snapshot { path } => self.write_scene_snapshot(&path),
            SnapshotCommand::ViewBegin { path, allow_file_restore } => self.begin_snapshot_view(&path, allow_file_restore),
            SnapshotCommand::ViewEnd => self.end_snapshot_view(),
        }
    }

    /// いまの世界を `path` へ書き出して応答する（SNAPSHOT_DONE / SNAPSHOT_FAILED）。
    fn write_scene_snapshot(&mut self, path: &str) {
        let started = Instant::now();
        let reply = match self.build_and_write_snapshot(path) {
            Ok(summary) => {
                let elapsed_ms = started.elapsed().as_secs_f64() * 1000.0;
                eprintln!(
                    "{SNAPSHOT_LOG_PREFIX} 写しを書き出しました: {path}（アクタ {}・飛ばした {}・{} バイト・{elapsed_ms:.1} ms・メインカメラ {}）",
                    summary.saved,
                    summary.skipped,
                    summary.bytes,
                    if summary.camera.is_some() { "あり" } else { "なし" },
                );
                if summary.skipped > 0 {
                    eprintln!(
                        "{SNAPSHOT_LOG_PREFIX} 保存できないため飛ばしたアクタ（子孫ごと）: {}",
                        summary.skipped_names.join(", ")
                    );
                }
                wire::format_done(path, summary.saved, summary.skipped, elapsed_ms, summary.camera.as_ref())
            }
            Err(reason) => {
                eprintln!("{SNAPSHOT_LOG_PREFIX} 写しを書き出せませんでした: {path} — {reason}");
                wire::format_failed(path, &reason)
            }
        };
        if let Some(ipc) = &self.ipc {
            ipc.send(&reply);
        }
    }

    /// 写しを組み立てて書く（書き先の確かめ → アクタを集める → メインカメラ → 直列化 → 書き込み）。
    fn build_and_write_snapshot(&self, path: &str) -> Result<SnapshotSummary, String> {
        wire::validate_output_path(path)?;
        let scene = self.scene.as_ref().ok_or_else(|| "シーンが読み込まれていません".to_string())?;
        let collected = collect::collect_snapshot_actors(&scene.actors, &scene.world);

        // メインカメラ（Transform はワールド空間）。応答の姿勢と、ファイルに埋めるエディタ視点
        let main_camera = scene.find_main_camera();
        let camera = main_camera.as_ref().and_then(|(transform, _)| collect::camera_pose(transform));
        let embedded_view = main_camera.as_ref().and_then(|(transform, data)| {
            collect::debug_camera_from_main(
                transform,
                data.fov_y_deg,
                self.camera.base.projection.far,
                self.camera.move_speed,
            )
        });

        let json = scene
            .to_json_with_actors(embedded_view.as_ref(), &collected.actors)
            .map_err(|e| format!("直列化に失敗しました: {e}"))?;
        write_file_atomically(Path::new(path), &json)?;
        Ok(SnapshotSummary {
            saved: collected.saved,
            skipped: collected.skipped,
            skipped_names: collected.skipped_names,
            camera,
            bytes: json.len(),
        })
    }
}

/// テキストを書きかけのファイルへ書き切ってから名前を変える（読む側が途中のファイルを読まない。フォルダが無ければ作る）。
///
/// 写しは共有されないキャッシュなので、シーンの保存（safe_write）のような世代のバックアップは取らない。
fn write_file_atomically(path: &Path, text: &str) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        if !parent.as_os_str().is_empty() {
            std::fs::create_dir_all(parent).map_err(|e| format!("フォルダを作れません: {} — {e}", parent.display()))?;
        }
    }
    let temp = path.with_extension(TEMP_EXTENSION);
    std::fs::write(&temp, text).map_err(|e| format!("書き込めません: {} — {e}", temp.display()))?;
    std::fs::rename(&temp, path).map_err(|e| {
        // 名前を変えられなければ書きかけを残さない
        let _ = std::fs::remove_file(&temp);
        format!("書き込めません: {} — {e}", path.display())
    })
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::{CameraComponent, ComponentKind, Transform};
    use crate::engine::core::app_base::scene::Scene;
    use crate::engine::structs::objects::Actor;

    /// 写しのファイル（build_and_write_snapshot と同じ手順: 集める → メインカメラ → 直列化 → 書く）に、
    /// スクリプトが生成したアクタのいまの Transform とメインカメラの視点が入り、応答の書式が正しいこと。
    /// App（GPU）を使わずに同じ部品を同じ順で通す。
    #[test]
    fn snapshot_file_contains_spawned_actor_and_camera() {
        let mut scene = Scene::new("Main");
        // メインカメラ（ワールド位置と向き）
        let cam_entity = scene.world.spawn();
        let mut cam_transform = Transform::default();
        cam_transform.position = [0.0, 5.0, -10.0];
        cam_transform.rotation = [20.0, 0.0, 0.0];
        scene.world.insert(cam_entity, cam_transform);
        let mut camera_actor = Actor::new(cam_entity, "MainCamera");
        let slot = scene.world.spawn();
        let mut camera = CameraComponent::default();
        camera.is_main = true;
        scene.world.insert(slot, camera);
        camera_actor.add_slot_typed::<CameraComponent>("Camera", ComponentKind::Camera, slot);
        scene.actors.push(camera_actor);
        // スクリプトが Instantiate したアクタ（シーンのツリーへ world_line 0 で入る）のいまの位置
        let fish = scene.world.spawn();
        let mut fish_transform = Transform::default();
        fish_transform.position = [3.0, -2.0, 4.5];
        scene.world.insert(fish, fish_transform);
        scene.actors.push(Actor::new(fish, "Fish(Clone)"));

        let collected = collect::collect_snapshot_actors(&scene.actors, &scene.world);
        let (main_transform, main_data) = scene.find_main_camera().expect("メインカメラ");
        let pose = collect::camera_pose(&main_transform).expect("有限");
        let view = collect::debug_camera_from_main(&main_transform, main_data.fov_y_deg, 1000.0, 5.0);
        let json = scene.to_json_with_actors(view.as_ref(), &collected.actors).expect("直列化");

        let dir = std::env::temp_dir().join(format!("seed_snapshot_file_{}", std::process::id()));
        let path = dir.join("paused.scene");
        write_file_atomically(&path, &json).expect("書ける");

        let back: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        let actors = back["actors"].as_array().expect("actors");
        let fish_json = actors.iter().find(|a| a["name"] == "Fish(Clone)").expect("生成したアクタが入る");
        assert_eq!(fish_json["transform"]["position"], serde_json::json!([3.0, -2.0, 4.5]), "いまの位置");
        assert_eq!(back["debug_camera"]["position"], serde_json::json!([0.0, 5.0, -10.0]), "視点はメインカメラから");

        let reply = wire::format_done(&path.to_string_lossy(), collected.saved, collected.skipped, 12.34, Some(&pose));
        assert!(reply.starts_with(wire::SNAPSHOT_DONE_PREFIX));
        assert!(reply.ends_with("|actors=2|skipped=0|ms=12.3|cam=0,5,-10,20,0,0"), "応答: {reply}");
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// 書きかけのファイルは残らず、フォルダが無ければ作り、上書きもできること。
    #[test]
    fn writes_atomically_and_overwrites() {
        let dir = std::env::temp_dir().join(format!("seed_snapshot_write_{}", std::process::id()));
        let path = dir.join("nested").join("snap.scene");
        write_file_atomically(&path, "first").expect("書ける");
        write_file_atomically(&path, "second").expect("上書きできる");
        assert_eq!(std::fs::read_to_string(&path).unwrap(), "second");
        assert!(!path.with_extension(TEMP_EXTENSION).exists(), "書きかけのファイルは残らない");
        let _ = std::fs::remove_dir_all(&dir);
    }
}
