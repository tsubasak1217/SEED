// ============================================================
//  terrain/cover/mod.rs — 地表カバー場（雪・落ち葉・泥・濡れ）モジュール
//
//  【責務】
//    地形の表面へ素材を積もらせるための「純粋なデータ + アルゴリズム」層。
//    親の terrain モジュールと同じく、ECS / GPU / wgpu / ファイル IO への
//    依存は一切持たない（JSON 文字列 in / 構造体 out、bytes in / bytes out）。
//    正典ドキュメントは docs/cover_field.md。
//
//  【ファイル構成（単一責任で分割）】
//    material.rs   — cover_materials.json のデータ定義（色・粗さ・変位）
//    field.rs      — チャンク紐づけのカバー場（素材＋量の 1 層）と地表情報・傾斜ルール
//    emit.rs       — エミッタ範囲（Global / Region / TextureMask）の評価
//    accumulate.rs — 積算（場 × 地表 × エミッタ × dt → 新しい場）
//    tcover.rs     — カバー場のバージョン付きバイナリ永続化（.tcover）
//
//  【エンジン層との境界】
//    エンジン層（app/terrain_cover_ops.rs）が
//      ・cover_materials.json の読み込み
//      ・`CoverEmitterComponent` からの `CoverEmitSpec` 組み立て
//      ・チャンクごとの `accumulate_chunk` 呼び出しとダーティ管理
//      ・.tcover の読み書きと、頂点へのカバー焼き込み
//    を担う。本モジュールはチャンク管理も ECS も一切知らない。
// ============================================================

pub mod accumulate;
pub mod emit;
pub mod field;
pub mod material;
pub mod tcover;

/// カバー場（I3.1）専用のユニットテスト（役割単位でファイル分割）。
#[cfg(test)]
mod tests_cover;

// ─── 再エクスポート（本モジュールの公開 API）─────────────────────────────────
// エンジン統合層はサブモジュールを直接指さず、必ずここ経由で参照する
// （公開面を 1 か所に集約するため。scatter/mod.rs と同じ流儀）。
pub use accumulate::accumulate_chunk;
pub use emit::{CoverEmitRange, CoverEmitSpec, CoverMask};
pub use field::{
    slope_scale, texel_center_uv, CoverField, CoverNeighborhood, CoverSurface,
    COVER_FIELD_RESOLUTION,
    COVER_FIELD_TEXELS, COVER_SLOPE_UP_FULL, COVER_SLOPE_UP_MIN, COVER_SURFACE_ABSENT,
};
pub use material::{
    CoverMaterial, CoverMaterialSet, COVER_MATERIAL_NONE, TERRAIN_MAX_COVER_MATERIALS,
};
pub use tcover::{
    read_chunk as read_cover_chunk, write_chunk as write_cover_chunk, TcoverError, TcoverHeader,
    TCOVER_MAGIC, TCOVER_VERSION,
};

// `read_header` は本体を読まずに座標だけ知りたい場面（統計表示）用。
// 現行の実行時経路からは呼ばれないが、データ層の公開 API としては意味があるので
// 消さずに残す（tscatter.rs の read_header と同じ扱い）。
#[allow(unused_imports)]
pub use tcover::read_header as read_cover_header;
