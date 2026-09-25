// ============================================================
//  gpu_timing/segments.rs — フレームの区間名（タイムスタンプを書く節目）
//
//  frame_renderer.rs が描画の節目で `GpuPassTimer::mark(区間名)` を呼ぶ。区間名は
//  「1 つ前の節目からこの節目まで」に GPU が使った時間の名前（report.rs）。
//  ログ（`[SEED GPU]`）はこの ORDER の順に並ぶ。docs/android.md §22 の表の列と同じ名前にする。
// ============================================================

/// フレームの頭〜影の前: スキニング・パーティクルのシミュレーション・メッシュレットカリング等の compute。
pub const COMPUTE: &str = "compute";
/// シャドウマップの深度パス（カスケード・スポット）。
pub const SHADOW: &str = "shadow";
/// レイトレの加速構造・DDGI のプローブ更新（RT 対応 GPU だけ。非対応ならほぼ 0）。
pub const RT: &str = "rt";
/// クラスタ化ライティングのクラスタ構築（compute）。
pub const CLUSTER: &str = "cluster";
/// G-Buffer パス（MRT 5 枚）と Hi-Z。
pub const GBUFFER: &str = "gbuffer";
/// AO・水中コースティクス・RT 影マスク。
pub const AO: &str = "ao";
/// デファードのフルスクリーン・ライティング。
pub const LIGHTING: &str = "lighting";
/// SSGI（半解像度＋ブラー）。
pub const SSGI: &str = "ssgi";
/// 反射（SSR / RT）と合成、屈折背景のミップ。
pub const REFLECTION: &str = "reflection";
/// メインの前方パス（スカイボックス・前方の不透明・背景の UI・半透明の距離ソート・3D スプライト等）。
pub const FORWARD: &str = "forward";
/// 水面（屈折背景のグラブ・水面反射・水面パス）。
pub const WATER: &str = "water";
/// WBOIT の半透明と合成。
pub const WBOIT: &str = "wboit";
/// エディタのオーバーレイと LineRenderer のリボン等（メインパス後の重ね描き）。
pub const OVERLAY: &str = "overlay";
/// GPU パーティクルの描画。
pub const PARTICLES: &str = "particles";
/// ブルーム。
pub const BLOOM: &str = "bloom";
/// ビネット・トーンマップ（描画スケールが 1 未満なら、ここで UI の解像度へ拡大する）。
pub const TONEMAP: &str = "tonemap";
/// UI（キャンバスのオーバーレイ）。
pub const UI: &str = "ui";
/// FXAA／提示先へのコピーとカメラプレビュー。
pub const PRESENT: &str = "present";

/// ログに並べる順（描画の順）。
pub const ORDER: [&str; 18] = [
    COMPUTE, SHADOW, RT, CLUSTER, GBUFFER, AO, LIGHTING, SSGI, REFLECTION, FORWARD, WATER, WBOIT,
    OVERLAY, PARTICLES, BLOOM, TONEMAP, UI, PRESENT,
];
