// ============================================================
//  terrain/scatter/mod.rs — 地形プロップ散布（草・木）モジュール
//
//  【責務】
//    地形表面へ草／モデルプロップを散布するための
//    「純粋なデータ + アルゴリズム」層。
//    親の terrain モジュールと同じく、ECS / GPU / wgpu / ファイル IO への
//    依存は一切持たない（JSON 文字列 in / 構造体 out、bytes in / bytes out）。
//
//  【ファイル構成（単一責任で分割）】
//    props.rs    — props.json のデータ定義と散布ルール評価（データドリブン）
//    tscatter.rs — 散布インスタンスのバージョン付きバイナリ永続化
//    generate.rs — 決定的なルール自動散布・ブラシ散布・地形編集後の再接地
//
//  【エンジン層との境界】
//    密度場（SDF）とレイヤ重みへのアクセスは `generate::ScatterField` トレイト
//    越しに行う。エンジン層が `HashMap<ChunkCoord, TerrainChunkData>` の上に
//    このトレイトを実装することで、本モジュールはチャンク管理を一切知らずに済む。
// ============================================================

pub mod props;
pub mod tscatter;
pub mod generate;

/// 散布レイヤ（T3）専用のユニットテスト（役割単位でファイル分割）。
#[cfg(test)]
mod tests_scatter;

// ─── 再エクスポート（本モジュールの公開 API）─────────────────────────────────
// エンジン統合層（app/terrain_scatter_ops.rs）はサブモジュールを直接指さず、
// 必ずここ経由で参照する（公開面を 1 か所に集約するため）。
pub use props::{
    GrassParams, LayerCondition, PropKind, ScatterParams, ScatterRule, TerrainProp,
    TerrainPropSet, GRASS_MAX_SEGMENTS, TERRAIN_MAX_PROPS, WindParams,
};
pub use tscatter::{
    read_chunk, write_chunk, ScatterInstance, TscatterError, TscatterHeader,
    TSCATTER_MAGIC, TSCATTER_VERSION,
};
pub use generate::{
    restick_instances, scatter_brush, scatter_chunk_by_rules, ScatterField, ScatterRng,
    MAX_SCATTER_GRID_PER_AXIS, MIN_INSTANCE_SPACING_FACTOR,
};

// 以下 3 つは統合層 第1段の実行時経路からは呼ばれない（テストからのみ使う）。
// データ層の公開 API としては意味があるので消さずに残すが、
// dead_code 警告が出るのでここだけ narrow に許可する。
//   * read_header      … 本体を読まずに件数だけ知りたい場面（LOD 判定・統計表示）用。
//                        第1段は常に本体ごと読むため未使用。
//   * hash_seed        … 決定性検証（同じ入力なら同じ乱数列）をテストが直接叩く。
//   * surface_hit_down … 接地点探索の単体。統合層は散布関数越しにしか使わない。
// モジュール全体を allow(dead_code) しないのは、タイプミス由来の本物の
// 死にコードを見逃さないため（統合前の暫定 allow はこの差し替えで撤去済み）。
#[allow(unused_imports)]
pub use generate::{hash_seed, surface_hit_down};
#[allow(unused_imports)]
pub use tscatter::read_header;
