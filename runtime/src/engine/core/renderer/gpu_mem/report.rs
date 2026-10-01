// ============================================================
//  gpu_mem/report.rs — GPU メモリの内訳の集計・ログの行・JSON
//
//  【1 回の内訳に並べるもの】
//    - 追跡した資源の生存の推定の合計（テクスチャ・バッファ別、件数）
//    - wgpu-hal の計数（資源ごとに確保したブロックの合計＝アロケータの 2 の冪への切り上げを含む・
//      GPU メモリの確保〈vkAllocateMemory〉の回数・加速構造。Cargo の wgpu の counters 機能）
//    - 実際の確保（Vulkan のヒープの使用量。heap_budget.rs）と、追跡した合計との差
//    - スワップチェインの見積り（実際の確保に入るかはドライバ次第なので別枠で書く）
//    - 分類ごとの合計（大きい順）と上位 TOP_ENTRY_COUNT 件
//    - 作り直しで外した分・作った累計（registry.rs の統計）
//  ログは 1 行ずつ `[SEED GPU MEM]` で始める（Android の logcat でも行ごとに読める）。
// ============================================================

use std::collections::BTreeMap;

use super::category::GpuMemCategory;
use super::heap_budget::{total_usage, HeapUsage};
use super::registry::{GpuMemEntry, RegistryStats, ResourceKind};
use super::LOG_TAG;

/// 上位の一覧に出す件数。
pub const TOP_ENTRY_COUNT: usize = 20;

/// 1 MiB のバイト数（表示の単位）。
const BYTES_PER_MIB: f64 = 1024.0 * 1024.0;

/// ログの表の列の幅: 分類の名前（文字数）。
const NAME_COLUMN_WIDTH: usize = 28;
/// ログの表の列の幅: 大きさ（"123.4 MiB"）。
const SIZE_COLUMN_WIDTH: usize = 10;
/// ログの表の列の幅: 上位の順位。
const RANK_COLUMN_WIDTH: usize = 2;

/// スワップチェインの見積り（`Renderer` が設定から作る）。
#[derive(Clone, Debug, serde::Serialize)]
pub struct SwapchainEstimate {
    /// 幅（画素）。
    pub width: u32,
    /// 高さ（画素）。
    pub height: u32,
    /// 形式（表示用）。
    pub format: String,
    /// 1 画素のバイト数。
    pub bytes_per_pixel: u32,
    /// 枚数の見積り（要求したフレーム遅延 + 1。ドライバはこれより多く作ることがある）。
    pub image_count: u32,
    /// 合計の見積り（バイト）。
    pub bytes: u64,
}

impl SwapchainEstimate {
    /// 寸法・形式・枚数から見積りを作る。
    pub fn new(width: u32, height: u32, format: wgpu::TextureFormat, image_count: u32) -> Self {
        let bytes_per_pixel = super::size::block_bytes(format).unwrap_or(0);
        let bytes = u64::from(width) * u64::from(height) * u64::from(bytes_per_pixel) * u64::from(image_count);
        Self { width, height, format: format!("{format:?}"), bytes_per_pixel, image_count, bytes }
    }
}

/// wgpu-hal の内部の計数（GPU メモリに関わる分だけ。`wgpu::Device::get_internal_counters`）。
///
/// バイト数は「資源ごとに確保したブロック」の合計で、gpu-alloc の 2 の冪への切り上げを含む
/// （まとめ取りした塊の空きは含まない。それはヒープの使用量で見る）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize)]
pub struct HalMemory {
    /// バッファのブロックの合計（バイト）。
    pub buffer_bytes: u64,
    /// テクスチャのブロックの合計（バイト）。
    pub texture_bytes: u64,
    /// 加速構造（BLAS / TLAS）の合計（バイト）。
    pub acceleration_structure_bytes: u64,
    /// GPU メモリの確保（vkAllocateMemory）の生きている数。
    pub memory_allocations: u64,
    /// バッファの数。
    pub buffers: u64,
    /// テクスチャの数。
    pub textures: u64,
}

impl HalMemory {
    /// wgpu の計数から作る（負の値〈計数の取り違え〉は 0 に丸める）。
    pub fn from_counters(counters: &wgpu::InternalCounters) -> Self {
        // 計数は isize（足し引きの途中で負になりうる型）なので 0 未満は 0 に丸める。
        let clamp = |value: isize| value.max(0) as u64;
        let hal = &counters.hal;
        Self {
            buffer_bytes: clamp(hal.buffer_memory.read()),
            texture_bytes: clamp(hal.texture_memory.read()),
            acceleration_structure_bytes: clamp(hal.acceleration_structure_memory.read()),
            memory_allocations: clamp(hal.memory_allocations.read()),
            buffers: clamp(hal.buffers.read()),
            textures: clamp(hal.textures.read()),
        }
    }

    /// ブロックの合計（バッファ＋テクスチャ＋加速構造）。
    pub fn total_bytes(&self) -> u64 {
        self.buffer_bytes + self.texture_bytes + self.acceleration_structure_bytes
    }
}

/// 分類ごとの合計。
#[derive(Clone, Debug, serde::Serialize)]
pub struct CategoryTotal {
    /// 分類。
    pub category: GpuMemCategory,
    /// 分類の英字のキー（JSON 用）。
    pub key: &'static str,
    /// 分類の日本語の名前。
    pub name: &'static str,
    /// 合計（バイト）。
    pub bytes: u64,
    /// 件数。
    pub count: usize,
}

/// 1 回の内訳。
#[derive(Clone, Debug, serde::Serialize)]
pub struct GpuMemReport {
    /// 出した理由（起動後・背面へ・IPC など）。
    pub reason: String,
    /// 出したフレーム。
    pub frame: u64,
    /// 文脈（描画の構成など。呼ぶ側が渡す 1 行）。
    pub context: String,
    /// 追跡した資源の生存の推定の合計（バイト）。
    pub tracked_bytes: u64,
    /// そのうちテクスチャ（バイト）。
    pub texture_bytes: u64,
    /// そのうちバッファ（バイト）。
    pub buffer_bytes: u64,
    /// 生きているとみなす資源の件数。
    pub resource_count: usize,
    /// 分類ごとの合計（大きい順）。
    pub categories: Vec<CategoryTotal>,
    /// 大きい順の上位（TOP_ENTRY_COUNT 件）。
    pub top: Vec<GpuMemEntry>,
    /// 記録の統計。
    pub stats: RegistryStats,
    /// wgpu-hal の計数（取れなければ None）。
    pub hal: Option<HalMemory>,
    /// 実際の確保（ヒープごと。取れなければ None）。
    pub heaps: Option<Vec<HeapUsage>>,
    /// 実際の確保の合計（バイト。取れなければ None）。
    pub heap_usage_bytes: Option<u64>,
    /// スワップチェインの見積り（無ければ None）。
    pub swapchain: Option<SwapchainEstimate>,
}

/// 生存の推定の一覧と統計から内訳を作る（純関数）。
pub fn build_report(
    reason: &str,
    frame: u64,
    context: &str,
    entries: Vec<GpuMemEntry>,
    stats: RegistryStats,
    hal: Option<HalMemory>,
    heaps: Option<Vec<HeapUsage>>,
    swapchain: Option<SwapchainEstimate>,
) -> GpuMemReport {
    let tracked_bytes = entries.iter().map(|e| e.bytes).sum();
    let texture_bytes = entries.iter().filter(|e| e.kind == ResourceKind::Texture).map(|e| e.bytes).sum();
    let buffer_bytes = entries.iter().filter(|e| e.kind == ResourceKind::Buffer).map(|e| e.bytes).sum();
    let resource_count = entries.len();

    // 分類ごとに合計する（BTreeMap で列挙の順を安定させてから、大きい順に並べる）。
    let mut by_category: BTreeMap<GpuMemCategory, (u64, usize)> = BTreeMap::new();
    for entry in &entries {
        let slot = by_category.entry(entry.category).or_insert((0, 0));
        slot.0 += entry.bytes;
        slot.1 += 1;
    }
    let mut categories: Vec<CategoryTotal> = by_category
        .into_iter()
        .map(|(category, (bytes, count))| CategoryTotal {
            category,
            key: category.key(),
            name: category.name(),
            bytes,
            count,
        })
        .collect();
    categories.sort_by(|a, b| b.bytes.cmp(&a.bytes).then(a.category.cmp(&b.category)));

    // 上位（大きい順。同じ大きさは場所・ラベルで順を決めて結果を安定させる）。
    let mut top = entries;
    top.sort_by(|a, b| {
        b.bytes
            .cmp(&a.bytes)
            .then_with(|| a.site_file.cmp(&b.site_file))
            .then_with(|| a.site_line.cmp(&b.site_line))
            .then_with(|| a.label.cmp(&b.label))
    });
    top.truncate(TOP_ENTRY_COUNT);

    let heap_usage_bytes = heaps.as_deref().map(total_usage);
    GpuMemReport {
        reason: reason.to_string(),
        frame,
        context: context.to_string(),
        tracked_bytes,
        texture_bytes,
        buffer_bytes,
        resource_count,
        categories,
        top,
        stats,
        hal,
        heaps,
        heap_usage_bytes,
        swapchain,
    }
}

/// バイトを MiB の文字列にする（小数 1 桁）。
pub fn mib(bytes: u64) -> String {
    format!("{:.1} MiB", bytes as f64 / BYTES_PER_MIB)
}

impl GpuMemReport {
    /// ログの行（行頭の印つき）。
    pub fn log_lines(&self) -> Vec<String> {
        let mut lines = Vec::new();
        lines.push(format!(
            "{LOG_TAG} ===== GPU メモリの内訳（理由: {}・frame={}）{} =====",
            self.reason, self.frame, self.context
        ));
        lines.push(format!(
            "{LOG_TAG} 追跡した資源（生存の推定）: {}（テクスチャ {} / バッファ {}・{} 件）",
            mib(self.tracked_bytes),
            mib(self.texture_bytes),
            mib(self.buffer_bytes),
            self.resource_count
        ));
        if let Some(hal) = &self.hal {
            lines.push(format!(
                "{LOG_TAG} wgpu-hal の確保ブロック（切り上げ込み）: {}（テクスチャ {} {} 個 / バッファ {} {} 個 / 加速構造 {}）・GPU メモリの確保 {} 回",
                mib(hal.total_bytes()),
                mib(hal.texture_bytes),
                hal.textures,
                mib(hal.buffer_bytes),
                hal.buffers,
                mib(hal.acceleration_structure_bytes),
                hal.memory_allocations
            ));
        }
        match (&self.heaps, self.heap_usage_bytes) {
            (Some(heaps), Some(total)) => {
                let gap = total as i128 - self.tracked_bytes as i128;
                lines.push(format!(
                    "{LOG_TAG} 実際の確保（VK_EXT_memory_budget のプロセス使用量）: {}（追跡との差 {}{}。アロケータのまとめ取り・切り上げ・スワップチェイン・ドライバ内部など）",
                    mib(total),
                    if gap < 0 { "-" } else { "+" },
                    mib(gap.unsigned_abs() as u64)
                ));
                for heap in heaps.iter().filter(|h| h.usage > 0 || h.device_local) {
                    lines.push(format!(
                        "{LOG_TAG}   heap{} {}: 使用 {} / 予算 {} / 大きさ {}",
                        heap.heap_index,
                        if heap.device_local { "DEVICE_LOCAL" } else { "HOST" },
                        mib(heap.usage),
                        mib(heap.budget),
                        mib(heap.heap_size)
                    ));
                }
            }
            _ => lines.push(format!(
                "{LOG_TAG} 実際の確保: 取れません（Vulkan でない・VK_EXT_memory_budget 非対応）"
            )),
        }
        if let Some(sc) = &self.swapchain {
            lines.push(format!(
                "{LOG_TAG} スワップチェイン（見積り・別枠）: {}（{}x{} {} × {} 枚）",
                mib(sc.bytes),
                sc.width,
                sc.height,
                sc.format,
                sc.image_count
            ));
        }
        lines.push(format!("{LOG_TAG} 分類ごと（大きい順）:"));
        for c in &self.categories {
            lines.push(format!(
                "{LOG_TAG}   {:<name_w$} {:>size_w$}  {} 件",
                c.name,
                mib(c.bytes),
                c.count,
                name_w = NAME_COLUMN_WIDTH,
                size_w = SIZE_COLUMN_WIDTH
            ));
        }
        lines.push(format!("{LOG_TAG} 上位 {} 件:", self.top.len()));
        for (rank, e) in self.top.iter().enumerate() {
            lines.push(format!(
                "{LOG_TAG}   {:>rank_w$}. {:>size_w$}  {}  {} \"{}\" {}  ({}:{})",
                rank + 1,
                mib(e.bytes),
                e.category.name(),
                e.kind.name(),
                e.label,
                e.detail,
                e.site_file,
                e.site_line,
                rank_w = RANK_COLUMN_WIDTH,
                size_w = SIZE_COLUMN_WIDTH
            ));
        }
        lines.push(format!(
            "{LOG_TAG} 作った累計 {} 件 / {}・作り直しで外した分 {} 件 / {}",
            self.stats.created_count,
            mib(self.stats.created_bytes),
            self.stats.replaced_count,
            mib(self.stats.replaced_bytes)
        ));
        lines
    }

    /// ログへ出す（標準エラー。Android は logcat）。
    pub fn log(&self) {
        for line in self.log_lines() {
            eprintln!("{line}");
        }
    }

    /// JSON の文字列（IPC の応答のファイル・検証の比べ合わせ用）。
    pub fn to_json(&self) -> String {
        serde_json::to_string_pretty(self).unwrap_or_else(|e| format!("{{\"error\":\"{e}\"}}"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 記録を短く作る補助。
    fn entry(category: GpuMemCategory, kind: ResourceKind, bytes: u64, label: &str) -> GpuMemEntry {
        GpuMemEntry {
            kind,
            category,
            label: label.to_string(),
            detail: String::new(),
            bytes,
            site_file: "src/x.rs".to_string(),
            site_line: 1,
            frame: 0,
            generation: 0,
        }
    }

    /// 分類ごとの合計は大きい順、上位は TOP_ENTRY_COUNT 件まで、テクスチャ・バッファ別の合計も合う。
    #[test]
    fn totals_and_ordering() {
        let mut entries = vec![
            entry(GpuMemCategory::Shadow, ResourceKind::Texture, 64, "shadow"),
            entry(GpuMemCategory::UiText, ResourceKind::Texture, 8, "atlas"),
            entry(GpuMemCategory::UiText, ResourceKind::Buffer, 4, "stream"),
        ];
        for i in 0..30 {
            entries.push(entry(GpuMemCategory::Other, ResourceKind::Buffer, 1, &format!("b{i}")));
        }
        let r = build_report("test", 3, "", entries, RegistryStats::default(), None, None, None);
        assert_eq!(r.tracked_bytes, 64 + 8 + 4 + 30);
        assert_eq!(r.texture_bytes, 72);
        assert_eq!(r.buffer_bytes, 34);
        assert_eq!(r.categories[0].category, GpuMemCategory::Shadow);
        assert_eq!(r.categories[1].category, GpuMemCategory::Other);
        assert_eq!(r.categories[2].category, GpuMemCategory::UiText);
        assert_eq!(r.categories[2].count, 2);
        assert_eq!(r.top.len(), TOP_ENTRY_COUNT);
        assert_eq!(r.top[0].label, "shadow");
    }

    /// 実際の確保が取れたときは合計と差を出し、取れないときはそう書く。
    #[test]
    fn heap_lines() {
        let heaps = vec![HeapUsage { heap_index: 0, device_local: true, heap_size: 1 << 30, usage: 3 << 20, budget: 1 << 29 }];
        let r = build_report("t", 0, "", vec![], RegistryStats::default(), None, Some(heaps), None);
        assert_eq!(r.heap_usage_bytes, Some(3 << 20));
        assert!(r.log_lines().iter().any(|l| l.contains("実際の確保（VK_EXT_memory_budget")));
        let r = build_report("t", 0, "", vec![], RegistryStats::default(), None, None, None);
        assert!(r.log_lines().iter().any(|l| l.contains("取れません")));
    }

    /// スワップチェインの見積りは 幅 × 高さ × 画素のバイト数 × 枚数。
    #[test]
    fn swapchain_estimate() {
        let s = SwapchainEstimate::new(1080, 2400, wgpu::TextureFormat::Rgba8UnormSrgb, 3);
        assert_eq!(s.bytes, 1080 * 2400 * 4 * 3);
    }

    /// JSON は分類のキーを持つ（検証の比べ合わせが読む）。
    #[test]
    fn json_has_category_keys() {
        let entries = vec![entry(GpuMemCategory::GBuffer, ResourceKind::Texture, 10, "gbuffer0")];
        let json = build_report("t", 0, "", entries, RegistryStats::default(), None, None, None).to_json();
        assert!(json.contains("\"key\": \"gbuffer\""));
    }
}
