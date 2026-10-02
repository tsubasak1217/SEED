// ============================================================
//  font/msdf/mod.rs — 輪郭から作る MTSDF（3 チャネルの MSDF + 真の SDF。2026-10-02）
//
//  【なぜ】
//  1 チャネルの SDF（rasterizer.rs。em 64 で 2 値にしたビットマップから距離変換）は、輪郭が 64 px の格子に丸められ、
//  大きな文字（実機 2.625 倍の時刻 150 px 超など）で角が丸まり曲線が波打って粗く見えた（docs/backlog.md）。
//  ここでは書体の輪郭（ベジェ）から直接、正確な距離を求める（2 値化の格子が無い）。さらに角の両側の辺を別のチャネルに
//  入れる MSDF（msdfgen の方式。Chlumský 2015）で、拡大しても角が立つ。4 チャネル目には真の SDF を入れ、
//  小さな文字・縁取り・ぼかしの影に使う（text.wgsl の fs_mtsdf。切り替えの規則は docs/ui_components.md §12.9）。
//
//  【方式の選定（docs/ui_components.md §12.8）】
//  純 Rust のクレート（fdsm 0.8・MIT）も検討したが、自前で実装した:
//    - 重なった輪郭への対応（msdfgen の OverlappingContourCombiner）が fdsm には無い（CJK の可変フォントなどで内側の辺が縁に出る）
//    - fdsm は nalgebra 0.34 に依存する（rapier の 0.33 と別の版が増え、PC・Android のビルドが重くなる）
//    - 字形ごとの検査（安全弁）・打ち切りによる高速化・行の並列化・SEED の値の決まり（0.5 = 縁・px → 値の変換）を
//      合わせて作り込む必要がある
//  式は msdfgen（MIT）に倣う（辺への距離・色分け simple / ink trap・疑似距離の延長・重なりの合成・誤差の補正）。
//
//  【構成】
//    geometry.rs         … 2 次元のベクトル・符号つき距離・多項式の解
//    segment.rs          … 辺（直線・2 次・3 次）と辺への符号つき距離
//    outline.rs          … ab_glyph の輪郭 → 輪郭 × 辺の形（組み直し・向きをそろえる）
//    edge_color.rs       … 辺の色分け（simple / ink trap）
//    distance.rs         … テクセルごとの距離（輪郭ごと → 重なりを考えて合成。打ち切り・行の並列化・符号の直し）
//    error_correction.rs … 補間の誤り（偽の縁）の補正
//    verify.rs           … 字形ごとの検査（参照のラスタとの比較。安全弁）
//    bake.rs             … 1 グリフを焼く流れ（上を順に呼ぶ・各段の時間）
//    params.rs           … 定数（大きさ・距離の幅・余白・補正と検査の閾値）
// ============================================================

pub mod bake;
pub mod distance;
pub mod edge_color;
pub mod error_correction;
pub mod geometry;
pub mod outline;
pub mod params;
pub mod segment;
pub mod verify;
/// 計測（焼き時間・検査の結果）と目視用の画像（`--ignored` で走らせる）
#[cfg(test)]
mod measure_tests;

pub use bake::{bake_glyph_mtsdf, BakeStats};
pub use edge_color::ColoringStrategy;
