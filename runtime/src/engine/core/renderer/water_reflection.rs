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

/// 粗さ 1.0 のときのブラー半径（**画面高さに対する比**）。
///
/// 画素で直接決めると解像度によってぼけ具合が変わる（4K で急にシャープになる）ため、
/// 画面高さ基準の比で持つ。0.015 は 1080p で 16 画素相当＝「反射像の輪郭は残るが
/// 細部は溶ける」ところで、これ以上大きいと反射が単なる環境色に潰れる。
pub const WATER_REFL_BLUR_MAX_RADIUS_RATIO: f32 = 0.015;

/// 粗さブラーのいもす法反復回数（H→V で 1 回）。
///
/// ボックスフィルタ 3 回でガウシアンをよく近似できるが、水面反射は元がノイジー
/// （1 画素 1 レイ＋IGN ジッタ）なので 2 回で十分に滑らかになる（1 回だと箱の角が残る）。
pub const WATER_REFL_BLUR_ITERATIONS: u32 = 2;

/// 粗さ（0..1）と画面高さから、いもす法ボックスブラーの半径（画素）を決める。
///
/// **0 を返したら呼び出し側はブラーパスごとスキップすること**（粗さ 0 ＝完全な鏡面という
/// パラメータの意味を、1 画素の誤差もなく守るため）。
pub fn water_reflection_blur_radius_px(roughness: f32, height: u32) -> i32 {
    let r = roughness.clamp(0.0, 1.0);
    if r <= 0.0 {
        return 0;
    }
    (r * height as f32 * WATER_REFL_BLUR_MAX_RADIUS_RATIO).round() as i32
}

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

/// RT 変種の連結シェーダ列を返す（**バインドレス可否で末尾が変わる唯一の場所**）。
///
/// 基底の並びは TOML（`water_reflection_rt.toml` の `shader_sources`）が正典で、
/// 本関数はその末尾へ「画面外ヒットのアルベド解決」変種を 1 つ（バインドレス時は
/// `bindless_common` と併せて 2 つ）足すだけである。基底を Rust 側に書き写すと
/// TOML と二重管理になるため、必ず TOML を読んで組み立てる。
///
/// TOML に hit 変種を書けないのは、どちらを連結するかが **実行時の GPU 対応**で
/// 決まるためである（TOML は静的な設定なので分岐を表現できない）。
/// ＝ TOML 単体では `water_refl_hit_albedo` が未定義になる。本関数が唯一の入口。
fn rt_shader_sources(use_bindless: bool) -> Vec<String> {
    let cfg: super::pipeline_config::PipelineConfig =
        toml::from_str(include_str!("pipelines/water_reflection_rt.toml"))
            .expect("water_reflection_rt.toml が読めない");
    let mut sources = cfg.shader_sources;
    if use_bindless {
        // BindlessInstanceRecord / BINDLESS_FLAG_* の定義 → それを使うヒット解決の順。
        sources.push("bindless_common.wgsl".to_string());
        sources.push("water_reflection_hit_on.wgsl".to_string());
    } else {
        sources.push("water_reflection_hit_off.wgsl".to_string());
    }
    sources
}

// ============================================================
//  スカイボックス連携（ミス経路で本物の空を映すための入力）
// ============================================================
//
// 型そのものは **不透明反射 D6 と共用**の `reflection_sky.rs` にある
// （`ReflectionSkyUniform` / `ReflectionSkySource`）。水面と不透明面が同じ方向へ
// 同じ空の色を返すことが要件であり、型・生成関数・WGSL 関数を 1 本に揃えてある。
// 本モジュールが持つのは「そのデータを water 反射パスの group3 へどう積むか」だけである。
pub use super::reflection_sky::{ReflectionSkySource, ReflectionSkyUniform};

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
    /// 天球サンプラー（**経度方向は Repeat**）。equirectangular の u は 0/1 で巻き付くので、
    /// clamp すると経度 0°（背後）の継ぎ目に縦線が出る。skybox.rs のサンプラーと同じ規約。
    sky_sampler: wgpu::Sampler,
    /// スカイボックスパラメータ（毎フレーム `update_sky` で書き換える 64B）。
    /// **storage バッファ**である（group3 は binding_array と同居するため uniform を置けない。
    /// 理由の詳細は `water_reflection_common.wgsl` の `wr_sky` 宣言のコメント）。
    sky_uniform: wgpu::Buffer,
    /// RT 変種が**バインドレスのヒットシェーディング**で構築されたか。
    ///
    /// true のとき `rt_scene_bgl`（group3）は instance_table / UV / index / テクスチャ配列 /
    /// サンプラーを含む拡張レイアウトになっている＝**`create_scene_bg` に
    /// `BindlessResources` を渡さないと BindGroup を作れない**。
    /// false なら従来の 6 binding（平均色経路）。
    rt_bindless: bool,
    /// 粗さブラー用のいもす法（分離ボックス）コンピュート一式。
    /// SSGI と同じく**自前のインスタンスを持つ**（AO の物を借りると所有関係が分かりにくく、
    /// パイプライン構築順の暗黙依存が生まれるため。実体は薄いパイプライン 1 本）。
    imos: super::imos_blur::ImosBlur,
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
        //
        // ヒットシェーディングはバインドレス可否で連結ファイルを差し替える（D6 と同じ流儀）:
        //   on : bindless_common + water_reflection_hit_on  … 実テクスチャをサンプル
        //   off: water_reflection_hit_off                    … 従来の平均色ベタ塗り
        // `binding_array` の**宣言があるだけ**で非対応 GPU の BGL 生成は落ちるため、
        // 分岐はシェーダ内 if ではなく連結レベルで行う必要がある。
        let use_bindless =
            super::rt_shadow::rt_shadows_supported() && super::bindless::bindless_supported();
        let (rt, rt_params_bgl, rt_field_bgl, rt_scene_bgl, rt_gi_bgl) =
            if super::rt_shadow::rt_shadows_supported() {
                let mut builder = RenderPipelineBuilder::new(
                    device,
                    include_str!("pipelines/water_reflection_rt.toml"),
                    WATER_REFLECTION_FORMAT,
                    super::DEPTH_FORMAT,
                )
                .with_label("Water Reflection RT")
                .with_cache(cache)
                .with_shader_sources(rt_shader_sources(use_bindless));
                if use_bindless {
                    // サイズ未指定の `binding_array<texture_2d<f32>>` の要素数
                    //（アダプタ上限でクランプ済みの実行時確定値）をリフレクションへ渡す。
                    builder = builder
                        .with_binding_array_count(super::bindless::bindless_capacity().max(1));
                }
                let (pipeline, mut bgls) = builder.build(resolve_water_reflection_shader);
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
        // RT パイプラインを作らなかった GPU では拡張レイアウトも存在しない。
        let rt_bindless = rt.is_some() && use_bindless;

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

        // 天球用サンプラー（u=Repeat / v=Clamp）。skybox.rs の `sampler` と同じ規約で、
        // 経度の巻き付きを Repeat に、天頂・天底のにじみ防止を Clamp に任せる。
        let sky_sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label:          Some("Water Reflection Sky Sampler"),
            address_mode_u: wgpu::AddressMode::Repeat,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            mag_filter:     wgpu::FilterMode::Linear,
            min_filter:     wgpu::FilterMode::Linear,
            mipmap_filter:  wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        // スカイボックスパラメータ（既定は「無し」。スカイボックスのあるフレームだけ上書きされる）。
        // **STORAGE** で作る: group3 はバインドレスのテクスチャ配列と同居するため、
        // uniform バッファを置けない（wgpu の bind group 制約）。値・レイアウトは不変。
        let sky_uniform = device.create_buffer(&wgpu::BufferDescriptor {
            label:              Some("Water Reflection Sky Params"),
            size:               std::mem::size_of::<ReflectionSkyUniform>() as u64,
            usage:              wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        queue.write_buffer(&sky_uniform, 0, bytemuck::bytes_of(&ReflectionSkyUniform::disabled()));

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
            sampler, sky_sampler, sky_uniform, rt_bindless,
            imos: super::imos_blur::ImosBlur::new(device, cache),
            dummy_tex, dummy_view,
        }
    }

    /// RT 反射パイプラインを持っているか（＝RAY_QUERY 対応 GPU か）。
    pub fn has_rt(&self) -> bool {
        self.rt.is_some()
    }

    /// RT 変種がバインドレス（ヒット点の実テクスチャサンプル）で構築されているか。
    ///
    /// true のときは **`create_scene_bg` に `BindlessResources` を必ず渡すこと**。
    /// 渡さないと group3 のレイアウトとエントリ数が食い違い、BindGroup 生成に失敗する。
    pub fn has_bindless(&self) -> bool {
        self.rt_bindless
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

    /// このフレームの代表スカイボックスを GPU へ反映する（`create_scene_bg` より前に呼ぶ）。
    ///
    /// `None` を渡すと「スカイボックス無し」（`enabled = 0`）を書き、反射のミス経路は
    /// 従来どおり GI プローブ／アンビエントへ落ちる。
    /// **パラメータバッファだけを更新する**（テクスチャの差し替えは `create_scene_bg` の引数側）。
    /// バッファの実体は storage だが、型・レイアウト・意味は uniform 時代と完全に同一である
    /// （group3 が binding_array と同居するための都合。値は 1 フレームに 1 回書くだけ）。
    pub fn update_sky(&self, queue: &wgpu::Queue, sky: Option<&ReflectionSkySource<'_>>) {
        let u = match sky {
            Some(s) => s.uniform,
            None    => ReflectionSkyUniform::disabled(),
        };
        queue.write_buffer(&self.sky_uniform, 0, bytemuck::bytes_of(&u));
    }

    /// group3（シーンカラーのグラブ＋サンプラー＋シーン深度＋スカイボックス）の BindGroup を作る。
    ///
    /// `grab_view` は `WaterRenderer::grab_view()`（屈折の背景と同一テクスチャ）。
    /// `depth_view` は共有深度の **DepthOnly ビュー**（本パスは深度アタッチメントを持たない）。
    /// `sky` はこのフレームの代表スカイボックス。`None` のときは**ダミー 1x1（黒）**を挿す
    /// （uniform 側の `enabled = 0` でサンプル自体が起きないので、中身は何でもよいが
    ///  バインドは必ず埋める必要がある＝レイアウトは常に同一）。
    /// `bindless` は **`has_bindless()` が true かつ `use_rt` のときのみ必須**
    /// （binding 6..10 にインスタンステーブル・UV・index・テクスチャ配列・サンプラーを挿す）。
    /// SSR 変種や非バインドレス GPU では `None` を渡す（余分なエントリは足さない）。
    pub fn create_scene_bg(
        &self,
        device:     &wgpu::Device,
        grab_view:  &wgpu::TextureView,
        depth_view: &wgpu::TextureView,
        sky:        Option<&ReflectionSkySource<'_>>,
        use_rt:     bool,
        bindless:   Option<&super::bindless::BindlessResources>,
    ) -> wgpu::BindGroup {
        let sky_view = match sky {
            Some(s) => s.view,
            None    => &self.dummy_view,
        };
        let mut entries = vec![
            wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(grab_view) },
            wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(&self.sampler) },
            wgpu::BindGroupEntry { binding: 2, resource: wgpu::BindingResource::TextureView(depth_view) },
            wgpu::BindGroupEntry { binding: 3, resource: self.sky_uniform.as_entire_binding() },
            wgpu::BindGroupEntry { binding: 4, resource: wgpu::BindingResource::TextureView(sky_view) },
            wgpu::BindGroupEntry { binding: 5, resource: wgpu::BindingResource::Sampler(&self.sky_sampler) },
        ];
        // ── バインドレス（画面外ヒットの実テクスチャサンプル）の追加エントリ ──
        // `texture_view_list()` が返す Vec は `TextureViewArray` が借用するので、
        // BindGroup を作り終わるまで生かしておく必要がある（先に束縛して寿命を伸ばす）。
        let views;
        if use_rt && self.rt_bindless {
            let bl = bindless.expect(
                "water_reflection: バインドレス変種の RT パイプラインなのに \
                 BindlessResources が渡されていない（group3 のレイアウトと食い違う）");
            views = bl.texture_view_list();
            entries.push(wgpu::BindGroupEntry {
                binding: 6, resource: bl.instance_table_buffer().as_entire_binding() });
            entries.push(wgpu::BindGroupEntry {
                binding: 7, resource: bl.uv_buffer().as_entire_binding() });
            entries.push(wgpu::BindGroupEntry {
                binding: 8, resource: bl.index_buffer().as_entire_binding() });
            entries.push(wgpu::BindGroupEntry {
                binding: 9, resource: wgpu::BindingResource::TextureViewArray(&views) });
            entries.push(wgpu::BindGroupEntry {
                binding: 10, resource: wgpu::BindingResource::Sampler(bl.shared_sampler()) });
        }
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Water Reflection Scene BG (group 3)"),
            layout:  self.scene_bgl(use_rt),
            entries: &entries,
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

    /// 反射 RT を粗さに比例した半径でぼかす（**ブラー結果は `targets.t1_view()` に残る**）。
    ///
    /// 反射パス（レンダーパス）を閉じた後・水面パスの前に記録すること。
    /// `radius_px <= 0`（粗さ 0）なら**何も記録しない**＝呼び出し側は元の反射 RT を
    /// そのまま使うこと（完全な鏡面の保証）。
    ///
    /// ぼかしは「ワールド座標のジッタ」ではなく後段のフィルタで作る、という方針は
    /// `docs/rendering_roadmap.md` の「ブラー系実装の方針」に従う
    /// （エッジ保持が要らない低周波用途なので、バイラテラルではなく いもす法を使う）。
    pub fn blur(
        &self,
        device:    &wgpu::Device,
        encoder:   &mut wgpu::CommandEncoder,
        src:       &wgpu::TextureView,
        targets:   &WaterReflectionBlurTargets,
        radius_px: i32,
    ) {
        if radius_px <= 0 || !targets.is_ready() {
            return;
        }
        self.imos.record(
            device, encoder,
            src, targets.t0_view(), targets.t1_view(),
            targets.width() as i32, targets.height() as i32,
            radius_px, WATER_REFL_BLUR_ITERATIONS,
        );
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
//  WaterReflectionBlurTargets（粗さブラーの ping-pong ターゲット）
// ============================================================

/// 粗さブラー（`imos_blur`）用のフル解像度 ping-pong テクスチャ 2 枚。
///
/// ## なぜ 2 枚要るのか
/// いもす法ブラーは「入力 → t0（水平）→ t1（垂直）」を反復する構造で、
/// 連続するサブパスの入出力が必ず別テクスチャでなければ storage の読み書き競合になる。
/// 反復回数は常に偶数サブパスなので **結果は必ず `t1`** に残る（`ImosBlur::record` の不変条件）。
///
/// ## なぜ反射 RT 自身を書き戻さないのか
/// 反射 RT は RENDER_ATTACHMENT で作られており、そこへ compute から storage 書きするには
/// usage の追加が要る。加えて「元の（ぼかす前の）反射」を入力として保持したまま
/// ping-pong する必要があるため、どのみち 2 枚は要る。
///
/// ## 確保条件
/// **粗さが 0 より大きい水域があるフレームだけ**確保・実行する。粗さ 0（既定）のシーンでは
/// 1 バイトも確保されず、コストは完全にゼロである。
pub struct WaterReflectionBlurTargets {
    /// ping-pong その 1（水平走査の出力）。
    t0: Option<(wgpu::Texture, wgpu::TextureView)>,
    /// ping-pong その 2（垂直走査の出力＝**最終結果**）。
    t1: Option<(wgpu::Texture, wgpu::TextureView)>,
    /// 確保済み幅・高さ（サイズ追従の判定用）。
    width:  u32,
    height: u32,
}

impl Default for WaterReflectionBlurTargets {
    fn default() -> Self { Self::new() }
}

impl WaterReflectionBlurTargets {
    /// 未確保の空ターゲットを作る（device 不要＝App::new から eager に持てる）。
    pub fn new() -> Self {
        Self { t0: None, t1: None, width: 0, height: 0 }
    }

    /// 指定サイズへ追従確保する（同サイズ確保済みなら何もしない。`SsgiTargets::ensure` の流儀）。
    pub fn ensure(&mut self, device: &wgpu::Device, width: u32, height: u32) {
        let w = width.max(1);
        let h = height.max(1);
        if self.t0.is_some() && self.width == w && self.height == h {
            return;
        }
        self.t0 = Some(Self::make_tex(device, "water_reflection_blur0", w, h));
        self.t1 = Some(Self::make_tex(device, "water_reflection_blur1", w, h));
        self.width  = w;
        self.height = h;
    }

    /// ブラー作業用テクスチャ 1 枚（storage 書き込み＋テクスチャ読み）。
    fn make_tex(device: &wgpu::Device, label: &str, w: u32, h: u32)
        -> (wgpu::Texture, wgpu::TextureView)
    {
        let tex = device.create_texture(&wgpu::TextureDescriptor {
            label:           Some(label),
            size:            wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
            mip_level_count: 1,
            sample_count:    1,
            dimension:       wgpu::TextureDimension::D2,
            // いもすブラーの storage フォーマットと反射 RT のフォーマットは
            // どちらも Rgba16Float（下のテストで固定）なので、そのまま流し込める。
            format:          WATER_REFLECTION_FORMAT,
            usage: wgpu::TextureUsages::STORAGE_BINDING | wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats:    &[],
        });
        let view = tex.create_view(&wgpu::TextureViewDescriptor::default());
        (tex, view)
    }

    /// 確保済みか（未確保ならブラーを記録してはならない）。
    pub fn is_ready(&self) -> bool { self.t0.is_some() && self.t1.is_some() }

    /// ping-pong その 1 のビュー（`ensure` 済みであること）。
    pub fn t0_view(&self) -> &wgpu::TextureView {
        &self.t0.as_ref().expect("WaterReflectionBlurTargets: ensure 未実行（t0）").1
    }
    /// ping-pong その 2＝**ブラー結果**のビュー（`ensure` 済みであること）。
    pub fn t1_view(&self) -> &wgpu::TextureView {
        &self.t1.as_ref().expect("WaterReflectionBlurTargets: ensure 未実行（t1）").1
    }
    /// 確保済み幅。
    pub fn width(&self) -> u32 { self.width }
    /// 確保済み高さ。
    pub fn height(&self) -> u32 { self.height }
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
    fn ssr_src() -> String { joined(include_str!("pipelines/water_reflection_ssr.toml")) }

    /// RT 変種の「実際にコンパイルされる WGSL」。**連結の正典は `rt_shader_sources`**
    /// （TOML の基底 ＋ 実行時に決まるヒット解決変種）なので、テストもそこから組み立てる。
    fn rt_variant_src(use_bindless: bool) -> String {
        rt_shader_sources(use_bindless).iter()
            .map(|n| resolve_water_reflection_shader(n))
            .collect::<Vec<_>>()
            .join("\n")
    }
    /// 従来（平均色）変種。既存テストが参照する既定の RT ソース。
    fn rt_src() -> String { rt_variant_src(false) }

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
    /// **バインドレス on / off の両方**を検証する（片方だけ壊れる事故を防ぐ）。
    /// on 側は `binding_array` を含むため、RAY_QUERY に加えて非一様インデックスの
    /// capability が要る（＝実 GPU の feature 要求とテストの前提が一致していることの証拠）。
    #[test]
    fn water_reflection_rt_shader_parses_and_validates() {
        for use_bindless in [false, true] {
            let src = rt_variant_src(use_bindless);
            let module = naga::front::wgsl::parse_str(&src).unwrap_or_else(|e|
                panic!("[water_reflection_rt bindless={use_bindless}] WGSL parse 失敗: {e:?}"));
            let mut caps = naga::valid::Capabilities::RAY_QUERY;
            if use_bindless {
                caps |= naga::valid::Capabilities::SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING;
            }
            let mut v = naga::valid::Validator::new(naga::valid::ValidationFlags::all(), caps);
            v.validate(&module).unwrap_or_else(|e|
                panic!("[water_reflection_rt bindless={use_bindless}] validate 失敗: {e:?}"));
        }
    }

    /// バインドレス変種が「ヒット点のテクスチャを実サンプルする」実装であること。
    ///
    /// ここが平均色へ戻ると、W5.2 で問題になった
    /// **画面外の物体が一様な灰色の塊として水面に映る**症状がそのまま再発する
    /// （テクスチャの平均色は多くのモデルで灰色に寄るため）。
    #[test]
    fn bindless_variant_samples_the_real_base_color_texture() {
        let on = rt_variant_src(true);
        assert!(on.contains("textureSampleLevel(wrbl_tex[rec.albedo_tex_index]"),
            "バインドレス変種がベースカラーテクスチャを実サンプルしていない");
        assert!(on.contains("rec.base_color_factor.rgb"),
            "サンプル値に base_color_factor を掛けていない（マテリアル色が反映されない）");
        // 対象外インスタンス（UV/テクスチャ未登録）は平均色へ縮退する＝退化しない保証。
        assert!(on.contains("BINDLESS_FLAG_ELIGIBLE"),
            "バインドレス対象判定が無い（未登録インスタンスで不正な添字を引く）");
        // 非バインドレス変種は binding_array を **一切宣言しない**
        //（宣言があるだけで非対応 GPU の BGL 生成が落ちる）。
        // コメント中の言及は無害なので、`@group(...)` を持つ宣言行だけを見る。
        let off = rt_variant_src(false);
        let declares_binding_array = |src: &str| src.lines().any(|l| {
            let t = l.trim();
            !t.starts_with("//") && t.contains("@group(") && t.contains("binding_array")
        });
        assert!(!declares_binding_array(&off),
            "非バインドレス変種に binding_array 宣言が混ざっている（非対応 GPU で起動不能になる）");
        assert!(declares_binding_array(&on),
            "バインドレス変種に binding_array 宣言が無い（テクスチャ配列を引けない）");
        assert!(off.contains("fn water_refl_hit_albedo("),
            "非バインドレス変種にヒット解決関数が無い");
    }

    /// group3（バインドレスを同居させるグループ）に uniform バッファが無いこと。
    ///
    /// wgpu の制約「binding_array と uniform buffer は同一 bind group に置けない」に
    /// 抵触すると、**バインドレス対応 GPU でだけ**起動時に BGL 生成が落ちる。
    /// group3 の唯一の uniform だった `wr_sky` を storage へ変えたのがこの制約対応であり、
    /// 戻されると再発するのでテストで固定する。
    #[test]
    fn group3_has_no_uniform_buffer_so_it_can_host_the_binding_array() {
        let common = get_shader_source("water_reflection_common.wgsl").replace("\r\n", "\n");
        for line in common.lines() {
            let l = line.trim();
            if l.starts_with("@group(3)") {
                assert!(!l.contains("var<uniform>"),
                    "group3 に uniform バッファがある: {l}\n\
                     （binding_array と同居できないため storage にすること）");
            }
        }
        // バインドレスのテクスチャ配列は group3 側に置く（group4 は GiParams が uniform）。
        // Rust 側 `create_scene_bg` が挿す binding 番号（6..10）と 1:1 で対応すること。
        let on = get_shader_source("water_reflection_hit_on.wgsl");
        for (binding, name) in [
            (6u32, "wrbl_records"), (7, "wrbl_uv"), (8, "wrbl_index"),
            (9, "wrbl_tex"), (10, "wrbl_smp"),
        ] {
            let decl = on.lines().find(|l| l.contains(&format!("{name}:")))
                .unwrap_or_else(|| panic!("バインドレス変種に {name} の宣言が無い"));
            assert!(decl.contains("@group(3)") && decl.contains(&format!("@binding({binding})")),
                "{name} が group3 binding{binding} に無い（Rust 側 create_scene_bg と食い違う）: {decl}");
        }
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
            assert!(src.contains("return water_refl_output(reflected, s.intensity);"),
                "反射色と強度の出力規約（プリマルチプライド rgb=色×強度 / a=強度）が変わっている");
            assert!(src.contains("if s.intensity <= 0.0 {"),
                "強度 0 の早期リターン（レイを飛ばさない）が消えている");
        }
        // 共有の出力関数がプリマルチプライドであること（ブラーの α 重み付き平均の前提）。
        assert!(get_shader_source("water_reflection_common.wgsl")
                .contains("return vec4<f32>(color * intensity, intensity);"),
            "出力がプリマルチプライドでない（ブラー時に水際が黒く滲む）");
        // 水面パス側は a を重みとして掛け、色は a で割り戻す（0 なら反射なしの水）。
        let surface = super::super::water::resolve_water_shader("water_surface.wgsl");
        assert!(surface.contains("fresnel * clamp(reflection.a, 0.0, 1.0)"),
            "水面パスが反射強度（a）を重みとして使っていない");
        assert!(surface.contains("reflection.rgb / max(reflection.a, WATER_REFL_UNPREMULT_MIN)"),
            "水面パスがプリマルチプライドを割り戻していない（反射が強度倍に暗く／明るくなる）");
    }

    /// 粗さ 0 はブラーパスごとスキップされること（＝完全な鏡面の保証）。
    #[test]
    fn zero_roughness_skips_the_blur_pass() {
        assert_eq!(water_reflection_blur_radius_px(0.0, 1080), 0,
            "粗さ 0 でブラー半径が 0 でない（鏡面が保証できない）");
        // 粗さ 1 は画面高さ比で決まる（解像度が変わってもぼけ具合が変わらないこと）。
        assert_eq!(water_reflection_blur_radius_px(1.0, 1080), 16);
        assert_eq!(water_reflection_blur_radius_px(1.0, 2160), 32);
        // 負値・1 超はクランプされる（パラメータの外れ値でパスが壊れない）。
        assert_eq!(water_reflection_blur_radius_px(-1.0, 1080), 0);
        assert_eq!(water_reflection_blur_radius_px(5.0, 1080),
                   water_reflection_blur_radius_px(1.0, 1080));
    }

    /// ブラーのストレージフォーマットが反射 RT と一致すること
    /// （いもすブラーは storage テクスチャへ書くので、食い違うと BindGroup 生成で落ちる）。
    #[test]
    fn blur_storage_format_matches_the_reflection_rt() {
        assert_eq!(WATER_REFLECTION_FORMAT,
                   crate::engine::core::renderer::imos_blur::IMOS_BLUR_FORMAT,
            "反射 RT といもすブラーの storage フォーマットが食い違っている");
    }

    /// いもすブラーが **α まで**平均すること（プリマルチプライド正規化の前提）。
    ///
    /// ここが `.rgb` だけに戻ると a=0 が書かれ、水面パスの `rgb / max(a, ε)` が
    /// 全画素で 0 除算相当になり、粗さを上げた瞬間に反射が消える。
    #[test]
    fn imos_blur_averages_the_alpha_channel_too() {
        // imos_blur.wgsl は `get_shader_source` のレジストリではなく imos_blur.rs が
        // include_str! で直接抱える（パイプライン TOML を経由しない compute のため）。
        let src = include_str!("shaders/imos_blur.wgsl");
        assert!(src.contains("textureStore(dst, coord, val);"),
            "いもすブラーが α を捨てている（水面反射のプリマルチプライド正規化が壊れる）");
        assert!(src.contains("var sum = vec4<f32>(0.0);"),
            "いもすブラーのランニング和が 4 チャンネルでない");
    }

    /// ミス経路が「画面内の空 → 天球テクスチャ → GI」の順であること（Phase W5.2 の空の映り込み）。
    #[test]
    fn miss_path_falls_back_to_the_real_skybox_before_gi() {
        let common = get_shader_source("water_reflection_common.wgsl").replace("\r\n", "\n");
        assert!(common.contains("fn water_refl_skybox("),
            "天球テクスチャの直接サンプル経路が無い（画面外の空が GI の平坦色になる）");
        // 順序: screen_sky → skybox → env。1 本の関数に閉じ込めて両変種で共有する。
        let miss = common.split("fn water_refl_miss(").nth(1)
            .expect("ミス経路の共通関数 water_refl_miss が無い");
        let i_screen = miss.find("water_refl_screen_sky(").expect("画面内の空の経路が無い");
        let i_sky    = miss.find("water_refl_skybox(").expect("天球サンプルの経路が無い");
        let i_env    = miss.find("water_refl_env(").expect("GI フォールバックが無い");
        assert!(i_screen < i_sky && i_sky < i_env,
            "ミス経路の優先順位が「画面内の空 → 天球 → GI」でない");
        // 天球サンプルの実体は **不透明反射 D6 と共有**の関数であること。
        // ここが自前実装に戻ると、水面と不透明面で UV 変換や実効色がズレて
        // 「同じ空を見ているのに違う色」になる（水際に不連続な境目が出る）。
        assert!(common.contains("sky_refl_sample(wr_sky, t_water_sky, s_water_sky, dir)"),
            "天球サンプルが共有関数 sky_refl_sample を経由していない（D6 と式が分岐する）");
        // スカイボックスが無いシーンでは天球を触らない（有効フラグで分岐する）。判定は共有側。
        let shared = get_shader_source("sky_reflection_common.wgsl").replace("\r\n", "\n");
        assert!(shared.contains("if sky.tint_enabled.w < SKY_REFL_ENABLED_EPS {"),
            "スカイボックス有効フラグの判定が消えている（無いシーンでダミーを映してしまう）");
        // 両変種がミス経路の共通窓口を使うこと（片方だけ空が映らない事故の防止）。
        for name in ["water_reflection_rt.wgsl", "water_reflection_ssr.wgsl"] {
            assert!(get_shader_source(name).contains("water_refl_miss("),
                "{name} がミス経路の共通窓口を使っていない");
        }
    }

    /// スカイボックス uniform の WGSL / Rust レイアウトが一致すること（64B・4 本の vec4）。
    #[test]
    fn water_sky_uniform_layout_matches_the_shader() {
        assert_eq!(std::mem::size_of::<ReflectionSkyUniform>(), 64);
        // 構造体定義は共有モジュール（D6 反射と共用）にある。
        let shared = get_shader_source("sky_reflection_common.wgsl");
        for field in ["rot_inv_0", "rot_inv_1", "rot_inv_2", "tint_enabled"] {
            assert!(shared.contains(field), "WGSL 側に {field} が無い（レイアウト不一致）");
        }
        // 水面パスは group3 に **storage** で挿す（binding_array と uniform は同居できない）。
        let common = get_shader_source("water_reflection_common.wgsl");
        assert!(common.contains("var<storage, read> wr_sky: ReflectionSkyUniform"),
            "水面反射の天球パラメータが storage の ReflectionSkyUniform でない");
        // 無効値は enabled=0（＝天球を一切サンプルしない中立値）。
        assert_eq!(ReflectionSkyUniform::disabled().tint_enabled[3], 0.0);
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
        // RT／バインドレスのグローバルフラグを書き換えるので、他の GPU テストと直列化する
        //（並列に走ると相手の「元へ戻す」で設定を潰され、偽陽性で落ちる）。
        let _guard = super::super::GPU_TEST_LOCK
            .lock()
            .unwrap_or_else(|e| e.into_inner());
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
        // バインドレス（テクスチャ配列＋非一様インデックス）も本体と同じ条件で判定する。
        // 対応していれば **バインドレス変種の RT パイプラインと group3 BindGroup** まで検証する
        //（binding_array と uniform の同居不可・要素数リミットは実機でしか出ない失敗要因）。
        let bindless_feats = wgpu::Features::TEXTURE_BINDING_ARRAY
            | wgpu::Features::SAMPLED_TEXTURE_AND_STORAGE_BUFFER_ARRAY_NON_UNIFORM_INDEXING;
        let adapter_limits = adapter.limits();
        let array_cap = adapter_limits.max_binding_array_elements_per_shader_stage;
        let has_bindless = has_rt && adapter.features().contains(bindless_feats) && array_cap > 0;
        // テクスチャ配列容量は本体（renderer/mod.rs）と同じくアダプタ上限でクランプする。
        let bindless_cap = array_cap
            .min(adapter_limits.max_sampled_textures_per_shader_stage)
            .min(super::super::BINDLESS_MAX_TEXTURES)
            .max(1);
        let (device, queue) = pollster::block_on(adapter.request_device(
            &wgpu::DeviceDescriptor {
                required_features: if has_rt { rt_feats } else { wgpu::Features::empty() }
                    | if has_bindless { bindless_feats } else { wgpu::Features::empty() },
                required_limits: wgpu::Limits {
                    // 本体（renderer/mod.rs）と同じ要求。本パスは group0〜4 を使う。
                    max_bind_groups: 5,
                    // バインドレス時のみ、テクスチャ配列に必要なリミットを引き上げる。
                    max_sampled_textures_per_shader_stage: if has_bindless {
                        bindless_cap.max(wgpu::Limits::default().max_sampled_textures_per_shader_stage)
                    } else {
                        wgpu::Limits::default().max_sampled_textures_per_shader_stage
                    },
                    max_binding_array_elements_per_shader_stage: if has_bindless {
                        bindless_cap
                    } else {
                        wgpu::Limits::default().max_binding_array_elements_per_shader_stage
                    },
                    ..wgpu::Limits::default()
                },
                ..Default::default()
            }))
            .expect("デバイス生成に失敗");
        // BindlessResources は `&Arc<Device>` を要求する（内部で保持するため）。
        // 以降は `&device` の deref 強制で `&wgpu::Device` としても使える。
        let device = std::sync::Arc::new(device);

        // RT／バインドレス対応フラグは他テストと共有のグローバルなので、必ず元へ戻す。
        let saved = super::super::rt_shadow::rt_shadows_supported();
        let saved_bl     = super::super::bindless::bindless_supported();
        let saved_bl_cap = super::super::bindless::bindless_capacity();
        super::super::rt_shadow::set_rt_shadows_supported(has_rt);
        super::super::bindless::set_bindless_supported(has_bindless);
        super::super::bindless::set_bindless_capacity(if has_bindless { bindless_cap } else { 0 });
        // 生成そのものが検証（失敗すれば wgpu がパニック／エラーを出す）。
        let pipelines = WaterReflectionPipelines::new(&device, &queue, None);
        assert_eq!(pipelines.has_rt(), has_rt,
            "RT 対応アダプタなら RT 変種が、非対応なら SSR 変種だけが構築されること");
        assert_eq!(pipelines.has_bindless(), has_bindless,
            "バインドレス対応アダプタならバインドレス変種で構築されること");

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
        // スカイボックス無し（ダミー 1x1 が挿さる）と有り（色テクスチャを天球に見立てる）の
        // 両方で BindGroup を作れること。天球バインドは group3 へ後から足した分なので、
        // ここが通ることが「binding 3..5 の種別（uniform / texture / sampler）が合っている」
        // ことの実機側の証拠になる。
        let sky = ReflectionSkySource { view: &color_view, uniform: ReflectionSkyUniform::disabled() };
        // バインドレス変種では group3 にテクスチャ配列一式が要るので、実体を用意して渡す
        //（これが通ることが「binding_array の count・種別・binding 番号が Rust と WGSL で
        //  一致している」ことの実機側の証拠になる）。
        let bindless = if has_bindless {
            Some(super::super::bindless::BindlessResources::new(&device, &queue, bindless_cap))
        } else {
            None
        };
        for use_rt in [false, has_rt] {
            let bl = if use_rt && pipelines.has_bindless() { bindless.as_ref() } else { None };
            let _bg = pipelines.create_scene_bg(
                &device, &color_view, &depth_view, None, use_rt, bl);
            let _bg_sky = pipelines.create_scene_bg(
                &device, &color_view, &depth_view, Some(&sky), use_rt, bl);
        }
        pipelines.update_sky(&queue, Some(&sky));
        pipelines.update_sky(&queue, None);

        // 粗さブラーのターゲットも実機で確保できること（storage usage の妥当性検証）。
        let mut blur = WaterReflectionBlurTargets::new();
        blur.ensure(&device, 8, 8);
        assert!(blur.is_ready());

        let _ = device.poll(wgpu::PollType::Wait);
        super::super::rt_shadow::set_rt_shadows_supported(saved);
        super::super::bindless::set_bindless_supported(saved_bl);
        super::super::bindless::set_bindless_capacity(saved_bl_cap);
        eprintln!("[water_reflection] 実 GPU 検証 OK（RT 変種: {} / バインドレス: {}）",
            if has_rt { "構築した" } else { "アダプタ非対応のためスキップ" },
            if has_bindless { format!("構築した（容量 {bindless_cap}）") }
            else { "アダプタ非対応のためスキップ".to_string() });
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
