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
//    `RenderFrame::finish()` が呼ばれた回数（= present したフレーム数）の 0 起点通し番号。
//    アプリ側のフレームカウンタとは独立しており、提示フレームと 1 対 1 に対応する。
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
//    サーフェス構成側で、この機能が有効なときだけ COPY_SRC を足している
//    （`Renderer::new` の SurfaceConfiguration を参照）。
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

/// キャプチャ機能が有効かどうか（サーフェスの COPY_SRC 付与判定にも使う）。
pub fn is_enabled() -> bool {
    CONFIG.is_some()
}

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

    let path = config
        .dir
        .join(format!("{FILE_NAME_PREFIX}{frame_index}{FILE_NAME_EXT}"));

    Some(PendingCapture {
        buffer,
        width,
        height,
        padded_bytes_per_row,
        format: texture.format(),
        path,
    })
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
    let slice = pending.buffer.slice(..);
    // map_async はコールバック方式なので、完了フラグをチャネル無しで受け取る。
    let (sender, receiver) = std::sync::mpsc::channel();
    slice.map_async(wgpu::MapMode::Read, move |result| {
        // 受信側が消えていても無視してよい（ここでの失敗は下の recv で検知する）。
        let _ = sender.send(result);
    });
    // マップ完了までコマンドキューを回す。
    let _ = device.poll(wgpu::PollType::Wait);

    match receiver.recv() {
        Ok(Ok(())) => {}
        Ok(Err(e)) => {
            eprintln!("[SEED screenshot] バッファのマップに失敗: {e}");
            return;
        }
        Err(e) => {
            eprintln!("[SEED screenshot] マップ完了通知の受信に失敗: {e}");
            return;
        }
    }

    // 行パディングを外しつつ、必要ならチャンネル順を RGBA へ揃える。
    let mapped = slice.get_mapped_range();
    let swap_rb = is_bgra(pending.format);
    let unpadded_bytes_per_row = (pending.width * BYTES_PER_PIXEL) as usize;
    let mut pixels = Vec::with_capacity(unpadded_bytes_per_row * pending.height as usize);
    for row in 0..pending.height as usize {
        let start = row * pending.padded_bytes_per_row as usize;
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
    drop(mapped);
    pending.buffer.unmap();

    // アルファはサーフェスの内容次第で 0 になり得るが、提示画像は不透明として扱う。
    // 透明 PNG になってビューアで真っ白/市松に見えるのを防ぐため、不透明で固定する。
    const ALPHA_OPAQUE: u8 = 255;
    for px in pixels.chunks_exact_mut(BYTES_PER_PIXEL as usize) {
        px[3] = ALPHA_OPAQUE;
    }

    let Some(img) = image::RgbaImage::from_raw(pending.width, pending.height, pixels) else {
        eprintln!("[SEED screenshot] 画素バッファのサイズが画像サイズと一致しません");
        return;
    };
    match img.save(&pending.path) {
        Ok(()) => eprintln!(
            "[SEED screenshot] 書き出し: {} ({}x{}, {:?})",
            pending.path.display(),
            pending.width,
            pending.height,
            pending.format
        ),
        Err(e) => eprintln!(
            "[SEED screenshot] PNG 書き出しに失敗 {}: {e}",
            pending.path.display()
        ),
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

    /// BGRA 系フォーマットだけがチャンネル入れ替え対象になること。
    #[test]
    fn bgra_detection() {
        assert!(is_bgra(wgpu::TextureFormat::Bgra8Unorm));
        assert!(is_bgra(wgpu::TextureFormat::Bgra8UnormSrgb));
        assert!(!is_bgra(wgpu::TextureFormat::Rgba8Unorm));
        assert!(!is_bgra(wgpu::TextureFormat::Rgba8UnormSrgb));
    }
}
