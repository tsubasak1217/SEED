// ============================================================
//  gesture/time_floor.rs — 指のイベントの記録の時刻を逆行させない下限（2026-09-29）
//
//  【何のためか】
//  Android の指のイベントは MotionEvent の時刻（eventTime と履歴。input/touch/os_timing/）で記録するが、控えが見つからない
//  イベントは受け取った時刻に戻る。2 つが混ざると、同じ指の後のイベントが前のイベントより前の時刻になりうる（受け取った時刻は
//  MotionEvent の時刻より数 ms 遅い）。記録は取り出すときに時刻の順へ並べ替える（pointer_log.rs の take）ので、逆行すると
//  離す前に触れ直した・動いた、のような並びになってアリーナの判定が崩れる。そこで記録する時刻を次の下限まで切り上げる。
//    1. 指ごと: その指の前の記録の時刻（離した後に同じ ID で触れ直すときも前の離しより前にしない）
//    2. 全部の取り消し（フォーカスを失った・背面へ回った）の時刻: 取り消しより後に届いたイベントを、取り消しより前へ並べない
//       （届いた順では取り消しの後に触れた指が、取り消しで消されないように）
//  PC のマウスの合成の指は受け取った時刻（今）なので下限に掛からない（従来どおり）。注入の指はここを通らない。
// ============================================================

use super::pointer_log::PointerKey;

/// 覚えておく指の数の上限（超えたら時刻の最も古い指を忘れる）。
/// Android の pointer id は 0〜31（MotionEvent の MAX_POINTER_ID = 31）なので、32 本とマウスの合成の指が入る余裕を見た値。
pub const MAX_FLOOR_POINTERS: usize = 64;

/// 指のイベントの記録の時刻の下限（Input が 1 つ持つ。フレームをまたいで持ち続ける）【状態を持つ純ロジック】。
#[derive(Clone, Debug)]
pub struct PointerTimeFloor {
    /// 指ごとの前の記録の時刻（秒。pointer_log の時計）。
    per_pointer: Vec<(PointerKey, f64)>,
    /// 最後の全部の取り消しの時刻（秒。まだ無ければ負の無限大）。
    cancel_all: f64,
}

impl Default for PointerTimeFloor {
    fn default() -> Self {
        Self::new()
    }
}

impl PointerTimeFloor {
    /// 下限の無い状態で作る。
    pub fn new() -> Self {
        Self { per_pointer: Vec::new(), cancel_all: f64::NEG_INFINITY }
    }

    /// 指のイベントを記録する時刻を決める: `time` を、その指の前の記録の時刻と全部の取り消しの時刻まで切り上げて覚える。
    ///
    /// # 引数
    /// * `pointer` - 指の鍵
    /// * `time`    - 候補の時刻（MotionEvent の時刻か受け取った時刻。秒）
    ///
    /// # 戻り値
    /// 記録に使う時刻（`time` 以上）。
    pub fn clamp(&mut self, pointer: PointerKey, time: f64) -> f64 {
        let mut out = time.max(self.cancel_all);
        match self.per_pointer.iter_mut().find(|(key, _)| *key == pointer) {
            Some((_, last)) => {
                out = out.max(*last);
                *last = out;
            }
            None => {
                if self.per_pointer.len() >= MAX_FLOOR_POINTERS {
                    // 時刻の最も古い指を忘れる（もう触れていない指のはず）
                    if let Some(oldest) = self
                        .per_pointer
                        .iter()
                        .enumerate()
                        .min_by(|a, b| a.1 .1.total_cmp(&b.1 .1))
                        .map(|(i, _)| i)
                    {
                        self.per_pointer.swap_remove(oldest);
                    }
                }
                self.per_pointer.push((pointer, out));
            }
        }
        out
    }

    /// 全部の取り消しを記録した時刻を覚える（以後の指のイベントはこれより前にならない）。
    pub fn note_cancel_all(&mut self, time: f64) {
        self.cancel_all = self.cancel_all.max(time);
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 指ごとに逆行を切り上げる（別の指には影響しない）。進む時刻はそのまま。
    #[test]
    fn clamps_per_pointer() {
        let mut f = PointerTimeFloor::new();
        assert_eq!(f.clamp(1, 1.0), 1.0);
        assert_eq!(f.clamp(1, 0.9), 1.0, "前の記録より前は切り上げ");
        assert_eq!(f.clamp(2, 0.5), 0.5, "別の指は別の下限");
        assert_eq!(f.clamp(1, 1.2), 1.2);
        assert_eq!(f.clamp(1, 1.1), 1.2, "切り上げた後も下限は最後の記録");
    }

    /// 全部の取り消しより前の時刻は取り消しの時刻まで切り上げる。
    #[test]
    fn clamps_to_cancel_all() {
        let mut f = PointerTimeFloor::new();
        f.clamp(3, 2.0);
        f.note_cancel_all(5.0);
        assert_eq!(f.clamp(4, 4.0), 5.0, "取り消しの後に届いた新しい指");
        assert_eq!(f.clamp(3, 6.0), 6.0);
        f.note_cancel_all(1.0);
        assert_eq!(f.clamp(5, 4.5), 5.0, "取り消しの下限は戻らない");
    }

    /// 上限を超えたら時刻の最も古い指を忘れる。
    #[test]
    fn forgets_the_oldest_pointer_over_the_cap() {
        let mut f = PointerTimeFloor::new();
        for k in 0..MAX_FLOOR_POINTERS as u64 {
            f.clamp(k, 10.0 + k as f64);
        }
        f.clamp(999, 100.0);
        assert_eq!(f.per_pointer.len(), MAX_FLOOR_POINTERS);
        assert_eq!(f.clamp(0, 1.0), 1.0, "指 0（最も古い）は忘れたので下限なし");
    }
}
