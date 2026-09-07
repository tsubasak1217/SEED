// ============================================================
//  scene_save_ops.rs — シーン保存のパス整合性ガード
//
//  【なぜ独立したファイルか】
//   「どのファイルへ書くか」の判断は .scene を失う／壊す唯一の分岐点であり、
//   IPC ディスパッチ（ipc_handler.rs）の巨大な match の中に埋めておくと
//   条件が増えたときに読めなくなる。保存の可否判定と実書き込みだけを
//   ここへ切り出し、単体テスト可能な純粋関数（scene_paths_equal）を伴わせる。
//
//  【守っている不変条件】
//   ・SAVE_SCENE は「ランタイムが実際に読み込んでいるシーン」のパスにしか書かない。
//     ずれていたら 1 バイトも書かずに SAVE_ERROR:path_mismatch:<実際のパス> を返す。
//   ・別名保存（SAVE_SCENE_AS）だけがパスを変更でき、成功後に読み込み中パスを更新する。
//   ・複製出力（SAVE_SCENE_COPY, Play 用一時シーン）は読み込み中パスを変更しない。
//   ・書き込みは safe_write 経由（.tmp → rename ＋ .backup へ世代バックアップ）。
// ============================================================

use std::path::Path;

use crate::engine::core::app_base::scene::DebugCameraData;

use super::App;

// ── 定数 ─────────────────────────────────────────────────────────

/// パス不一致で保存を拒否したときにエディタへ返す応答の接頭辞。
pub const SAVE_ERROR_PATH_MISMATCH: &str = "SAVE_ERROR:path_mismatch:";

/// 読み込み中のシーンが無いのに上書き保存を求められたときの応答。
pub const SAVE_ERROR_NO_SCENE: &str = "SAVE_ERROR:no_scene_loaded";

// ── 保存モード ───────────────────────────────────────────────────

/// シーン保存の 3 種類。呼び出し元の IPC コマンドと 1 対 1 で対応する。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum SceneSaveMode {
    /// 上書き保存（SAVE_SCENE）。読み込み中パスと一致しなければ拒否する。
    Overwrite,
    /// 別名保存（SAVE_SCENE_AS）。チェックせずに書き、読み込み中パスを更新する。
    SaveAs,
    /// 複製出力（SAVE_SCENE_COPY）。チェックせず、読み込み中パスも更新しない。
    Copy,
}

// ── パス比較（純粋関数・テスト対象）───────────────────────────────

/// 2 つのシーンパスが「同じファイルを指しているか」を判定する。
///
/// エディタとランタイムの間ではパスの表記が揺れる:
///   - 区切り文字が `/` と `\` で混在する
///   - 仮想パス（`assets://mainGame/MainGame.scene`）と絶対パスが混在する
///   - Windows なので大文字小文字は区別しない
///
/// これらを吸収したうえで比較する。片方でも空なら「不一致」とする
/// （不明なときに一致と答えると、まさに防ぎたい誤上書きを許してしまうため）。
pub fn scene_paths_equal(a: &str, b: &str) -> bool {
    if a.trim().is_empty() || b.trim().is_empty() {
        return false;
    }
    normalize_scene_path(a) == normalize_scene_path(b)
}

/// 比較用にシーンパスを正規化する（仮想パス解決 → 区切り統一 → 小文字化）。
pub fn normalize_scene_path(path: &str) -> String {
    let trimmed = path.trim().trim_matches('"');
    // 仮想パス（assets://）は実パスへ解決してから比較する。
    // 解決できない環境（アセットルート未初期化）でも文字列としては比較できる。
    let resolved = crate::engine::asset_fs::resolve(trimmed);
    let text = resolved.to_string_lossy().to_string();
    let text = if text.is_empty() {
        trimmed.to_string()
    } else {
        text
    };
    text.replace('\\', "/").to_lowercase()
}

// ── 保存処理 ─────────────────────────────────────────────────────

impl App {
    /// シーン保存 IPC の共通処理。可否を判定し、許されたときだけ実際に書き込む。
    ///
    /// 応答（`SAVE_OK` / `SAVE_ERROR:...`）の送信までここで完結させる。
    pub(super) fn handle_save_scene(&mut self, path: String, mode: SceneSaveMode) {
        // ── 1. 上書き保存はパス整合性を検査する ──────────────────
        if mode == SceneSaveMode::Overwrite {
            match self.loaded_scene_path.clone() {
                None => {
                    // 一度も読み込んでいない＝新規シーンの初回保存。
                    // 書いてよいが、以後の上書き保存の基準にするためパスを採用する。
                    self.write_scene_file(&path, true);
                    return;
                }
                Some(loaded) => {
                    if !scene_paths_equal(&loaded, &path) {
                        self.reject_save_path_mismatch(&path, &loaded);
                        return;
                    }
                }
            }
        }

        // ── 2. 書き込み ───────────────────────────────────────────
        let adopt = mode != SceneSaveMode::Copy;
        self.write_scene_file(&path, adopt);
    }

    /// 地形の実体をフラッシュしてからシーンを保存する（IPC ハンドラの入口）。
    ///
    /// 地形（.tvox / .tscatter / .tcover）は .scene の外に住んでいるため、ここで
    /// 書かないと「Ctrl+S したのに掘った地形・積もった雪が消える」ことになる。
    /// 書くのはダーティなチャンクだけなので、地形を触っていないセッションでは
    /// 1 バイトも触らない。.scene 本体より先に書き、最後の .scene 書き込みを確定操作にする。
    ///
    /// ただし **パス不一致で拒否されるときは地形も書かない**。保存操作そのものが
    /// 無効なので、副作用だけ残るのは避ける。
    pub(super) fn save_scene_with_terrain(&mut self, path: String, mode: SceneSaveMode) {
        if mode == SceneSaveMode::Overwrite {
            if let Some(loaded) = self.loaded_scene_path.clone() {
                if !scene_paths_equal(&loaded, &path) {
                    self.reject_save_path_mismatch(&path, &loaded);
                    return;
                }
            }
        }
        if let Err(e) = self.flush_dirty_terrain() {
            if let Some(ipc) = &self.ipc {
                ipc.send(&format!("TERRAIN_SAVE_ERROR:{e}"));
            }
        }
        self.handle_save_scene(path, mode);
    }

    /// パス不一致で保存を拒否したことをログとエディタへ通知する。
    fn reject_save_path_mismatch(&self, requested: &str, loaded: &str) {
        eprintln!(
            "[SEED SAVE] 保存を拒否しました: 要求先={requested} / 読み込み中={loaded} — \
             エディタが持っているシーンパスとランタイムの実体がずれています。\
             シーンを開き直してから保存してください。"
        );
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("{SAVE_ERROR_PATH_MISMATCH}{loaded}"));
        }
    }

    /// 実際に .scene を書き出す。`adopt` が true なら読み込み中パスを更新する。
    fn write_scene_file(&mut self, path: &str, adopt: bool) {
        let Some(scene) = &self.scene else {
            if let Some(ipc) = &self.ipc {
                ipc.send(SAVE_ERROR_NO_SCENE);
            }
            return;
        };

        let pos = self.camera.base.transform.position;
        let cam_data = DebugCameraData {
            position: [pos.x, pos.y, pos.z],
            yaw: self.camera.yaw,
            pitch: self.camera.pitch,
            fov_deg: self.camera.base.projection.fov_y_rad.to_degrees(),
            far: self.camera.base.projection.far,
            speed: self.camera.move_speed,
        };

        match scene.save(Path::new(path), &cam_data) {
            Ok(()) => {
                if adopt {
                    self.loaded_scene_path = Some(path.to_string());
                }
                if let Some(ipc) = &self.ipc {
                    ipc.send("SAVE_OK");
                }
            }
            Err(e) => {
                if let Some(ipc) = &self.ipc {
                    ipc.send(&format!("SAVE_ERROR:{e}"));
                }
            }
        }
    }

    /// シーン読み込み成功時に「読み込み中のシーンパス」を確定させる唯一の入口。
    /// エディタへ `SCENE_LOADED:<path>` を送るのもここに揃えるため、パスだけ返す。
    pub(super) fn set_loaded_scene_path(&mut self, path: &str) {
        self.loaded_scene_path = Some(path.to_string());
    }
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn same_path_with_different_separators_matches() {
        assert!(scene_paths_equal(
            r"C:\dev\assets\mainGame\MainGame.scene",
            "C:/dev/assets/mainGame/MainGame.scene"
        ));
    }

    #[test]
    fn case_differences_are_ignored_on_windows() {
        assert!(scene_paths_equal(
            "C:/dev/assets/MainGame.scene",
            "c:/DEV/Assets/maingame.scene"
        ));
    }

    #[test]
    fn different_scenes_do_not_match() {
        // 事故そのもの: 読み込み中は MainGame、保存先は proLogue
        assert!(!scene_paths_equal(
            "C:/dev/assets/mainGame/MainGame.scene",
            "C:/dev/assets/prologue/proLogue.scene"
        ));
    }

    #[test]
    fn prefix_collision_does_not_match() {
        assert!(!scene_paths_equal(
            "C:/dev/assets/MainGame.scene",
            "C:/dev/assets/MainGame2.scene"
        ));
    }

    #[test]
    fn empty_paths_never_match() {
        assert!(!scene_paths_equal("", ""));
        assert!(!scene_paths_equal("C:/a.scene", ""));
        assert!(!scene_paths_equal("   ", "C:/a.scene"));
    }

    #[test]
    fn quoted_path_matches_unquoted() {
        assert!(scene_paths_equal("\"C:/a/b.scene\"", "C:/a/b.scene"));
    }
}
