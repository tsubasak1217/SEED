// ============================================================
//  renderer/ui_shape/ — スプライトの「形と塗り」と角丸・楕円の切り抜き（W2-4）
//
//  【方式】（決定の根拠は docs/app_platform_roadmap.md §3.8.4、規則の正典は docs/ui_components.md）
//  形（角丸の矩形・楕円・弧）はシェーダーの SDF（符号付き距離）で描き、縁の線・グラデーション・9 スライス・ぼかしの影も
//  同じシェーダー（shaders/sprite_shape.wgsl）で行う。**欄がすべて既定のスプライトは従来のパイプライン（sprite.wgsl）の
//  まま**描く（batch2d.rs が振り分ける。既定の見た目は画素単位で変わらない）。
//
//  | ファイル        | 役割（どれも World・GPU に触れない純関数。WGSL と同じ式を持ち、単体テストで検算する）      |
//  |-----------------|------------------------------------------------------------------------------------------|
//  | `sdf.rs`        | 形の SDF（角丸の矩形・楕円・弧）・角丸の半径の縮め方・画素の幅のアンチエイリアス・影のぼかし |
//  | `fill.rs`       | グラデーション（線形の端点・放射・色の位置・乗算済みアルファの補間）                         |
//  | `nine_slice.rs` | 9 スライスの軸ごとの切り方（描く位置と UV の格子＝頂点）と写像（伸ばす・繰り返す）           |
//  | `shape_hit.rs`  | 形の当たり判定（最小のヒット領域まで広げた形・切り抜きの領域のローカル座標）                 |
//  | `clip_sdf.rs`   | 切り抜きの領域の形（角丸・楕円）と、描画のワールド座標 → 領域のローカル座標の写像             |
//  | `params.rs`     | GPU へ渡す形のパラメータ（ストレージバッファの 1 要素）とインスタンス（行列の付け替え）の組み立て |
//
//  【空間】形は「形の空間」＝スプライトの矩形 [0, 幅]×[0, 高さ]（キャンバスの単位。Y 下向き）で決める。
//  角丸の半径・縁の太さ・影のずれは同じ単位。アンチエイリアスは画面の 1 画素がこの空間で何単位かを
//  シェーダーの微分（dpdx・dpdy）で求め、境界の前後 0.5 画素でぼかす（画素の格子に揃った辺は従来どおりくっきり）。
// ============================================================

pub mod clip_sdf;
pub mod fill;
pub mod nine_slice;
pub mod params;
pub mod sdf;
pub mod shape_hit;

pub use clip_sdf::{ClipAffine, ClipGeomKind, ClipSdf, UiClipShape};
pub use params::{
    build_shape_instances, ShapeInstance, ShapeParamsGpu, SpriteStyleDraw, SHAPE_INSTANCE_SIZE,
    SHAPE_PARAMS_SIZE,
};
pub use sdf::{ShapeGeom, ShapeGeomKind};
