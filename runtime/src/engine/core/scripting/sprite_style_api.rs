// ============================================================
//  scripting/sprite_style_api.rs — スプライトの形と塗り・切り抜きの形のスクリプトの欄（W2-4）
//
//  host_api.rs の "Sprite"・"CanvasClip" の分岐から、既存の欄（color・width など）に当たらなかった欄名で呼ばれる。
//  C# のラッパー（scripting/src/Api/Sprite.cs・CanvasClip.cs・SpriteStyleTypes.cs）と欄名・要素数・列挙の添字を一致させること。
//
//  【データの表現】（host_api.rs の冒頭の規約と同じ）
//    - 数値は float の列。色 = 4、2 次元 = 2、四隅 = 4（左上・右上・右下・左下）、9 スライスの枠 = 4（左・上・右・下）
//    - bool は 0 / 1
//    - 列挙は添字（`IndexedEnum::ALL` の並び。C# の列挙の値と一致）
//    - 読み取りは 4 要素まで（FFI の読み取りの上限）なので、グラデーションの色は 1 色ずつ（fill_color0〜3）と数（fill_color_count）
//
//  | 欄                        | 読 | 書 | 要素 | 内容                                              |
//  |---------------------------|----|----|------|---------------------------------------------------|
//  | shape                     | ○ | ○ | 1    | 形（SpriteShapeKind の添字: 0 矩形・1 楕円・2 弧）   |
//  | corner_radii              | ○ | ○ | 4    | 四隅の角丸（キャンバスの単位。負は 0）              |
//  | border_width              | ○ | ○ | 1    | 縁の太さ（負は 0）                                  |
//  | border_color              | ○ | ○ | 4    | 縁の色                                              |
//  | arc_start / arc_sweep     | ○ | ○ | 1    | 弧の開始角・角度（度）                              |
//  | arc_thickness             | ○ | ○ | 1    | 弧の太さ（負は 0）                                  |
//  | arc_round_caps            | ○ | ○ | 1    | 弧の端を丸くするか                                  |
//  | fill                      | ○ | ○ | 1    | 塗り（SpriteFillKind の添字: 0 単色・1 線形・2 放射） |
//  | fill_colors               |    | ○ | 4n   | グラデーションの色（n = 2〜4）                      |
//  | fill_color0〜3            | ○ | ○ | 4    | グラデーションの i 番目の色（書くと足りない色を補う）|
//  | fill_color_count          | ○ |    | 1    | グラデーションの色の数                              |
//  | fill_stops                | ○ | ○ | 0〜4 | 色の位置（読むと n 要素。空なら 0 要素＝等間隔）    |
//  | fill_stops_clear          |    | ○ | 1    | 色の位置を消す（等間隔へ）                          |
//  | fill_angle                | ○ | ○ | 1    | 線形の角度（度）                                    |
//  | fill_center / fill_radius | ○ | ○ | 2    | 放射の中心・半径（矩形に対する割合）                |
//  | nine_slice                | ○ | ○ | 1    | 9 スライスで描くか                                  |
//  | nine_slice_border         | ○ | ○ | 4    | 枠の幅（左・上・右・下。テクスチャの画素。負は 0）  |
//  | nine_slice_scale          | ○ | ○ | 1    | 描く枠の倍率（負は 0）                              |
//  | nine_slice_edge / _center | ○ | ○ | 1    | 辺・中央の埋め方（NineSliceMode の添字: 0 伸ばす・1 繰り返す）|
//  | nine_slice_fill_center    | ○ | ○ | 1    | 中央を描くか                                        |
//  | shadow                    | ○ | ○ | 1    | 影を描くか                                          |
//  | shadow_color              | ○ | ○ | 4    | 影の色                                              |
//  | shadow_offset             | ○ | ○ | 2    | 影のずれ                                            |
//  | shadow_blur               | ○ | ○ | 1    | 影のぼかし（負は 0）                                |
//  CanvasClip: shape（ClipShapeMode の添字: 0 矩形・1 角丸・2 楕円・3 スプライトの形）・corner_radii（4）。
// ============================================================

use crate::engine::components::canvas_layout_params::IndexedEnum;
use crate::engine::components::sprite_style::{GRADIENT_MAX_COLORS, GRADIENT_MIN_COLORS};
use crate::engine::components::{
    CanvasClipComponent, ClipShapeMode, NineSliceMode, SpriteComponent, SpriteFillKind, SpriteShapeKind,
};

/// 色の要素数。
const COLOR_LEN: usize = 4;
/// 「i 番目の色」の欄名の接頭辞。
const FILL_COLOR_PREFIX: &str = "fill_color";

impl IndexedEnum for SpriteShapeKind {
    const ALL: &'static [Self] = &[SpriteShapeKind::Rect, SpriteShapeKind::Ellipse, SpriteShapeKind::Arc];
}

impl IndexedEnum for SpriteFillKind {
    const ALL: &'static [Self] = &[SpriteFillKind::Solid, SpriteFillKind::Linear, SpriteFillKind::Radial];
}

impl IndexedEnum for NineSliceMode {
    const ALL: &'static [Self] = &[NineSliceMode::Stretch, NineSliceMode::Repeat];
}

impl IndexedEnum for ClipShapeMode {
    const ALL: &'static [Self] = &[
        ClipShapeMode::Rect,
        ClipShapeMode::RoundedRect,
        ClipShapeMode::Ellipse,
        ClipShapeMode::SpriteShape,
    ];
}

// ── 読み取りの小道具 ──

/// out の先頭へ書き、要素数を返す（out が短ければ失敗）。
fn put(out: &mut [f32], v: &[f32]) -> Option<usize> {
    out.get_mut(..v.len())?.copy_from_slice(v);
    Some(v.len())
}

/// bool を 0 / 1 で。
fn put_bool(out: &mut [f32], b: bool) -> Option<usize> {
    put(out, &[if b { 1.0 } else { 0.0 }])
}

/// 列挙を添字で。
fn put_enum<E: IndexedEnum>(out: &mut [f32], e: E) -> Option<usize> {
    put(out, &[e.to_script_value()])
}

// ── 書き込みの小道具 ──

/// 有限の数値ちょうど N 個。
fn take<const N: usize>(v: &[f32]) -> Option<[f32; N]> {
    let arr: [f32; N] = v.try_into().ok()?;
    arr.iter().all(|x| x.is_finite()).then_some(arr)
}

/// 0 以上へ収めた数値 1 個。
fn take_non_negative(v: &[f32]) -> Option<f32> {
    take::<1>(v).map(|[x]| x.max(0.0))
}

/// bool（0 以外は true）。
fn take_bool(v: &[f32]) -> Option<bool> {
    take::<1>(v).map(|[x]| x != 0.0)
}

/// 列挙（添字。範囲外・整数でない値は失敗）。
fn take_enum<E: IndexedEnum>(v: &[f32]) -> Option<E> {
    take::<1>(v).and_then(|[x]| E::from_script_value(x))
}

/// 「fill_color{i}」の i（0〜3）。
fn fill_color_index(field: &str) -> Option<usize> {
    let rest = field.strip_prefix(FILL_COLOR_PREFIX)?;
    let i: usize = rest.parse().ok()?;
    (i < GRADIENT_MAX_COLORS).then_some(i)
}

/// スプライトの形と塗りの欄を読む（知らない欄は None）。
///
/// # 引数
/// * `s`     - スプライト
/// * `field` - 欄名（冒頭の表）
/// * `out`   - 書き込み先（4 要素以上）
pub fn read_sprite(s: &SpriteComponent, field: &str, out: &mut [f32]) -> Option<usize> {
    match field {
        // ── 形 ──
        "shape" => put_enum(out, s.shape.kind),
        "corner_radii" => put(out, &s.shape.corner_radii),
        "border_width" => put(out, &[s.shape.border_width]),
        "border_color" => put(out, &s.shape.border_color),
        "arc_start" => put(out, &[s.shape.arc_start]),
        "arc_sweep" => put(out, &[s.shape.arc_sweep]),
        "arc_thickness" => put(out, &[s.shape.arc_thickness]),
        "arc_round_caps" => put_bool(out, s.shape.arc_round_caps),
        // ── 塗り ──
        "fill" => put_enum(out, s.fill.kind),
        "fill_color_count" => put(out, &[s.fill.colors.len() as f32]),
        "fill_stops" => {
            let n = s.fill.stops.len().min(GRADIENT_MAX_COLORS);
            // 空（等間隔）は 0 要素
            if n == 0 {
                return Some(0);
            }
            put(out, &s.fill.stops[..n])
        }
        "fill_angle" => put(out, &[s.fill.angle]),
        "fill_center" => put(out, &s.fill.center),
        "fill_radius" => put(out, &s.fill.radius),
        // ── 9 スライス ──
        "nine_slice" => put_bool(out, s.nine_slice.enabled),
        "nine_slice_border" => put(out, &s.nine_slice.border),
        "nine_slice_scale" => put(out, &[s.nine_slice.scale]),
        "nine_slice_edge" => put_enum(out, s.nine_slice.edge_mode),
        "nine_slice_center" => put_enum(out, s.nine_slice.center_mode),
        "nine_slice_fill_center" => put_bool(out, s.nine_slice.fill_center),
        // ── 影 ──
        "shadow" => put_bool(out, s.shadow.enabled),
        "shadow_color" => put(out, &s.shadow.color),
        "shadow_offset" => put(out, &s.shadow.offset),
        "shadow_blur" => put(out, &[s.shadow.blur]),
        // グラデーションの i 番目の色
        other => {
            let i = fill_color_index(other)?;
            put(out, s.fill.colors.get(i)?)
        }
    }
}

/// スプライトの形と塗りの欄へ書く（知らない欄・要素数や値が不正なら false で何も変えない）。
///
/// # 引数
/// * `s`     - スプライト
/// * `field` - 欄名（冒頭の表）
/// * `v`     - 値
pub fn write_sprite(s: &mut SpriteComponent, field: &str, v: &[f32]) -> bool {
    match field {
        // ── 形 ──
        "shape" => take_enum(v).map(|k| s.shape.kind = k).is_some(),
        "corner_radii" => take::<4>(v).map(|r| s.shape.corner_radii = r.map(|x| x.max(0.0))).is_some(),
        "border_width" => take_non_negative(v).map(|x| s.shape.border_width = x).is_some(),
        "border_color" => take::<COLOR_LEN>(v).map(|c| s.shape.border_color = c).is_some(),
        "arc_start" => take::<1>(v).map(|[x]| s.shape.arc_start = x).is_some(),
        "arc_sweep" => take::<1>(v).map(|[x]| s.shape.arc_sweep = x).is_some(),
        "arc_thickness" => take_non_negative(v).map(|x| s.shape.arc_thickness = x).is_some(),
        "arc_round_caps" => take_bool(v).map(|b| s.shape.arc_round_caps = b).is_some(),
        // ── 塗り ──
        "fill" => take_enum(v).map(|k| s.fill.kind = k).is_some(),
        "fill_colors" => {
            let n = v.len() / COLOR_LEN;
            let valid = v.len() % COLOR_LEN == 0
                && (GRADIENT_MIN_COLORS..=GRADIENT_MAX_COLORS).contains(&n)
                && v.iter().all(|x| x.is_finite());
            if !valid {
                return false;
            }
            s.fill.colors = v.chunks_exact(COLOR_LEN).map(|c| [c[0], c[1], c[2], c[3]]).collect();
            true
        }
        "fill_stops" => {
            let valid = !v.is_empty() && v.len() <= GRADIENT_MAX_COLORS && v.iter().all(|x| x.is_finite());
            if valid {
                s.fill.stops = v.to_vec();
            }
            valid
        }
        "fill_stops_clear" => take_bool(v).map(|b| if b { s.fill.stops.clear() }).is_some(),
        "fill_angle" => take::<1>(v).map(|[x]| s.fill.angle = x).is_some(),
        "fill_center" => take::<2>(v).map(|c| s.fill.center = c).is_some(),
        "fill_radius" => take::<2>(v).map(|r| s.fill.radius = r.map(|x| x.max(0.0))).is_some(),
        // ── 9 スライス ──
        "nine_slice" => take_bool(v).map(|b| s.nine_slice.enabled = b).is_some(),
        "nine_slice_border" => take::<4>(v).map(|b| s.nine_slice.border = b.map(|x| x.max(0.0))).is_some(),
        "nine_slice_scale" => take_non_negative(v).map(|x| s.nine_slice.scale = x).is_some(),
        "nine_slice_edge" => take_enum(v).map(|m| s.nine_slice.edge_mode = m).is_some(),
        "nine_slice_center" => take_enum(v).map(|m| s.nine_slice.center_mode = m).is_some(),
        "nine_slice_fill_center" => take_bool(v).map(|b| s.nine_slice.fill_center = b).is_some(),
        // ── 影 ──
        "shadow" => take_bool(v).map(|b| s.shadow.enabled = b).is_some(),
        "shadow_color" => take::<COLOR_LEN>(v).map(|c| s.shadow.color = c).is_some(),
        "shadow_offset" => take::<2>(v).map(|o| s.shadow.offset = o).is_some(),
        "shadow_blur" => take_non_negative(v).map(|x| s.shadow.blur = x).is_some(),
        // グラデーションの i 番目の色（足りない色は最後の色で補ってから書く）
        other => {
            let (Some(i), Some(c)) = (fill_color_index(other), take::<COLOR_LEN>(v)) else {
                return false;
            };
            let last = s.fill.colors.last().copied().unwrap_or(c);
            while s.fill.colors.len() <= i {
                s.fill.colors.push(last);
            }
            s.fill.colors[i] = c;
            true
        }
    }
}

/// 切り抜きの形の欄を読む（知らない欄は None）。
pub fn read_clip(c: &CanvasClipComponent, field: &str, out: &mut [f32]) -> Option<usize> {
    match field {
        "shape" => put_enum(out, c.shape),
        "corner_radii" => put(out, &c.corner_radii),
        _ => None,
    }
}

/// 切り抜きの形の欄へ書く（知らない欄・不正な値は false）。
pub fn write_clip(c: &mut CanvasClipComponent, field: &str, v: &[f32]) -> bool {
    match field {
        "shape" => take_enum(v).map(|m| c.shape = m).is_some(),
        "corner_radii" => take::<4>(v).map(|r| c.corner_radii = r.map(|x| x.max(0.0))).is_some(),
        _ => false,
    }
}

// ============================================================
//  単体テスト（欄の読み書きの往復）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 読み取りの作業場所（FFI の読み取りの上限と同じ 4 要素）。
    fn read(s: &SpriteComponent, field: &str) -> Option<Vec<f32>> {
        let mut out = [0.0f32; 4];
        read_sprite(s, field, &mut out).map(|n| out[..n].to_vec())
    }

    /// 形・塗り・9 スライス・影の欄の往復。
    #[test]
    fn sprite_fields_round_trip() {
        let mut s = SpriteComponent::default();
        assert!(write_sprite(&mut s, "shape", &[1.0]));
        assert_eq!(s.shape.kind, SpriteShapeKind::Ellipse);
        assert!(write_sprite(&mut s, "corner_radii", &[4.0, -1.0, 8.0, 0.0]));
        assert_eq!(read(&s, "corner_radii").unwrap(), vec![4.0, 0.0, 8.0, 0.0], "負は 0");
        assert!(write_sprite(&mut s, "fill", &[2.0]));
        assert_eq!(s.fill.kind, SpriteFillKind::Radial);
        assert!(write_sprite(&mut s, "fill_colors", &[1.0, 0.0, 0.0, 1.0, 0.0, 1.0, 0.0, 1.0, 0.0, 0.0, 1.0, 1.0]));
        assert_eq!(read(&s, "fill_color_count").unwrap(), vec![3.0]);
        assert_eq!(read(&s, "fill_color2").unwrap(), vec![0.0, 0.0, 1.0, 1.0]);
        assert!(write_sprite(&mut s, "fill_stops", &[0.0, 0.3, 1.0]));
        assert_eq!(read(&s, "fill_stops").unwrap(), vec![0.0, 0.3, 1.0]);
        assert!(write_sprite(&mut s, "fill_stops_clear", &[1.0]));
        assert_eq!(read(&s, "fill_stops").unwrap(), Vec::<f32>::new());
        assert!(write_sprite(&mut s, "nine_slice_edge", &[1.0]));
        assert_eq!(s.nine_slice.edge_mode, NineSliceMode::Repeat);
        assert!(write_sprite(&mut s, "shadow_offset", &[2.0, 3.0]));
        assert_eq!(read(&s, "shadow_offset").unwrap(), vec![2.0, 3.0]);
    }

    /// 不正な値は書かない（範囲外の列挙・色の数・要素数・NaN）。
    #[test]
    fn invalid_values_are_rejected() {
        let mut s = SpriteComponent::default();
        assert!(!write_sprite(&mut s, "shape", &[3.0]));
        assert!(!write_sprite(&mut s, "shape", &[0.5]));
        assert!(!write_sprite(&mut s, "fill_colors", &[1.0, 1.0, 1.0, 1.0]), "1 色は不可");
        assert!(!write_sprite(&mut s, "fill_colors", &[1.0; 20]), "5 色は不可");
        assert!(!write_sprite(&mut s, "border_width", &[f32::NAN]));
        assert!(!write_sprite(&mut s, "corner_radii", &[1.0, 2.0]));
        assert!(!write_sprite(&mut s, "fill_color4", &[1.0; 4]));
        assert!(s.is_plain_style(), "何も変わっていない");
    }

    /// 色を 1 つずつ書くと足りない色を最後の色で補う。
    #[test]
    fn single_color_write_extends() {
        let mut s = SpriteComponent::default();
        assert!(write_sprite(&mut s, "fill_color3", &[0.0, 0.0, 1.0, 1.0]));
        assert_eq!(s.fill.colors.len(), 4);
        assert_eq!(s.fill.colors[2], s.fill.colors[1]);
    }

    /// 切り抜きの形の往復。
    #[test]
    fn clip_fields_round_trip() {
        let mut c = CanvasClipComponent::default();
        assert!(write_clip(&mut c, "shape", &[3.0]));
        assert_eq!(c.shape, ClipShapeMode::SpriteShape);
        assert!(write_clip(&mut c, "corner_radii", &[1.0, 2.0, 3.0, 4.0]));
        let mut out = [0.0f32; 4];
        assert_eq!(read_clip(&c, "corner_radii", &mut out), Some(4));
        assert_eq!(out, [1.0, 2.0, 3.0, 4.0]);
        assert!(!write_clip(&mut c, "shape", &[4.0]));
    }
}
