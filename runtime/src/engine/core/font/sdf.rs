// ============================================================
//  font/sdf.rs — SDF グリフアトラスの共通定数と変換ヘルパー
//
//  【役割】
//  グリフアトラスは「サイズ非依存」＝ フォントサイズごとにラスタライズせず、
//  固定 em サイズ（SDF_EM_PX）で 1 度だけ距離場を焼いて、描画時に
//  フォントサイズを掛けて拡大縮小する。その基準値をここへ集約する。
//
//  【なぜ 1 箇所に集めるか】
//  ラスタライザ（焼く側）・アトラス（em → px 換算する側）・
//  キャンバステキスト（アウトライン太さを SDF 距離へ変換する側）の
//  3 箇所が同じ定数を使う。ズレると縁取りが太さ通りに出なくなる。
// ============================================================

// ─── SDF ラスタライズの基準値 ────────────────────────────────

/// SDF をラスタライズする固定 em サイズ（px）。サイズ非依存アトラスの基準。
///
/// これを大きくすると品質は上がるがアトラス消費が二乗で増える。
/// 64px は「拡大時に角が丸まらず、4096 アトラスに日本語 2500 字が載る」妥協点。
pub const SDF_EM_PX: f32 = 64.0;

/// SDF のスプレッド半径（SDF_EM_PX 空間でのピクセル数 = グリフ矩形の四方パディング）。
///
/// 距離場がこの半径まで外側へ伸びる。縁取りの最大太さもここで決まる。
pub const SDF_SPREAD_PX: u32 = 8;

/// スプレッドを em 単位にしたもの (= 0.125)。
pub const SDF_SPREAD_EM: f32 = SDF_SPREAD_PX as f32 / SDF_EM_PX;

/// テクスチャ値 0..1 が表す距離（em 単位）(= 0.25)。d = 0.5 がエッジ。
///
/// 距離場は内側 0.5→1.0、外側 0.5→0.0 の 2 方向へ広がるのでスプレッドの 2 倍。
pub const SDF_RANGE_EM: f32 = 2.0 * SDF_SPREAD_EM;

/// アウトラインとして表現できる SDF 距離の上限。
///
/// 0.5 = スプレッド一杯（テクスチャ値 0.0 の位置）。これ以上外側の距離は
/// 焼かれていないので、太さを増やしても縁は広がらない。
pub const MAX_OUTLINE_SDF: f32 = 0.5;

// ─── 変換ヘルパー ────────────────────────────────────────────

/// アウトライン太さ(px) を SDF テクスチャ単位へ変換する。
///
/// - `outline_width_px`: 縁取りの太さ（そのテキストのローカルピクセル）
/// - `font_size_px`    : そのテキストのフォントサイズ（px）
///
/// 返り値は「エッジ(0.5) から外側へ何テクスチャ単位ぶん広げるか」。
/// 0.5 = スプレッド一杯（これ以上太くできない上限）。
/// `font_size <= 0` や `width <= 0` は 0（＝縁取りなし）を返す。
pub fn outline_px_to_sdf(outline_width_px: f32, font_size_px: f32) -> f32 {
    // 太さ 0 以下・サイズ 0 以下は縁取り無し（0 除算も避ける）。
    if outline_width_px <= 0.0 || font_size_px <= 0.0 {
        return 0.0;
    }
    // px → em → テクスチャ単位。焼いてある範囲を超えたら頭打ちにする。
    ((outline_width_px / font_size_px) / SDF_RANGE_EM).clamp(0.0, MAX_OUTLINE_SDF)
}

// ============================================================
//  ユニットテスト（GPU 不要の純関数）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 定数同士の関係が崩れていないこと（片方だけ書き換えた事故の検出）。
    #[test]
    fn sdf_constants_are_consistent() {
        assert!((SDF_SPREAD_EM - 0.125).abs() < 1e-6);
        assert!((SDF_RANGE_EM - 0.25).abs() < 1e-6);
    }

    /// 太さ 0 / サイズ 0 は縁取りなし（0）。
    #[test]
    fn outline_zero_width_is_zero() {
        assert_eq!(outline_px_to_sdf(0.0, 24.0), 0.0);
        assert_eq!(outline_px_to_sdf(-3.0, 24.0), 0.0);
        assert_eq!(outline_px_to_sdf(4.0, 0.0), 0.0);
    }

    /// スプレッドを超える太さは 0.5 で頭打ちになる。
    #[test]
    fn outline_clamps_at_spread_limit() {
        // font_size 24 で SDF_RANGE_EM(=0.25) 相当 = 6px。その倍を渡す。
        assert!((outline_px_to_sdf(12.0, 24.0) - MAX_OUTLINE_SDF).abs() < 1e-6);
        assert!((outline_px_to_sdf(1000.0, 24.0) - MAX_OUTLINE_SDF).abs() < 1e-6);
    }

    /// 中間値は閉じた式 (w/fs)/SDF_RANGE_EM と一致する。
    #[test]
    fn outline_matches_closed_form() {
        let w = 2.0f32;
        let fs = 32.0f32;
        let expect = (w / fs) / SDF_RANGE_EM;
        assert!(expect < MAX_OUTLINE_SDF, "テスト値がクランプ域に入っている");
        assert!((outline_px_to_sdf(w, fs) - expect).abs() < 1e-6);
    }
}
