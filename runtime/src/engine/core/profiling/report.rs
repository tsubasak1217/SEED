// ============================================================
//  report.rs — 集計結果の JSON 直列化（IPC 送信用）
//
//  エディタのプロファイラパネルへ渡す 1 窓ぶんのレポートを組み立てる。
//  IPC は「1 行 = 1 メッセージ」の行区切りテキストなので、必ず改行を含まない
//  1 行 JSON にする（serde_json::to_string は改行を出さない）。
//
//  スキーマ（docs/profiler.md が正典）:
//  {
//    "frames":       集計窓に含まれたフレーム数,
//    "window_ms":    窓の実経過時間(ms),
//    "fps":          窓内の平均フレームレート,
//    "frame_avg_ms": フレーム全体の平均時間(ms),
//    "frame_max_ms": 窓内で最も重かったフレームの時間(ms),
//    "samples":      [各フレームの所要時間(ms)…]  ← 推移グラフ用,
//    "root":         セクションツリーのルート
//  }
//  セクションノード:
//  {
//    "name":       セクション名,
//    "avg_ms":     1 フレームあたりの平均時間(ms。毎フレーム走らない処理は按分される),
//    "max_ms":     窓内の 1 フレームでの最大時間(ms),
//    "self_ms":    avg_ms から子の avg_ms 合計を引いた自己時間(ms),
//    "share":      フレーム全体に対する比率(%),
//    "calls":      1 フレームあたりの平均呼び出し回数,
//    "calls_total":窓内の総呼び出し回数,
//    "children":   [子ノード…]
//  }
// ============================================================

use serde_json::{json, Value};

use super::aggregate::ProfilerAggregator;

/// 割合(%)へ変換する係数（マジックナンバー化を避けるための定数）。
const PERCENT_SCALE: f64 = 100.0;

/// 集計結果を 1 行 JSON へ直列化する。
pub fn build_report_json(agg: &ProfilerAggregator) -> String {
    let frames = agg.frame_count.max(1) as f64;
    let window_secs = agg.window_secs();

    // ルート（フレーム全体）の平均時間。フレーム比の分母にもなる。
    let root_avg_ms = agg.nodes[0].sum_ms / frames;

    let root = build_node_json(agg, 0, frames, root_avg_ms);

    let report = json!({
        "frames":       agg.frame_count,
        "window_ms":    window_secs * 1000.0,
        // 窓の経過時間が 0 になることは実質ないが、0 除算だけは避ける。
        "fps":          if window_secs > 0.0 { frames / window_secs } else { 0.0 },
        "frame_avg_ms": root_avg_ms,
        "frame_max_ms": agg.nodes[0].max_ms,
        "samples":      agg.frame_samples,
        "root":         root,
    });

    // 1 行 JSON（serde_json::to_string は改行を含まない）。
    report.to_string()
}

/// セクションノードを再帰的に JSON 化する。
///
/// ネスト深さは計測側で上限（`MAX_SCOPE_DEPTH`）が掛かっているため、
/// ここでの再帰はスタックを溢れさせない。
fn build_node_json(
    agg: &ProfilerAggregator,
    idx: usize,
    frames: f64,
    root_avg_ms: f64,
) -> Value {
    let node = &agg.nodes[idx];
    let avg_ms = node.sum_ms / frames;

    // 子の平均時間の合計。自己時間（この階層自身が消費した時間）の算出に使う。
    let children_avg_ms: f64 = node
        .children
        .iter()
        .map(|&c| agg.nodes[c].sum_ms / frames)
        .sum();

    let children: Vec<Value> = node
        .children
        .iter()
        .map(|&c| build_node_json(agg, c, frames, root_avg_ms))
        .collect();

    json!({
        "name":        node.name,
        "avg_ms":      avg_ms,
        "max_ms":      node.max_ms,
        // 計測誤差で僅かに負になることがあるため 0 でクランプする。
        "self_ms":     (avg_ms - children_avg_ms).max(0.0),
        "share":       if root_avg_ms > 0.0 { avg_ms / root_avg_ms * PERCENT_SCALE } else { 0.0 },
        "calls":       node.calls as f64 / frames,
        "calls_total": node.calls,
        "children":    children,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::profiling::scope::{FrameNode, FrameTree, FRAME_ROOT_NAME};

    /// ルート 10ms・子 4ms のフレームを 1 つ集計し、JSON の主要値を検証する。
    #[test]
    fn builds_report_with_share_and_self_time() {
        let mut agg = ProfilerAggregator::new();
        agg.accumulate_frame(&FrameTree {
            nodes: vec![
                FrameNode {
                    name: FRAME_ROOT_NAME,
                    parent: None,
                    children: vec![1],
                    total_ns: 10_000_000,
                    calls: 1,
                },
                FrameNode {
                    name: "描画",
                    parent: Some(0),
                    children: Vec::new(),
                    total_ns: 4_000_000,
                    calls: 2,
                },
            ],
        });

        let json_str = build_report_json(&agg);
        assert!(!json_str.contains('\n'), "IPC は 1 行 1 メッセージなので改行禁止");

        let v: Value = serde_json::from_str(&json_str).expect("有効な JSON であること");
        assert_eq!(v["frames"], 1);
        assert!((v["frame_avg_ms"].as_f64().unwrap() - 10.0).abs() < 1e-6);

        let root = &v["root"];
        // ルートの自己時間 = 10ms - 子 4ms = 6ms
        assert!((root["self_ms"].as_f64().unwrap() - 6.0).abs() < 1e-6);

        let child = &root["children"][0];
        assert_eq!(child["name"], "描画");
        assert!((child["avg_ms"].as_f64().unwrap() - 4.0).abs() < 1e-6);
        assert!((child["share"].as_f64().unwrap() - 40.0).abs() < 1e-6);
        assert!((child["calls"].as_f64().unwrap() - 2.0).abs() < 1e-6);
    }
}
