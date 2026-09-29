// ============================================================
//  gesture/recognizers/ — ジェスチャーの認識器の規則（W2-2）
//
//  認識器は「ある指について、あるノードが受けたいジェスチャー」を 1 つ表す。アリーナ（arena.rs）が
//  指ごとに認識器を並べて競わせ、最初に「勝ちを申し出た」ものが勝つ（他は負け）。ここには認識器ごとの
//  **判定の規則だけ**を純関数で置く（状態の遷移とイベントの発行はアリーナの責務）。
//
//  【構成】（1 ファイル 1 認識器）
//    tap.rs        … タップ（押して・動かず・離す）
//    long_press.rs … 長押し（一定時間・動いたら不成立）
//    drag.rs       … ドラッグ（全方向・横だけ・縦だけ。slop を超えたら勝ちを申し出る）と軸への射影
//    fling.rs      … フリック（離した時点の速度が閾値以上。上限で切り詰める。離す前にほぼ止まっていれば 0 = R2）
//  押下の見た目（PressDown / PressCancel / PressUp）は認識器ではなく、ノードごとにアリーナが決める。
// ============================================================

pub mod drag;
pub mod fling;
pub mod long_press;
pub mod tap;

/// 認識器の種類（アリーナの参加者 1 つ）。
///
/// 同じノードの中の並び（タップ → 長押し → ドラッグ）がアリーナでの順になる（同じ時に勝ちを申し出たら先が勝つ）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RecognizerKind {
    /// タップ。
    Tap,
    /// 長押し。
    LongPress,
    /// ドラッグ（フリックだけを受けるノードもこれで指を取る）。
    Drag,
}

impl RecognizerKind {
    /// 押下の見た目の対象になる認識器（タップ・長押し）か。
    pub fn is_press(self) -> bool {
        matches!(self, RecognizerKind::Tap | RecognizerKind::LongPress)
    }
}

/// 認識器の状態。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MemberState {
    /// まだ競っている。
    Possible,
    /// 勝った（指はこの認識器のもの）。
    Won,
    /// 負けた・成り立たなかった（以後この指について何もしない）。
    Lost,
}
