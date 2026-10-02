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

/// 画素の中心から画素の縁までの距離（SDF_EM_PX 空間のピクセル = テクセル）。
///
/// 距離場の値は「字の縁（2 値にしたビットマップの内側と外側の画素の**境目**）までの距離」を表す
/// （2026-10-01 から。docs/ui_components.md §12）。距離変換が返すのは「反対側の画素の**中心**までの距離」なので、
/// そこからこの半画素を引いて縁までの距離にする。引かないと縁の前後 1 テクセルだけ値の傾きが 2 倍になり、
/// 画面の画素の幅で平滑化したときに縁が硬く・細い線が細く出る（縁取り・太さも半テクセル細かった）。
pub const SDF_PIXEL_CENTER_TO_EDGE_PX: f32 = 0.5;

/// SDF の値で字の縁を表す値 (= 0.5)。内側はこれより大きく（1.0 まで）、外側は小さい（0.0 まで）。
///
/// シェーダー（text.wgsl の `TEXT_SDF_EDGE`）と同じ値。縁から 0 / 1 までの値の幅もこの値に等しい。
pub const SDF_EDGE_VALUE: f32 = 0.5;

/// 1 テクセル（SDF_EM_PX 空間の 1 ピクセル）進むと SDF の値がいくつ変わるか (= 0.0625)。
///
/// エッジ 0.5 からスプレッドぶん離れると 0 / 1 に届く。シェーダー（text.wgsl の `TEXT_SDF_VALUE_PER_TEXEL`）と同じ式。
pub const SDF_VALUE_PER_TEXEL: f32 = SDF_EDGE_VALUE / SDF_SPREAD_PX as f32;

/// アウトラインとして表現できる SDF 距離の上限 (= 0.46875)。
///
/// グリフのクアッドの端（パディングの外周の画素の中心）は字の縁から
/// 少なくとも `スプレッド − 半画素` 離れている。そこまでを上限にする（これより太い縁取りは
/// クアッドの端で切れて四角く見えるので頭打ちにする）。距離を縁から測るようにした 2026-10-01 より前の
/// 上限 0.5（中心から測った 8 テクセル = 縁から 7.5 テクセル）と同じ所まで塗る。
pub const MAX_OUTLINE_SDF: f32 = (SDF_SPREAD_PX as f32 - SDF_PIXEL_CENTER_TO_EDGE_PX) * SDF_VALUE_PER_TEXEL;

// ─── 変換ヘルパー ────────────────────────────────────────────

/// px → 距離場の値の変換の決まり（距離場の種類ごと。1 チャネルの SDF と MTSDF は em の幅が同じで上限だけ違う）。
///
/// `Weight`・`OutlineWidth`・`ShadowSoftness` の px を、そのテキストの大きさで割って em にし、値 0..1 が表す距離
/// （`range_em`）で割って値の単位にする。焼いてある範囲（±`max_outline_value`）で頭打ちにする。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FieldValueSpec {
    /// 値 0..1 が表す距離（em 単位）。
    pub range_em: f32,
    /// 縁取り・太さとして表せる値の上限（クアッドの端まで）。
    pub max_outline_value: f32,
}

impl FieldValueSpec {
    /// 1 チャネルの SDF（em 64・spread 8）の決まり。
    pub const SDF: FieldValueSpec = FieldValueSpec { range_em: SDF_RANGE_EM, max_outline_value: MAX_OUTLINE_SDF };

    /// 任意の px 量を値の単位へ（符号つき）。`font_size <= 0` は 0。
    pub fn px_to_value(&self, px: f32, font_size_px: f32) -> f32 {
        if font_size_px <= 0.0 {
            return 0.0;
        }
        ((px / font_size_px) / self.range_em).clamp(-self.max_outline_value, self.max_outline_value)
    }

    /// 縁取りの太さ（px）を値の単位へ（0 以上）。太さ 0 以下・大きさ 0 以下は 0（縁取りなし）。
    pub fn outline_px_to_value(&self, outline_width_px: f32, font_size_px: f32) -> f32 {
        if outline_width_px <= 0.0 || font_size_px <= 0.0 {
            return 0.0;
        }
        ((outline_width_px / font_size_px) / self.range_em).clamp(0.0, self.max_outline_value)
    }
}

/// アウトライン太さ(px) を SDF テクスチャ単位へ変換する。
///
/// - `outline_width_px`: 縁取りの太さ（そのテキストのローカルピクセル）
/// - `font_size_px`    : そのテキストのフォントサイズ（px）
///
/// 返り値は「エッジ(0.5) から外側へ何テクスチャ単位ぶん広げるか」。
/// `MAX_OUTLINE_SDF`（= 0.46875。クアッドの端まで）がこれ以上太くできない上限。
/// `font_size <= 0` や `width <= 0` は 0（＝縁取りなし）を返す。
/// 任意の px 量を SDF テクスチャ単位へ変換する（**符号つき**）。
///
/// - `px`           : 変換したい距離（負値も許す）
/// - `font_size_px` : そのテキストのフォントサイズ（px）
///
/// 文字の太さ調整（負 = 細く / 正 = 太く）と、影のぼかし幅に使う。
/// 焼いてある距離場の範囲（±`MAX_OUTLINE_SDF`）で頭打ちにするのは
/// `outline_px_to_sdf` と同じ理由（それ以上は距離が存在しない）。
/// `font_size <= 0` は 0 を返す（0 除算回避）。
pub fn px_to_sdf(px: f32, font_size_px: f32) -> f32 {
    FieldValueSpec::SDF.px_to_value(px, font_size_px)
}

pub fn outline_px_to_sdf(outline_width_px: f32, font_size_px: f32) -> f32 {
    // px → em → テクスチャ単位。焼いてある範囲を超えたら頭打ちにする（式は FieldValueSpec に一本化）。
    FieldValueSpec::SDF.outline_px_to_value(outline_width_px, font_size_px)
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
        assert!((SDF_VALUE_PER_TEXEL - 0.0625).abs() < 1e-6);
        // 縁取りの上限 = 縁から 7.5 テクセル（パディングの外周の画素の中心まで）
        assert!((MAX_OUTLINE_SDF - 0.46875).abs() < 1e-6);
        assert!(MAX_OUTLINE_SDF < 0.5, "縁取りの上限はテクスチャ値 0 より内側（クアッドの端で切れない）");
    }

    /// 太さ 0 / サイズ 0 は縁取りなし（0）。
    #[test]
    fn outline_zero_width_is_zero() {
        assert_eq!(outline_px_to_sdf(0.0, 24.0), 0.0);
        assert_eq!(outline_px_to_sdf(-3.0, 24.0), 0.0);
        assert_eq!(outline_px_to_sdf(4.0, 0.0), 0.0);
    }

    /// スプレッドを超える太さは MAX_OUTLINE_SDF（クアッドの端まで）で頭打ちになる。
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

    /// 符号つき変換は 0 で 0、正負対称、スプレッド上限で頭打ちになる。
    #[test]
    fn px_to_sdf_is_signed_and_clamped() {
        assert_eq!(px_to_sdf(0.0, 24.0), 0.0);
        assert_eq!(px_to_sdf(1.0, 0.0), 0.0, "サイズ 0 は 0");
        let a = px_to_sdf(2.0, 40.0);
        let b = px_to_sdf(-2.0, 40.0);
        assert!((a + b).abs() < 1e-6, "正負対称");
        // 0.125em（= スプレッド）を超えると ±MAX_OUTLINE_SDF で頭打ち。
        assert!((px_to_sdf(1000.0, 40.0) - MAX_OUTLINE_SDF).abs() < 1e-6);
        assert!((px_to_sdf(-1000.0, 40.0) + MAX_OUTLINE_SDF).abs() < 1e-6);
    }
}
