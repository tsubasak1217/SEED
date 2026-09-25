/// `.actor` / `.actor2d` ファイルの読み書き（版の変換・刻印を含む唯一の経路）
pub mod actor_file;
pub mod app;
/// エディタ視点（デバッグカメラの位置・向き）のユーザー別サイドカー
/// （`<プロジェクト>/cache/editor/view/**.view.json`）
pub mod editor_view_state;
pub mod ipc;
/// IPC の通信路（名前付きパイプ・TCP）。行の中身（プロトコル）は ipc.rs、運び方はこちら（段階D-1）
pub mod ipc_transport;
/// プレハブの「取り込んだ版」を表す内容ハッシュ（FNV-1a 64bit）
pub mod prefab_hash;
/// `project_settings.json` を読む唯一の入口（版の変換を含む）
pub mod project_settings;
pub mod safe_write;
pub mod scene;
/// シーン単位のビューポート／レンダリング設定（`.scene` の settings 節）
pub mod scene_settings;
pub mod undo;

pub use app::{App, LaunchArgs, RuntimeMode};
