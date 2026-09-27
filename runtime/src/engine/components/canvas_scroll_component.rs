// ============================================================
//  canvas_scroll_component.rs — スクロールの領域（W2-3。docs/ui_scroll_list.md が正典）
//
//  2D キャンバスのノードに付けると、そのノードが「中身をずらして見せる窓」になる:
//    - 子（中身）の配置をスクロールの位置だけずらす（レイアウトの走査 canvas_layout/pass.rs が子へ渡す文脈を平行移動する）
//    - 指のドラッグ（W2-2 のジェスチャーアリーナ。軸はスクロールの向き）で位置を動かし、離したらフリックの速度で慣性、
//      端では跳ね返る（ばね）か止まる。ページ・間隔のスナップ、入れ子（同じ向きは端で外側へ渡す）
//    - 切り抜き（CanvasClipComponent）と一緒に付けると、窓の外の子を描画アイテムと当たり判定に入れない（見える範囲の外を飛ばす）
//  窓の大きさ（ビューポート）は CanvasComponent があればキャンバスの箱、無ければ最初の Sprite の矩形。
//
//  【データとロジックの分離】このファイルは設定のデータだけ（シリアライズする ECS のコンポーネント）。
//  実行中の状態（位置・速度・慣性・測った大きさ）は別のコンポーネント `CanvasScrollState`
//  （engine/core/canvas_scroll/state.rs。保存しない）が同じスロットのエンティティに持つ。
//  物理（慣性・跳ね返り・スナップ）は engine/core/canvas_scroll/、フレームの処理と配達は app/scroll_events.rs。
//
//  【スクリプトとの受け渡し】列挙は添字で受け渡す（canvas_layout_params.rs の IndexedEnum。並びを変えると
//  スクリプトの値が変わるので、足すときは末尾へ）。C# の SEED.ScrollDirection などと一致させる。
// ============================================================

use serde::{Deserialize, Serialize};

use crate::engine::ecs::Component;

use super::canvas_layout_params::{default_true, IndexedEnum};

/// 見える範囲の外として飛ばす判定の余白の既定（キャンバスの単位。dp のキャンバスなら dp）。
/// 出典: Flutter `RenderAbstractViewport.defaultCacheExtent` = 250.0（論理画素。2026-09-28 に取得して確認）。
pub const DEFAULT_CACHE_EXTENT: f32 = 250.0;

/// Clamp（端で止める）の慣性の摩擦の既定。
/// 出典: Android `ViewConfiguration.getScrollFriction()` = 0.015 = Flutter `ClampingScrollSimulation.friction` の既定。
pub const DEFAULT_FLING_FRICTION: f32 = 0.015;

/// Bounce（跳ね返り）の慣性の減衰の既定（1 秒で速度が何倍になるか）。
/// 出典: Flutter `BouncingScrollSimulation` の `FrictionSimulation(0.135, …)`（2026-09-28 に取得して確認）。
pub const DEFAULT_BOUNCE_DRAG: f32 = 0.135;

/// serde の既定値: 見える範囲の外の余白。
fn default_cache_extent() -> f32 {
    DEFAULT_CACHE_EXTENT
}

/// serde の既定値: Clamp の摩擦。
fn default_fling_friction() -> f32 {
    DEFAULT_FLING_FRICTION
}

/// serde の既定値: Bounce の減衰。
fn default_bounce_drag() -> f32 {
    DEFAULT_BOUNCE_DRAG
}

/// スクロールの向き。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum ScrollDirection {
    /// 縦だけ（一覧）。
    #[default]
    Vertical,
    /// 横だけ（帯・ページ送り）。
    Horizontal,
    /// 縦と横の両方（大きな絵・表）。
    Both,
}

impl IndexedEnum for ScrollDirection {
    const ALL: &'static [Self] = &[Self::Vertical, Self::Horizontal, Self::Both];
}

impl ScrollDirection {
    /// 軸（0 = X・1 = Y）がスクロールするか。
    pub fn scrolls_axis(self, axis: usize) -> bool {
        match self {
            ScrollDirection::Vertical => axis == 1,
            ScrollDirection::Horizontal => axis == 0,
            ScrollDirection::Both => axis <= 1,
        }
    }
}

/// 端での振る舞い。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum ScrollEdge {
    /// 端を越えて引っぱれ、離すとばねで戻る（Flutter の BouncingScrollPhysics。iOS の手触り）。
    #[default]
    Bounce,
    /// 端で止まる（Flutter の ClampingScrollPhysics・Android の ScrollView。端の光・伸びの表示は無い）。
    Clamp,
}

impl IndexedEnum for ScrollEdge {
    const ALL: &'static [Self] = &[Self::Bounce, Self::Clamp];
}

/// 指を離したときのスナップ。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum ScrollSnap {
    /// スナップしない（慣性のまま止まる）。
    #[default]
    None,
    /// ページ送り（1 回のフリックで最大 1 ページ。ページの長さは `snap_interval`、0 なら窓の長さ。Flutter の PageScrollPhysics）。
    Page,
    /// 間隔（慣性で止まる位置に最も近い `snap_interval` の倍数へ。時刻ホイール・カルーセル。Flutter の FixedExtentScrollPhysics）。
    Interval,
}

impl IndexedEnum for ScrollSnap {
    const ALL: &'static [Self] = &[Self::None, Self::Page, Self::Interval];
}

/// 中身の大きさの決め方。
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum ScrollContentSize {
    /// 子から測る（子の矩形の右端・下端のいちばん遠いところ。窓自身がコンテナなら並べた中身の大きさ＋余白も）。
    #[default]
    Auto,
    /// `content_width`・`content_height` をそのまま使う（一覧の仮想化: 見えている行だけを置き、全体の長さは数で決める）。
    Fixed,
}

impl IndexedEnum for ScrollContentSize {
    const ALL: &'static [Self] = &[Self::Auto, Self::Fixed];
}

/// スクロールの領域の設定（World に置く実体。シリアライズもこの型のまま行う）。
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CanvasScrollComponent {
    /// 有効フラグ。false の間はスクロールしない（位置 0 として置き、アリーナにも参加しない）。
    #[serde(default = "default_true")]
    pub enabled: bool,
    /// スクロールの向き。
    #[serde(default)]
    pub direction: ScrollDirection,
    /// 端での振る舞い（跳ね返る・止まる）。
    #[serde(default)]
    pub edge: ScrollEdge,
    /// 指を離した後の慣性（false なら離した所で止まる。跳ね返りの戻り・スナップはする）。
    #[serde(default = "default_true")]
    pub inertia: bool,
    /// スナップ。
    #[serde(default)]
    pub snap: ScrollSnap,
    /// スナップの長さ（キャンバスの単位）。Page で 0 以下なら窓の長さ、Interval で 0 以下ならスナップしない。
    #[serde(default)]
    pub snap_interval: f32,
    /// 入れ子: 同じ向きの外側のスクロールへ、端に達した残りのドラッグとフリックを渡す。
    #[serde(default = "default_true")]
    pub hand_off_to_parent: bool,
    /// 中身の大きさの決め方。
    #[serde(default)]
    pub content_size: ScrollContentSize,
    /// 中身の幅（Fixed のとき。キャンバスの単位）。
    #[serde(default)]
    pub content_width: f32,
    /// 中身の高さ（Fixed のとき。キャンバスの単位）。
    #[serde(default)]
    pub content_height: f32,
    /// 見える範囲の外の子を描画と当たり判定から外す（切り抜きが有効なときだけ効く）。
    #[serde(default = "default_true")]
    pub cull_outside: bool,
    /// 見える範囲の外として飛ばす判定の余白（キャンバスの単位。窓の外側へこの長さまでは残す）。
    #[serde(default = "default_cache_extent")]
    pub cache_extent: f32,
    /// Clamp の慣性の摩擦（大きいほど早く止まる。Android の ScrollFriction）。
    #[serde(default = "default_fling_friction")]
    pub fling_friction: f32,
    /// Bounce の慣性の減衰（1 秒で速度が何倍になるか。0 と 1 の間。小さいほど早く止まる）。
    #[serde(default = "default_bounce_drag")]
    pub bounce_drag: f32,
}

/// シリアライズ用データ（実体と同じ型）。
pub type CanvasScrollComponentData = CanvasScrollComponent;

impl Default for CanvasScrollComponent {
    /// 既定は縦の一覧（跳ね返り・慣性・入れ子で渡す・見える範囲の外を飛ばす）。
    fn default() -> Self {
        Self {
            enabled: default_true(),
            direction: ScrollDirection::default(),
            edge: ScrollEdge::default(),
            inertia: default_true(),
            snap: ScrollSnap::default(),
            snap_interval: 0.0,
            hand_off_to_parent: default_true(),
            content_size: ScrollContentSize::default(),
            content_width: 0.0,
            content_height: 0.0,
            cull_outside: default_true(),
            cache_extent: DEFAULT_CACHE_EXTENT,
            fling_friction: DEFAULT_FLING_FRICTION,
            bounce_drag: DEFAULT_BOUNCE_DRAG,
        }
    }
}

impl CanvasScrollComponent {
    /// シリアライズ用データから実体を作る。
    pub fn from_data(data: CanvasScrollComponentData) -> Self {
        data
    }

    /// 実体からシリアライズ用データを作る。
    pub fn to_data(&self) -> CanvasScrollComponentData {
        self.clone()
    }

    /// Fixed のときの中身の大きさ（負・NaN は 0）。
    pub fn fixed_content(&self) -> [f32; 2] {
        [self.content_width, self.content_height].map(|v| if v.is_finite() && v > 0.0 { v } else { 0.0 })
    }

    /// 見える範囲の外の余白（負・NaN は 0）。
    pub fn cache_extent_units(&self) -> f32 {
        if self.cache_extent.is_finite() && self.cache_extent > 0.0 { self.cache_extent } else { 0.0 }
    }

    /// Clamp の摩擦（0 以下・NaN は既定）。
    pub fn fling_friction_value(&self) -> f64 {
        if self.fling_friction.is_finite() && self.fling_friction > 0.0 {
            f64::from(self.fling_friction)
        } else {
            f64::from(DEFAULT_FLING_FRICTION)
        }
    }

    /// Bounce の減衰（0 と 1 の間の外・NaN は既定）。
    pub fn bounce_drag_value(&self) -> f64 {
        if self.bounce_drag.is_finite() && self.bounce_drag > 0.0 && self.bounce_drag < 1.0 {
            f64::from(self.bounce_drag)
        } else {
            f64::from(DEFAULT_BOUNCE_DRAG)
        }
    }

    /// スナップの長さ（0 以下・NaN は None）。
    pub fn snap_interval_value(&self) -> Option<f64> {
        (self.snap_interval.is_finite() && self.snap_interval > 0.0).then_some(f64::from(self.snap_interval))
    }
}

impl Component for CanvasScrollComponent {}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 欄の無い旧データ（`{}`）は既定（縦・跳ね返り・慣性・入れ子で渡す・見える範囲の外を飛ばす）で読める。
    #[test]
    fn empty_data_defaults() {
        let d: CanvasScrollComponentData = serde_json::from_str("{}").unwrap();
        assert_eq!(d, CanvasScrollComponent::default());
        assert_eq!(d.cache_extent, DEFAULT_CACHE_EXTENT);
        assert_eq!(d.fling_friction_value(), f64::from(DEFAULT_FLING_FRICTION));
        assert!(d.direction.scrolls_axis(1) && !d.direction.scrolls_axis(0));
    }

    /// 実体 ⇔ データの往復（列挙は snake_case）と、列挙の添字・壊れた値の扱い。
    #[test]
    fn round_trip_indices_and_sanitizing() {
        let c = CanvasScrollComponent {
            direction: ScrollDirection::Both,
            edge: ScrollEdge::Clamp,
            snap: ScrollSnap::Interval,
            snap_interval: 40.0,
            content_size: ScrollContentSize::Fixed,
            content_height: -5.0,
            bounce_drag: 2.0,
            ..CanvasScrollComponent::default()
        };
        let json = serde_json::to_string(&c.to_data()).unwrap();
        assert!(json.contains(r#""direction":"both""#) && json.contains(r#""edge":"clamp""#), "{json}");
        let back: CanvasScrollComponentData = serde_json::from_str(&json).unwrap();
        assert_eq!(CanvasScrollComponent::from_data(back), c);
        assert_eq!(c.fixed_content(), [0.0, 0.0], "負は 0");
        assert_eq!(c.bounce_drag_value(), f64::from(DEFAULT_BOUNCE_DRAG), "1 以上は既定");
        assert_eq!(c.snap_interval_value(), Some(40.0));
        assert_eq!(ScrollDirection::Both.index(), 2);
        assert_eq!(ScrollSnap::from_script_value(1.0), Some(ScrollSnap::Page));
        assert!(ScrollDirection::Both.scrolls_axis(0) && ScrollDirection::Both.scrolls_axis(1));
        assert!(ScrollDirection::Horizontal.scrolls_axis(0) && !ScrollDirection::Horizontal.scrolls_axis(1));
    }
}
