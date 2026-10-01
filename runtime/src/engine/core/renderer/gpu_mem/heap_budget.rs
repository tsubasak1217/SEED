// ============================================================
//  gpu_mem/heap_budget.rs — Vulkan のヒープの実使用量（VK_EXT_memory_budget）
//
//  【何のためか】
//  追跡した資源の合計（registry.rs）は「資源の論理的な大きさ」でしかない。wgpu の Vulkan 実装は
//  gpu-alloc で GPU メモリを大きな塊で確保して切り分ける（2 の冪への切り上げ・まとめ取り・転送用の塊を
//  手放さない）ので、実際にプロセスが確保している量はそれより大きい。Android の "GL mtrack" に当たる
//  「実際の確保」を同じ実行の中で並べて見るため、VK_EXT_memory_budget の heapUsage を読む。
//    heapUsage … 「このプロセスがそのヒープで今使っている量の見積り」（ドライバが返す。仕様の定義）
//    heapBudget … そのヒープでこのプロセスが使ってよい量の見積り
//  拡張は物理デバイスの問い合わせ（vkGetPhysicalDeviceMemoryProperties2 の pNext）だけで使い、
//  論理デバイスで有効にする必要は無い（物理デバイスが対応していればよい）。
//
//  Vulkan 以外のバックエンド（PC は Vulkan 固定・Android も Vulkan なので通常は通らない）と、
//  拡張に対応しない GPU では None を返す（内訳のログに「取れない」と書く）。
// ============================================================

/// 1 つのメモリヒープの使用量。
#[derive(Clone, Debug, PartialEq, Eq, serde::Serialize)]
pub struct HeapUsage {
    /// ヒープの番号（Vulkan の memoryHeaps の添字）。
    pub heap_index: u32,
    /// GPU 専用の速いメモリ（DEVICE_LOCAL）か。統合 GPU（スマートフォン）は主メモリと共有のヒープも DEVICE_LOCAL。
    pub device_local: bool,
    /// ヒープの大きさ（バイト）。
    pub heap_size: u64,
    /// このプロセスの使用量の見積り（バイト。VK_EXT_memory_budget の heapUsage）。
    pub usage: u64,
    /// このプロセスが使ってよい量の見積り（バイト。heapBudget）。
    pub budget: u64,
}

/// ヒープの使用量の合計（バイト）。
pub fn total_usage(heaps: &[HeapUsage]) -> u64 {
    heaps.iter().map(|heap| heap.usage).sum()
}

/// アダプタのヒープの使用量を問い合わせる（Vulkan でない・拡張が無い・取れないときは None）。
pub fn query_heap_usage(adapter: &wgpu::Adapter) -> Option<Vec<HeapUsage>> {
    #[cfg(any(windows, target_os = "android", target_os = "linux"))]
    {
        // SAFETY: 受け取った hal のアダプタは問い合わせ（読み取り）にだけ使い、ハンドルを破棄しない
        // （wgpu の as_hal の約束）。コールバックの外へ参照を持ち出さない。
        unsafe {
            adapter.as_hal::<wgpu::hal::api::Vulkan, _, _>(|hal_adapter| {
                hal_adapter.and_then(vulkan::query)
            })
        }
    }
    #[cfg(not(any(windows, target_os = "android", target_os = "linux")))]
    {
        let _ = adapter;
        None
    }
}

/// Vulkan の問い合わせの本体（Vulkan のバックエンドがあるプラットフォームだけ）。
#[cfg(any(windows, target_os = "android", target_os = "linux"))]
mod vulkan {
    use ash::vk;

    use super::HeapUsage;

    /// 物理デバイスのヒープの使用量を読む（VK_EXT_memory_budget）。
    pub(super) fn query(adapter: &wgpu::hal::vulkan::Adapter) -> Option<Vec<HeapUsage>> {
        // 物理デバイスが拡張に対応していなければ読めない。
        if !adapter
            .physical_device_capabilities()
            .supports_extension(ash::ext::memory_budget::NAME)
        {
            return None;
        }
        let physical_device = adapter.raw_physical_device();
        let shared = adapter.shared_instance();
        let instance = shared.raw_instance();

        let mut budget = vk::PhysicalDeviceMemoryBudgetPropertiesEXT::default();
        let memory_properties = {
            let mut properties2 = vk::PhysicalDeviceMemoryProperties2::default().push_next(&mut budget);
            if shared.instance_api_version() >= vk::API_VERSION_1_1 {
                // SAFETY: Vulkan 1.1 以上のインスタンスなので core の関数が読み込まれている。
                // physical_device は wgpu が列挙した有効なハンドル。
                unsafe { instance.get_physical_device_memory_properties2(physical_device, &mut properties2) };
            } else if shared
                .extensions()
                .contains(&ash::khr::get_physical_device_properties2::NAME)
            {
                // 1.0 のインスタンスでは KHR 拡張の版を使う（wgpu は使えるなら常にこの拡張を有効にしている）。
                let ext = ash::khr::get_physical_device_properties2::Instance::new(shared.entry(), instance);
                // SAFETY: 拡張がインスタンスで有効なので関数ポインタは読み込まれている。
                unsafe { ext.get_physical_device_memory_properties2(physical_device, &mut properties2) };
            } else {
                return None;
            }
            properties2.memory_properties
        };

        let heap_count = (memory_properties.memory_heap_count as usize).min(vk::MAX_MEMORY_HEAPS);
        Some(
            (0..heap_count)
                .map(|index| {
                    let heap = memory_properties.memory_heaps[index];
                    HeapUsage {
                        heap_index: index as u32,
                        device_local: heap.flags.contains(vk::MemoryHeapFlags::DEVICE_LOCAL),
                        heap_size: heap.size,
                        usage: budget.heap_usage[index],
                        budget: budget.heap_budget[index],
                    }
                })
                .collect(),
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 合計はヒープの使用量の和。
    #[test]
    fn total_is_sum_of_usage() {
        let heaps = vec![
            HeapUsage { heap_index: 0, device_local: true, heap_size: 100, usage: 30, budget: 90 },
            HeapUsage { heap_index: 1, device_local: false, heap_size: 200, usage: 12, budget: 150 },
        ];
        assert_eq!(total_usage(&heaps), 42);
        assert_eq!(total_usage(&[]), 0);
    }
}
