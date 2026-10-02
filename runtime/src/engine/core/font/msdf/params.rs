// ============================================================
//  font/msdf/params.rs — MTSDF の焼き方の定数（1 か所に集める）
//
//  【役割】
//  MTSDF（赤・緑・青 = MSDF の疑似距離、アルファ = 真の SDF。RGBA8）を焼く大きさ・距離の幅・余白と、
//  誤差の補正・字形ごとの検査の閾値をここへ置く（sdf.rs の流儀）。シェーダー（text.wgsl の `TEXT_MTSDF_*`）と
//  同じ値でなければならない定数は、text_aa_tests.rs がシェーダーの本文から読んで一致を確かめる。
//
//  【大きさの決め方（docs/ui_components.md §12.8）】
//  - メモリを 1 チャネルの SDF（R8 の 4096² = 16 MiB）と同じに保つ: RGBA8 の 2048²（16 MiB）。
//  - SEED の文字の大きさ（書体の ascent − descent）40 テクセル = この書体の em の正方形（1000 単位）で約 28.7 テクセル。
//    全角の字は余白込みで約 40 × 40 テクセル → 2048² に約 2,500 字（日本語の常用漢字 + かなが入る数）。
//  - 距離の片側の幅 5 テクセル = 0.125 em（1 チャネルの SDF と同じ em の幅）→ px から値への変換（Weight・縁取り・影）が
//    1 チャネルの SDF と同じ式のまま使える。
// ============================================================

/// MTSDF を焼く文字の大きさ（SEED の文字の大きさの単位 = 書体の ascent − descent。テクセル）。
pub const MTSDF_EM_PX: f32 = 40.0;

/// 距離の片側の幅（テクセル）。値 0.5 が縁、縁から内側へこの距離で 1.0・外側へ 0.0。
pub const MTSDF_SPREAD_PX: f32 = 5.0;

/// グリフの四方の余白（テクセル。整数。距離の片側の幅と同じだけ取る＝縁取りがクアッドの端で切れない所まで）。
pub const MTSDF_PAD_PX: u32 = 5;

/// 距離の片側の幅（em 単位。= 0.125。1 チャネルの SDF の `SDF_SPREAD_EM` と同じ）。
pub const MTSDF_SPREAD_EM: f32 = MTSDF_SPREAD_PX / MTSDF_EM_PX;

/// テクスチャの値 0..1 が表す距離（em 単位。= 0.25。内外の 2 方向なので片側の幅の 2 倍）。
pub const MTSDF_RANGE_EM: f32 = 2.0 * MTSDF_SPREAD_EM;

/// 縁を表す値（0.5。シェーダーの `TEXT_SDF_EDGE` と同じ）。
pub const MTSDF_EDGE_VALUE: f32 = 0.5;

/// 1 テクセル進むと値がいくつ変わるか（= 0.1。シェーダーの `TEXT_MTSDF_VALUE_PER_TEXEL` と同じ式）。
pub const MTSDF_VALUE_PER_TEXEL: f32 = MTSDF_EDGE_VALUE / MTSDF_SPREAD_PX;

/// 縁取り・太さとして表せる値の上限（= 0.45）: クアッドの端の画素の中心（縁から 余白 − 半テクセル）まで。
pub const MTSDF_MAX_OUTLINE_VALUE: f32 = (MTSDF_PAD_PX as f32 - TEXEL_CENTER_OFFSET) * MTSDF_VALUE_PER_TEXEL;

/// テクセルの中心のずれ（テクセルの中心は i + 0.5）。
pub const TEXEL_CENTER_OFFSET: f32 = 0.5;

/// 1 グリフを焼く大きさの組（テクセル）。距離の片側の幅は常に em の `MTSDF_SPREAD_EM`（0.125）倍
/// （px → 値の変換・シェーダーの 1 画素あたりの値の変化が大きさによらず同じになる）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FieldScale {
    /// 焼く文字の大きさ（SEED の文字の大きさの単位。テクセル）。
    pub em_px: f32,
    /// 距離の片側の幅（テクセル）。
    pub spread_px: f32,
    /// 四方の余白（テクセル。整数）。
    pub pad_px: u32,
}

impl FieldScale {
    /// 既定の大きさ（em 40・幅 5・余白 5）。
    pub const BASE: FieldScale = FieldScale { em_px: MTSDF_EM_PX, spread_px: MTSDF_SPREAD_PX, pad_px: MTSDF_PAD_PX };

    /// 文字の大きさ `em_px` で焼く組（幅 = em × 0.125、余白 = 幅を切り上げた整数）。
    pub fn with_em(em_px: f32) -> Self {
        let spread_px = em_px * MTSDF_SPREAD_EM;
        Self { em_px, spread_px, pad_px: spread_px.ceil() as u32 }
    }

    /// 1 テクセル進むと値がいくつ変わるか（0.5 ÷ 幅）。
    pub fn value_per_texel(&self) -> f32 {
        MTSDF_EDGE_VALUE / self.spread_px
    }

    /// これより遠い辺は見ない（テクセル）。
    pub fn cutoff_px(&self) -> f64 {
        f64::from(self.spread_px) + CUTOFF_MARGIN_PX
    }

    /// 辺の残らない輪郭・チャネルの距離の大きさ（テクセル。打ち切りの 2 倍）。
    pub fn far_px(&self) -> f64 {
        2.0 * self.cutoff_px()
    }
}

/// 輪郭の全長（em 単位）がこれを超える字は、長さに比例して大きな em で焼く（画数の多い漢字の細部を潰さない）。
///
/// この書体では英数 0.5〜4.8 em・かな 1.2〜5.1 em・ふつうの漢字 5〜8 em・画数の多い漢字 8〜12 em（measure_tests の計測）。
/// em 40 の 150 px の誤差（参照と 0.5 以上違う画素）はふつうの漢字で 15〜56 画素、画数の多い漢字で 46〜606 画素、
/// em 64 ならどれも 33 画素以下（docs/ui_components.md §12.8）。
pub const MTSDF_DETAIL_LENGTH_EM: f32 = 7.5;

/// 輪郭の長い字を焼く em の上限（テクセル。1 チャネルの SDF と同じ 64＝その字のアトラスの面積は em 40 の 2.56 倍まで）。
pub const MTSDF_MAX_EM_PX: f32 = 64.0;

/// 輪郭の全長（em 単位）から焼く em（テクセル）を決める: `MTSDF_DETAIL_LENGTH_EM` までは既定の em、それを超えると長さに比例、
/// 上限 `MTSDF_MAX_EM_PX`。整数に丸める（同じ字は毎回同じ大きさ）。
pub fn em_for_outline_length(length_em: f32) -> f32 {
    let scaled = MTSDF_EM_PX * (length_em / MTSDF_DETAIL_LENGTH_EM);
    scaled.round().clamp(MTSDF_EM_PX, MTSDF_MAX_EM_PX)
}

/// キャンバスの文字の MTSDF のアトラスの一辺（RGBA8 なので 2048² = 16 MiB。R8 の 4096² と同じ）。
pub const MTSDF_CANVAS_ATLAS_SIZE: u32 = 2048;

/// 1 テクセルのバイト数（RGBA8）。
pub const MTSDF_BYTES_PER_TEXEL: usize = 4;

// ─── 距離の計算 ─────────────────────────────────────────────

/// これより遠い辺は見ない（既定の大きさのとき。テクセル）。値が張り付く片側の幅に、バイリニアの隣のテクセルと平滑化の幅の余裕を足す。
pub const DISTANCE_CUTOFF_PX: f64 = MTSDF_SPREAD_PX as f64 + CUTOFF_MARGIN_PX;
/// 打ち切りの余裕（テクセル）。
const CUTOFF_MARGIN_PX: f64 = 1.5;
/// 辺の残らない輪郭・チャネルの距離の大きさ（既定の大きさのとき。テクセル。打ち切りの 2 倍＝必ず値が張り付く）。
pub const FAR_DISTANCE_PX: f64 = 2.0 * DISTANCE_CUTOFF_PX;

// ─── 誤差の補正（msdfgen の MSDFErrorCorrection。error_correction.rs） ──────

/// 隣のテクセルとの値の差が「1 テクセルぶんの変化 × この比」を超えたら補間の誤りを疑う（msdfgen の minDeviationRatio）。
pub const MIN_DEVIATION_RATIO: f64 = 10.0 / 9.0;
/// 補正で誤差が「この比」以上よくなるときだけ直す（msdfgen の minImproveRatio）。
pub const MIN_IMPROVE_RATIO: f64 = 10.0 / 9.0;
/// 端（t = 0 / 1）とみなす補間の比の幅（msdfgen の ARTIFACT_T_EPSILON）。
pub const ARTIFACT_T_EPSILON: f64 = 0.01;
/// 縁を守るテクセルの判定の余裕（msdfgen の PROTECTION_RADIUS_TOLERANCE）。
pub const PROTECTION_RADIUS_TOLERANCE: f64 = 1.001;

// ─── 字形ごとの検査（安全弁。verify.rs） ──────────────────────────

/// 参照のラスタの細かさ（MTSDF の 1 テクセルを何画素で見るか）。
pub const VERIFY_OVERSAMPLE: u32 = 4;
/// 縁の近くの食い違いを許す幅（参照のラスタの画素。= 0.5 テクセル）。これより内側・外側に深い所の食い違いだけを数える。
pub const VERIFY_EDGE_TOLERANCE_PX: f32 = 2.0;
/// 細部が潰れた量を測る幅（参照のラスタの画素。= 0.25 テクセル）。これより深い所の食い違いを `detail_px` に数える。
pub const VERIFY_DETAIL_TOLERANCE_PX: f32 = 1.0;
/// 食い違いの画素（縁から許す幅より深い所）がこれを超えたら、その字は MSDF をやめて真の SDF（アルファ）で描く。
///
/// 参照のラスタの画素の数（4 × 4 = 1 テクセルの 16 画素の半分 = 0.5 テクセル²）。
pub const VERIFY_MAX_ARTIFACT_PX: usize = 8;

#[cfg(test)]
mod tests {
    use super::*;

    /// 定数どうしの関係（片方だけ書き換えた事故の検出）と、1 チャネルの SDF と同じ em の幅であること。
    #[test]
    fn mtsdf_constants_are_consistent() {
        use crate::engine::core::font::sdf::{SDF_RANGE_EM, SDF_SPREAD_EM};
        assert!((MTSDF_SPREAD_EM - SDF_SPREAD_EM).abs() < 1e-7, "片側の幅は SDF と同じ em（px → 値の変換を共有する）");
        assert!((MTSDF_RANGE_EM - SDF_RANGE_EM).abs() < 1e-7);
        assert!((MTSDF_VALUE_PER_TEXEL - 0.1).abs() < 1e-7);
        assert!((MTSDF_MAX_OUTLINE_VALUE - 0.45).abs() < 1e-6);
        assert!(MTSDF_MAX_OUTLINE_VALUE < MTSDF_EDGE_VALUE, "縁取りの上限は値 0 より内側");
        assert!(MTSDF_PAD_PX as f32 >= MTSDF_SPREAD_PX, "余白は距離の片側の幅以上");
        assert!(DISTANCE_CUTOFF_PX > MTSDF_SPREAD_PX as f64, "打ち切りは値の張り付く距離より遠い");
        // 既定の組は with_em と同じ式（幅 = em × 0.125・余白 = 切り上げ）
        assert_eq!(FieldScale::with_em(MTSDF_EM_PX), FieldScale::BASE);
        assert!((FieldScale::BASE.value_per_texel() - MTSDF_VALUE_PER_TEXEL).abs() < 1e-7);
        assert!((FieldScale::BASE.cutoff_px() - DISTANCE_CUTOFF_PX).abs() < 1e-12);
        // 輪郭の長さ → em: 短い字は既定、長い字は比例して上限まで
        assert_eq!(em_for_outline_length(2.0), MTSDF_EM_PX);
        assert_eq!(em_for_outline_length(MTSDF_DETAIL_LENGTH_EM), MTSDF_EM_PX);
        assert_eq!(em_for_outline_length(MTSDF_DETAIL_LENGTH_EM * 1.25), 50.0);
        assert_eq!(em_for_outline_length(100.0), MTSDF_MAX_EM_PX);
        // どの em でも余白は距離の片側の幅以上（縁取りの上限 MTSDF_MAX_OUTLINE_VALUE は em 40 がいちばん厳しい）
        for em in 40..=64 {
            let s = FieldScale::with_em(em as f32);
            assert!(s.pad_px as f32 >= s.spread_px);
            assert!((s.pad_px as f32 - TEXEL_CENTER_OFFSET) * s.value_per_texel() >= MTSDF_MAX_OUTLINE_VALUE - 1e-6);
        }
        // メモリ: RGBA8 の 2048² = 16 MiB（R8 の 4096² と同じ）
        let bytes = (MTSDF_CANVAS_ATLAS_SIZE as usize).pow(2) * MTSDF_BYTES_PER_TEXEL;
        assert_eq!(bytes, 4096 * 4096);
    }
}
