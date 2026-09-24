// ============================================================
//  audio_output_sync.rs — 背面・音声フォーカスに合わせて音声の出力全体を止める・戻す
//
//  【役割】
//  「アプリが背面にいるか」（core::background_gate）と「OS の音声フォーカス」（platform::audio_focus。
//  Android の UI スレッドから JNI で届く）を集め、出力全体の状態（core/audio/output_policy.rs の純関数）を
//  決めて AudioManager へ当てる。変わったときだけ実際に切り替え、ログ（[SEED AUDIO]）を出す。
//
//  【呼ばれる場所】
//    - background_lifecycle.rs の enter_background（背面の印を立てた直後）・enter_foreground（印を下ろした直後）
//      … 背面への出入りはフレームが回らないところで起きるので、その場で当てる
//    - render.rs の about_to_wait（イベントループの 1 周ごと）… 音声フォーカスの変化を次の周回で当てる。
//      フレーム（RedrawRequested）ごとにしないのは、ホームへ戻る途中などでフレームが止まってもループは回り続けるため
//      （フレームで見ると、手放した音声フォーカスの反映が suspended まで約 1 秒遅れ、その間は鳴り続けた。エミュレータで確認）。
//      背面の間はループが眠るが、背面で止めているので困らない（前面へ戻るときに最新の状態を当てる）
//    - audio_ops.rs の ensure_audio_manager（AudioManager を作った直後）… 作る前から続いている状態を、
//      最初の音を鳴らす前に当てる
//  デスクトップは背面にも音声フォーカスの喪失にもならないので、状態は常に「通常」で何も起きない。
// ============================================================

use crate::engine::core::audio::output_policy::{self, OutputConditions};
use crate::engine::core::background_gate;
use crate::engine::platform::audio_focus;

use super::App;

/// ログの行頭（Android の音声まわりの印）。
const LOG_TAG: &str = "[SEED AUDIO]";

impl App {
    /// 背面・音声フォーカスから出力全体の状態を決めて AudioManager へ当てる（変わったときだけ切り替える）。
    ///
    /// AudioManager がまだ無い（まだ何も鳴らしていない）ときは何もしない。作ったときに改めて呼ばれる。
    pub(super) fn sync_audio_output(&mut self) {
        let conditions = OutputConditions {
            background: background_gate::is_background(),
            focus: audio_focus::current(),
        };
        let policy = output_policy::decide(conditions);
        let Some(audio) = &mut self.audio else { return };
        if let Some(previous) = audio.apply_output_policy(policy) {
            eprintln!("{LOG_TAG} {}", output_policy::describe_change(previous, policy, conditions));
        }
    }
}
