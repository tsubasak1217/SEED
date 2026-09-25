// ============================================================
//  water/sky_fallback.rs — 水面反射パスが走らないフレームの「空の映り込み」（前方描画のフォールバック）
//
//  ## 役割（単一責任）
//  水面パス（water_surface.wgsl）の group1 binding8〜10 に挿す天球の資源
//  （ReflectionSkyUniform の uniform・天球テクスチャのビュー・Repeat サンプラー・ダミー）を持ち、
//  「このフレームに空のフォールバックを使うか」を決めて uniform を書くことだけを担う。
//  サンプルと合成は WGSL 側（`sky_refl_sample` → 反射色・反射強度の差し替え）。
//
//  ## なぜ要るのか（直した不具合）
//  描画品質プリセット `mobile`（前方描画・`water_reflection=false`）では水面反射パスが走らず、
//  反射 RT が黒ダミー（強度 0）になる。水面の色は「深場の色 × 背景の屈折」だけになり、
//  空の色も波の揺らぎも映らない平坦な一色に見えていた。反射パス（SSR / RT）は重いので戻さず、
//  天球を 1 回サンプルするだけの安い代用で「空の色を映すフレネル」と「波法線の揺らぎ」を出す。
//
//  ## デスクトップ（デファード）の見た目を変えない約束
//  反射パスが走るフレームは `fallback_source` が None を返し、無効フラグ（0）の uniform と
//  ダミーのテクスチャが挿さる。WGSL は有効フラグを見て分岐に入らないので、反射パスの結果が
//  そのまま使われる（従来と同じ値）。
// ============================================================

use crate::engine::core::renderer::reflection_sky::{ReflectionSkySource, ReflectionSkyUniform};

/// このフレームに水面へ映す空のフォールバックを使うかを決める【純関数】。
///
/// - `reflection_pass_runs` : このフレームに水面反射パス（SSR / RT）を走らせるか
/// - `sky`                  : このフレームの代表スカイボックス（無ければ None）
///
/// 反射パスが走るなら None（本物の反射を使う）。走らないならスカイボックスをそのまま返す
/// （スカイボックスが無いシーンは None＝従来どおり反射なし）。
/// GPU 資源を持つ型（`ReflectionSkySource`）でもテスト用の値でも同じ規則を通せるよう型を問わない。
pub fn fallback_source<T>(reflection_pass_runs: bool, sky: Option<T>) -> Option<T> {
    if reflection_pass_runs { None } else { sky }
}

/// 水面パスへ書く天球 uniform を決める【純関数】。フォールバックを使わないなら無効値（有効フラグ 0）。
pub fn fallback_uniform(sky: Option<ReflectionSkyUniform>) -> ReflectionSkyUniform {
    sky.unwrap_or_else(ReflectionSkyUniform::disabled)
}

/// 空のフォールバックの GPU 資源（水面パスの group1 binding8〜10 に挿す）。
pub struct WaterSkyFallback {
    /// 天球パラメータの uniform（80B。毎フレーム `update` で書き換える）。
    uniform: wgpu::Buffer,
    /// 天球のサンプラー（経度方向 Repeat・緯度方向 Clamp・線形。反射パスの天球サンプラーと同じ設定）。
    sampler: wgpu::Sampler,
    /// フォールバックを使わないフレームに挿す 1x1 のダミー（有効フラグ 0 なのでサンプルされない）。
    _dummy_tex: wgpu::Texture,
    /// 上のビュー。
    dummy_view: wgpu::TextureView,
}

impl WaterSkyFallback {
    /// 資源を作る（uniform は無効値で初期化する）。
    pub fn new(device: &wgpu::Device) -> Self {
        use wgpu::util::DeviceExt;
        let uniform = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label:    Some("Water Surface Sky Fallback Uniform"),
            contents: bytemuck::bytes_of(&ReflectionSkyUniform::disabled()),
            usage:    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
        });
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Water Surface Sky Fallback Sampler"),
            // equirectangular の経度は巡回するので Repeat（clamp すると経度 0°に縦線が出る）。
            address_mode_u: wgpu::AddressMode::Repeat,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });
        let dummy_tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Water Surface Sky Fallback Dummy"),
            size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          wgpu::TextureFormat::Rgba8Unorm,
            usage:           wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats:    &[],
        });
        let dummy_view = dummy_tex.create_view(&wgpu::TextureViewDescriptor::default());
        Self { uniform, sampler, _dummy_tex: dummy_tex, dummy_view }
    }

    /// このフレームの天球 uniform を書く（`sky` が None なら無効値）。
    pub fn update(&self, queue: &wgpu::Queue, sky: Option<&ReflectionSkySource<'_>>) {
        let u = fallback_uniform(sky.map(|s| s.uniform));
        queue.write_buffer(&self.uniform, 0, bytemuck::bytes_of(&u));
    }

    /// binding8（uniform）。
    pub fn uniform_binding(&self) -> wgpu::BindingResource<'_> {
        self.uniform.as_entire_binding()
    }

    /// binding9（天球テクスチャ）。フォールバックを使わないフレームはダミー。
    pub fn view<'a>(&'a self, sky: Option<&ReflectionSkySource<'a>>) -> &'a wgpu::TextureView {
        sky.map_or(&self.dummy_view, |s| s.view)
    }

    /// binding10（天球サンプラー）。
    pub fn sampler(&self) -> &wgpu::Sampler {
        &self.sampler
    }
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 有効な天球 uniform（テスト用の値。有効フラグ 1・色 0.5）。
    fn enabled_uniform() -> ReflectionSkyUniform {
        let mut u = ReflectionSkyUniform::disabled();
        u.tint_enabled = [0.5, 0.5, 0.5, 1.0];
        u
    }

    /// WGSL の有効フラグのしきい値（sky_reflection_common.wgsl の SKY_REFL_ENABLED_EPS）を読む。
    fn shader_enabled_eps() -> f32 {
        let src = include_str!("../shaders/sky_reflection_common.wgsl");
        let line = src
            .lines()
            .map(str::trim)
            .find(|l| l.starts_with("const SKY_REFL_ENABLED_EPS"))
            .expect("sky_reflection_common.wgsl に SKY_REFL_ENABLED_EPS が無い");
        line.split('=')
            .nth(1)
            .and_then(|r| r.trim().trim_end_matches(';').parse().ok())
            .expect("SKY_REFL_ENABLED_EPS を f32 として読めない")
    }

    /// 反射パスが走るなら、スカイボックスがあってもフォールバックを使わないこと（デスクトップ不変の要）。
    #[test]
    fn no_fallback_when_reflection_pass_runs() {
        assert!(fallback_source(true, Some(enabled_uniform())).is_none());
    }

    /// 反射パスが走らないなら、スカイボックスの値をそのまま使うこと。
    #[test]
    fn fallback_uses_skybox_when_reflection_pass_is_off() {
        let picked = fallback_source(false, Some(enabled_uniform())).expect("フォールバックが選ばれない");
        assert_eq!(bytemuck::bytes_of(&fallback_uniform(Some(picked))), bytemuck::bytes_of(&enabled_uniform()));
    }

    /// スカイボックスが無いシーンは、反射パスが無くても従来どおり反射なし（無効値）であること。
    #[test]
    fn no_skybox_means_disabled_uniform() {
        let none: Option<ReflectionSkyUniform> = None;
        assert!(fallback_source(false, none).is_none());
        assert!(fallback_uniform(None).tint_enabled[3] < shader_enabled_eps(), "無効値が WGSL で有効と判定される");
    }

    /// 有効な uniform の有効フラグが WGSL の判定しきい値を上回ること（フラグの約束が食い違っていないこと）。
    #[test]
    fn enabled_uniform_is_above_shader_threshold() {
        assert!(enabled_uniform().tint_enabled[3] >= shader_enabled_eps());
    }

    /// 水面パスの WGSL が「有効フラグ・水上のときだけ」空で反射を上書きし、強度は水域の反射強度を使うこと。
    /// ここが崩れると、デファード（反射パスあり）の見た目が変わるか、前方描画で空が映らなくなる。
    #[test]
    fn surface_shader_overrides_reflection_only_when_enabled_and_above_water() {
        let src = super::super::resolve_water_shader("water_surface.wgsl");
        assert!(src.contains("if (!underwater && u_surface_sky.tint_enabled.w >= SKY_REFL_ENABLED_EPS) {"),
            "空のフォールバックの門（有効フラグ・水上）が変わっている");
        assert!(src.contains("sky_refl_sample(u_surface_sky, t_surface_sky, s_surface_sky, sky_dir)"),
            "天球のサンプルが反射パスのミス経路と同じ共有関数（sky_refl_sample）を通っていない");
        assert!(src.contains("refl_strength = clamp(p.reflection.x, 0.0, 1.0);"),
            "フォールバックの強度が水域の反射強度（反射パスの出力と同じ値）になっていない");
        // binding の番号は Rust 側の BindGroup（water/mod.rs の prepare）と一致させる。
        for decl in [
            "@group(1) @binding(8) var<uniform> u_surface_sky: ReflectionSkyUniform;",
            "@group(1) @binding(9) var t_surface_sky: texture_2d<f32>;",
            "@group(1) @binding(10) var s_surface_sky: sampler;",
        ] {
            assert!(src.contains(decl), "水面パスの天球の宣言が変わっている: {decl}");
        }
    }
}
