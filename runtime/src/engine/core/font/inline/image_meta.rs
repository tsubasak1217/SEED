// ============================================================
//  font/inline/image_meta.rs — インライン画像の寸法メタ情報キャッシュ
//
//  【役割】
//  インライン画像の**幅**は「高さ × アスペクト比」で決まるため、
//  レイアウト（＝ GPU に触らない純計算）の段階で画像の縦横比が要る。
//  ここはその縦横比だけを `asset_fs` 経由で調べ、パス単位でキャッシュする。
//
//  【なぜ GPU テクスチャから取らないか】
//  折り返し・行幅・境界矩形の計算は、描画器を持たない層
//  （ピック・選択枠・スプライト収集）からも走る。GPU テクスチャに依存すると
//  「描いた形」と「測った形」が経路ごとにズレる。寸法の唯一の出所を
//  CPU 側のこのキャッシュに固定することで、構造的にズレを防ぐ。
//
//  【コスト】
//  画像はデコードせず**ヘッダだけ**読む（`ImageReader::into_dimensions`）。
//  結果は成功・失敗ともキャッシュするので、同じパスで 2 度ディスクへ行かない。
//
//  【ライブ編集の自動反映（ポーリング）】
//  画像を差し替えるとアスペクト比（＝レイアウト上の幅）も変わり得るため、
//  `icon_set.rs` と同じ方式で「実ファイルパス＋読み込み時点の更新時刻」を
//  エントリに持たせ、`poll_changes` で定期的に差分を検知する。
//  デコードに失敗した場合（保存の途中で不完全な画像を掴んだ等）は
//  前回のアスペクト比を維持し、記録 mtime も更新しない
//  （次回のポーリングで再試行される）。
// ============================================================

use std::collections::HashMap;
use std::io::Cursor;
use std::sync::{Mutex, OnceLock};

use crate::engine::asset_fs;

/// アスペクト比（幅 / 高さ）の下限。
///
/// 極端に細い画像でも送り幅が 0 にならないようにする（0 幅の分割不可クラスタは
/// 折り返しの前進判定を壊す）。
pub const MIN_ASPECT: f32 = 0.01;

/// アスペクト比（幅 / 高さ）の上限。
///
/// 誤って巨大な帯画像を差し込んでも 1 クラスタの幅が発散しないようにする。
pub const MAX_ASPECT: f32 = 100.0;

/// キャッシュ 1 件分の内容。
///
/// `resolved_path` / `mtime` は `icon_set::CacheEntry` と同じ役割で、
/// ポーリングによる差し替え検知にのみ使う付帯情報。
struct CacheEntry {
    /// `asset_fs::normalize_asset_path` 済みの実ファイルパス。
    resolved_path: String,
    /// 読み込み時点の更新時刻（UNIX 秒）。`0` = PAK 内 / 取得不能（変化検出しない）。
    mtime: u64,
    /// 解決済みアスペクト比（幅 / 高さ）。`None` = 読み込み／解析に失敗済み。
    aspect: Option<f32>,
}

/// パス（`aspect_cached` に渡された生の文字列）→ キャッシュ内容。
type AspectCache = HashMap<String, CacheEntry>;

/// キャッシュ実体（プロセス内で 1 つ）。
static CACHE: OnceLock<Mutex<AspectCache>> = OnceLock::new();

/// キャッシュへの排他アクセスを得る。
fn cache() -> &'static Mutex<AspectCache> {
    CACHE.get_or_init(|| Mutex::new(AspectCache::new()))
}

/// 画像のアスペクト比（幅 / 高さ）を返す。解決できなければ `None`。
///
/// 失敗時は警告を **1 度だけ** 出す（毎フレーム走る経路のため）。
pub fn aspect_cached(path: &str) -> Option<f32> {
    if path.is_empty() {
        return None;
    }
    let mut map = cache().lock().ok()?;
    if let Some(hit) = map.get(path) {
        return hit.aspect;
    }
    // 相対パスはアセットルート基準へ寄せる（read_bytes の CWD 依存を避ける）。
    let resolved = asset_fs::normalize_asset_path(path);
    let mtime = asset_fs::mtime(&resolved);
    let value = read_aspect_at(path, &resolved);
    map.insert(path.to_string(), CacheEntry { resolved_path: resolved, mtime, aspect: value });
    value
}

/// 画像のヘッダを読んでアスペクト比を求める（キャッシュを介さない実処理）。
///
/// - `display_path`: 警告メッセージに出す「元のパス」（利用者から見た手がかり）
/// - `resolved_path`: 実際に読みに行く正規化済みパス
fn read_aspect_at(display_path: &str, resolved_path: &str) -> Option<f32> {
    let bytes = match asset_fs::read_bytes(resolved_path) {
        Ok(b) => b,
        Err(e) => {
            eprintln!("[SEED TEXT] インライン画像を読み込めません: {display_path} ({e})");
            return None;
        }
    };
    // 拡張子ではなく中身から形式を推定する（PAK 内の拡張子ゆれに強い）。
    let reader = match image::ImageReader::new(Cursor::new(&bytes)).with_guessed_format() {
        Ok(r) => r,
        Err(e) => {
            eprintln!("[SEED TEXT] インライン画像の形式を判別できません: {display_path} ({e})");
            return None;
        }
    };
    match reader.into_dimensions() {
        Ok((w, h)) if w > 0 && h > 0 => {
            Some((w as f32 / h as f32).clamp(MIN_ASPECT, MAX_ASPECT))
        }
        Ok(_) => {
            eprintln!("[SEED TEXT] インライン画像の寸法が 0 です: {display_path}");
            None
        }
        Err(e) => {
            eprintln!("[SEED TEXT] インライン画像の寸法を取得できません: {display_path} ({e})");
            None
        }
    }
}

/// 指定パスのキャッシュを捨てる（アセットのホットリロード用）。
pub fn invalidate(path: &str) {
    if let Ok(mut map) = cache().lock() {
        map.remove(path);
    }
}

/// 全キャッシュを捨てる（プロジェクト切り替え・アセットルート変更用）。
pub fn invalidate_all() {
    if let Ok(mut map) = cache().lock() {
        map.clear();
    }
}

// ─── ライブ編集ポーリング ────────────────────────────────────

/// 全エントリの更新時刻を確認し、変化したものだけ再デコードする。
///
/// `icon_set::poll_changes_with` と同じ設計。`mtime_fn` はテスト用の
/// 注入口で、本番では `asset_fs::mtime` を渡す（`poll_changes` 参照）。
///
/// 戻り値: 1 件でも再読込（アスペクト比の差し替え）が起きたか。
/// デコード失敗時は前回のアスペクト比と記録 mtime を維持する。
fn poll_changes_with(mtime_fn: &dyn Fn(&str) -> u64) -> bool {
    let Ok(mut map) = cache().lock() else { return false };
    let mut changed = false;
    for (path, entry) in map.iter_mut() {
        let current = mtime_fn(&entry.resolved_path);
        if current == 0 || current == entry.mtime {
            continue;
        }
        match read_aspect_at(path, &entry.resolved_path) {
            Some(aspect) => {
                entry.aspect = Some(aspect);
                entry.mtime = current;
                changed = true;
            }
            None => {
                // 保存の途中で不完全な画像を掴んだ可能性がある。
                // mtime は更新しないので、次のポーリングで再度読み直しに来る。
                eprintln!(
                    "[SEED TEXT] インライン画像の再読込に失敗しました（前回の内容を維持します）: {path}"
                );
            }
        }
    }
    changed
}

/// 全エントリの更新時刻を確認し、変化したものだけ再デコードする（本番用）。
pub fn poll_changes() -> bool {
    poll_changes_with(&asset_fs::mtime)
}

// ============================================================
//  単体テスト（実ファイルに依存しない範囲だけ）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 空パスはディスクへ触らず None。
    #[test]
    fn empty_path_is_none() {
        assert!(aspect_cached("").is_none());
    }

    /// 存在しないパスは None を返し、2 度目もディスクへ行かない（キャッシュ済み）。
    #[test]
    fn missing_path_is_cached_as_failure() {
        let path = "assets://__no_such_inline_image__.png";
        assert!(aspect_cached(path).is_none());
        assert!(aspect_cached(path).is_none());
        assert!(
            cache().lock().unwrap().contains_key(path),
            "失敗もキャッシュされる（毎フレームの再試行防止）"
        );
    }

    // ── ライブ編集ポーリング ──────────────────────────────────
    //
    // 実ファイルとして最小の PNG（1x1 → 差し替え後は 2x1）を一時ディレクトリへ
    // 書き、`icon_set.rs` のテストと同じ方式で疑似 mtime を注入して検証する。

    /// 幅 `w` 高さ `h` の最小 PNG バイト列を作る（`image` クレートでエンコード）。
    fn make_png_bytes(w: u32, h: u32) -> Vec<u8> {
        let img = image::RgbaImage::from_pixel(w, h, image::Rgba([255, 0, 255, 255]));
        let mut buf = Vec::new();
        image::DynamicImage::ImageRgba8(img)
            .write_to(&mut Cursor::new(&mut buf), image::ImageFormat::Png)
            .expect("PNG エンコードできる");
        buf
    }

    /// テスト用一時ファイルを作り、絶対パス文字列を返す。
    fn write_temp_png(name: &str, w: u32, h: u32) -> String {
        let mut path = std::env::temp_dir();
        path.push(format!("seed_image_meta_test_{}_{}", std::process::id(), name));
        std::fs::write(&path, make_png_bytes(w, h)).expect("書き込める");
        path.to_string_lossy().to_string()
    }

    /// 更新時刻が変化したエントリだけが再デコードされ、アスペクト比が更新されること。
    #[test]
    fn poll_reloads_aspect_when_mtime_changes() {
        invalidate_all();
        let path = write_temp_png("square.png", 10, 10);
        let before = aspect_cached(&path).expect("最初は 1.0 で読める");
        assert!((before - 1.0).abs() < f32::EPSILON);

        // 横に長い画像へ差し替える（アスペクト比が 2.0 になるはず）。
        write_temp_png("square.png", 20, 10);
        let resolved = asset_fs::normalize_asset_path(&path);
        let changed = poll_changes_with(&move |p: &str| if p == resolved { 12345 } else { 0 });

        assert!(changed, "アスペクト比の変化を検出して再読込するはず");
        let after = aspect_cached(&path).expect("再読込後も読める");
        assert!((after - 2.0).abs() < f32::EPSILON, "新しいアスペクト比（2.0）に更新されている");

        let _ = std::fs::remove_file(&path);
        invalidate_all();
    }

    /// デコードに失敗する（不完全な画像を掴んだ）場合は前回のアスペクト比が維持されること。
    #[test]
    fn poll_keeps_previous_aspect_on_decode_failure() {
        invalidate_all();
        let path = write_temp_png("broken.png", 10, 10);
        let before = aspect_cached(&path).expect("最初は読める");

        // 保存の途中を模した不完全なバイト列（PNG として壊れている）。
        std::fs::write(&path, b"not a valid png").expect("書き込める");
        let resolved = asset_fs::normalize_asset_path(&path);
        let changed = poll_changes_with(&move |p: &str| if p == resolved { 999 } else { 0 });

        assert!(!changed, "デコードに失敗したので「変化した」扱いにはしない");
        let after = aspect_cached(&path).expect("前回のアスペクト比が維持されている");
        assert_eq!(before, after);

        let _ = std::fs::remove_file(&path);
        invalidate_all();
    }
}
