pub mod animation;
/// 実行環境フラグ（エディタからの Play か）。スクリプト API SEED.Application の判定源
pub mod app_env;
pub mod asset_fs;
/// シーン内コンポーネントの変数を「値の供給元」として公開する仕組み（`@ref` バインド）
pub mod binding;
pub mod components;
pub mod core;
pub mod ecs;
pub mod methods;
pub mod pak;
/// 配布物（assets.pak と PAK 外のファイル）を相対パスで開く読み口。Android の APK 内 pak 用（docs/android.md §13）
pub mod package_source;
pub mod physics;
pub mod plugin;
pub mod structs;
pub mod systems;
pub mod terrain;
/// 汎用パス: コントロールポイント列のワールド解決・補間・折れ線化
pub mod path;
/// 水システム（Phase W）: 水ボリュームのワールド解決と問い合わせ API
pub mod water;
/// インタラクションフィールド（Phase I）: 書き手（InteractionSource）の収集と速度算出
pub mod interaction;
/// ロジック配置: 円形・グリッド・直線・ランダムのパターンから決定的に点列を生成する純粋層
pub mod placement;
/// 実行プラットフォームの特性表（ウィンドウ寸法の主導権・スクリプト可否・診断ログ）。docs/android.md 参照
pub mod platform;
