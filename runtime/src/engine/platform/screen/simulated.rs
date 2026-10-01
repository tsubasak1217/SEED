// ============================================================
//  platform/screen/simulated.rs — PC で安全領域（切り欠き・システムバー）を模擬する（W2-1b。検証用）
//
//  【役割】
//  デスクトップには OS からの安全領域の報告が無いので、`Screen.SafeArea` とキャンバスの安全領域の部品
//  （CanvasSafeArea）は常に画面全体になる。PC の Play で安全領域に沿った UI を確かめるため、環境変数
//  `SEED_SIM_SAFE_AREA` に「左,上,右,下」（描画面の各辺からの距離。物理ピクセル）を書くと、報告が無いフレームに
//  その値を報告として使う（app/screen_publish.rs）。回転は無し（自然な向き＝描画面の縦横）。
//  既定（環境変数なし）では何もしない。Android では使わない（実機の報告がある）。
//
//  例: `SEED_SIM_SAFE_AREA=0,48,0,24`（上 48 px・下 24 px を避ける）
//
//  同じく `SEED_SIM_SCALE_FACTOR`（正の実数）で表示倍率（winit の scale_factor）を上書きできる。
//  `Screen.DPI` と、dp のルートキャンバスの 1 dp の画素数がこの値になる（PC で端末の密度を模擬する。
//  例: Pixel 6a の 420 dpi は 2.625）。
//
//  同じく `SEED_SIM_WINDOW_SIZE`（"幅x高さ"。物理ピクセル）で Play の窓の最初の大きさを上書きできる
//  （project_settings.json の window_width / window_height の代わり。端末の画面の大きさで描画の資源を確かめる
//  GPU メモリの計測用。例: Pixel 6a の `1080x2400`）。窓が画面より大きいと OS が縮めることがある
//  （実際の大きさは起動ログの描画面の寸法で確かめる）。
// ============================================================

use std::sync::OnceLock;

use super::report::{EdgeInsets, ScreenReport};

/// 模擬の安全領域を指定する環境変数の名前。
pub const ENV_SIM_SAFE_AREA: &str = "SEED_SIM_SAFE_AREA";

/// 模擬の表示倍率を指定する環境変数の名前。
pub const ENV_SIM_SCALE_FACTOR: &str = "SEED_SIM_SCALE_FACTOR";

/// Play の窓の最初の大きさを上書きする環境変数の名前（"幅x高さ"）。
pub const ENV_SIM_WINDOW_SIZE: &str = "SEED_SIM_WINDOW_SIZE";

/// 窓の大きさの幅と高さの区切り（"1080x2400"）。
const SIZE_SEPARATOR: char = 'x';

/// 値の区切り（左,上,右,下）。
const SEPARATOR: char = ',';

/// 値の数（左・上・右・下）。
const EDGE_COUNT: usize = 4;

/// 回転なし（自然な向き）。
const NO_ROTATION: u32 = 0;

/// 「左,上,右,下」の文字列を読む【純関数】（数が違う・数でない値は None）。
pub fn parse_insets(text: &str) -> Option<EdgeInsets> {
    let values: Vec<u32> = text
        .split(SEPARATOR)
        .map(|v| v.trim().parse::<u32>().ok())
        .collect::<Option<Vec<u32>>>()?;
    let [left, top, right, bottom]: [u32; EDGE_COUNT] = values.try_into().ok()?;
    Some(EdgeInsets { left, top, right, bottom })
}

/// 環境変数の模擬の安全領域（プロセスで 1 度だけ読む。無い・読めなければ None）。
pub fn simulated_insets() -> Option<EdgeInsets> {
    static INSETS: OnceLock<Option<EdgeInsets>> = OnceLock::new();
    *INSETS.get_or_init(|| {
        if cfg!(target_os = "android") {
            return None;
        }
        let text = std::env::var(ENV_SIM_SAFE_AREA).ok()?;
        let parsed = parse_insets(&text);
        if parsed.is_none() {
            eprintln!("[SEED screen] {ENV_SIM_SAFE_AREA}={text:?} を読めません（左,上,右,下 の 0 以上の整数 4 つ）。無視します");
        }
        parsed
    })
}

/// 表示倍率の文字列を読む【純関数】（正の有限の実数だけ）。
pub fn parse_scale_factor(text: &str) -> Option<f64> {
    text.trim().parse::<f64>().ok().filter(|v| v.is_finite() && *v > 0.0)
}

/// 環境変数の模擬の表示倍率（プロセスで 1 度だけ読む。無い・読めなければ None。Android では使わない）。
pub fn simulated_scale_factor() -> Option<f64> {
    static SCALE: OnceLock<Option<f64>> = OnceLock::new();
    *SCALE.get_or_init(|| {
        if cfg!(target_os = "android") {
            return None;
        }
        let text = std::env::var(ENV_SIM_SCALE_FACTOR).ok()?;
        let parsed = parse_scale_factor(&text);
        if parsed.is_none() {
            eprintln!("[SEED screen] {ENV_SIM_SCALE_FACTOR}={text:?} を読めません（正の実数）。無視します");
        }
        parsed
    })
}

/// 窓の大きさの文字列（"幅x高さ"）を読む【純関数】（どちらも 1 以上の整数だけ。大文字の X も受け付ける）。
pub fn parse_window_size(text: &str) -> Option<(u32, u32)> {
    let lower = text.trim().to_ascii_lowercase();
    let (w, h) = lower.split_once(SIZE_SEPARATOR)?;
    let w = w.trim().parse::<u32>().ok().filter(|v| *v > 0)?;
    let h = h.trim().parse::<u32>().ok().filter(|v| *v > 0)?;
    Some((w, h))
}

/// 環境変数の Play の窓の大きさの上書き（プロセスで 1 度だけ読む。無い・読めなければ None。Android では使わない）。
pub fn simulated_window_size() -> Option<(u32, u32)> {
    static SIZE: OnceLock<Option<(u32, u32)>> = OnceLock::new();
    *SIZE.get_or_init(|| {
        if cfg!(target_os = "android") {
            return None;
        }
        let text = std::env::var(ENV_SIM_WINDOW_SIZE).ok()?;
        let parsed = parse_window_size(&text);
        if parsed.is_none() {
            eprintln!("[SEED screen] {ENV_SIM_WINDOW_SIZE}={text:?} を読めません（幅x高さ の 1 以上の整数）。無視します");
        }
        parsed
    })
}

/// 描画面の大きさに対する模擬の報告（模擬が無ければ None）【純関数（模擬の値を引数で受ける版）】。
///
/// # 引数
/// * `insets` - 模擬の安全領域
/// * `window` - 描画面の実寸（物理ピクセル）
pub fn simulated_report(insets: EdgeInsets, window: (u32, u32)) -> ScreenReport {
    let (width, height) = window;
    ScreenReport {
        frame_width: width,
        frame_height: height,
        insets,
        rotation_quarter_turns: NO_ROTATION,
        natural_width: width,
        natural_height: height,
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 「左,上,右,下」を読む（空白は許す）。数が違う・負・数でない値は None。
    #[test]
    fn parse_insets_reads_four_edges() {
        assert_eq!(parse_insets("0, 48 ,0,24"), Some(EdgeInsets { left: 0, top: 48, right: 0, bottom: 24 }));
        assert_eq!(parse_insets("1,2,3"), None);
        assert_eq!(parse_insets("1,2,3,-4"), None);
        assert_eq!(parse_insets("a,b,c,d"), None);
    }

    /// 表示倍率は正の有限の実数だけ。
    #[test]
    fn parse_scale_factor_accepts_positive_numbers() {
        assert_eq!(parse_scale_factor(" 2.625 "), Some(2.625));
        assert_eq!(parse_scale_factor("0"), None);
        assert_eq!(parse_scale_factor("-1"), None);
        assert_eq!(parse_scale_factor("x"), None);
    }

    /// 窓の大きさは "幅x高さ"（空白・大文字の X は許す）。0・負・数でない・区切りの無い値は None。
    #[test]
    fn parse_window_size_reads_width_and_height() {
        assert_eq!(parse_window_size("1080x2400"), Some((1080, 2400)));
        assert_eq!(parse_window_size(" 540 X 1200 "), Some((540, 1200)));
        assert_eq!(parse_window_size("0x10"), None);
        assert_eq!(parse_window_size("1080"), None);
        assert_eq!(parse_window_size("ax2"), None);
    }

    /// 模擬の報告は描画面の大きさと一致し（select_for_frame が選ぶ）、回転は無し。
    #[test]
    fn simulated_report_matches_the_frame() {
        let report = simulated_report(EdgeInsets { left: 0, top: 48, right: 0, bottom: 24 }, (540, 1200));
        assert!(report.matches_frame(540, 1200));
        assert_eq!(report.rotation_quarter_turns, 0);
        assert_eq!((report.natural_width, report.natural_height), (540, 1200));
    }
}
