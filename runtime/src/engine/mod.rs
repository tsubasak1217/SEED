pub mod animation;
pub mod asset_fs;
pub mod components;
pub mod core;
pub mod ecs;
pub mod methods;
pub mod pak;
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
