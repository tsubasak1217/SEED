/// `.actor` / `.actor2d` ファイルの読み書き（版の変換・刻印を含む唯一の経路）
pub mod actor_file;
pub mod app;
/// エディタ視点（デバッグカメラの位置・向き）のユーザー別サイドカー
/// （`<プロジェクト>/cache/editor/view/**.view.json`）
pub mod editor_view_state;
pub mod ipc;
pub mod safe_write;
pub mod scene;
/// シーン単位のビューポート／レンダリング設定（`.scene` の settings 節）
pub mod scene_settings;
pub mod undo;

pub use app::{App, LaunchArgs, RuntimeMode};
