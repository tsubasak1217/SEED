// ============================================================
//  audio_ops.rs — スクリプト発行のオーディオコマンド適用
//
//  C# スクリプトの SEED.Audio.Play / PlayBgm / StopBgm は
//  host_api のオーディオコマンドキューへ積まれる。
//  本モジュールの apply_script_audio_commands がフレーム末尾に
//  それらをまとめて AudioManager へ適用する。
// ============================================================

use crate::engine::core::audio::AudioManager;
use crate::engine::core::scripting::{take_audio_commands, ScriptAudioCommand};

use super::App;

impl App {
    /// スクリプトが積んだオーディオコマンドを適用し、再生済み SE を回収する。
    ///
    /// frame_renderer のゲームロジックブロック直後（シーンコマンド適用の隣）に呼ばれる。
    /// AudioManager は初回コマンド時に遅延初期化する
    /// （オーディオデバイスが無い環境では初期化に失敗し、全コマンドが無音で無視される）。
    pub(super) fn apply_script_audio_commands(&mut self) {
        let commands = take_audio_commands();

        // コマンドが無くても再生終了 SE の回収だけは行う
        if commands.is_empty() {
            if let Some(audio) = &mut self.audio {
                audio.cleanup();
            }
            return;
        }

        // 遅延初期化（デバイスなしでは None のまま = 無音動作）
        if self.audio.is_none() {
            self.audio = AudioManager::new();
            if self.audio.is_none() {
                eprintln!("[Script] Audio: オーディオデバイスの初期化に失敗しました（無音で継続）");
            }
        }
        let Some(audio) = &mut self.audio else { return };

        for cmd in commands {
            match cmd {
                ScriptAudioCommand::PlaySe { path, volume } => {
                    audio.play_se(&path, volume);
                }
                ScriptAudioCommand::PlayBgm { path, volume, looped } => {
                    audio.play_bgm(&path, volume, looped);
                }
                ScriptAudioCommand::StopBgm => {
                    audio.stop_bgm();
                }
                ScriptAudioCommand::SetBgmVolume { volume } => {
                    audio.set_bgm_volume(volume);
                }
            }
        }
        audio.cleanup();
    }
}
