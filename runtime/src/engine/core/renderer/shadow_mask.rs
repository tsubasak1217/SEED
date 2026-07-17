// ============================================================
//  shadow_mask.rs — RT ソフト影マスク生成パイプライン＋半解像度リソース（Phase RT-Shadow-Denoise）
//
//  ## 役割（単一責任）
//  Deferred（G-Buffer）有効かつ RT 影オンのとき、CPU が選定した最大 RT_SHADOW_MASK_LIGHTS 灯の
//  ソフト影ライトについて、既存 rt_shadow_factor（rt_shadow_on.wgsl・無改変）を半解像度で評価し
//  texture_2d_array（レイヤ=スロット, Rgba16Float）へ .rgb=透過率／.a=half-res ビュー空間深度を
//  書き出すフルスクリーン MRT パスと、そのデノイズ（影マスク専用 separable バイラテラルブラーを
//  各レイヤに適用。.a の深度をガイドにエッジ保持）・半解像度リソース・選定ロジックを持つ。
//  手本は ao.rs / ssgi.rs（half-res）と reflection.rs（group4 TLAS 借用）。
//
//  ## パス順（frame_renderer が制御）
//    G-Buffer 書き込み → AO 生成
//      → シャドウマスク生成（G-Buffer＋TLAS → 半解像度 mask_raw の 4 レイヤ。.rgb=透過率/.a=深度）
//      → バイラテラルブラー（各レイヤ mask_raw → mask_a → mask_b、.a 深度でエッジ保持。結果 mask_b）
//      → デファードライティング（group1 binding10 に mask_b をバイリニアで供給し、ライトループが
//         light.shadow_mask_slot の指すレイヤをサンプルして遮蔽率にする）
//
//  ## なぜ半解像度マスク＋ブラーで根治するか
//  ソフト影のディザ状ガサガサは「ピクセルごとの IGN 回転 × 確率的サンプリングの量子化ノイズ」。
//  1 ライトを画面全体でまとめて評価し、空間ブラー（影マスク専用バイラテラル）で均せば滑らかな半影になる。
//  半解像度化でレイ本数も従来比 ~1/4。ハード影（cone_radius=0）は元々ノイズが無く、マスクにも
//  正しく載る（rt_shadow_factor がそのまま 1 本レイへ分岐）。
//
//  ## group4 の借用（max_bind_groups=5 厳守）
//    group0=camera / group1=G-Buffer / group2=ShadowMaskParams / group3=gap / group4=ライト+TLAS
//  group4 は RT 影用複合 BindGroup（lighting.rs create_rt_bind_group）をそのまま bind し、パイプライン
//  レイアウトも RtMeshPipelines.lights_bgl を流用する（レイアウト等価＝借用が合法）。ゆえに本パイプラインは
//  RT 対応 GPU（rt_lights_bgl が存在する）でのみ構築される（DrawPipelines.shadow_mask は Option）。
//
//  ## フォーマット（Rgba16Float 単一運用。.a に深度同梱）
//  mask_raw/mask_a/mask_b はすべて IMOS_BLUR_FORMAT=Rgba16Float。storage・filterable・
//  render-attachment を core 保証で満たす（ao.rs / imos_blur.rs のフォーマット解説と一致）。
//  .rgb=透過率／.a=half-res ビュー空間深度（別 R32Float アタッチメントは 4×16B=32B に 4B を足すと
//  max_color_attachment_bytes_per_sample=32 を超過するため不可。未使用の .a へ同梱してバイト予算内に収める）。
// ============================================================

use std::sync::atomic::{AtomicBool, Ordering};

use super::deferred::DeferredLightingPipelines;
use super::imos_blur::IMOS_BLUR_FORMAT;
use super::shadow_mask_bilateral::ShadowMaskBilateral;
use super::lighting::GpuLight;
use super::pipeline::get_shader_source;

// ─── 定数 ────────────────────────────────────────────────────

/// 事前計算シャドウマスクの対象ライト上限（＝レイヤ数＝MRT 出力数）。
/// WGSL 側 shadow_mask.wgsl SHADOW_MASK_LAYERS / surface.wgsl SURFACE_SHADOW_MASK_SLOTS と一致必須
/// （下のユニットテスト shadow_mask_layer_count_matches_wgsl が 3 者一致を担保）。
/// これを超えるソフト影ライトは従来のインライン（ノイズあり）経路になる。
pub const RT_SHADOW_MASK_LIGHTS: u32 = 4;

/// マスクテクスチャ（raw/a/b）の共通フォーマット。imos の storage フォーマットと一致必須。
pub const SHADOW_MASK_FORMAT: wgpu::TextureFormat = IMOS_BLUR_FORMAT;

/// マスク計算解像度の分母（フル解像度に対する 1/N）。2＝半解像度（コスト 1/4）。
pub const SHADOW_MASK_RESOLUTION_DIVISOR: u32 = 2;

/// バイラテラルブラーの半径（半解像度画素）。窓は [-radius, radius]＝(2r+1) タップ。
/// 小半径（深度エッジ保持のため固定タップ。σ・深度許容は WGSL 側の名前付き定数）。
pub const SHADOW_MASK_BLUR_RADIUS: i32 = 3;

/// ソフト影ライトが上限を超えたときの警告を 1 度だけ出すためのフラグ（ログ爆発防止）。
static OVERFLOW_WARNED: AtomicBool = AtomicBool::new(false);

// ─── ShadowMaskParams（group2 binding0 uniform, 32B）─────────

/// マスク生成パスの選定ライト情報 UBO（WGSL shadow_mask.wgsl の ShadowMaskParams と一致・32B）。
///
/// | offset | field    | size |
/// |--------|----------|------|
/// |   0    | indices  |  16  |（vec4<u32>）
/// |  16    | count    |   4  |
/// |  20    | _pad0..2 |  12  |
#[repr(C)]
#[derive(Debug, Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ShadowMaskParams {
    /// スロット 0..3 が評価するライトの u_lights グローバルインデックス（count 未満のみ有効）。
    pub indices: [u32; 4],
    /// 選定されたソフト影ライト数（0..RT_SHADOW_MASK_LIGHTS）。
    pub count:   u32,
    pub _pad0:   u32,
    pub _pad1:   u32,
    pub _pad2:   u32,
}

impl ShadowMaskParams {
    /// 選定インデックス列（スロット順）から UBO 値を作る（4 個超は切り捨て）。
    pub fn new(selection: &[u32]) -> Self {
        let mut indices = [0u32; RT_SHADOW_MASK_LIGHTS as usize];
        let count = selection.len().min(RT_SHADOW_MASK_LIGHTS as usize);
        indices[..count].copy_from_slice(&selection[..count]);
        Self { indices, count: count as u32, _pad0: 0, _pad1: 0, _pad2: 0 }
    }
}

// ─── ライト選定（CPU, 毎フレーム）────────────────────────────

/// soft_radius>0 のライトから最大 RT_SHADOW_MASK_LIGHTS 灯を intensity 降順で選ぶ。
///
/// 戻り値は選定ライトの**グローバルインデックス**（u_lights 配列内の位置）をスロット順で並べたもの。
/// 上限を超えたソフト影ライトは選ばれず、従来のインライン rt_shadow_factor（ノイズあり）経路になる
/// （初回のみ警告を出す）。ハード影（soft_radius=0）は対象外（元々ノイズが無い）。
pub fn select_shadow_mask_lights(lights: &[GpuLight]) -> Vec<u32> {
    let mut cand: Vec<u32> = lights.iter().enumerate()
        .filter(|(_, l)| l.soft_radius > 0.0)
        .map(|(i, _)| i as u32)
        .collect();
    // intensity 降順。sort_by は安定ソートなので同 intensity は元の並び順を保つ。
    cand.sort_by(|&a, &b| {
        lights[b as usize].intensity
            .partial_cmp(&lights[a as usize].intensity)
            .unwrap_or(std::cmp::Ordering::Equal)
    });
    let total = cand.len();
    cand.truncate(RT_SHADOW_MASK_LIGHTS as usize);
    if total > RT_SHADOW_MASK_LIGHTS as usize
        && !OVERFLOW_WARNED.swap(true, Ordering::Relaxed)
    {
        eprintln!(
            "[SEED RT] ソフト影ライトが {total} 灯あり、RT シャドウマスク上限 {} を超過しました。\
             intensity 上位 {} 灯のみデノイズマスク経路（滑らかな半影）になり、残りは従来の\
             インライン経路（ディザあり）になります。",
            RT_SHADOW_MASK_LIGHTS, RT_SHADOW_MASK_LIGHTS,
        );
    }
    cand
}

/// ソフト影ライトを選定し、選ばれたライトの `shadow_mask_slot` にスロット番号を書き込む。
/// 選定外のライトは呼び出し側で -1.0（既定）のままにしておくこと（collect_gpu_lights の既定）。
/// 戻り値は ShadowMaskParams / マスク生成パスで使う選定インデックス列（スロット順）。
pub fn assign_shadow_mask_slots(lights: &mut [GpuLight]) -> Vec<u32> {
    let selection = select_shadow_mask_lights(lights);
    for (slot, &gi) in selection.iter().enumerate() {
        lights[gi as usize].shadow_mask_slot = slot as f32;
    }
    selection
}

// ─── ShadowMaskPipelines（静的リソース: パイプライン・BGL・UBO・ブラー・サンプラー）───

/// シャドウマスク生成パイプライン一式（RT 対応 GPU でのみ構築）。起動時 1 回・不変。
pub struct ShadowMaskPipelines {
    /// マスク生成パイプライン（RAY_QUERY 必須・4 カラーターゲット MRT。各 .rgb=透過率／.a=深度同梱）。
    pub mask:          wgpu::RenderPipeline,
    /// group2: ShadowMaskParams のレイアウト。
    pub params_bgl:    wgpu::BindGroupLayout,
    /// ShadowMaskParams UBO（毎フレーム選定インデックスを書き込む）。
    pub params_buffer: wgpu::Buffer,
    /// 影マスク専用 separable バイラテラルブラー基盤（深度エッジ保持。各レイヤ mask_raw→mask_a→mask_b）。
    pub bilateral:     ShadowMaskBilateral,
}

impl ShadowMaskPipelines {
    /// マスク生成パイプライン一式を構築する（RT 対応 GPU＝rt_lights_bgl 存在時のみ呼ぶこと）。
    ///
    /// - `deferred`      : group0/1/3（camera/gbuffer/gap）の BGL を借用する（等価性担保）。
    /// - `rt_lights_bgl` : group4（ライト＋シャドウ＋TLAS＋平均アルベド複合）。RtMeshPipelines.lights_bgl。
    pub fn new(
        device:        &wgpu::Device,
        deferred:      &DeferredLightingPipelines,
        rt_lights_bgl: &wgpu::BindGroupLayout,
        cache:         Option<&wgpu::PipelineCache>,
    ) -> Self {
        // group2: ShadowMaskParams uniform（binding0）。
        let params_bgl = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("Shadow Mask Params BGL (group2)"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0, visibility: wgpu::ShaderStages::FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false, min_binding_size: None,
                },
                count: None,
            }],
        });

        // 連結順（RAY_QUERY 必須）: cluster_common → ddgi_common → light_common → rt_shadow_on → shadow_mask。
        let combined: String = [
            "cluster_common.wgsl", "ddgi_common.wgsl", "light_common.wgsl",
            "rt_shadow_on.wgsl", "shadow_mask.wgsl",
        ].iter().map(|n| get_shader_source(n)).collect::<Vec<_>>().join("\n");
        let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label:  Some("shadow_mask"),
            source: wgpu::ShaderSource::Wgsl(combined.into()),
        });

        // パイプラインレイアウト: [camera, gbuffer, params, gap3, lights+TLAS]。
        // group3 は deferred の空 gap を流用（描画時 empty_bg3 をセット）。group4 は rt_lights_bgl。
        let layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("shadow_mask"),
            bind_group_layouts: &[
                &deferred.camera_bgl, &deferred.gbuffer_bgl, &params_bgl,
                &deferred.gap_bgl3, rt_lights_bgl,
            ],
            push_constant_ranges: &[],
        });

        // 4 レイヤぶんを MRT で同時出力（location 0..3）。全ターゲット SHADOW_MASK_FORMAT・blend なし。
        // 各レイヤの .rgb=透過率, .a=half-res ビュー空間深度（バイラテラルブラーの深度ガイド）を同梱する
        //（別 R32Float アタッチメントは 32B+4B=36B で max_color_attachment_bytes_per_sample=32 超過のため不可）。
        let color_target = wgpu::ColorTargetState {
            format: SHADOW_MASK_FORMAT, blend: None, write_mask: wgpu::ColorWrites::ALL,
        };
        let targets = [
            Some(color_target.clone()), Some(color_target.clone()),
            Some(color_target.clone()), Some(color_target),
        ];
        let mask = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label:  Some("shadow_mask"),
            layout: Some(&layout),
            vertex: wgpu::VertexState {
                module: &shader, entry_point: Some("vs_fullscreen"),
                buffers: &[], compilation_options: Default::default(),
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader, entry_point: Some("fs_mask"),
                targets: &targets, compilation_options: Default::default(),
            }),
            primitive:     wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample:   wgpu::MultisampleState::default(),
            multiview:     None,
            cache,
        });

        let params_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("Shadow Mask Params UBO"),
            size:  std::mem::size_of::<ShadowMaskParams>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let bilateral = ShadowMaskBilateral::new(device, cache);

        Self { mask, params_bgl, params_buffer, bilateral }
    }

    /// 選定インデックス列（スロット順）を ShadowMaskParams UBO へ書き込む（毎フレーム パス直前）。
    pub fn write_params(&self, queue: &wgpu::Queue, selection: &[u32]) {
        queue.write_buffer(&self.params_buffer, 0, bytemuck::bytes_of(&ShadowMaskParams::new(selection)));
    }

    /// group2（ShadowMaskParams）の BindGroup を生成する。
    pub fn create_params_bg(&self, device: &wgpu::Device) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("Shadow Mask Params BG"),
            layout: &self.params_bgl,
            entries: &[wgpu::BindGroupEntry {
                binding: 0, resource: self.params_buffer.as_entire_binding(),
            }],
        })
    }

    /// 各レイヤぶん mask_raw → (mask_a 経由 H→V) → mask_b を separable バイラテラルでブラーする。
    /// 結果は必ず mask_b。深度ガイドは各レイヤ自身の .a（マスク生成時に同梱した half-res ビュー空間 z）を
    /// 読み、深度エッジを跨ぐ影値の混合（＝カーテンのフチのハロー）を断つ。texture_2d_array に対し
    /// レイヤごとの 2D ビューで RT_SHADOW_MASK_LIGHTS 回掛ける（バイラテラルは単層 2D 前提のため）。
    pub fn blur(&self, device: &wgpu::Device, encoder: &mut wgpu::CommandEncoder, targets: &ShadowMaskTargets) {
        for layer in 0..RT_SHADOW_MASK_LIGHTS as usize {
            self.bilateral.record_layer(
                device, encoder,
                targets.raw_layer_view(layer), targets.a_layer_view(layer), targets.b_layer_view(layer),
                targets.width() as i32, targets.height() as i32,
                SHADOW_MASK_BLUR_RADIUS,
            );
        }
    }
}

// ─── ShadowMaskTargets（動的リソース: 半解像度 4 レイヤ mask_raw/mask_a/mask_b。App が保持）───

/// レイヤ配列テクスチャ 1 枚＋そのビュー群（レイヤごとの 2D ビュー＋全レイヤ D2Array ビュー）。
struct LayeredTex {
    #[allow(dead_code)]
    tex:         wgpu::Texture,
    /// 全レイヤをまとめた D2Array ビュー（サンプル用。deferred の binding10 に渡す）。
    array_view:  wgpu::TextureView,
    /// レイヤごとの単層 2D ビュー（MRT アタッチメント／いもす法ブラーの単層入出力）。
    layer_views: Vec<wgpu::TextureView>,
}

/// 半解像度 4 レイヤのマスクテクスチャ 3 枚（mask_raw=生成 / mask_a,mask_b=ブラー ping-pong）。
/// 各レイヤの .rgb=透過率, .a=half-res ビュー空間深度（バイラテラルブラーの深度ガイドを同梱）。
/// STORAGE_BINDING を要するため RtPool には載せられず本構造体が専有する（AoTargets の 4 レイヤ版）。
/// ブラー後の最終マスクは各レイヤとも mask_b に残る（ShadowMaskPipelines::blur の不変条件）。
pub struct ShadowMaskTargets {
    raw:   Option<LayeredTex>,
    a:     Option<LayeredTex>,
    b:     Option<LayeredTex>,
    hw:    u32,
    hh:    u32,
}

impl Default for ShadowMaskTargets {
    fn default() -> Self { Self::new() }
}

impl ShadowMaskTargets {
    /// 空の（未確保の）ターゲット群を生成する（device 不要・eager 構築可）。
    pub fn new() -> Self {
        Self { raw: None, a: None, b: None, hw: 0, hh: 0 }
    }

    /// 半解像度サイズへ追従する。既存が同サイズなら何もしない（AoTargets.ensure の流儀）。
    pub fn ensure(&mut self, device: &wgpu::Device, hw: u32, hh: u32) {
        let hw = hw.max(1);
        let hh = hh.max(1);
        if self.raw.is_some() && self.hw == hw && self.hh == hh {
            return;
        }
        // mask_raw: 生成パスの MRT 出力＋ブラー入力（.a に view_z 同梱）。
        let raw = make_layered(
            device, "shadow_mask_raw", hw, hh,
            wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
        );
        // mask_a / mask_b: バイラテラルブラーの storage ping-pong（write）＋次段/サンプル入力。
        let storage = wgpu::TextureUsages::STORAGE_BINDING | wgpu::TextureUsages::TEXTURE_BINDING;
        let a = make_layered(device, "shadow_mask_a", hw, hh, storage);
        let b = make_layered(device, "shadow_mask_b", hw, hh, storage);
        self.raw = Some(raw);
        self.a = Some(a);
        self.b = Some(b);
        self.hw = hw;
        self.hh = hh;
    }

    /// 確保済み半解像度幅。
    pub fn width(&self) -> u32 { self.hw }
    /// 確保済み半解像度高さ。
    pub fn height(&self) -> u32 { self.hh }

    /// mask_raw のレイヤ layer の 2D ビュー（MRT アタッチメント／ブラー入力）。
    pub fn raw_layer_view(&self, layer: usize) -> &wgpu::TextureView {
        &self.raw.as_ref().expect("ShadowMaskTargets: ensure 未実行（raw）").layer_views[layer]
    }
    /// mask_a のレイヤ layer の 2D ビュー（ブラー ping）。
    pub fn a_layer_view(&self, layer: usize) -> &wgpu::TextureView {
        &self.a.as_ref().expect("ShadowMaskTargets: ensure 未実行（a）").layer_views[layer]
    }
    /// mask_b のレイヤ layer の 2D ビュー（ブラー ping。結果はここに残る）。
    pub fn b_layer_view(&self, layer: usize) -> &wgpu::TextureView {
        &self.b.as_ref().expect("ShadowMaskTargets: ensure 未実行（b）").layer_views[layer]
    }
    /// mask_b の全レイヤ D2Array ビュー＝ブラー後の最終マスク（deferred の binding10 へ渡す）。
    pub fn b_array_view(&self) -> &wgpu::TextureView {
        &self.b.as_ref().expect("ShadowMaskTargets: ensure 未実行（b array）").array_view
    }
}

/// RT_SHADOW_MASK_LIGHTS レイヤの配列テクスチャ 1 枚を確保し、レイヤ 2D ビュー＋D2Array ビューを作る。
fn make_layered(device: &wgpu::Device, label: &str, w: u32, h: u32, usage: wgpu::TextureUsages) -> LayeredTex {
    let layers = RT_SHADOW_MASK_LIGHTS;
    let tex = device.create_texture(&wgpu::TextureDescriptor {
        label: Some(label),
        size: wgpu::Extent3d { width: w, height: h, depth_or_array_layers: layers },
        mip_level_count: 1, sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: SHADOW_MASK_FORMAT,
        usage,
        view_formats: &[],
    });
    // 全レイヤをまとめた D2Array ビュー（サンプル用）。
    let array_view = tex.create_view(&wgpu::TextureViewDescriptor {
        label: Some(label),
        dimension: Some(wgpu::TextureViewDimension::D2Array),
        ..Default::default()
    });
    // レイヤごとの単層 2D ビュー（MRT アタッチメント／storage 単層入出力）。
    let layer_views = (0..layers).map(|l| {
        tex.create_view(&wgpu::TextureViewDescriptor {
            label: Some(label),
            dimension: Some(wgpu::TextureViewDimension::D2),
            base_array_layer: l,
            array_layer_count: Some(1),
            ..Default::default()
        })
    }).collect();
    LayeredTex { tex, array_view, layer_views }
}

// ============================================================
//  ユニットテスト（レイアウト照合＋WGSL naga 検証＋選定ロジック）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// ShadowMaskParams は 32 バイト（WGSL shadow_mask.wgsl の ShadowMaskParams と一致）。
    #[test]
    fn shadow_mask_params_is_32_bytes() {
        assert_eq!(std::mem::size_of::<ShadowMaskParams>(), 32);
        assert_eq!(SHADOW_MASK_FORMAT, IMOS_BLUR_FORMAT, "マスクと imos の storage フォーマットは一致必須");
    }

    /// WGSL 側 ShadowMaskParams（naga 計算サイズ）が Rust の size_of と一致すること（回帰ガード）。
    /// shadow_mask.wgsl は light_common 等を参照するため実パイプラインと同順で連結して parse する。
    #[test]
    fn wgsl_shadow_mask_params_size_matches_rust() {
        let src = [
            include_str!("shaders/cluster_common.wgsl"),
            include_str!("shaders/ddgi_common.wgsl"),
            include_str!("shaders/light_common.wgsl"),
            include_str!("shaders/rt_shadow_on.wgsl"),
            include_str!("shaders/shadow_mask.wgsl"),
        ].join("\n");
        let module = naga::front::wgsl::parse_str(&src).expect("shadow_mask 連結の parse に失敗");
        let (handle, _) = module.types.iter()
            .find(|(_, t)| t.name.as_deref() == Some("ShadowMaskParams"))
            .expect("WGSL に struct ShadowMaskParams が見つかりません");
        let mut layouter = naga::proc::Layouter::default();
        layouter.update(module.to_ctx()).expect("naga Layouter の計算に失敗");
        assert_eq!(layouter[handle].size as usize, std::mem::size_of::<ShadowMaskParams>(),
            "WGSL の ShadowMaskParams サイズが Rust と一致しません");
    }

    /// マスク生成連結（cluster+ddgi+light+rt_on+shadow_mask, RAY_QUERY 必須）を naga で parse + validate。
    #[test]
    fn shadow_mask_shader_parses_and_validates() {
        let src = [
            include_str!("shaders/cluster_common.wgsl"),
            include_str!("shaders/ddgi_common.wgsl"),
            include_str!("shaders/light_common.wgsl"),
            include_str!("shaders/rt_shadow_on.wgsl"),
            include_str!("shaders/shadow_mask.wgsl"),
        ].join("\n");
        let module = naga::front::wgsl::parse_str(&src)
            .unwrap_or_else(|e| panic!("[shadow_mask] WGSL parse 失敗: {e:?}"));
        let mut v = naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(), naga::valid::Capabilities::RAY_QUERY);
        v.validate(&module).unwrap_or_else(|e| panic!("[shadow_mask] validate 失敗: {e:?}"));
    }

    /// RT_SHADOW_MASK_LIGHTS（Rust）＝ SHADOW_MASK_LAYERS（shadow_mask.wgsl）＝
    /// SURFACE_SHADOW_MASK_SLOTS（surface.wgsl）が一致すること（3 者ズレ防止）。
    #[test]
    fn shadow_mask_layer_count_matches_wgsl() {
        let parse_u32 = |src: &str, name: &str| -> u32 {
            let decl = src.lines().map(str::trim)
                .find(|l| l.starts_with(&format!("const {name}")))
                .unwrap_or_else(|| panic!("WGSL に const {name} が見つかりません"));
            decl.split('=').nth(1).unwrap().trim().trim_end_matches(';').trim().trim_end_matches('u')
                .parse::<u32>().unwrap_or_else(|_| panic!("const {name} が u32 として解釈できません"))
        };
        let mask_layers = parse_u32(include_str!("shaders/shadow_mask.wgsl"), "SHADOW_MASK_LAYERS");
        let surf_slots  = parse_u32(include_str!("shaders/surface.wgsl"), "SURFACE_SHADOW_MASK_SLOTS");
        assert_eq!(mask_layers, RT_SHADOW_MASK_LIGHTS, "shadow_mask.wgsl SHADOW_MASK_LAYERS と Rust 不一致");
        assert_eq!(surf_slots,  RT_SHADOW_MASK_LIGHTS, "surface.wgsl SURFACE_SHADOW_MASK_SLOTS と Rust 不一致");
    }

    /// テスト用のソフト影ライト（soft_radius/intensity 指定）。
    fn soft_light(intensity: f32, soft_radius: f32) -> GpuLight {
        let mut l = GpuLight::point([0.0, 0.0, 0.0], [1.0, 1.0, 1.0], intensity, 10.0);
        l.soft_radius = soft_radius;
        l
    }

    /// 選定は soft_radius=0 を除外し、intensity 降順で最大 4 灯を選ぶ。
    #[test]
    fn selection_excludes_hard_and_ranks_by_intensity() {
        // index: 0=soft(int 5), 1=hard(int 100, soft 0), 2=soft(int 9), 3=soft(int 7)
        let lights = vec![
            soft_light(5.0, 0.1),
            soft_light(100.0, 0.0), // ハード影（soft_radius=0）→ 除外
            soft_light(9.0, 0.2),
            soft_light(7.0, 0.05),
        ];
        let sel = select_shadow_mask_lights(&lights);
        // intensity 降順: idx2(9) > idx3(7) > idx0(5)。idx1 はハードで除外。
        assert_eq!(sel, vec![2, 3, 0], "期待は [2,3,0]（intensity 降順・ハード除外）");
    }

    /// 4 灯上限: 5 灯のソフト影から上位 4 灯（intensity 降順）だけ選ぶ。
    #[test]
    fn selection_caps_at_four() {
        let lights: Vec<GpuLight> = (0..5)
            .map(|i| soft_light((i as f32 + 1.0) * 10.0, 0.1)) // intensity 10,20,30,40,50
            .collect();
        let sel = select_shadow_mask_lights(&lights);
        assert_eq!(sel.len(), RT_SHADOW_MASK_LIGHTS as usize);
        // 上位 4 灯 = index 4(50),3(40),2(30),1(20)。index 0(10) は溢れる。
        assert_eq!(sel, vec![4, 3, 2, 1]);
    }

    /// assign はスロット番号を選定ライトの shadow_mask_slot へ書き、非選定は既定（-1）のまま。
    #[test]
    fn assign_writes_slots() {
        let mut lights = vec![
            soft_light(5.0, 0.1),   // idx0
            soft_light(9.0, 0.2),   // idx1
            soft_light(0.0, 0.0),   // idx2 ハード（既定 -1 のまま）
        ];
        // 既定の -1 を確認（GpuLight::point は shadow_mask_slot=-1）。
        assert_eq!(lights[2].shadow_mask_slot, -1.0);
        let sel = assign_shadow_mask_slots(&mut lights);
        assert_eq!(sel, vec![1, 0]);           // intensity 降順: idx1(9) → slot0, idx0(5) → slot1
        assert_eq!(lights[1].shadow_mask_slot, 0.0);
        assert_eq!(lights[0].shadow_mask_slot, 1.0);
        assert_eq!(lights[2].shadow_mask_slot, -1.0); // ハードは非対象のまま
    }
}
