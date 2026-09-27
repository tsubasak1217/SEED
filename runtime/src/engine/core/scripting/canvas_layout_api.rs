// ============================================================
//  canvas_layout_api.rs — レイアウトの部品（W2-1b）と CanvasGesture（W2-2）・CanvasScroll（W2-3）のスクリプト API（host_api のコンポーネントレジストリの一部）
//
//  C# の SEED.CanvasStack・CanvasWrap・CanvasGrid・CanvasLayoutItem・CanvasSafeArea・CanvasGesture が名前指定で読み書きする欄を、
//  host_api.rs の read_floats / write_floats / has_component / slot_is_kind から 1 行で呼べるようにまとめる
//  （5 種ぶんの分岐を host_api.rs へ並べない）。どれもスロット格納型なので locate でスロットを解決する。
//  CanvasScroll の欄（設定と実行中の状態）は canvas_scroll_api.rs にある（ここは名前の振り分けだけ）。
//
//  【データ表現】（host_api.rs 冒頭の規約どおり、すべて f32 の配列）
//    - bool            … 0 / 1
//    - 数値（間隔など） … 1 要素（NaN・無限大の書き込みは失敗）
//    - 列挙            … 添字（components/canvas_layout_params.rs の IndexedEnum::ALL の並び。C# の列挙の値と一致）
//    - 余白            … 4 要素（左・上・右・下）
//    - Grid の間隔     … 2 要素（列の間隔・行の間隔）
//    - Grid の列数     … 1 要素（0 以上の整数。0 = 自動）
//  欄の名前（C# の文字列）は serde の欄の名前と同じ（padding・main_align …）。
// ============================================================

use crate::engine::components::{
    CanvasGestureComponent, CanvasGridComponent, CanvasLayoutItemComponent, CanvasPadding,
    CanvasSafeAreaComponent, CanvasStackComponent, CanvasWrapComponent, CrossAlign, GestureDragAxis,
    HiddenChildren, IndexedEnum, ItemAlign, LayoutDirection, MainAlign,
};
use crate::engine::ecs::{Component, Entity, World};

/// C# `SEED.CanvasStack` の ComponentKindName（Rust 側の解決キー。完全一致させること）。
pub const KIND_CANVAS_STACK: &str = "CanvasStack";
/// C# `SEED.CanvasWrap` の ComponentKindName。
pub const KIND_CANVAS_WRAP: &str = "CanvasWrap";
/// C# `SEED.CanvasGrid` の ComponentKindName。
pub const KIND_CANVAS_GRID: &str = "CanvasGrid";
/// C# `SEED.CanvasLayoutItem` の ComponentKindName。
pub const KIND_CANVAS_LAYOUT_ITEM: &str = "CanvasLayoutItem";
/// C# `SEED.CanvasSafeArea` の ComponentKindName。
pub const KIND_CANVAS_SAFE_AREA: &str = "CanvasSafeArea";
/// C# `SEED.CanvasGesture` の ComponentKindName（W2-2）。
pub const KIND_CANVAS_GESTURE: &str = "CanvasGesture";
/// C# `SEED.CanvasScroll` の ComponentKindName（W2-3。欄は canvas_scroll_api.rs）。
pub use super::canvas_scroll_api::KIND_CANVAS_SCROLL;

/// 余白の要素数（左・上・右・下）。
const PADDING_LEN: usize = 4;
/// 2 要素の値（Grid の間隔など）の要素数。
const PAIR_LEN: usize = 2;

/// レイアウトの部品のコンポーネント名か。
pub fn is_layout_component(component: &str) -> bool {
    matches!(
        component,
        KIND_CANVAS_STACK
            | KIND_CANVAS_WRAP
            | KIND_CANVAS_GRID
            | KIND_CANVAS_LAYOUT_ITEM
            | KIND_CANVAS_SAFE_AREA
            | KIND_CANVAS_GESTURE
            | KIND_CANVAS_SCROLL
    )
}

/// エンティティ（スロット専用エンティティ）が指定のレイアウトの部品の実体を持つか。
pub fn entity_has(world: &World, entity: Entity, component: &str) -> bool {
    match component {
        KIND_CANVAS_STACK => world.get::<CanvasStackComponent>(entity).is_some(),
        KIND_CANVAS_WRAP => world.get::<CanvasWrapComponent>(entity).is_some(),
        KIND_CANVAS_GRID => world.get::<CanvasGridComponent>(entity).is_some(),
        KIND_CANVAS_LAYOUT_ITEM => world.get::<CanvasLayoutItemComponent>(entity).is_some(),
        KIND_CANVAS_SAFE_AREA => world.get::<CanvasSafeAreaComponent>(entity).is_some(),
        KIND_CANVAS_GESTURE => world.get::<CanvasGestureComponent>(entity).is_some(),
        KIND_CANVAS_SCROLL => world.get::<crate::engine::components::CanvasScrollComponent>(entity).is_some(),
        _ => false,
    }
}

// ─── 読み（out へ書いた要素数を返す）─────────────────────────

/// 値を out へ写して要素数を返す。
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

/// 余白を 4 要素（左・上・右・下）で。
fn put_padding(out: &mut [f32], p: CanvasPadding) -> Option<usize> {
    put(out, &p.to_array())
}

/// レイアウトの部品の数値の欄を読む（コンポーネントが無い・欄が無ければ None）。
///
/// # 引数
/// * `entity` - スロット専用エンティティ（host_api の locate で解決済み）
pub fn read(world: &World, entity: Entity, component: &str, field: &str, out: &mut [f32]) -> Option<usize> {
    match component {
        KIND_CANVAS_STACK => {
            let c = world.get::<CanvasStackComponent>(entity)?;
            match field {
                "enabled" => put_bool(out, c.enabled),
                "direction" => put_enum(out, c.direction),
                "spacing" => put(out, &[c.spacing]),
                "padding" => put_padding(out, c.padding),
                "main_align" => put_enum(out, c.main_align),
                "cross_align" => put_enum(out, c.cross_align),
                "reverse" => put_bool(out, c.reverse),
                "fit_width" => put_bool(out, c.fit_width),
                "fit_height" => put_bool(out, c.fit_height),
                "hidden_children" => put_enum(out, c.hidden_children),
                _ => None,
            }
        }
        KIND_CANVAS_WRAP => {
            let c = world.get::<CanvasWrapComponent>(entity)?;
            match field {
                "enabled" => put_bool(out, c.enabled),
                "direction" => put_enum(out, c.direction),
                "spacing" => put(out, &[c.spacing]),
                "run_spacing" => put(out, &[c.run_spacing]),
                "padding" => put_padding(out, c.padding),
                "main_align" => put_enum(out, c.main_align),
                "cross_align" => put_enum(out, c.cross_align),
                "run_align" => put_enum(out, c.run_align),
                "fit_width" => put_bool(out, c.fit_width),
                "fit_height" => put_bool(out, c.fit_height),
                "hidden_children" => put_enum(out, c.hidden_children),
                _ => None,
            }
        }
        KIND_CANVAS_GRID => {
            let c = world.get::<CanvasGridComponent>(entity)?;
            match field {
                "enabled" => put_bool(out, c.enabled),
                "columns" => put(out, &[c.columns as f32]),
                "cell_min_width" => put(out, &[c.cell_min_width]),
                "cell_aspect_ratio" => put(out, &[c.cell_aspect_ratio]),
                "spacing" => put(out, &[c.spacing_x, c.spacing_y]),
                "padding" => put_padding(out, c.padding),
                "cell_align" => put_enum(out, c.cell_align),
                "fit_width" => put_bool(out, c.fit_width),
                "fit_height" => put_bool(out, c.fit_height),
                "hidden_children" => put_enum(out, c.hidden_children),
                _ => None,
            }
        }
        KIND_CANVAS_LAYOUT_ITEM => {
            let c = world.get::<CanvasLayoutItemComponent>(entity)?;
            match field {
                "ignore_layout" => put_bool(out, c.ignore_layout),
                "flex" => put(out, &[c.flex]),
                "preferred_size" => put(out, &[c.preferred_width, c.preferred_height]),
                "min_size" => put(out, &[c.min_width, c.min_height]),
                "max_size" => put(out, &[c.max_width, c.max_height]),
                "align_self" => put_enum(out, c.align_self),
                "fill_width" => put_bool(out, c.fill_width),
                "fill_height" => put_bool(out, c.fill_height),
                _ => None,
            }
        }
        KIND_CANVAS_SAFE_AREA => {
            let c = world.get::<CanvasSafeAreaComponent>(entity)?;
            match field {
                "enabled" => put_bool(out, c.enabled),
                "left" => put_bool(out, c.left),
                "top" => put_bool(out, c.top),
                "right" => put_bool(out, c.right),
                "bottom" => put_bool(out, c.bottom),
                _ => None,
            }
        }
        // ジェスチャーを受けるノード（W2-2）: 旗は 0/1、軸は添字（GestureDragAxis の並び）、最小のヒット領域は dp
        KIND_CANVAS_GESTURE => {
            let c = world.get::<CanvasGestureComponent>(entity)?;
            match field {
                "enabled" => put_bool(out, c.enabled),
                "tap" => put_bool(out, c.tap),
                "long_press" => put_bool(out, c.long_press),
                "drag" => put_bool(out, c.drag),
                "fling" => put_bool(out, c.fling),
                "drag_axis" => put_enum(out, c.drag_axis),
                "press_feedback" => put_bool(out, c.press_feedback),
                "min_hit_size_dp" => put(out, &[c.min_hit_size_dp]),
                _ => None,
            }
        }
        // スクロールの領域（W2-3。設定と実行中の状態）
        KIND_CANVAS_SCROLL => super::canvas_scroll_api::read(world, entity, field, out),
        _ => None,
    }
}

// ─── 書き（成功なら true）───────────────────────────────

/// 要素数が一致するときだけ固定長の配列にする（C# 側の要素数の誤りを丸めずに検出する）。
fn take<const N: usize>(v: &[f32]) -> Option<[f32; N]> {
    v.try_into().ok()
}

/// 有限の数値 1 つ。
fn take_f32(v: &[f32]) -> Option<f32> {
    take::<1>(v).map(|[x]| x).filter(|x| x.is_finite())
}

/// bool（0 以外は true）。
fn take_bool(v: &[f32]) -> Option<bool> {
    take::<1>(v).map(|[x]| x != 0.0)
}

/// 列挙（添字。範囲外・整数でない値は失敗）。
fn take_enum<E: IndexedEnum>(v: &[f32]) -> Option<E> {
    take::<1>(v).and_then(|[x]| E::from_script_value(x))
}

/// 有限の数値 N 個。
fn take_finite<const N: usize>(v: &[f32]) -> Option<[f32; N]> {
    take::<N>(v).filter(|a| a.iter().all(|x| x.is_finite()))
}

/// 余白（左・上・右・下）。
fn take_padding(v: &[f32]) -> Option<CanvasPadding> {
    take_finite::<PADDING_LEN>(v).map(CanvasPadding::from_array)
}

/// 0 以上の整数（Grid の列数）。
fn take_count(v: &[f32]) -> Option<u32> {
    take_f32(v).filter(|x| *x >= 0.0 && x.fract() == 0.0 && *x <= u32::MAX as f32).map(|x| x as u32)
}

/// 欄を 1 つ書き換える（Some(値) なら代入して true、None なら何もせず false）。
fn assign<T>(slot: &mut T, value: Option<T>) -> bool {
    match value {
        Some(v) => {
            *slot = v;
            true
        }
        None => false,
    }
}

/// コンポーネントを可変で引く（無ければ None）。
fn get_mut<T: Component>(world: &mut World, entity: Entity) -> Option<&mut T> {
    world.get_mut::<T>(entity)
}

/// レイアウトの部品の数値の欄へ書く（成功なら true。値の型・要素数・範囲が合わなければ false）。
///
/// # 引数
/// * `entity` - スロット専用エンティティ（host_api の locate で解決済み）
pub fn write(world: &mut World, entity: Entity, component: &str, field: &str, v: &[f32]) -> bool {
    match component {
        KIND_CANVAS_STACK => {
            let Some(c) = get_mut::<CanvasStackComponent>(world, entity) else { return false };
            match field {
                "enabled" => assign(&mut c.enabled, take_bool(v)),
                "direction" => assign(&mut c.direction, take_enum::<LayoutDirection>(v)),
                "spacing" => assign(&mut c.spacing, take_f32(v)),
                "padding" => assign(&mut c.padding, take_padding(v)),
                "main_align" => assign(&mut c.main_align, take_enum::<MainAlign>(v)),
                "cross_align" => assign(&mut c.cross_align, take_enum::<CrossAlign>(v)),
                "reverse" => assign(&mut c.reverse, take_bool(v)),
                "fit_width" => assign(&mut c.fit_width, take_bool(v)),
                "fit_height" => assign(&mut c.fit_height, take_bool(v)),
                "hidden_children" => assign(&mut c.hidden_children, take_enum::<HiddenChildren>(v)),
                _ => false,
            }
        }
        KIND_CANVAS_WRAP => {
            let Some(c) = get_mut::<CanvasWrapComponent>(world, entity) else { return false };
            match field {
                "enabled" => assign(&mut c.enabled, take_bool(v)),
                "direction" => assign(&mut c.direction, take_enum::<LayoutDirection>(v)),
                "spacing" => assign(&mut c.spacing, take_f32(v)),
                "run_spacing" => assign(&mut c.run_spacing, take_f32(v)),
                "padding" => assign(&mut c.padding, take_padding(v)),
                "main_align" => assign(&mut c.main_align, take_enum::<MainAlign>(v)),
                "cross_align" => assign(&mut c.cross_align, take_enum::<CrossAlign>(v)),
                "run_align" => assign(&mut c.run_align, take_enum::<MainAlign>(v)),
                "fit_width" => assign(&mut c.fit_width, take_bool(v)),
                "fit_height" => assign(&mut c.fit_height, take_bool(v)),
                "hidden_children" => assign(&mut c.hidden_children, take_enum::<HiddenChildren>(v)),
                _ => false,
            }
        }
        KIND_CANVAS_GRID => {
            let Some(c) = get_mut::<CanvasGridComponent>(world, entity) else { return false };
            match field {
                "enabled" => assign(&mut c.enabled, take_bool(v)),
                "columns" => assign(&mut c.columns, take_count(v)),
                "cell_min_width" => assign(&mut c.cell_min_width, take_f32(v)),
                "cell_aspect_ratio" => assign(&mut c.cell_aspect_ratio, take_f32(v)),
                "spacing" => match take_finite::<PAIR_LEN>(v) {
                    Some([x, y]) => {
                        c.spacing_x = x;
                        c.spacing_y = y;
                        true
                    }
                    None => false,
                },
                "padding" => assign(&mut c.padding, take_padding(v)),
                "cell_align" => assign(&mut c.cell_align, take_enum::<CrossAlign>(v)),
                "fit_width" => assign(&mut c.fit_width, take_bool(v)),
                "fit_height" => assign(&mut c.fit_height, take_bool(v)),
                "hidden_children" => assign(&mut c.hidden_children, take_enum::<HiddenChildren>(v)),
                _ => false,
            }
        }
        KIND_CANVAS_LAYOUT_ITEM => {
            let Some(c) = get_mut::<CanvasLayoutItemComponent>(world, entity) else { return false };
            match field {
                "ignore_layout" => assign(&mut c.ignore_layout, take_bool(v)),
                "flex" => assign(&mut c.flex, take_f32(v)),
                "preferred_size" => match take_finite::<PAIR_LEN>(v) {
                    Some([w, h]) => {
                        c.preferred_width = w;
                        c.preferred_height = h;
                        true
                    }
                    None => false,
                },
                "min_size" => match take_finite::<PAIR_LEN>(v) {
                    Some([w, h]) => {
                        c.min_width = w;
                        c.min_height = h;
                        true
                    }
                    None => false,
                },
                "max_size" => match take_finite::<PAIR_LEN>(v) {
                    Some([w, h]) => {
                        c.max_width = w;
                        c.max_height = h;
                        true
                    }
                    None => false,
                },
                "align_self" => assign(&mut c.align_self, take_enum::<ItemAlign>(v)),
                "fill_width" => assign(&mut c.fill_width, take_bool(v)),
                "fill_height" => assign(&mut c.fill_height, take_bool(v)),
                _ => false,
            }
        }
        KIND_CANVAS_SAFE_AREA => {
            let Some(c) = get_mut::<CanvasSafeAreaComponent>(world, entity) else { return false };
            match field {
                "enabled" => assign(&mut c.enabled, take_bool(v)),
                "left" => assign(&mut c.left, take_bool(v)),
                "top" => assign(&mut c.top, take_bool(v)),
                "right" => assign(&mut c.right, take_bool(v)),
                "bottom" => assign(&mut c.bottom, take_bool(v)),
                _ => false,
            }
        }
        // ジェスチャーを受けるノード（W2-2）。書き換えは次に触れた指から効く（触れている指はそのときの値のまま）
        KIND_CANVAS_GESTURE => {
            let Some(c) = get_mut::<CanvasGestureComponent>(world, entity) else { return false };
            match field {
                "enabled" => assign(&mut c.enabled, take_bool(v)),
                "tap" => assign(&mut c.tap, take_bool(v)),
                "long_press" => assign(&mut c.long_press, take_bool(v)),
                "drag" => assign(&mut c.drag, take_bool(v)),
                "fling" => assign(&mut c.fling, take_bool(v)),
                "drag_axis" => assign(&mut c.drag_axis, take_enum::<GestureDragAxis>(v)),
                "press_feedback" => assign(&mut c.press_feedback, take_bool(v)),
                "min_hit_size_dp" => assign(&mut c.min_hit_size_dp, take_f32(v).filter(|x| *x >= 0.0)),
                _ => false,
            }
        }
        // スクロールの領域（W2-3。位置の書き込みはすぐ移す・scroll_to は次のフレームから動かす）
        KIND_CANVAS_SCROLL => super::canvas_scroll_api::write(world, entity, field, v),
        _ => false,
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 読み書きの往復（列挙の添字・余白 4 要素・Grid の間隔 2 要素・bool）。
    #[test]
    fn read_write_round_trip() {
        let mut world = World::new();
        let e = world.spawn();
        world.insert(e, CanvasStackComponent::default());
        let mut out = [0.0f32; 8];
        assert!(write(&mut world, e, KIND_CANVAS_STACK, "direction", &[1.0]));
        assert_eq!(read(&world, e, KIND_CANVAS_STACK, "direction", &mut out), Some(1));
        assert_eq!(out[0], 1.0);
        assert!(write(&mut world, e, KIND_CANVAS_STACK, "padding", &[1.0, 2.0, 3.0, 4.0]));
        assert_eq!(read(&world, e, KIND_CANVAS_STACK, "padding", &mut out), Some(4));
        assert_eq!(&out[..4], &[1.0, 2.0, 3.0, 4.0]);
        assert_eq!(world.get::<CanvasStackComponent>(e).unwrap().padding.bottom, 4.0);

        let g = world.spawn();
        world.insert(g, CanvasGridComponent::default());
        assert!(write(&mut world, g, KIND_CANVAS_GRID, "spacing", &[6.0, 8.0]));
        assert_eq!(read(&world, g, KIND_CANVAS_GRID, "spacing", &mut out), Some(2));
        assert_eq!(&out[..2], &[6.0, 8.0]);
    }

    /// 範囲外の列挙・整数でない列数・要素数の違い・NaN・知らない欄は書かない。
    #[test]
    fn rejects_bad_values() {
        let mut world = World::new();
        let g = world.spawn();
        world.insert(g, CanvasGridComponent::default());
        assert!(!write(&mut world, g, KIND_CANVAS_GRID, "cell_align", &[9.0]));
        assert!(!write(&mut world, g, KIND_CANVAS_GRID, "columns", &[2.5]));
        assert!(!write(&mut world, g, KIND_CANVAS_GRID, "columns", &[-1.0]));
        assert!(!write(&mut world, g, KIND_CANVAS_GRID, "padding", &[1.0, 2.0]));
        assert!(!write(&mut world, g, KIND_CANVAS_GRID, "cell_min_width", &[f32::NAN]));
        assert!(!write(&mut world, g, KIND_CANVAS_GRID, "no_such", &[1.0]));
        assert_eq!(world.get::<CanvasGridComponent>(g).unwrap(), &CanvasGridComponent::default());
        assert!(write(&mut world, g, KIND_CANVAS_GRID, "columns", &[0.0]), "0 = 自動は書ける");
    }

    /// CanvasGesture（W2-2）の旗・軸・大きさの読み書き（範囲外の軸・負の大きさは書かない）。
    #[test]
    fn gesture_fields_round_trip() {
        let mut world = World::new();
        let e = world.spawn();
        world.insert(e, CanvasGestureComponent::default());
        let mut out = [0.0f32; 4];
        assert!(write(&mut world, e, KIND_CANVAS_GESTURE, "drag", &[1.0]));
        assert!(write(&mut world, e, KIND_CANVAS_GESTURE, "drag_axis", &[2.0]));
        assert!(!write(&mut world, e, KIND_CANVAS_GESTURE, "drag_axis", &[3.0]));
        assert!(!write(&mut world, e, KIND_CANVAS_GESTURE, "min_hit_size_dp", &[-1.0]));
        assert!(write(&mut world, e, KIND_CANVAS_GESTURE, "min_hit_size_dp", &[56.0]));
        assert_eq!(read(&world, e, KIND_CANVAS_GESTURE, "drag_axis", &mut out), Some(1));
        assert_eq!(out[0], 2.0);
        let c = world.get::<CanvasGestureComponent>(e).unwrap();
        assert!(c.drag && c.drag_axis == GestureDragAxis::Vertical && c.min_hit_size_dp == 56.0);
        assert!(is_layout_component(KIND_CANVAS_GESTURE) && entity_has(&world, e, KIND_CANVAS_GESTURE));
    }

    /// 名前の判定と実体の判定（種類の違うエンティティは false）。
    #[test]
    fn component_names_and_presence() {
        let mut world = World::new();
        let s = world.spawn();
        world.insert(s, CanvasSafeAreaComponent::default());
        assert!(is_layout_component(KIND_CANVAS_SAFE_AREA));
        assert!(!is_layout_component("Sprite"));
        assert!(entity_has(&world, s, KIND_CANVAS_SAFE_AREA));
        assert!(!entity_has(&world, s, KIND_CANVAS_STACK));
    }
}
