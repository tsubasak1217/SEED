// ============================================================
//  aggregate.rs — フレーム計測ツリーの集計（集計窓ごとの平均 / 最大 / 回数）
//
//  毎フレームの生値をそのままエディタへ送るとフレームレート分の IPC が発生し、
//  パネル側の表示もちらついて読めない。そこで一定時間（集計窓）ぶんのフレームを
//  畳み込み、窓が満了したタイミングで 1 回だけ送る。
//
//  集計は「フレームツリーと同じ形の永続ツリー」へ加算していく方式で、
//  ノードは (親, 名前) で一意に決まる。窓が満了したら統計をリセットして
//  次の窓を始める（消えたセクションが残り続けないよう、ツリーごと作り直す）。
// ============================================================

use std::time::Instant;

use super::scope::{FrameTree, FRAME_ROOT_NAME, NANOS_PER_MILLI};

/// 集計窓の長さ（秒）。この間隔でエディタへ 1 回送信する。
pub const WINDOW_DURATION_SECS: f64 = 0.5;

/// フレーム時間推移グラフ用に 1 窓で保持するサンプル数の上限。
///
/// 60fps × 0.5 秒 = 30 サンプル程度が普通だが、可変フレームレートや
/// 極端な高リフレッシュレート環境で送信量が膨れないよう上限を設ける。
const MAX_FRAME_SAMPLES_PER_WINDOW: usize = 240;

/// 集計ツリーの 1 ノード。
#[derive(Clone, Debug)]
pub struct AggNode {
    /// セクション名。
    pub name: &'static str,
    /// 親ノードの添字（ルートは `None`）。
    pub parent: Option<usize>,
    /// 子ノードの添字一覧。
    pub children: Vec<usize>,
    /// 窓内の合計時間（ミリ秒）。平均はこれをフレーム数で割って求める。
    pub sum_ms: f64,
    /// 窓内で最も重かったフレームでの時間（ミリ秒）。
    pub max_ms: f64,
    /// 窓内の総呼び出し回数。
    pub calls: u64,
    /// このノードが出現したフレーム数（毎フレーム走らないセクションの判別用）。
    pub frames_present: u32,
}

/// 集計窓 1 本ぶんの状態。
pub struct ProfilerAggregator {
    /// 集計ツリー（`nodes[0]` がルート＝フレーム全体）。
    pub nodes: Vec<AggNode>,
    /// 窓の開始時刻。
    window_start: Instant,
    /// 窓内で集計したフレーム数。
    pub frame_count: u32,
    /// 窓内の各フレームの所要時間（ミリ秒）。推移グラフ用。
    pub frame_samples: Vec<f32>,
}

impl ProfilerAggregator {
    /// 空の集計器を作る（ルートノードのみ）。
    pub fn new() -> Self {
        Self {
            nodes:         vec![Self::new_node(FRAME_ROOT_NAME, None)],
            window_start:  Instant::now(),
            frame_count:   0,
            frame_samples: Vec::new(),
        }
    }

    /// 統計ゼロのノードを作る内部ヘルパ。
    fn new_node(name: &'static str, parent: Option<usize>) -> AggNode {
        AggNode {
            name,
            parent,
            children: Vec::new(),
            sum_ms: 0.0,
            max_ms: 0.0,
            calls: 0,
            frames_present: 0,
        }
    }

    /// 集計窓が満了したか。
    pub fn window_elapsed(&self) -> bool {
        // フレームが 1 つも入っていない窓を送っても意味がないため、
        // 経過時間とフレーム数の両方を条件にする。
        self.frame_count > 0
            && self.window_start.elapsed().as_secs_f64() >= WINDOW_DURATION_SECS
    }

    /// 窓の実経過時間（秒）。レポートの fps 算出に使う。
    pub fn window_secs(&self) -> f64 {
        self.window_start.elapsed().as_secs_f64()
    }

    /// 統計をリセットして次の窓を開始する。
    ///
    /// ツリーそのものも作り直す。前の窓にしか存在しなかったセクション
    /// （その後実行されなくなったもの）が 0ms 行として残らないようにするため。
    pub fn reset_window(&mut self) {
        self.nodes.clear();
        self.nodes.push(Self::new_node(FRAME_ROOT_NAME, None));
        self.window_start  = Instant::now();
        self.frame_count   = 0;
        self.frame_samples.clear();
    }

    /// 1 フレーム分の計測ツリーを集計へ畳み込む。
    pub fn accumulate_frame(&mut self, frame: &FrameTree) {
        self.frame_count += 1;
        if self.frame_samples.len() < MAX_FRAME_SAMPLES_PER_WINDOW {
            self.frame_samples.push(frame.frame_ms() as f32);
        }

        // フレームツリーのノード添字 → 集計ツリーのノード添字の対応表。
        // DFS を親から順に辿るので、子を処理する時点で親は必ず解決済みになる。
        let mut mapping = vec![usize::MAX; frame.nodes.len()];
        mapping[0] = 0;
        self.merge_node(&frame.nodes[0], 0);

        // 幅優先で子孫を辿る（再帰にしないのは深い計装でスタックを消費しないため）。
        let mut queue: Vec<usize> = vec![0];
        while let Some(fi) = queue.pop() {
            let agg_parent = mapping[fi];
            for &fc in &frame.nodes[fi].children {
                let child_name = frame.nodes[fc].name;
                let agg_child = self.find_or_insert_child(agg_parent, child_name);
                mapping[fc] = agg_child;
                self.merge_node(&frame.nodes[fc], agg_child);
                queue.push(fc);
            }
        }
    }

    /// 指定した集計ノードへ、1 フレーム分の値を加算する。
    fn merge_node(&mut self, src: &super::scope::FrameNode, dst: usize) {
        let ms = src.total_ns as f64 / NANOS_PER_MILLI;
        let node = &mut self.nodes[dst];
        node.sum_ms += ms;
        if ms > node.max_ms {
            node.max_ms = ms;
        }
        node.calls += src.calls as u64;
        node.frames_present += 1;
    }

    /// 親ノード配下から同名の子を探し、無ければ作って添字を返す。
    fn find_or_insert_child(&mut self, parent: usize, name: &'static str) -> usize {
        if let Some(&idx) = self.nodes[parent]
            .children
            .iter()
            .find(|&&c| self.nodes[c].name == name)
        {
            return idx;
        }
        let idx = self.nodes.len();
        self.nodes.push(Self::new_node(name, Some(parent)));
        self.nodes[parent].children.push(idx);
        idx
    }
}

impl Default for ProfilerAggregator {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::profiling::scope::FrameNode;

    /// テスト用に「ルート → 子」の 2 段フレームツリーを作る。
    fn make_tree(root_ns: u64, child_name: &'static str, child_ns: u64, calls: u32) -> FrameTree {
        FrameTree {
            nodes: vec![
                FrameNode {
                    name: FRAME_ROOT_NAME,
                    parent: None,
                    children: vec![1],
                    total_ns: root_ns,
                    calls: 1,
                },
                FrameNode {
                    name: child_name,
                    parent: Some(0),
                    children: Vec::new(),
                    total_ns: child_ns,
                    calls,
                },
            ],
        }
    }

    /// 同じ形のフレームを 2 回入れると、合計・最大・回数が正しく積み上がる。
    #[test]
    fn accumulates_sum_max_and_calls() {
        let mut agg = ProfilerAggregator::new();
        agg.accumulate_frame(&make_tree(10_000_000, "描画", 4_000_000, 1));
        agg.accumulate_frame(&make_tree(20_000_000, "描画", 6_000_000, 3));

        assert_eq!(agg.frame_count, 2);
        assert_eq!(agg.nodes.len(), 2, "ルート + 描画");
        let child = &agg.nodes[1];
        assert_eq!(child.name, "描画");
        assert!((child.sum_ms - 10.0).abs() < 1e-6);
        assert!((child.max_ms - 6.0).abs() < 1e-6);
        assert_eq!(child.calls, 4);
        assert_eq!(child.frames_present, 2);
    }

    /// 名前が異なる子は別ノードとして並ぶ。
    #[test]
    fn distinct_names_become_distinct_nodes() {
        let mut agg = ProfilerAggregator::new();
        agg.accumulate_frame(&make_tree(10_000_000, "描画", 4_000_000, 1));
        agg.accumulate_frame(&make_tree(10_000_000, "物理", 2_000_000, 1));
        assert_eq!(agg.nodes.len(), 3);
        assert_eq!(agg.nodes[0].children.len(), 2);
    }

    /// 窓のリセットでツリーごと初期化される。
    #[test]
    fn reset_window_clears_tree() {
        let mut agg = ProfilerAggregator::new();
        agg.accumulate_frame(&make_tree(10_000_000, "描画", 4_000_000, 1));
        agg.reset_window();
        assert_eq!(agg.frame_count, 0);
        assert_eq!(agg.nodes.len(), 1);
        assert!(agg.frame_samples.is_empty());
    }
}
