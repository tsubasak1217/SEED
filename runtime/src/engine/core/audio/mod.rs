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
//    - BGM は専用 Sink を 1 本保持し、切り替え時は前の BGM を停止する。
//    - SE は再生ごとに Sink を生成し、finished になったものは cleanup で回収する。
//    - オーディオデバイスが無い環境では new が None を返し、全操作が無音で無視される。
// ============================================================

use std::collections::{HashMap, HashSet};
use std::io::Cursor;
use std::sync::Arc;

use rodio::{Decoder, OutputStream, OutputStreamHandle, Sink, Source, SpatialSink};

use crate::engine::ecs::Entity;

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
    se_sinks: Vec<Sink>,
    /// 再生中のコンポーネント音源（Key = AudioComponent のスロットエンティティ）。
    /// SpatialSink を使い、毎フレーム update_component_voice で減衰・パンを更新する。
    component_voices: HashMap<Entity, SpatialSink>,
    /// play_on_start を一度発火させたスロットエンティティ
    /// （非ループ SE が鳴り終わった後に再発火しないための記録）。
    component_started: HashSet<Entity>,
    /// パス → 生バイト列のキャッシュ
    cache: HashMap<String, SharedBytes>,
}

impl AudioManager {
    /// 既定のオーディオデバイスで初期化する。デバイスが無い場合は None。
    pub fn new() -> Option<Self> {
        let (stream, handle) = OutputStream::try_default().ok()?;
        Some(Self {
            _stream: stream,
            handle,
            bgm: None,
            bgm_speed: BGM_SPEED_DEFAULT,
            se_sinks: Vec::new(),
            component_voices: HashMap::new(),
            component_started: HashSet::new(),
            cache: HashMap::new(),
        })
    }

    /// 効果音を再生する（多重再生可）。volume は 1.0 = 等倍。
    pub fn play_se(&mut self, path: &str, volume: f32) {
        let Some(bytes) = self.load(path) else { return };
        let Ok(decoder) = Decoder::new(Cursor::new(bytes)) else {
            eprintln!("[Script] Audio: デコード失敗 ({path})");
            return;
        };
        let Ok(sink) = Sink::try_new(&self.handle) else {
            return;
        };
        sink.set_volume(volume.max(0.0));
        sink.append(decoder);
        self.se_sinks.push(sink);
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
    pub fn cleanup(&mut self) {
        self.se_sinks.retain(|s| !s.empty());
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
