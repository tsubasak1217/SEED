// ============================================================
//  gpu_timing/timer.rs — GPU タイムスタンプの書き込み・読み戻し（wgpu）
//
//  【仕組み】
//  - フレームの頭（begin_frame）で「基準」を 1 つ書き、描画の節目ごとに mark で区間名付きの
//    タイムスタンプを書く（`CommandEncoder::write_timestamp`。レンダーパスの外でだけ呼ぶ）。
//  - 提出の直前（end_frame）でクエリを解決バッファへ解き、読み戻し用のバッファへ写す。
//  - 提出の後（after_submit）で読み戻し用のバッファのマップを予約し、次以降のフレームの頭で
//    マップが済んだものから集計へ足す（待たない。1〜数フレーム遅れて届く）。
//  - 読み戻し用のバッファは READBACK_SLOTS 枚を順に使い、空きが無いフレームは測らない
//    （マップ中のバッファへ写すことは wgpu が許さないため）。
//
//  【注意（タイルベースの GPU）】
//  Mali などのタイルベースの GPU は、隣り合うレンダーパスの頂点処理と画素処理を重ねて実行することがある。
//  タイムスタンプ（BOTTOM_OF_PIPE）は「それまでの仕事が終わった時刻」なので、区間の合計はフレームの
//  GPU 時間に一致するが、区間の境目で仕事が前後の区間へ少し漏れることがある（目安として読む）。
//
//  計測は既定でオフ（Renderer が TIMESTAMP_QUERY 系の feature を要求したときだけ作られる）。
// ============================================================

use std::sync::mpsc::{channel, Receiver, TryRecvError};
use std::time::{Duration, Instant};

use super::report::{CpuFrameSample, TimingAccumulator, BEGIN};
use super::segments;

/// 1 フレームに書けるタイムスタンプの数（区間 18 ＋ 同名の繰り返しに余裕を持たせる）。
const MAX_MARKS: u32 = 32;

/// タイムスタンプ 1 つのバイト数（u64）。
const TIMESTAMP_BYTES: u64 = std::mem::size_of::<u64>() as u64;

/// 読み戻し用のバッファの枚数（マップの完了が 2 フレーム遅れても測り続けられる数）。
const READBACK_SLOTS: usize = 3;

/// 集計を 1 行にして出す間隔。
const REPORT_INTERVAL: Duration = Duration::from_secs(3);

/// 読み戻し用のバッファ 1 枚の状態。
enum SlotState {
    /// 使っていない（このフレームで書き込み先にできる）。
    Free,
    /// このフレームの提出で写す（提出の後にマップを予約する）。
    Recorded,
    /// マップを予約した（完了の知らせを待っている）。
    Mapping(Receiver<Result<(), wgpu::BufferAsyncError>>),
}

/// 読み戻し用のバッファ 1 枚。
struct ReadbackSlot {
    /// MAP_READ | COPY_DST のバッファ（MAX_MARKS 個のタイムスタンプぶん）。
    buffer: wgpu::Buffer,
    /// 状態。
    state: SlotState,
    /// 写したタイムスタンプの区間名（書いた順）。
    labels: Vec<&'static str>,
}

/// パスごとの GPU 時間の計測器。
pub struct GpuPassTimer {
    /// タイムスタンプのクエリセット。
    query_set: wgpu::QuerySet,
    /// クエリを解く先（QUERY_RESOLVE | COPY_SRC）。
    resolve_buffer: wgpu::Buffer,
    /// 読み戻し用のバッファ。
    slots: Vec<ReadbackSlot>,
    /// タイムスタンプ 1 つあたりのナノ秒。
    period_ns: f32,
    /// このフレームで書き込み先にした読み戻しのバッファ（None ならこのフレームは測らない）。
    active_slot: Option<usize>,
    /// このフレームで書いたタイムスタンプの区間名。
    frame_labels: Vec<&'static str>,
    /// 集計。
    accumulator: TimingAccumulator,
    /// 集計を始めた時刻。
    window_started: Instant,
}

impl GpuPassTimer {
    /// 計測器を作る（デバイスが TIMESTAMP_QUERY と TIMESTAMP_QUERY_INSIDE_ENCODERS を持つこと）。
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Self {
        let query_set = device.create_query_set(&wgpu::QuerySetDescriptor {
            label: Some("GPU Pass Timer Queries"),
            ty: wgpu::QueryType::Timestamp,
            count: MAX_MARKS,
        });
        let bytes = u64::from(MAX_MARKS) * TIMESTAMP_BYTES;
        let resolve_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("GPU Pass Timer Resolve"),
            size: bytes,
            usage: wgpu::BufferUsages::QUERY_RESOLVE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let slots = (0..READBACK_SLOTS)
            .map(|index| ReadbackSlot {
                buffer: device.create_buffer(&wgpu::BufferDescriptor {
                    label: Some(&format!("GPU Pass Timer Readback {index}")),
                    size: bytes,
                    usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                }),
                state: SlotState::Free,
                labels: Vec::new(),
            })
            .collect();
        Self {
            query_set,
            resolve_buffer,
            slots,
            period_ns: queue.get_timestamp_period(),
            active_slot: None,
            frame_labels: Vec::with_capacity(MAX_MARKS as usize),
            accumulator: TimingAccumulator::default(),
            window_started: Instant::now(),
        }
    }

    /// タイムスタンプ 1 つあたりのナノ秒（起動ログ用）。
    pub fn period_ns(&self) -> f32 {
        self.period_ns
    }

    /// フレームの頭で呼ぶ: 届いた読み戻しを集計へ足し、空きがあればこのフレームの基準を書く。
    pub fn begin_frame(&mut self, device: &wgpu::Device, encoder: &mut wgpu::CommandEncoder) {
        self.collect_finished(device);
        self.frame_labels.clear();
        self.active_slot = self.slots.iter().position(|slot| matches!(slot.state, SlotState::Free));
        if self.active_slot.is_some() {
            self.mark(encoder, BEGIN);
        }
    }

    /// 描画の節目で呼ぶ: 1 つ前の節目からここまでを `segment` の区間として測る（レンダーパスの外で呼ぶ）。
    pub fn mark(&mut self, encoder: &mut wgpu::CommandEncoder, segment: &'static str) {
        if self.active_slot.is_none() || self.frame_labels.len() as u32 >= MAX_MARKS {
            return;
        }
        encoder.write_timestamp(&self.query_set, self.frame_labels.len() as u32);
        self.frame_labels.push(segment);
    }

    /// 提出の直前で呼ぶ: このフレームのタイムスタンプを解いて読み戻し用のバッファへ写す。
    pub fn end_frame(&mut self, encoder: &mut wgpu::CommandEncoder) {
        let Some(index) = self.active_slot.take() else { return };
        let count = self.frame_labels.len() as u32;
        if count < 2 {
            return; // 区間が 1 つも無い（基準だけ）
        }
        encoder.resolve_query_set(&self.query_set, 0..count, &self.resolve_buffer, 0);
        let slot = &mut self.slots[index];
        encoder.copy_buffer_to_buffer(
            &self.resolve_buffer,
            0,
            &slot.buffer,
            0,
            u64::from(count) * TIMESTAMP_BYTES,
        );
        slot.labels.clear();
        slot.labels.extend_from_slice(&self.frame_labels);
        slot.state = SlotState::Recorded;
    }

    /// 提出の後で呼ぶ: このフレームで写した読み戻し用のバッファのマップを予約する。
    pub fn after_submit(&mut self) {
        for slot in &mut self.slots {
            if matches!(slot.state, SlotState::Recorded) {
                let (sender, receiver) = channel();
                let bytes = slot.labels.len() as u64 * TIMESTAMP_BYTES;
                slot.buffer.slice(0..bytes).map_async(wgpu::MapMode::Read, move |result| {
                    let _ = sender.send(result);
                });
                slot.state = SlotState::Mapping(receiver);
            }
        }
    }

    /// CPU 側の 1 フレームの値を足す（フレームループの既存の計測値）。
    pub fn record_cpu(&mut self, sample: CpuFrameSample) {
        self.accumulator.add_cpu_frame(sample);
    }

    /// 集計の間隔が過ぎていれば 1 行（`[SEED GPU] …`）を返して集計を始め直す。
    pub fn take_report(&mut self, now: Instant) -> Option<String> {
        let elapsed = now.saturating_duration_since(self.window_started);
        if elapsed < REPORT_INTERVAL || self.accumulator.is_empty() {
            return None;
        }
        let line = format!(
            "[SEED GPU] {}",
            self.accumulator.format(elapsed.as_secs_f64(), &segments::ORDER)
        );
        self.accumulator = TimingAccumulator::default();
        self.window_started = now;
        Some(line)
    }

    /// マップが済んだ読み戻しを集計へ足して空きへ戻す（待たない）。
    fn collect_finished(&mut self, device: &wgpu::Device) {
        if !self.slots.iter().any(|slot| matches!(slot.state, SlotState::Mapping(_))) {
            return;
        }
        // マップの完了の知らせを進める（待たない）。
        let _ = device.poll(wgpu::PollType::Poll);
        for slot in &mut self.slots {
            let SlotState::Mapping(receiver) = &slot.state else { continue };
            match receiver.try_recv() {
                Ok(Ok(())) => {
                    let bytes = slot.labels.len() as u64 * TIMESTAMP_BYTES;
                    {
                        let view = slot.buffer.slice(0..bytes).get_mapped_range();
                        // マップの先頭が 8 バイト境界とは限らないので、u64 への読み替えはバイト列から組み立てる
                        // （bytemuck::cast_slice は境界がずれていると panic する）。
                        let ticks: Vec<u64> = view
                            .chunks_exact(TIMESTAMP_BYTES as usize)
                            .map(|chunk| {
                                let mut raw = [0u8; TIMESTAMP_BYTES as usize];
                                raw.copy_from_slice(chunk);
                                u64::from_le_bytes(raw)
                            })
                            .collect();
                        self.accumulator.add_gpu_frame(&slot.labels, &ticks, self.period_ns);
                    }
                    slot.buffer.unmap();
                    slot.state = SlotState::Free;
                }
                // マップの失敗（デバイスの喪失等）: そのフレームは捨てて空きへ戻す。
                Ok(Err(_)) | Err(TryRecvError::Disconnected) => slot.state = SlotState::Free,
                // まだ届いていない: 次のフレームでまた見る。
                Err(TryRecvError::Empty) => {}
            }
        }
    }
}
