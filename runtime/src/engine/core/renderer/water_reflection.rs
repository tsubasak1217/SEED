// ============================================================
//  water_reflection.rs — 水面反射パスのリソース（Phase W5.2）
//
//  ## 役割（単一責任）
//  「水面ピクセルの反射色を 1 枚のフル解像度 HDR テクスチャへ焼く」ための
//  パイプラインと BindGroup 生成だけを持つ。**いつ・どの順で描くかはフレーム側**
//  （`frame_renderer.rs`）の責務で、本モジュールはパスの記録を行わない
//  （`caustics.rs` / `ssgi.rs` と同じ分担）。
//
//  ## なぜ独立パスなのか
//  水面パイプライン（`water_surface.wgsl`）は group0〜2 を使い切っており、
//  RT 反射に必要な TLAS・ライト・GI プローブ・シーンカラー・深度を足すと
//  **バインドグループ上限 5 を超える**。反射だけを別パスへ切り出し、
//  水面シェーダは結果 RT を 1 枚 `textureLoad` するだけにしてある。
//  D6（不透明の RT 反射 → 合成）とまったく同じ分担である。
//
//  ## 2 本のパイプライン（RT / SSR）
//    ・`rt`  … TLAS へレイを飛ばすハイブリッド反射（`reflection_rt.wgsl` の流用）。
//              RAY_QUERY 対応 GPU でのみ構築されるので `Option`。
//    ・`ssr` … スクリーンスペースマーチ（常に構築）。RT 非対応 GPU・RT を使わない
//              設定でのフォールバックで、**出力の規約は RT 版と完全に同一**
//              （rgb = 反射色 / a = 反射強度）。水面パスは両者を区別しない。
//  この 2 本立てが「RT 非対応 GPU でも水面反射が出る」ことの根拠である。
//
//  ## パスの位置づけ（順序制約）
//    不透明 Deferred ライティング → 反射（D6）→ メインパス（スカイボックス・不透明）
//      → **屈折背景グラブ**（シーン HDR のコピー）
//      → **本パス**（グラブ＋深度＋TLAS/GI → 水面反射 RT）
//      → 水面パス（反射 RT を 1 枚読んでフレネル合成）
//  グラブの後に置くのが要点である:
//    ・グラブには **スカイボックスが入っている**（メインパスで描画済み）。水面反射で
//      いちばん重要な「空が映る」がこれで成立する。反射パスを D6 と同じ位置
//      （メインパス前）へ置くと空の無いシーンを映すことになる。
//    ・グラブは **シーン HDR のコピー**なので、本パスが読んでいる間に scene_hdr が
//      アタッチされていても読み書き競合が起きない（コピーは 1 フレーム 1 回のまま）。
//    ・水面自身はまだ描かれていない＝反射が水面を映す自己参照が構造的に起きない。
//
//  ## BindGroup を水面パスから使い回さない理由
//  group1（水パラメータ＋岸波）・group2（波紋の場）に挿すリソースは
//  `WaterRenderer` が持つものと同一だが、**BindGroupLayout はパイプラインごとに違う**
//  （WGSL リフレクションが宣言済み binding の集合から導出するため）。
//  ID パス・コースティクスパスが既に同じ理由で専用 BindGroup を持っている。
// ============================================================

use super::ddgi::GiResources;
use super::pipeline::get_shader_source;
use super::pipeline_config::RenderPipelineBuilder;

/// 水面反射 RT の RtPool 登録名（`post::rt_pool` の小文字スネーク流儀）。
pub const RT_WATER_REFLECTION_NAME: &str = "water_reflection";

/// 水面反射 RT のフォーマット（HDR。反射色は露出前の輝度なので f16 が要る）。
/// **`pipelines/water_reflection_*.toml` の `color_format` と一致必須。**
pub const WATER_REFLECTION_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;

/// 本パスのシェーダソース解決（パイプライン生成とテストが共有する唯一の窓口）。
///
/// 高さ場・水面共有モジュールの実体は water モジュールが持つので委譲する
/// （`include_str!` を 2 箇所に書くと参照が二重管理になる。caustics.rs と同じ流儀）。
fn resolve_water_reflection_shader(name: &str) -> &'static str {
    match name {
        "water_height_field.wgsl" => super::water::resolve_water_shader(name),
        other                     => get_shader_source(other),
    }
}

// ============================================================
//  WaterReflectionPipelines
// ============================================================

/// 水面反射パイプライン一式。起動時に 1 回構築して以降は不変。
pub struct WaterReflectionPipelines {
    /// SSR パイプライン（RAY_QUERY 不要・常に構築）。
    ssr: wgpu::RenderPipeline,
    /// RT パイプライン（RAY_QUERY 必須・対応 GPU でのみ `Some`）。
    rt: Option<wgpu::RenderPipeline>,
    /// SSR の group1（水パラメータ＋ショアフィールド）レイアウト。
    ssr_params_bgl: wgpu::BindGroupLayout,
    /// SSR の group2（波紋の場）レイアウト。
    ssr_field_bgl: wgpu::BindGroupLayout,
    /// SSR の group3（グラブ＋サンプラー＋深度）レイアウト。
    ssr_scene_bgl: wgpu::BindGroupLayout,
    /// SSR の group4（GI＋ライトメタ）レイアウト。
    ssr_gi_bgl: wgpu::BindGroupLayout,
    /// RT の group1（水パラメータ＋ショアフィールド）レイアウト（RT 構築時のみ）。
    rt_params_bgl: Option<wgpu::BindGroupLayout>,
    /// RT の group2（波紋の場）レイアウト（同上）。
    rt_field_bgl: Option<wgpu::BindGroupLayout>,
    /// RT の group3（グラブ＋サンプラー＋深度）レイアウト（同上）。
    rt_scene_bgl: Option<wgpu::BindGroupLayout>,
    /// RT の group4（GI＋ライトメタ＋ライト配列＋TLAS＋平均アルベド）レイアウト（同上）。
    rt_gi_bgl: Option<wgpu::BindGroupLayout>,
    /// グラブ・GI で共用するリニア clamp サンプラー。
    sampler: wgpu::Sampler,
    /// 反射パスが走らないフレームに水面パスの反射スロットを埋めるダミー 1x1（黒・永続）。
    #[allow(dead_code)]
    dummy_tex: wgpu::Texture,
    /// 上のビュー。**全成分 0＝A（反射強度）0＝反射寄与なし**が中立値なので、
    /// これを挿すだけで「反射機能 OFF」を表現できる（分岐を増やさない）。
    pub dummy_view: wgpu::TextureView,
}

impl WaterReflectionPipelines {
    /// パイプラインとダミーリソースを構築する。
    ///
    /// `queue` はダミー 1x1 を黒で初期化するために使う（未初期化メモリを読ませない）。
    pub fn new(
        device: &wgpu::Device,
        queue:  &wgpu::Queue,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        // ── SSR（常に構築）─────────────────────────────────────
        let (ssr, mut ssr_bgls) = RenderPipelineBuilder::new(
            device,
            include_str!("pipelines/water_reflection_ssr.toml"),
            WATER_REFLECTION_FORMAT,
            super::DEPTH_FORMAT,
        )
        .with_label("Water Reflection SSR")
        .with_cache(cache)
        .build(resolve_water_reflection_shader);
        assert!(ssr_bgls.len() >= 5,
            "water_reflection_ssr は group0(カメラ)/group1(水パラメータ)/group2(波紋の場)/\
             group3(グラブ＋深度)/group4(GI) を宣言すること");
        // **remove は添字がずれるので大きい方から取る**（water/mod.rs と同じ流儀）。
        let ssr_gi_bgl     = ssr_bgls.remove(4);
        let ssr_scene_bgl  = ssr_bgls.remove(3);
        let ssr_field_bgl  = ssr_bgls.remove(2);
        let ssr_params_bgl = ssr_bgls.remove(1);

        // ── RT（RAY_QUERY 対応 GPU のみ）────────────────────────
        // 非対応 GPU では `acceleration_structure` を含むモジュール生成自体が失敗するため、
        // **パイプラインごと作らない**（シェーダ内分岐では回避できない）。
        let (rt, rt_params_bgl, rt_field_bgl, rt_scene_bgl, rt_gi_bgl) =
            if super::rt_shadow::rt_shadows_supported() {
                let (pipeline, mut bgls) = RenderPipelineBuilder::new(
                    device,
                    include_str!("pipelines/water_reflection_rt.toml"),
                    WATER_REFLECTION_FORMAT,
                    super::DEPTH_FORMAT,
                )
                .with_label("Water Reflection RT")
                .with_cache(cache)
                .build(resolve_water_reflection_shader);
                assert!(bgls.len() >= 5,
                    "water_reflection_rt は group0..group4 を宣言すること");
                let gi     = bgls.remove(4);
                let scene  = bgls.remove(3);
                let field  = bgls.remove(2);
                let params = bgls.remove(1);
                (Some(pipeline), Some(params), Some(field), Some(scene), Some(gi))
            } else {
                (None, None, None, None, None)
            };

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Water Reflection Sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        // ── 機能 OFF 用のダミー 1x1（黒＝A も 0＝反射寄与ゼロ）──────
        let dummy_tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Water Reflection Dummy 1x1"),
            size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          WATER_REFLECTION_FORMAT,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats:    &[],
        });
        /// Rgba16Float 1 テクセルのバイト数（4 チャネル × f16）。
        const WATER_REFLECTION_TEXEL_BYTES: u32 = 8;
        let black = [0u8; WATER_REFLECTION_TEXEL_BYTES as usize];
        queue.write_texture(
            dummy_tex.as_image_copy(),
            &black,
            wgpu::TexelCopyBufferLayout {
                offset:         0,
                bytes_per_row:  Some(WATER_REFLECTION_TEXEL_BYTES),
                rows_per_image: Some(1),
            },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        );
        let dummy_view = dummy_tex.create_view(&wgpu::TextureViewDescriptor::default());

        Self {
            ssr, rt,
            ssr_params_bgl, ssr_field_bgl, ssr_scene_bgl, ssr_gi_bgl,
            rt_params_bgl, rt_field_bgl, rt_scene_bgl, rt_gi_bgl,
            sampler, dummy_tex, dummy_view,
        }
    }

    /// RT 反射パイプラインを持っているか（＝RAY_QUERY 対応 GPU か）。
    pub fn has_rt(&self) -> bool {
        self.rt.is_some()
    }

    /// group1（水パラメータ＋ショアフィールド）の BindGroup を作る。
    ///
    /// リソースは `WaterRenderer` のものをそのまま挿す（同じ水域を同じ高さ場で評価するため）。
    /// `use_rt` はこのフレームに使うパイプラインの別（レイアウトが違うので取り違えると bind に失敗する）。
    /// 水パラメータが未確保のフレーム（`prepare` が false）は `None`。
    pub fn create_params_bg(
        &self,
        device: &wgpu::Device,
        water:  &super::water::WaterRenderer,
        use_rt: bool,
    ) -> Option<wgpu::BindGroup> {
        let params = water.params_buffer()?;
        let layout = self.params_bgl(use_rt);
        Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Reflection Params BG (group 1)"),
            layout,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: params.as_entire_binding() },
                wgpu::BindGroupEntry {
                    binding: 4,
                    resource: wgpu::BindingResource::TextureView(water.shore_texture_view()),
                },
                wgpu::BindGroupEntry {
                    binding: 5,
                    resource: wgpu::BindingResource::Sampler(water.shore_sampler()),
                },
            ],
        }))
    }

    /// group2（波紋・航跡の場）の BindGroup を作る。
    ///
    /// 場が未構築のフレーム（`field = None`）は `WaterRenderer` のゼロフォールバックを挿す
    /// （`inv_extent = 0` なので波紋サンプルは常に窓外扱い＝寄与ゼロ）。
    pub fn create_field_bg(
        &self,
        device: &wgpu::Device,
        water:  &super::water::WaterRenderer,
        field:  Option<&super::interaction::InteractionFieldRenderer>,
        use_rt: bool,
    ) -> wgpu::BindGroup {
        let (view, sampler, uniform) = match field {
            Some(f) => (f.field_view(), f.field_sampler(), f.field_uniform_buffer()),
            None    => (
                water.fallback_field_view(),
                water.fallback_sampler(),
                water.fallback_field_uniform(),
            ),
        };
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Reflection Ripple Field BG (group 2)"),
            layout:  self.field_bgl(use_rt),
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(view) },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(sampler) },
                wgpu::BindGroupEntry { binding: 2, resource: uniform.as_entire_binding() },
            ],
        })
    }

    /// group3（シーンカラーのグラブ＋サンプラー＋シーン深度）の BindGroup を作る。
    ///
    /// `grab_view` は `WaterRenderer::grab_view()`（屈折の背景と同一テクスチャ）。
    /// `depth_view` は共有深度の **DepthOnly ビュー**（本パスは深度アタッチメントを持たない）。
    pub fn create_scene_bg(
        &self,
        device:     &wgpu::Device,
        grab_view:  &wgpu::TextureView,
        depth_view: &wgpu::TextureView,
        use_rt:     bool,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Reflection Scene BG (group 3)"),
            layout:  self.scene_bgl(use_rt),
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(grab_view) },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(&self.sampler) },
                wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(depth_view) },
            ],
        })
    }

    /// group4（GI プローブ＋ライトメタ）の BindGroup を作る（**SSR 用**）。
    pub fn create_gi_bg(
        &self,
        device: &wgpu::Device,
        gi:     &GiResources,
        meta:   &wgpu::Buffer,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Reflection GI BG (group 4, SSR)"),
            layout:  &self.ssr_gi_bgl,
            entries: &Self::gi_entries(gi, &self.sampler, meta),
        })
    }

    /// group4（GI＋ライトメタ＋ライト配列＋TLAS＋平均アルベド）の BindGroup を作る（**RT 用**）。
    ///
    /// RT パイプラインが構築されていない GPU では呼んではならない（`has_rt` で判定する）。
    pub fn create_rt_gi_bg(
        &self,
        device: &wgpu::Device,
        gi:     &GiResources,
        meta:   &wgpu::Buffer,
        lights: &wgpu::Buffer,
        tlas:   &wgpu::Tlas,
        albedo: &wgpu::Buffer,
    ) -> wgpu::BindGroup {
        let layout = self.rt_gi_bgl.as_ref()
            .expect("water_reflection: RT パイプライン未構築の GPU で RT BindGroup を作ろうとした");
        // 共通 5 エントリの後ろへ RT 専用の 3 本を足す（clone を避けるため Vec で組む）。
        let mut entries = Self::gi_entries(gi, &self.sampler, meta);
        entries.push(wgpu::BindGroupEntry { binding: 5, resource: lights.as_entire_binding() });
        entries.push(wgpu::BindGroupEntry {
            binding: 6,
            resource: wgpu::BindingResource::AccelerationStructure(tlas),
        });
        entries.push(wgpu::BindGroupEntry { binding: 7, resource: albedo.as_entire_binding() });
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Reflection GI BG (group 4, RT)"),
            layout,
            entries: &entries,
        })
    }

    /// group4 の共通 5 エントリ（GI パラメータ・照度・可視性・サンプラー・ライトメタ）。
    fn gi_entries<'a>(
        gi:      &'a GiResources,
        sampler: &'a wgpu::Sampler,
        meta:    &'a wgpu::Buffer,
    ) -> Vec<wgpu::BindGroupEntry<'a>> {
        vec![
            wgpu::BindGroupEntry { binding: 0, resource: gi.params_buffer().as_entire_binding() },
            wgpu::BindGroupEntry {
                binding: 1,
                resource: wgpu::BindingResource::TextureView(gi.irradiance_view()),
            },
            wgpu::BindGroupEntry {
                binding: 2,
                resource: wgpu::BindingResource::TextureView(gi.visibility_view()),
            },
            wgpu::BindGroupEntry { binding: 3, resource: wgpu::BindingResource::Sampler(sampler) },
            wgpu::BindGroupEntry { binding: 4, resource: meta.as_entire_binding() },
        ]
    }


    /// 開始済みの水面反射パスへ水面の格子を記録する（バケット分割は `WaterRenderer` が持つ）。
    ///
    /// `use_rt` が true かつ RT パイプラインがあれば RT 版、さもなくば SSR 版を使う
    /// （**BindGroup も同じ `use_rt` で作ったものを渡すこと**。レイアウトが違う）。
    pub fn draw<'p>(
        &'p self,
        pass:      &mut wgpu::RenderPass<'p>,
        water:     &'p super::water::WaterRenderer,
        camera_bg: &'p wgpu::BindGroup,
        params_bg: &'p wgpu::BindGroup,
        field_bg:  &'p wgpu::BindGroup,
        scene_bg:  &'p wgpu::BindGroup,
        gi_bg:     &'p wgpu::BindGroup,
        use_rt:    bool,
    ) {
        let pipeline = match (use_rt, self.rt.as_ref()) {
            (true, Some(rt)) => rt,
            _                => &self.ssr,
        };
        pass.set_pipeline(pipeline);
        pass.set_bind_group(0, camera_bg, &[]);
        pass.set_bind_group(1, params_bg, &[]);
        pass.set_bind_group(2, field_bg,  &[]);
        pass.set_bind_group(3, scene_bg,  &[]);
        pass.set_bind_group(4, gi_bg,     &[]);
        water.draw_grid_buckets(pass);
    }

    // ── レイアウト選択（RT / SSR でパイプラインが違う＝BGL も別物）──

    fn params_bgl(&self, use_rt: bool) -> &wgpu::BindGroupLayout {
        match (use_rt, self.rt_params_bgl.as_ref()) {
            (true, Some(l)) => l,
            _               => &self.ssr_params_bgl,
        }
    }
    fn field_bgl(&self, use_rt: bool) -> &wgpu::BindGroupLayout {
        match (use_rt, self.rt_field_bgl.as_ref()) {
            (true, Some(l)) => l,
            _               => &self.ssr_field_bgl,
        }
    }
    fn scene_bgl(&self, use_rt: bool) -> &wgpu::BindGroupLayout {
        match (use_rt, self.rt_scene_bgl.as_ref()) {
            (true, Some(l)) => l,
            _               => &self.ssr_scene_bgl,
        }
    }
}

// ============================================================
//  テスト（WGSL 静的検証・TOML 照合・規約の固定）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// TOML の `shader_sources` 順に連結した「実際にコンパイルされる WGSL」を返す。
    /// 連結順の正典は TOML なので、テストもそこから組み立てる（caustics.rs と同流儀）。
    fn joined(toml_src: &str) -> String {
        let cfg: crate::engine::core::renderer::pipeline_config::PipelineConfig =
            toml::from_str(toml_src).expect("水面反射のパイプライン TOML が読めない");
        cfg.shader_sources.iter()
            .map(|n| resolve_water_reflection_shader(n))
            .collect::<Vec<_>>()
            .join("\n")
    }
    fn rt_src()  -> String { joined(include_str!("pipelines/water_reflection_rt.toml")) }
    fn ssr_src() -> String { joined(include_str!("pipelines/water_reflection_ssr.toml")) }

    /// SSR 変種（RAY_QUERY 不要）を naga で parse + validate する。
    /// **RAY_QUERY capability を与えずに通ること**が「RT 非対応 GPU で動く」の根拠である。
    #[test]
    fn water_reflection_ssr_shader_parses_and_validates() {
        let src = ssr_src();
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[water_reflection_ssr] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        v.validate(&module)
            .unwrap_or_else(|e| panic!("[water_reflection_ssr] validate 失敗: {e:?}"));
    }

    /// RT 変種（RAY_QUERY 必須）を naga で parse + validate する。
    #[test]
    fn water_reflection_rt_shader_parses_and_validates() {
        let src = rt_src();
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[water_reflection_rt] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::RAY_QUERY,
        );
        v.validate(&module)
            .unwrap_or_else(|e| panic!("[water_reflection_rt] validate 失敗: {e:?}"));
    }

    /// TOML のエントリポイント名・出力設定がシェーダ／Rust 側の前提と一致すること。
    #[test]
    fn water_reflection_toml_entries_match_shader() {
        for toml_src in [
            include_str!("pipelines/water_reflection_rt.toml"),
            include_str!("pipelines/water_reflection_ssr.toml"),
        ] {
            assert!(toml_src.contains(r#"vertex_entry    = "vs_water_reflection""#));
            assert!(toml_src.contains(r#"fragment_entry  = "fs_water_reflection""#));
            // 深度アタッチメントを持たない（サンプルして手動深度テスト）。
            assert!(toml_src.contains("no_depth        = true"));
            // 出力は HDR。Rust 側 WATER_REFLECTION_FORMAT と一致必須。
            assert!(toml_src.contains(r#"color_format    = "Rgba16Float""#));
            // 頂点段が高さ場を読むので group1/2 を VERTEX 可視にする必要がある。
            assert!(toml_src.contains("vertex_visible_groups = [1, 2]"));
            // 高さ場の共有モジュールを必ず連結する（独自波の新造を禁じる契約）。
            assert!(toml_src.contains("water_height_field.wgsl"),
                "高さ場の共有モジュールが連結されていない（＝独自波を作っている疑い）");
        }
        assert_eq!(WATER_REFLECTION_FORMAT, wgpu::TextureFormat::Rgba16Float);
        // 両変種が同じエントリポイント名を持つ（Rust 側が同じ描画手続きを使える根拠）。
        assert!(rt_src().contains("fn fs_water_reflection("));
        assert!(ssr_src().contains("fn fs_water_reflection("));
        assert!(rt_src().contains("fn vs_water_reflection("));
    }

    /// 水面パスとまったく同じ頂点生成関数を呼ぶこと（Phase W5.2 の設計の芯）。
    ///
    /// ここが独自実装に分岐すると、反射 RT と水面パスのラスタライズがズレて
    /// 「水面の縁だけ反射が出ない／隣の水面の反射を拾う」という気づきにくい不具合になる。
    /// 水面パスが `textureLoad`（補間なしの 1:1 読み出し）で結果を拾えるのは
    /// 両者のカバレッジが完全一致していることが前提である。
    #[test]
    fn water_reflection_shares_the_vertex_generation_with_surface() {
        let common = get_shader_source("water_reflection_common.wgsl");
        assert!(common.contains("water_grid_vertex(p, vi, u_camera.time)"),
            "共有の格子頂点生成を呼んでいない（水面パスと形状がズレる）");
        assert!(common.contains("water_surface_gradient(p, in.world_pos.xz"),
            "共有の合成勾配（波法線）を使っていない（見えている波と反射する波がズレる）");
        // 水面パス側は textureLoad で 1:1 に読む（サンプラー補間に頼らない）。
        assert!(super::super::water::resolve_water_shader("water_surface.wgsl")
                .contains("textureLoad(t_water_reflection"),
            "水面パスが反射 RT を textureLoad で読んでいない");
    }

    /// 反射 RT の A（アルファ）は「反射強度」であり、**0 が反射なし**であること。
    ///
    /// パスが走らなかったフレームは RT がクリア値 0／ダミー 1x1（黒）になる。
    /// A=0 が「反射寄与ゼロ」を意味するからこそ、機能 OFF・非対応 GPU・水域の
    /// `reflection_intensity=0` の 3 経路すべてが分岐なしで安全に無効化される。
    #[test]
    fn reflection_alpha_is_the_intensity_and_zero_disables() {
        for src in [
            get_shader_source("water_reflection_rt.wgsl"),
            get_shader_source("water_reflection_ssr.wgsl"),
        ] {
            assert!(src.contains("return vec4<f32>(reflected, s.intensity);"),
                "反射色と強度の出力規約（rgb=色 / a=強度）が変わっている");
            assert!(src.contains("if s.intensity <= 0.0 {"),
                "強度 0 の早期リターン（レイを飛ばさない）が消えている");
        }
        // 水面パス側は a を重みとして掛ける（0 なら従来どおり反射なしの水になる）。
        assert!(super::super::water::resolve_water_shader("water_surface.wgsl")
                .contains("fresnel * clamp(reflection.a, 0.0, 1.0)"),
            "水面パスが反射強度（a）を重みとして使っていない");
    }

    /// **GPU 実機でのパイプライン生成**が通ること（`--ignored` で実行する検証用）。
    ///
    /// naga の静的検証（上のテスト群）は WGSL の妥当性しか見ない。本パス固有の
    /// 実機でしか出ない失敗要因が 2 つあるので、ここだけは実 GPU が要る:
    ///   ① **バインドのステージ可視性**（`vertex_visible_groups` を忘れると
    ///      「頂点段から見えない binding を使っている」で生成が落ちる。W5.1 の前例）
    ///   ② **`max_bind_groups`**（本パスは group0〜4 の 5 個＝上限ちょうどを使う）
    /// そのため `Limits::default()`（max_bind_groups=4）ではなく、
    /// 本体と同じ 5 を要求したデバイスで検証する。
    ///
    /// RT 変種はアダプタが RAY_QUERY に対応していれば**併せて検証する**
    /// （`rt_shadows_supported()` は本体が起動時に立てるグローバルなので、ここで
    /// アダプタの実対応に合わせて設定してから構築し、後で元へ戻す）。
    /// 対応していない環境では SSR 変種だけの検証になる（RT 変種の WGSL 妥当性は
    /// 上の naga テストが RAY_QUERY capability 付きで見ている）。
    ///
    /// 実行: `cargo test water_reflection::tests::water_reflection_pipelines_build_on_gpu -- --ignored --nocapture`
    #[test]
    #[ignore = "実 GPU が必要。--ignored で実行する"]
    fn water_reflection_pipelines_build_on_gpu() {
        let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor::default());
        let Ok(adapter) = pollster::block_on(instance.request_adapter(
            &wgpu::RequestAdapterOptions::default())) else {
            eprintln!("[water_reflection] GPU アダプタが見つからないため検証をスキップ");
            return;
        };
        // アダプタが RAY_QUERY に対応していれば RT 変種も検証対象に含める。
        let rt_feats = wgpu::Features::EXPERIMENTAL_RAY_QUERY
            | wgpu::Features::EXPERIMENTAL_RAY_TRACING_ACCELERATION_STRUCTURE;
        let has_rt = adapter.features().contains(rt_feats);
        let (device, queue) = pollster::block_on(adapter.request_device(
            &wgpu::DeviceDescriptor {
                required_features: if has_rt { rt_feats } else { wgpu::Features::empty() },
                required_limits: wgpu::Limits {
                    // 本体（renderer/mod.rs）と同じ要求。本パスは group0〜4 を使う。
                    max_bind_groups: 5,
                    ..wgpu::Limits::default()
                },
                ..Default::default()
            }))
            .expect("デバイス生成に失敗");

        // RT 対応フラグは他テストと共有のグローバルなので、必ず元へ戻す。
        let saved = super::super::rt_shadow::rt_shadows_supported();
        super::super::rt_shadow::set_rt_shadows_supported(has_rt);
        // 生成そのものが検証（失敗すれば wgpu がパニック／エラーを出す）。
        let pipelines = WaterReflectionPipelines::new(&device, &queue, None);
        assert_eq!(pipelines.has_rt(), has_rt,
            "RT 対応アダプタなら RT 変種が、非対応なら SSR 変種だけが構築されること");

        // ── group3（グラブ＋サンプラー＋深度）の BindGroup 生成まで検証する ──
        // BGL は WGSL リフレクション由来なので、Rust 側の `entries` と
        // **binding 番号・種別（float テクスチャ / サンプラー / depth テクスチャ）**が
        // 食い違うとここで初めて落ちる（パイプライン生成だけでは検出できない）。
        // 実行時に water_gate が立った最初のフレームで初めて分かる類の事故なので、
        // 一番間違えやすいこのグループだけでも静的に押さえておく。
        let make_tex = |format: wgpu::TextureFormat, usage: wgpu::TextureUsages| {
            device.create_texture(&wgpu::TextureDescriptor {
                label:           Some("water_reflection test texture"),
                size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
                mip_level_count: 1,
                sample_count:    1,
                dimension:       wgpu::TextureDimension::D2,
                format,
                usage,
                view_formats:    &[],
            })
        };
        let color = make_tex(WATER_REFLECTION_FORMAT, wgpu::TextureUsages::TEXTURE_BINDING);
        let depth = make_tex(crate::engine::core::renderer::DEPTH_FORMAT, wgpu::TextureUsages::TEXTURE_BINDING);
        let color_view = color.create_view(&wgpu::TextureViewDescriptor::default());
        let depth_view = depth.create_view(&wgpu::TextureViewDescriptor {
            label:  Some("water_reflection test depth-only view"),
            aspect: wgpu::TextureAspect::DepthOnly,
            ..Default::default()
        });
        for use_rt in [false, has_rt] {
            let _bg = pipelines.create_scene_bg(&device, &color_view, &depth_view, use_rt);
        }

        let _ = device.poll(wgpu::PollType::Wait);
        super::super::rt_shadow::set_rt_shadows_supported(saved);
        eprintln!("[water_reflection] 実 GPU 検証 OK（RT 変種: {}）",
            if has_rt { "構築した" } else { "アダプタ非対応のためスキップ" });
    }

    /// 粗さ 0 は厳密に鏡面（法線を一切いじらない）であること。
    ///
    /// `reflection_roughness` は既定が控えめな正値なので、0 に落としたときに
    /// 「完全な鏡面へ戻せる」ことがパラメータとして意味を持つ最低条件である。
    #[test]
    fn zero_roughness_keeps_the_normal_untouched() {
        // チェックアウトの改行コード（LF/CRLF）に依存しないよう正規化してから照合する
        // （cherry-pick 先の CRLF 実体化で複数行 contains が偽陽性で落ちた実績あり）。
        let src = get_shader_source("water_reflection_common.wgsl").replace("\r\n", "\n");
        assert!(src.contains("if roughness <= 0.0 {\n        return n;\n    }"),
            "粗さ 0 の早期リターン（＝厳密な鏡面）が消えている");
    }
}
