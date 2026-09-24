// ============================================================
//  pipeline_cache/mod.rs — パイプラインキャッシュ（ドライバがコンパイルしたシェーダ）の生成と永続化
//
//  【何を速くするか】
//  GPU ドライバは create_render_pipeline / create_compute_pipeline のたびに SPIR-V を GPU の
//  機械語へコンパイルする。Android のドライバは自前のディスクキャッシュを持たないことが多く、
//  毎回の起動で全パイプラインを作り直していた（実機 Pixel 6a で起動 4.5 秒のうち約 3.4 秒。
//  docs/android.md §7）。wgpu の `Features::PIPELINE_CACHE`（Vulkan のみ）で作ったキャッシュを
//  全パイプラインの生成に渡し、その中身をファイルへ保存して次回起動で読み込む。
//
//  【構成】
//    mod.rs    … PersistentPipelineCache（生成・読み込み・保存。Renderer が 1 つ持つ）
//    path.rs   … 置き場とファイル名（純関数。アダプタごとに別ファイル）
//    shared.rs … 引数で渡せない生成箇所（遅延生成・DrawContext の外の描画器）向けの共有ハンドル
//
//  【いつ保存するか】（呼ぶのは Renderer::save_pipeline_cache）
//    - Android: バックグラウンドへ回るたび（app/background_lifecycle.rs の suspended）。Android は
//      アプリを閉じるとプロセスごと即終了する（MainActivity.onDestroy）ため、Renderer の Drop は走らない。
//    - デスクトップ: Renderer の Drop（アプリ終了時。従来どおり）。
//    内容が前回の読み込み・保存から変わっていなければ書かない（内容のハッシュで判定。suspended のたびに
//    数 MB を書き直さないため）。
//
//  【壊れたデータへの備え】
//    - 書き込みは `<名前>.bin.tmp` へ書いてから置き換える（書き込み中に落ちても半端なファイルを残さない）。
//    - 読み込んだデータは wgpu が検証する。版違い・破損（fallback=true で黙って空にされる）に加え、
//      別アダプタのデータ（fallback でも検証エラーになる）もエラースコープで捕まえて、空のキャッシュで
//      作り直す。キャッシュの不具合で起動を落とさない。
//
//  非対応の環境（Vulkan 以外・feature 無し）では None で、各パイプラインは従来どおりキャッシュ無しで作る。
// ============================================================

pub mod path;
pub mod shared;

use std::hash::{Hash, Hasher};
use std::path::Path;
use std::sync::Mutex;
use std::time::Instant;

use path::CacheFiles;

use crate::engine::asset_fs;
use crate::engine::platform;

/// ログの行頭（logcat / 起動ログで grep する印）。
const LOG_TAG: &str = "[SEED PIPELINE CACHE]";

/// wgpu のパイプラインキャッシュのラベル（検証エラーの表示用）。
const CACHE_LABEL: &str = "SEED Pipeline Cache";

/// ログに出すバイト数を KiB へ直す除数。
const BYTES_PER_KIB: f64 = 1024.0;

/// ログに出す経過時間をミリ秒へ直す係数。
const MILLIS_PER_SEC: f64 = 1000.0;

/// ディスクへ保存するパイプラインキャッシュ（Renderer が 1 つ持つ）。
pub struct PersistentPipelineCache {
    /// 全パイプラインの生成に渡すキャッシュ本体。
    cache: wgpu::PipelineCache,
    /// 保存先（置き場かアダプタのキーが決まらないときは None＝メモリ上だけで使う）。
    files: Option<CacheFiles>,
    /// ファイルと一致していることが分かっている内容のハッシュ（同じ内容の書き直しを省く）。
    ///
    /// 保存は suspended（メインスレッド）と Drop からしか呼ばれないが、`&self` のまま更新できるよう Mutex に入れる。
    synced_hash: Mutex<Option<u64>>,
}

/// 起動時にファイルから読んだデータ（どこから読んだかで扱いが変わる）。
enum LoadedData {
    /// 読むファイルが無かった（初回起動）。
    None,
    /// 今の名前（アダプタごと）のファイルから読んだ。
    Current(Vec<u8>),
    /// 旧ファイル名（pipeline_cache.bin）から読んだ。保存は必ず新しい名前へ行う。
    Legacy(Vec<u8>),
}

impl LoadedData {
    /// キャッシュの初期データとして渡すバイト列。
    fn bytes(&self) -> Option<&[u8]> {
        match self {
            LoadedData::None => None,
            LoadedData::Current(data) | LoadedData::Legacy(data) => Some(data),
        }
    }
}

impl PersistentPipelineCache {
    /// キャッシュを作る。保存済みのファイルがあれば読み込んで初期データにする。
    ///
    /// # 引数
    /// * `device`       - パイプラインを作るデバイス（キャッシュはこのデバイス専用）
    /// * `adapter_info` - アダプタ情報（ファイル名のキー＝ベンダー ID・デバイス ID に使う）
    /// * `supported`    - デバイスに `Features::PIPELINE_CACHE` を要求できたか
    ///
    /// # 戻り値
    /// 非対応なら None（呼び出し側はキャッシュ無しで従来どおり動く）。
    pub fn open(device: &wgpu::Device, adapter_info: &wgpu::AdapterInfo, supported: bool) -> Option<Self> {
        if !supported {
            eprintln!("{LOG_TAG} 非対応（Features::PIPELINE_CACHE 無し）: シェーダは起動のたびに作り直します");
            return None;
        }

        let exe_dir = std::env::current_exe()
            .ok()
            .and_then(|exe| exe.parent().map(Path::to_path_buf));
        let adapter_key = wgpu::util::pipeline_cache_key(adapter_info);
        let files = path::decide_cache_files(
            platform::paths::cache_dir(),
            asset_fs::is_packaged(),
            exe_dir.as_deref(),
            adapter_key.as_deref(),
        );

        let loaded = files.as_ref().map_or(LoadedData::None, read_saved_data);
        let cache = create_cache(device, loaded.bytes());

        // 採用後の中身（ドライバが受け付けた内容）。読み込んだのに小さくなっていれば弾かれている。
        let adopted = cache.get_data();
        log_open(files.as_ref(), &loaded, adopted.as_deref());

        // 今の名前のファイルから読んで採用された内容なら「ファイルと一致」として覚える
        // （新しいパイプラインが増えない限り、次の保存で同じ内容を書き直さない）。
        // 旧ファイル名から読んだときは、新しい名前へ必ず 1 回書かせるため覚えない。
        let synced_hash = match (&loaded, &adopted) {
            (LoadedData::Current(_), Some(data)) => Some(content_hash(data)),
            _ => None,
        };

        Some(Self {
            cache,
            files,
            synced_hash: Mutex::new(synced_hash),
        })
    }

    /// パイプラインの生成に渡すキャッシュ本体。
    pub fn cache(&self) -> &wgpu::PipelineCache {
        &self.cache
    }

    /// 今の中身をファイルへ保存する（前回の読み込み・保存から変わっていなければ書かない）。
    ///
    /// 失敗しても起動時間が伸びるだけで動作には影響しないため、結果はログ 1 行で済ませる。
    pub fn save(&self) {
        let Some(files) = &self.files else { return };
        let Some(data) = self.cache.get_data() else { return };

        let hash = content_hash(&data);
        let mut synced = self.synced_hash.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        if *synced == Some(hash) {
            eprintln!(
                "{LOG_TAG} 変化なし（{:.0} KiB）のため保存しません: {}",
                kib(data.len()),
                files.path.display()
            );
            return;
        }

        let started = Instant::now();
        match write_atomically(files, &data) {
            Ok(()) => {
                *synced = Some(hash);
                eprintln!(
                    "{LOG_TAG} 保存 {:.0} KiB（{:.1} ms）: {}",
                    kib(data.len()),
                    started.elapsed().as_secs_f64() * MILLIS_PER_SEC,
                    files.path.display()
                );
            }
            Err(err) => eprintln!("{LOG_TAG}[WARN] 保存に失敗しました（次回もシェーダを作り直します）: {} — {err}", files.path.display()),
        }
    }
}

/// 保存済みのデータを読む（今の名前 → 旧ファイル名の順。どちらも無ければ None）。
fn read_saved_data(files: &CacheFiles) -> LoadedData {
    if let Ok(data) = std::fs::read(&files.path) {
        return LoadedData::Current(data);
    }
    if let Ok(data) = std::fs::read(&files.legacy_path) {
        return LoadedData::Legacy(data);
    }
    LoadedData::None
}

/// キャッシュを作る。初期データが wgpu の検証で弾かれたら、空のキャッシュで作り直す。
///
/// wgpu は fallback=true でも「別アダプタのデータ」を検証エラーとして報告し、エラーハンドラが
/// 無いとその場で panic する（wgpu-core の `PipelineCacheValidationError::was_avoidable`）。
/// エラースコープで捕まえて、キャッシュの不具合で起動を落とさないようにする。
fn create_cache(device: &wgpu::Device, data: Option<&[u8]>) -> wgpu::PipelineCache {
    let Some(data) = data else {
        return create_raw(device, None);
    };
    device.push_error_scope(wgpu::ErrorFilter::Validation);
    let cache = create_raw(device, Some(data));
    match pollster::block_on(device.pop_error_scope()) {
        None => cache,
        Some(err) => {
            eprintln!("{LOG_TAG}[WARN] 保存済みのデータを使えません（空のキャッシュで作り直します）: {err}");
            create_raw(device, None)
        }
    }
}

/// wgpu のキャッシュを 1 つ作る。
fn create_raw(device: &wgpu::Device, data: Option<&[u8]>) -> wgpu::PipelineCache {
    // SAFETY: data はこのアプリ自身が同じ置き場へ書き出したものか None。wgpu がヘッダ（形式・ABI・
    // アダプタ・ドライバの検証キー）を確かめ、fallback=true なので版違い・破損は空のキャッシュになる
    // （別アダプタのデータは create_cache がエラースコープで捕まえる）。
    unsafe {
        device.create_pipeline_cache(&wgpu::PipelineCacheDescriptor {
            label: Some(CACHE_LABEL),
            data,
            fallback: true,
        })
    }
}

/// 一時ファイルへ書いてから置き換える（書き込み中に落ちても保存済みのファイルを壊さない）。
fn write_atomically(files: &CacheFiles, data: &[u8]) -> std::io::Result<()> {
    // 置き場はパッケージ化で作らない方針（空フォルダは zip で落ちる）なので、書く直前に作る。
    if let Some(dir) = files.path.parent() {
        std::fs::create_dir_all(dir)?;
    }
    let temp = files.temp_path();
    std::fs::write(&temp, data)?;
    // std の rename は Windows でも既存ファイルを置き換える（MoveFileEx + MOVEFILE_REPLACE_EXISTING）。
    std::fs::rename(&temp, &files.path)
}

/// 内容のハッシュ（同じ内容の書き直しを省く判定用。実行間で比べないので std の既定ハッシュでよい）。
fn content_hash(data: &[u8]) -> u64 {
    let mut hasher = std::collections::hash_map::DefaultHasher::new();
    data.hash(&mut hasher);
    hasher.finish()
}

/// バイト数を KiB へ（ログ用）。
fn kib(bytes: usize) -> f64 {
    bytes as f64 / BYTES_PER_KIB
}

/// 起動時の読み込み結果を 1 行ログへ出す（2 回目の起動で効いているかを確かめる印）。
fn log_open(files: Option<&CacheFiles>, loaded: &LoadedData, adopted: Option<&[u8]>) {
    let Some(files) = files else {
        eprintln!("{LOG_TAG} 保存先を決められないため、このプロセスの中だけで使います（ファイルへは保存しません）");
        return;
    };
    let adopted_kib = kib(adopted.map_or(0, <[u8]>::len));
    match loaded {
        LoadedData::None => eprintln!(
            "{LOG_TAG} 保存済みのファイル無し（初回）。シェーダを作り、バックグラウンド移行時・終了時に保存します: {}",
            files.path.display()
        ),
        LoadedData::Current(data) => eprintln!(
            "{LOG_TAG} 読込 {:.0} KiB → 採用後 {adopted_kib:.0} KiB: {}",
            kib(data.len()),
            files.path.display()
        ),
        LoadedData::Legacy(data) => eprintln!(
            "{LOG_TAG} 旧ファイル名から読込 {:.0} KiB → 採用後 {adopted_kib:.0} KiB: {}（保存は {}）",
            kib(data.len()),
            files.legacy_path.display(),
            files.path.display()
        ),
    }
}
