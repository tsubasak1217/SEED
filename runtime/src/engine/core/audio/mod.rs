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

use std::collections::HashMap;
use std::io::Cursor;
use std::sync::Arc;

use rodio::{Decoder, OutputStream, OutputStreamHandle, Sink, Source};

// ─── 生バイト列の共有ラッパー ────────────────────────────────

/// キャッシュ済み音声ファイルの生バイト列。
/// Cursor<T: AsRef<[u8]>> の要件を満たすため Arc<Vec<u8>> を包む。
#[derive(Clone)]
struct SharedBytes(Arc<Vec<u8>>);

impl AsRef<[u8]> for SharedBytes {
    fn as_ref(&self) -> &[u8] { &self.0 }
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
    /// 再生中の SE 群（finished は cleanup で回収する）
    se_sinks: Vec<Sink>,
    /// パス → 生バイト列のキャッシュ
    cache: HashMap<String, SharedBytes>,
}

impl AudioManager {
    /// 既定のオーディオデバイスで初期化する。デバイスが無い場合は None。
    pub fn new() -> Option<Self> {
        let (stream, handle) = OutputStream::try_default().ok()?;
        Some(Self {
            _stream:  stream,
            handle,
            bgm:      None,
            se_sinks: Vec::new(),
            cache:    HashMap::new(),
        })
    }

    /// 効果音を再生する（多重再生可）。volume は 1.0 = 等倍。
    pub fn play_se(&mut self, path: &str, volume: f32) {
        let Some(bytes) = self.load(path) else { return };
        let Ok(decoder) = Decoder::new(Cursor::new(bytes)) else {
            eprintln!("[Script] Audio: デコード失敗 ({path})");
            return;
        };
        let Ok(sink) = Sink::try_new(&self.handle) else { return };
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
        let Ok(sink) = Sink::try_new(&self.handle) else { return };
        sink.set_volume(volume.max(0.0));
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

    /// 再生し終えた SE の Sink を回収する（毎フレーム呼んでも軽量）。
    pub fn cleanup(&mut self) {
        self.se_sinks.retain(|s| !s.empty());
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
