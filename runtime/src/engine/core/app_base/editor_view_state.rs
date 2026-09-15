// ============================================================
//  editor_view_state.rs — エディタ視点（デバッグカメラの位置・向き）のユーザー別サイドカー
//
//  【なぜ独立したファイルか】
//  以前は「Edit のフリーカメラをどこから見ていたか」を `.scene` のトップレベル
//  `debug_camera` 節へ書いていた。これは **人ごとに必ず違う値** なので、
//  シーンを保存するたびに差分が出て、複数人開発では毎回コンフリクトした。
//  しかも同じ節に fov / far / speed も入っており、こちらは `settings.debug_camera`
//  （`scene_settings::DebugCameraSettings`）と二重管理になっていた。
//
//  そこで **共有する設定（fov/far/speed/ortho_2d）は `settings` に一本化**し、
//  **共有しない視点（position/yaw/pitch）はこのモジュールが扱うサイドカー**へ分離した。
//  「どこに置くか」「どう名前を付けるか」「どちらを優先するか」の判断を 1 か所へ
//  集めることで、保存経路（`app/scene_save_ops.rs`）と読み込み経路（`scene.rs::load`）が
//  規約を取り違える余地を無くす。
//
//  【置き場】
//  ```text
//  <プロジェクト>/cache/editor/view/<アセットルート相対のシーンパス>.view.json
//     例) assets/mainGame/MainGame.scene
//         → cache/editor/view/mainGame/MainGame.view.json
//  ```
//  `cache/` の決め方はモデル変換キャッシュ（`.smdl`）・サムネイルと同じ
//  `loader::asset_cache::cache_dir()` を使う。置き場の知識を 2 か所に持たないため。
//  `cache/` は `.gitignore` 済み（`/cache/` と `projects/*/cache/`）なので共有されない。
//
//  【書かない条件】
//  - パッケージ実行（`asset_fs::is_packaged()`）… 配布物にエディタ視点は不要。
//  - キャッシュ置き場が決められない（アセットルート未初期化など）… 1 回だけ警告して諦める。
//  - シーンがアセットルートの外（Play 用一時シーン `%TEMP%\SEED\_play_temp.scene` など）
//    … 相対キーを作れないので黙ってスキップする（通常運用で起きる正常系）。
//
//  【バックアップを取らない理由】
//  サイドカーはユーザー別のキャッシュであり、失っても「視点が既定に戻る」だけで
//  作品のデータは 1 バイトも失われない。`.scene` と同じ `.backup/` 世代管理を掛けると
//  保存のたびにゴミが増えるだけなので、原子的置換（tmp → rename）だけを使う。
// ============================================================

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};

use serde::{Deserialize, Serialize};

use crate::engine::core::app_base::scene::DebugCameraData;

// ============================================================
//  定数（フォルダ名・拡張子）
// ============================================================

/// キャッシュ直下に作る「エディタ専用の状態」フォルダ名。
///
/// モデルキャッシュ（`*.smdl`）やサムネイル（`thumbnails/`）と同じ `cache/` の中で、
/// 「ランタイムの派生データ」と「エディタのセッション状態」を混ぜないための 1 段目。
pub const VIEW_STATE_ROOT_DIR: &str = "editor";

/// エディタ状態フォルダの中で視点サイドカーを置くフォルダ名（2 段目）。
pub const VIEW_STATE_DIR: &str = "view";

/// 視点サイドカーの拡張子。シーンファイルの拡張子（`.scene`）をこれに差し替える。
pub const VIEW_STATE_EXT: &str = ".view.json";

/// 「キャッシュ置き場が決められない」警告を出したかどうか。
///
/// 毎フレーム出るような経路ではないが、シーンを開き直すたびに同じ行が並ぶのは
/// ログのノイズにしかならないのでプロセス内で 1 回に絞る。
static CACHE_DIR_WARNED: AtomicBool = AtomicBool::new(false);

// ============================================================
//  EditorViewState — サイドカーの中身
// ============================================================

/// エディタ視点（デバッグカメラの位置・向き）だけを持つユーザー別の保存データ。
///
/// `DebugCameraData` から fov / far / speed を落とした部分集合。
/// それらは共有設定（`.scene` の `settings.debug_camera`）が持つため、ここには入れない。
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq)]
pub struct EditorViewState {
    /// カメラのワールド座標。
    #[serde(default = "default_position")]
    pub position: [f32; 3],
    /// 水平回転（ラジアン）。規約は `DebugCamera` と同じ。
    #[serde(default)]
    pub yaw: f32,
    /// 垂直回転（ラジアン）。規約は `DebugCamera` と同じ。
    #[serde(default)]
    pub pitch: f32,
}

/// `position` の既定値（デバッグカメラの既定位置と必ず一致させる）。
fn default_position() -> [f32; 3] {
    DebugCameraData::default().position
}

impl Default for EditorViewState {
    fn default() -> Self {
        let d = DebugCameraData::default();
        Self { position: d.position, yaw: d.yaw, pitch: d.pitch }
    }
}

impl EditorViewState {
    /// デバッグカメラの保存データから「視点」だけを抜き出す【純関数】。
    pub fn from_camera(cam: &DebugCameraData) -> Self {
        Self { position: cam.position, yaw: cam.yaw, pitch: cam.pitch }
    }
}

// ============================================================
//  パス変換（純関数・テスト対象）
// ============================================================

/// シーンパスを「アセットルート相対のスラッシュ区切りパス」へ直す【純関数】。
///
/// シーンパスの表記は出所によって 2 系統ある:
///   1. 仮想パス `assets://mainGame/MainGame.scene`（Play 起動・スクリプト遷移）
///   2. 絶対パス `D:\proj\assets\mainGame\MainGame.scene`（エディタからの IPC）
///
/// どちらもアセットルート基準の `mainGame/MainGame.scene` へ寄せる。
/// アセットルート配下でない絶対パス（`%TEMP%` の Play 用一時シーンなど）は
/// 相対キーを作れないので `None`（＝サイドカーを扱わない）。
///
/// # 引数
/// * `scene_path`  - シーンパス（仮想パス or 絶対パス）
/// * `assets_root` - アセットルートの絶対パス（未初期化なら `None`）
fn to_asset_relative(scene_path: &str, assets_root: Option<&Path>) -> Option<String> {
    // エディタ／IPC 経由の文字列は前後に空白や引用符が付くことがあるので落とす。
    let trimmed = scene_path.trim().trim_matches('"');
    if trimmed.is_empty() {
        return None;
    }

    // ── 1. 仮想パスはスキームを外すだけ ──
    if let Some(rel) = trimmed.strip_prefix(crate::engine::asset_fs::ASSETS_SCHEME) {
        return Some(rel.replace('\\', "/"));
    }

    // ── 2. 絶対パスはアセットルートを前方一致で剥がす ──
    //   Windows は大文字小文字を区別しないので、比較だけ ASCII 無視で行う
    //   （日本語フォルダ名は大小の概念が無いのでこれで足りる）。
    let root = assets_root?;
    let abs_norm = trimmed.replace('\\', "/");
    let root_norm = root.to_string_lossy().replace('\\', "/");
    let prefix = format!("{}/", root_norm.trim_end_matches('/'));
    let head = abs_norm.get(..prefix.len())?;
    if !head.eq_ignore_ascii_case(&prefix) {
        return None;
    }
    let rel = &abs_norm[prefix.len()..];
    if rel.is_empty() {
        return None;
    }
    Some(rel.to_string())
}

/// シーンパス → サイドカーの「キャッシュ内相対パス」を求める【純関数】。
///
/// 例: `assets://mainGame/MainGame.scene` → `mainGame/MainGame.view.json`
///
/// キャッシュフォルダの外へ書かせないため、`..` や空セグメントを含むパスは拒否する
/// （シーンパスは基本的に信頼できるが、ここが唯一「外部文字列からファイルパスを組み立てる」
/// 場所なので、素通しにはしない）。
///
/// # 戻り値
/// 相対パス。相対化できない／安全でないときは `None`。
pub fn sidecar_rel_path(scene_path: &str, assets_root: Option<&Path>) -> Option<String> {
    let rel = to_asset_relative(scene_path, assets_root)?;
    let rel = rel.trim_start_matches('/');
    if rel.is_empty() {
        return None;
    }
    // パス脱出・空セグメントの拒否
    if rel
        .split('/')
        .any(|seg| seg.is_empty() || seg == "." || seg == "..")
    {
        return None;
    }
    Some(replace_ext_with_view(rel))
}

/// ファイル名の拡張子を `.view.json` へ差し替える【純関数】。
///
/// 先頭ドットだけのファイル名（`.gitignore` のような隠しファイル）は拡張子扱いしない。
fn replace_ext_with_view(rel: &str) -> String {
    // ディレクトリ部とファイル名部に分ける（`/` は上位で正規化済み）
    let cut = rel.rfind('/').map(|i| i + 1).unwrap_or(0);
    let (dir, file) = rel.split_at(cut);
    let stem = match file.rfind('.') {
        // 先頭のドットは拡張子の区切りではない（`.scene` という名前のファイル等）
        Some(0) | None => file,
        Some(i) => &file[..i],
    };
    format!("{dir}{stem}{VIEW_STATE_EXT}")
}

/// キャッシュフォルダ + 相対パス → サイドカーの絶対パスを組み立てる【純関数】。
pub fn sidecar_path_in(cache_dir: &Path, rel: &str) -> PathBuf {
    cache_dir
        .join(VIEW_STATE_ROOT_DIR)
        .join(VIEW_STATE_DIR)
        .join(rel)
}

/// シーン内容とサイドカーから「実際に適用するデバッグカメラ」を決める【純関数】。
///
/// 優先順位（要件そのもの）:
///   1. サイドカー … あれば position / yaw / pitch を採用する
///   2. `.scene` のトップレベル `debug_camera` … 旧シーンの後方互換
///   3. どちらも無ければ `None`（＝カメラに触らない＝起動時の既定位置のまま）
///
/// fov / far / speed は **サイドカーが持たない**ので、旧シーンの値があればそれを
/// 土台にして視点だけを差し替える。こうすると `settings` 節を持たない旧シーンでも
/// 従来と同じ画角・速度で開ける（`settings` 節があれば直後に
/// `App::apply_scene_settings` が上書きするので、どちらの経路でも辻褄が合う）。
pub fn merge_into_camera(
    from_scene: Option<DebugCameraData>,
    view: Option<EditorViewState>,
) -> Option<DebugCameraData> {
    let Some(view) = view else {
        // サイドカーが無ければ従来どおり .scene の値（それも無ければ None）。
        return from_scene;
    };
    let mut cam = from_scene.unwrap_or_default();
    cam.position = view.position;
    cam.yaw = view.yaw;
    cam.pitch = view.pitch;
    Some(cam)
}

// ============================================================
//  ファイル I/O（環境依存。純関数へ環境を注入するだけの薄い層）
// ============================================================

/// シーンパスからサイドカーの絶対パスを決める（決められなければ `None`）。
///
/// 環境（パッケージ判定・アセットルート・キャッシュ置き場）を集めて純関数へ渡す。
fn sidecar_path(scene_path: &str) -> Option<PathBuf> {
    // パッケージ実行ではエディタ視点という概念が無い。読み書きとも行わない。
    if crate::engine::asset_fs::is_packaged() {
        return None;
    }
    // アセットルート配下でないシーン（Play 用一時シーンなど）は対象外。
    // 通常運用で起きる正常系なのでログは出さない。
    let assets_root = crate::engine::asset_fs::root().map(PathBuf::as_path);
    let rel = sidecar_rel_path(scene_path, assets_root)?;

    // キャッシュ置き場はモデルキャッシュ・サムネイルと同一の決め方を使う。
    let Some(cache_dir) = crate::engine::core::loader::asset_cache::cache_dir() else {
        // ここへ来るのは異常（アセットルート未初期化）なので 1 回だけ知らせる。
        if !CACHE_DIR_WARNED.swap(true, Ordering::Relaxed) {
            eprintln!(
                "[SEED VIEW] キャッシュ置き場を決められないため、エディタ視点の保存／復元を行いません \
                 （アセットルート未初期化の可能性）"
            );
        }
        return None;
    };
    Some(sidecar_path_in(&cache_dir, &rel))
}

/// エディタ視点をサイドカーへ書き出す。
///
/// 失敗しても **呼び出し元の保存は成功扱いのまま**にする（視点はあくまで利便性であり、
/// 書けなかったからといって `.scene` の保存を失敗と報告するのは筋が違う）。
/// `.backup/` 世代は作らず、原子的置換（tmp → rename）のみを使う。
///
/// # 引数
/// * `scene_path` - 保存した `.scene` のパス（仮想パス or 絶対パス）
/// * `view`       - 書き出す視点
pub fn save_for_scene(scene_path: &str, view: &EditorViewState) {
    let Some(path) = sidecar_path(scene_path) else {
        return;
    };
    save_to(&path, view);
}

/// 指定した絶対パスへサイドカーを書き出す（置き場の判断を含まない実書き込み）。
///
/// 置き場の決定（環境依存）と書き込み（ファイル I/O）を分けてあるので、
/// テストから一時ディレクトリを渡して実際の書き込み経路を検証できる。
/// 親フォルダは `safe_write::write_atomic` が `create_dir_all` する。
fn save_to(path: &Path, view: &EditorViewState) {
    let json = match serde_json::to_string_pretty(view) {
        Ok(s) => s,
        Err(e) => {
            eprintln!("[SEED VIEW] エディタ視点の直列化に失敗しました: {e}");
            return;
        }
    };
    if let Err(e) = crate::engine::core::app_base::safe_write::write_atomic(path, json.as_bytes()) {
        eprintln!(
            "[SEED VIEW] エディタ視点の保存に失敗しました: {} — {e}",
            path.display()
        );
    }
}

/// シーンに対応するサイドカーを読み込む（無い・壊れているときは `None`）。
///
/// 「ファイルが無い」は正常系（初めて開くシーン・他の人が作ったシーン）なので黙って `None`。
/// 「あるのに読めない」だけログへ出す（手で壊した・別バージョンのゴミが残っている）。
pub fn load_for_scene(scene_path: &str) -> Option<EditorViewState> {
    let path = sidecar_path(scene_path)?;
    load_from(&path)
}

/// 指定した絶対パスからサイドカーを読む（置き場の判断を含まない実読み込み）。
fn load_from(path: &Path) -> Option<EditorViewState> {
    let text = std::fs::read_to_string(path).ok()?;
    // 外部ツールが付けることのある BOM は許容する（.scene 読み込みと同じ扱い）。
    let json = text.strip_prefix('\u{FEFF}').unwrap_or(&text);
    match serde_json::from_str::<EditorViewState>(json) {
        Ok(view) => Some(view),
        Err(e) => {
            eprintln!(
                "[SEED VIEW] エディタ視点サイドカーを読めませんでした（既定の視点で開きます）: {} — {e}",
                path.display()
            );
            None
        }
    }
}

// ============================================================
//  ユニットテスト（パス変換・往復・優先順位はすべて純関数なので完全に検証できる）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用のアセットルート。
    fn root() -> PathBuf {
        PathBuf::from(r"D:\SEED_projects\WarashibeFishing\assets")
    }

    // ── パス変換 ────────────────────────────────────────────────

    /// 仮想パスは拡張子だけが `.view.json` へ変わる（要件の例そのもの）。
    #[test]
    fn virtual_path_becomes_view_json() {
        assert_eq!(
            sidecar_rel_path("assets://mainGame/MainGame.scene", None).as_deref(),
            Some("mainGame/MainGame.view.json")
        );
    }

    /// 絶対パスはアセットルートを剥がしてから変換される（区切りは `\` でもよい）。
    #[test]
    fn absolute_path_is_relativized_against_assets_root() {
        let p = r"D:\SEED_projects\WarashibeFishing\assets\mainGame\MainGame.scene";
        assert_eq!(
            sidecar_rel_path(p, Some(&root())).as_deref(),
            Some("mainGame/MainGame.view.json")
        );
    }

    /// Windows なのでアセットルートの大文字小文字は区別しない。
    #[test]
    fn absolute_path_matches_root_case_insensitively() {
        let p = r"d:\seed_projects\warashibefishing\ASSETS\prologue\proLogue.scene";
        assert_eq!(
            sidecar_rel_path(p, Some(&root())).as_deref(),
            Some("prologue/proLogue.view.json")
        );
    }

    /// アセットルートの外（Play 用一時シーン）は対象外。
    #[test]
    fn scene_outside_assets_root_has_no_sidecar() {
        let p = r"C:\Users\me\AppData\Local\Temp\SEED\_play_temp.scene";
        assert_eq!(sidecar_rel_path(p, Some(&root())), None);
        // アセットルートが未初期化なら絶対パスは相対化できない
        assert_eq!(sidecar_rel_path(p, None), None);
    }

    /// 空文字・引用符だけ・ルートそのものは `None`。
    #[test]
    fn degenerate_paths_yield_none() {
        assert_eq!(sidecar_rel_path("", Some(&root())), None);
        assert_eq!(sidecar_rel_path("   ", Some(&root())), None);
        assert_eq!(sidecar_rel_path("assets://", None), None);
        assert_eq!(
            sidecar_rel_path(r"D:\SEED_projects\WarashibeFishing\assets", Some(&root())),
            None
        );
    }

    /// `..` を含むパスはキャッシュの外を指しうるので拒否する。
    #[test]
    fn path_traversal_is_rejected() {
        assert_eq!(sidecar_rel_path("assets://../../evil.scene", None), None);
        assert_eq!(sidecar_rel_path("assets://a/../b.scene", None), None);
        assert_eq!(sidecar_rel_path("assets://a//b.scene", None), None);
    }

    /// 引用符付き・区切り混在でも同じキーになる（IPC 文字列の揺れ吸収）。
    #[test]
    fn quoted_and_mixed_separators_normalize() {
        assert_eq!(
            sidecar_rel_path("\"assets://ui\\menu\\Title.scene\"", None).as_deref(),
            Some("ui/menu/Title.view.json")
        );
    }

    /// 拡張子が無い／先頭ドットのファイル名でも壊れない。
    #[test]
    fn extension_replacement_edge_cases() {
        assert_eq!(replace_ext_with_view("a/b"), "a/b.view.json");
        assert_eq!(replace_ext_with_view(".scene"), ".scene.view.json");
        assert_eq!(replace_ext_with_view("a.b/c.scene"), "a.b/c.view.json");
    }

    /// 絶対パスの組み立ては `cache/editor/view/<相対>`。
    #[test]
    fn sidecar_absolute_path_layout() {
        let cache = Path::new(r"D:\SEED_projects\WarashibeFishing\cache");
        let p = sidecar_path_in(cache, "mainGame/MainGame.view.json");
        // 区切り文字は OS 依存なので、比較も PathBuf 同士で行う
        assert_eq!(
            p,
            cache
                .join("editor")
                .join("view")
                .join("mainGame")
                .join("MainGame.view.json")
        );
    }

    // ── JSON 往復 ───────────────────────────────────────────────

    /// 直列化 → 逆直列化で値が保たれること。
    #[test]
    fn json_roundtrip_preserves_view() {
        let v = EditorViewState { position: [1.5, -2.0, 30.25], yaw: 0.75, pitch: -0.25 };
        let json = serde_json::to_string_pretty(&v).unwrap();
        let back: EditorViewState = serde_json::from_str(&json).unwrap();
        assert_eq!(back, v);
    }

    /// キーが欠けた古い／手書きのサイドカーでも読めること（`serde(default)` の確認）。
    #[test]
    fn missing_keys_fall_back_to_defaults() {
        let back: EditorViewState = serde_json::from_str("{}").unwrap();
        assert_eq!(back, EditorViewState::default());

        let back: EditorViewState = serde_json::from_str(r#"{"yaw": 1.0}"#).unwrap();
        assert_eq!(back.yaw, 1.0);
        assert_eq!(back.position, DebugCameraData::default().position);
    }

    // ── 優先順位 ────────────────────────────────────────────────

    /// サイドカーがあれば視点だけを上書きし、fov/far/speed は `.scene` の値を残す。
    #[test]
    fn sidecar_overrides_only_the_viewpoint() {
        let scene_cam = DebugCameraData {
            position: [9.0, 9.0, 9.0],
            yaw: 9.0,
            pitch: 9.0,
            fov_deg: 60.0,
            far: 500.0,
            speed: 12.0,
        };
        let view = EditorViewState { position: [1.0, 2.0, 3.0], yaw: 0.5, pitch: -0.5 };

        let merged = merge_into_camera(Some(scene_cam), Some(view)).expect("値が返ること");
        assert_eq!(merged.position, [1.0, 2.0, 3.0]);
        assert_eq!(merged.yaw, 0.5);
        assert_eq!(merged.pitch, -0.5);
        assert_eq!(merged.fov_deg, 60.0, "画角は .scene 側（旧シーン互換）");
        assert_eq!(merged.far, 500.0);
        assert_eq!(merged.speed, 12.0);
    }

    /// サイドカーだけのとき（新形式の .scene）は既定値を土台に視点を載せる。
    #[test]
    fn sidecar_without_scene_block_uses_defaults_for_rest() {
        let view = EditorViewState { position: [1.0, 2.0, 3.0], yaw: 0.5, pitch: -0.5 };
        let merged = merge_into_camera(None, Some(view)).expect("値が返ること");
        let d = DebugCameraData::default();
        assert_eq!(merged.position, [1.0, 2.0, 3.0]);
        assert_eq!(merged.fov_deg, d.fov_deg, "fov は既定（settings 節が後で上書きする）");
        assert_eq!(merged.far, d.far);
        assert_eq!(merged.speed, d.speed);
    }

    /// サイドカーが無ければ `.scene` の値をそのまま使う（旧シーンの後方互換）。
    #[test]
    fn without_sidecar_scene_block_is_used_as_is() {
        let scene_cam = DebugCameraData {
            position: [9.0, 9.0, 9.0],
            yaw: 9.0,
            pitch: 9.0,
            fov_deg: 60.0,
            far: 500.0,
            speed: 12.0,
        };
        let merged = merge_into_camera(Some(scene_cam.clone()), None).expect("値が返ること");
        assert_eq!(merged.position, scene_cam.position);
        assert_eq!(merged.yaw, scene_cam.yaw);
        assert_eq!(merged.fov_deg, 60.0);
    }

    /// どちらも無ければ `None`（＝カメラに触らない）。
    #[test]
    fn without_anything_camera_is_left_untouched() {
        assert!(merge_into_camera(None, None).is_none());
    }

    // ── 実ファイル I/O（置き場の決定だけを外から与える）──────────────

    /// 「シーンパス → 相対キー → 絶対パス → 書き込み → 読み戻し」を実ファイルで通す。
    ///
    /// 純関数の検証だけでは「深いフォルダを実際に掘れるか」「書いたものが読めるか」
    /// が分からない。環境依存なのはキャッシュ置き場の決定だけなので、そこを
    /// 一時ディレクトリで差し替えて、残りの経路をそのまま実行する。
    #[test]
    fn write_then_read_back_through_real_files() {
        // 一時キャッシュフォルダ（プロセス ID で衝突を避ける）
        let cache = std::env::temp_dir().join(format!("seed_view_state_test_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&cache);

        let rel = sidecar_rel_path("assets://mainGame/MainGame.scene", None).expect("相対キー");
        let path = sidecar_path_in(&cache, &rel);

        let view = EditorViewState { position: [59.0, 9.96, -13.93], yaw: -0.19, pitch: 0.02 };
        save_to(&path, &view);

        // 置き場が要件どおり `cache/editor/view/<相対>` になっていること
        assert!(path.exists(), "サイドカーが作られていない: {}", path.display());
        assert!(
            path.ends_with(Path::new("editor").join("view").join("mainGame").join("MainGame.view.json")),
            "置き場が規約とずれている: {}",
            path.display()
        );
        // 原子的置換の一時ファイルが残っていないこと
        assert!(!crate::engine::core::app_base::safe_write::temp_path_for(&path).exists());

        // 読み戻して一致すること
        assert_eq!(load_from(&path), Some(view));

        // 上書き（2 回目の保存）も通ること
        let view2 = EditorViewState { position: [0.0, 1.0, 2.0], yaw: 1.0, pitch: -1.0 };
        save_to(&path, &view2);
        assert_eq!(load_from(&path), Some(view2));

        // 壊れた内容は None（＝既定の視点で開く）
        std::fs::write(&path, b"{ this is not json").unwrap();
        assert_eq!(load_from(&path), None);

        // 存在しないファイルも None
        assert_eq!(load_from(&cache.join("nope.view.json")), None);

        let _ = std::fs::remove_dir_all(&cache);
    }
}
