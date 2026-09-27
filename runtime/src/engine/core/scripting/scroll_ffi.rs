// ============================================================
//  scroll_ffi.rs — スクロールのイベントを C# スクリプトへ渡す FFI の型（W2-3）
//
//  ジェスチャー（gesture_ffi.rs）と同じく、専用の構造体と専用の入口（C# の ScriptBridge.OnScrollEvent）を使う。
//  入口は省略可能として取り出す（clr_host/entry_points.rs の bridge_fn_optional）: 古い SEEDScripting.dll でも
//  CLR の起動は失敗せず、スクロールのイベントが届かないだけになる（スクロール自体は動く）。
//
//  【C# との契約】`RawScrollEvent` のフィールドの並び・型は scripting/src/NativeScrollEvent.cs と完全に一致させる
//  （すべて 4 バイトの値。詰め物は無い）。種類の数値は canvas_scroll/events.rs の ScrollEventKind = C# の SEED.ScrollEventKind。
//  値はすべてスクロールの単位（キャンバスの単位。dp のキャンバスなら dp）。
// ============================================================

use crate::engine::core::canvas_scroll::ScrollEmit;

/// C# 側 NativeScrollEvent と同じメモリレイアウト（#[repr(C)]・すべて 4 バイト・16 個）。
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct RawScrollEvent {
    /// 種類（ScrollEventKind の数値）。
    pub kind: i32,
    /// 指でドラッグしているか（0 / 1）。
    pub dragging: i32,
    /// 届け先のアクターのエンティティ（gameObject の束縛用）。
    pub self_index: u32,
    pub self_generation: u32,
    /// 今の位置。
    pub position_x: f32,
    pub position_y: f32,
    /// 前に知らせた位置からの差（位置のイベントだけ）。
    pub delta_x: f32,
    pub delta_y: f32,
    /// 速度（単位/秒・位置の向き）。
    pub velocity_x: f32,
    pub velocity_y: f32,
    /// 位置の最大（中身 − 窓）。
    pub max_x: f32,
    pub max_y: f32,
    /// 窓の大きさ。
    pub viewport_w: f32,
    pub viewport_h: f32,
    /// 中身の大きさ。
    pub content_w: f32,
    pub content_h: f32,
}

/// スクロールのイベントをスクリプトへ通知する入口の型（引数は (ハンドル, イベント)）。
pub(crate) type ScrollEventFn = unsafe extern "system" fn(isize, *const RawScrollEvent);

impl RawScrollEvent {
    /// スクロールのシステムのイベントから作る【純関数】。
    pub(crate) fn from_emit(emit: &ScrollEmit) -> Self {
        Self {
            kind: emit.kind.id(),
            dragging: i32::from(emit.dragging),
            self_index: emit.node.index(),
            self_generation: emit.node.generation(),
            position_x: emit.position[0] as f32,
            position_y: emit.position[1] as f32,
            delta_x: emit.delta[0] as f32,
            delta_y: emit.delta[1] as f32,
            velocity_x: emit.velocity[0] as f32,
            velocity_y: emit.velocity[1] as f32,
            max_x: emit.max_position[0] as f32,
            max_y: emit.max_position[1] as f32,
            viewport_w: emit.viewport[0] as f32,
            viewport_h: emit.viewport[1] as f32,
            content_w: emit.content[0] as f32,
            content_h: emit.content[1] as f32,
        }
    }
}

// ============================================================
//  単体テスト（C# との契約）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::canvas_scroll::ScrollEventKind;
    use crate::engine::ecs::Entity;

    /// 大きさは 16 × 4 バイト（C# の NativeScrollEvent と同じ。フィールドを足したら両方を直す）。
    #[test]
    fn layout_matches_csharp_contract() {
        assert_eq!(std::mem::size_of::<RawScrollEvent>(), 16 * 4);
        assert_eq!(std::mem::align_of::<RawScrollEvent>(), 4);
    }

    /// 種類・エンティティ・値の写し。
    #[test]
    fn from_emit_copies_fields() {
        let emit = ScrollEmit {
            node: Entity::from_raw(7, 2),
            kind: ScrollEventKind::Update,
            position: [0.0, 120.5],
            delta: [0.0, 4.0],
            velocity: [0.0, -300.0],
            max_position: [0.0, 900.0],
            viewport: [360.0, 640.0],
            content: [360.0, 1540.0],
            dragging: true,
        };
        let raw = RawScrollEvent::from_emit(&emit);
        assert_eq!((raw.kind, raw.dragging, raw.self_index, raw.self_generation), (1, 1, 7, 2));
        assert_eq!((raw.position_y, raw.delta_y, raw.velocity_y, raw.max_y), (120.5, 4.0, -300.0, 900.0));
        assert_eq!((raw.viewport_w, raw.content_h), (360.0, 1540.0));
    }
}
