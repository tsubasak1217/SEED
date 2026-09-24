// ============================================================
//  core/audio/spatial_voice.rs — 左右の耳の位置で音量を振り分ける音源 1 つ（AudioComponent 用）
//
//  rodio 0.20 の SpatialSink と同じ仕組みを、自前の出力（output::AudioOutput）の Sink の上に作ったもの。
//  SpatialSink は rodio の OutputStreamHandle からしか作れず、出力ストリームを自前で持つ
//  （一時停止のため。output/stream_builder.rs の冒頭）と使えなくなったため、同じ処理をここに移した。
//    - 流す音を rodio::source::Spatial（音源と両耳の距離で左右の音量を決める）で包む
//    - 位置は Mutex 越しに共有し、オーディオスレッド側が一定間隔（SPATIAL_UPDATE_PERIOD）で読み直す
//  音量・停止・再生終了の判定は中の Sink がそのまま受け持つ。
// ============================================================

use std::sync::{Arc, Mutex};
use std::time::Duration;

use rodio::cpal::FromSample;
use rodio::source::Spatial;
use rodio::{Sample, Sink, Source};

/// オーディオスレッドが音源・両耳の位置を読み直す間隔（rodio の SpatialSink と同じ 10 ms）。
const SPATIAL_UPDATE_PERIOD: Duration = Duration::from_millis(10);

/// 音源と両耳の位置（仮想空間の座標）。
#[derive(Debug, Clone, Copy)]
struct SoundPositions {
    /// 音源の位置。
    emitter: [f32; 3],
    /// 左耳の位置。
    left_ear: [f32; 3],
    /// 右耳の位置。
    right_ear: [f32; 3],
}

/// 左右の耳の位置で音量を振り分けて鳴らす音源 1 つ。
pub struct SpatialVoice {
    /// 実際に音を流す Sink（音量・停止・再生終了の判定）。
    sink: Sink,
    /// 音源と両耳の位置（オーディオスレッドと共有する）。
    positions: Arc<Mutex<SoundPositions>>,
}

impl SpatialVoice {
    /// ミキサーへつないだ Sink と、最初の位置から作る。
    ///
    /// # 引数
    /// * `sink`      - 出力（AudioOutput::new_sink）で作った Sink
    /// * `emitter`   - 音源の位置
    /// * `left_ear`  - 左耳の位置
    /// * `right_ear` - 右耳の位置（左耳と同じ位置にはしない。左右の差が 0 だと振り分けられない）
    pub fn new(sink: Sink, emitter: [f32; 3], left_ear: [f32; 3], right_ear: [f32; 3]) -> Self {
        Self {
            sink,
            positions: Arc::new(Mutex::new(SoundPositions { emitter, left_ear, right_ear })),
        }
    }

    /// 音源の位置を変える（オーディオスレッドが次に読み直したときから効く）。
    pub fn set_emitter_position(&self, position: [f32; 3]) {
        // 毒されたロック（オーディオスレッドが保持中に panic）でも座標の書き換えは安全なので続ける。
        let mut positions = self.positions.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        positions.emitter = position;
    }

    /// 音を流す（Sink の後ろへ積む）。
    pub fn append<S>(&self, source: S)
    where
        S: Source + Send + 'static,
        f32: FromSample<S::Item>,
        S::Item: Sample + Send,
    {
        let start = *self.positions.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let shared = Arc::clone(&self.positions);
        let source = Spatial::new(source, start.emitter, start.left_ear, start.right_ear)
            .periodic_access(SPATIAL_UPDATE_PERIOD, move |spatial| {
                let now = *shared.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
                spatial.set_positions(now.emitter, now.left_ear, now.right_ear);
            });
        self.sink.append(source);
    }

    /// 音量を変える（1.0 = 等倍）。
    pub fn set_volume(&self, volume: f32) {
        self.sink.set_volume(volume);
    }

    /// 再生を止めて積んだ音を捨てる。
    pub fn stop(&self) {
        self.sink.stop();
    }

    /// 鳴らす音が残っていないか（再生し終えた）。
    pub fn empty(&self) -> bool {
        self.sink.empty()
    }
}
