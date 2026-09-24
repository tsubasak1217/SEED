// ============================================================
//  core/audio/output — 音声の出力（出力ストリーム・ミキサー・全体音量）
//
//  【構成】
//    stream_builder … cpal の出力ストリームとミキサーを開く（rodio 0.20 の OutputStream::try_default と同じ手順）
//    gain           … 全体音量の倍率（ダッキング）と、出力バッファの書き込み（コールバックの本体）
//
//  【AudioOutput の役割】
//  AudioManager が 1 つだけ持つ「スピーカーへの出口」。
//    - new_sink: 音を流す Sink を作ってミキサーへつなぐ（rodio の Sink::try_new と同じつなぎ方）
//    - set_paused: 出力ストリームごと一時停止・再開する（Android の背面・音声フォーカスの喪失。
//      止めている間はオーディオスレッドのコールバックも来ず、どの音の再生位置も進まない。
//      Sink ごとの一時停止（スクリプトの PauseBgm 等）には触らない）
//    - set_gain: 全体音量の倍率（ダッキング）
//  デスクトップは一時停止も倍率の変更もしない（出力は rodio の OutputStream だった頃と同じ）。
// ============================================================

mod gain;
mod stream_builder;

use std::sync::Arc;

use rodio::cpal::traits::StreamTrait;
use rodio::dynamic_mixer::DynamicMixerController;
use rodio::Sink;

use crate::engine::core::audio::output_policy::FULL_GAIN;
use crate::engine::platform;

use self::gain::OutputGain;

/// 音声の出力（出力ストリームとミキサー）。Drop すると全音声が止まる。
pub struct AudioOutput {
    /// 再生中の cpal ストリーム（一時停止・再開に使う）。
    stream: rodio::cpal::Stream,
    /// 音を足すミキサーの操作口（Sink の出口をここへ足す）。
    mixer: Arc<DynamicMixerController<f32>>,
    /// 全体音量の倍率（コールバックと共有する）。
    gain: Arc<OutputGain>,
    /// いまストリームを一時停止しているか。
    paused: bool,
}

impl AudioOutput {
    /// 既定の出力デバイスで開いて再生を始める。出力デバイスが無い・開けないときは None（原因はログへ）。
    pub fn open_default() -> Option<Self> {
        let gain = Arc::new(OutputGain::new(FULL_GAIN));
        match stream_builder::open_default(&gain) {
            Ok(opened) => {
                // 開けた設定は端末の検証用の診断ログにだけ出す（デスクトップのログは従来どおり増やさない）。
                if platform::CURRENT.lifecycle_diag_log {
                    eprintln!(
                        "[SEED AUDIO] 出力ストリームを開きました（{}ch・{} Hz・{:?}）",
                        opened.config.channels(),
                        opened.config.sample_rate().0,
                        opened.config.sample_format()
                    );
                }
                Some(Self { stream: opened.stream, mixer: opened.mixer, gain, paused: false })
            }
            Err(err) => {
                eprintln!("[SEED AUDIO] 出力ストリームを開けません: {err}");
                None
            }
        }
    }

    /// 音を流す Sink を作り、ミキサーへつなぐ（再生は append したときから始まる）。
    pub fn new_sink(&self) -> Sink {
        let (sink, queue_output) = Sink::new_idle();
        self.mixer.add(queue_output);
        sink
    }

    /// 出力ストリームを一時停止する（true）・再開する（false）。今と同じなら何もしない。
    ///
    /// 失敗（ストリームがデバイスから切り離された等）はログに残し、状態は変えない。自動でやり直しはしない
    /// （AudioManager は状態が変わったときにしか呼ばない。切り離されたストリームは毎回失敗するので、
    /// やり直すとログが埋まるだけ。そのようなストリームはもともと音を出せない）。
    pub fn set_paused(&mut self, paused: bool) {
        if self.paused == paused {
            return;
        }
        let result = if paused {
            self.stream.pause().map_err(|err| err.to_string())
        } else {
            self.stream.play().map_err(|err| err.to_string())
        };
        match result {
            Ok(()) => self.paused = paused,
            Err(err) => {
                let action = if paused { "一時停止" } else { "再開" };
                eprintln!("[SEED AUDIO] 出力ストリームの{action}に失敗しました: {err}");
            }
        }
    }

    /// いま出力ストリームを一時停止しているか。
    pub fn is_paused(&self) -> bool {
        self.paused
    }

    /// 全体音量の倍率を変える（次のバッファから 1 バッファかけて移る）。
    pub fn set_gain(&self, gain: f32) {
        self.gain.set(gain);
    }
}
