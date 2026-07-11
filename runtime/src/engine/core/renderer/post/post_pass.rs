// ============================================================
//  post_pass.rs — ポストパス抽象（宣言的フルスクリーン合成）
//
//  「入力テクスチャ（1〜N）＋任意のマスクテクスチャ＋出力 RT＋パラメータ UBO」を
//  フルスクリーン三角形で処理する仕組み（Phase R3 土台）。パイプラインは既存の
//  TOML 機構（RenderPipelineBuilder + WGSL リフレクション）にそのまま乗せる。
//
//  バインド規約（WGSL 側と一致させること）:
//    group 0: パラメータ UBO（uniform）
//    group 1: 入力テクスチャ0 + サンプラー（binding 0 = tex, 1 = sampler）
//    group 2: マスクテクスチャ + サンプラー（任意。未指定時は白 1x1 を既定バインド）
//  マスクを常にバインド可能にすることで「テクスチャ単位・マスクのかけやすさ」を担保する。
//
//  複数入力（group 1 に texture を増やす）や追加 group は WGSL の宣言に応じて
//  リフレクションが BGL を生成するため、本ランナーを拡張して対応する（R4 のブルーム合成等）。
// ============================================================

use super::super::pipeline_config::RenderPipelineBuilder;

/// ポストパスパイプライン（TOML + WGSL リフレクションから構築）。
pub struct PostPipeline {
    /// レンダーパイプライン本体（フルスクリーン三角形描画）。
    pub pipeline: wgpu::RenderPipeline,
    /// group 番号順の BindGroupLayout（0=params, 1=input+sampler, 2=mask+sampler ...）。
    /// バインドグループ生成に使う。
    pub bgls: Vec<wgpu::BindGroupLayout>,
}

impl PostPipeline {
    /// 出力フォーマット `out_format` のポストパイプラインを TOML から構築する。
    ///
    /// `resolve` は shader_sources のファイル名 → `&'static str` ソースへの変換関数。
    /// ポストパスは深度を使わないため（TOML 側 `no_depth = true`）、depth_format は
    /// 参照されないダミー値を渡す。
    pub fn from_toml<F: Fn(&str) -> &'static str>(
        device:     &wgpu::Device,
        toml_src:   &str,
        out_format: wgpu::TextureFormat,
        cache:      Option<&wgpu::PipelineCache>,
        resolve:    F,
    ) -> Self {
        // out_format を builder の surface_format 引数として渡す（TOML の color_format="surface" が解決される）。
        // depth_format は no_depth=true のため未使用（任意の深度フォーマットでよい）。
        let (pipeline, bgls) = RenderPipelineBuilder::new(
            device,
            toml_src,
            out_format,
            wgpu::TextureFormat::Depth24PlusStencil8,
        )
        .with_cache(cache)
        .build(resolve);

        Self { pipeline, bgls }
    }
}

/// 1 つのポストパスを実行する（フルスクリーン三角形描画）。
///
/// - `input`      : 入力テクスチャ（group 1）
/// - `mask`       : マスクテクスチャ（group 2）。None なら `white_mask` を使う
/// - `white_mask` : マスク未指定時の既定（白 1x1、全面適用）
/// - `sampler`    : 入力・マスク共通のサンプラー（リニア clamp）
/// - `params`     : パラメータ UBO のバイト列（group 0）
/// - `output`     : 出力 RT ビュー（Clear で全面上書き。深度なし）
///
/// バインドグループは `pipe.bgls` から動的に構築する。group 2（マスク）は
/// パイプラインが 3 グループ以上を宣言している場合のみ設定する。
#[allow(clippy::too_many_arguments)]
pub fn run_post_stage(
    device:     &wgpu::Device,
    encoder:    &mut wgpu::CommandEncoder,
    pipe:       &PostPipeline,
    input:      &wgpu::TextureView,
    mask:       Option<&wgpu::TextureView>,
    white_mask: &wgpu::TextureView,
    sampler:    &wgpu::Sampler,
    params:     &[u8],
    output:     &wgpu::TextureView,
    label:      &str,
) {
    use wgpu::util::DeviceExt;

    // ── group 0: パラメータ UBO ──────────────────────────────
    let param_buf = device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label:    Some("Post Params UBO"),
        contents: params,
        usage:    wgpu::BufferUsages::UNIFORM,
    });
    let bg0 = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label:   Some("Post BG0 params"),
        layout:  &pipe.bgls[0],
        entries: &[wgpu::BindGroupEntry { binding: 0, resource: param_buf.as_entire_binding() }],
    });

    // ── group 1: 入力テクスチャ + サンプラー ────────────────
    let bg1 = device.create_bind_group(&wgpu::BindGroupDescriptor {
        label:   Some("Post BG1 input"),
        layout:  &pipe.bgls[1],
        entries: &[
            wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(input) },
            wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(sampler) },
        ],
    });

    // ── group 2: マスクテクスチャ + サンプラー（任意）───────
    let mask_view = mask.unwrap_or(white_mask);
    let bg2 = if pipe.bgls.len() >= 3 {
        Some(device.create_bind_group(&wgpu::BindGroupDescriptor {
            label:   Some("Post BG2 mask"),
            layout:  &pipe.bgls[2],
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(mask_view) },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(sampler) },
            ],
        }))
    } else {
        None
    };

    // ── フルスクリーン描画（深度なし・全面 Clear）───────────
    let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
        label: Some(label),
        color_attachments: &[Some(wgpu::RenderPassColorAttachment {
            view:           output,
            resolve_target: None,
            ops: wgpu::Operations {
                // フルスクリーン三角形が全ピクセルを書き込むため Clear で十分（Load 不要）。
                load:  wgpu::LoadOp::Clear(wgpu::Color::BLACK),
                store: wgpu::StoreOp::Store,
            },
        })],
        depth_stencil_attachment: None,
        occlusion_query_set:      None,
        timestamp_writes:         None,
    });
    pass.set_pipeline(&pipe.pipeline);
    pass.set_bind_group(0, &bg0, &[]);
    pass.set_bind_group(1, &bg1, &[]);
    if let Some(bg2) = &bg2 {
        pass.set_bind_group(2, bg2, &[]);
    }
    // フルスクリーン三角形（3 頂点、頂点バッファなし）を 1 インスタンス描画。
    pass.draw(0..3, 0..1);
}
