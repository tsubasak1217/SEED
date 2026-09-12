// ============================================================
//  screenshot.rs — 最終提示カラーターゲットの PNG 書き出し（常設デバッグフック）
// ------------------------------------------------------------
//  目的:
//    レンダリング結果を「実際に GPU が提示したフレームバッファそのまま」で
//    ファイルへ落とす。外部のウィンドウキャプチャと違い、ウィンドウの重なり・
//    DPI スケール・カラーマネジメントの影響を受けないため、
//    描画機能の目視検証（例: 草の描画確認、風アニメーションの差分検証）に使える。
//
//  有効化（両方が指定されたときのみ動作。既定では完全に無効）:
//    SEED_SCREENSHOT_DIR    … 出力先ディレクトリ（存在しなければ作成する）
//    SEED_SCREENSHOT_FRAMES … 撮影するフレーム番号のカンマ区切り（例 "120,240"）
//
//  フレーム番号の定義:
//    **present したフレーム数**の 0 起点通し番号（提示フレームと 1 対 1 に対応する）。
//    アプリ側のフレームカウンタとは独立している。
//    サムネイル撮影フレームはオフスクリーンへ描いて present しないため、
//    番号を消費しない（`RenderFrame::finish()` の呼び出し回数とは一致しない）。
//
//  wgpu 上の制約と対処:
//    - `copy_texture_to_buffer` の `bytes_per_row` は COPY_BYTES_PER_ROW_ALIGNMENT
//      (256) の倍数でなければならない。行ごとにパディングしてコピーし、
//      PNG 書き出し時にパディングを取り除く（アンパディング）。
//    - 読み出しは `map_async` 後に `device.poll(PollType::Wait)` で完了を待つ。
//    - サーフェスのフォーマットは環境により Bgra8 / Rgba8 が変わるため、
//      B が先頭のフォーマットのときだけチャンネルを入れ替えて RGBA へ揃える。
//    - sRGB フォーマット（*UnormSrgb）はテクセルが既に sRGB エンコード済みの
//      バイト列であり、PNG が期待する表現と一致するため追加変換は行わない。
//
//  注意:
//    サーフェスから読み戻すには `TextureUsages::COPY_SRC` が必要。
//    ヘッドレス運用（IPC 駆動スクリーンショット）では起動時に撮る/撮らないが
//    決まらないため、`Renderer::new` の SurfaceConfiguration で常時付与している。
//
//  このモジュールは 2 系統の入口を持つ:
//    1. 環境変数駆動のフレームダンプ（`schedule` / `resolve`）— 上記の常設デバッグフック。
//    2. IPC 駆動の単発撮影（`request_capture` / `schedule_requested` / `resolve_requested`）
//       — エディタ（MCP 経由の AI）からの `SCREENSHOT:` に応答する。ファイル後半参照。
// ============================================================

use std::path::PathBuf;
use std::sync::LazyLock;
use std::sync::atomic::{AtomicU64, Ordering};

/// 出力先ディレクトリを指定する環境変数名。
const ENV_DIR: &str = "SEED_SCREENSHOT_DIR";
/// 撮影フレーム番号（カンマ区切り）を指定する環境変数名。
const ENV_FRAMES: &str = "SEED_SCREENSHOT_FRAMES";
/// 撮影フレーム番号の区切り文字。
const FRAME_LIST_SEPARATOR: char = ',';
/// RGBA8 / BGRA8 の 1 画素あたりバイト数。
const BYTES_PER_PIXEL: u32 = 4;
/// 出力ファイル名の書式（`{}` にフレーム番号が入る）。
const FILE_NAME_PREFIX: &str = "frame_";
/// 出力ファイル名の拡張子。
const FILE_NAME_EXT: &str = ".png";

/// 環境変数から読んだキャプチャ設定（プロセス起動時に 1 度だけ解決する）。
struct ScreenshotConfig {
    /// PNG の出力先ディレクトリ。
    dir: PathBuf,
    /// 撮影対象のフレーム番号（昇順・重複除去済み）。
    frames: Vec<u64>,
}

/// キャプチャ設定。環境変数が揃っていなければ `None`（＝機能まるごと無効）。
static CONFIG: LazyLock<Option<ScreenshotConfig>> = LazyLock::new(|| {
    let dir = std::env::var(ENV_DIR).ok()?;
    let frames_raw = std::env::var(ENV_FRAMES).ok()?;

    // "120, 240" のような空白混じりも受け付ける。数値として読めない要素は捨てる。
    let mut frames: Vec<u64> = frames_raw
        .split(FRAME_LIST_SEPARATOR)
        .filter_map(|s| s.trim().parse::<u64>().ok())
        .collect();
    frames.sort_unstable();
    frames.dedup();
    if frames.is_empty() {
        eprintln!(
            "[SEED screenshot] {ENV_FRAMES}=\"{frames_raw}\" にフレーム番号が 1 つも無いため無効化します"
        );
        return None;
    }

    let dir = PathBuf::from(dir);
    if let Err(e) = std::fs::create_dir_all(&dir) {
        eprintln!(
            "[SEED screenshot] 出力先 {} を作成できません: {e}",
            dir.display()
        );
        return None;
    }

    eprintln!(
        "[SEED screenshot] 有効化: dir={} frames={:?}",
        dir.display(),
        frames
    );
    Some(ScreenshotConfig { dir, frames })
});

/// present 済みフレーム数のカウンタ（`next_frame_index` が 1 つずつ払い出す）。
static FRAME_COUNTER: AtomicU64 = AtomicU64::new(0);

/// これから present するフレームの通し番号を払い出す（0 起点）。
///
/// `RenderFrame::finish()` から 1 フレームにつき 1 回だけ呼ぶこと。
pub fn next_frame_index() -> u64 {
    FRAME_COUNTER.fetch_add(1, Ordering::Relaxed)
}

/// 指定フレームが撮影対象かどうか。
fn should_capture(frame_index: u64) -> bool {
    CONFIG
        .as_ref()
        .is_some_and(|c| c.frames.binary_search(&frame_index).is_ok())
}

/// GPU サブミット後に読み出すための、コピー先バッファと復元情報。
pub struct PendingCapture {
    /// テクスチャからコピーされた（行パディング付きの）読み戻しバッファ。
    buffer: wgpu::Buffer,
    /// 画像の幅（画素）。
    width: u32,
    /// 画像の高さ（画素）。
    height: u32,
    /// アラインメント調整済みの 1 行あたりバイト数（`width * 4` 以上）。
    padded_bytes_per_row: u32,
    /// コピー元テクスチャのフォーマット（チャンネル順の判定に使う）。
    format: wgpu::TextureFormat,
    /// 書き出し先のフルパス。
    path: PathBuf,
}

/// `value` を `alignment` の倍数へ切り上げる（アラインメントは 2 の冪を想定）。
fn align_up(value: u32, alignment: u32) -> u32 {
    value.div_ceil(alignment) * alignment
}

/// 撮影対象フレームなら、テクスチャ → バッファのコピーコマンドを `encoder` へ積む。
///
/// 実際の読み出しは GPU サブミット後に [`resolve`] で行う。
/// 撮影対象でない、または機能無効なら `None` を返し何もしない。
pub fn schedule(
    device: &wgpu::Device,
    encoder: &mut wgpu::CommandEncoder,
    texture: &wgpu::Texture,
    frame_index: u64,
) -> Option<PendingCapture> {
    if !should_capture(frame_index) {
        return None;
    }
    let config = CONFIG.as_ref()?;

    let path = config
        .dir
        .join(format!("{FILE_NAME_PREFIX}{frame_index}{FILE_NAME_EXT}"));
    Some(begin_copy(device, encoder, texture, path))
}

/// テクスチャ → 読み戻しバッファのコピーコマンドを `encoder` へ積み、
/// 読み出しに必要な情報を [`PendingCapture`] にまとめて返す。
///
/// 環境変数駆動のフレームダンプと IPC 駆動のスクリーンショットで共通の処理。
/// GPU 実行は呼び出し元の submit 時なので、ここでは実際の画素はまだ読めない。
fn begin_copy(
    device: &wgpu::Device,
    encoder: &mut wgpu::CommandEncoder,
    texture: &wgpu::Texture,
    path: PathBuf,
) -> PendingCapture {
    let size = texture.size();
    let (width, height) = (size.width, size.height);
    // copy_texture_to_buffer の bytes_per_row は 256 バイト境界に揃える必要がある。
    let padded_bytes_per_row =
        align_up(width * BYTES_PER_PIXEL, wgpu::COPY_BYTES_PER_ROW_ALIGNMENT);
    let buffer_size = u64::from(padded_bytes_per_row) * u64::from(height);

    let buffer = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("Screenshot Readback"),
        size: buffer_size,
        usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        mapped_at_creation: false,
    });

    encoder.copy_texture_to_buffer(
        wgpu::ImageCopyTexture {
            texture,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        wgpu::ImageCopyBuffer {
            buffer: &buffer,
            layout: wgpu::ImageDataLayout {
                offset: 0,
                bytes_per_row: Some(padded_bytes_per_row),
                rows_per_image: Some(height),
            },
        },
        wgpu::Extent3d {
            width,
            height,
            depth_or_array_layers: 1,
        },
    );

    PendingCapture {
        buffer,
        width,
        height,
        padded_bytes_per_row,
        format: texture.format(),
        path,
    }
}

/// このフォーマットが BGRA 並び（B が先頭）かどうか。
///
/// PNG は RGBA 並びを要求するため、true のときだけ R と B を入れ替える。
fn is_bgra(format: wgpu::TextureFormat) -> bool {
    matches!(
        format,
        wgpu::TextureFormat::Bgra8Unorm | wgpu::TextureFormat::Bgra8UnormSrgb
    )
}

/// GPU サブミット後にバッファを読み出し、パディングを外して PNG を書き出す。
///
/// 同期的にマップ完了を待つ（`PollType::Wait`）。デバッグ用途のみに使うこと。
pub fn resolve(device: &wgpu::Device, pending: PendingCapture) {
    match resolve_to_file(device, pending) {
        Ok((path, width, height)) => eprintln!(
            "[SEED screenshot] 書き出し: {} ({width}x{height})",
            path.display()
        ),
        Err(e) => eprintln!("[SEED screenshot] {e}"),
    }
}

/// 読み戻し → アンパディング → PNG 書き出しまでを行い、`(パス, 幅, 高さ)` を返す。
///
/// 失敗時は人が読めるエラーメッセージ（日本語）を返す。IPC 応答にもそのまま載せる。
fn resolve_to_file(
    device: &wgpu::Device,
    pending: PendingCapture,
) -> Result<(PathBuf, u32, u32), String> {
    let (width, height) = (pending.width, pending.height);
    let path = pending.path.clone();
    let pixels = read_back_pixels(device, &pending)?;

    let img = image::RgbaImage::from_raw(width, height, pixels)
        .ok_or_else(|| "画素バッファのサイズが画像サイズと一致しません".to_string())?;
    // 出力先ディレクトリが未作成でも書けるようにしておく（IPC 指定パス対策）。
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)
            .map_err(|e| format!("出力先 {} を作成できません: {e}", parent.display()))?;
    }
    img.save(&path)
        .map_err(|e| format!("PNG 書き出しに失敗 {}: {e}", path.display()))?;
    Ok((path, width, height))
}

/// マップ完了を待ってバッファを読み出し、行パディングを外した RGBA8 画素列を返す。
///
/// - チャンネル順: コピー元が BGRA 系なら R と B を入れ替えて RGBA へ揃える。
/// - アルファ: 提示画像は不透明として扱う（下記 `ALPHA_OPAQUE` 参照）。
fn read_back_pixels(
    device: &wgpu::Device,
    pending: &PendingCapture,
) -> Result<Vec<u8>, String> {
    let slice = pending.buffer.slice(..);
    // map_async はコールバック方式なので、完了フラグをチャネルで受け取る。
    let (sender, receiver) = std::sync::mpsc::channel();
    slice.map_async(wgpu::MapMode::Read, move |result| {
        // 受信側が消えていても無視してよい（ここでの失敗は下の recv で検知する）。
        let _ = sender.send(result);
    });
    // マップ完了までコマンドキューを回す。
    let _ = device.poll(wgpu::PollType::Wait);

    match receiver.recv() {
        Ok(Ok(())) => {}
        Ok(Err(e)) => return Err(format!("バッファのマップに失敗: {e}")),
        Err(e) => return Err(format!("マップ完了通知の受信に失敗: {e}")),
    }

    // 行パディングを外しつつ、必要ならチャンネル順を RGBA へ揃える。
    let mapped = slice.get_mapped_range();
    let pixels = unpad_to_rgba(
        &mapped,
        pending.width,
        pending.height,
        pending.padded_bytes_per_row,
        is_bgra(pending.format),
    );
    drop(mapped);
    pending.buffer.unmap();
    Ok(pixels)
}

/// 行パディング付きの生バイト列を、詰めた RGBA8 画素列へ変換する（純粋関数）。
///
/// - `padded_bytes_per_row`: GPU コピー時の 256 バイト境界に揃えた 1 行バイト数。
/// - `swap_rb`: コピー元が BGRA 並びのとき true（R と B を入れ替える）。
/// - アルファはサーフェスの内容次第で 0 になり得るが、提示画像は不透明として扱う。
///   透明 PNG になってビューアで真っ白/市松に見えるのを防ぐため、不透明で固定する。
fn unpad_to_rgba(
    mapped: &[u8],
    width: u32,
    height: u32,
    padded_bytes_per_row: u32,
    swap_rb: bool,
) -> Vec<u8> {
    let unpadded_bytes_per_row = (width * BYTES_PER_PIXEL) as usize;
    let mut pixels = Vec::with_capacity(unpadded_bytes_per_row * height as usize);
    for row in 0..height as usize {
        let start = row * padded_bytes_per_row as usize;
        let row_bytes = &mapped[start..start + unpadded_bytes_per_row];
        if swap_rb {
            for px in row_bytes.chunks_exact(BYTES_PER_PIXEL as usize) {
                // BGRA → RGBA
                pixels.extend_from_slice(&[px[2], px[1], px[0], px[3]]);
            }
        } else {
            pixels.extend_from_slice(row_bytes);
        }
    }
    for px in pixels.chunks_exact_mut(BYTES_PER_PIXEL as usize) {
        px[ALPHA_CHANNEL_INDEX] = ALPHA_OPAQUE;
    }
    pixels
}

// ============================================================
//  IPC 駆動のスクリーンショット（SCREENSHOT:<target>,<abs_path>）
// ------------------------------------------------------------
//  環境変数駆動のフレームダンプと違い、任意のタイミングで 1 枚だけ撮る。
//  エディタ（AI）からの要求 → 次に描いたフレームの提示テクスチャを読み戻す。
//
//  スレッド設計:
//    要求の投入（IPC ハンドラ）と消化（RenderFrame::finish）は同じスレッドだが、
//    将来の別スレッド化に耐えるようグローバルな Mutex キューで受け渡す。
//    App 構造体に持たせないのは、コピーを積む `finish()` が App を参照できないため。
// ============================================================

/// 撮影対象の指定として受け付ける文字列（すべて「提示中のカラーターゲット」を指す）。
///
/// Play 中はゲーム画面、Edit 中はシーンビューがそのままスワップチェーンに出ているため、
/// ランタイム側では両者を区別しない。`editor`（エディタ全体）はランタイムでは撮れない。
const ACCEPTED_TARGETS: [&str; 3] = ["game", "viewport", "surface"];

/// アルファ値を不透明で固定するときの値。
const ALPHA_OPAQUE: u8 = 255;
/// RGBA8 におけるアルファチャンネルのバイト位置。
const ALPHA_CHANNEL_INDEX: usize = 3;

/// IPC で受け取った 1 件の撮影要求。
struct CaptureRequest {
    /// 書き出し先の絶対パス。
    path: PathBuf,
}

/// 撮影の結果（IPC 応答の材料）。
pub enum CaptureOutcome {
    /// 撮影成功。書き出したパスと画像サイズ。
    Done { path: PathBuf, width: u32, height: u32 },
    /// 撮影失敗。人が読めるエラーメッセージ。
    Error { message: String },
}

/// 未処理の撮影要求キュー（IPC ハンドラが積み、フレーム末尾が 1 件ずつ消化する）。
static REQUESTS: std::sync::Mutex<Vec<CaptureRequest>> = std::sync::Mutex::new(Vec::new());
/// 撮影結果キュー（フレーム末尾が積み、IPC ハンドラが取り出して応答を送る）。
static OUTCOMES: std::sync::Mutex<Vec<CaptureOutcome>> = std::sync::Mutex::new(Vec::new());

/// 撮影要求を受け付ける（IPC `SCREENSHOT:<target>,<abs_path>` の実体）。
///
/// 対象名・パスの妥当性はここで検証し、駄目なら即座に失敗結果を積む
/// （＝ 呼び出し元は成否に関わらず「結果キューを見る」だけでよい）。
pub fn request_capture(target: &str, path: &str) {
    let target_norm = target.trim().to_ascii_lowercase();
    if !ACCEPTED_TARGETS.contains(&target_norm.as_str()) {
        push_outcome(CaptureOutcome::Error {
            message: format!(
                "未対応の target です: {target}（対応: {}）",
                ACCEPTED_TARGETS.join(" / ")
            ),
        });
        return;
    }
    let path = path.trim();
    if path.is_empty() {
        push_outcome(CaptureOutcome::Error {
            message: "出力パスが空です".to_string(),
        });
        return;
    }
    if let Ok(mut q) = REQUESTS.lock() {
        q.push(CaptureRequest { path: PathBuf::from(path) });
    }
}

/// 未処理の撮影要求が残っているか（フレーム強制描画の判定に使う）。
pub fn has_pending_request() -> bool {
    REQUESTS.lock().is_ok_and(|q| !q.is_empty())
}

/// 撮影結果をすべて取り出す（取り出した分はキューから消える）。
pub fn take_outcomes() -> Vec<CaptureOutcome> {
    match OUTCOMES.lock() {
        Ok(mut q) => std::mem::take(&mut *q),
        Err(_) => Vec::new(),
    }
}

/// 未処理の撮影要求をすべて失敗として打ち切る。
///
/// 最小化（0x0）などで「これから先もフレームを描けない」と分かったときに呼ぶ。
/// 放置すると要求が残り続け、強制フレームポンプが空回りして応答が返らない。
pub fn fail_pending_requests(message: &str) {
    let dropped = match REQUESTS.lock() {
        Ok(mut q) => std::mem::take(&mut *q).len(),
        Err(_) => 0,
    };
    for _ in 0..dropped {
        push_outcome(CaptureOutcome::Error { message: message.to_string() });
    }
}

/// 撮影結果を 1 件積む。
fn push_outcome(outcome: CaptureOutcome) {
    if let Ok(mut q) = OUTCOMES.lock() {
        q.push(outcome);
    }
}

/// 撮影要求が溜まっていれば、先頭 1 件ぶんのコピーコマンドを `encoder` へ積む。
///
/// 1 フレームにつき 1 枚だけ処理する（同一フレームを何枚も撮っても意味がないため）。
/// 残りは次フレーム以降で消化される。
pub fn schedule_requested(
    device: &wgpu::Device,
    encoder: &mut wgpu::CommandEncoder,
    texture: &wgpu::Texture,
) -> Option<PendingCapture> {
    let request = {
        let mut q = REQUESTS.lock().ok()?;
        if q.is_empty() {
            return None;
        }
        q.remove(0)
    };
    Some(begin_copy(device, encoder, texture, request.path))
}

/// GPU サブミット後に読み出して PNG を書き出し、結果をキューへ積む。
pub fn resolve_requested(device: &wgpu::Device, pending: PendingCapture) {
    match resolve_to_file(device, pending) {
        Ok((path, width, height)) => {
            eprintln!(
                "[SEED screenshot] IPC 撮影: {} ({width}x{height})",
                path.display()
            );
            push_outcome(CaptureOutcome::Done { path, width, height });
        }
        Err(message) => {
            eprintln!("[SEED screenshot] IPC 撮影に失敗: {message}");
            push_outcome(CaptureOutcome::Error { message });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 行アラインメントの切り上げが 256 の倍数を返すこと。
    #[test]
    fn align_up_rounds_to_multiple() {
        const ALIGN: u32 = wgpu::COPY_BYTES_PER_ROW_ALIGNMENT;
        assert_eq!(align_up(0, ALIGN), 0);
        assert_eq!(align_up(1, ALIGN), ALIGN);
        assert_eq!(align_up(ALIGN, ALIGN), ALIGN);
        assert_eq!(align_up(ALIGN + 1, ALIGN), ALIGN * 2);
        // 1920 幅 RGBA = 7680 バイトは既に 256 の倍数なのでそのまま。
        assert_eq!(align_up(1920 * BYTES_PER_PIXEL, ALIGN), 7680);
        // 1000 幅 RGBA = 4000 バイト → 4096 へ切り上げ。
        assert_eq!(align_up(1000 * BYTES_PER_PIXEL, ALIGN), 4096);
    }

    /// 不正な撮影要求（未対応 target / 空パス）が要求キューに積まれず、
    /// 失敗結果として取り出せること。
    ///
    /// グローバルキューを共有するため、他テストと分離せず 1 本にまとめている。
    #[test]
    fn request_capture_rejects_invalid_arguments() {
        // 先に溜まっているものを捨ててから検証する。
        let _ = take_outcomes();

        request_capture("editor", "C:/tmp/a.png"); // ランタイムでは撮れない対象
        request_capture("game", "   ");            // パスが空
        assert!(!has_pending_request(), "不正な要求が撮影キューへ積まれてはならない");

        let outcomes = take_outcomes();
        assert_eq!(outcomes.len(), 2);
        assert!(outcomes
            .iter()
            .all(|o| matches!(o, CaptureOutcome::Error { .. })));

        // 正しい要求は積まれること（積んだら消しておく）。
        request_capture("game", "C:/tmp/ok.png");
        assert!(has_pending_request());
        REQUESTS.lock().unwrap().clear();
    }

    /// 行パディング付き BGRA バッファが RGBA へ正しく詰め直され、
    /// PNG としてエンコード・デコードできること。
    #[test]
    fn unpad_swizzle_and_png_roundtrip() {
        // 2x2 の BGRA 画像。行あたり 8 バイトだが 256 バイトにパディングされている想定。
        const W: u32 = 2;
        const H: u32 = 2;
        const PADDED_BPR: u32 = wgpu::COPY_BYTES_PER_ROW_ALIGNMENT; // 256
        // 画素（RGBA で意図する色）: 赤・緑 / 青・白
        let expect: [[u8; 4]; 4] = [
            [255, 0, 0, 255],
            [0, 255, 0, 255],
            [0, 0, 255, 255],
            [255, 255, 255, 255],
        ];
        let mut mapped = vec![0u8; (PADDED_BPR * H) as usize];
        for row in 0..H as usize {
            for col in 0..W as usize {
                let rgba = expect[row * W as usize + col];
                let off = row * PADDED_BPR as usize + col * BYTES_PER_PIXEL as usize;
                // BGRA 並びで書き込む（アルファは 0 にして不透明固定も検証する）。
                mapped[off] = rgba[2];
                mapped[off + 1] = rgba[1];
                mapped[off + 2] = rgba[0];
                mapped[off + 3] = 0;
            }
        }

        let pixels = unpad_to_rgba(&mapped, W, H, PADDED_BPR, true);
        assert_eq!(pixels.len(), (W * H * BYTES_PER_PIXEL) as usize);
        for (i, chunk) in pixels.chunks_exact(BYTES_PER_PIXEL as usize).enumerate() {
            assert_eq!(chunk, &expect[i], "画素 {i} のチャンネル順が一致しない");
        }

        // PNG として書き出して読み直せること（image クレートの png feature の疎通確認）。
        let img = image::RgbaImage::from_raw(W, H, pixels).expect("画素数が一致すること");
        let path = std::env::temp_dir().join("seed_screenshot_roundtrip_test.png");
        img.save(&path).expect("PNG 書き出しに成功すること");
        let decoded = image::open(&path).expect("PNG 読み込みに成功すること").to_rgba8();
        assert_eq!(decoded.dimensions(), (W, H));
        assert_eq!(decoded.get_pixel(0, 0).0, expect[0]);
        assert_eq!(decoded.get_pixel(1, 1).0, expect[3]);
        let _ = std::fs::remove_file(&path);
    }

    /// BGRA 系フォーマットだけがチャンネル入れ替え対象になること。
    #[test]
    fn bgra_detection() {
        assert!(is_bgra(wgpu::TextureFormat::Bgra8Unorm));
        assert!(is_bgra(wgpu::TextureFormat::Bgra8UnormSrgb));
        assert!(!is_bgra(wgpu::TextureFormat::Rgba8Unorm));
        assert!(!is_bgra(wgpu::TextureFormat::Rgba8UnormSrgb));
    }
}

// ============================================================
//  サムネイル用のカラー読み戻し（図鑑画像・モデル一覧のタイル画像）
// ------------------------------------------------------------
//  上の `SCREENSHOT:` レーンとの違いは 2 点:
//    1. **PNG を書かず、生の RGBA ピクセルを返す**。
//    2. コピー元が**提示テクスチャではない**。撮影フレームは専用の
//       オフスクリーンターゲットへ描かれる（`renderer/thumbnail/target.rs`）ので、
//       このレーンはそのターゲットから読み戻す。上の 2 レーンは提示フレーム専用で、
//       撮影フレームでは `RenderFrame::finish` がそもそも呼ばない。
//
//  なぜ分けたか:
//    サムネイルはカラーだけでは完成しない。ID バッファから作ったマスクでアルファを
//    抜き、アルファブリードを掛け、正方形に切り出して縮小してから初めて PNG になる。
//    途中経過を一度 PNG にして読み直すのは無駄なうえ、`SCREENSHOT_DONE:` 応答が
//    エディタへ飛んでしまい（撮っていないはずのスクリーンショットが記録される）
//    プロトコルが濁る。そのため専用レーンを設けた。
//
//  同時実行しない前提:
//    サムネイル生成は 1 枚ずつ逐次進む状態機械（app/thumbnail_ops.rs）が駆動するので、
//    要求も結果も常に高々 1 件。キューではなく Option で持つ。
// ============================================================

/// サムネイル用カラー読み戻しの要求フラグ（true = 次のフレームで 1 枚読み戻す）。
static THUMBNAIL_REQUESTED: std::sync::Mutex<bool> = std::sync::Mutex::new(false);

/// サムネイル用カラー読み戻しの結果（`(RGBA ピクセル, 幅, 高さ)` または失敗メッセージ）。
static THUMBNAIL_RESULT: std::sync::Mutex<Option<Result<(Vec<u8>, u32, u32), String>>> =
    std::sync::Mutex::new(None);

/// 次に描くフレームのカラーターゲットを、サムネイル用に読み戻すよう要求する。
///
/// 撮影フレームのカラーターゲットは専用オフスクリーン（提示テクスチャではない）。
pub fn request_thumbnail_color() {
    if let Ok(mut flag) = THUMBNAIL_REQUESTED.lock() {
        *flag = true;
    }
    if let Ok(mut result) = THUMBNAIL_RESULT.lock() {
        // 前回の残骸を必ず捨てる（古い絵をそのまま採用してしまう事故を防ぐ）
        *result = None;
    }
}

/// 要求が立っていれば、`texture`（そのフレームのカラーターゲット）→ 読み戻しバッファの
/// コピーを `encoder` へ積む。
///
/// `RenderFrame::finish()` から呼ぶ。撮影フレームではオフスクリーンターゲットが渡る。
pub fn schedule_thumbnail_color(
    device: &wgpu::Device,
    encoder: &mut wgpu::CommandEncoder,
    texture: &wgpu::Texture,
) -> Option<PendingCapture> {
    {
        let mut flag = THUMBNAIL_REQUESTED.lock().ok()?;
        if !*flag {
            return None;
        }
        // 1 フレームで消費する（多重に積まない）
        *flag = false;
    }
    // 出力先パスは使わないが PendingCapture が要求するのでダミーを入れる
    Some(begin_copy(device, encoder, texture, PathBuf::new()))
}

/// GPU サブミット後にピクセルを読み出して結果へ積む（PNG は書かない）。
pub fn resolve_thumbnail_color(device: &wgpu::Device, pending: PendingCapture) {
    let outcome = match read_back_pixels(device, &pending) {
        Ok(pixels) => Ok((pixels, pending.width, pending.height)),
        Err(message) => Err(message),
    };
    // 【必須】読み戻しバッファを明示的に解放する。Drop 任せだと解放が遅延し、
    // サムネイルの連続生成でフレームバッファ数枚ぶんが積み上がる。
    // 同じ理由で ID バッファ側も frame_renderer で destroy している。
    pending.buffer.destroy();
    if let Ok(mut slot) = THUMBNAIL_RESULT.lock() {
        *slot = Some(outcome);
    }
}

/// 読み戻し結果を取り出す（取り出した分は消える）。まだ来ていなければ None。
pub fn take_thumbnail_color_result() -> Option<Result<(Vec<u8>, u32, u32), String>> {
    THUMBNAIL_RESULT.lock().ok().and_then(|mut slot| slot.take())
}

/// 未処理のサムネイル要求・結果を捨てる（ジョブ中断・失敗時の後始末）。
pub fn clear_thumbnail_color() {
    if let Ok(mut flag) = THUMBNAIL_REQUESTED.lock() {
        *flag = false;
    }
    if let Ok(mut slot) = THUMBNAIL_RESULT.lock() {
        *slot = None;
    }
}
