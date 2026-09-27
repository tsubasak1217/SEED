// ============================================================
//  canvas_scroll/nesting.rs — 入れ子のスクロールの受け渡し（同じ向きの内側 → 外側。W2-3）
//
//  【どれが指を取るか】向きの違う入れ子（縦の一覧の中の横の帯）は W2-2 のアリーナの軸の競いで決まる
//  （最初の動きの向き）。同じ向きの入れ子は、アリーナでは内側（葉に近い方）が勝つ。ここはその後の規則。
//
//  【ドラッグの受け渡し】（Android の NestedScrollingChild の dispatchNestedScroll と同じ順: 内側が先に使い、残りを外側へ）
//    鎖 = 指を取った内側のスクロール → 同じ軸をスクロールする祖先（近い順）。子の `hand_off_to_parent` が false なら
//    そこで鎖を切る。
//    1. 内側から順に、範囲の中で動ける分だけ使う（はみ出していれば戻る向きの分だけ摩擦つきで戻す）。残りを次へ
//    2. 鎖の全員が使い切れなかった残りは、内側（指を取ったもの）が Bounce なら跳ね返りとして範囲の外へ（摩擦つき）
//  【フリックの受け渡し】（dispatchNestedPreFling / dispatchNestedFling の考え方）
//    内側から順に「はみ出している（戻る必要がある）」か「速度の向きへまだ動ける」最初の 1 つがフリックを受ける。
//    鎖が切れたらそこまで。誰も動けなければ内側が受ける（Bounce なら端で跳ね返る）。受けなかったスクロールは
//    速度 0 で離す（スナップ・はみ出しの戻りだけ）。
//  ノードごとに単位（1 単位の画素数）が違ってよいので、受け渡す量は画素で持つ。
// ============================================================

use super::overscroll::{apply_user_delta, AxisRange};

/// 受け渡しとみなさない残り（画素）。
const HANDOFF_EPSILON_PX: f64 = 1e-6;

/// 鎖の 1 つ（軸 1 本ぶん。呼び出し側が状態から写し、処理の後に書き戻す）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ChainLink {
    /// 位置（このスクロールの単位）。
    pub position: f64,
    /// 範囲（単位）。
    pub range: AxisRange,
    /// 1 単位の画素数。
    pub px_per_unit: f64,
    /// Bounce（跳ね返り）か。
    pub bounce: bool,
    /// 端に達した残りを外側（鎖の次）へ渡すか。
    pub hand_off_to_parent: bool,
}

impl ChainLink {
    /// 画素の量をこのスクロールの単位へ。
    fn to_units(&self, px: f64) -> f64 {
        if self.px_per_unit > 0.0 { px / self.px_per_unit } else { px }
    }
}

/// ドラッグの移動を鎖へ当てる【純関数】。
///
/// # 引数
/// * `chain`    - 鎖（[0] が指を取った内側。以降は同じ軸の祖先を近い順に。`hand_off_to_parent` で切れる所まで）
/// * `delta_px` - 移動（画素・位置の向き）
///
/// # 戻り値
/// 使い切れなかった残り（画素）。内側が Bounce なら 0（跳ね返りとして使う）。
pub fn route_drag(chain: &mut [ChainLink], delta_px: f64) -> f64 {
    let mut remaining = delta_px;
    for link in chain.iter_mut() {
        let (position, used) = apply_user_delta(link.position, link.to_units(remaining), link.range, link.bounce, false);
        link.position = position;
        remaining -= used * link.px_per_unit;
        if remaining.abs() <= HANDOFF_EPSILON_PX || !link.hand_off_to_parent {
            break;
        }
    }
    if remaining.abs() > HANDOFF_EPSILON_PX {
        if let Some(inner) = chain.first_mut() {
            if inner.bounce {
                let (position, used) = apply_user_delta(inner.position, inner.to_units(remaining), inner.range, true, true);
                inner.position = position;
                remaining -= used * inner.px_per_unit;
            }
        }
    }
    remaining
}

/// フリックを受ける鎖の添字を選ぶ【純関数】。
///
/// # 引数
/// * `chain`       - 鎖（`route_drag` と同じ並び）
/// * `velocity_px` - 離した速度（画素/秒・位置の向き）
pub fn fling_receiver(chain: &[ChainLink], velocity_px: f64) -> usize {
    for (i, link) in chain.iter().enumerate() {
        if link.range.out_of_range(link.position) || link.range.can_move(link.position, velocity_px) {
            return i;
        }
        if !link.hand_off_to_parent {
            break;
        }
    }
    0
}

/// 鎖を `hand_off_to_parent` で切れる所までに縮める（[0] は常に残す）【純関数】。
pub fn effective_len(chain: &[ChainLink]) -> usize {
    let mut len = 0;
    for link in chain {
        len += 1;
        if !link.hand_off_to_parent {
            break;
        }
    }
    len
}
