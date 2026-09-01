// ============================================================
//  sky_color_adjust.rs — 空の色調整（色相／彩度／明度／コントラスト）の CPU 側ミラー
//
//  ## 役割（単一責任）
//  WGSL `shaders/sky_reflection_common.wgsl::sky_apply_color_adjust()` と
//  **同一の式**を Rust で持つ。GPU では単体テストが書けないため、
//  「既定値で恒等」「彩度 0 でグレー」「色相 360°で恒等」「コントラストの中間値不変」
//  といった数学的性質をここで固定し、WGSL 側の式を書き換えたら必ず両方直る形にする。
//
//  ## 実行時には使われない（意図的）
//  空の色調整は毎ピクセル GPU で行うので、本モジュールの `apply` は描画経路から呼ばれない。
//  ここにあるのは「WGSL の式の契約」であり、定数（恒等値）だけはランタイムからも
//  参照される（`SKY_COLOR_ADJUST_IDENTITY`）。
//
//  ## 式を変えるときの手順
//  1. `sky_reflection_common.wgsl` の `sky_apply_color_adjust` を直す
//  2. 本ファイルの `apply` を同じに直す
//  3. `cargo test` で性質テスト＋定数一致テストが通ることを確認する
//  この 3 点セットを崩さないこと（片方だけ直すと GPU と CPU の契約が黙って割れる）。
// ============================================================

// ─── 定数（WGSL の SKY_ADJ_* と 1:1。値を変えるときは両方直す）────

/// 色調整の恒等値（x=色相 0° / y=彩度 1 / z=明度 1 / w=コントラスト 1）。
/// この値のとき出力は入力とビット一致する（WGSL 側も同じ分岐で計算を飛ばす）。
pub const SKY_COLOR_ADJUST_IDENTITY: [f32; 4] = [0.0, 1.0, 1.0, 1.0];

/// 「既定値（無変換）とみなす」許容幅（WGSL `SKY_ADJ_EPS`）。
const EPS: f32 = 1.0e-6;
/// 度 → ラジアン（WGSL `SKY_ADJ_DEG_TO_RAD`）。
const DEG_TO_RAD: f32 = 0.017453292519943295;
/// 輝度係数 R（WGSL `SKY_ADJ_LUMA_R`）。
const LUMA_R: f32 = 0.213;
/// 輝度係数 G。
const LUMA_G: f32 = 0.715;
/// 輝度係数 B。
const LUMA_B: f32 = 0.072;
/// 1 - LUMA_R。
const LUMA_R_INV: f32 = 0.787;
/// 1 - LUMA_G。
const LUMA_G_INV: f32 = 0.285;
/// 1 - LUMA_B。
const LUMA_B_INV: f32 = 0.928;
/// 色相回転行列の非輝度項（G 行・R 列）。
const HUE_GR: f32 = 0.143;
/// 同（G 行・G 列）。
const HUE_GG: f32 = 0.140;
/// 同（G 行・B 列）。
const HUE_GB: f32 = 0.283;
/// コントラストの基準点（中間グレー。リニア空間の 0.5）。
const CONTRAST_PIVOT: f32 = 0.5;
/// 色調整後の下限（負値クランプ）。
const MIN_RGB: f32 = 0.0;

// ─── 本体 ────────────────────────────────────────────────────

/// 空の色へ色調整を掛ける（WGSL `sky_apply_color_adjust` の CPU ミラー）。
///
/// - `color` … 天球テクスチャの生の色（tint / intensity を掛ける**前**）。
/// - `adj`   … `[色相シフト(度), 彩度, 明度, コントラスト]`。
///
/// 適用順は 色相 → 彩度 → 明度 → コントラスト。恒等値では入力をそのまま返す。
#[cfg_attr(not(test), allow(dead_code))]
pub fn apply(color: [f32; 3], adj: [f32; 4]) -> [f32; 3] {
    let mut c = color;
    let mut changed = false;

    // 1. 色相シフト（輝度保存の回転行列）。
    if adj[0].abs() > EPS {
        let a = adj[0] * DEG_TO_RAD;
        let (sn, cs) = a.sin_cos();
        let r0 = [
            LUMA_R + cs * LUMA_R_INV - sn * LUMA_R,
            LUMA_G - cs * LUMA_G - sn * LUMA_G,
            LUMA_B - cs * LUMA_B + sn * LUMA_B_INV,
        ];
        let r1 = [
            LUMA_R - cs * LUMA_R + sn * HUE_GR,
            LUMA_G + cs * LUMA_G_INV + sn * HUE_GG,
            LUMA_B - cs * LUMA_B - sn * HUE_GB,
        ];
        let r2 = [
            LUMA_R - cs * LUMA_R - sn * LUMA_R_INV,
            LUMA_G - cs * LUMA_G + sn * LUMA_G,
            LUMA_B + cs * LUMA_B_INV + sn * LUMA_B,
        ];
        let dot = |r: [f32; 3], v: [f32; 3]| r[0] * v[0] + r[1] * v[1] + r[2] * v[2];
        c = [dot(r0, c), dot(r1, c), dot(r2, c)];
        changed = true;
    }

    // 2. 彩度（同輝度グレーとの線形補間。>1 の外挿も許す）。
    if (adj[1] - 1.0).abs() > EPS {
        let luma = c[0] * LUMA_R + c[1] * LUMA_G + c[2] * LUMA_B;
        let s = adj[1];
        c = [
            luma * (1.0 - s) + c[0] * s,
            luma * (1.0 - s) + c[1] * s,
            luma * (1.0 - s) + c[2] * s,
        ];
        changed = true;
    }

    // 3. 明度（乗算）。
    if (adj[2] - 1.0).abs() > EPS {
        c = [c[0] * adj[2], c[1] * adj[2], c[2] * adj[2]];
        changed = true;
    }

    // 4. コントラスト（中間グレー基準の線形補間／外挿）。
    if (adj[3] - 1.0).abs() > EPS {
        let k = adj[3];
        c = [
            (c[0] - CONTRAST_PIVOT) * k + CONTRAST_PIVOT,
            (c[1] - CONTRAST_PIVOT) * k + CONTRAST_PIVOT,
            (c[2] - CONTRAST_PIVOT) * k + CONTRAST_PIVOT,
        ];
        changed = true;
    }

    // 何か掛けたときだけ負値を落とす（既定値のビット一致を壊さないため）。
    if changed {
        c = [c[0].max(MIN_RGB), c[1].max(MIN_RGB), c[2].max(MIN_RGB)];
    }
    c
}

// ============================================================
//  テスト（WGSL 側の式が満たすべき数学的性質を固定する）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 誤差付き比較（浮動小数の丸めを許す比較）。
    fn close(a: [f32; 3], b: [f32; 3], tol: f32) -> bool {
        (0..3).all(|i| (a[i] - b[i]).abs() <= tol)
    }

    /// 既定値（恒等値）では入力と**ビット一致**すること。
    /// 既存シーンの見た目が 1 ビットも変わらないことの根拠テスト。
    #[test]
    fn identity_is_bit_exact() {
        for c in [
            [0.0, 0.0, 0.0],
            [0.25, 0.5, 0.75],
            [1.0, 1.0, 1.0],
            [12345.75, 0.125, 4096.0], // HDR（太陽ディスク級）
            [-0.5, 0.3, 0.1],          // 負値も素通し（クランプは調整時のみ）
        ] {
            assert_eq!(apply(c, SKY_COLOR_ADJUST_IDENTITY), c);
        }
    }

    /// 既定値のままの段は完全に飛ばされること
    /// （明度だけ変えたら色相・彩度・コントラストは 1 ビットも触らない）。
    #[test]
    fn each_stage_is_skipped_at_its_default() {
        let c = [0.3, 0.6, 0.9];
        assert_eq!(apply(c, [0.0, 1.0, 2.0, 1.0]), [0.6, 1.2, 1.8]);
    }

    /// 彩度 0 は完全なグレースケール（R=G=B=輝度）になること。
    #[test]
    fn saturation_zero_is_grayscale() {
        let c = [0.2, 0.8, 0.4];
        let out = apply(c, [0.0, 0.0, 1.0, 1.0]);
        let luma = c[0] * LUMA_R + c[1] * LUMA_G + c[2] * LUMA_B;
        assert!(close(out, [luma, luma, luma], 1e-6), "out={out:?} luma={luma}");
    }

    /// 色相 360°（一周）は恒等に戻ること（回転行列であることの確認）。
    /// 360 は EPS 分岐を通らないので、実際に行列計算が走ったうえでの恒等性を見る。
    #[test]
    fn hue_full_turn_is_identity() {
        let c = [0.2, 0.8, 0.4];
        let out = apply(c, [360.0, 1.0, 1.0, 1.0]);
        assert!(close(out, c, 1e-4), "out={out:?}");
    }

    /// 色相シフトは輝度を（ほぼ）保存すること（輝度保存行列であることの確認）。
    #[test]
    fn hue_shift_preserves_luma() {
        let c = [0.2, 0.8, 0.4];
        let luma = |v: [f32; 3]| v[0] * LUMA_R + v[1] * LUMA_G + v[2] * LUMA_B;
        for deg in [-180.0, -90.0, 30.0, 120.0, 180.0] {
            let out = apply(c, [deg, 1.0, 1.0, 1.0]);
            assert!(
                (luma(out) - luma(c)).abs() < 1e-3,
                "deg={deg} out={out:?} luma差={}",
                luma(out) - luma(c)
            );
        }
    }

    /// コントラストは中間グレー（0.5）を動かさないこと（軸が中間値である根拠）。
    #[test]
    fn contrast_keeps_pivot_fixed() {
        let pivot = [CONTRAST_PIVOT, CONTRAST_PIVOT, CONTRAST_PIVOT];
        for k in [0.0, 0.5, 1.5, 2.0] {
            let out = apply(pivot, [0.0, 1.0, 1.0, k]);
            assert!(close(out, pivot, 1e-6), "k={k} out={out:?}");
        }
    }

    /// コントラスト 0 は全てが中間グレーへ潰れること。
    #[test]
    fn contrast_zero_collapses_to_pivot() {
        let out = apply([0.0, 0.9, 4.0], [0.0, 1.0, 1.0, 0.0]);
        assert!(close(out, [CONTRAST_PIVOT; 3], 1e-6), "out={out:?}");
    }

    /// 調整を掛けたときは負値が出ないこと（Bloom / トーンマップの NaN 源を断つ）。
    #[test]
    fn adjusted_output_is_never_negative() {
        // 暗い色をコントラスト 2 倍すると素の式では負に振れる。
        let out = apply([0.0, 0.1, 0.2], [0.0, 1.0, 1.0, 2.0]);
        assert!(out.iter().all(|v| *v >= 0.0), "out={out:?}");
        // 彩度 2 倍でも同様（低い成分が負へ振れる）。
        let out2 = apply([0.0, 0.9, 0.9], [0.0, 2.0, 1.0, 1.0]);
        assert!(out2.iter().all(|v| *v >= 0.0), "out2={out2:?}");
    }

    /// HDR（1.0 超）でも有限値のまま扱えること（HSV 往復のような 0..1 前提が無いこと）。
    #[test]
    fn hdr_values_stay_finite() {
        let out = apply([10000.0, 5000.0, 1.0], [45.0, 1.5, 1.2, 1.3]);
        assert!(out.iter().all(|v| v.is_finite()), "out={out:?}");
        assert!(out[0] > 1.0, "HDR の高輝度が保たれること out={out:?}");
    }

    /// WGSL 側の定数と本ファイルの定数が一致していること（式ミラーの前提）。
    /// 片方だけ書き換えたら落ちるようにしてある。
    #[test]
    fn wgsl_constants_match_cpu_mirror() {
        let src = include_str!("shaders/sky_reflection_common.wgsl");
        let parse = |name: &str| -> f32 {
            let line = src
                .lines()
                .map(str::trim)
                .find(|l| l.starts_with(&format!("const {name}:")))
                .unwrap_or_else(|| panic!("sky_reflection_common.wgsl に const {name} が無い"));
            let rhs = line.split('=').nth(1).expect("右辺がありません");
            let num: String = rhs
                .trim()
                .chars()
                .take_while(|c| c.is_ascii_digit() || *c == '.' || *c == '-' || *c == 'e')
                .collect();
            num.parse::<f32>()
                .unwrap_or_else(|_| panic!("const {name} を f32 として解釈できない: {num:?}"))
        };
        for (name, expected) in [
            ("SKY_ADJ_EPS", EPS),
            ("SKY_ADJ_DEG_TO_RAD", DEG_TO_RAD),
            ("SKY_ADJ_LUMA_R", LUMA_R),
            ("SKY_ADJ_LUMA_G", LUMA_G),
            ("SKY_ADJ_LUMA_B", LUMA_B),
            ("SKY_ADJ_LUMA_R_INV", LUMA_R_INV),
            ("SKY_ADJ_LUMA_G_INV", LUMA_G_INV),
            ("SKY_ADJ_LUMA_B_INV", LUMA_B_INV),
            ("SKY_ADJ_HUE_GR", HUE_GR),
            ("SKY_ADJ_HUE_GG", HUE_GG),
            ("SKY_ADJ_HUE_GB", HUE_GB),
            ("SKY_ADJ_CONTRAST_PIVOT", CONTRAST_PIVOT),
            ("SKY_ADJ_MIN_RGB", MIN_RGB),
        ] {
            assert_eq!(parse(name), expected, "{name} が CPU ミラーと不一致");
        }
    }

    /// **全ての空サンプル経路が共通関数を通る**ことの構造テスト（要件の核心）。
    ///
    /// 「背景だけ色が変わって反射の空は元の色」を構造的に防ぐため、
    /// 天球テクスチャをサンプルする WGSL の実装箇所を列挙し、
    /// いずれも `sky_apply_color_adjust` を経由していることを固定する。
    ///
    /// 天球テクスチャのサンプルは engine 全体で次の 2 か所しか無い:
    ///   1. `skybox.wgsl::fs_main`                       … 背景描画
    ///   2. `sky_reflection_common.wgsl::sky_refl_sample` … D6 不透明反射 / 水面反射の
    ///      ミス経路（`reflection_common.wgsl::reflection_sky_miss` と
    ///      `water_reflection_common.wgsl::water_refl_skybox` が委譲する）
    /// GI/DDGI のミス経路はシーンのアンビエント色を使い天球を読まないため対象外。
    #[test]
    fn every_sky_sample_site_goes_through_the_shared_adjust() {
        let skybox = include_str!("shaders/skybox.wgsl");
        let shared = include_str!("shaders/sky_reflection_common.wgsl");
        let refl = include_str!("shaders/reflection_common.wgsl");
        let water = include_str!("shaders/water_reflection_common.wgsl");

        // 1. 背景描画が共有関数を呼んでいる。
        assert!(
            skybox.contains("sky_apply_color_adjust(tex, u_skybox.adjust)"),
            "skybox.wgsl の背景描画が共有の色調整を通っていない"
        );
        // 2. 共有サンプル関数が色調整を通している。
        assert!(
            shared.contains("sky_apply_color_adjust(tex_rgb, sky.adjust)"),
            "sky_refl_sample が色調整を通っていない"
        );
        // 3. 反射・水面反射は自前で天球を読まず、共有サンプル関数へ委譲している。
        assert!(
            refl.contains("sky_refl_sample(u_refl_sky, t_refl_sky, s_refl_sky, dir)"),
            "D6 反射のミス経路が sky_refl_sample を経由していない"
        );
        assert!(
            water.contains("sky_refl_sample(wr_sky, t_water_sky, s_water_sky, dir)"),
            "水面反射のミス経路が sky_refl_sample を経由していない"
        );
        // 4. 共有実装以外に色調整の式が複製されていないこと（1 箇所実装の担保）。
        for (name, src) in [("skybox.wgsl", skybox), ("reflection_common.wgsl", refl),
                            ("water_reflection_common.wgsl", water)] {
            assert!(
                !src.contains("fn sky_apply_color_adjust"),
                "{name} に色調整の実装が複製されている（実装は共有 1 本のみ）"
            );
        }
    }
}
