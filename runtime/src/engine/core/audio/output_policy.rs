// ============================================================
//  core/audio/output_policy.rs — 音声の出力全体を止めるか・全体の音量をどうするかの判断（純関数）
//
//  【役割】
//  「アプリが背面にいるか」と「OS の音声フォーカス」から、出力（スピーカーへ出す音全体）の状態
//  OutputPolicy（一時停止か・全体音量の倍率）を決める。ここは判断だけで、実際の切り替えは
//  AudioManager::apply_output_policy（出力ストリームの一時停止・再開と全体音量）が行う。
//  条件を集めて当てるのは App（app/audio_output_sync.rs）。
//
//  | 条件                         | 出力                              | 例                                   |
//  |------------------------------|-----------------------------------|--------------------------------------|
//  | 背面                         | 一時停止                          | ホーム・アプリ切り替え・画面オフ     |
//  | フォーカス: 持っている       | そのまま                          | 通常（デスクトップは常にこれ）       |
//  | フォーカス: 下げてよい       | 全体音量 ×DUCKED_GAIN             | 通知音                               |
//  | フォーカス: 一時的に喪失     | 一時停止（取り戻したら再開）      | 着信の呼び出し音・通話中の要求保留   |
//  | フォーカス: 恒久的に喪失     | 一時停止（前面へ戻り直すまで）    | 他のアプリが音楽の再生を始めた       |
//  | フォーカス: 手放した         | 一時停止                          | 前面を離れた（onPause）              |
//  背面とフォーカスのどちらか一方でも止める理由があれば止める（両方が解けたときだけ再開）。
//
//  【ゲーム側の一時停止とは独立】
//  出力ストリームごと止めるので、スクリプトの PauseBgm などの Sink 単位の状態には一切触らない
//  （止めていた BGM が再開で勝手に鳴り出す・鳴っていた BGM が止まったまま、は起きない）。
// ============================================================

use crate::engine::platform::audio_focus::AudioFocus;

/// 通常の全体音量の倍率（1 = 各音の音量そのまま。出力のサンプルは変わらない）。
pub const FULL_GAIN: f32 = 1.0;

/// ダッキング中（他のアプリの通知音などが鳴っている間）の全体音量の倍率（約 -14 dB）。
pub const DUCKED_GAIN: f32 = 0.2;

/// 出力の状態を決める条件。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct OutputConditions {
    /// アプリが背面にいるか（core::background_gate。Android の suspended 〜 resumed の間）。
    pub background: bool,
    /// OS の音声フォーカス（platform::audio_focus。デスクトップは常に Gained）。
    pub focus: AudioFocus,
}

/// 出力全体の状態（AudioManager へ当てる値）。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct OutputPolicy {
    /// 出力ストリームを一時停止するか（止めている間は再生位置も進まない）。
    pub paused: bool,
    /// 全体音量の倍率（出力の全サンプルに掛ける）。
    pub gain: f32,
}

impl OutputPolicy {
    /// 通常（鳴らす・全体音量そのまま）。AudioManager を作った直後の状態。
    pub const NORMAL: Self = Self { paused: false, gain: FULL_GAIN };
}

/// 条件から出力の状態を決める。
pub fn decide(conditions: OutputConditions) -> OutputPolicy {
    // 音声フォーカスの状態ごとの扱い（止めるか・全体音量）。
    let (focus_pauses, gain) = match conditions.focus {
        AudioFocus::Gained => (false, FULL_GAIN),
        AudioFocus::Ducked => (false, DUCKED_GAIN),
        AudioFocus::LostTransient | AudioFocus::Lost | AudioFocus::Released => (true, FULL_GAIN),
    };
    OutputPolicy { paused: conditions.background || focus_pauses, gain }
}

/// 出力の状態が変わったときのログの本文（「何が起きたか（理由）」）。
///
/// # 引数
/// * `previous`   - 変わる前の状態
/// * `current`    - 変わった後の状態
/// * `conditions` - 変わった後の状態を決めた条件（理由の表示に使う）
pub fn describe_change(previous: OutputPolicy, current: OutputPolicy, conditions: OutputConditions) -> String {
    let reason = describe_conditions(conditions);
    match (previous.paused, current.paused) {
        (false, true) => format!("音声を一時停止しました（{reason}）"),
        (true, false) => format!("音声を再開しました（全体音量 ×{:.2}・{reason}）", current.gain),
        (true, true) => format!("音声は一時停止のままです（{reason}）"),
        (false, false) => format!("全体音量を ×{:.2} にしました（{reason}）", current.gain),
    }
}

/// 条件を人が読める形にする（例: 「背面=はい・音声フォーカス=持っている」）。
fn describe_conditions(conditions: OutputConditions) -> String {
    let background = if conditions.background { "はい" } else { "いいえ" };
    format!("背面={background}・音声フォーカス={}", conditions.focus.describe())
}

// ============================================================
//  ユニットテスト（状態遷移の表を固定する）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 前面で音声フォーカスを持っている（デスクトップの常の状態）。
    const FOREGROUND_GAINED: OutputConditions =
        OutputConditions { background: false, focus: AudioFocus::Gained };

    /// 全フォーカス状態（組み合わせの検査に使う）。
    const ALL_FOCUS: [AudioFocus; 5] = [
        AudioFocus::Gained,
        AudioFocus::LostTransient,
        AudioFocus::Ducked,
        AudioFocus::Lost,
        AudioFocus::Released,
    ];

    /// 前面でフォーカスを持っていれば通常のまま（デスクトップの振る舞いが変わらないことの根拠）。
    #[test]
    fn foreground_with_focus_is_normal() {
        assert_eq!(decide(FOREGROUND_GAINED), OutputPolicy::NORMAL);
    }

    /// 背面ではフォーカスの状態に関係なく止める。
    #[test]
    fn background_always_pauses() {
        for focus in ALL_FOCUS {
            let policy = decide(OutputConditions { background: true, focus });
            assert!(policy.paused, "背面なのに止めない: {focus:?}");
        }
    }

    /// 一時的な喪失・恒久的な喪失・手放したでは前面でも止め、全体音量は触らない。
    #[test]
    fn losing_focus_pauses_in_foreground() {
        for focus in [AudioFocus::LostTransient, AudioFocus::Lost, AudioFocus::Released] {
            let policy = decide(OutputConditions { background: false, focus });
            assert_eq!(policy, OutputPolicy { paused: true, gain: FULL_GAIN }, "{focus:?}");
        }
    }

    /// ダッキングは止めずに全体音量だけを下げる。
    #[test]
    fn ducking_lowers_gain_without_pausing() {
        let policy = decide(OutputConditions { background: false, focus: AudioFocus::Ducked });
        assert_eq!(policy, OutputPolicy { paused: false, gain: DUCKED_GAIN });
        assert!(DUCKED_GAIN > 0.0 && DUCKED_GAIN < FULL_GAIN);
    }

    /// 一時的な喪失 → 取り戻した、で元の状態へ戻る（着信の呼び出し音が止んだとき）。
    #[test]
    fn regaining_after_transient_loss_restores_normal() {
        let lost = decide(OutputConditions { background: false, focus: AudioFocus::LostTransient });
        assert!(lost.paused);
        assert_eq!(decide(FOREGROUND_GAINED), OutputPolicy::NORMAL);
    }

    /// 背面から戻っても、フォーカスを失ったままなら止めたまま（両方が解けたときだけ再開）。
    #[test]
    fn returning_to_foreground_keeps_pause_while_focus_is_lost() {
        let policy = decide(OutputConditions { background: false, focus: AudioFocus::Lost });
        assert!(policy.paused);
    }

    /// ログの本文が遷移の向きと理由を表すこと。
    #[test]
    fn change_description_names_direction_and_reason() {
        let paused = OutputPolicy { paused: true, gain: FULL_GAIN };
        let ducked = OutputPolicy { paused: false, gain: DUCKED_GAIN };
        let in_background = OutputConditions { background: true, focus: AudioFocus::Released };

        let text = describe_change(OutputPolicy::NORMAL, paused, in_background);
        assert!(text.contains("一時停止しました") && text.contains("背面=はい"), "{text}");

        let text = describe_change(paused, OutputPolicy::NORMAL, FOREGROUND_GAINED);
        assert!(text.contains("再開しました") && text.contains("×1.00"), "{text}");

        let ducking = OutputConditions { background: false, focus: AudioFocus::Ducked };
        let text = describe_change(OutputPolicy::NORMAL, ducked, ducking);
        assert!(text.contains("×0.20") && text.contains("ダッキング"), "{text}");
    }
}
