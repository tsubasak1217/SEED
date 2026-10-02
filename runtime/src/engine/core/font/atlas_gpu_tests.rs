// ============================================================
//  font/atlas_gpu_tests.rs — グリフアトラスのページの追加・追い出しを実 GPU の FontSystem で確かめる（テストだけのモジュール）
//
//  【なぜ要るか】
//  配置の規則（atlas_pages.rs）は wgpu 無しで単体テストしているが、FontSystem の流れ（字のページの印・満杯で入らなかった字の
//  覚え方・追い出しの後の入れ直し・テクスチャ配列の作り直しと bind group）は GPU の資源を持つので、実 GPU で通しで確かめる。
//  実 GPU が要るので `--ignored` で走らせる（ほかの GPU テストと `GPU_TEST_LOCK` で直列にする）。
// ============================================================

use super::atlas::GlyphKey;
use super::glyph_field::DistanceFieldKind;
use super::msdf::ColoringStrategy;
use super::{FontConfig, FontSystem, ATLAS_EVICT_MIN_IDLE_FRAMES};

/// 試験のアトラスの 1 ページの一辺（1 チャネルの SDF の英大文字 1 字〈em 64 + 四方の余白 8 ≒ 45〜50 × 50〜55 テクセル〉が
/// 1 ページに 1 字だけ入る大きさ。横に 2 字・縦に 2 段は入らない）。
const TEST_PAGE_SIZE: u32 = 72;
/// 試験のページの上限。
const TEST_MAX_PAGES: u32 = 2;

/// 実 GPU のデバイスを作る（無ければ None＝試験を飛ばす）。
fn test_device() -> Option<(wgpu::Device, wgpu::Queue)> {
    let instance = wgpu::Instance::new(&wgpu::InstanceDescriptor::default());
    let adapter = pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions::default())).ok()?;
    pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor::default())).ok()
}

/// フレームを `n` 回進め、そのたびに `text` だけを使う（そのページに使った印が付く）。
fn run_frames(fs: &mut FontSystem, device: &wgpu::Device, queue: &wgpu::Queue, text: &str, n: u64) {
    for _ in 0..n {
        fs.begin_frame();
        let _ = fs.prepare_glyphs(text, "");
        fs.flush(device, queue);
    }
}

/// 【ページの追加と追い出し】1 ページ 1 字・上限 2 ページのアトラスで:
///   1. "A"・"B" は 2 ページに入る（ページを足したフレームの flush でテクスチャを作り直す）
///   2. "B" だけを使い続けた後の "C" は、使われていない "A" のページを追い出して入る（"A" はキャッシュから消える）
///   3. 直後の "D" は、どのページも最近使われているので入らない（追い出さない＝入れ替えの往復を作らない）
///   4. "C" だけを使い続けると、フレームの頭で "B" のページが追い出され、"D" が入る
#[test]
#[ignore = "実 GPU が必要。--ignored で実行する"]
fn pages_are_added_then_idle_page_is_evicted_on_gpu() {
    let _guard = crate::engine::core::renderer::GPU_TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let Some((device, queue)) = test_device() else {
        eprintln!("[font atlas] GPU が無いので飛ばす");
        return;
    };
    let config = FontConfig {
        atlas_size: TEST_PAGE_SIZE,
        field: DistanceFieldKind::Sdf,
        coloring: ColoringStrategy::default(),
        max_atlas_pages: TEST_MAX_PAGES,
        atlas_label: "Glyph Atlas Test",
    };
    let mut fs = FontSystem::new(&device, wgpu::TextureFormat::Rgba8Unorm, wgpu::TextureFormat::Depth32Float, config).expect("組み込みの書体");

    // ── 1. 2 ページに 1 字ずつ ──
    fs.begin_frame();
    let ab = fs.prepare_glyphs("AB", "");
    assert_eq!(ab.len(), 2, "A・B とも入る");
    let size = |i: usize| [ab[i].1.size_em[0] * 64.0, ab[i].1.size_em[1] * 64.0];
    assert_eq!((ab[0].1.page, ab[1].1.page), (0, 1), "1 ページ 1 字なので 2 ページ目へ（大きさ {:?} {:?}）", size(0), size(1));
    assert_eq!(fs.atlas.page_count(), 2);
    fs.flush(&device, &queue);

    // ── 2. B だけを使い続けた後の C は A のページを追い出して入る ──
    run_frames(&mut fs, &device, &queue, "B", ATLAS_EVICT_MIN_IDLE_FRAMES + 1);
    fs.begin_frame();
    let c = fs.prepare_glyphs("C", "");
    assert_eq!(c.len(), 1, "C は追い出しで入る");
    assert_eq!(c[0].1.page, 0, "A のページを使い直す");
    assert_eq!(fs.atlas_evictions(), 1);
    assert!(fs.atlas.get(&GlyphKey { font_id: 0, codepoint: 'A' }).is_none(), "A はキャッシュから消えている");
    fs.flush(&device, &queue);

    // ── 3. 直後の D: どのページも最近使われているので入らない ──
    fs.begin_frame();
    let _ = fs.prepare_glyphs("BC", "");
    let d = fs.prepare_glyphs("D", "");
    assert!(d.is_empty(), "最近使ったページは追い出さない");
    let evictions_before = fs.atlas_evictions();
    fs.flush(&device, &queue);

    // ── 4. C だけを使い続けると、フレームの頭で B のページが追い出され、D が入る ──
    run_frames(&mut fs, &device, &queue, "C", ATLAS_EVICT_MIN_IDLE_FRAMES + 1);
    assert_eq!(fs.atlas_evictions(), evictions_before + 1, "満杯で入らなかった字があるので、フレームの頭で追い出す");
    let d = fs.prepare_glyphs("D", "");
    assert_eq!(d.len(), 1, "空きができたので D が入る");
    assert_eq!(d[0].1.page, 1, "B のページを使い直す");
    fs.flush(&device, &queue);
    device.poll(wgpu::PollType::Wait).expect("GPU の完了待ち");
}
