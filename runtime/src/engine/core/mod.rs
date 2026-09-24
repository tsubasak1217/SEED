pub mod app_base;
pub mod audio;
/// アプリがバックグラウンドにいるか（Android の suspended〜resumed）の共有状態と、その間スレッドを眠らせる待機。
pub mod background_gate;
pub mod clock;
pub mod font;
pub mod input;
pub mod loader;
/// アセット形式のバージョンとマイグレーション（正典は docs/asset_migration.md）。
pub mod migration;
/// 配布パッケージのフォルダ構成（bin / caches / logs / saved）の正典。
pub mod package_layout;
pub mod parent_guard;
/// フレーム内セクション別 CPU 時間プロファイラ（エディタのプロファイラパネル用）。
pub mod profiling;
pub mod renderer;
/// セーブデータ（スクリプト API `SEED.SaveData` の実体・JSON 永続化）。
pub mod save;
pub mod scripting;
/// 配布パッケージ版の起動ログ（標準出力のファイル化）と panic 通知。
pub mod startup_log;
pub mod transform_sync;
pub mod window;

pub use input::Input;
