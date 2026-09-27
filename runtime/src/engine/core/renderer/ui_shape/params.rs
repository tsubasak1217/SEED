// ============================================================
//  ui_shape/params.rs — 形と塗りのパイプラインへ渡す GPU のデータの組み立て（W2-4）
//
//  形と塗りのパイプライン（pipelines/sprite_shape.toml・shaders/sprite_shape.wgsl）は、1 スプライトを
//  1 枚の四角形（インスタンス）で描く。インスタンスの頂点属性は「行列 + パラメータの番号」（`ShapeInstance`・80 bytes）で、
//  形・塗り・9 スライス・影・切り抜きの値はストレージバッファの 1 要素（`ShapeParamsGpu`・304 bytes）に置く
//  （頂点属性の上限 16 に収まらないため。W2-4 の決定）。
//
//  【四角形の大きさ】形の SDF のアンチエイリアスは境界の外へ半画素はみ出すので、形のある四角形は
//  ワールドの `AA_MARGIN_WORLD` 画素ぶん外へ広げて描く（行列を付け替える＝`remap_model`）。影は形を `offset` だけずらし、
//  ぼかしの 3σ ぶん広げた別のインスタンスを**先に**積む（形の後ろに描かれる）。形の無いスプライト（グラデーション・
//  9 スライスだけ・切り抜きだけ受ける）は四角形を広げない（行列はそのまま＝従来と同じ位置・同じ画素の辺）。
//
//  【色】塗りの色の表はスプライトの color（乗算の色）を掛け済み（単色は color そのもの）。縁・影の色は color から独立
//  （塗りを透明にした輪・ラジオの輪・チェックボックスの枠が消えないように）。
//
//  WGSL の定数（SHAPE_KIND_* など）はこのファイルの定数と同じ値でなければならない（単体テストで照合する）。
// ============================================================

use bytemuck::{Pod, Zeroable};

use super::clip_sdf::{ClipGeomKind, ClipSdf};
use super::fill::{gradient_geom, normalized_stops, resolve_colors, GradientGeom};
use super::nine_slice::resolve_axis;
use super::sdf::{ShapeGeom, ShapeGeomKind, SHADOW_EXTENT_SIGMAS, SHADOW_SIGMA_PER_BLUR};
use crate::engine::components::{
    NineSliceMode, SpriteComponent, SpriteFill, SpriteFillKind, SpriteNineSlice, SpriteShadow, SpriteShape,
};

// ── 形の種類（WGSL の SHAPE_KIND_* と一致必須）──
/// 形なし（矩形いっぱい・被覆率 1）。
pub const SHAPE_KIND_NONE: u32 = 0;
/// 角丸の矩形。
pub const SHAPE_KIND_ROUNDED_RECT: u32 = 1;
/// 楕円。
pub const SHAPE_KIND_ELLIPSE: u32 = 2;
/// 弧（リング）。
pub const SHAPE_KIND_ARC: u32 = 3;

// ── 塗りの種類（WGSL の FILL_* と一致必須）──
/// 単色。
pub const FILL_SOLID: u32 = 0;
/// 線形グラデーション。
pub const FILL_LINEAR: u32 = 1;
/// 放射グラデーション。
pub const FILL_RADIAL: u32 = 2;

// ── 旗（WGSL の FLAG_* と一致必須）──
/// 9 スライスで UV を求める。
pub const FLAG_NINE_SLICE: u32 = 1;
/// 9 スライスの辺を繰り返す。
pub const FLAG_EDGE_REPEAT: u32 = 2;
/// 9 スライスの中央を繰り返す。
pub const FLAG_CENTER_REPEAT: u32 = 4;
/// 9 スライスの中央を描かない。
pub const FLAG_HOLLOW_CENTER: u32 = 8;
/// 影のインスタンス（テクスチャを見ず、ぼかした形の被覆率で塗る）。
pub const FLAG_SHADOW: u32 = 16;
/// 弧の端を丸くする。
pub const FLAG_ROUND_CAPS: u32 = 32;
/// 角丸の矩形の切り抜きで切る。
pub const FLAG_CLIP_ROUNDED: u32 = 64;
/// 楕円の切り抜きで切る。
pub const FLAG_CLIP_ELLIPSE: u32 = 128;

/// 形のある四角形を外へ広げる量（ワールドの単位。スクリーンスペースでは画素。アンチエイリアスの半画素を欠かさない余裕）。
pub const AA_MARGIN_WORLD: f32 = 2.0;
/// 0 とみなす長さ（割り算の保護）。
const EPSILON: f32 = 1e-6;
/// 切り抜きだけ受けるスプライトの形の空間（ユニットクワッドそのもの）。
const UNIT_SPACE: [f32; 2] = [1.0, 1.0];

/// 形と塗りのパラメータ（ストレージバッファの 1 要素。WGSL の `ShapeParams` と 1:1。すべて 16 バイト境界）。
#[repr(C)]
#[derive(Copy, Clone, Debug, PartialEq, Pod, Zeroable)]
pub struct ShapeParamsGpu {
    /// 描く四角形（形の空間。x, y, 幅, 高さ）。
    pub quad: [f32; 4],
    /// 形の空間の大きさ（幅, 高さ）, 影のσ, 縁の太さ。
    pub size: [f32; 4],
    /// 形の種類, 塗りの種類, 旗, 色の数。
    pub modes: [u32; 4],
    /// 四隅の半径（左上, 右上, 右下, 左下。縮め済み）。
    pub radii: [f32; 4],
    /// 弧（中心線の半径, 太さの半分, 開始角〈ラジアン〉, 角度〈ラジアン〉）。
    pub arc: [f32; 4],
    /// 縁の色（color の不透明度を掛け済み）。
    pub border_color: [f32; 4],
    /// 塗りの色の表（color を掛け済み）。
    pub colors: [[f32; 4]; 4],
    /// 色の位置。
    pub stops: [f32; 4],
    /// グラデーションの形（線形: p0.x, p0.y, p1.x, p1.y ／ 放射: 中心 x, y, 半径 x, y）。
    pub grad: [f32; 4],
    /// 9 スライスの横（a, b, ua, ub）。
    pub nine_x: [f32; 4],
    /// 9 スライスの縦（a, b, va, vb）。
    pub nine_y: [f32; 4],
    /// 9 スライスの繰り返しの回数（横, 縦, 0, 0）。
    pub nine_tiles: [f32; 4],
    /// 切り抜き: ワールド → 領域のローカルの x（a, b, c, 0）。
    pub clip_row0: [f32; 4],
    /// 切り抜き: ワールド → 領域のローカルの y（d, e, f, 0）。
    pub clip_row1: [f32; 4],
    /// 切り抜きの領域の大きさ（幅, 高さ, 0, 0）。
    pub clip_size: [f32; 4],
    /// 切り抜きの四隅の半径。
    pub clip_radii: [f32; 4],
}

/// `ShapeParamsGpu` のバイト数（WGSL の配列の要素の大きさと一致必須）。
pub const SHAPE_PARAMS_SIZE: u64 = std::mem::size_of::<ShapeParamsGpu>() as u64;

/// 形と塗りのインスタンスの頂点属性（80 bytes。pipeline_config.rs の "shape_instance" と一致必須）。
#[repr(C)]
#[derive(Copy, Clone, Debug, PartialEq, Pod, Zeroable)]
pub struct ShapeInstance {
    /// モデル行列（列優先。ユニットクワッド → 描く四角形のワールド）。
    pub model: [[f32; 4]; 4],
    /// パラメータの番号（ストレージバッファの添字）。
    pub params: u32,
    /// 16 バイト境界のための詰め物。
    pub _pad: [u32; 3],
}

/// `ShapeInstance` のバイト数。
pub const SHAPE_INSTANCE_SIZE: u64 = std::mem::size_of::<ShapeInstance>() as u64;

/// 描画アイテムが運ぶ「形と塗り」（収集のときにスプライトから写す）。
#[derive(Clone, Debug, PartialEq)]
pub struct SpriteStyleDraw {
    /// 形。
    pub shape: SpriteShape,
    /// 塗り。
    pub fill: SpriteFill,
    /// 9 スライス。
    pub nine_slice: SpriteNineSlice,
    /// 影。
    pub shadow: SpriteShadow,
    /// 形の空間の大きさ（キャンバスの単位。レイアウトが伸ばした軸は矩形の大きさ ÷ サイズ倍率）。
    pub space: [f32; 2],
    /// アンチエイリアスの余白（形の空間。`aa_margin`）。
    pub margin: [f32; 2],
}

impl SpriteStyleDraw {
    /// スプライトの「形と塗り」を描画アイテムへ写す【純関数】（欄がすべて既定なら None＝従来のパイプライン）。
    ///
    /// # 引数
    /// * `sc`           - スプライト
    /// * `space`        - 形の空間の大きさ
    /// * `model`        - スプライトのモデル行列（列優先。ユニットクワッド → スプライトの矩形）
    /// * `canvas_scale` - キャンバス px → ワールドの倍率（スクリーンスペースは 1）
    pub fn for_sprite(
        sc: &SpriteComponent,
        space: [f32; 2],
        model: &[[f32; 4]; 4],
        canvas_scale: f32,
    ) -> Option<Box<Self>> {
        if sc.is_plain_style() {
            return None;
        }
        Some(Box::new(Self {
            shape: sc.shape.clone(),
            fill: sc.fill.clone(),
            nine_slice: sc.nine_slice.clone(),
            shadow: sc.shadow.clone(),
            space,
            margin: aa_margin(model, space, canvas_scale),
        }))
    }
}

/// アンチエイリアスの余白（形の空間）【純関数】: ワールドの `AA_MARGIN_WORLD`（× canvas_scale）を形の空間へ直す。
///
/// # 引数
/// * `model`        - スプライトのモデル行列（列 0・1 の長さ = スプライトのワールドの幅・高さ）
/// * `space`        - 形の空間の大きさ
/// * `canvas_scale` - キャンバス px → ワールドの倍率
pub fn aa_margin(model: &[[f32; 4]; 4], space: [f32; 2], canvas_scale: f32) -> [f32; 2] {
    [0, 1].map(|a| {
        let col = &model[a];
        let world_len = (col[0] * col[0] + col[1] * col[1] + col[2] * col[2]).sqrt();
        if world_len > EPSILON {
            AA_MARGIN_WORLD * canvas_scale.abs() * space[a].abs() / world_len
        } else {
            0.0
        }
    })
}

/// モデル行列を付け替える【純関数】: ユニットクワッドを、形の空間の四角形 `quad` のワールドへ写す行列にする。
///
/// # 引数
/// * `model` - スプライトのモデル行列（ユニットクワッド → 形の空間 [0, 幅]×[0, 高さ] のワールド）
/// * `space` - 形の空間の大きさ
/// * `quad`  - 形の空間の四角形（x, y, 幅, 高さ）
pub fn remap_model(model: &[[f32; 4]; 4], space: [f32; 2], quad: [f32; 4]) -> [[f32; 4]; 4] {
    let inv = space.map(|s| if s.abs() > EPSILON { 1.0 / s } else { 0.0 });
    let (fx, fy) = (quad[2] * inv[0], quad[3] * inv[1]);
    let (ox, oy) = (quad[0] * inv[0], quad[1] * inv[1]);
    let c0 = model[0].map(|v| v * fx);
    let c1 = model[1].map(|v| v * fy);
    let c3 = [0, 1, 2, 3].map(|k| model[3][k] + model[0][k] * ox + model[1][k] * oy);
    [c0, c1, model[2], c3]
}

/// 形の種類を GPU の番号へ。
fn shape_kind_code(kind: ShapeGeomKind) -> u32 {
    match kind {
        ShapeGeomKind::None => SHAPE_KIND_NONE,
        ShapeGeomKind::RoundedRect => SHAPE_KIND_ROUNDED_RECT,
        ShapeGeomKind::Ellipse => SHAPE_KIND_ELLIPSE,
        ShapeGeomKind::Arc => SHAPE_KIND_ARC,
    }
}

/// 形（半径・弧）をパラメータへ書く。
fn write_geom(p: &mut ShapeParamsGpu, geom: &ShapeGeom) {
    p.modes[0] = shape_kind_code(geom.kind);
    p.radii = geom.radii;
    p.arc = [geom.arc.radius, geom.arc.half_thickness, geom.arc.start, geom.arc.sweep];
    if geom.arc.round_caps {
        p.modes[2] |= FLAG_ROUND_CAPS;
    }
}

/// 切り抜きをパラメータへ書く（無ければ何もしない）。
fn write_clip(p: &mut ShapeParamsGpu, clip: Option<&ClipSdf>) {
    let Some(clip) = clip else { return };
    p.modes[2] |= match clip.shape.kind {
        ClipGeomKind::None => 0,
        ClipGeomKind::RoundedRect => FLAG_CLIP_ROUNDED,
        ClipGeomKind::Ellipse => FLAG_CLIP_ELLIPSE,
    };
    let [a, b, c] = clip.affine.row0;
    let [d, e, f] = clip.affine.row1;
    p.clip_row0 = [a, b, c, 0.0];
    p.clip_row1 = [d, e, f, 0.0];
    p.clip_size = [clip.shape.local_size[0], clip.shape.local_size[1], 0.0, 0.0];
    p.clip_radii = clip.shape.radii;
}

/// 単色（1 色）の塗りを書く。
fn write_solid(p: &mut ShapeParamsGpu, color: [f32; 4]) {
    p.modes[1] = FILL_SOLID;
    p.modes[3] = 1;
    p.colors = [color; 4];
    p.stops = normalized_stops(&[], 1);
}

/// 1 つの描画アイテムの形と塗りのインスタンス（影があれば影 → 本体の順）を積む【純関数】。
///
/// # 引数
/// * `model`    - スプライトのモデル行列（列優先。ユニットクワッド → スプライトの矩形）
/// * `tint`     - スプライトの color
/// * `style`    - 形と塗り（None = 切り抜きだけ受ける従来のスプライト）
/// * `tex_size` - テクスチャの大きさ（画素。無ければ 9 スライスをしない）
/// * `clip`     - いちばん内側の形のある切り抜き（無ければ None）
/// * `out`      - (モデル行列, パラメータ) の積み先
pub fn build_shape_instances(
    model: &[[f32; 4]; 4],
    tint: [f32; 4],
    style: Option<&SpriteStyleDraw>,
    tex_size: Option<[u32; 2]>,
    clip: Option<&ClipSdf>,
    out: &mut Vec<([[f32; 4]; 4], ShapeParamsGpu)>,
) {
    // ── 切り抜きだけ受ける従来のスプライト: 行列もユニットクワッドもそのまま、被覆率 1 × 切り抜き ──
    let Some(style) = style else {
        let mut p = ShapeParamsGpu::zeroed();
        p.quad = [0.0, 0.0, UNIT_SPACE[0], UNIT_SPACE[1]];
        p.size = [UNIT_SPACE[0], UNIT_SPACE[1], 0.0, 0.0];
        write_solid(&mut p, tint);
        write_clip(&mut p, clip);
        out.push((*model, p));
        return;
    };
    let space = style.space;
    let geom = ShapeGeom::from_sprite_shape(&style.shape, space);

    // ── 影（形の後ろ。形をずらしてぼかす）──
    if style.shadow.is_visible() {
        let sigma = style.shadow.blur.max(0.0) * SHADOW_SIGMA_PER_BLUR;
        let extent = [0, 1].map(|a| style.margin[a] + sigma * SHADOW_EXTENT_SIGMAS);
        // 形の無いスプライトの影は直角の矩形の影
        let shadow_geom = if geom.kind == ShapeGeomKind::None { ShapeGeom::rounded_rect(space, [0.0; 4]) } else { geom };
        let local = [-extent[0], -extent[1], space[0] + extent[0] * 2.0, space[1] + extent[1] * 2.0];
        let placed = [local[0] + style.shadow.offset[0], local[1] + style.shadow.offset[1], local[2], local[3]];
        let mut p = ShapeParamsGpu::zeroed();
        p.quad = local;
        p.size = [space[0], space[1], sigma, 0.0];
        write_solid(&mut p, style.shadow.color);
        write_geom(&mut p, &shadow_geom);
        p.modes[2] |= FLAG_SHADOW;
        write_clip(&mut p, clip);
        out.push((remap_model(model, space, placed), p));
    }

    // ── 本体 ──
    let mut p = ShapeParamsGpu::zeroed();
    let expand = if geom.kind == ShapeGeomKind::None { [0.0, 0.0] } else { style.margin };
    p.quad = [-expand[0], -expand[1], space[0] + expand[0] * 2.0, space[1] + expand[1] * 2.0];
    p.size = [space[0], space[1], 0.0, style.shape.border_width.max(0.0)];
    write_geom(&mut p, &geom);
    // 塗り
    let (colors, count) = resolve_colors(&style.fill, tint);
    p.modes[1] = match style.fill.kind {
        SpriteFillKind::Solid => FILL_SOLID,
        SpriteFillKind::Linear => FILL_LINEAR,
        SpriteFillKind::Radial => FILL_RADIAL,
    };
    p.modes[3] = count as u32;
    p.colors = colors;
    p.stops = normalized_stops(&style.fill.stops, count);
    p.grad = match gradient_geom(&style.fill, space) {
        GradientGeom::Solid => [0.0; 4],
        GradientGeom::Linear { p0, p1 } => [p0[0], p0[1], p1[0], p1[1]],
        GradientGeom::Radial { center, radius } => [center[0], center[1], radius[0], radius[1]],
    };
    p.border_color = style.shape.border_color;
    // 9 スライス（テクスチャがあるときだけ）
    if let (true, Some([tw, th])) = (style.nine_slice.enabled, tex_size) {
        let n = &style.nine_slice;
        let x = resolve_axis(space[0], n.border[0], n.border[2], tw as f32, n.scale);
        let y = resolve_axis(space[1], n.border[1], n.border[3], th as f32, n.scale);
        p.nine_x = [x.dest[0], x.dest[1], x.uv[0], x.uv[1]];
        p.nine_y = [y.dest[0], y.dest[1], y.uv[0], y.uv[1]];
        p.nine_tiles = [x.tiles, y.tiles, 0.0, 0.0];
        p.modes[2] |= FLAG_NINE_SLICE;
        if n.edge_mode == NineSliceMode::Repeat {
            p.modes[2] |= FLAG_EDGE_REPEAT;
        }
        if n.center_mode == NineSliceMode::Repeat {
            p.modes[2] |= FLAG_CENTER_REPEAT;
        }
        if !n.fill_center {
            p.modes[2] |= FLAG_HOLLOW_CENTER;
        }
    }
    write_clip(&mut p, clip);
    let placed_model = if expand == [0.0, 0.0] { *model } else { remap_model(model, space, p.quad) };
    out.push((placed_model, p));
}

// ============================================================
//  単体テスト（GPU のデータの組み立て）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::SpriteShapeKind;
    use crate::engine::core::renderer::ui_shape::clip_sdf::UiClipShape;

    /// 大きさ 100×50 のスプライトを (10, 20) に置くモデル行列（列優先・スクリーンスペース）。
    fn model() -> [[f32; 4]; 4] {
        [[100.0, 0.0, 0.0, 0.0], [0.0, 50.0, 0.0, 0.0], [0.0, 0.0, 1.0, 0.0], [10.0, 20.0, 0.0, 1.0]]
    }

    /// 列優先の行列で点を写す。
    fn apply(m: &[[f32; 4]; 4], t: [f32; 2]) -> [f32; 2] {
        [m[0][0] * t[0] + m[1][0] * t[1] + m[3][0], m[0][1] * t[0] + m[1][1] * t[1] + m[3][1]]
    }

    /// GPU の構造体の大きさ（WGSL・頂点レイアウトと一致必須）。
    #[test]
    fn gpu_struct_sizes() {
        assert_eq!(SHAPE_PARAMS_SIZE, 304);
        assert_eq!(SHAPE_INSTANCE_SIZE, 80);
    }

    /// WGSL の定数がこのファイルの定数と同じ値であること。
    #[test]
    fn wgsl_constants_match() {
        let wgsl = include_str!("../shaders/sprite_shape.wgsl");
        for (name, value) in [
            ("SHAPE_KIND_NONE", SHAPE_KIND_NONE),
            ("SHAPE_KIND_ROUNDED_RECT", SHAPE_KIND_ROUNDED_RECT),
            ("SHAPE_KIND_ELLIPSE", SHAPE_KIND_ELLIPSE),
            ("SHAPE_KIND_ARC", SHAPE_KIND_ARC),
            ("FILL_SOLID", FILL_SOLID),
            ("FILL_LINEAR", FILL_LINEAR),
            ("FILL_RADIAL", FILL_RADIAL),
            ("FLAG_NINE_SLICE", FLAG_NINE_SLICE),
            ("FLAG_EDGE_REPEAT", FLAG_EDGE_REPEAT),
            ("FLAG_CENTER_REPEAT", FLAG_CENTER_REPEAT),
            ("FLAG_HOLLOW_CENTER", FLAG_HOLLOW_CENTER),
            ("FLAG_SHADOW", FLAG_SHADOW),
            ("FLAG_ROUND_CAPS", FLAG_ROUND_CAPS),
            ("FLAG_CLIP_ROUNDED", FLAG_CLIP_ROUNDED),
            ("FLAG_CLIP_ELLIPSE", FLAG_CLIP_ELLIPSE),
        ] {
            let decl = format!("const {name}: u32 = {value}u;");
            assert!(wgsl.contains(&decl), "WGSL に `{decl}` が無い");
        }
    }

    /// 欄がすべて既定のスプライトは形と塗りを運ばない（＝従来のパイプラインで描く）。
    #[test]
    fn plain_sprite_has_no_style() {
        let sc = SpriteComponent::default();
        assert!(SpriteStyleDraw::for_sprite(&sc, [100.0, 50.0], &model(), 1.0).is_none());
        let rounded = SpriteComponent {
            shape: SpriteShape { corner_radii: [8.0; 4], ..SpriteShape::default() },
            ..SpriteComponent::default()
        };
        assert!(SpriteStyleDraw::for_sprite(&rounded, [100.0, 50.0], &model(), 1.0).is_some());
    }

    /// 切り抜きだけ受けるスプライト: 行列はそのまま・形なし・色は color。
    #[test]
    fn clip_only_keeps_the_model() {
        let clip = ClipSdf::new(
            &[[0.0, 0.0, 0.0], [100.0, 0.0, 0.0], [0.0, 100.0, 0.0], [100.0, 100.0, 0.0]],
            &UiClipShape::ellipse([100.0, 100.0]),
        )
        .unwrap();
        let mut out = Vec::new();
        build_shape_instances(&model(), [0.5, 0.6, 0.7, 0.8], None, None, Some(&clip), &mut out);
        assert_eq!(out.len(), 1);
        let (m, p) = out[0];
        assert_eq!(m, model(), "行列はビット単位で同じ");
        assert_eq!(p.modes[0], SHAPE_KIND_NONE);
        assert_eq!(p.colors[0], [0.5, 0.6, 0.7, 0.8]);
        assert_eq!(p.modes[2] & FLAG_CLIP_ELLIPSE, FLAG_CLIP_ELLIPSE);
    }

    /// 形のあるスプライト: 四角形はアンチエイリアスの余白ぶん広がり、行列は広げた四角形を写す。
    #[test]
    fn shaped_quad_is_expanded() {
        let sc = SpriteComponent {
            width: 100.0,
            height: 50.0,
            shape: SpriteShape { kind: SpriteShapeKind::Ellipse, ..SpriteShape::default() },
            ..SpriteComponent::default()
        };
        let style = SpriteStyleDraw::for_sprite(&sc, [100.0, 50.0], &model(), 1.0).unwrap();
        // ワールドの 2 画素 = 形の空間の 2 単位（100 単位 = 100 画素）
        assert!((style.margin[0] - AA_MARGIN_WORLD).abs() < 1e-5);
        let mut out = Vec::new();
        build_shape_instances(&model(), [1.0; 4], Some(&style), None, None, &mut out);
        let (m, p) = out[0];
        assert_eq!(p.modes[0], SHAPE_KIND_ELLIPSE);
        let top_left = apply(&m, [0.0, 0.0]);
        assert!((top_left[0] - 8.0).abs() < 1e-4 && (top_left[1] - 18.0).abs() < 1e-4);
        let bottom_right = apply(&m, [1.0, 1.0]);
        assert!((bottom_right[0] - 112.0).abs() < 1e-4 && (bottom_right[1] - 72.0).abs() < 1e-4);
    }

    /// 影: 本体より先に積み、ずれとぼかしの 3σ の分だけ広がる。影の色は color から独立（塗りを透明にしても影は出る）。
    #[test]
    fn shadow_comes_first() {
        let sc = SpriteComponent {
            shape: SpriteShape { corner_radii: [10.0; 4], ..SpriteShape::default() },
            shadow: SpriteShadow { enabled: true, color: [0.0, 0.0, 0.0, 0.5], offset: [0.0, 6.0], blur: 8.0 },
            ..SpriteComponent::default()
        };
        let style = SpriteStyleDraw::for_sprite(&sc, [100.0, 50.0], &model(), 1.0).unwrap();
        let mut out = Vec::new();
        build_shape_instances(&model(), [1.0, 0.0, 0.0, 0.5], Some(&style), None, None, &mut out);
        assert_eq!(out.len(), 2);
        let (sm, sp) = out[0];
        assert_ne!(sp.modes[2] & FLAG_SHADOW, 0);
        assert_eq!(sp.colors[0], [0.0, 0.0, 0.0, 0.5]);
        // σ = 4 → 3σ = 12 + 余白 2 = 14。ずれ (0, 6)
        let tl = apply(&sm, [0.0, 0.0]);
        assert!((tl[0] - (10.0 - 14.0)).abs() < 1e-3 && (tl[1] - (20.0 + 6.0 - 14.0)).abs() < 1e-3);
        assert_eq!(out[1].1.modes[2] & FLAG_SHADOW, 0, "2 つ目が本体");
    }

    /// 塗りとの 9 スライス: テクスチャがあるときだけ 9 スライスの旗・格子が入る。
    #[test]
    fn nine_slice_needs_texture() {
        let sc = SpriteComponent {
            nine_slice: SpriteNineSlice { enabled: true, border: [8.0, 8.0, 8.0, 8.0], ..SpriteNineSlice::default() },
            ..SpriteComponent::default()
        };
        let style = SpriteStyleDraw::for_sprite(&sc, [100.0, 50.0], &model(), 1.0).unwrap();
        let mut out = Vec::new();
        build_shape_instances(&model(), [1.0; 4], Some(&style), None, None, &mut out);
        assert_eq!(out[0].1.modes[2] & FLAG_NINE_SLICE, 0);
        assert_eq!(out[0].0, model(), "形の無い 9 スライスは四角形を広げない");
        out.clear();
        build_shape_instances(&model(), [1.0; 4], Some(&style), Some([32, 32]), None, &mut out);
        let p = out[0].1;
        assert_ne!(p.modes[2] & FLAG_NINE_SLICE, 0);
        assert_eq!(p.nine_x, [8.0, 92.0, 0.25, 0.75]);
    }

    /// 付け替えた行列: 形の空間の四角形 (0,0,幅,高さ) ならスプライトの行列と同じ点を写す。
    #[test]
    fn remap_identity_quad() {
        let m = remap_model(&model(), [100.0, 50.0], [0.0, 0.0, 100.0, 50.0]);
        for t in [[0.0, 0.0], [1.0, 1.0], [0.3, 0.7]] {
            let (a, b) = (apply(&m, t), apply(&model(), t));
            assert!((a[0] - b[0]).abs() < 1e-4 && (a[1] - b[1]).abs() < 1e-4);
        }
    }
}
