pub mod app_base;
pub mod audio;
/// 2D キャンバスノードのレイアウト計算（配置の純関数・木を 1 回たどる走査・切り抜きの領域。W2-1a）。
pub mod canvas_layout;
/// スクロールの本体（慣性・跳ね返り・スナップ・入れ子・見える範囲の外を飛ばす。W2-3。docs/ui_scroll_list.md）。
pub mod canvas_scroll;
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
/// 「描く理由」の判定（render_policy の on_demand で、理由の無いフレームが続いたら描画を止める。W2-10a）。
pub mod redraw;
pub mod renderer;
/// セーブデータ（スクリプト API `SEED.SaveData` の実体・JSON 永続化）。
pub mod save;
pub mod scripting;
/// 配布パッケージ版の起動ログ（標準出力のファイル化）と panic 通知。
pub mod startup_log;
pub mod transform_sync;
/// アプリ基盤 W2-0 のスパイク（IME の試作。既定で無効。描かないときの判定は redraw、切り抜きは canvas_layout へ本番化した）。
pub mod ui_spike;
pub mod window;

pub use input::Input;
