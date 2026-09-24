// ============================================================
//  core/audio/output/stream_builder.rs — 出力ストリーム（cpal）とミキサーを開く
//
//  【rodio の OutputStream を使わない理由】
//  rodio 0.20 の OutputStream は中の cpal::Stream を公開しておらず、ストリームを一時停止できない
//  （Sink を全部 pause しても、オーディオスレッドは無音を作り続け、Android では AAudio のストリームが
//  「再生中」のまま端末の音声経路を起こし続ける）。そこで cpal のストリームを自分で開いて持ち、
//  一時停止（Android の oboe では AAudio の requestPause。コールバック自体が止まる）できるようにした。
//
//  【開き方は rodio 0.20.1 の OutputStream::try_default と同じ】
//    1. 既定の出力デバイスを、そのデバイスの既定の設定で開く
//    2. 失敗したら、そのデバイスが対応する設定を cpal の既定の優先順（cmp_default_heuristics）に並べ、
//       各設定の最大周波数 → 44.1 kHz（範囲の内側なら）→ 最小周波数の順に試す
//    3. それでも駄目なら、他の出力デバイスを順に 1〜2 の手順で試す
//  ミキサー（rodio::dynamic_mixer）は開けた設定（チャンネル数・周波数）で作り、Sink はそこへ足す
//  （rodio の Sink::try_new が内部でしているのと同じ）。データコールバックはミキサーから 1 サンプルずつ取り、
//  全体音量（gain.rs）を掛けて出力形式へ変換する。対応する出力形式も rodio と同じ 10 種類。
//  違いは (1) ストリームを返すこと、(2) 全体音量を掛けること（倍率 1 なら値は変わらない）、
//  (3) 符号無し整数形式の無音をその形式の中央値（EQUILIBRIUM）にしたこと（rodio は MAX / 2。1 ずれる。
//  Windows（WASAPI の共有モードは通常 f32）と Android（oboe は i16 / f32 だけ）では使われない形式）だけ。
// ============================================================

use std::sync::Arc;

use rodio::cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use rodio::cpal::{self, FromSample, SampleFormat, SampleRate, SizedSample, SupportedStreamConfig};
use rodio::dynamic_mixer::{self, DynamicMixer, DynamicMixerController};
use rodio::StreamError;

use super::gain::{fill_buffer, GainRamp, OutputGain};

/// 設定の周波数範囲の内側にあれば、最大・最小に加えて試す周波数（rodio と同じ 44.1 kHz）。
const FALLBACK_SAMPLE_RATE: SampleRate = SampleRate(44_100);

/// 開けた出力ストリームと、そこへ音を足すミキサー。
pub struct OpenedStream {
    /// 再生を始めた cpal のストリーム（Drop で閉じる）。
    pub stream: cpal::Stream,
    /// 音（Sink の出口）を足すミキサーの操作口。
    pub mixer: Arc<DynamicMixerController<f32>>,
    /// 実際に開けた設定（ログ用）。
    pub config: SupportedStreamConfig,
}

/// 既定の出力デバイスでストリームを開いて再生を始める（手順はファイル先頭のコメント）。
///
/// # 引数
/// * `gain` - 全体音量の倍率（コールバックがバッファごとに読む）
pub fn open_default(gain: &Arc<OutputGain>) -> Result<OpenedStream, StreamError> {
    let default_device = cpal::default_host().default_output_device().ok_or(StreamError::NoDevice)?;
    open_on_device(&default_device, gain).or_else(|original_err| {
        // 既定のデバイスで開けなければ、他のデバイスを順に試す（どれも駄目なら最初の失敗を返す）。
        let mut devices = match cpal::default_host().output_devices() {
            Ok(devices) => devices,
            Err(_) => return Err(original_err),
        };
        devices
            .find_map(|device| open_on_device(&device, gain).ok())
            .ok_or(original_err)
    })
}

/// 指定のデバイスを、その既定の設定で開く。
fn open_on_device(device: &cpal::Device, gain: &Arc<OutputGain>) -> Result<OpenedStream, StreamError> {
    let default_config = device
        .default_output_config()
        .map_err(StreamError::DefaultStreamConfigError)?;
    open_with_config(device, default_config, gain)
}

/// 指定の設定で開き、駄目なら対応する他の設定を順に試して、開けたら再生を始める。
fn open_with_config(
    device: &cpal::Device,
    config: SupportedStreamConfig,
    gain: &Arc<OutputGain>,
) -> Result<OpenedStream, StreamError> {
    let opened = build_with_format(device, config, gain).or_else(|err| {
        supported_output_formats(device)?
            .find_map(|format| build_with_format(device, format, gain).ok())
            .ok_or(StreamError::BuildStreamError(err))
    })?;
    opened.stream.play().map_err(StreamError::PlayStreamError)?;
    Ok(opened)
}

/// 1 つの設定でミキサーとストリームを作る（まだ再生は始めない）。
fn build_with_format(
    device: &cpal::Device,
    format: SupportedStreamConfig,
    gain: &Arc<OutputGain>,
) -> Result<OpenedStream, cpal::BuildStreamError> {
    let (controller, mixer) = dynamic_mixer::mixer::<f32>(format.channels(), format.sample_rate().0);
    let stream_config = format.config();
    let gain = Arc::clone(gain);
    let stream = match format.sample_format() {
        SampleFormat::F32 => build_typed::<f32>(device, &stream_config, mixer, gain),
        SampleFormat::F64 => build_typed::<f64>(device, &stream_config, mixer, gain),
        SampleFormat::I8 => build_typed::<i8>(device, &stream_config, mixer, gain),
        SampleFormat::I16 => build_typed::<i16>(device, &stream_config, mixer, gain),
        SampleFormat::I32 => build_typed::<i32>(device, &stream_config, mixer, gain),
        SampleFormat::I64 => build_typed::<i64>(device, &stream_config, mixer, gain),
        SampleFormat::U8 => build_typed::<u8>(device, &stream_config, mixer, gain),
        SampleFormat::U16 => build_typed::<u16>(device, &stream_config, mixer, gain),
        SampleFormat::U32 => build_typed::<u32>(device, &stream_config, mixer, gain),
        SampleFormat::U64 => build_typed::<u64>(device, &stream_config, mixer, gain),
        _ => return Err(cpal::BuildStreamError::StreamConfigNotSupported),
    }?;
    Ok(OpenedStream { stream, mixer: controller, config: format })
}

/// 出力形式 `T` のストリームを作る。コールバックはミキサーの音に全体音量を掛けて `T` へ変換する。
fn build_typed<T>(
    device: &cpal::Device,
    config: &cpal::StreamConfig,
    mut mixer: DynamicMixer<f32>,
    gain: Arc<OutputGain>,
) -> Result<cpal::Stream, cpal::BuildStreamError>
where
    T: SizedSample + FromSample<f32> + Send + 'static,
{
    let mut ramp = GainRamp::new(gain.get());
    device.build_output_stream::<T, _, _>(
        config,
        move |data: &mut [T], _info: &cpal::OutputCallbackInfo| {
            // 目標の倍率はバッファごとに 1 回だけ読む（1 サンプルごとに原子操作をしない）。
            fill_buffer(data, &mut mixer, &mut ramp, gain.get());
        },
        |err| eprintln!("[SEED AUDIO] 出力ストリームでエラーが起きました: {err}"),
        None,
    )
}

/// デバイスが対応する設定を、試す順に並べる（rodio 0.20.1 の supported_output_formats と同じ）。
fn supported_output_formats(
    device: &cpal::Device,
) -> Result<impl Iterator<Item = SupportedStreamConfig>, StreamError> {
    let mut supported: Vec<_> = device
        .supported_output_configs()
        .map_err(StreamError::SupportedStreamConfigsError)?
        .collect();
    supported.sort_by(|a, b| b.cmp_default_heuristics(a));

    Ok(supported.into_iter().flat_map(|range| {
        let max_rate = range.max_sample_rate();
        let min_rate = range.min_sample_rate();
        let mut formats = vec![range.with_max_sample_rate()];
        if FALLBACK_SAMPLE_RATE < max_rate && FALLBACK_SAMPLE_RATE > min_rate {
            formats.push(range.with_sample_rate(FALLBACK_SAMPLE_RATE));
        }
        formats.push(range.with_sample_rate(min_rate));
        formats
    }))
}
