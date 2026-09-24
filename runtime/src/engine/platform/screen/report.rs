// ============================================================
//  platform/screen/report.rs — OS から届く画面の報告（実行時に更新される画面情報）
//
//  【役割】
//  安全領域（カメラ穴・ジェスチャーバー等を避けた内側）と表示の回転は、OS が実行中に
//  いつでも変える値で、しかもエンジンのスレッドとは別のスレッド（Android の UI スレッド）から届く。
//  それを「最新の報告」としてプロセス全体で 1 か所に保持する。
//    - 書く側: Android の糊（runtime/android/native の jni_exports.rs。MainActivity の UI スレッドから）
//    - 読む側: App がフレームごとに 1 回（app/screen_publish.rs）。スクリプトはその写し（snapshot.rs）しか見ない
//  デスクトップは報告が来ない（空のまま）。そのときは全画面が安全領域・向きは縦横比から決まる。
//
//  【報告と描画面の大きさの対応】
//  回転の直後は「新しい向きの報告」と「描画面（サーフェス）の大きさの変化（winit の Resized）」が
//  別々の経路で前後して届く。報告には「報告したときの描画面の大きさ」を添えてもらい、読む側は
//  今の描画面と大きさが一致する報告だけを使う（ReportHistory::select_for_frame）。
//  直前の報告も残しておくのは、報告が先に届いて描画面の大きさがまだ古い数フレームに、
//  古い向きの正しい報告を使い続けるため。ただし描画面が一度でも最新の報告に追い付いたら、
//  それより古い報告は捨てる（270 度 → 180 度 → 90 度と回したとき、大きさが同じ 270 度の報告を
//  90 度の描画面に当ててしまわないように。エミュレータで実際に 1 フレーム起きた）。
//
//  全体像は docs/android.md「画面の向きと安全領域」。
// ============================================================

use std::sync::Mutex;

/// 描画面の各辺から内側へ入った距離（物理ピクセル）。安全領域 = 描画面からこの分を削った矩形。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct EdgeInsets {
    /// 左端からの距離。
    pub left: u32,
    /// 上端からの距離。
    pub top: u32,
    /// 右端からの距離。
    pub right: u32,
    /// 下端からの距離。
    pub bottom: u32,
}

impl EdgeInsets {
    /// すべての辺が 0（描画面全体が安全領域）か。
    pub fn is_zero(&self) -> bool {
        *self == Self::default()
    }
}

/// OS からの画面の報告 1 件。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ScreenReport {
    /// 報告したときの描画面の幅（物理ピクセル。Android では SurfaceView の幅）。
    pub frame_width: u32,
    /// 報告したときの描画面の高さ（物理ピクセル）。
    pub frame_height: u32,
    /// 安全領域の、描画面の各辺からの距離（物理ピクセル）。
    pub insets: EdgeInsets,
    /// 表示の回転（自然な向きからの 90 度単位の回数。Android の Display.getRotation。0..=3）。
    pub rotation_quarter_turns: u32,
    /// 表示の自然な向き（回転 0）のときの幅（物理ピクセル。Display.Mode の physicalWidth）。
    /// 回転から向きを決めるときの「縦持ちが自然な端末か」の判定に使う。
    pub natural_width: u32,
    /// 表示の自然な向き（回転 0）のときの高さ（物理ピクセル。Display.Mode の physicalHeight）。
    pub natural_height: u32,
}

impl ScreenReport {
    /// この報告が、指定した大きさの描画面についてのものか。
    pub fn matches_frame(&self, width: u32, height: u32) -> bool {
        self.frame_width == width && self.frame_height == height
    }
}

/// 保持する報告の件数（最新＋直前）。
///
/// 回転の直後に「新しい報告が先・描画面の変化が後」で届いた数フレームは、直前（古い向き）の報告が
/// 今の描画面に一致する。それより古い報告は、描画面の大きさが同じでも向きが違い得る（180 度の回転）ので持たない。
pub const REPORT_HISTORY_LEN: usize = 2;

/// 報告の履歴（新しい順）【純ロジック・単体テスト対象】。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ReportHistory {
    /// 添字 0 が最新。空きは None。
    entries: [Option<ScreenReport>; REPORT_HISTORY_LEN],
}

impl ReportHistory {
    /// 空の履歴。
    pub const fn new() -> Self {
        Self { entries: [None; REPORT_HISTORY_LEN] }
    }

    /// 報告を最新として積む。最新と同じ内容なら何もしない（同じ報告が何度届いても履歴を押し出さない）。
    ///
    /// # 戻り値
    /// 積んだ＝true（内容が変わった）。同じ内容で積まなかった＝false。
    pub fn push(&mut self, report: ScreenReport) -> bool {
        if self.entries[0] == Some(report) {
            return false;
        }
        self.entries.rotate_right(1);
        self.entries[0] = Some(report);
        true
    }

    /// 最新の報告（大きさを問わない）。
    pub fn latest(&self) -> Option<ScreenReport> {
        self.entries[0]
    }

    /// 指定した大きさの描画面に使う報告を選ぶ（フレームごとに 1 回呼ぶ）。無ければ None。
    ///
    /// - 最新の報告が一致する → それを使い、より古い報告は捨てる（描画面が最新の報告に追い付いたので、
    ///   古い報告はもう「報告が先に届いた間のつなぎ」として要らない。残すと大きさが同じ別の向きの報告に当たる）
    /// - 最新は一致しないが直前が一致する → 直前を使う（報告が先に届き、描画面の大きさがまだ古い間）
    /// - どれも一致しない → None（描画面が先に変わり、報告がまだ届いていない間）
    pub fn select_for_frame(&mut self, width: u32, height: u32) -> Option<ScreenReport> {
        if let Some(newest) = self.entries[0] {
            if newest.matches_frame(width, height) {
                self.entries[1..].fill(None);
                return Some(newest);
            }
        }
        self.entries[1..]
            .iter()
            .flatten()
            .find(|report| report.matches_frame(width, height))
            .copied()
    }
}

/// プロセス全体の報告の履歴。書くのは OS のスレッド（Android の UI スレッド）、読むのはエンジンのスレッド。
static REPORTS: Mutex<ReportHistory> = Mutex::new(ReportHistory::new());

/// 報告の履歴へ触る（Mutex が毒されていても中身は壊れない単純な値なので、そのまま使う）。
fn with_reports<R>(f: impl FnOnce(&mut ReportHistory) -> R) -> R {
    let mut guard = REPORTS.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    f(&mut guard)
}

/// OS からの報告を受け取る（Android の糊が UI スレッドから呼ぶ）。
///
/// # 戻り値
/// 内容が変わった＝true（同じ内容の報告の繰り返しなら false。ログを間引くのに使う）。
pub fn submit(report: ScreenReport) -> bool {
    with_reports(|reports| reports.push(report))
}

/// 指定した大きさの描画面に使う報告を選ぶ（エンジンがフレームごとに 1 回呼ぶ。規則は ReportHistory::select_for_frame）。
pub fn select_for_frame(width: u32, height: u32) -> Option<ScreenReport> {
    with_reports(|reports| reports.select_for_frame(width, height))
}

/// 最新の報告（描画面の大きさを問わない。診断ログ用）。
pub fn latest() -> Option<ScreenReport> {
    with_reports(|reports| reports.latest())
}

// ============================================================
//  テスト（純ロジックの ReportHistory だけ。グローバルの REPORTS はテストから触らない:
//  並行に走る他のテストの画面情報まで変わってしまうため）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 縦（1080x2400・上に穴）の報告。
    fn portrait() -> ScreenReport {
        ScreenReport {
            frame_width: 1080,
            frame_height: 2400,
            insets: EdgeInsets { left: 0, top: 136, right: 0, bottom: 63 },
            rotation_quarter_turns: 0,
            natural_width: 1080,
            natural_height: 2400,
        }
    }

    /// 横（2400x1080・左に穴）の報告。
    fn landscape() -> ScreenReport {
        ScreenReport {
            frame_width: 2400,
            frame_height: 1080,
            insets: EdgeInsets { left: 136, top: 0, right: 0, bottom: 63 },
            rotation_quarter_turns: 1,
            natural_width: 1080,
            natural_height: 2400,
        }
    }

    /// 逆さの縦（回転 2・穴が下）の報告。
    fn upside_down() -> ScreenReport {
        ScreenReport {
            insets: EdgeInsets { left: 0, top: 0, right: 0, bottom: 136 },
            rotation_quarter_turns: 2,
            ..portrait()
        }
    }

    /// 反対向きの横（回転 3・穴が右）の報告。
    fn landscape_right() -> ScreenReport {
        ScreenReport {
            insets: EdgeInsets { left: 0, top: 0, right: 136, bottom: 63 },
            rotation_quarter_turns: 3,
            ..landscape()
        }
    }

    /// 報告が無ければ何も返さない（デスクトップ）。
    #[test]
    fn empty_history_has_no_report() {
        let mut history = ReportHistory::new();
        assert_eq!(history.latest(), None);
        assert_eq!(history.select_for_frame(1080, 2400), None);
    }

    /// 回転の直後、報告が先に届いて描画面がまだ古い大きさの間は、古い向きの報告を使い続ける。
    /// 描画面が新しい大きさになったら新しい報告に切り替わる。
    #[test]
    fn report_ahead_of_resize_keeps_previous_matching_report() {
        let mut history = ReportHistory::new();
        assert!(history.push(portrait()));
        assert!(history.push(landscape()));
        // まだ描画面は縦のまま → 直前の縦の報告（何フレーム続いても同じ）
        assert_eq!(history.select_for_frame(1080, 2400), Some(portrait()));
        assert_eq!(history.select_for_frame(1080, 2400), Some(portrait()));
        // 描画面が横になった → 最新の横の報告
        assert_eq!(history.select_for_frame(2400, 1080), Some(landscape()));
        assert_eq!(history.latest(), Some(landscape()));
    }

    /// 描画面が先に変わって報告がまだ来ていない間は、一致する報告が無い（呼び出し側は全画面扱いにする）。
    #[test]
    fn resize_ahead_of_report_has_no_matching_report() {
        let mut history = ReportHistory::new();
        history.push(portrait());
        assert_eq!(history.select_for_frame(2400, 1080), None);
    }

    /// エミュレータで起きた並び: 270 度 → 180 度 → 90 度。180 度の報告に描画面が追い付いた後、
    /// 90 度へ回って描画面だけ先に横になったフレームで、大きさが同じ 270 度の報告を当ててはいけない。
    #[test]
    fn stale_report_of_another_rotation_is_dropped_once_frame_caught_up() {
        let mut history = ReportHistory::new();
        history.push(landscape_right());
        assert_eq!(history.select_for_frame(2400, 1080), Some(landscape_right()));
        history.push(upside_down());
        // 描画面が逆さの縦に追い付いた（ここで 270 度の報告は捨てられる）
        assert_eq!(history.select_for_frame(1080, 2400), Some(upside_down()));
        // 90 度へ: 描画面だけ先に横になり、90 度の報告はまだ → 報告なし（270 度の報告を使わない）
        assert_eq!(history.select_for_frame(2400, 1080), None);
        history.push(landscape());
        assert_eq!(history.select_for_frame(2400, 1080), Some(landscape()));
    }

    /// 同じ内容の報告が繰り返し届いても履歴を押し出さない（直前の向きの報告が残る）。
    #[test]
    fn duplicate_report_does_not_evict_history() {
        let mut history = ReportHistory::new();
        history.push(portrait());
        history.push(landscape());
        assert!(!history.push(landscape()));
        assert_eq!(history.select_for_frame(1080, 2400), Some(portrait()));
    }

    /// 180 度の回転（大きさが同じで穴の辺が変わる）は新しい報告が勝つ。
    /// 保持するのは 2 件だけなので、3 件目で最古が押し出される。
    #[test]
    fn same_size_prefers_newest_and_history_is_bounded() {
        let mut history = ReportHistory::new();
        history.push(portrait());
        history.push(upside_down());
        assert_eq!(history.select_for_frame(1080, 2400), Some(upside_down()));
        history.push(landscape());
        // 描画面がまだ縦の間は、追い付いていた逆さの縦の報告が直前として残っている
        assert_eq!(history.select_for_frame(1080, 2400), Some(upside_down()));
    }

    /// 辺の距離が全部 0 か。
    #[test]
    fn zero_insets_are_detected() {
        assert!(EdgeInsets::default().is_zero());
        assert!(!portrait().insets.is_zero());
    }
}
