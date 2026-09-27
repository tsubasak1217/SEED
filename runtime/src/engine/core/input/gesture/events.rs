// ============================================================
//  gesture/events.rs — アリーナが出すジェスチャーのイベント（ノードへ届ける 1 件。W2-2）
//
//  アリーナ（arena_set.rs）は判定の結果を `GestureEmit` の列として返す。App（app/gesture_events.rs）が
//  それをスクリプトのコールバック（SEEDScript.OnGestureTap など）へ配る。
//
//  【種類の番号】`GestureEventKind` の数値は FFI で C# へ渡す（scripting の RawGestureEvent.kind）。
//  C# の `SEED.GestureKind`（scripting/src/Api/GestureEvent.cs）と必ず一致させる。足すときは末尾へ。
//
//  【まとめ】ドラッグの途中（DragUpdate）は 1 フレームに指 1 本・ノード 1 つにつき 1 件へまとめる
//  （移動量は足し合わせ、位置・速度・時刻は最新）。間に同じ指・同じノードの別の種類が挟まったらまとめない。
// ============================================================

use crate::engine::ecs::Entity;

/// ジェスチャーのイベントの種類（数値は C# の SEED.GestureKind と同じ）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(i32)]
pub enum GestureEventKind {
    /// タップが成り立った（離したとき）。
    Tap = 0,
    /// 長押しが成り立った（長押しの時間に達したとき。指はまだ触れている）。
    LongPress = 1,
    /// ドラッグが始まった（slop を超えてアリーナに勝ったとき）。
    DragStart = 2,
    /// ドラッグの途中（1 フレームに 1 回までにまとめる）。
    DragUpdate = 3,
    /// ドラッグが終わった（離した・取り消された）。
    DragEnd = 4,
    /// フリック（ドラッグの終わりの速度がフリックの最小の速度以上）。DragEnd の直後に届く。
    Fling = 5,
    /// 押下の見た目を出す（押したノードがタップ・長押しで勝ちそうになった）。
    PressDown = 6,
    /// 押下の見た目を戻す（タップ・長押しにならなかった: 外へ出た・スクロールに負けた・複数指・取り消し）。
    PressCancel = 7,
    /// 押下の見た目を戻す（タップ・長押しとして離した）。PressUp → Tap の順に届く。
    PressUp = 8,
}

impl GestureEventKind {
    /// FFI・ログで使う数値。
    #[inline]
    pub fn id(self) -> i32 {
        self as i32
    }

    /// 診断ログ用の名前。
    pub fn label(self) -> &'static str {
        match self {
            GestureEventKind::Tap => "Tap",
            GestureEventKind::LongPress => "LongPress",
            GestureEventKind::DragStart => "DragStart",
            GestureEventKind::DragUpdate => "DragUpdate",
            GestureEventKind::DragEnd => "DragEnd",
            GestureEventKind::Fling => "Fling",
            GestureEventKind::PressDown => "PressDown",
            GestureEventKind::PressCancel => "PressCancel",
            GestureEventKind::PressUp => "PressUp",
        }
    }
}

/// ノードへ届ける 1 件。位置・移動量・速度はキャンバスの画素（画面の中央が原点・Y 下向き）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct GestureEmit {
    /// 届け先のノード（アクターの entity）。
    pub node: Entity,
    /// 種類。
    pub kind: GestureEventKind,
    /// 指の番号（0 起点。ジェスチャーに参加している指の間で空いている最小の番号）。
    pub pointer_id: u32,
    /// 今の位置。
    pub position: [f32; 2],
    /// 押した位置。
    pub start_position: [f32; 2],
    /// 移動量（DragStart: 押した位置からの移動、DragUpdate: 前のドラッグのイベントからの移動。軸のドラッグは軸へ射影）。
    pub delta: [f32; 2],
    /// 速度（画素/秒。ドラッグ・フリック。軸のドラッグは軸へ射影、上限で切り詰め）。
    pub velocity: [f32; 2],
    /// イベントの時刻（秒。pointer_log の時計）。
    pub time: f64,
    /// 押してからの時間（秒）。
    pub duration: f64,
    /// 取り消しで終わったか（DragEnd が指の取り消しで来たとき true。速度は 0）。
    pub canceled: bool,
}

/// ドラッグの途中を 1 フレームぶんまとめる【純関数】（冒頭の「まとめ」の規則）。
pub fn coalesce_drag_updates(emits: Vec<GestureEmit>) -> Vec<GestureEmit> {
    let mut out: Vec<GestureEmit> = Vec::with_capacity(emits.len());
    for e in emits {
        if e.kind == GestureEventKind::DragUpdate {
            // 同じ指・同じノードの直近のイベントがドラッグの途中なら、そこへ足し込む
            let last_same = out.iter_mut().rev().find(|o| o.node == e.node && o.pointer_id == e.pointer_id);
            if let Some(prev) = last_same {
                if prev.kind == GestureEventKind::DragUpdate {
                    prev.delta = [prev.delta[0] + e.delta[0], prev.delta[1] + e.delta[1]];
                    prev.position = e.position;
                    prev.velocity = e.velocity;
                    prev.time = e.time;
                    prev.duration = e.duration;
                    continue;
                }
            }
        }
        out.push(e);
    }
    out
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 数値は C# の SEED.GestureKind と同じ（変えるなら両方を同時に直す）。
    #[test]
    fn ids_match_csharp_contract() {
        let kinds = [
            GestureEventKind::Tap,
            GestureEventKind::LongPress,
            GestureEventKind::DragStart,
            GestureEventKind::DragUpdate,
            GestureEventKind::DragEnd,
            GestureEventKind::Fling,
            GestureEventKind::PressDown,
            GestureEventKind::PressCancel,
            GestureEventKind::PressUp,
        ];
        for (i, k) in kinds.iter().enumerate() {
            assert_eq!(k.id(), i as i32, "{}", k.label());
        }
    }

    /// 1 件のイベントを作る（試験用）。
    fn ev(node: u32, pointer: u32, kind: GestureEventKind, dx: f32) -> GestureEmit {
        GestureEmit {
            node: Entity::from_raw(node, 0),
            kind,
            pointer_id: pointer,
            position: [dx, 0.0],
            start_position: [0.0, 0.0],
            delta: [dx, 0.0],
            velocity: [dx, 0.0],
            time: 0.0,
            duration: 0.0,
            canceled: false,
        }
    }

    /// 同じ指・同じノードの途中は足し合わせ、別の指は別々、間に別の種類が挟まればまとめない。
    #[test]
    fn coalesces_updates_per_pointer_and_node() {
        use GestureEventKind::*;
        let out = coalesce_drag_updates(vec![
            ev(1, 0, DragStart, 1.0),
            ev(1, 0, DragUpdate, 2.0),
            ev(2, 1, DragUpdate, 5.0),
            ev(1, 0, DragUpdate, 3.0),
            ev(2, 1, DragUpdate, 7.0),
            ev(1, 0, DragEnd, 0.0),
            ev(1, 0, DragUpdate, 4.0),
        ]);
        let summary: Vec<_> = out.iter().map(|e| (e.node.index(), e.kind, e.delta[0])).collect();
        assert_eq!(
            summary,
            vec![(1, DragStart, 1.0), (1, DragUpdate, 5.0), (2, DragUpdate, 12.0), (1, DragEnd, 0.0), (1, DragUpdate, 4.0)]
        );
        assert_eq!(out[1].position[0], 3.0, "位置は最新");
    }
}
