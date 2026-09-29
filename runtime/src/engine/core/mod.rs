pub mod app_base;
pub mod audio;
/// 2D キャンバスノードのレイアウト計算（配置の純関数・木を 1 回たどる走査・切り抜きの領域。W2-1a）。
pub mod canvas_layout;
/// スクロールの本体（慣性・跳ね返り・スナップ・入れ子・見える範囲の外を飛ばす。W2-3。docs/ui_scroll_list.md）。
pub mod canvas_scroll;
/// アプリがバックグラウンドにいるか（Android の suspended〜resumed）の共有状態と、その間スレッドを眠らせる待機。
pub mod background_gate;
pub mod clock;
/// フレームごとの使い回しの表を引く速いハッシュ（Fx 方式。2026-09-28）
pub mod fast_hash;
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
/// 文字入力の受け口（入力欄の状態・PC の IME とキー・Android の IME の知らせ・スクリプトの窓口。W2-6a。docs/ui_text_input.md）。
/// W2-0 のスパイク（ui_spike）の最後の試作（ime）をこれに置き換えて、ui_spike は外した。
pub mod text_input;
pub mod transform_sync;
pub mod window;

pub use input::Input;
