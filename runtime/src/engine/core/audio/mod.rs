// ============================================================
//  core/audio — オーディオマネージャ
//
//  【責務】
//    rodio を用いた BGM / SE の再生管理。
//    スクリプトの Audio API（SEED.Audio.Play 等）から
//    コマンドキュー経由で呼ばれる（audio_ops.rs 参照）。
//
//  【設計】
//    - ファイルは asset_fs 経由で読む（assets:// 仮想パス・PAK モード対応）。
//    - デコード前の生バイト列をキャッシュし、再生ごとに Cursor で包んで
//      Decoder へ渡す（同じ SE の連続再生でディスク読みが発生しない）。
//    - **SE は「デコード済み PCM」をキャッシュして鳴らす**（下の「SE の再生方式」参照）。
//    - BGM は専用 Sink を 1 本保持し、切り替え時は前の BGM を停止する。
//    - SE は再生ごとに Sink を生成し、finished になったものは cleanup で回収する。
//    - オーディオデバイスが無い環境では new が None を返し、全操作が無音で無視される。
//
//  【SE の再生方式 — なぜ PCM をキャッシュするのか】
//    以前は play_se のたびに `Decoder::new` で圧縮音声を開き、その Decoder を
//    そのまま Sink へ流していた。これは 2 か所で高くつく。
//
//      1. `Decoder::new`（フォーマット判定＋初期化）を**メインスレッドで毎回**払う。
//         実測（debug ビルド・21KB の MP3）で 1 回あたり 2.1 ms。
//         リズムゲームの連打のように 1 秒に 10 回鳴らすと 21 ms/秒 がフレームループから消える。
//      2. 圧縮音声の**デコード本体はオーディオコールバックスレッドで再生中ずっと走る**。
//         同 MP3 の実測で「再生 1 秒あたり 75 ms の CPU」。音が 1.75 秒あるので、
//         連打で 10 声も重なるとコールバックが実時間に間に合わずアンダーラン
//         （＝音が途切れる・バリバリ歪む）を起こす。
//
//    そこで**初回の 1 回だけ全部デコードして f32 PCM にし、以後はその PCM を
//    Arc 共有のまま鳴らす**（PcmSource）。2 回目以降の play_se はデコードが消え、
//    オーディオスレッド側もメモリ上の f32 を読むだけになる。
//
//    **初回デコードはバックグラウンドスレッドで行う。** 全デコードは実測 115 ms
//    （debug ビルド・1.75 秒の MP3）掛かり、メインスレッドでやるとその音が初めて
//    鳴った 1 フレームだけ盛大に落ちる。デコードが終わるまでの再生は従来どおり
//    ストリーム再生でしのぎ、完成した PCM は次のフレーム以降で取り込む
//    （＝初回だけ従来と同じコスト、2 回目以降はキャッシュ）。
//
//    長い音（BGM を誤って Audio.Play した場合など）は PCM がメモリを食うので
//    PCM_CACHE_MAX_SECONDS を超えたらキャッシュせず、従来どおりストリーム再生へ落とす。
//    「長すぎる」と分かった結果もキャッシュに残す（毎回デコードし直さないため）。
// ============================================================

/// 音声辞書のキー索引（`グループ/用途` → パス・既定音量）。
/// AudioManager とは独立した純粋ロジックなので、サブモジュールとして分離している。
pub mod dictionary_index;

use std::collections::{HashMap, HashSet};
use std::io::Cursor;
use std::sync::Arc;
use std::sync::mpsc::{Receiver, Sender, channel};
use std::time::Duration;

use rodio::{Decoder, OutputStream, OutputStreamHandle, Sink, Source, SpatialSink};

use crate::engine::ecs::Entity;

// ─── SE の同時発音数・PCM キャッシュの定数 ───────────────────

/// SE 全体の同時発音数の上限【保険】。
/// 超えたら**最も古い声から**止める。声が増えるほどミキサーの 1 サンプルあたりの
/// 加算数が増えるため、暴走時にオーディオスレッドを守る最後の砦として置く。
const MAX_SE_VOICES: usize = 24;

/// **同じ SE**（同じアセットパス）の同時発音数の上限。
///
/// 同一波形が何重にも足し合わさると振幅がそのまま倍になり、出力で頭打ち（クリッピング）して
/// 「バリバリ」と歪む。連打で同じ音を鳴らすリズムゲームではこれが真っ先に効くので、
/// 全体の上限とは別に、同一音だけを厳しく制限する。
const MAX_SE_VOICES_PER_SOUND: usize = 4;

/// デコード済み PCM をキャッシュする長さの上限（秒）。
/// これより長い音は PCM がメモリを大きく食う（44.1kHz ステレオ f32 で 1 秒 ≒ 353KB）ため、
/// キャッシュせずストリーム再生に落とす。
const PCM_CACHE_MAX_SECONDS: f32 = 10.0;

// ─── 空間再生の定数 ──────────────────────────────────────────

/// リスナーの両耳オフセット（仮想空間単位）。
/// コンポーネント音源はエミッタを単位球面上に置くため、この値との比率でパンの強さが決まる。
const EAR_OFFSET: f32 = 0.3;

// ─── BGM 再生速度の定数 ──────────────────────────────────────

/// BGM 再生速度の下限（これ未満は音として成立しないため切り上げる）。
const BGM_SPEED_MIN: f32 = 0.25;

/// BGM 再生速度の上限。
const BGM_SPEED_MAX: f32 = 4.0;

/// BGM 再生速度の既定値（等倍）。
const BGM_SPEED_DEFAULT: f32 = 1.0;

// ─── 生バイト列の共有ラッパー ────────────────────────────────

/// キャッシュ済み音声ファイルの生バイト列。
/// Cursor<T: AsRef<[u8]>> の要件を満たすため Arc<Vec<u8>> を包む。
#[derive(Clone)]
struct SharedBytes(Arc<Vec<u8>>);

impl AsRef<[u8]> for SharedBytes {
    fn as_ref(&self) -> &[u8] {
        &self.0
    }
}

// ─── デコード済み PCM ────────────────────────────────────────

/// デコード済みの音声データ（f32・チャンネルインターリーブ）。
///
/// 1 つの SE につき 1 つだけ作り、鳴らすたびに `Arc` で共有する
/// （再生ごとにサンプル列を複製しないため、連打しても確保が増えない）。
struct DecodedPcm {
    /// インターリーブされたサンプル列（[L, R, L, R, …]）。
    samples: Vec<f32>,
    /// チャンネル数。
    channels: u16,
    /// サンプリング周波数（Hz）。
    sample_rate: u32,
    /// 全体の再生時間（`Source::total_duration` でそのまま返す）。
    duration: Duration,
}

impl DecodedPcm {
    /// サンプル列とフォーマットから作る（再生時間はここで一度だけ求める）。
    fn new(samples: Vec<f32>, channels: u16, sample_rate: u32) -> Self {
        let frames = samples.len() as f64 / (channels as f64 * sample_rate as f64);
        Self {
            samples,
            channels,
            sample_rate,
            duration: Duration::from_secs_f64(frames),
        }
    }
}

/// `Arc` 共有の PCM をそのまま鳴らす rodio ソース。
///
/// rodio 標準の `SamplesBuffer` は `Vec` を所有するため**再生のたびにサンプル列を複製**する。
/// SE は同じ音を何度も鳴らすので、複製の無いこちらを使う。
struct PcmSource {
    /// 鳴らす PCM（全再生で共有する）。
    pcm: Arc<DecodedPcm>,
    /// 次に返すサンプルの添字。
    pos: usize,
}

impl PcmSource {
    /// 先頭から鳴らすソースを作る。
    fn new(pcm: Arc<DecodedPcm>) -> Self {
        Self { pcm, pos: 0 }
    }
}

impl Iterator for PcmSource {
    type Item = f32;

    #[inline]
    fn next(&mut self) -> Option<f32> {
        let sample = *self.pcm.samples.get(self.pos)?;
        self.pos += 1;
        Some(sample)
    }
}

impl Source for PcmSource {
    /// フォーマットは最後まで一定なので「区切り無し」を返す。
    #[inline]
    fn current_frame_len(&self) -> Option<usize> {
        None
    }

    #[inline]
    fn channels(&self) -> u16 {
        self.pcm.channels
    }

    #[inline]
    fn sample_rate(&self) -> u32 {
        self.pcm.sample_rate
    }

    #[inline]
    fn total_duration(&self) -> Option<Duration> {
        Some(self.pcm.duration)
    }
}

// ─── 再生中の SE 1 声 ────────────────────────────────────────

/// 再生中の SE 1 声。どの音かを保持して「同じ音の重なり」を数えられるようにする。
struct SeVoice {
    /// 鳴らしている PCM（ストリーム再生へ落とした音は `None`）。
    /// 同一音の判定は `Arc::ptr_eq`（同じキャッシュ実体か）で行うので、
    /// パス文字列を持ち回らなくてよい。
    pcm: Option<Arc<DecodedPcm>>,
    /// rodio の再生ハンドル。`Sink` は drop すると再生が止まる（＝声の打ち切りになる）。
    sink: Sink,
}

impl SeVoice {
    /// 指定の PCM と同じ音か。
    fn is_same_sound(&self, pcm: &Arc<DecodedPcm>) -> bool {
        matches!(&self.pcm, Some(mine) if Arc::ptr_eq(mine, pcm))
    }
}

// ─── 初回デコード（バックグラウンド）────────────────────────

/// バックグラウンドデコードの結果（アセットパス, PCM）。
/// PCM が `None` は「キャッシュ対象外（長すぎる／デコードできない）」を表す。
type PcmDecodeResult = (String, Option<Arc<DecodedPcm>>);

/// 生バイト列を丸ごとデコードして PCM にする【デコードの唯一の実装】。
///
/// `AudioManager` を借りないのでバックグラウンドスレッドから呼べる。
/// `None` を返すのは「キャッシュせずストリーム再生に任せるべき音」。
fn decode_pcm(bytes: SharedBytes, path: &str) -> Option<Arc<DecodedPcm>> {
    let Ok(decoder) = Decoder::new(Cursor::new(bytes)) else {
        eprintln!("[Script] Audio: デコード失敗 ({path})");
        return None;
    };

    let channels = decoder.channels();
    let sample_rate = decoder.sample_rate();
    // 0 は PCM の長さ計算で 0 除算になるうえ、鳴らしようがないので弾く
    if channels == 0 || sample_rate == 0 {
        eprintln!("[Script] Audio: 不正なフォーマット ({path}: {channels}ch / {sample_rate}Hz)");
        return None;
    }

    // 長すぎる音でメモリを食い潰さないよう、上限に達した時点で打ち切って諦める
    // （最後まで読んでから捨てると、その分だけ無駄にデコードすることになる）。
    let max_samples = (PCM_CACHE_MAX_SECONDS * sample_rate as f32 * channels as f32).ceil() as usize;
    let mut samples: Vec<f32> = Vec::new();
    for sample in decoder.convert_samples::<f32>() {
        if samples.len() >= max_samples {
            eprintln!(
                "[Script] Audio: {PCM_CACHE_MAX_SECONDS} 秒を超えるため PCM キャッシュせず\
                 ストリーム再生します ({path})。効果音には短い音を使ってください。"
            );
            return None;
        }
        samples.push(sample);
    }
    if samples.is_empty() {
        return None;
    }

    Some(Arc::new(DecodedPcm::new(samples, channels, sample_rate)))
}

// ─── AudioManager ────────────────────────────────────────────

/// BGM / SE の再生を管理するオーディオマネージャ。
///
/// App が遅延初期化で保持し、スクリプトのオーディオコマンド適用時に使用する。
pub struct AudioManager {
    /// 出力ストリーム（Drop されると全音声が止まるため保持し続ける）
    _stream: OutputStream,
    /// Sink 生成用のストリームハンドル
    handle: OutputStreamHandle,
    /// 再生中の BGM（None = BGM なし）
    bgm: Option<Sink>,
    /// BGM の再生速度（1.0 = 等倍）。
    /// **Sink をまたいで保持する**ため、play_bgm で新しく作った Sink にもこの値が適用される
    /// （スクリプトが再生前に速度を決めても、再生後に変えても同じ結果になる）。
    bgm_speed: f32,
    /// 再生中の SE 群（finished は cleanup で回収する）
    se_sinks: Vec<SeVoice>,
    /// 再生中のコンポーネント音源（Key = AudioComponent のスロットエンティティ）。
    /// SpatialSink を使い、毎フレーム update_component_voice で減衰・パンを更新する。
    component_voices: HashMap<Entity, SpatialSink>,
    /// play_on_start を一度発火させたスロットエンティティ
    /// （非ループ SE が鳴り終わった後に再発火しないための記録）。
    component_started: HashSet<Entity>,
    /// パス → 生バイト列のキャッシュ
    cache: HashMap<String, SharedBytes>,
    /// パス → デコード済み PCM のキャッシュ（SE 専用）。
    ///
    /// 値が `None` は「一度試したがキャッシュ対象外（長すぎる／デコードできない）」の記録で、
    /// 毎回デコードし直さないために残している。
    pcm_cache: HashMap<String, Option<Arc<DecodedPcm>>>,
    /// いまバックグラウンドでデコード中のパス（同じ音で何本もスレッドを立てないための記録）。
    pcm_decoding: HashSet<String>,
    /// 初回デコードの完了通知を送る口（デコードスレッドへ複製して渡す）。
    pcm_done_tx: Sender<PcmDecodeResult>,
    /// 同・受け取る口（メインスレッドで毎フレーム引き取る）。
    pcm_done_rx: Receiver<PcmDecodeResult>,
}

impl AudioManager {
    /// 既定のオーディオデバイスで初期化する。デバイスが無い場合は None。
    pub fn new() -> Option<Self> {
        let (stream, handle) = OutputStream::try_default().ok()?;
        let (pcm_done_tx, pcm_done_rx) = channel();
        Some(Self {
            _stream: stream,
            handle,
            bgm: None,
            bgm_speed: BGM_SPEED_DEFAULT,
            se_sinks: Vec::new(),
            component_voices: HashMap::new(),
            component_started: HashSet::new(),
            cache: HashMap::new(),
            pcm_cache: HashMap::new(),
            pcm_decoding: HashSet::new(),
            pcm_done_tx,
            pcm_done_rx,
        })
    }

    /// 効果音を再生する（多重再生可）。volume は 1.0 = 等倍。
    ///
    /// 初回だけ全デコードして PCM をキャッシュし、2 回目以降はその PCM を共有して鳴らす
    /// （理由はファイル冒頭「SE の再生方式」）。同時発音数は
    /// [`MAX_SE_VOICES_PER_SOUND`] / [`MAX_SE_VOICES`] で頭打ちにする。
    pub fn play_se(&mut self, path: &str, volume: f32) {
        crate::profile_scope!("オーディオ/SE 再生");

        // 1. 済んだ初回デコードがあれば取り込む（この呼び出しから使えるようになる）
        self.collect_finished_decodes();

        // 2. デコード済み PCM を探す。まだ無ければバックグラウンドデコードを始める
        //    （この 1 回はストリーム再生でしのぐ）
        let pcm = self.pcm_cache.get(path).cloned().flatten();
        if pcm.is_none() {
            self.begin_background_decode(path);
        }

        // 3. 同時発音数の上限を守る（古い声から止める）
        self.enforce_se_voice_limits(pcm.as_ref());

        // 4. Sink を作って流す
        let Ok(sink) = Sink::try_new(&self.handle) else {
            return;
        };
        sink.set_volume(volume.max(0.0));
        match &pcm {
            // 通常経路: キャッシュ済み PCM をそのまま鳴らす（デコード無し）
            Some(cached) => sink.append(PcmSource::new(cached.clone())),
            // 例外経路: デコード待ち・キャッシュ対象外の音は従来どおりストリーム再生する
            None => {
                let Some(bytes) = self.load(path) else { return };
                let Ok(decoder) = Decoder::new(Cursor::new(bytes)) else {
                    eprintln!("[Script] Audio: デコード失敗 ({path})");
                    return;
                };
                sink.append(decoder);
            }
        }
        self.se_sinks.push(SeVoice { pcm, sink });
    }

    /// 済んだ初回デコードを PCM キャッシュへ取り込む【キャッシュ更新の唯一の入口】。
    ///
    /// `play_se` と `cleanup`（毎フレーム）から呼ぶので、デコードが終わった次の
    /// フレームにはキャッシュが効き始める。
    fn collect_finished_decodes(&mut self) {
        while let Ok((path, pcm)) = self.pcm_done_rx.try_recv() {
            self.pcm_decoding.remove(&path);
            self.pcm_cache.insert(path, pcm);
        }
    }

    /// SE の初回デコードをバックグラウンドスレッドで始める。
    ///
    /// 全デコードは長い音ほど重く（1.75 秒の MP3 で実測 115 ms）、メインスレッドで
    /// 行うとその音が初めて鳴ったフレームだけ大きく落ちるため、必ず別スレッドで行う。
    /// 既にキャッシュ済み／デコード中／読み込めないパスでは何もしない。
    fn begin_background_decode(&mut self, path: &str) {
        // 判定済み（PCM 有り・キャッシュ対象外のどちらも）ならやり直さない
        if self.pcm_cache.contains_key(path) {
            return;
        }
        // 既にデコード中なら二重に走らせない
        if !self.pcm_decoding.insert(path.to_string()) {
            return;
        }
        // 生バイト列はメインスレッドで用意する（asset_fs / PAK は &mut self が要る）。
        // 読めない音は「対象外」として記録し、以後デコードを試みない。
        let Some(bytes) = self.load(path) else {
            self.pcm_decoding.remove(path);
            self.pcm_cache.insert(path.to_string(), None);
            return;
        };

        let tx = self.pcm_done_tx.clone();
        let key = path.to_string();
        // 送信先が閉じている（アプリ終了時）場合はデコード結果を捨てるだけでよい
        std::thread::spawn(move || {
            let pcm = decode_pcm(bytes, &key);
            let _ = tx.send((key, pcm));
        });
    }

    /// SE の同時発音数を上限内へ収める（超えている分は**古い声から**止める）。
    ///
    /// これから鳴らす 1 声ぶんの空きを作るので、判定は「上限 - 1」ではなく
    /// 「上限に達していたら削る」で行う。
    ///
    /// - `incoming`: これから鳴らす音の PCM（ストリーム再生なら `None`）
    fn enforce_se_voice_limits(&mut self, incoming: Option<&Arc<DecodedPcm>>) {
        // 鳴り終わった声を先に回収する（まだ生きている声を無駄に打ち切らないため）
        self.se_sinks.retain(|voice| !voice.sink.empty());

        // 同じ音の重なりを制限する（同一波形の足し合わせによる歪みを防ぐ）
        if let Some(pcm) = incoming {
            while self
                .se_sinks
                .iter()
                .filter(|voice| voice.is_same_sound(pcm))
                .count()
                >= MAX_SE_VOICES_PER_SOUND
            {
                let Some(oldest) = self.se_sinks.iter().position(|v| v.is_same_sound(pcm)) else {
                    break;
                };
                self.se_sinks.remove(oldest); // drop で再生が止まる
            }
        }

        // 全体の上限（別々の音が大量に重なった場合の保険）
        while self.se_sinks.len() >= MAX_SE_VOICES {
            self.se_sinks.remove(0);
        }
    }

    /// BGM を再生する（既存の BGM は停止して置き換える）。
    /// looped = true でループ再生。volume は 1.0 = 等倍。
    pub fn play_bgm(&mut self, path: &str, volume: f32, looped: bool) {
        self.stop_bgm();
        let Some(bytes) = self.load(path) else { return };
        let Ok(decoder) = Decoder::new(Cursor::new(bytes)) else {
            eprintln!("[Script] Audio: デコード失敗 ({path})");
            return;
        };
        let Ok(sink) = Sink::try_new(&self.handle) else {
            return;
        };
        sink.set_volume(volume.max(0.0));
        // 保持している再生速度を新しい Sink にも引き継ぐ（等倍ならリセット扱いになる）
        sink.set_speed(self.bgm_speed);
        if looped {
            sink.append(decoder.repeat_infinite());
        } else {
            sink.append(decoder);
        }
        self.bgm = Some(sink);
    }

    /// BGM を一時停止する（再生位置は保持する）。
    ///
    /// stop_bgm と違い Sink を破棄しないので、resume_bgm で**止めた位置から**続けられる。
    /// リズムゲームのように「ゲーム時間の停止に合わせて BGM も凍結し、
    /// 再開時に拍の位相をそのまま繋ぎたい」用途のために用意している。
    /// BGM が無いとき・既に一時停止しているときは何もしない（多重呼び出し安全）。
    pub fn pause_bgm(&mut self) {
        if let Some(sink) = &self.bgm {
            sink.pause();
        }
    }

    /// 一時停止していた BGM を止めた位置から再開する。
    /// BGM が無いとき・再生中のときは何もしない（多重呼び出し安全）。
    pub fn resume_bgm(&mut self) {
        if let Some(sink) = &self.bgm {
            sink.play();
        }
    }

    /// BGM を停止する。
    pub fn stop_bgm(&mut self) {
        if let Some(sink) = self.bgm.take() {
            sink.stop();
        }
    }

    /// BGM の音量を変更する（1.0 = 等倍）。BGM 再生中でなければ何もしない。
    pub fn set_bgm_volume(&mut self, volume: f32) {
        if let Some(sink) = &self.bgm {
            sink.set_volume(volume.max(0.0));
        }
    }

    /// BGM の再生速度を変更する（1.0 = 等倍）。
    ///
    /// rodio の Sink::set_speed は**リサンプリングによる早送り／スロー再生**なので、
    /// 速度に比例してピッチも上下する（テンポだけを変える機能ではない）。
    ///
    /// 値は BGM_SPEED_MIN..=BGM_SPEED_MAX にクランプして保持し、BGM 再生中でなくても
    /// 記憶する（次の play_bgm で作られる Sink にも適用される）。
    pub fn set_bgm_speed(&mut self, speed: f32) {
        let speed = if speed.is_finite() {
            speed.clamp(BGM_SPEED_MIN, BGM_SPEED_MAX)
        } else {
            BGM_SPEED_DEFAULT
        };
        self.bgm_speed = speed;
        if let Some(sink) = &self.bgm {
            sink.set_speed(speed);
        }
    }

    /// 再生し終えた SE / コンポーネント音源の Sink を回収する（毎フレーム呼んでも軽量）。
    ///
    /// バックグラウンドで終わった初回デコードの取り込みもここで行う。SE を鳴らさない
    /// フレームでもキャッシュが埋まるようにするため（＝取り込みが次の再生まで遅れない）。
    pub fn cleanup(&mut self) {
        self.collect_finished_decodes();
        self.se_sinks.retain(|voice| !voice.sink.empty());
        self.component_voices.retain(|_, v| !v.empty());
    }

    // ─── コンポーネント音源（AudioComponent）────────────────────

    /// コンポーネント音源を再生する（既に同スロットで再生中なら停止して置き換える）。
    ///
    /// SpatialSink を使用し、減衰・パンは毎フレームの update_component_voice で反映する。
    /// 再生開始時はエミッタを正面（中央）に置く。
    pub fn play_component(&mut self, slot: Entity, path: &str, volume: f32, looped: bool) {
        self.stop_component(slot);
        let Some(bytes) = self.load(path) else { return };
        let Ok(decoder) = Decoder::new(Cursor::new(bytes)) else {
            eprintln!("[Script] Audio: デコード失敗 ({path})");
            return;
        };
        // エミッタ正面（単位距離）・両耳 ±EAR_OFFSET で初期化する
        let Ok(sink) = SpatialSink::try_new(
            &self.handle,
            [0.0, 0.0, 1.0],
            [-EAR_OFFSET, 0.0, 0.0],
            [EAR_OFFSET, 0.0, 0.0],
        ) else {
            return;
        };
        sink.set_volume(volume.max(0.0));
        if looped {
            sink.append(decoder.repeat_infinite());
        } else {
            sink.append(decoder);
        }
        self.component_voices.insert(slot, sink);
        self.component_started.insert(slot);
    }

    /// コンポーネント音源を停止する。
    pub fn stop_component(&mut self, slot: Entity) {
        if let Some(voice) = self.component_voices.remove(&slot) {
            voice.stop();
        }
    }

    /// 指定スロットのコンポーネント音源が再生中か。
    pub fn is_component_playing(&self, slot: Entity) -> bool {
        self.component_voices.get(&slot).is_some_and(|v| !v.empty())
    }

    /// play_on_start をまだ発火していないスロットか
    /// （非ループ SE の再生終了後に再発火しないための判定）。
    pub fn component_needs_autostart(&self, slot: Entity) -> bool {
        !self.component_started.contains(&slot)
    }

    /// 実際には鳴らさずに「自動再生を発火済み」と記録する。
    ///
    /// 音源が解決できなかった（音声辞書のキーを引けない等）ときに呼ぶ。
    /// これを呼ばないと `component_needs_autostart` が毎フレーム true を返し続け、
    /// 警告が毎フレーム出る（＝ログが埋まって他の問題が見えなくなる）。
    pub fn mark_component_autostart_consumed(&mut self, slot: Entity) {
        self.component_started.insert(slot);
    }

    /// コンポーネント音源の減衰・パンを更新する（毎フレーム呼ばれる）。
    ///
    /// - direction: リスナーローカル空間での音源方向（正規化済み。x=右, y=上, z=前）
    /// - volume: 距離減衰適用済みの最終音量
    ///
    /// エミッタは単位球面上（direction）に置くため、rodio 側の距離減衰はほぼ一定になり
    /// 方向によるパンだけが効く。距離減衰は呼び出し側で計算して volume に織り込む。
    pub fn update_component_voice(&mut self, slot: Entity, direction: [f32; 3], volume: f32) {
        if let Some(voice) = self.component_voices.get_mut(&slot) {
            voice.set_emitter_position(direction);
            voice.set_volume(volume.max(0.0));
        }
    }

    /// 全コンポーネント音源を停止し、play_on_start の発火記録をクリアする。
    /// シーン遷移時に呼ばれる（新シーンの play_on_start を再発火させるため）。
    pub fn reset_components(&mut self) {
        for (_, voice) in self.component_voices.drain() {
            voice.stop();
        }
        self.component_started.clear();
    }

    /// 音声ファイルを読み込む（キャッシュ優先。assets:// 仮想パス・PAK 対応）。
    fn load(&mut self, path: &str) -> Option<SharedBytes> {
        if let Some(bytes) = self.cache.get(path) {
            return Some(bytes.clone());
        }
        match crate::engine::asset_fs::read_bytes(path) {
            Ok(data) => {
                let bytes = SharedBytes(Arc::new(data));
                self.cache.insert(path.to_string(), bytes.clone());
                Some(bytes)
            }
            Err(e) => {
                eprintln!("[Script] Audio: 読み込み失敗 ({path}): {e}");
                None
            }
        }
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 実測・検証に使う実在の SE（ビートバトルの回答クリック音）。
    /// `runtime/assets` は環境によっては別ドライブへのジャンクションなので、
    /// 無い環境ではテストを飛ばす（CI を落とさないため）。
    fn sample_se_path() -> Option<String> {
        let path = format!(
            "{}/assets/mainGame/audios/Motion-Swish07-1.mp3",
            env!("CARGO_MANIFEST_DIR")
        );
        std::path::Path::new(&path).is_file().then_some(path)
    }

    /// オーディオデバイスが無い環境（CI 等）ではデバイス依存テストを飛ばす。
    fn manager_or_skip() -> Option<AudioManager> {
        AudioManager::new()
    }

    /// 初回デコード（バックグラウンド）が終わるまで待って、その結果を返す。
    /// `None` は「キャッシュ対象外」。時間内に終わらなければテストを落とす。
    fn wait_pcm(audio: &mut AudioManager, path: &str) -> Option<Arc<DecodedPcm>> {
        const POLL_LIMIT: u32 = 200;
        const POLL_INTERVAL_MS: u64 = 50;

        audio.begin_background_decode(path);
        for _ in 0..POLL_LIMIT {
            audio.collect_finished_decodes();
            if let Some(entry) = audio.pcm_cache.get(path) {
                return entry.clone();
            }
            std::thread::sleep(Duration::from_millis(POLL_INTERVAL_MS));
        }
        panic!("初回デコードが終わらない: {path}");
    }

    /// PCM の再生時間が「サンプル数 ÷ (チャンネル数 × 周波数)」になること。
    #[test]
    fn decoded_pcm_duration_matches_sample_count() {
        // 2ch / 1000Hz で 2000 サンプル = 1000 フレーム = 1.0 秒
        let pcm = DecodedPcm::new(vec![0.0; 2000], 2, 1000);
        assert_eq!(pcm.duration, Duration::from_secs(1));
        assert_eq!(pcm.channels, 2);
        assert_eq!(pcm.sample_rate, 1000);
    }

    /// PcmSource が全サンプルを順に返し、最後まで来たら終わること。
    /// （終わらないと Sink が永久に空にならず、同時発音数が減らなくなる）
    #[test]
    fn pcm_source_plays_all_samples_then_ends() {
        let pcm = Arc::new(DecodedPcm::new(vec![0.1, 0.2, 0.3, 0.4], 2, 8000));
        let source = PcmSource::new(pcm.clone());

        assert_eq!(source.channels(), 2);
        assert_eq!(source.sample_rate(), 8000);
        assert_eq!(source.current_frame_len(), None);

        let played: Vec<f32> = PcmSource::new(pcm).collect();
        assert_eq!(played, vec![0.1, 0.2, 0.3, 0.4]);
    }

    /// 同じ PCM を共有しているかを Arc のポインタで見分けられること。
    #[test]
    fn se_voice_distinguishes_sounds_by_pcm_identity() {
        let a = Arc::new(DecodedPcm::new(vec![0.0; 4], 1, 8000));
        // 中身は同じでも実体が違えば「別の音」
        let b = Arc::new(DecodedPcm::new(vec![0.0; 4], 1, 8000));

        assert!(Arc::ptr_eq(&a, &a.clone()));
        assert!(!Arc::ptr_eq(&a, &b));
    }

    /// 同じ SE を何度も鳴らしても、デコードは 1 回だけで PCM が使い回されること
    /// 【連打時にメインスレッドが重くなる原因を潰した、という回帰テスト】。
    #[test]
    fn same_se_is_decoded_only_once() {
        let Some(path) = sample_se_path() else { return };
        let Some(mut audio) = manager_or_skip() else { return };

        let first = wait_pcm(&mut audio, &path).expect("SE をデコードできない");
        assert_eq!(audio.pcm_cache.len(), 1, "1 パスにつき 1 エントリのはず");

        // 2 回目以降は同じ実体（＝再デコードしていない）を返す
        for _ in 0..5 {
            audio.play_se(&path, 0.0);
            let again = audio
                .pcm_cache
                .get(&path)
                .cloned()
                .flatten()
                .expect("キャッシュから取れない");
            assert!(Arc::ptr_eq(&first, &again), "毎回デコードし直している");
        }
        assert_eq!(audio.pcm_cache.len(), 1);
        assert!(audio.pcm_decoding.is_empty(), "デコードスレッドが残っている");
        audio.se_sinks.clear();
    }

    /// 同じ SE を連打しても、同時に鳴る声が MAX_SE_VOICES_PER_SOUND を超えないこと
    /// 【同一波形の重ね合わせで歪む（バリバリ鳴る）のを防いだ、という回帰テスト】。
    #[test]
    fn same_se_voices_are_capped() {
        let Some(path) = sample_se_path() else { return };
        let Some(mut audio) = manager_or_skip() else { return };

        // 上限は「キャッシュ済み PCM の重なり」に効くので、先に初回デコードを済ませる
        let pcm = wait_pcm(&mut audio, &path).expect("SE をデコードできない");

        // 音量 0 で鳴らす（テスト実行中に実際の音を出さないため）
        for _ in 0..(MAX_SE_VOICES_PER_SOUND * 3) {
            audio.play_se(&path, 0.0);
        }

        let same = audio
            .se_sinks
            .iter()
            .filter(|v| v.is_same_sound(&pcm))
            .count();
        assert!(
            same <= MAX_SE_VOICES_PER_SOUND,
            "同一 SE が {same} 声も重なっている（上限 {MAX_SE_VOICES_PER_SOUND}）"
        );
        assert!(audio.se_sinks.len() <= MAX_SE_VOICES);

        // 後始末（テスト終了時に鳴りっぱなしにしない）
        audio.se_sinks.clear();
    }

    /// SE 1 回あたりのコストを実測する【性能改修の前後を数字で比べるための道具】。
    ///
    /// 通常のテスト実行では走らない。次のコマンドで明示的に実行する:
    /// `cargo test --bin SEED -- --ignored --nocapture se_play_cost`
    #[test]
    #[ignore]
    fn se_play_cost() {
        let Some(path) = sample_se_path() else {
            println!("[SE 計測] SE が見つからないため中止");
            return;
        };
        let Some(mut audio) = manager_or_skip() else {
            println!("[SE 計測] オーディオデバイスが無いため中止");
            return;
        };
        const N: u32 = 50;

        // 1) 旧方式相当: 毎回 Decoder::new する場合のメインスレッドコスト
        let bytes = audio.load(&path).expect("SE を読めない");
        let t = std::time::Instant::now();
        for _ in 0..N {
            let d = Decoder::new(Cursor::new(bytes.clone())).expect("decode");
            std::hint::black_box(&d);
        }
        let decoder_new_ms = t.elapsed().as_secs_f64() * 1000.0 / N as f64;

        // 2) 新方式: 初回デコード（1 回だけ・バックグラウンドスレッドで払う）
        let t = std::time::Instant::now();
        let pcm = wait_pcm(&mut audio, &path).expect("decode");
        let first_ms = t.elapsed().as_secs_f64() * 1000.0;

        // 3) 新方式: 2 回目以降の play_se（キャッシュ命中）
        let t = std::time::Instant::now();
        for _ in 0..N {
            audio.play_se(&path, 0.0);
        }
        let play_ms = t.elapsed().as_secs_f64() * 1000.0 / N as f64;
        audio.se_sinks.clear();

        println!("[SE 計測] 旧: Decoder::new のみ（メインスレッド） = {decoder_new_ms:.4} ms/回");
        println!(
            "[SE 計測] 新: 初回デコード（別スレッド・待ち時間込み） = {first_ms:.4} ms（{} サンプル / {}ch / {}Hz = {:.3} 秒）",
            pcm.samples.len(),
            pcm.channels,
            pcm.sample_rate,
            pcm.duration.as_secs_f32()
        );
        println!("[SE 計測] 新: 2 回目以降の play_se 全体 = {play_ms:.4} ms/回");
    }
}
