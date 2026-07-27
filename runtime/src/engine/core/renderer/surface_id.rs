// ============================================================
//  surface_id.rs — G-Buffer 「サーフェス識別情報」のビット規約（単一の真実source）
//
//  ## 役割（単一責任）
//  G-Buffer の空きチャンネルへ詰め込む「情報系」の値について、
//  ビット幅・シフト量・パック／アンパック手順**だけ**を定義する。
//  GPU リソースもパイプラインも持たない純粋な規約モジュールである。
//
//  ## ここが扱う 3 つの値（3 層モデルの第 1 層 = G-Buffer に載る）
//    1. セマンティックタグ  `render_tag`    : アクタ単位（「敵」「インタラクト可能」等）
//    2. シェーディングモデル `shading_model` : マテリアル単位（0 = DefaultPBR。将来トゥーン等）
//    3. ユーザーデータ      `user_data`     : マテリアル単位の自由 0..1 float（濡れ・ダメージ等）
//
//  ## 格納先（gbuffer_write.wgsl と一致必須。正典はシェーダ側）
//    - `render_tag` + `shading_model` → **RT3(Rgba16Float).a** に「整数値の float」として 1 本化
//      （下位 RENDER_TAG_BITS ビット = tag / 続く SHADING_MODEL_BITS ビット = shading model）。
//      Rgba16Float（IEEE half）の仮数部は 10bit + 暗黙 1bit ＝ **整数 2048 まで誤差ゼロ**で
//      表現できるため、6bit（最大 63）は完全に無損失で往復する。
//    - `user_data` → **RT2(Rgba8Unorm).a**（0..1 を 8bit 量子化。1/255 刻み）。
//
//  ## なぜ RT2.a / RT3.a なのか（新規 MRT を足さない理由）
//  棚卸しの結果、G-Buffer 4 枚のうち完全に未使用だったのは RT2.a と RT3.a の 2 チャンネル
//  だけだった（RT0 は albedo+occlusion で満杯、RT1.w は「authored 法線フラグ」で使用中、
//  RT2.b は diffuse_transmission で使用中）。5 枚目の MRT を足すと不透明ジオメトリパスの
//  帯域が全ピクセルで増えるのに対し、既存の空きチャンネルへ詰めれば **帯域増加はゼロ**で、
//  かつ既存チャンネルの精度にも一切影響しない（他の値のビットを削っていないため）。
// ============================================================

// ── セマンティックタグ（render_tag）─────────────────────────

/// セマンティックタグのビット幅。16 種（0..15）まで表現できる。
/// 0 は「タグ無し（既定）」として予約する（旧シーン・未設定アクタがここに落ちる）。
pub const RENDER_TAG_BITS: u32 = 4;
/// セマンティックタグの値マスク（下位 RENDER_TAG_BITS ビット）。
pub const RENDER_TAG_MASK: u8 = (1u16 << RENDER_TAG_BITS) as u8 - 1;
/// タグ未設定を表す既定値（`#[serde(default)]` のゼロ値と一致させること）。
pub const RENDER_TAG_NONE: u8 = 0;

// ── シェーディングモデル ID（shading_model）───────────────────

/// シェーディングモデル ID のビット幅。4 種（0..3）まで表現できる。
pub const SHADING_MODEL_BITS: u32 = 2;
/// シェーディングモデル ID の値マスク（シフト前の下位ビット）。
pub const SHADING_MODEL_MASK: u8 = (1u16 << SHADING_MODEL_BITS) as u8 - 1;
/// パック時のシフト量（タグの直上に置く）。
pub const SHADING_MODEL_SHIFT: u32 = RENDER_TAG_BITS;
/// 既定のシェーディングモデル＝標準 PBR（エンジン実装・アセットでは上書き不可）。
/// ID 1..3 は L3-a のシェーディングアセット（`renderer::shading_asset`）がユーザーの WGSL で
/// 定義する枠で、未定義の ID はこの 0 へフォールバックする。
pub const SHADING_MODEL_DEFAULT_PBR: u8 = 0;

// ── パック結果 ───────────────────────────────────────────────

/// パック後の全ビット幅（= tag + shading model）。
pub const SURFACE_ID_BITS: u32 = RENDER_TAG_BITS + SHADING_MODEL_BITS;
/// パック後に取り得る最大値。
pub const SURFACE_ID_MAX: u32 = (1u32 << SURFACE_ID_BITS) - 1;

/// Rgba16Float（IEEE half）が誤差ゼロで表現できる整数の上限。
/// 仮数 10bit + 暗黙の先頭 1bit ＝ 11bit 精度なので 2^11 = 2048 まで無損失。
/// `SURFACE_ID_MAX` がこれを超えないことをユニットテストで固定する
/// （将来ビット幅を広げるときに、この前提が壊れたら必ずテストが落ちる）。
pub const HALF_FLOAT_EXACT_INT_MAX: u32 = 2048;

/// `render_tag` と `shading_model` を G-Buffer RT3.a に書く 1 個の float へパックする。
///
/// GPU 側（gbuffer_write.wgsl の `pack_surface_id`）と**同一の規約**であること。
/// 入力は各マスクで切り詰めるため、範囲外の値を渡してもビットが隣へ漏れない。
#[inline]
pub fn pack_surface_id(render_tag: u8, shading_model: u8) -> f32 {
    let tag = (render_tag & RENDER_TAG_MASK) as u32;
    let sm  = (shading_model & SHADING_MODEL_MASK) as u32;
    (tag | (sm << SHADING_MODEL_SHIFT)) as f32
}

/// `pack_surface_id` の逆変換。`(render_tag, shading_model)` を返す。
/// CPU 側では主にテスト・デバッグ表示用（実運用の復元はライティングパスが GPU 上で行う）。
#[inline]
pub fn unpack_surface_id(packed: f32) -> (u8, u8) {
    // half float から戻る際の丸め誤差に備えて四捨五入してから整数化する。
    let v = packed.round().max(0.0) as u32;
    let tag = (v as u8) & RENDER_TAG_MASK;
    let sm  = ((v >> SHADING_MODEL_SHIFT) as u8) & SHADING_MODEL_MASK;
    (tag, sm)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// マスク値がビット幅から正しく導出されていることを固定する。
    #[test]
    fn masks_match_bit_widths() {
        assert_eq!(RENDER_TAG_MASK, 0b1111, "タグは 4bit");
        assert_eq!(SHADING_MODEL_MASK, 0b11, "シェーディングモデルは 2bit");
        assert_eq!(SHADING_MODEL_SHIFT, RENDER_TAG_BITS);
        assert_eq!(SURFACE_ID_BITS, 6);
        assert_eq!(SURFACE_ID_MAX, 63);
    }

    /// Rgba16Float に無損失で載る範囲に収まっていること
    /// （ビット幅を広げるときにこの前提が壊れたら落ちる番人）。
    #[test]
    fn packed_surface_id_fits_in_half_float_exactly() {
        assert!(
            SURFACE_ID_MAX < HALF_FLOAT_EXACT_INT_MAX,
            "パック値 {SURFACE_ID_MAX} が half float の無損失整数上限 {HALF_FLOAT_EXACT_INT_MAX} を超えている"
        );
    }

    /// 全組み合わせでパック→アンパックが完全に往復すること。
    #[test]
    fn pack_unpack_roundtrips_for_all_values() {
        for tag in 0..=RENDER_TAG_MASK {
            for sm in 0..=SHADING_MODEL_MASK {
                let packed = pack_surface_id(tag, sm);
                assert!(packed <= SURFACE_ID_MAX as f32);
                assert_eq!(unpack_surface_id(packed), (tag, sm), "tag={tag} sm={sm}");
            }
        }
    }

    /// 範囲外の入力がマスクで切り詰められ、隣のビットを侵食しないこと。
    #[test]
    fn out_of_range_inputs_are_masked_not_overflowed() {
        // タグに 0xFF を渡しても下位 4bit だけが残り、シェーディングモデル域は汚れない。
        let packed = pack_surface_id(0xFF, SHADING_MODEL_DEFAULT_PBR);
        assert_eq!(unpack_surface_id(packed), (RENDER_TAG_MASK, SHADING_MODEL_DEFAULT_PBR));
    }

    /// 既定値（タグ無し・DefaultPBR）のパック結果は 0＝旧シーン／未対応パスの
    /// クリア値（0）と一致すること（後方互換の要）。
    #[test]
    fn default_values_pack_to_zero() {
        assert_eq!(pack_surface_id(RENDER_TAG_NONE, SHADING_MODEL_DEFAULT_PBR), 0.0);
    }
}
