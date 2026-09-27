// ============================================================
//  gesture/scene.rs — アリーナが問い合わせるノードの世界（当たり判定の経路と押下の領域。W2-2）
//
//  アリーナ（arena_set.rs）は World もレイアウトの表も知らない。代わりにこのトレイトで
//    - 押したときの当たり判定の経路（葉 → 根。ノードと設定）
//    - 押下の途中で「指がノードの外へ出たか」（押下の領域 + ドラッグの slop）
//  を問い合わせる。本番は `GestureHitScene`（フレームごとにレイアウトの表から作る。app/gesture_scene.rs）、
//  試験も同じ型に矩形のノードを並べて使う（当たり判定の規則ごと試せる）。
//
//  【吸い込むノード（W2-3）】指に触れずに動いているスクロール（慣性・ScrollTo）は、触れた指を自分で受けて止める。
//  そのとき中の子（行のボタン）へは指を渡さない（Flutter の Scrollable が動いている間 IgnorePointer にする・Android の
//  RecyclerView が慣性中の指を onInterceptTouchEvent で取るのと同じ）。経路の中でいちばん根に近い吸い込むノードより
//  葉の側を経路から外す。
// ============================================================

use std::collections::{HashMap, HashSet};

use crate::engine::components::CanvasGestureComponent;
use crate::engine::ecs::Entity;

use super::hit_slop::{select_hit_path, GestureHitNode};

/// アリーナが問い合わせるノードの世界。
pub trait GestureScene {
    /// 点（キャンバスの画素）に当たるジェスチャーの経路（葉 → 根）と、各ノードの設定。
    fn hit_path(&self, p: [f32; 2]) -> Vec<(Entity, CanvasGestureComponent)>;

    /// 点が、ノードの押下の領域（見た目 → 最小のヒット領域 → さらに extra_px）の中か。
    /// ノードが今のフレームの世界に無い（消えた・隠れた・無効になった）ときは None。
    fn press_region_contains(&self, node: Entity, p: [f32; 2], extra_px: f32) -> Option<bool>;
}

/// 1 フレームぶんのジェスチャーの当たり判定の材料（ノードの一覧と引き表）。
#[derive(Clone, Debug, Default)]
pub struct GestureHitScene {
    /// ジェスチャーを受けるノード（`ancestors` はこの一覧の添字）。
    pub nodes: Vec<GestureHitNode>,
    /// ノード → 一覧の添字。
    index: HashMap<Entity, usize>,
    /// 1 dp の画素数（最小のヒット領域の換算）。
    pub dp_scale: f32,
    /// 触れた指を自分で受け、中の子へ渡さないノード（指に触れずに動いているスクロール。W2-3）。
    absorbing: HashSet<Entity>,
}

impl GestureHitScene {
    /// ノードの一覧から作る。
    pub fn new(nodes: Vec<GestureHitNode>, dp_scale: f32) -> Self {
        let index = nodes.iter().enumerate().map(|(i, n)| (n.key, i)).collect();
        Self { nodes, index, dp_scale, absorbing: HashSet::new() }
    }

    /// 触れた指を自分で受けて中の子へ渡さないノードを決める（W2-3。指に触れずに動いているスクロール）。
    pub fn with_absorbing(mut self, absorbing: HashSet<Entity>) -> Self {
        self.absorbing = absorbing;
        self
    }

    /// ノードの材料を引く。
    pub fn node(&self, key: Entity) -> Option<&GestureHitNode> {
        self.index.get(&key).map(|&i| &self.nodes[i])
    }

    /// ノードが 1 つも無いか。
    pub fn is_empty(&self) -> bool {
        self.nodes.is_empty()
    }
}

impl GestureScene for GestureHitScene {
    fn hit_path(&self, p: [f32; 2]) -> Vec<(Entity, CanvasGestureComponent)> {
        let mut path = select_hit_path(&self.nodes, p, self.dp_scale);
        // 吸い込むノード（W2-3）: 経路（葉 → 根）でいちばん根に近いものより葉の側を外す
        if let Some(k) = path.iter().rposition(|&i| self.absorbing.contains(&self.nodes[i].key)) {
            path.drain(..k);
        }
        path.into_iter().map(|i| (self.nodes[i].key, self.nodes[i].settings.clone())).collect()
    }

    fn press_region_contains(&self, node: Entity, p: [f32; 2], extra_px: f32) -> Option<bool> {
        let n = self.node(node)?;
        if !n.settings.enabled {
            return None;
        }
        Some(n.clip_contains(p) && n.contains(p, self.dp_scale, extra_px))
    }
}
