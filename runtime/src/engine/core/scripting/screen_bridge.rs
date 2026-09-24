// ============================================================
//  screen_bridge.rs — スクリプトの画面情報 API（SEED.Screen）の FFI
//
//  【役割】
//  C# の `SEED.Screen`（Width / Height / SafeArea / Orientation / DPI）が呼ぶ FFI 関数 1 本
//  （`ffi_screen`。問い合わせの種別は kind で分ける）と、その答えの源になる
//  「このフレームの画面情報の写し」（ScreenSnapshot）の公開口を持つ。
//
//  【1 フレーム内で値が変わらない仕組み】
//  写しは App がフレームの決まった位置（スクリプトのフェーズ・ポインタイベントより前。
//  app/screen_publish.rs）で `publish_screen_snapshot` により 1 回だけ差し替える。
//  OS（Android の UI スレッド）からの報告は platform::screen::report に溜まるだけで、
//  スクリプトが読む写しには次のフレームの公開まで反映されない。
//  公開前（起動直後・Play 外）に読まれた場合は初期値（全画面・縦横比の向き・基準 DPI）を返す。
//
//  【数値の並び】（C# 側 scripting/src/Api/ScriptHost.cs の Screen* 定数と一致させる）
//    kind 0 = 寸法      … out[0..2] = 幅, 高さ（ピクセル）
//    kind 1 = 安全領域  … out[0..4] = x, y, 幅, 高さ（描画ターゲット座標・左上原点）
//    kind 2 = 向き      … out[0]    = ScreenOrientation の数値（C# の SEED.ScreenOrientation と同じ）
//    kind 3 = DPI       … out[0]    = 論理 DPI
// ============================================================

use std::cell::Cell;

use crate::engine::platform;
use crate::engine::platform::screen::ScreenSnapshot;

use super::camera_project::{FALLBACK_TARGET_HEIGHT, FALLBACK_TARGET_WIDTH};

// ── 問い合わせ種別（C# 側 ScriptHost.ScreenQuery* と一致させる）──
/// 描画ターゲットの寸法（2 要素: 幅, 高さ）。
pub const SCREEN_QUERY_SIZE: i32 = 0;
/// 安全領域（4 要素: x, y, 幅, 高さ）。
pub const SCREEN_QUERY_SAFE_AREA: i32 = 1;
/// 画面の向き（1 要素: ScreenOrientation の数値）。
pub const SCREEN_QUERY_ORIENTATION: i32 = 2;
/// 論理 DPI（1 要素）。
pub const SCREEN_QUERY_DPI: i32 = 3;

/// どの問い合わせでも書き込む要素数の上限（C# 側はこの容量のバッファを渡す）。
pub const SCREEN_QUERY_MAX_FLOATS: usize = 4;

// ── 種別ごとの要素数と並び（C# 側 Screen.cs の *Count / Index* と一致させる）──
/// 寸法の要素数（幅, 高さ）。
pub const SCREEN_SIZE_FLOATS: usize = 2;
/// 安全領域の要素数（x, y, 幅, 高さ）。
pub const SCREEN_SAFE_AREA_FLOATS: usize = 4;
/// 向き・DPI の要素数。
pub const SCREEN_SCALAR_FLOATS: usize = 1;
/// 寸法: 幅の位置。
pub const SCREEN_FIELD_SIZE_WIDTH: usize = 0;
/// 寸法: 高さの位置。
pub const SCREEN_FIELD_SIZE_HEIGHT: usize = 1;
/// 安全領域: 左上の X の位置。
pub const SCREEN_FIELD_SAFE_X: usize = 0;
/// 安全領域: 左上の Y の位置。
pub const SCREEN_FIELD_SAFE_Y: usize = 1;
/// 安全領域: 幅の位置。
pub const SCREEN_FIELD_SAFE_WIDTH: usize = 2;
/// 安全領域: 高さの位置。
pub const SCREEN_FIELD_SAFE_HEIGHT: usize = 3;
/// 向き・DPI: 値の位置。
pub const SCREEN_FIELD_SCALAR: usize = 0;

thread_local! {
    /// このフレームの画面情報の写し（スクリプトのスレッド＝メインスレッドだけが読み書きする）。
    static SCREEN_SNAPSHOT: Cell<ScreenSnapshot> = Cell::new(initial_snapshot());
}

/// 公開前の初期値（全画面・縦横比の向き・プラットフォームの基準 DPI）。
fn initial_snapshot() -> ScreenSnapshot {
    ScreenSnapshot::fallback(
        [FALLBACK_TARGET_WIDTH, FALLBACK_TARGET_HEIGHT],
        platform::CURRENT.reference_dpi as f32,
    )
}

/// このフレームの画面情報をスクリプトへ公開する（App がフレームごとに 1 回だけ呼ぶ）。
pub fn publish_screen_snapshot(snapshot: ScreenSnapshot) {
    SCREEN_SNAPSHOT.with(|cell| cell.set(snapshot));
}

/// 公開中の画面情報（スクリプトが読むのと同じ値。診断ログ・テスト用）。
pub fn current_screen_snapshot() -> ScreenSnapshot {
    SCREEN_SNAPSHOT.with(|cell| cell.get())
}

/// 問い合わせ種別に応じて、写しの値を FFI の並びへ詰める【純関数】。
///
/// # 戻り値
/// (値の並び, 有効な要素数)。未知の kind は None。
pub fn snapshot_floats(
    snapshot: &ScreenSnapshot,
    kind: i32,
) -> Option<([f32; SCREEN_QUERY_MAX_FLOATS], usize)> {
    let mut out = [0.0; SCREEN_QUERY_MAX_FLOATS];
    let len = match kind {
        SCREEN_QUERY_SIZE => {
            out[SCREEN_FIELD_SIZE_WIDTH] = snapshot.width;
            out[SCREEN_FIELD_SIZE_HEIGHT] = snapshot.height;
            SCREEN_SIZE_FLOATS
        }
        SCREEN_QUERY_SAFE_AREA => {
            out[SCREEN_FIELD_SAFE_X] = snapshot.safe_area.x;
            out[SCREEN_FIELD_SAFE_Y] = snapshot.safe_area.y;
            out[SCREEN_FIELD_SAFE_WIDTH] = snapshot.safe_area.width;
            out[SCREEN_FIELD_SAFE_HEIGHT] = snapshot.safe_area.height;
            SCREEN_SAFE_AREA_FLOATS
        }
        // 列挙の数値（0..=3）は f32 で正確に表せる。
        SCREEN_QUERY_ORIENTATION => {
            out[SCREEN_FIELD_SCALAR] = snapshot.orientation.id() as f32;
            SCREEN_SCALAR_FLOATS
        }
        SCREEN_QUERY_DPI => {
            out[SCREEN_FIELD_SCALAR] = snapshot.dpi;
            SCREEN_SCALAR_FLOATS
        }
        _ => return None,
    };
    Some((out, len))
}

/// 画面情報の問い合わせ（SEED.Screen.Width / Height / SafeArea / Orientation / DPI）。
///
/// # 引数
/// - `kind` … `SCREEN_QUERY_*`
/// - `out` / `cap` … 書き込み先と容量（要素数）。kind ごとの要素数以上が必要
///
/// # 戻り値
/// 書いた要素数。未知の kind・容量不足・`out` が null のときは 0（例外にしない）。
///
/// # Safety
/// `out` は null か、`cap` 要素以上の f32 を書ける領域を指していること（C# 側が stackalloc で用意する）。
pub(super) unsafe extern "system" fn ffi_screen(kind: i32, out: *mut f32, cap: i32) -> i32 {
    if out.is_null() || cap < 0 {
        return 0;
    }
    let snapshot = current_screen_snapshot();
    let Some((values, len)) = snapshot_floats(&snapshot, kind) else { return 0 };
    if (cap as usize) < len {
        return 0;
    }
    // SAFETY: out は null でなく、呼び出し側が cap（>= len）要素の領域を確保している。
    unsafe { std::ptr::copy_nonoverlapping(values.as_ptr(), out, len) };
    len as i32
}

// ============================================================
//  テスト（スレッドローカルなので、各テストのスレッドの中だけで完結する）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::platform::screen::{ScreenOrientation, ScreenRect};

    /// 縦画面・上に穴の写し。
    fn sample() -> ScreenSnapshot {
        ScreenSnapshot {
            width: 1080.0,
            height: 2400.0,
            safe_area: ScreenRect { x: 0.0, y: 136.0, width: 1080.0, height: 2201.0 },
            orientation: ScreenOrientation::PortraitUpsideDown,
            dpi: 420.0,
        }
    }

    /// 呼び出しの共通部分（容量 cap のバッファで FFI を呼ぶ）。
    fn query(kind: i32, cap: usize) -> (i32, [f32; SCREEN_QUERY_MAX_FLOATS]) {
        let mut buf = [0.0f32; SCREEN_QUERY_MAX_FLOATS];
        // SAFETY: buf は SCREEN_QUERY_MAX_FLOATS 要素あり、cap はそれ以下。
        let written = unsafe { ffi_screen(kind, buf.as_mut_ptr(), cap as i32) };
        (written, buf)
    }

    /// 公開した写しの値が種別ごとの並びで返る。同じフレームで何度呼んでも同じ。
    #[test]
    fn ffi_returns_published_values_per_kind() {
        publish_screen_snapshot(sample());
        assert_eq!(query(SCREEN_QUERY_SIZE, SCREEN_QUERY_MAX_FLOATS), (2, [1080.0, 2400.0, 0.0, 0.0]));
        assert_eq!(query(SCREEN_QUERY_SAFE_AREA, SCREEN_QUERY_MAX_FLOATS), (4, [0.0, 136.0, 1080.0, 2201.0]));
        assert_eq!(query(SCREEN_QUERY_ORIENTATION, SCREEN_QUERY_MAX_FLOATS).0, 1);
        assert_eq!(query(SCREEN_QUERY_ORIENTATION, SCREEN_QUERY_MAX_FLOATS).1[0], 1.0);
        assert_eq!(query(SCREEN_QUERY_DPI, SCREEN_QUERY_MAX_FLOATS), (1, [420.0, 0.0, 0.0, 0.0]));
        // 2 回目も同じ（写しは次の公開まで変わらない）
        assert_eq!(query(SCREEN_QUERY_SAFE_AREA, SCREEN_QUERY_MAX_FLOATS), (4, [0.0, 136.0, 1080.0, 2201.0]));
    }

    /// 未知の種別・容量不足・null は 0（何も書かない）。
    #[test]
    fn ffi_rejects_bad_requests() {
        publish_screen_snapshot(sample());
        assert_eq!(query(99, SCREEN_QUERY_MAX_FLOATS).0, 0);
        assert_eq!(query(SCREEN_QUERY_SAFE_AREA, 3), (0, [0.0; SCREEN_QUERY_MAX_FLOATS]));
        // SAFETY: null は関数側で弾く。
        assert_eq!(unsafe { ffi_screen(SCREEN_QUERY_SIZE, std::ptr::null_mut(), 4) }, 0);
    }

    /// 公開前は初期値（全画面・基準 DPI）。
    #[test]
    fn initial_value_is_full_screen_fallback() {
        let snap = current_screen_snapshot();
        assert_eq!((snap.width, snap.height), (FALLBACK_TARGET_WIDTH, FALLBACK_TARGET_HEIGHT));
        assert_eq!(snap.safe_area, ScreenRect::full(FALLBACK_TARGET_WIDTH, FALLBACK_TARGET_HEIGHT));
        assert_eq!(snap.dpi, platform::CURRENT.reference_dpi as f32);
    }
}
