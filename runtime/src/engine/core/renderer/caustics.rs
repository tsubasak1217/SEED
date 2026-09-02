// ============================================================
//  caustics.rs — 水中コースティクス生成パスのリソース（Phase W5.3）
//
//  ## 役割（単一責任）
//  「水中コースティクス（集光模様）を 1 枚のフル解像度 1ch テクスチャへ焼く」ための
//  パイプラインと GPU リソースだけを持つ。**いつ・どの順で描くかはフレーム側**
//  （`frame_renderer.rs`）の責務で、本モジュールはパスの記録すら行わない
//  （`ssgi.rs` / `ao.rs` と同じ分担）。
//
//  ## パスの位置づけ
//    G-Buffer 書き込み（深度確定）
//      → AO / シャドウマスク
//      → **本パス**（深度 → ワールド座標 → 水中なら集光係数）
//      → deferred ライティング（group1 binding12 で読み、Surface.caustics へ）
//  消費側は `lighting_eval.wgsl` が平行光の**影適用後**の radiance を増幅する。
//
//  ## BindGroup を水面パスから使い回さない理由
//  group1（水パラメータ＋岸波）・group2（波紋の場）に挿すリソースは
//  `WaterRenderer` が持つものと同一だが、**BindGroupLayout はパイプラインごとに違う**
//  （WGSL リフレクションが宣言済み binding の集合から導出するため）。
//  したがって BindGroup オブジェクトは互換にならず、本パス専用に作り直すしかない。
//  ID パスが既に同じ理由で専用 BindGroup を持っているのが前例である。
//
//  ## フォーマット
//  R16Float の 1ch。値域は 0..`CAUSTICS_MAX_GAIN`（caustics.wgsl）なので f16 で足り、
//  帯域もフル解像度で 2 byte/px に収まる。deferred 側は `textureLoad` で読むため
//  サンプラーを持たない（フル解像度＝1:1 対応で補間が不要）。
// ============================================================

use super::pipeline::get_shader_source;
use super::pipeline_config::RenderPipelineBuilder;

/// コースティクス結果テクスチャのフォーマット（1ch HDR）。
/// **`pipelines/caustics.toml` の `color_format` と一致必須。**
pub const CAUSTICS_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba16Float;

/// 影の屈折オフセットテクスチャ（location 1）のフォーマット（2ch HDR・単位 m）。
/// **`pipelines/caustics.toml` の `extra_color_formats` と一致必須。**
///
/// 値域は ±`CAUSTICS_SHADOW_OFFSET_MAX_M`（2m）で、f16 の刻みはこの帯域で約 1mm。
/// 影のサンプル位置をずらす用途に対して過剰なほど細かいので f16 で足りる。
pub const CAUSTICS_OFFSET_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rg16Float;

/// 本パスのシェーダソース解決（パイプライン生成とテストが共有する唯一の窓口）。
///
/// 高さ場モジュール（`water_height_field.wgsl`）の実体は water モジュールが持つ。
/// ここで `include_str!` を書くと同じファイルの参照が 2 箇所に増えるので委譲する。
fn resolve_caustics_shader(name: &str) -> &'static str {
    match name {
        "water_height_field.wgsl" => super::water::resolve_water_shader(name),
        other                     => get_shader_source(other),
    }
}

// ============================================================
//  CausticsUniform — 本パス専用の UBO
// ============================================================

/// コースティクス生成パス専用のパラメータ UBO。
///
/// WGSL `caustics.wgsl` の `struct CausticsUniform` と **フィールド順・サイズ一致必須**
/// （下のテスト `wgsl_caustics_uniform_size_matches_rust` が番人）。
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct CausticsUniform {
    /// x = クアッド系インスタンス数（水域探索の範囲）／y,z,w = 予約(0)。
    ///
    /// 水パラメータ配列は `[クアッド…, 川…]` の順（`water/mod.rs`）。コースティクスは
    /// クアッド（Ocean / Region）だけを対象にするので、先頭のこの個数だけを走査する。
    pub counts: [u32; 4],
}

impl CausticsUniform {
    /// クアッドインスタンス数から UBO 値を作る。
    pub fn new(quad_count: u32) -> Self {
        Self { counts: [quad_count, 0, 0, 0] }
    }
}

// ============================================================
//  CausticsPipelines — パイプラインと不変リソース
// ============================================================

/// コースティクス生成パイプライン一式。起動時に 1 回構築して以降は不変。
pub struct CausticsPipelines {
    /// 生成パイプライン（フルスクリーン三角形・深度アタッチメント無し）。
    pipeline: wgpu::RenderPipeline,
    /// group1（水パラメータ＋ショアフィールド）の BindGroupLayout（リフレクション由来）。
    params_bgl: wgpu::BindGroupLayout,
    /// group2（波紋の場）の BindGroupLayout（リフレクション由来）。
    field_bgl: wgpu::BindGroupLayout,
    /// group3（シーン深度＋本パス UBO）の BindGroupLayout（リフレクション由来）。
    depth_bgl: wgpu::BindGroupLayout,
    /// 本パス専用 UBO（毎フレーム `write_params` でクアッド数を書き込む）。
    uniform_buf: wgpu::Buffer,
    /// コースティクス非使用時に deferred の `t_caustics` スロットを埋めるダミー 1x1（黒・永続）。
    #[allow(dead_code)]
    dummy_tex: wgpu::Texture,
    /// 上のビュー。deferred の group1 binding12 へ「機能 OFF」として渡す。
    ///
    /// deferred 側は `textureLoad` で読み、WGSL 仕様では**範囲外の textureLoad は
    /// ゼロを返す**ため、1x1 のダミーでも全画面がコースティクス 0（＝増幅なし）になる。
    pub dummy_view: wgpu::TextureView,
    /// 影の屈折オフセット用ダミー 1x1（ゼロ＝オフセット無し・永続）。
    #[allow(dead_code)]
    dummy_offset_tex: wgpu::Texture,
    /// 上のビュー。deferred の group1 binding13／shadow_mask 生成パスへ「機能 OFF」として渡す。
    ///
    /// オフセットは**加算項**なので中立値は 0 であり、ダミーの黒・クリア値 0・
    /// 範囲外 textureLoad の 0 がすべてそのまま「従来と同一の影」を意味する
    /// （透過率のように中立値へ写し直す分岐が要らないのはこのため）。
    pub dummy_offset_view: wgpu::TextureView,
}

impl CausticsPipelines {
    /// パイプラインとダミーリソースを構築する。
    ///
    /// `queue` はダミー 1x1 を黒で初期化するために使う（未初期化メモリを読ませない）。
    pub fn new(
        device: &wgpu::Device,
        queue:  &wgpu::Queue,
        cache:  Option<&wgpu::PipelineCache>,
    ) -> Self {
        // color_format は TOML 側で R16Float を明示しているため、ここで渡す
        // surface_format / depth_format は使われない（ビルダ引数の体裁合わせ）。
        let (pipeline, mut bgls) = RenderPipelineBuilder::new(
            device,
            include_str!("pipelines/caustics.toml"),
            CAUSTICS_FORMAT,
            super::DEPTH_FORMAT,
        )
        .with_label("Water Caustics")
        .with_cache(cache)
        .build(resolve_caustics_shader);
        assert!(bgls.len() >= 4,
            "caustics.wgsl は group0(カメラ)/group1(水パラメータ)/group2(波紋の場)/\
             group3(深度＋UBO) を宣言すること");
        // **remove は添字がずれるので大きい方から取る**（water/mod.rs と同じ流儀）。
        let depth_bgl  = bgls.remove(3);
        let field_bgl  = bgls.remove(2);
        let params_bgl = bgls.remove(1);

        let uniform_buf = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Water Caustics Uniform"),
            size:  std::mem::size_of::<CausticsUniform>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });

        // 機能 OFF 用のダミー 1x1（黒）。deferred の binding12 を常に埋められるようにする
        // （DeferredLightingPipelines の mask_dummy_* と同じ役割）。
        let dummy_tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Water Caustics Dummy 1x1"),
            size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          CAUSTICS_FORMAT,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats:    &[],
        });
        /// Rgba16Float 1 テクセルのバイト数（4 チャネル × f16）。
        const CAUSTICS_TEXEL_BYTES: u32 = 8;
        let black: [u8; CAUSTICS_TEXEL_BYTES as usize] = [0u8; CAUSTICS_TEXEL_BYTES as usize];
        queue.write_texture(
            dummy_tex.as_image_copy(),
            &black,
            wgpu::TexelCopyBufferLayout {
                offset:         0,
                bytes_per_row:  Some(CAUSTICS_TEXEL_BYTES),
                rows_per_image: Some(1),
            },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        );
        let dummy_view = dummy_tex.create_view(&wgpu::TextureViewDescriptor::default());

        // 影の屈折オフセット用ダミー 1x1（ゼロ＝オフセット無し）。
        let dummy_offset_tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("Water Caustics Offset Dummy 1x1"),
            size:            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          CAUSTICS_OFFSET_FORMAT,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats:    &[],
        });
        /// Rg16Float 1 テクセルのバイト数（2 チャネル × f16）。
        const CAUSTICS_OFFSET_TEXEL_BYTES: u32 = 4;
        let zero: [u8; CAUSTICS_OFFSET_TEXEL_BYTES as usize] =
            [0u8; CAUSTICS_OFFSET_TEXEL_BYTES as usize];
        queue.write_texture(
            dummy_offset_tex.as_image_copy(),
            &zero,
            wgpu::TexelCopyBufferLayout {
                offset:         0,
                bytes_per_row:  Some(CAUSTICS_OFFSET_TEXEL_BYTES),
                rows_per_image: Some(1),
            },
            wgpu::Extent3d { width: 1, height: 1, depth_or_array_layers: 1 },
        );
        let dummy_offset_view =
            dummy_offset_tex.create_view(&wgpu::TextureViewDescriptor::default());

        Self {
            pipeline, params_bgl, field_bgl, depth_bgl, uniform_buf,
            dummy_tex, dummy_view, dummy_offset_tex, dummy_offset_view,
        }
    }

    /// このフレームのクアッドインスタンス数を UBO へ書き込む（パス直前に呼ぶ）。
    pub fn write_params(&self, queue: &wgpu::Queue, quad_count: u32) {
        queue.write_buffer(&self.uniform_buf, 0,
            bytemuck::bytes_of(&CausticsUniform::new(quad_count)));
    }

    /// group1（水パラメータ＋ショアフィールド）の BindGroup を本パス専用レイアウトで作る。
    ///
    /// リソースは `WaterRenderer` のものをそのまま挿す（同じ水域を同じ高さ場で評価するため）。
    /// `params` は `WaterRenderer::params_buffer()` の値（`prepare` 済みのフレームでのみ `Some`）。
    pub fn create_params_bg(
        &self,
        device: &wgpu::Device,
        water:  &super::water::WaterRenderer,
    ) -> Option<wgpu::BindGroup> {
        let params = water.params_buffer()?;
        Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Caustics Params BG (group 1)"),
            layout:  &self.params_bgl,
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

    /// group2（波紋・航跡の場）の BindGroup を本パス専用レイアウトで作る。
    ///
    /// 場が未構築のフレーム（`field = None`）は `WaterRenderer` のゼロフォールバックを挿す。
    /// ゼロ UBO は `inv_extent = 0` なので、波紋サンプルは常に窓外扱い＝寄与ゼロになる。
    pub fn create_field_bg(
        &self,
        device: &wgpu::Device,
        water:  &super::water::WaterRenderer,
        field:  Option<&super::interaction::InteractionFieldRenderer>,
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
            label:   Some("Water Caustics Ripple Field BG (group 2)"),
            layout:  &self.field_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(view) },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(sampler) },
                wgpu::BindGroupEntry { binding: 2, resource: uniform.as_entire_binding() },
            ],
        })
    }

    /// group3（シーン深度＋本パス UBO）の BindGroup を作る。
    ///
    /// `depth_view` は共有深度の **DepthOnly ビュー**（`RenderFrame::depth_only_view_r`）。
    /// 本パスは深度アタッチメントを持たず、これを `textureLoad` で読む。
    pub fn create_depth_bg(
        &self,
        device:     &wgpu::Device,
        depth_view: &wgpu::TextureView,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Caustics Depth BG (group 3)"),
            layout:  &self.depth_bgl,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(depth_view) },
                wgpu::BindGroupEntry { binding: 1, resource: self.uniform_buf.as_entire_binding() },
            ],
        })
    }

    /// 開始済みのコースティクスパスへフルスクリーン三角形 1 枚を記録する。
    pub fn draw<'p>(
        &'p self,
        pass:      &mut wgpu::RenderPass<'p>,
        camera_bg: &'p wgpu::BindGroup,
        params_bg: &'p wgpu::BindGroup,
        field_bg:  &'p wgpu::BindGroup,
        depth_bg:  &'p wgpu::BindGroup,
    ) {
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, camera_bg, &[]);
        pass.set_bind_group(1, params_bg, &[]);
        pass.set_bind_group(2, field_bg,  &[]);
        pass.set_bind_group(3, depth_bg,  &[]);
        pass.draw(0..3, 0..1);
    }
}

// ============================================================
//  CausticsTargets — フル解像度の結果テクスチャ
// ============================================================

/// コースティクス結果テクスチャ（フル解像度 1ch）。App が保持しサーフェスサイズに追従する。
///
/// 半解像度にしていないのは、消費側（deferred ライティング）が `textureLoad` で
/// 1:1 に読む前提だからである（アップサンプル用のサンプラー・深度考慮補間が要らず、
/// 模様の輝線が半解像度のボケでにじむこともない）。R16Float の 1ch なので
/// 1920×1080 でも約 4MB と、G-Buffer 1 枚より軽い。
/// 手本は `ssgi.rs` の `SsgiTargets`。
pub struct CausticsTargets {
    /// 結果テクスチャとそのビュー（未確保なら `None`）。
    tex: Option<(wgpu::Texture, wgpu::TextureView)>,
    /// 影の屈折オフセット（location 1・Rg16Float）とそのビュー（未確保なら `None`）。
    /// `tex` と必ず同時に確保・同一サイズ（同じ MRT パスのアタッチメントであるため）。
    offset_tex: Option<(wgpu::Texture, wgpu::TextureView)>,
    /// 確保済みサイズ（幅・高さ）。
    w: u32,
    h: u32,
}

impl Default for CausticsTargets {
    fn default() -> Self { Self::new() }
}

impl CausticsTargets {
    /// 空の（未確保の）ターゲットを生成する（device 不要・eager 構築可）。
    pub fn new() -> Self {
        Self { tex: None, offset_tex: None, w: 0, h: 0 }
    }

    /// フル解像度サイズへ追従する。既存が同サイズなら何もしない。
    ///
    /// 戻り値: 再確保（初回 or サイズ変更）が起きたら `true`。
    /// コースティクスはフレーム跨ぎの履歴を持たないため呼び出し側は無視してよいが、
    /// `AoTargets` / `SsgiTargets` と同じ契約に揃えてある。
    pub fn ensure(&mut self, device: &wgpu::Device, w: u32, h: u32) -> bool {
        let w = w.max(1);
        let h = h.max(1);
        if self.tex.is_some() && self.w == w && self.h == h {
            return false;
        }
        let tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("water_caustics"),
            size:            wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          CAUSTICS_FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats:    &[],
        });
        let view = tex.create_view(&wgpu::TextureViewDescriptor::default());
        // 影の屈折オフセット（location 1）。同一パスの MRT なので必ず同サイズで確保する。
        let offset_tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some("water_caustics_offset"),
            size:            wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            format:          CAUSTICS_OFFSET_FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats:    &[],
        });
        let offset_view = offset_tex.create_view(&wgpu::TextureViewDescriptor::default());
        self.tex = Some((tex, view));
        self.offset_tex = Some((offset_tex, offset_view));
        self.w = w;
        self.h = h;
        true
    }

    /// 結果テクスチャのビュー（`ensure` 済みであること）。
    pub fn view(&self) -> &wgpu::TextureView {
        &self.tex.as_ref().expect("CausticsTargets: ensure 未実行").1
    }

    /// 影の屈折オフセットテクスチャのビュー（`ensure` 済みであること）。
    pub fn offset_view(&self) -> &wgpu::TextureView {
        &self.offset_tex.as_ref().expect("CausticsTargets: ensure 未実行（offset）").1
    }
}

// ============================================================
//  テスト（WGSL 静的検証・レイアウト照合）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// TOML の `shader_sources` 順に連結した「実際にコンパイルされる WGSL」を返す。
    /// 連結順の正典は TOML なので、テストもそこから組み立てる（water/mod.rs の `joined` と同流儀）。
    fn joined() -> String {
        let cfg: crate::engine::core::renderer::pipeline_config::PipelineConfig =
            toml::from_str(include_str!("pipelines/caustics.toml"))
                .expect("caustics.toml が読めない");
        cfg.shader_sources.iter()
            .map(|n| resolve_caustics_shader(n))
            .collect::<Vec<_>>()
            .join("\n")
    }

    /// 連結後の WGSL を naga で parse + validate する（GPU デバイス不要）。
    #[test]
    fn caustics_shader_parses_and_validates() {
        let src = joined();
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("caustics.wgsl WGSL parse 失敗: {e:?}"));
        let mut validator = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::empty(),
        );
        validator.validate(&module)
            .unwrap_or_else(|e| panic!("caustics.wgsl WGSL validate 失敗: {e:?}"));
    }

    /// TOML のエントリポイント名がシェーダの実装と一致すること
    /// （不一致はパイプライン生成時のランタイム失敗になるため静的に照合しておく）。
    #[test]
    fn caustics_toml_entries_match_shader() {
        let toml_src = include_str!("pipelines/caustics.toml");
        let wgsl_src = joined();
        assert!(toml_src.contains("vertex_entry    = \"vs_fullscreen\""));
        assert!(toml_src.contains("fragment_entry  = \"fs_caustics\""));
        assert!(wgsl_src.contains("fn vs_fullscreen("));
        assert!(wgsl_src.contains("fn fs_caustics("));
        // 深度アタッチメントを持たない（サンプルテクスチャとして読む）前提。
        assert!(toml_src.contains("no_depth        = true"));
        // 出力は水色着色済みの RGB HDR（A 未使用）。Rust 側 CAUSTICS_FORMAT と一致必須。
        assert!(toml_src.contains("color_format    = \"Rgba16Float\""));
        assert_eq!(CAUSTICS_FORMAT, wgpu::TextureFormat::Rgba16Float);
        // 高さ場の共有モジュールを必ず連結すること（独自波の新造を禁じる契約）。
        assert!(toml_src.contains("water_height_field.wgsl"),
            "高さ場の共有モジュールが連結されていない（＝独自波を作っている疑い）");
        // 2 枚目の MRT（影の屈折オフセット・Rg16Float）。Rust 側 CAUSTICS_OFFSET_FORMAT と一致必須。
        assert!(toml_src.contains(r#"extra_color_formats = ["Rg16Float"]"#),
            "影の屈折オフセット用 MRT（location1）が TOML から消えている");
        assert_eq!(CAUSTICS_OFFSET_FORMAT, wgpu::TextureFormat::Rg16Float);
        assert!(wgsl_src.contains("@location(1) shadow_offset: vec2<f32>,"),
            "シェーダ側の location1 出力が TOML の MRT 宣言と食い違っている");
    }

    /// 影の屈折オフセットは「機能 OFF なら厳密に 0」でなければならない（回帰ゼロの根拠）。
    ///
    /// 消費側（`lighting_eval.wgsl` / `shadow_mask.wgsl`）はオフセットを**加算**するため、
    /// 中立値は 0 ちょうどである。0 を作る経路が 3 つ（クリア値／`discard` で書かれない／
    /// ダミー 1x1 への範囲外 textureLoad）あり、そのいずれもが「従来と同一の影」を意味する。
    /// 誇張倍率（`WaterParams.caustics.w`）が 0 のときに式が厳密な 0 を返すことを固定する。
    #[test]
    fn shadow_offset_is_exactly_zero_when_disabled() {
        let src = get_shader_source("caustics.wgsl");
        // 誇張倍率 caustics.w を掛けているので、0 なら積は厳密に 0（f32 の乗算で誤差は出ない）。
        assert!(src.contains("(sxz - world_pos.xz) * p.caustics.w"),
            "誇張倍率が掛かっていない（0 で無効化できなくなる）");
        // クランプの下限・上限は対称（片側だけ効くと影が一方向へ寄る）。
        assert!(src.contains("vec2<f32>(-CAUSTICS_SHADOW_OFFSET_MAX_M)")
             && src.contains("vec2<f32>( CAUSTICS_SHADOW_OFFSET_MAX_M)"),
            "オフセットの発散防止クランプが対称でない／消えている");
        // 深度フェード（fade）を掛けてはいけない（屈折ずれは深いほど大きい現象）。
        assert!(!src.contains("(sxz - world_pos.xz) * fade"),
            "影の屈折オフセットに深度フェードを掛けてはいけない");
    }

    /// 水中判定の厳密化（追補）が WGSL に入っていること。
    ///
    /// ここで守りたい規則は 3 つ:
    ///   (1) 水域の下端（`wave_axis.w`）より下は水中ではない（Region の AABB 判定）
    ///   (2) 影の屈折オフセットは最小水深 `CAUSTICS_SHADOW_MIN_DEPTH_M` を満たす画素だけに出す
    ///       （海面 Y よりわずかに低い陸地を水中扱いして影を毎フレーム振り回さないため）
    ///   (3) (2) の判定は**光（集光模様）には掛けない**（浅い水たまりでも模様は出したい）
    #[test]
    fn submerge_test_is_tightened_for_shadow_offset() {
        let src = get_shader_source("caustics.wgsl");
        // (1) 下端判定（負値＝下端なしはスキップ）。
        assert!(
            src.contains("let bottom_depth = q.wave_axis.w;")
                && src.contains("if (bottom_depth >= 0.0 && below > bottom_depth)"),
            "水域の下端（wave_axis.w）による水中判定が入っていない"
        );
        // (2) 影オフセットの最小水深ゲート。既定値も固定する（緩めたら気付けるように）。
        assert!(
            src.contains("if (d >= CAUSTICS_SHADOW_MIN_DEPTH_M) {"),
            "影オフセットの最小水深ゲートが入っていない"
        );
        assert!(
            src.contains("const CAUSTICS_SHADOW_MIN_DEPTH_M: f32 = 0.5;"),
            "最小水深の既定が 0.5m から変わっている（意図的なら本テストも更新すること）"
        );
        // クランプ上限は 5cm（実屈折ずれ 2〜3cm の 2 倍弱）。
        assert!(
            src.contains("const CAUSTICS_SHADOW_OFFSET_MAX_M: f32 = 0.05;"),
            "影オフセットのクランプ上限が 0.05m から変わっている"
        );
        // (3) 光（transmission / strength）は最小水深ゲートの内側で計算されていないこと。
        let gate = src
            .split("if (d >= CAUSTICS_SHADOW_MIN_DEPTH_M) {")
            .nth(1)
            .expect("最小水深ゲートが見つからない");
        let body = gate.split('}').next().unwrap();
        assert!(
            !body.contains("strength") && !body.contains("transmission"),
            "最小水深ゲートの中に集光の光が入っている（浅瀬で模様が消えてしまう）"
        );
    }

    /// WGSL 側 `CausticsUniform` の naga 実測サイズが Rust と一致すること。
    #[test]
    fn wgsl_caustics_uniform_size_matches_rust() {
        // caustics.wgsl 単体は `WaterParams` を共有モジュールに依存するため parse できない。
        // 実際にコンパイルされるのと同じ連結ソースから型を引く。
        let src = joined();
        let module = naga::front::wgsl::parse_str(&src)
            .expect("連結 WGSL の parse に失敗");
        let (handle, _) = module.types.iter()
            .find(|(_, t)| t.name.as_deref() == Some("CausticsUniform"))
            .expect("WGSL に struct CausticsUniform が見つかりません");
        let mut layouter = naga::proc::Layouter::default();
        layouter.update(module.to_ctx()).expect("naga Layouter の計算に失敗");
        assert_eq!(
            layouter[handle].size as usize,
            std::mem::size_of::<CausticsUniform>(),
            "WGSL の CausticsUniform サイズが Rust と一致しません",
        );
    }

    /// 高さ場は共有モジュールのものだけを使うこと（Phase W5.3 の設計の芯）。
    ///
    /// 独自のノイズ・波を足すと「水面の見た目」と「水底の模様」が食い違い、
    /// 一目で嘘くさくなる。消費している関数名を契約として固定する。
    #[test]
    fn caustics_uses_only_the_shared_height_field() {
        let src = get_shader_source("caustics.wgsl");
        assert!(src.contains("water_surface_height(p, sxz"),
            "共有の合成高さ場を使っていない");
        assert!(src.contains("water_surface_gradient(p, world_pos.xz"),
            "共有の合成勾配（屈折オフセット）を使っていない");
        // 時間は水面パスと同一ソース（＝模様と水面が同期する）。
        assert!(src.contains("let t = u_camera.time;"),
            "時間が水面パスと別ソースになっている（模様と水面がずれる）");
    }

    /// ワールド座標復元の規約が deferred ライティングと**同一**であること。
    ///
    /// ここが食い違うと Play のレターボックス時に模様だけが水底を横滑りする
    /// （ライティングが読む座標と模様を焼いた座標がずれるため）。式を文字列で固定する。
    #[test]
    fn world_reconstruction_matches_deferred_convention() {
        let caustics = get_shader_source("caustics.wgsl");
        let deferred = get_shader_source("deferred_lighting.wgsl");
        const EXPR: &str =
            "(pix - u_camera.viewport.xy) / max(u_camera.viewport.zw, vec2<f32>(1.0, 1.0))";
        assert!(caustics.contains(EXPR), "caustics のビューポート相対 UV 式が変わっている");
        assert!(deferred.contains(EXPR), "deferred のビューポート相対 UV 式が変わっている");
    }
}
