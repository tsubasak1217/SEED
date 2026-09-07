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

/// パス → アスペクト比。`None` = 読み込み／解析に失敗済み（再試行しない）。
type AspectCache = HashMap<String, Option<f32>>;

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
        return *hit;
    }
    let value = read_aspect(path);
    map.insert(path.to_string(), value);
    value
}

/// 画像のヘッダを読んでアスペクト比を求める（キャッシュを介さない実処理）。
fn read_aspect(path: &str) -> Option<f32> {
    // 相対パスはアセットルート基準へ寄せる（read_bytes の CWD 依存を避ける）。
    let resolved = asset_fs::normalize_asset_path(path);
    let bytes = match asset_fs::read_bytes(&resolved) {
        Ok(b) => b,
        Err(e) => {
            eprintln!("[SEED TEXT] インライン画像を読み込めません: {path} ({e})");
            return None;
        }
    };
    // 拡張子ではなく中身から形式を推定する（PAK 内の拡張子ゆれに強い）。
    let reader = match image::ImageReader::new(Cursor::new(&bytes)).with_guessed_format() {
        Ok(r) => r,
        Err(e) => {
            eprintln!("[SEED TEXT] インライン画像の形式を判別できません: {path} ({e})");
            return None;
        }
    };
    match reader.into_dimensions() {
        Ok((w, h)) if w > 0 && h > 0 => {
            Some((w as f32 / h as f32).clamp(MIN_ASPECT, MAX_ASPECT))
        }
        Ok(_) => {
            eprintln!("[SEED TEXT] インライン画像の寸法が 0 です: {path}");
            None
        }
        Err(e) => {
            eprintln!("[SEED TEXT] インライン画像の寸法を取得できません: {path} ({e})");
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
}
