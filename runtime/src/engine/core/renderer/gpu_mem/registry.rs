// ============================================================
//  gpu_mem/registry.rs — 作った GPU 資源の記録の表（生存の推定つき）
//
//  【生存の推定】
//  wgpu は資源の破棄を外へ知らせない（`wgpu::Texture` の Drop を横から見られない）。そこで、
//  資源を作った「場所（ソースのファイル・行）とラベル」を 1 つの枠（slot）とし、
//    - 後の世代で同じ枠に作られたら、前の世代までの記録は「作り直しで捨てた」とみなして外す
//      （レンダーターゲットのプールの作り直し・伸びるバッファ・毎フレーム使い捨てのバッファ・窓の大きさの変化での作り直し）
//    - 同じ世代の中で同じ枠に作られたものは並んで生きているとみなす（ループで作る配列・同じ型の 2 つの持ち主）
//  世代はフレームの始めと、窓の大きさの変化などの節目で進む（mod.rs の advance_frame / advance_generation）。
//    - 資産ごとに作る場所（category::ACCUMULATING_SITES。スプライトの画像・モデル）は外さずに積み増す
//  として「今生きている資源」を推定する。外したものの件数・バイト数も別に数えて報告する。
// ============================================================

use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};

use super::category::{self, GpuMemCategory};

/// 資源の種類。
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize)]
pub enum ResourceKind {
    /// テクスチャ。
    Texture,
    /// バッファ。
    Buffer,
}

impl ResourceKind {
    /// ログに出す短い名前。
    pub fn name(self) -> &'static str {
        match self {
            Self::Texture => "texture",
            Self::Buffer => "buffer",
        }
    }
}

/// 記録 1 件（作った資源 1 つ）。
#[derive(Clone, Debug, serde::Serialize)]
pub struct GpuMemEntry {
    /// テクスチャかバッファか。
    pub kind: ResourceKind,
    /// 分類（category.rs の規則で決めた値）。
    pub category: GpuMemCategory,
    /// ラベル（無ければ空）。
    pub label: String,
    /// 寸法・形式・用途の短い説明。
    pub detail: String,
    /// 論理的な大きさ（バイト。size.rs）。
    pub bytes: u64,
    /// 作った呼び出し元のソースのファイル（`/` 区切りに揃えた値）。
    pub site_file: String,
    /// 作った呼び出し元の行。
    pub site_line: u32,
    /// 作ったフレーム（起動中は 0）。
    pub frame: u64,
    /// 作った世代（作り直しの判定に使う。mod.rs）。
    pub generation: u64,
}

/// 記録の統計（作った累計・作り直しで外した分）。
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize)]
pub struct RegistryStats {
    /// 作った資源の累計の件数。
    pub created_count: u64,
    /// 作った資源の累計のバイト数。
    pub created_bytes: u64,
    /// 作り直しで捨てたとみなして外した件数。
    pub replaced_count: u64,
    /// 作り直しで捨てたとみなして外したバイト数。
    pub replaced_bytes: u64,
}

/// 枠の鍵（作った場所とラベル）。
#[derive(Clone, Debug, PartialEq, Eq, Hash)]
struct SlotKey {
    /// ソースのファイル（揃えた値）。
    file: String,
    /// ソースの行。
    line: u32,
    /// ラベル。
    label: String,
}

/// 記録の表。
#[derive(Default)]
pub struct Registry {
    /// 枠ごとの、今生きているとみなす記録。
    slots: HashMap<SlotKey, Vec<GpuMemEntry>>,
    /// 統計。
    stats: RegistryStats,
}

impl Registry {
    /// 空の表を作る。
    pub fn new() -> Self {
        Self::default()
    }

    /// 1 件記録する（同じ枠の前の世代までの記録は、積み増しの場所でなければ外す）。
    ///
    /// * `accumulate` … 資産ごとに作る場所か（true なら前の記録を外さない）
    pub fn record(&mut self, entry: GpuMemEntry, accumulate: bool) {
        self.stats.created_count += 1;
        self.stats.created_bytes += entry.bytes;
        let key = SlotKey {
            file: entry.site_file.clone(),
            line: entry.site_line,
            label: entry.label.clone(),
        };
        let list = self.slots.entry(key).or_default();
        if !accumulate {
            // 前の世代までに同じ枠で作ったものは、作り直しで捨てたとみなす。
            let generation = entry.generation;
            let mut replaced_count = 0u64;
            let mut replaced_bytes = 0u64;
            list.retain(|old| {
                let keep = old.generation >= generation;
                if !keep {
                    replaced_count += 1;
                    replaced_bytes += old.bytes;
                }
                keep
            });
            self.stats.replaced_count += replaced_count;
            self.stats.replaced_bytes += replaced_bytes;
        }
        list.push(entry);
    }

    /// 今生きているとみなす記録の一覧（順不同）。
    pub fn live_entries(&self) -> Vec<GpuMemEntry> {
        self.slots.values().flat_map(|list| list.iter().cloned()).collect()
    }

    /// 統計。
    pub fn stats(&self) -> RegistryStats {
        self.stats
    }
}

/// プロセスで 1 つの記録の表。
static REGISTRY: OnceLock<Mutex<Registry>> = OnceLock::new();

/// プロセスで 1 つの記録の表を返す。
fn global() -> &'static Mutex<Registry> {
    REGISTRY.get_or_init(|| Mutex::new(Registry::new()))
}

/// 1 件を分類して全体の表へ記録する（track.rs から呼ぶ。計測が有効なときだけ呼ばれる）。
///
/// * `site_file` / `site_line` … 呼び出し元（#[track_caller] の Location）
pub fn record_global(
    kind: ResourceKind,
    label: Option<&str>,
    detail: String,
    bytes: u64,
    site_file: &str,
    site_line: u32,
) {
    let site = category::normalize_site(site_file);
    let label = label.unwrap_or("").to_string();
    let category = category::classify(&label, &site);
    let accumulate = category::is_accumulating_site(&site);
    let entry = GpuMemEntry {
        kind,
        category,
        label,
        detail,
        bytes,
        site_file: site,
        site_line,
        frame: super::current_frame(),
        generation: super::current_generation(),
    };
    // 毒されたロック（記録中に別の場所が panic した後）でも計測は続ける（中身を取り出して使う）。
    let mut registry = global().lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    registry.record(entry, accumulate);
}

/// 全体の表の今の写し（生存の推定と統計）を取る。
pub fn snapshot_global() -> (Vec<GpuMemEntry>, RegistryStats) {
    let registry = global().lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    (registry.live_entries(), registry.stats())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 記録を短く作る補助。
    fn entry(label: &str, line: u32, bytes: u64, generation: u64) -> GpuMemEntry {
        GpuMemEntry {
            kind: ResourceKind::Texture,
            category: GpuMemCategory::Other,
            label: label.to_string(),
            detail: String::new(),
            bytes,
            site_file: "src/x.rs".to_string(),
            site_line: line,
            frame: generation,
            generation,
        }
    }

    /// 後の世代で同じ枠に作り直したら、前のものは外れる（作り直しの統計に入る）。
    #[test]
    fn later_frame_replaces_same_slot() {
        let mut r = Registry::new();
        r.record(entry("scene_hdr", 10, 100, 1), false);
        r.record(entry("scene_hdr", 10, 400, 5), false);
        let live = r.live_entries();
        assert_eq!(live.len(), 1);
        assert_eq!(live[0].bytes, 400);
        assert_eq!(r.stats().replaced_count, 1);
        assert_eq!(r.stats().replaced_bytes, 100);
        assert_eq!(r.stats().created_bytes, 500);
    }

    /// 同じ世代の中で同じ枠に作ったものは並んで生きている（ループで作る配列）。
    #[test]
    fn same_frame_coexists() {
        let mut r = Registry::new();
        r.record(entry("cascade", 20, 10, 0), false);
        r.record(entry("cascade", 20, 10, 0), false);
        assert_eq!(r.live_entries().len(), 2);
        assert_eq!(r.stats().replaced_count, 0);
    }

    /// 場所かラベルが違えば別の枠（作り直しにならない）。
    #[test]
    fn different_label_or_line_is_another_slot() {
        let mut r = Registry::new();
        r.record(entry("a", 30, 1, 1), false);
        r.record(entry("b", 30, 1, 2), false);
        r.record(entry("a", 31, 1, 3), false);
        assert_eq!(r.live_entries().len(), 3);
    }

    /// 積み増しの場所は後の世代でも外さない（資産ごとに作る画像）。
    #[test]
    fn accumulating_site_keeps_all() {
        let mut r = Registry::new();
        r.record(entry("SpriteTexture", 40, 7, 1), true);
        r.record(entry("SpriteTexture", 40, 9, 8), true);
        assert_eq!(r.live_entries().len(), 2);
        assert_eq!(r.stats().replaced_count, 0);
    }
}
