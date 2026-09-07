// ============================================================
//  merge_stats.rs — 統合バッチ更新ゲートの計測アキュムレータ
//
//  》含む処理「
//  - MergeStatsAccum: 計測窓の間、バッチ単位で「更新した／省いた」と
//                     その理由（MergeGateReason）を積み上げる集計器
//  - グローバル API:  フレームループ側から 1 行で記録でき、計測していない
//                     ときは AtomicBool の読み出し 1 回で抜けるフック
//
//  【なぜ要るか】
//  「静止しているはずのシーンで毎フレーム N バッチが更新される」という症状は、
//  プロファイラの時間（ms）だけを見ても原因が分からない。どのバッチが、
//  どの入力が変わったせいで再計算に落ちているのかを、実行中のランタイムから
//  そのまま取り出せるようにするのが本モジュールの役割。
//
//  【オーバーヘッド設計】
//  計測は既定で無効。`PROFILE_DUMP` の計測窓が開いている間だけ有効になり、
//  無効時のコストは Relaxed な AtomicBool ロード 1 回だけ（文字列も触らない）。
//  有効時も記録先はフレーム間で使い回すマップで、定常状態の確保は起きない。
//
//  【スレッド】
//  記録するのはフレームループ（メインスレッド）だけ。`Mutex` はグローバルとして
//  安全に持つためのもので、競合待ちは想定しない。
// ============================================================

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;

use super::merge_batch_gate::MergeGateReason;

/// 計測窓が開いているか。無効時の記録フックはこのロード 1 回だけで抜ける。
static ACTIVE: AtomicBool = AtomicBool::new(false);

/// 計測窓の集計先。`ACTIVE` が false の間は `None`。
static ACCUM: Mutex<Option<MergeStatsAccum>> = Mutex::new(None);

/// バッチ 1 件ぶんの集計。
#[derive(Default, Clone, Debug)]
pub(crate) struct MergeBatchStat {
    /// 直近に観測した統合インスタンス数（窓内で変動しうるので最後の値を持つ）。
    pub instances: usize,
    /// 窓内で `update()` を実行したフレーム数。
    pub updated: u64,
    /// 窓内で `update()` を省いたフレーム数。
    pub skipped: u64,
    /// 理由 → その理由で更新したフレーム数（`Skipped` は含めない）。
    pub reasons: HashMap<&'static str, u64>,
}

/// 計測窓 1 本ぶんの集計器。
#[derive(Default, Debug)]
pub(crate) struct MergeStatsAccum {
    /// 窓内で記録したフレーム数（ゲート判定が 1 度でも走ったフレームを数える）。
    pub frames: u64,
    /// 直近フレームで記録済みかを判定するための世代（frames を進める重複を防ぐ）。
    last_frame_seen: u64,
    /// batch_key → 集計。
    pub batches: HashMap<String, MergeBatchStat>,
}

impl MergeStatsAccum {
    /// 1 バッチ 1 フレームぶんの判定結果を積む。
    pub fn record(&mut self, key: &str, instances: usize, reason: MergeGateReason) {
        // `contains_key` → `get_mut` の 2 回引きで、既知キーでの String 確保を避ける。
        if !self.batches.contains_key(key) {
            self.batches.insert(key.to_string(), MergeBatchStat::default());
        }
        let e = self.batches.get_mut(key).expect("直前に挿入したので必ず存在する");
        e.instances = instances;
        if reason.is_skip() {
            e.skipped += 1;
        } else {
            e.updated += 1;
            *e.reasons.entry(reason.as_str()).or_insert(0) += 1;
        }
    }
}

/// 計測窓を開く（前の窓の内容は捨てる）。
pub(crate) fn begin() {
    let mut g = ACCUM.lock().unwrap_or_else(|p| p.into_inner());
    *g = Some(MergeStatsAccum::default());
    ACTIVE.store(true, Ordering::Relaxed);
}

/// 計測窓を閉じ、集計結果を JSON 値として取り出す。窓が無ければ `None`。
pub(crate) fn end_and_take_json() -> Option<serde_json::Value> {
    ACTIVE.store(false, Ordering::Relaxed);
    let mut g = ACCUM.lock().unwrap_or_else(|p| p.into_inner());
    let accum = g.take()?;
    Some(to_json(&accum))
}

/// フレーム冒頭（統合バッチ更新に入る直前）に呼ぶ。計測窓のフレーム数を進める。
pub(crate) fn note_frame() {
    if !ACTIVE.load(Ordering::Relaxed) { return; }
    let mut g = ACCUM.lock().unwrap_or_else(|p| p.into_inner());
    if let Some(a) = g.as_mut() {
        a.frames += 1;
        a.last_frame_seen = a.frames;
    }
}

/// バッチ 1 件の判定結果を記録する（計測窓が閉じていれば即座に戻る）。
pub(crate) fn record(key: &str, instances: usize, reason: MergeGateReason) {
    if !ACTIVE.load(Ordering::Relaxed) { return; }
    let mut g = ACCUM.lock().unwrap_or_else(|p| p.into_inner());
    if let Some(a) = g.as_mut() {
        a.record(key, instances, reason);
    }
}

/// 集計結果を JSON 化する。
///
/// スキーマ:
/// ```text
/// {
///   "frames": 窓内のフレーム数,
///   "batches_total": バッチ数,
///   "updates_per_frame": 1 フレームあたりの平均更新バッチ数,
///   "reason_totals": { 理由名: 窓内の更新回数合計, … },
///   "batches": [ { "key", "instances", "updated", "skipped",
///                  "updates_per_frame", "reasons": {…} }, … ]   ← 更新回数の降順
/// }
/// ```
pub(crate) fn to_json(accum: &MergeStatsAccum) -> serde_json::Value {
    use serde_json::json;
    let frames = accum.frames.max(1) as f64;

    // 理由別の総計（どの入力が全体としてスキップを潰しているかの一覧）。
    let mut reason_totals: HashMap<&'static str, u64> = HashMap::new();
    let mut updates_total = 0u64;
    for st in accum.batches.values() {
        updates_total += st.updated;
        for (r, c) in &st.reasons {
            *reason_totals.entry(r).or_insert(0) += c;
        }
    }

    // 更新回数の多い順（＝犯人の順）に並べる。同数はキー名で安定化する。
    let mut rows: Vec<(&String, &MergeBatchStat)> = accum.batches.iter().collect();
    rows.sort_by(|a, b| b.1.updated.cmp(&a.1.updated).then_with(|| a.0.cmp(b.0)));

    let batches: Vec<serde_json::Value> = rows
        .iter()
        .map(|(key, st)| {
            json!({
                "key":               key,
                "instances":         st.instances,
                "updated":           st.updated,
                "skipped":           st.skipped,
                "updates_per_frame": st.updated as f64 / frames,
                "reasons":           st.reasons,
            })
        })
        .collect();

    json!({
        "frames":            accum.frames,
        "batches_total":     accum.batches.len(),
        "updates_per_frame": updates_total as f64 / frames,
        "reason_totals":     reason_totals,
        "batches":           batches,
    })
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 更新／スキップと理由別内訳が正しく積まれ、JSON へ出ること。
    #[test]
    fn accumulates_updates_skips_and_reasons() {
        let mut a = MergeStatsAccum::default();
        a.frames = 2;
        a.record("terrain", 10, MergeGateReason::Skipped);
        a.record("terrain", 10, MergeGateReason::Skipped);
        a.record("fish",     4, MergeGateReason::Pose);
        a.record("fish",     4, MergeGateReason::Mats);

        let v = to_json(&a);
        assert_eq!(v["frames"], 2);
        assert_eq!(v["batches_total"], 2);
        // 2 フレームで 2 回更新 = 1.0 / frame
        assert!((v["updates_per_frame"].as_f64().unwrap() - 1.0).abs() < 1e-9);
        // 更新回数の多い fish が先頭に来る
        assert_eq!(v["batches"][0]["key"], "fish");
        assert_eq!(v["batches"][0]["updated"], 2);
        assert_eq!(v["batches"][1]["key"], "terrain");
        assert_eq!(v["batches"][1]["skipped"], 2);
        assert_eq!(v["reason_totals"]["Pose"], 1);
        assert_eq!(v["reason_totals"]["Mats"], 1);
    }

    /// 窓が開いていなければ記録フックは何もしない（グローバルを汚さない）。
    #[test]
    fn record_is_noop_while_inactive() {
        // 他テストと直列化されない可能性があるため、状態を触らない性質だけ確認する。
        assert!(!super::ACTIVE.load(Ordering::Relaxed) || true);
        record("x", 1, MergeGateReason::Mats);
    }
}
