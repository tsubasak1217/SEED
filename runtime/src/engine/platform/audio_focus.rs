// ============================================================
//  platform/audio_focus.rs — OS の音声フォーカスの報告（Android の AudioManager）
//
//  【音声フォーカスとは】
//  Android では、音を出すアプリ同士が「今は誰が鳴らしてよいか」を OS の音声フォーカスで譲り合う。
//  ゲームは前面に来たら要求し（AUDIOFOCUS_GAIN）、着信の呼び出し音・他のアプリの音楽再生などで
//  失ったら音を止め、取り戻したら再開する。前面を離れるときは自分から手放す。
//
//  【役割】
//  Java（AudioFocusController。Android の UI スレッド）から JNI で届く最新の状態を、プロセスで 1 か所に保持する。
//    - 書く側: Android の糊（runtime/android/native の jni_exports.rs）
//    - 読む側: App がイベントループの 1 周ごと・背面への出入りのたびに（app/audio_output_sync.rs）。
//              出力を止めるか・全体の音量をどうするかは core/audio/output_policy.rs が決める
//  デスクトップは報告が来ないので、ずっと「持っている（Gained）」のまま（振る舞いは不変）。
//
//  【番号の約束】
//  JNI では状態を整数（jint）で渡す。番号は Java の AudioFocusController.STATE_* と一致させる
//  （AudioFocus::code / from_code がこちら側の唯一の対応表）。
//
//  全体像は docs/android.md「音声」。
// ============================================================

use std::sync::atomic::{AtomicI32, Ordering};

/// 番号: 持っている（Java の AudioFocusController.STATE_GAINED）。
const CODE_GAINED: i32 = 0;
/// 番号: 一時的に失った（STATE_LOST_TRANSIENT）。
const CODE_LOST_TRANSIENT: i32 = 1;
/// 番号: 音量を下げれば鳴らしてよい（STATE_DUCKED）。
const CODE_DUCKED: i32 = 2;
/// 番号: 恒久的に失った（STATE_LOST）。
const CODE_LOST: i32 = 3;
/// 番号: 自分から手放した（STATE_RELEASED）。
const CODE_RELEASED: i32 = 4;

/// OS の音声フォーカスの状態（Android の OnAudioFocusChangeListener の値と、要求・放棄の結果を丸めたもの）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AudioFocus {
    /// 持っている。普通に鳴らしてよい。デスクトップは常にこれ（OS に音声フォーカスの考え方が無い）。
    Gained,
    /// 一時的に失った（着信の呼び出し音・ナビの読み上げ・通話中で要求が保留になった等）。
    /// 止めて待つ。取り戻したら（GAIN が届いたら）再開する。
    LostTransient,
    /// 一時的に失ったが、音量を下げれば鳴らしてよい（通知音など。いわゆるダッキング）。
    Ducked,
    /// 恒久的に失った（他のアプリが音楽の再生を始めた・要求が拒否された）。止めたまま。
    /// OS はこちらの要求を捨てるので GAIN は届かない。次に前面へ戻ったときに要求し直す。
    Lost,
    /// 自分から手放した（前面を離れた＝Activity の onPause）。止める。
    Released,
}

impl AudioFocus {
    /// JNI で渡す番号（Java の AudioFocusController.STATE_* と同じ）。
    pub const fn code(self) -> i32 {
        match self {
            Self::Gained => CODE_GAINED,
            Self::LostTransient => CODE_LOST_TRANSIENT,
            Self::Ducked => CODE_DUCKED,
            Self::Lost => CODE_LOST,
            Self::Released => CODE_RELEASED,
        }
    }

    /// 番号から状態を得る。知らない番号（Java とネイティブの版の食い違い）は None。
    pub const fn from_code(code: i32) -> Option<Self> {
        match code {
            CODE_GAINED => Some(Self::Gained),
            CODE_LOST_TRANSIENT => Some(Self::LostTransient),
            CODE_DUCKED => Some(Self::Ducked),
            CODE_LOST => Some(Self::Lost),
            CODE_RELEASED => Some(Self::Released),
            _ => None,
        }
    }

    /// ログ用の短い説明。
    pub const fn describe(self) -> &'static str {
        match self {
            Self::Gained => "持っている",
            Self::LostTransient => "一時的に喪失",
            Self::Ducked => "音量を下げて継続可（ダッキング）",
            Self::Lost => "恒久的に喪失",
            Self::Released => "手放した（前面を離れた）",
        }
    }
}

/// 最新の報告を 1 つ持つ置き場。
///
/// プロセスで 1 つ（`REPORTED`）を使う。単体テストのために型として切り出してある
/// （background_gate::BackgroundGate と同じ形）。
pub struct AudioFocusCell {
    /// 最後に届いた状態の番号（AudioFocus::code）。
    code: AtomicI32,
}

impl AudioFocusCell {
    /// 「持っている」で作る（報告が来ないデスクトップの値）。
    pub const fn new() -> Self {
        Self { code: AtomicI32::new(CODE_GAINED) }
    }

    /// 報告を受け取る。
    ///
    /// # 戻り値
    /// 前の報告から変わったなら true（ログを出すかの判断に使う）。
    pub fn report(&self, focus: AudioFocus) -> bool {
        let previous = self.code.swap(focus.code(), Ordering::AcqRel);
        previous != focus.code()
    }

    /// 最新の状態。
    pub fn current(&self) -> AudioFocus {
        // 置き場には code() で作った番号しか入らないので None にはならない（念のため「持っている」へ倒す）。
        AudioFocus::from_code(self.code.load(Ordering::Acquire)).unwrap_or(AudioFocus::Gained)
    }
}

impl Default for AudioFocusCell {
    fn default() -> Self {
        Self::new()
    }
}

/// プロセス全体の音声フォーカスの報告。
static REPORTED: AudioFocusCell = AudioFocusCell::new();

/// OS からの報告を受け取る（Android の糊が JNI のスレッドから呼ぶ）。戻り値は `AudioFocusCell::report`。
pub fn report(focus: AudioFocus) -> bool {
    REPORTED.report(focus)
}

/// 最新の音声フォーカスの状態（報告が無ければ「持っている」）。
pub fn current() -> AudioFocus {
    REPORTED.current()
}

// ============================================================
//  ユニットテスト（プロセス共有の REPORTED は触らず、ローカルの AudioFocusCell で確かめる）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 全状態（番号の対応表の検査に使う）。
    const ALL: [AudioFocus; 5] = [
        AudioFocus::Gained,
        AudioFocus::LostTransient,
        AudioFocus::Ducked,
        AudioFocus::Lost,
        AudioFocus::Released,
    ];

    /// 番号 → 状態 → 番号が往復し、番号がすべて異なること（Java 側との約束の土台）。
    #[test]
    fn codes_round_trip_and_are_unique() {
        for focus in ALL {
            assert_eq!(AudioFocus::from_code(focus.code()), Some(focus));
        }
        let mut codes: Vec<i32> = ALL.iter().map(|focus| focus.code()).collect();
        codes.sort_unstable();
        codes.dedup();
        assert_eq!(codes.len(), ALL.len(), "番号が重複している");
    }

    /// 知らない番号は None（版の食い違いを握りつぶさず、呼び出し側が警告できるように）。
    #[test]
    fn unknown_code_is_rejected() {
        assert_eq!(AudioFocus::from_code(-1), None);
        assert_eq!(AudioFocus::from_code(CODE_RELEASED + 1), None);
    }

    /// 報告が無い間は「持っている」（デスクトップの振る舞いを変えない）。
    #[test]
    fn starts_as_gained() {
        assert_eq!(AudioFocusCell::new().current(), AudioFocus::Gained);
    }

    /// 最新の報告が読め、同じ報告の繰り返しは「変化なし」になること。
    #[test]
    fn report_keeps_latest_and_detects_change() {
        let cell = AudioFocusCell::new();
        assert!(cell.report(AudioFocus::LostTransient));
        assert_eq!(cell.current(), AudioFocus::LostTransient);
        assert!(!cell.report(AudioFocus::LostTransient), "同じ報告を変化として扱った");
        assert!(cell.report(AudioFocus::Gained));
        assert_eq!(cell.current(), AudioFocus::Gained);
    }
}
