// ============================================================
//  reflection_sky.rs — 反射のミス経路が映すスカイボックス（D6 不透明反射／W5.2 水面反射 共用）
//
//  ## 役割（単一責任）
//  「反射レイが空へ抜けたとき、どの天球テクスチャをどの向きで・どんな色でサンプルするか」
//  という **1 つの入力データ**の型だけを持つ。パイプラインもバッファも持たない
//  （バッファの所有・更新は各反射パス側 = `reflection.rs` / `water_reflection.rs`）。
//
//  ## なぜ水面専用ではなく共用モジュールなのか
//  水面反射（W5.2）と不透明反射（D6）が **同じ方向へ同じ空の色を返す**ことが要件である
//  （食い違うと水際で反射色に不連続な境目が出る）。両者が同じ型・同じ生成関数
//  （`skybox.rs::reflection_sky_source`）・同じ WGSL 関数（`sky_reflection_common.wgsl::
//  sky_refl_sample`）を通ることで、規約のズレが構造的に起きないようにしてある。
//  そのため型名からは水固有の接頭辞（`Water*`）を外してある。
//
//  ## GPU 側の置き場所（uniform か storage かはパス側の都合）
//  レイアウトは 64B（vec4 × 4）で固定だが、どのバッファ種別で読むかはパスごとに違う:
//    ・D6 反射   … group2 に **uniform**（group2 に binding_array は無い）
//    ・水面反射 … group3 に **storage**（group3 にバインドレスのテクスチャ配列が同居し、
//                  WebGPU 制約により binding_array と uniform buffer は同居できない）
//  値・意味・レイアウトはどちらも完全に同一である。
// ============================================================

/// 反射のミス経路が使うスカイボックス uniform（WGSL `ReflectionSkyUniform` と 1:1・64B）。
///
/// **`skybox.rs::SkyboxUniform`（描画用・96B）とは別物**である。あちらは球メッシュを
/// 配置するための行列を持つが、こちらが要るのは「ワールド方向 → 天球ローカル方向」の
/// 逆回転と実効色だけで、平行移動もスケールも意味を持たない。共有すると
/// 「描画用の行列を反射がどう解釈するか」が暗黙の契約になるため、意味の違う 2 本に分けてある。
#[repr(C)]
#[derive(Copy, Clone, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ReflectionSkyUniform {
    /// 逆回転行列の第 1 行（.xyz。.w はパディング）。
    pub rot_inv_0: [f32; 4],
    /// 同 第 2 行。
    pub rot_inv_1: [f32; 4],
    /// 同 第 3 行。
    pub rot_inv_2: [f32; 4],
    /// rgb = tint × intensity ／ **a = 有効フラグ（0 = スカイボックス無し）**。
    pub tint_enabled: [f32; 4],
}

impl ReflectionSkyUniform {
    /// スカイボックスが無いシーン用の中立値（`enabled = 0`）。
    /// これが挿さっている限り WGSL 側は天球を一切サンプルせず、GI へフォールバックする。
    pub fn disabled() -> Self {
        Self {
            rot_inv_0: [1.0, 0.0, 0.0, 0.0],
            rot_inv_1: [0.0, 1.0, 0.0, 0.0],
            rot_inv_2: [0.0, 0.0, 1.0, 0.0],
            tint_enabled: [0.0, 0.0, 0.0, 0.0],
        }
    }
}

/// このフレームに反射へ映す代表スカイボックス（`SkyboxSystem` が組み立てて渡す）。
pub struct ReflectionSkySource<'a> {
    /// equirectangular 天球テクスチャのビュー（LDR sRGB / HDR f16 のどちらもあり得る）。
    pub view: &'a wgpu::TextureView,
    /// GPU へ書く uniform（逆回転＋実効色＋有効フラグ）。
    pub uniform: ReflectionSkyUniform,
}

// ============================================================
//  レイアウト契約テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// `ReflectionSkyUniform` は 64B（WGSL の `ReflectionSkyUniform` = vec4 × 4 と一致）。
    /// ここがズレると uniform／storage どちらの読み口でも値が化ける。
    #[test]
    fn reflection_sky_uniform_is_64_bytes() {
        assert_eq!(std::mem::size_of::<ReflectionSkyUniform>(), 64);
    }

    /// 既定（スカイボックス無し）は有効フラグ 0 であること。
    /// ここが 1 だと、天球テクスチャの代わりに挿さるダミー 1x1 の色が反射に出てしまう。
    #[test]
    fn disabled_sky_has_zero_enabled_flag() {
        assert_eq!(ReflectionSkyUniform::disabled().tint_enabled[3], 0.0);
    }

    /// WGSL 共有モジュールの equirect 換算係数が数学定数と一致すること
    /// （`skybox.wgsl` の描画式・水面反射・D6 反射がすべてこの 1 本を使う）。
    #[test]
    fn shared_wgsl_equirect_constants_match_math() {
        let src = include_str!("shaders/sky_reflection_common.wgsl");
        let parse = |name: &str| -> f32 {
            let line = src
                .lines()
                .map(str::trim)
                .find(|l| l.starts_with(&format!("const {name}")))
                .unwrap_or_else(|| {
                    panic!("sky_reflection_common.wgsl に const {name} が見つかりません")
                });
            let rhs = line.split('=').nth(1).expect("右辺がありません");
            let num: String = rhs
                .trim()
                .chars()
                .take_while(|c| c.is_ascii_digit() || *c == '.')
                .collect();
            num.parse::<f32>()
                .unwrap_or_else(|_| panic!("const {name} を f32 として解釈できません: {num:?}"))
        };
        let inv_2pi = parse("SKY_REFL_INV_2PI");
        let inv_pi = parse("SKY_REFL_INV_PI");
        assert!(
            (inv_2pi - 1.0 / (2.0 * std::f32::consts::PI)).abs() < 1e-7,
            "SKY_REFL_INV_2PI({inv_2pi}) は 1/(2π) であること"
        );
        assert!(
            (inv_pi - 1.0 / std::f32::consts::PI).abs() < 1e-7,
            "SKY_REFL_INV_PI({inv_pi}) は 1/π であること"
        );
    }
}
