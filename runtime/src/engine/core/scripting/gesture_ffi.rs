// ============================================================
//  gesture_ffi.rs — ジェスチャーのイベントを C# スクリプトへ渡す FFI の型（W2-2）
//
//  ポインタのイベント（OnPointer*）は物理イベントの FFI（RawPhysicsEvent）に相乗りしているが、ジェスチャーは
//  位置・移動量・速度などの値を持つので、専用の構造体と専用の入口（C# の ScriptBridge.OnGestureEvent）を使う。
//  入口は省略可能として取り出す（clr_host/entry_points.rs の bridge_fn_optional）: 古い SEEDScripting.dll でも
//  CLR の起動は失敗せず、ジェスチャーのイベントが届かないだけになる。
//
//  【C# との契約】`RawGestureEvent` のフィールドの並び・型は scripting/src/NativeGestureEvent.cs と完全に一致させる
//  （すべて 4 バイトの値。詰め物は無い）。種類の数値は gesture/events.rs の GestureEventKind = C# の SEED.GestureKind。
// ============================================================

use crate::engine::core::input::gesture::GestureEmit;
use crate::engine::ecs::Entity;

/// C# 側 NativeGestureEvent と同じメモリレイアウト（#[repr(C)]・すべて 4 バイト）。
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) struct RawGestureEvent {
    /// 種類（GestureEventKind の数値）。
    pub kind: i32,
    /// 指の番号（0 起点）。
    pub pointer_id: i32,
    /// 届け先のアクターのエンティティ（gameObject の束縛用）。
    pub self_index: u32,
    pub self_generation: u32,
    /// 今の位置（キャンバスの画素。画面の中央が原点・Y 下向き。Input.MousePositionCanvas と同じ）。
    pub position_x: f32,
    pub position_y: f32,
    /// 今の位置（画面の画素。左上が原点。Input.MousePos・Touch.Position と同じ）。
    pub screen_x: f32,
    pub screen_y: f32,
    /// 今の位置（ノードのローカル。見た目の矩形の左上が原点・ノードの単位＝dp のキャンバスなら dp）。
    pub local_x: f32,
    pub local_y: f32,
    /// 押した位置（キャンバスの画素）。
    pub start_x: f32,
    pub start_y: f32,
    /// 移動量（画素）。
    pub delta_x: f32,
    pub delta_y: f32,
    /// 速度（画素/秒）。
    pub velocity_x: f32,
    pub velocity_y: f32,
    /// 1 dp の画素数（C# が dp の値を作る）。
    pub dp_scale: f32,
    /// 押してからの時間（秒）。
    pub duration: f32,
    /// 取り消しで終わったか（0 / 1）。
    pub canceled: i32,
}

/// ジェスチャーのイベントをスクリプトへ通知する入口の型（引数は (ハンドル, イベント)）。
pub(crate) type GestureEventFn = unsafe extern "system" fn(isize, *const RawGestureEvent);

impl RawGestureEvent {
    /// アリーナのイベントから作る【純関数】。
    ///
    /// # 引数
    /// * `emit`        - アリーナのイベント（位置はキャンバスの画素）
    /// * `self_owner`  - 届け先のアクター（emit.node と同じ）
    /// * `window`      - 画面の大きさ（画素。キャンバスの画素 → 画面の画素の換算）
    /// * `local`       - ノードのローカルの位置（ノードの単位。ノードが消えていれば 0）
    /// * `dp_scale`    - 1 dp の画素数
    pub(crate) fn from_emit(
        emit: &GestureEmit,
        self_owner: Entity,
        window: [f32; 2],
        local: [f32; 2],
        dp_scale: f32,
    ) -> Self {
        Self {
            kind: emit.kind.id(),
            pointer_id: emit.pointer_id as i32,
            self_index: self_owner.index(),
            self_generation: self_owner.generation(),
            position_x: emit.position[0],
            position_y: emit.position[1],
            screen_x: emit.position[0] + window[0] * 0.5,
            screen_y: emit.position[1] + window[1] * 0.5,
            local_x: local[0],
            local_y: local[1],
            start_x: emit.start_position[0],
            start_y: emit.start_position[1],
            delta_x: emit.delta[0],
            delta_y: emit.delta[1],
            velocity_x: emit.velocity[0],
            velocity_y: emit.velocity[1],
            dp_scale,
            duration: emit.duration as f32,
            canceled: i32::from(emit.canceled),
        }
    }
}

// ============================================================
//  単体テスト（C# との契約）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::input::gesture::GestureEventKind;

    /// 大きさは 19 × 4 バイト（C# の NativeGestureEvent と同じ。フィールドを足したら両方を直す）。
    #[test]
    fn layout_matches_csharp_contract() {
        assert_eq!(std::mem::size_of::<RawGestureEvent>(), 19 * 4);
        assert_eq!(std::mem::align_of::<RawGestureEvent>(), 4);
    }

    /// キャンバスの画素 → 画面の画素（中央原点 → 左上原点）と、種類・取り消しの数値。
    #[test]
    fn from_emit_converts_positions() {
        let emit = GestureEmit {
            node: Entity::from_raw(3, 1),
            kind: GestureEventKind::DragEnd,
            pointer_id: 2,
            position: [-10.0, 20.0],
            start_position: [0.0, 0.0],
            delta: [1.0, 2.0],
            velocity: [300.0, 0.0],
            time: 1.0,
            duration: 0.25,
            canceled: true,
        };
        let raw = RawGestureEvent::from_emit(&emit, emit.node, [800.0, 600.0], [5.0, 6.0], 2.0);
        assert_eq!((raw.kind, raw.pointer_id, raw.self_index, raw.self_generation), (4, 2, 3, 1));
        assert_eq!((raw.screen_x, raw.screen_y), (390.0, 320.0));
        assert_eq!((raw.local_x, raw.local_y, raw.dp_scale, raw.duration, raw.canceled), (5.0, 6.0, 2.0, 0.25, 1));
    }
}
