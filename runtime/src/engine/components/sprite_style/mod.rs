// ============================================================
//  sprite_style/mod.rs — スプライトの「形と塗り」のデータ（W2-4）
//
//  SpriteComponent に足す 4 つの欄のデータ型（ECS のコンポーネントの一部。ロジックは持たない）。
//
//  | 欄           | 型                 | 内容                                                         |
//  |--------------|--------------------|--------------------------------------------------------------|
//  | `shape`      | `SpriteShape`      | 形（矩形〈四隅ごとの角丸〉・楕円・弧）と縁の線（太さ・色）   |
//  | `fill`       | `SpriteFill`       | 塗り（単色・線形グラデーション 2〜4 色・放射グラデーション） |
//  | `nine_slice` | `SpriteNineSlice`  | 画像の 9 スライス（枠の 4 辺の幅・辺と中央の伸ばす／繰り返し）|
//  | `shadow`     | `SpriteShadow`     | ぼかしの影（色・ずれ・ぼかし）                               |
//
//  【既定値の約束】4 つとも既定（角丸 0・縁 0・単色・9 スライス無し・影無し）なら、スプライトは
//  従来のパイプライン（sprite.wgsl）で**今と画素単位で同じ**に描かれる（renderer/batch2d.rs が振り分ける）。
//  既定の欄は `.scene` / `.actor` に書き出さない（`skip_serializing_if`）ので、既存のシーンを保存し直しても
//  ファイルの中身は変わらない。欄の無い旧データは既定で読める（`#[serde(default)]`）。
//
//  【単位】長さ（角丸の半径・縁の太さ・影のずれ・ぼかし・弧の太さ・9 スライスの描く幅）はスプライトの
//  幅・高さと同じ**キャンバスの単位**（dp のキャンバスなら dp）。角度は度（0 = +X・時計回りが正。SEED.Draw と同じ）。
//
//  描き方（SDF・グラデーション・9 スライスの写像）の正典は engine/core/renderer/ui_shape/ と docs/ui_components.md。
// ============================================================

pub mod fill;
pub mod nine_slice;
pub mod shadow;
pub mod shape;

pub use fill::{SpriteFill, SpriteFillKind, GRADIENT_MAX_COLORS, GRADIENT_MIN_COLORS};
pub use nine_slice::{NineSliceMode, SpriteNineSlice};
pub use shadow::SpriteShadow;
pub use shape::{SpriteShape, SpriteShapeKind};

/// 4 つの欄がすべて既定か（＝従来のスプライトとまったく同じに描く）【純関数】。
///
/// 描画の振り分け（従来のパイプラインか、形と塗りのパイプラインか）と、エディタの表示で使う。
pub fn is_plain_style(
    shape: &SpriteShape,
    fill: &SpriteFill,
    nine_slice: &SpriteNineSlice,
    shadow: &SpriteShadow,
) -> bool {
    shape.is_plain_rect() && fill.is_solid() && !nine_slice.enabled && !shadow.is_visible()
}
