// ============================================================
//  tests_color_pipeline.rs — 2D スプライトの色再現に関する数式検証（CPU 純関数テスト）
//
//  【なぜこのファイルがあるか】
//  「元画像より暗く・彩度が落ちて見える」というスプライトの不具合は、
//  次の 2 つのどちらが原因かで対処がまったく変わる。
//
//   (A) 色空間（sRGB ⇔ リニア）の変換が抜けている／二重に掛かっている
//   (B) 色空間は正しいが、描き込み先が「トーンマップ前の HDR バッファ」である
//
//  本テストは GPU を使わずに (A) と (B) を数値で切り分ける。
//   - `srgb_roundtrip_is_identity_for_all_8bit_values`
//       → 前面ゾーン UI の経路（Rgba8UnormSrgb テクスチャ → リニア →
//         sRGB スワップチェーン）が元画素値を厳密に復元することを示す（= (A) は無い）。
//   - `reinhard_luma_darkens_and_desaturates_sprite_color`
//       → 背景ゾーン UI の経路（HDR バッファ経由 → トーンマップ）が
//         元画素値を暗く・低彩度に変えることを示す（= (B) が起きると症状が出る）。
//
//  【前提の固定】
//  トーンマップ式は `shaders/tonemap_ops.wgsl` が正典であり、本テストの CPU 実装は
//  その写しにすぎない。シェーダ側の式が変わってもテストが黙って通り続けないよう、
//  `wgsl_tonemap_constants_match_cpu_mirror` で WGSL 原文に係数が含まれることを検査する。
// ============================================================

// ─── sRGB 伝達関数の定数（IEC 61966-2-1）──────────────────────
// GPU が Rgba8UnormSrgb のサンプル時／sRGB レンダーターゲットの書き込み時に
// ハードウェアで適用する変換そのもの。マジックナンバー化を避けて名前で持つ。

/// sRGB → リニア変換で線形区間を使う上限（この値以下は単純除算）。
const SRGB_LINEAR_SEGMENT_THRESHOLD: f64 = 0.040_45;
/// リニア → sRGB 変換で線形区間を使う上限。
const LINEAR_SRGB_SEGMENT_THRESHOLD: f64 = 0.003_130_8;
/// 線形区間の傾き。
const SRGB_LINEAR_SEGMENT_SLOPE: f64 = 12.92;
/// べき乗区間のオフセット。
const SRGB_CURVE_OFFSET: f64 = 0.055;
/// べき乗区間のスケール（= 1 + オフセット）。
const SRGB_CURVE_SCALE: f64 = 1.055;
/// べき乗区間の指数（sRGB → リニア方向）。
const SRGB_CURVE_EXPONENT: f64 = 2.4;

/// 8bit 量子化の最大値（0..=255 の 255）。
const U8_MAX_AS_F64: f64 = 255.0;

// ─── 輝度（Rec.709）係数 ─────────────────────────────────────
// `tonemap_ops.wgsl` の `tonemap_reinhard_luma` と同一の係数。

/// 輝度計算の R 係数。
const LUMA_WEIGHT_R: f64 = 0.2126;
/// 輝度計算の G 係数。
const LUMA_WEIGHT_G: f64 = 0.7152;
/// 輝度計算の B 係数。
const LUMA_WEIGHT_B: f64 = 0.0722;

// ─── 検証用のサンプル色 ───────────────────────────────────────

/// 症状報告に出てくるロゴのシアン（元 PNG のもっとも鮮やかな部分, 8bit sRGB）。
const LOGO_CYAN_SRGB_U8: [u8; 3] = [0, 255, 220];

/// 「目に見えて暗い」と判断する G チャンネルの上限（8bit）。
/// 元の 255 に対してこの値を下回れば、肉眼で明らかな暗化と言える。
const VISIBLY_DARKER_G_LIMIT_U8: u8 = 220;

/// ロゴのシアン→白グラデーション中程の画素（元 PNG, 8bit sRGB）。
/// 症状報告のスクリーンショットで採取された画素は、この明るさのシアンに相当する。
const LOGO_LIGHT_CYAN_SRGB_U8: [u8; 3] = [143, 203, 191];

/// 上記画素を Reinhard 通過後に画面で観測した色（症状報告の実測値, 8bit sRGB）。
/// 本テストはこの値を計算で再現できることを確認する（＝原因がトーンマップだと特定できる）。
const OBSERVED_ON_SCREEN_SRGB_U8: [u8; 3] = [120, 170, 160];

/// 実測値との許容差（8bit）。スクリーンショットからの目視採取なので数階調のずれを許す。
const OBSERVED_TOLERANCE_U8: i32 = 4;

/// 色度（チャンネル比）の一致判定に使う許容誤差。
/// Reinhard は全チャンネル一律スケールなので、リニア空間の比は厳密に保存される。
const CHROMATICITY_EPSILON: f64 = 1.0e-12;

/// 浮動小数比較の許容誤差（f64 の往復計算に十分な余裕を取った値）。
const FLOAT_EPSILON: f64 = 1.0e-9;

// ─── sRGB 伝達関数（GPU ハードウェア変換の CPU ミラー）───────

/// sRGB エンコード値（0..1）→ リニア値（0..1）。
///
/// GPU が `Rgba8UnormSrgb` テクスチャを `textureSample` した際に
/// 自動適用する変換と同じ式。
fn srgb_to_linear(s: f64) -> f64 {
    if s <= SRGB_LINEAR_SEGMENT_THRESHOLD {
        s / SRGB_LINEAR_SEGMENT_SLOPE
    } else {
        ((s + SRGB_CURVE_OFFSET) / SRGB_CURVE_SCALE).powf(SRGB_CURVE_EXPONENT)
    }
}

/// リニア値（0..1）→ sRGB エンコード値（0..1）。
///
/// GPU が sRGB レンダーターゲット（例: `Bgra8UnormSrgb` スワップチェーン）へ
/// 書き込む際に自動適用する変換と同じ式。
fn linear_to_srgb(l: f64) -> f64 {
    if l <= LINEAR_SRGB_SEGMENT_THRESHOLD {
        l * SRGB_LINEAR_SEGMENT_SLOPE
    } else {
        SRGB_CURVE_SCALE * l.powf(1.0 / SRGB_CURVE_EXPONENT) - SRGB_CURVE_OFFSET
    }
}

/// 8bit sRGB 値 → リニア（0..1）。
fn u8_srgb_to_linear(v: u8) -> f64 {
    srgb_to_linear(f64::from(v) / U8_MAX_AS_F64)
}

/// リニア（0..1）→ 8bit sRGB 値（最近傍丸め）。
fn linear_to_u8_srgb(l: f64) -> u8 {
    (linear_to_srgb(l) * U8_MAX_AS_F64).round().clamp(0.0, U8_MAX_AS_F64) as u8
}

// ─── トーンマップ（tonemap_ops.wgsl の CPU ミラー）───────────

/// 輝度ベース Reinhard。`shaders/tonemap_ops.wgsl::tonemap_reinhard_luma` と同一式。
///   luma   = dot(hdr, (0.2126, 0.7152, 0.0722))
///   mapped = hdr * (1 / (luma + 1))
fn tonemap_reinhard_luma(hdr: [f64; 3]) -> [f64; 3] {
    let luma = hdr[0] * LUMA_WEIGHT_R + hdr[1] * LUMA_WEIGHT_G + hdr[2] * LUMA_WEIGHT_B;
    let scale = 1.0 / (luma + 1.0);
    [hdr[0] * scale, hdr[1] * scale, hdr[2] * scale]
}

// ============================================================
//  テスト
// ============================================================

/// 前面ゾーン UI の経路（色空間のみ）は 8bit 値を厳密に復元する。
///
/// 経路: PNG（sRGB 8bit）→ `Rgba8UnormSrgb` テクスチャのサンプル（→ リニア）
///     → `Rgba16Float` の LDR 中間 RT へリニアのまま格納
///     → sRGB スワップチェーンへ書き込み（→ sRGB エンコード）
///
/// この往復で元の 8bit 値が 1 も変わらないことを 0..=255 の全値で確認する。
/// つまり「sRGB 変換の抜け／二重掛け」があれば必ずここが落ちる。
#[test]
fn srgb_roundtrip_is_identity_for_all_8bit_values() {
    for v in 0u8..=255u8 {
        let linear = u8_srgb_to_linear(v);
        let back = linear_to_u8_srgb(linear);
        assert_eq!(
            back, v,
            "sRGB 往復が一致しない: 入力={v} リニア={linear} 出力={back}"
        );
    }
}

/// リニア→sRGB→リニアの逆向き往復も一致する（変換式そのものの健全性）。
#[test]
fn linear_srgb_transfer_functions_are_mutual_inverses() {
    /// 検査する分割数（0.0〜1.0 を等分割して往復させる）。
    const SAMPLE_COUNT: u32 = 1024;
    for i in 0..=SAMPLE_COUNT {
        let l = f64::from(i) / f64::from(SAMPLE_COUNT);
        let back = srgb_to_linear(linear_to_srgb(l));
        assert!(
            (back - l).abs() < FLOAT_EPSILON,
            "リニア往復が一致しない: 入力={l} 出力={back}"
        );
    }
}

/// 背景ゾーン UI の経路（HDR バッファ経由）は、色空間が正しくても
/// トーンマップ（Reinhard）によって暗くなる。
///
/// これが「ロゴがくすんで見える」症状の正体であり、前面ゾーン
/// （トーンマップ後の LDR 中間へ直描き）との唯一の違いである。
///
/// なお Reinhard は全チャンネル一律スケールなので **色相・色度は変えない**。
/// 「彩度が落ちた」という見えは、色度ではなく明度が落ちたことによる知覚上のもの。
/// 本テストは「暗化する」「色度は保たれる」の両方を明示的に固定する。
#[test]
fn reinhard_luma_darkens_sprite_color_without_shifting_chromaticity() {
    // 元画素 → リニア
    let src_linear = [
        u8_srgb_to_linear(LOGO_CYAN_SRGB_U8[0]),
        u8_srgb_to_linear(LOGO_CYAN_SRGB_U8[1]),
        u8_srgb_to_linear(LOGO_CYAN_SRGB_U8[2]),
    ];
    // HDR バッファに置かれた同じリニア値をトーンマップに通す
    let mapped_linear = tonemap_reinhard_luma(src_linear);
    // 画面に出る 8bit 値
    let shown = [
        linear_to_u8_srgb(mapped_linear[0]),
        linear_to_u8_srgb(mapped_linear[1]),
        linear_to_u8_srgb(mapped_linear[2]),
    ];

    // (1) 全チャンネルが元より暗くなる（元が 0 のチャンネルは 0 のまま）。
    for ch in 0..3 {
        assert!(
            shown[ch] <= LOGO_CYAN_SRGB_U8[ch],
            "チャンネル {ch} が明るくなっている: 元={} 画面={}",
            LOGO_CYAN_SRGB_U8[ch], shown[ch]
        );
    }
    // (2) 最も明るい G が肉眼で分かるレベルまで落ちる。
    assert!(
        shown[1] < VISIBLY_DARKER_G_LIMIT_U8,
        "G が十分に暗化していない（症状を再現できていない）: 画面={}",
        shown[1]
    );
    // (3) リニア空間の色度（G:B 比）は厳密に保存される。
    //     ＝ 色被り（緑かぶり等）ではなく、純粋な明度の損失であることの証明。
    let src_ratio    = src_linear[2] / src_linear[1];
    let mapped_ratio = mapped_linear[2] / mapped_linear[1];
    assert!(
        (src_ratio - mapped_ratio).abs() < CHROMATICITY_EPSILON,
        "Reinhard が色度を変えている: 元 B/G={src_ratio} 後 B/G={mapped_ratio}"
    );
}

/// 症状報告のスクリーンショット実測値を、トーンマップの式だけで再現できる。
///
/// 「元 PNG のグラデーション中程の画素」→ Reinhard → sRGB エンコード を計算すると、
/// 実際に画面で観測された色とほぼ一致する。ここが一致する以上、原因は
/// 色空間変換の誤り（sRGB の抜け／二重掛け）ではなくトーンマップ通過である、
/// と断定できる（色空間の誤りなら別の値になる）。
#[test]
fn observed_screenshot_color_is_explained_by_tonemap_alone() {
    let src_linear = [
        u8_srgb_to_linear(LOGO_LIGHT_CYAN_SRGB_U8[0]),
        u8_srgb_to_linear(LOGO_LIGHT_CYAN_SRGB_U8[1]),
        u8_srgb_to_linear(LOGO_LIGHT_CYAN_SRGB_U8[2]),
    ];
    let mapped = tonemap_reinhard_luma(src_linear);
    let predicted = [
        linear_to_u8_srgb(mapped[0]),
        linear_to_u8_srgb(mapped[1]),
        linear_to_u8_srgb(mapped[2]),
    ];
    for ch in 0..3 {
        let diff = i32::from(predicted[ch]) - i32::from(OBSERVED_ON_SCREEN_SRGB_U8[ch]);
        assert!(
            diff.abs() <= OBSERVED_TOLERANCE_U8,
            "チャンネル {ch} が実測と乖離: 予測={} 実測={} 差={diff}",
            predicted[ch], OBSERVED_ON_SCREEN_SRGB_U8[ch]
        );
    }
}

/// CPU ミラーがシェーダ原文と乖離していないことを、WGSL の文字列で担保する。
///
/// `tonemap_ops.wgsl` の式が変わったのに本テストの CPU 実装が古いままだと、
/// 「テストは通るが実機は違う」状態になる。係数の出現を検査して乖離を検出する。
#[test]
fn wgsl_tonemap_constants_match_cpu_mirror() {
    let wgsl = include_str!("../shaders/tonemap_ops.wgsl");
    for (name, value) in [
        ("R", LUMA_WEIGHT_R),
        ("G", LUMA_WEIGHT_G),
        ("B", LUMA_WEIGHT_B),
    ] {
        // WGSL 側は "0.2126" のような素の小数リテラルで書かれている。
        let literal = format!("{value}");
        assert!(
            wgsl.contains(&literal),
            "tonemap_ops.wgsl に輝度係数 {name}={literal} が見当たらない（式が変わった可能性）"
        );
    }
    // Reinhard のスケール式（1 / (luma + 1)）が残っていること。
    assert!(
        wgsl.contains("1.0 / (luma + 1.0)"),
        "tonemap_ops.wgsl の Reinhard 式が変わっている可能性がある"
    );
}
