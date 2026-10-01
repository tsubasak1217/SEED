// ============================================================
//  render_profile/tests_gpu.rs — 実 GPU で「ui の構成は 3D の資源を作らない」を確かめるテスト（--ignored）
//
//  【何を確かめるか】
//  同じデバイスで DrawContext（パイプライン一式と 3D の共有資源）を、ui と full の構成でそれぞれ作り、
//  GPU メモリの計測（gpu_mem）の記録から次を比べる。
//    - ui   … bindless の資源が 0 件・シャドウマップと GI のアトラスは 1x1 の置き場・スキニングの compute の
//             パイプライン本体なし（ローカルメモリの予約なし）・RT の資源なし
//    - full … シャドウマップは設定どおりの解像度・スキニングのパイプラインあり（従来どおり）
//  実行: cargo test render_profile::tests_gpu -- --ignored --nocapture
// ============================================================

use std::sync::Arc;

use crate::engine::core::renderer::gpu_mem::registry::ResourceKind;
use crate::engine::core::renderer::gpu_mem::{self, GpuMemCategory};
use crate::engine::core::renderer::render_profile::{self, builtin_profile_catalog, RenderProfile};
use crate::engine::methods::drawer::DrawContext;

/// 置き場の深度・アトラスとみなす上限（1x1 の層を数枚。数十バイトに収まる）。
const PLACEHOLDER_MAX_BYTES: u64 = 64;

/// テストの間だけ書き換えるグローバル（RT・bindless の対応・計測の有無・今の構成）を、終わりに必ず元へ戻す
/// （表明が失敗して途中で抜けても、ui の構成が他の GPU テストへ残らないようにする）。
struct GlobalsRestore {
    /// 元の RT の対応。
    rt: bool,
    /// 元の bindless の対応。
    bindless: bool,
}

impl Drop for GlobalsRestore {
    fn drop(&mut self) {
        super::super::rt_shadow::set_rt_shadows_supported(self.rt);
        super::super::bindless::set_bindless_supported(self.bindless);
        install_profile(super::catalog::FALLBACK_PROFILE_NAME);
        gpu_mem::request(false);
    }
}

/// 名前の構成を登録する（組み込みの定義から）。
fn install_profile(name: &str) {
    let entry = builtin_profile_catalog().find(name).expect("組み込みの構成がある");
    let profile = RenderProfile {
        name: entry.name.clone(),
        label: entry.label.clone(),
        flags: entry.flags,
        ..RenderProfile::default()
    };
    render_profile::install(&profile);
}

/// 分類ごとのテクスチャの（件数, 最大のバイト数, 合計のバイト数）を、記録の写しのうち `since_generation` 以降のものから数える
/// （同じ分類の小さな uniform バッファ〈影の行列・GI の定数〉は数えない＝深度・アトラスの大きさだけを見る）。
fn texture_stats(category: GpuMemCategory, since_generation: u64) -> (usize, u64, u64) {
    let (entries, _) = gpu_mem::registry::snapshot_global();
    let mine: Vec<_> = entries
        .into_iter()
        .filter(|e| {
            e.category == category && e.kind == ResourceKind::Texture && e.generation >= since_generation
        })
        .collect();
    let max = mine.iter().map(|e| e.bytes).max().unwrap_or(0);
    let sum = mine.iter().map(|e| e.bytes).sum();
    (mine.len(), max, sum)
}

#[test]
#[ignore = "実 GPU が必要。--ignored で実行する"]
fn ui_profile_skips_3d_resources_on_gpu() {
    // RT／バインドレスのグローバルと構成のグローバルを書き換えるので、他の GPU テストと直列化する。
    let _guard = super::super::GPU_TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor::default());
    let Ok(adapter) = pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions::default())) else {
        eprintln!("[render_profile] GPU アダプタが見つからないため検証をスキップ");
        return;
    };
    // 本体（renderer/mod.rs）と同じ上限（スキニングの静的 BGL が storage 12 本・group 0〜4・G-Buffer の MRT）。
    let Ok((device, queue)) = pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor {
        required_limits: wgpu::Limits {
            max_storage_buffers_per_shader_stage: 12,
            max_bind_groups: 5,
            max_color_attachment_bytes_per_sample: adapter.limits().max_color_attachment_bytes_per_sample,
            ..wgpu::Limits::default()
        },
        ..Default::default()
    })) else {
        eprintln!("[render_profile] デバイス生成に失敗したため検証をスキップ");
        return;
    };
    let device = Arc::new(device);
    let queue = Arc::new(queue);

    // RT・bindless は使わない（ui の構成と同じ。full でもこのテストでは作らない）。元の値は終わりに戻す（GlobalsRestore）。
    let _restore = GlobalsRestore {
        rt: super::super::rt_shadow::rt_shadows_supported(),
        bindless: super::super::bindless::bindless_supported(),
    };
    super::super::rt_shadow::set_rt_shadows_supported(false);
    super::super::bindless::set_bindless_supported(false);
    gpu_mem::request(true);

    let make = || {
        DrawContext::new(
            Arc::clone(&device),
            Arc::clone(&queue),
            super::super::HDR_FORMAT,
            wgpu::TextureFormat::Bgra8UnormSrgb,
            super::super::DEPTH_FORMAT,
            None,
        )
    };

    // ── ui ──
    install_profile("ui");
    gpu_mem::advance_generation();
    let ui_from = gpu_mem::current_generation();
    let ui_ctx = make();
    let (shadow_count, shadow_max, _) = texture_stats(GpuMemCategory::Shadow, ui_from);
    let (_, gi_max, _) = texture_stats(GpuMemCategory::Gi, ui_from);
    let bindless_count = gpu_mem::registry::snapshot_global()
        .0
        .into_iter()
        .filter(|e| e.category == GpuMemCategory::Bindless && e.generation >= ui_from)
        .count();
    eprintln!("[render_profile] ui: 影 {shadow_count} 件・最大 {shadow_max} B / GI 最大 {gi_max} B / bindless {bindless_count} 件");
    assert!(ui_ctx.pipelines.skin_compute.pipeline.is_none(), "ui はスキニングのパイプライン本体を作らない");
    assert!(ui_ctx.bindless.is_none(), "ui は bindless を作らない");
    assert!(ui_ctx.rt_shadow.is_none(), "ui は RT の資源を作らない");
    assert_eq!(bindless_count, 0, "ui は bindless の資源を 1 つも作らない");
    assert!(shadow_count > 0, "影の置き場（バインド用）は作る");
    assert!(shadow_max <= PLACEHOLDER_MAX_BYTES, "影は 1x1 の置き場だけ（最大 {shadow_max} B）");
    assert!(gi_max <= PLACEHOLDER_MAX_BYTES, "GI のアトラスは 1x1 の置き場だけ（最大 {gi_max} B）");
    drop(ui_ctx);

    // ── full（従来どおり）──
    install_profile("full");
    gpu_mem::advance_generation();
    let full_from = gpu_mem::current_generation();
    let full_ctx = make();
    let (_, full_shadow_max, _) = texture_stats(GpuMemCategory::Shadow, full_from);
    let (_, full_gi_max, _) = texture_stats(GpuMemCategory::Gi, full_from);
    eprintln!("[render_profile] full: 影 最大 {full_shadow_max} B / GI 最大 {full_gi_max} B");
    assert!(full_ctx.pipelines.skin_compute.pipeline.is_some(), "full はスキニングのパイプラインを作る（従来どおり）");
    let csm = u64::from(super::super::shadow_quality().resolution);
    let csm_bytes = csm * csm * super::super::CSM_CASCADE_COUNT as u64 * 4;
    let spot = u64::from(super::super::shadow::SPOT_SHADOW_SIZE);
    let spot_bytes = spot * spot * super::super::MAX_SHADOW_SPOTS as u64 * 4;
    assert_eq!(full_shadow_max, csm_bytes.max(spot_bytes), "full の影は設定どおりの解像度（従来どおり）");
    assert!(full_gi_max > PLACEHOLDER_MAX_BYTES, "full の GI のアトラスは既定の大きさ（従来どおり）");
    drop(full_ctx);
    // グローバル（構成は full＝既定・計測は無効・RT と bindless の対応）は _restore の Drop が元へ戻す。
}
