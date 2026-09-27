// ============================================================
//  sprite_style_ipc.rs — エディタのインスペクタからの形と塗り・切り抜きの形の欄の編集（W2-4）
//
//  `SET_SPRITE_FIELD:{actor},{slot},{key},{value}` と `SET_CANVAS_CLIP_FIELD:…` のうち、W2-4 で足した欄を
//  値の文字列から解釈して、スクリプトと同じ書き込み（scripting/sprite_style_api.rs）へ渡す。
//  欄名はスクリプトと同じ（sprite_style_api.rs の冒頭の表）。値の書き方:
//    - 列挙: serde の名前（shape: rect / ellipse / arc、fill: solid / linear / radial、
//      nine_slice_edge・nine_slice_center: stretch / repeat、切り抜きの shape: rect / rounded_rect / ellipse / sprite_shape）
//    - bool: true / false（1 / 0 も可）
//    - 数値: カンマ区切りの数（色 4・四隅 4・2 次元 2・fill_colors は 4 × 色の数）
//  インスペクタへ送る JSON（`style_json`・`clip_style_json`）も serde の書式のままここで作る。
// ============================================================

use crate::engine::components::canvas_layout_params::IndexedEnum;
use crate::engine::components::{
    CanvasClipComponent, ClipShapeMode, NineSliceMode, SpriteComponent, SpriteFillKind, SpriteShapeKind,
};
use crate::engine::core::scripting::sprite_style_api::{write_clip, write_sprite};

/// bool の欄（値の文字列が true / false）。
const BOOL_KEYS: &[&str] = &["arc_round_caps", "nine_slice", "nine_slice_fill_center", "shadow", "fill_stops_clear"];

/// 値の文字列を bool にする（true / false / 1 / 0。前後の空白は許す）【純関数】。
fn parse_bool(value: &str) -> Option<bool> {
    match value.trim() {
        "true" | "1" => Some(true),
        "false" | "0" => Some(false),
        _ => None,
    }
}

/// カンマ区切りの数を読む（1 つでも読めなければ None）【純関数】。
fn parse_floats(value: &str) -> Option<Vec<f32>> {
    value.split(',').map(|t| t.trim().parse::<f32>().ok()).collect()
}

/// 列挙の欄の値（serde の名前）をスクリプトの添字へ直す【純関数】（列挙の欄でなければ None）。
fn sprite_enum_value(key: &str, value: &str) -> Option<Option<f32>> {
    let index = match key {
        "shape" => SpriteShapeKind::from_key(value).map(|k| k.to_script_value()),
        "fill" => SpriteFillKind::from_key(value).map(|k| k.to_script_value()),
        "nine_slice_edge" | "nine_slice_center" => NineSliceMode::from_key(value).map(|m| m.to_script_value()),
        _ => return None,
    };
    Some(index)
}

/// スプライトの形と塗りの欄を 1 つ書く【純関数】（知らない欄・読めない値は false で何も変えない）。
///
/// # 引数
/// * `sc`    - スプライト
/// * `key`   - 欄名（スクリプトと同じ）
/// * `value` - 値の文字列（冒頭の書き方）
pub(super) fn apply_sprite_style_field(sc: &mut SpriteComponent, key: &str, value: &str) -> bool {
    if let Some(index) = sprite_enum_value(key, value) {
        return index.is_some_and(|i| write_sprite(sc, key, &[i]));
    }
    if BOOL_KEYS.contains(&key) {
        return parse_bool(value).is_some_and(|b| write_sprite(sc, key, &[if b { 1.0 } else { 0.0 }]));
    }
    parse_floats(value).is_some_and(|v| write_sprite(sc, key, &v))
}

/// 切り抜きの形の欄を 1 つ書く【純関数】（shape は serde の名前、corner_radii はカンマ区切りの 4 つ）。
pub(super) fn apply_clip_shape_field(clip: &mut CanvasClipComponent, key: &str, value: &str) -> bool {
    match key {
        "shape" => ClipShapeMode::from_key(value).is_some_and(|m| write_clip(clip, key, &[m.to_script_value()])),
        "corner_radii" => parse_floats(value).is_some_and(|v| write_clip(clip, key, &v)),
        _ => false,
    }
}

/// インスペクタへ送るスプライトの形と塗りの JSON（serde の書式。既定の欄も省かずに送る）【純関数】。
///
/// 形: `{"shape":{…},"fill":{…},"nine_slice":{…},"shadow":{…}}`。
pub(super) fn style_json(sc: &SpriteComponent) -> String {
    let value = serde_json::json!({
        "shape": serde_json::to_value(&sc.shape).unwrap_or_default(),
        "fill": serde_json::to_value(&sc.fill).unwrap_or_default(),
        "nine_slice": serde_json::to_value(&sc.nine_slice).unwrap_or_default(),
        "shadow": serde_json::to_value(&sc.shadow).unwrap_or_default(),
    });
    value.to_string()
}

/// インスペクタへ送る切り抜きの形の JSON（`{"shape":"…","corner_radii":[…]}`）【純関数】。
pub(super) fn clip_style_json(clip: &CanvasClipComponent) -> String {
    serde_json::json!({ "shape": clip.shape.key(), "corner_radii": clip.corner_radii }).to_string()
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 列挙は serde の名前、bool は true / false、数はカンマ区切りで書ける。
    #[test]
    fn inspector_values_are_parsed() {
        let mut sc = SpriteComponent::default();
        assert!(apply_sprite_style_field(&mut sc, "shape", "ellipse"));
        assert_eq!(sc.shape.kind, SpriteShapeKind::Ellipse);
        assert!(apply_sprite_style_field(&mut sc, "corner_radii", "1, 2,3,4"));
        assert_eq!(sc.shape.corner_radii, [1.0, 2.0, 3.0, 4.0]);
        assert!(apply_sprite_style_field(&mut sc, "shadow", "true"));
        assert!(sc.shadow.enabled);
        assert!(apply_sprite_style_field(&mut sc, "fill", "linear"));
        assert!(apply_sprite_style_field(&mut sc, "fill_colors", "1,0,0,1,0,0,1,1"));
        assert_eq!(sc.fill.colors.len(), 2);
        assert!(apply_sprite_style_field(&mut sc, "nine_slice_center", "repeat"));
        assert_eq!(sc.nine_slice.center_mode, NineSliceMode::Repeat);
        // 読めない値・知らない欄は何も変えない
        assert!(!apply_sprite_style_field(&mut sc, "shape", "star"));
        assert!(!apply_sprite_style_field(&mut sc, "border_width", "abc"));
        assert!(!apply_sprite_style_field(&mut sc, "unknown", "1"));
    }

    /// 切り抜きの形と、インスペクタへ送る JSON の形。
    #[test]
    fn clip_fields_and_json() {
        let mut clip = CanvasClipComponent::default();
        assert!(apply_clip_shape_field(&mut clip, "shape", "rounded_rect"));
        assert!(apply_clip_shape_field(&mut clip, "corner_radii", "8,8,8,8"));
        assert_eq!(clip.shape, ClipShapeMode::RoundedRect);
        let json: serde_json::Value = serde_json::from_str(&clip_style_json(&clip)).unwrap();
        assert_eq!(json["shape"], "rounded_rect");
        let style: serde_json::Value = serde_json::from_str(&style_json(&SpriteComponent::default())).unwrap();
        assert_eq!(style["shape"]["kind"], "rect");
        assert_eq!(style["fill"]["kind"], "solid");
        assert_eq!(style["nine_slice"]["enabled"], false);
    }
}
