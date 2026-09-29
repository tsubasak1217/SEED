// ============================================================
//  canvas_scroll_api.rs — CanvasScroll（W2-3）のスクリプト API の欄（canvas_layout_api.rs から呼ぶ）
//
//  C# の SEED.CanvasScroll が名前指定で読み書きする欄。設定（CanvasScrollComponent。保存する）と、実行中の状態
//  （CanvasScrollState。同じスロットのエンティティ。保存しない）の両方を 1 つのコンポーネント名 "CanvasScroll" で扱う。
//
//  【データ表現】（host_api.rs 冒頭の規約どおり、すべて f32 の配列）
//    設定: enabled / inertia / hand_off_to_parent / cull_outside … 0/1、direction / edge / snap / content_size … 添字、
//          snap_interval / cache_extent（0 以上）・fling_friction（正）・bounce_drag（0 と 1 の間）… 1 要素、
//          fixed_content_size … 2 要素（Fixed のときの幅・高さ。0 以上）
//    状態（読み）: position / velocity / viewport_size / content_extent / max_position … 2 要素、
//          phase … 段階の番号（ScrollPhase）、is_scrolling / is_dragging / has_metrics … 0/1
//    状態（書き）: position … 2 要素（すぐ移す＝Jump。動きを止め、範囲へ収める）、
//          scroll_to … 3 要素（x, y, 秒。0 以下ならすぐ移す。次のフレームのスクロールのシステムが動かし始める）
//    状態（読み書き）: end_inset … 2 要素（中身の末尾に足す余白。0 以上。実行中だけ。W2-6b の入力欄がキーボードを避けるのに使う）
//  値の単位はキャンバスの単位（dp のキャンバスなら dp）。
// ============================================================

use crate::engine::components::{
    CanvasScrollComponent, IndexedEnum, ScrollContentSize, ScrollDirection, ScrollEdge, ScrollSnap,
};
use crate::engine::core::canvas_scroll::controller::scroll_axes;
use crate::engine::core::canvas_scroll::state::AXES;
use crate::engine::core::canvas_scroll::{CanvasScrollState, ScrollPhase, ScrollRequest};
use crate::engine::ecs::{Entity, World};

/// C# `SEED.CanvasScroll` の ComponentKindName（Rust 側の解決キー。完全一致させること）。
pub const KIND_CANVAS_SCROLL: &str = "CanvasScroll";

/// 2 要素の値の要素数。
const PAIR_LEN: usize = 2;
/// scroll_to の要素数（x, y, 秒）。
const SCROLL_TO_LEN: usize = 3;

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

/// f64 の 2 要素を f32 で。
fn put_pair(out: &mut [f32], v: [f64; AXES]) -> Option<usize> {
    put(out, &[v[0] as f32, v[1] as f32])
}

/// 欄を読む（コンポーネントが無い・欄が無ければ None）。状態が無い（Edit・最初のフレームの前）ときの状態の欄は 0。
pub fn read(world: &World, entity: Entity, field: &str, out: &mut [f32]) -> Option<usize> {
    let c = world.get::<CanvasScrollComponent>(entity)?;
    let state = world.get::<CanvasScrollState>(entity).cloned().unwrap_or_default();
    let metrics = state.metrics;
    match field {
        // ── 設定 ──
        "enabled" => put_bool(out, c.enabled),
        "direction" => put_enum(out, c.direction),
        "edge" => put_enum(out, c.edge),
        "inertia" => put_bool(out, c.inertia),
        "snap" => put_enum(out, c.snap),
        "snap_interval" => put(out, &[c.snap_interval]),
        "hand_off_to_parent" => put_bool(out, c.hand_off_to_parent),
        "content_size" => put_enum(out, c.content_size),
        "fixed_content_size" => put(out, &[c.content_width, c.content_height]),
        "cull_outside" => put_bool(out, c.cull_outside),
        "cache_extent" => put(out, &[c.cache_extent]),
        "fling_friction" => put(out, &[c.fling_friction]),
        "bounce_drag" => put(out, &[c.bounce_drag]),
        // ── 状態 ──
        "position" => put_pair(out, state.position),
        "velocity" => put_pair(out, state.velocity),
        "phase" => put(out, &[state.phase as i32 as f32]),
        "is_scrolling" => put_bool(out, state.phase.is_scrolling()),
        "is_dragging" => put_bool(out, state.phase == ScrollPhase::Dragging),
        "has_metrics" => put_bool(out, metrics.is_some()),
        "viewport_size" => put_pair(out, metrics.map_or([0.0; AXES], |m| m.viewport)),
        "content_extent" => put_pair(out, metrics.map_or([0.0; AXES], |m| m.content)),
        "max_position" => put_pair(out, [0, 1].map(|a| metrics.map_or(0.0, |m| m.max_position(a)))),
        "end_inset" => put_pair(out, state.end_inset),
        _ => None,
    }
}

/// 要素数が一致するときだけ固定長の配列にする。
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

/// 欄へ書く（成功なら true。値の型・要素数・範囲が合わなければ false）。
pub fn write(world: &mut World, entity: Entity, field: &str, v: &[f32]) -> bool {
    match field {
        "position" => return jump(world, entity, v),
        "scroll_to" => return scroll_to(world, entity, v),
        "end_inset" => return set_end_inset(world, entity, v),
        _ => {}
    }
    let Some(c) = world.get_mut::<CanvasScrollComponent>(entity) else { return false };
    match field {
        "enabled" => assign(&mut c.enabled, take_bool(v)),
        "direction" => assign(&mut c.direction, take_enum::<ScrollDirection>(v)),
        "edge" => assign(&mut c.edge, take_enum::<ScrollEdge>(v)),
        "inertia" => assign(&mut c.inertia, take_bool(v)),
        "snap" => assign(&mut c.snap, take_enum::<ScrollSnap>(v)),
        "snap_interval" => assign(&mut c.snap_interval, take_f32(v).filter(|x| *x >= 0.0)),
        "hand_off_to_parent" => assign(&mut c.hand_off_to_parent, take_bool(v)),
        "content_size" => assign(&mut c.content_size, take_enum::<ScrollContentSize>(v)),
        "fixed_content_size" => match take::<PAIR_LEN>(v).filter(|a| a.iter().all(|x| x.is_finite() && *x >= 0.0)) {
            Some([w, h]) => {
                c.content_width = w;
                c.content_height = h;
                true
            }
            None => false,
        },
        "cull_outside" => assign(&mut c.cull_outside, take_bool(v)),
        "cache_extent" => assign(&mut c.cache_extent, take_f32(v).filter(|x| *x >= 0.0)),
        "fling_friction" => assign(&mut c.fling_friction, take_f32(v).filter(|x| *x > 0.0)),
        "bounce_drag" => assign(&mut c.bounce_drag, take_f32(v).filter(|x| *x > 0.0 && *x < 1.0)),
        _ => false,
    }
}

/// 中身の末尾の余白を置く（2 要素。有限の 0 以上。次のフレームの描画が中身の大きさへ足す。W2-6b のキーボードを避ける）。
fn set_end_inset(world: &mut World, entity: Entity, v: &[f32]) -> bool {
    let Some([x, y]) = take::<PAIR_LEN>(v).filter(|a| a.iter().all(|value| value.is_finite() && *value >= 0.0)) else {
        return false;
    };
    let Some(state) = state_mut(world, entity) else { return false };
    state.end_inset = [f64::from(x), f64::from(y)];
    true
}

/// 状態を可変で引く（無ければ作る。設定が無ければ None）。
fn state_mut(world: &mut World, entity: Entity) -> Option<&mut CanvasScrollState> {
    world.get::<CanvasScrollComponent>(entity)?;
    if world.get::<CanvasScrollState>(entity).is_none() {
        world.insert(entity, CanvasScrollState::default());
    }
    world.get_mut::<CanvasScrollState>(entity)
}

/// すぐ移す（Jump）: 動きを止め、スクロールする軸だけ範囲へ収めて位置を書く。同じフレームの描画から新しい位置で置かれる。
fn jump(world: &mut World, entity: Entity, v: &[f32]) -> bool {
    let Some(target) = take::<PAIR_LEN>(v).filter(|a| a.iter().all(|x| x.is_finite())) else { return false };
    let Some(settings) = world.get::<CanvasScrollComponent>(entity).cloned() else { return false };
    let Some(state) = state_mut(world, entity) else { return false };
    state.stop_motion();
    state.pending = None;
    state.drag_pointer = None;
    for axis in scroll_axes(&settings) {
        let value = f64::from(target[axis]);
        state.position[axis] = match state.max_position(axis) {
            Some(max) => value.clamp(0.0, max),
            None => value.max(0.0),
        };
    }
    state.phase = ScrollPhase::Idle;
    true
}

/// 時間をかけて移す（ScrollTo）: 次のスクロールのシステムの処理で動かし始める。0 秒以下はすぐ移す。
fn scroll_to(world: &mut World, entity: Entity, v: &[f32]) -> bool {
    let Some([x, y, duration]) = take::<SCROLL_TO_LEN>(v).filter(|a| a.iter().all(|x| x.is_finite())) else {
        return false;
    };
    if duration <= 0.0 {
        return jump(world, entity, &[x, y]);
    }
    let Some(state) = state_mut(world, entity) else { return false };
    state.pending = Some(ScrollRequest::Animate { target: [f64::from(x), f64::from(y)], duration: f64::from(duration) });
    true
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::canvas_scroll::ScrollMetrics;

    /// 大きさの分かった状態を作る（窓 100・中身 400 の縦）。
    fn world_with_scroll() -> (World, Entity) {
        let mut world = World::new();
        let e = world.spawn();
        world.insert(e, CanvasScrollComponent::default());
        world.insert(
            e,
            CanvasScrollState {
                metrics: Some(ScrollMetrics {
                    viewport: [100.0, 100.0],
                    content: [100.0, 400.0],
                    px_per_unit: [1.0, 1.0],
                    dp_scale: 1.0,
                    axis_dirs: [[1.0, 0.0], [0.0, 1.0]],
                }),
                ..CanvasScrollState::default()
            },
        );
        (world, e)
    }

    /// 設定の読み書き（列挙の添字・範囲外の拒否・Fixed の大きさ）。
    #[test]
    fn settings_round_trip_and_reject_bad_values() {
        let (mut world, e) = world_with_scroll();
        let mut out = [0.0f32; 4];
        assert!(write(&mut world, e, "direction", &[2.0]));
        assert_eq!(read(&world, e, "direction", &mut out), Some(1));
        assert_eq!(out[0], 2.0);
        assert!(!write(&mut world, e, "edge", &[5.0]), "範囲外の列挙");
        assert!(!write(&mut world, e, "bounce_drag", &[1.5]), "0 と 1 の外");
        assert!(write(&mut world, e, "fixed_content_size", &[10.0, 2000.0]));
        assert_eq!(read(&world, e, "fixed_content_size", &mut out), Some(2));
        assert_eq!(&out[..2], &[10.0, 2000.0]);
        assert!(!write(&mut world, e, "fixed_content_size", &[-1.0, 0.0]), "負は書けない");
    }

    /// 位置の書き込みは範囲へ収めてすぐ移し、ScrollTo は要求として積む。状態の読み取り。
    #[test]
    fn position_jump_clamps_and_scroll_to_queues() {
        let (mut world, e) = world_with_scroll();
        let mut out = [0.0f32; 4];
        assert!(write(&mut world, e, "position", &[50.0, 999.0]));
        assert_eq!(read(&world, e, "position", &mut out), Some(2));
        assert_eq!(&out[..2], &[0.0, 300.0], "縦だけ・最大 300 へ収める");
        assert!(write(&mut world, e, "scroll_to", &[0.0, 120.0, 0.25]));
        assert!(matches!(
            world.get::<CanvasScrollState>(e).unwrap().pending,
            Some(ScrollRequest::Animate { duration, .. }) if (duration - 0.25).abs() < 1e-9
        ));
        assert!(write(&mut world, e, "scroll_to", &[0.0, 10.0, 0.0]), "0 秒はすぐ移す");
        assert_eq!(world.get::<CanvasScrollState>(e).unwrap().position[1], 10.0);
        assert_eq!(read(&world, e, "max_position", &mut out), Some(2));
        assert_eq!(out[1], 300.0);
        assert_eq!(read(&world, e, "is_scrolling", &mut out), Some(1));
        assert_eq!(out[0], 0.0);
        assert!(!write(&mut world, e, "position", &[f32::NAN, 0.0]));
    }

    /// 中身の末尾の余白（W2-6b のキーボードを避ける）: 0 以上の 2 要素だけ書け、読み返せる。
    #[test]
    fn end_inset_round_trip() {
        let (mut world, e) = world_with_scroll();
        let mut out = [0.0f32; 4];
        assert_eq!(read(&world, e, "end_inset", &mut out), Some(2));
        assert_eq!(&out[..2], &[0.0, 0.0], "既定は 0");
        assert!(write(&mut world, e, "end_inset", &[0.0, 180.0]));
        assert_eq!(read(&world, e, "end_inset", &mut out), Some(2));
        assert_eq!(&out[..2], &[0.0, 180.0]);
        assert!(!write(&mut world, e, "end_inset", &[0.0, -1.0]), "負は書けない");
        assert!(!write(&mut world, e, "end_inset", &[5.0]), "要素数が違う");
    }
}
