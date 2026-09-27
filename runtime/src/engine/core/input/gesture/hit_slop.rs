// ============================================================
//  gesture/hit_slop.rs — ジェスチャーの当たり判定（最小のヒット領域・近い方・遮り。W2-2）
//
//  【対象】ジェスチャーを受けるノード（CanvasGestureComponent を持ち有効なもの）だけ。形は「見た目の矩形」
//  （CanvasComponent があればキャンバス領域、無ければ最初の有効な Sprite の矩形。回転・拡大を含む行列 `matrix` が
//  ローカルの [0,w]×[0,h] をキャンバスの画素へ写す）。点も矩形もキャンバスの画素（画面の中央が原点・Y 下向き）。
//
//  【規則】（docs/input_gestures.md §3）
//    R1 候補: 見た目の矩形を「最小のヒット領域」（既定 48 dp。縦横それぞれ、中心をそろえて広げる）まで広げた矩形が
//       点を含み、祖先の切り抜き（CanvasClip）のすべての内側にあるノード（切り抜きの外で押した指は参加しない）
//    R2 葉: 候補のうち、他の候補の祖先でないもの（祖先は後で経路に入る）
//    R3 遮り: 見た目の矩形が点を含む候補 y が、葉 x より手前に描かれ（描画ゾーン → レイヤー → 木の順）、
//       x の祖先でないなら x は遮られて外れる（覆いの板・ダイアログの後ろへは届かない）
//    R4 近い方: 残った葉のうち、点から見た目の矩形までの距離（中なら 0）が最小のもの。同じなら手前のもの
//    経路: 選んだ葉と、その祖先のうち候補であるもの（近い順）。アリーナは経路の順（葉が先）に認識器を並べる
// ============================================================

use crate::engine::components::CanvasGestureComponent;
use crate::engine::ecs::Entity;

/// 行列（行優先 4×4。キャンバスのレイアウトの表と同じ形）。
pub type Mat4 = [[f32; 4]; 4];

/// 軸の長さを 0 とみなす大きさ（退化した行列の軸で割らない）。
const AXIS_EPSILON: f32 = 1e-9;

/// 描画の手前・奥の順（大きいほど手前。pointer_events.rs の frontmost_candidate と同じ比べ方）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Default)]
pub struct PaintOrder {
    /// 描画ゾーンの手前の順（前景 = 1・背景 = 0）。
    pub zone_front: u8,
    /// レイヤー（大きいほど手前。ノードの最初の有効な Sprite のレイヤー。無ければ 0）。
    pub layer: i32,
    /// 木の深さ優先の番号（後ほど手前）。
    pub dfs: usize,
}

/// 切り抜きの領域の AABB（キャンバスの画素。min, max）。
pub type ClipAabb = ([f32; 2], [f32; 2]);

/// ジェスチャーの当たり判定の材料（ノード 1 つ）。
#[derive(Clone, Debug)]
pub struct GestureHitNode {
    /// ノード（アクターの entity）。
    pub key: Entity,
    /// ローカルの矩形 [0,w]×[0,h] → キャンバスの画素 の行列。
    pub matrix: Mat4,
    /// ローカルの矩形の大きさ（w, h）。
    pub size: [f32; 2],
    /// 祖先の切り抜きの領域（すべての内側のときだけ当たる）。
    pub clip_aabbs: Vec<ClipAabb>,
    /// 祖先のうちジェスチャーを受けるノード（この一覧の添字。近い順）。
    pub ancestors: Vec<usize>,
    /// 描画の手前・奥の順。
    pub paint: PaintOrder,
    /// 受けるジェスチャーの設定。
    pub settings: CanvasGestureComponent,
    /// ローカルの画素 ÷ キャンバスの単位（イベントの LocalPosition をノードの単位にする倍率。dp のキャンバスなら dp）。
    pub unit_scale: [f32; 2],
}

impl GestureHitNode {
    /// キャンバスの画素の点をローカルの座標（矩形の左上が原点）へ戻す。行列が退化していれば None。
    pub fn to_local(&self, p: [f32; 2]) -> Option<[f32; 2]> {
        let m = &self.matrix;
        let det = m[0][0] * m[1][1] - m[0][1] * m[1][0];
        if det.abs() < AXIS_EPSILON {
            return None;
        }
        let dx = p[0] - m[0][3];
        let dy = p[1] - m[1][3];
        Some([(dx * m[1][1] - dy * m[0][1]) / det, (m[0][0] * dy - m[1][0] * dx) / det])
    }

    /// ローカルの 1 単位が画面で何画素か（x 軸・y 軸の長さ。回転と拡大を含む）。
    pub fn axis_scale(&self) -> [f32; 2] {
        let m = &self.matrix;
        [(m[0][0] * m[0][0] + m[1][0] * m[1][0]).sqrt(), (m[0][1] * m[0][1] + m[1][1] * m[1][1]).sqrt()]
    }

    /// 最小のヒット領域まで広げるときの、片側の広げる量（ローカルの単位。縦横それぞれ）。
    ///
    /// # 引数
    /// * `dp_scale` - 1 dp の画素数
    pub fn expansion(&self, dp_scale: f32) -> [f32; 2] {
        let min_px = self.settings.min_hit_size() * dp_scale;
        let scale = self.axis_scale();
        [0, 1].map(|a| {
            if scale[a] <= AXIS_EPSILON {
                return 0.0;
            }
            let on_screen = self.size[a].abs() * scale[a];
            ((min_px - on_screen) * 0.5).max(0.0) / scale[a]
        })
    }

    /// 点が「見た目 → 最小のヒット領域 → さらに extra_px」まで広げた矩形の中か（切り抜きは見ない）。
    ///
    /// # 引数
    /// * `extra_px` - さらに広げる量（画素。押下の途中で「外へ出た」を判定するときのドラッグの slop）
    pub fn contains(&self, p: [f32; 2], dp_scale: f32, extra_px: f32) -> bool {
        let Some(local) = self.to_local(p) else { return false };
        let expand = self.expansion(dp_scale);
        let scale = self.axis_scale();
        (0..2).all(|a| {
            let extra = if scale[a] > AXIS_EPSILON { extra_px.max(0.0) / scale[a] } else { 0.0 };
            let margin = expand[a] + extra;
            let (lo, hi) = (self.size[a].min(0.0), self.size[a].max(0.0));
            local[a] >= lo - margin && local[a] <= hi + margin
        })
    }

    /// 点から見た目の矩形までの距離（画面の画素。中なら 0）。行列が退化していれば無限大。
    pub fn visual_distance(&self, p: [f32; 2]) -> f32 {
        let Some(local) = self.to_local(p) else { return f32::INFINITY };
        let scale = self.axis_scale();
        let d = [0, 1].map(|a| {
            let (lo, hi) = (self.size[a].min(0.0), self.size[a].max(0.0));
            let outside = (lo - local[a]).max(local[a] - hi).max(0.0);
            outside * scale[a]
        });
        (d[0] * d[0] + d[1] * d[1]).sqrt()
    }

    /// 点が祖先のすべての切り抜きの内側か（境界は内側。描画の scissor・pick_2d と同じ AABB）。
    pub fn clip_contains(&self, p: [f32; 2]) -> bool {
        self.clip_aabbs
            .iter()
            .all(|(min, max)| p[0] >= min[0] && p[0] <= max[0] && p[1] >= min[1] && p[1] <= max[1])
    }

    /// 点を切り抜きの内側で、広げたヒット領域に含むか（R1 の候補の条件）。
    pub fn is_candidate(&self, p: [f32; 2], dp_scale: f32) -> bool {
        self.settings.enabled && self.clip_contains(p) && self.contains(p, dp_scale, 0.0)
    }

    /// 点のローカルの座標をノードの単位（キャンバスの単位。dp のキャンバスなら dp）で返す。
    pub fn local_position_in_units(&self, p: [f32; 2]) -> [f32; 2] {
        let local = self.to_local(p).unwrap_or([0.0, 0.0]);
        [0, 1].map(|a| if self.unit_scale[a].abs() > AXIS_EPSILON { local[a] / self.unit_scale[a] } else { local[a] })
    }
}

/// 点に当たるジェスチャーの経路（葉 → 根。`nodes` の添字）を選ぶ【純関数】。規則は冒頭の R1〜R4。
///
/// # 引数
/// * `nodes`    - ジェスチャーを受けるノードの一覧（`ancestors` は同じ一覧の添字）
/// * `p`        - 点（キャンバスの画素）
/// * `dp_scale` - 1 dp の画素数（最小のヒット領域を画素へ直す）
pub fn select_hit_path(nodes: &[GestureHitNode], p: [f32; 2], dp_scale: f32) -> Vec<usize> {
    // R1 候補
    let candidates: Vec<usize> = (0..nodes.len()).filter(|&i| nodes[i].is_candidate(p, dp_scale)).collect();
    if candidates.is_empty() {
        return Vec::new();
    }
    let is_candidate = |i: usize| candidates.contains(&i);
    // 見た目の矩形が点を含む候補（遮る側）
    let visual: Vec<usize> = candidates.iter().copied().filter(|&i| nodes[i].visual_distance(p) <= 0.0).collect();
    // R2 葉（他の候補の祖先でないもの）
    let leaves: Vec<usize> = candidates
        .iter()
        .copied()
        .filter(|&i| !candidates.iter().any(|&j| j != i && nodes[j].ancestors.contains(&i)))
        .collect();
    // R3 遮り（手前にある見た目の候補で、祖先でないものに遮られた葉を外す）
    let unoccluded: Vec<usize> = leaves
        .iter()
        .copied()
        .filter(|&x| {
            !visual.iter().any(|&y| y != x && nodes[y].paint > nodes[x].paint && !nodes[x].ancestors.contains(&y))
        })
        .collect();
    // 葉がすべて遮られた（祖先の並びが描画の順と食い違う稀な木）ときは、見た目の候補のうち最も手前、無ければ候補の最も手前
    let pool = if unoccluded.is_empty() {
        let fallback = if visual.is_empty() { &candidates } else { &visual };
        fallback.iter().copied().max_by_key(|&i| nodes[i].paint).into_iter().collect()
    } else {
        unoccluded
    };
    // R4 近い方（同じなら手前）
    let Some(target) = pool.into_iter().min_by(|&a, &b| {
        nodes[a]
            .visual_distance(p)
            .total_cmp(&nodes[b].visual_distance(p))
            .then_with(|| nodes[b].paint.cmp(&nodes[a].paint))
    }) else {
        return Vec::new();
    };
    // 経路: 葉と、祖先のうち候補であるもの（近い順）
    let mut path = vec![target];
    path.extend(nodes[target].ancestors.iter().copied().filter(|&a| is_candidate(a)));
    path
}

// ============================================================
//  単体テスト（幾何と選び方。アリーナを通した試験は tests.rs）
// ============================================================

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    /// 軸にそろった矩形のノード（左上 (x, y)・大きさ (w, h)・キャンバスの画素）を作る。
    pub(crate) fn rect_node(index: u32, x: f32, y: f32, w: f32, h: f32, dfs: usize) -> GestureHitNode {
        GestureHitNode {
            key: Entity::from_raw(index, 0),
            matrix: [[1.0, 0.0, 0.0, x], [0.0, 1.0, 0.0, y], [0.0, 0.0, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0]],
            size: [w, h],
            clip_aabbs: Vec::new(),
            ancestors: Vec::new(),
            paint: PaintOrder { zone_front: 1, layer: 0, dfs },
            settings: CanvasGestureComponent::default(),
            unit_scale: [1.0, 1.0],
        }
    }

    /// 小さな見た目（20×20）は 48 dp まで広がる（dp の倍率 1・2・3）。大きな見た目は広げない。
    #[test]
    fn min_hit_size_expands_small_nodes() {
        let n = rect_node(1, 0.0, 0.0, 20.0, 20.0, 0);
        for scale in [1.0f32, 2.0, 3.0] {
            let half = 24.0 * scale; // 48 dp の半分
            let center = 10.0;
            assert!(n.contains([center + half - 0.5, center], scale, 0.0), "倍率 {scale}: 広げた内側");
            assert!(!n.contains([center + half + 0.5, center], scale, 0.0), "倍率 {scale}: 広げた外側");
        }
        let big = rect_node(2, 0.0, 0.0, 200.0, 100.0, 0);
        assert!(!big.contains([200.5, 50.0], 1.0, 0.0), "48 dp より大きい見た目は広げない");
        assert!(big.contains([200.5, 50.0], 1.0, 1.0), "extra の分だけ広がる");
    }

    /// 最小の大きさ 0 は広げない。回転したノードも広げた矩形で判定する。
    #[test]
    fn zero_min_and_rotation() {
        let mut n = rect_node(1, 0.0, 0.0, 20.0, 20.0, 0);
        n.settings.min_hit_size_dp = 0.0;
        assert!(!n.contains([21.0, 10.0], 1.0, 0.0));
        // 90 度回した 20×20（ローカル x → 画面 y）。左上は (0, 0) のまま、画面では x ∈ [-20, 0]
        let mut r = rect_node(2, 0.0, 0.0, 20.0, 20.0, 0);
        r.matrix = [[0.0, -1.0, 0.0, 0.0], [1.0, 0.0, 0.0, 0.0], [0.0, 0.0, 1.0, 0.0], [0.0, 0.0, 0.0, 1.0]];
        assert!(r.contains([-10.0, 10.0], 1.0, 0.0));
        assert!(r.contains([-10.0, 10.0 + 23.5], 1.0, 0.0), "回転しても 48 まで広がる");
        assert_eq!(r.visual_distance([-10.0, 10.0]), 0.0);
        assert!((r.visual_distance([-10.0, 25.0]) - 5.0).abs() < 1e-4);
    }

    /// 近い方: 広げた領域が重なる 2 つの小さなボタンの間では、見た目に近い方が勝つ。
    #[test]
    fn overlapping_expansions_pick_the_nearest() {
        // 20×20 のボタンを 10 画素あけて並べる（広げた 48×48 が重なる）
        let a = rect_node(1, 0.0, 0.0, 20.0, 20.0, 0);
        let b = rect_node(2, 30.0, 0.0, 20.0, 20.0, 1);
        let nodes = vec![a, b];
        assert_eq!(select_hit_path(&nodes, [23.0, 10.0], 1.0), vec![0], "A まで 3・B まで 7");
        assert_eq!(select_hit_path(&nodes, [27.0, 10.0], 1.0), vec![1], "A まで 7・B まで 3");
        assert_eq!(select_hit_path(&nodes, [25.0, 10.0], 1.0), vec![1], "同じなら手前（後に描く B）");
        assert_eq!(select_hit_path(&nodes, [10.0, 10.0], 1.0), vec![0], "A の中（B の広げた領域でも A）");
    }

    /// 子の広げた領域は親の見た目の中でも子が勝ち、経路は子 → 親。遮る板の後ろのノードは外れる。
    #[test]
    fn child_expansion_and_occlusion() {
        let parent = rect_node(1, 0.0, 0.0, 300.0, 300.0, 0);
        let mut child = rect_node(2, 100.0, 100.0, 20.0, 20.0, 1);
        child.ancestors = vec![0];
        // 手前の兄弟の板（ジェスチャーなし＝遮るだけ）
        let mut blocker = rect_node(3, 200.0, 0.0, 100.0, 100.0, 2);
        blocker.settings.tap = false;
        let nodes = vec![parent, child, blocker];
        assert_eq!(select_hit_path(&nodes, [125.0, 110.0], 1.0), vec![1, 0], "子の広げた領域");
        assert_eq!(select_hit_path(&nodes, [50.0, 50.0], 1.0), vec![0], "親だけ");
        assert_eq!(select_hit_path(&nodes, [250.0, 50.0], 1.0), vec![2], "板が親を遮る");
        assert!(select_hit_path(&nodes, [400.0, 50.0], 1.0).is_empty());
    }

    /// 切り抜きの外で押した指は参加しない（見た目・広げた領域の中でも）。
    #[test]
    fn clipped_points_do_not_hit() {
        let mut n = rect_node(1, 0.0, 0.0, 100.0, 100.0, 0);
        n.clip_aabbs = vec![([0.0, 0.0], [100.0, 50.0])];
        assert_eq!(select_hit_path(std::slice::from_ref(&n), [50.0, 25.0], 1.0), vec![0]);
        assert!(select_hit_path(std::slice::from_ref(&n), [50.0, 75.0], 1.0).is_empty(), "切り抜きの外");
    }

    /// 無効なノードは候補にならない（後ろのノードへ届く）。ローカルの座標はノードの単位で返す。
    #[test]
    fn disabled_nodes_and_local_units() {
        let back = rect_node(1, 0.0, 0.0, 100.0, 100.0, 0);
        let mut front = rect_node(2, 0.0, 0.0, 100.0, 100.0, 1);
        front.settings.enabled = false;
        let nodes = vec![back, front];
        assert_eq!(select_hit_path(&nodes, [10.0, 10.0], 1.0), vec![0]);
        let mut n = rect_node(3, 10.0, 20.0, 100.0, 100.0, 0);
        n.unit_scale = [2.0, 2.0];
        assert_eq!(n.local_position_in_units([30.0, 60.0]), [10.0, 20.0]);
    }
}
