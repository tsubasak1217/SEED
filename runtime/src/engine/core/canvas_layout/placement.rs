// ============================================================
//  canvas_layout/placement.rs — ノード 1 つの配置を求める純関数（W2-1a の一本化の本体）
//
//  旧実装の 5 か所（collect_sprite_items / collect_canvas_rects / collect_canvas_id_items /
//  pick_2d::walk_pick_candidates_2d / physics2d_ops::collect_actor2d_contexts）が複製していた
//    root_auto 上書き → eff_viewport → アンカー → eff_ct → size_scale → self_world_rs → 子への継承
//  をここ 1 か所にまとめた。**浮動小数の演算の順序まで旧実装と同じ**にしてある
//  （同じ入力なら旧実装とビット単位で同じ結果になる。tests/equivalence.rs で確かめている）。
//  ここを変えると描画・当たり判定・枠・物理のすべてが同時に変わる。
// ============================================================

use crate::engine::components::{
    AspectRatioAxis, CanvasComponent, CanvasDrawZone, CanvasTransform,
};
use crate::engine::ecs::Entity;
use crate::engine::methods::gizmo_interact::mat4x4_mul;

use super::anchor::{child_anchor_basis, node_anchor_offset};
use super::frame::{AutoScaleDivisor, CanvasLayoutEnv, CanvasParentFrame};

/// CanvasComponent を持たないノードの「キャンバス領域の大きさ」（pivot の基準）。
///
/// 旧実装どおり 1×1 として扱う（pivot × 1px の平行移動が残る既知の規約。
/// docs/backlog.md「CanvasComponent を持たないノードの pivot は基準サイズ 1x1 で解決される」）。
pub const NO_CANVAS_EFF_SIZE: [f32; 2] = [1.0, 1.0];

/// 自動スケールが掛からないときの倍率。
const NO_AUTO_SCALE: [f32; 2] = [1.0, 1.0];

/// サイズ倍率が掛からないとき（scale_size = false）の倍率。
const NO_SIZE_SCALE: f32 = 1.0;

/// ノード 1 つぶんの入力（World から読んだ値）。
pub struct CanvasNodeInput<'a> {
    /// アクター本体の entity（自動解像度・ビューポート上書きの表のキー）。
    pub entity: Entity,
    /// 保存されている CanvasTransform。
    pub transform: &'a CanvasTransform,
    /// レイアウトに使う CanvasComponent（Canvas スロットのうち最初にコンポーネントが引けたもの）。
    pub canvas: Option<&'a CanvasComponent>,
}

/// ノード 1 つの配置（`resolve` の結果）。読み手はここから行列・大きさ・ゾーンを取る。
#[derive(Clone, Debug, PartialEq)]
pub struct CanvasNodePlacement {
    /// 自動解像度の上書き（最上位のビューポート・ルートキャンバスだけ Some）。
    pub root_auto: Option<[f32; 2]>,
    /// 解決に使った CanvasTransform（`root_auto` があれば恒等に置き換えたもの）。
    /// スケールモード・scale・pivot・rotation・position の出どころ。
    pub transform: CanvasTransform,
    /// 最上位ノードの基準ビューポート（Camera 参照の上書き → 既定のビューポートの順）。
    pub eff_viewport: Option<[f32; 2]>,
    /// アンカーのオフセット（親ローカル px）。
    pub anchor_offset: [f32; 2],
    /// 有効 CanvasTransform（位置はアンカーとスケールモードを反映済み・anchor は 0）。
    pub eff_transform: CanvasTransform,
    /// 描画ゾーン（ルートは自身の CanvasComponent の draw_zone、それ以外は親から継承）。
    pub zone: CanvasDrawZone,
    /// サイズの倍率（scale_size とアスペクト比維持を反映。スプライト・テキスト・メッシュの大きさへ掛ける）。
    pub size_scale: [f32; 2],
    /// CanvasComponent の基準の大きさ（自動解像度を反映・サイズ倍率は掛けない）。無ければ None。
    pub canvas_base: Option<[f32; 2]>,
    /// CanvasComponent の auto_scale（無ければ false）。
    pub canvas_auto_scale: bool,
    /// キャンバス領域の実効の大きさ（基準 × サイズ倍率。CanvasComponent が無ければ 1×1）。
    pub eff_size: [f32; 2],
    /// 自身のワールド行列（行優先。スケールを外した有効トランスフォーム × 親。子の親行列になる）。
    pub world_rs: [[f32; 4]; 4],
    /// ルートキャンバスの自動スケール（ビューポート ÷ キャンバス。それ以外は 1）。
    pub auto_scale_factor: [f32; 2],
    /// 子へ渡す文脈。
    pub child_frame: CanvasParentFrame,
}

impl CanvasNodePlacement {
    /// スケールモード: 位置を親の累積スケールに追従させるか。
    #[inline]
    pub fn scale_transform(&self) -> bool {
        self.transform.scale_transform
    }

    /// スケールモード: サイズを親の累積スケールに追従させるか。
    #[inline]
    pub fn scale_size(&self) -> bool {
        self.transform.scale_size
    }
}

/// サイズの倍率を求める（scale_size とアスペクト比維持の規則）【純関数】。
///
/// # 引数
/// * `transform`          - ノードの CanvasTransform（スケールモードの出どころ）
/// * `parent_cumul_scale` - 親までの累積スケール
fn size_scale_of(transform: &CanvasTransform, parent_cumul_scale: [f32; 2]) -> [f32; 2] {
    let keep_aspect = transform.keep_aspect_ratio;
    let is_width_axis = matches!(transform.aspect_ratio_axis, AspectRatioAxis::Width);
    let x = if transform.scale_size {
        if keep_aspect && !is_width_axis {
            parent_cumul_scale[1]
        } else {
            parent_cumul_scale[0]
        }
    } else {
        NO_SIZE_SCALE
    };
    let y = if transform.scale_size {
        if keep_aspect && is_width_axis {
            parent_cumul_scale[0]
        } else {
            parent_cumul_scale[1]
        }
    } else {
        NO_SIZE_SCALE
    };
    [x, y]
}

/// 自動スケールの倍率（ルートキャンバスかつ auto_scale のとき、ビューポート ÷ キャンバスの大きさ）【純関数】。
fn auto_scale_factor_of(
    is_root: bool,
    eff_viewport: Option<[f32; 2]>,
    canvas_auto_scale: Option<bool>,
    eff_size: [f32; 2],
    divisor: AutoScaleDivisor,
) -> [f32; 2] {
    if !is_root {
        return NO_AUTO_SCALE;
    }
    match (eff_viewport, canvas_auto_scale) {
        (Some([vw, vh]), Some(true)) => match divisor {
            AutoScaleDivisor::Raw => [vw / eff_size[0], vh / eff_size[1]],
            AutoScaleDivisor::GuardEpsilon => [
                vw / eff_size[0].max(f32::EPSILON),
                vh / eff_size[1].max(f32::EPSILON),
            ],
        },
        _ => NO_AUTO_SCALE,
    }
}

/// ノード 1 つの配置を求める【純関数・副作用なし】。
///
/// # 引数
/// * `parent` - 親から受け取った文脈
/// * `node`   - ノードの入力（CanvasTransform と CanvasComponent）
/// * `env`    - 走査全体の入力（ビューポート・自動解像度・設計空間表示）
///
/// # 戻り値
/// 配置（有効トランスフォーム・ワールド行列・大きさ・ゾーン・子へ渡す文脈）。
pub fn resolve(
    parent: &CanvasParentFrame,
    node: &CanvasNodeInput<'_>,
    env: &CanvasLayoutEnv<'_>,
) -> CanvasNodePlacement {
    let is_root = parent.is_root();

    // ── ビューポート・ルートキャンバスの自動解像度上書き（Phase B）──
    // Some のとき: 解像度を自動計算値へ置き換え、Transform を恒等として扱う（保存データは書き換えない）。
    let root_auto = if is_root {
        env.root_auto_sizes.get(&node.entity).copied()
    } else {
        None
    };
    let transform = if root_auto.is_some() {
        CanvasTransform::default()
    } else {
        node.transform.clone()
    };

    // ── 基準ビューポート（最上位は Camera 参照の上書きを優先）──
    let eff_viewport = if is_root {
        env.viewport_overrides
            .get(&node.entity)
            .copied()
            .or(env.viewport_size)
    } else {
        env.viewport_size
    };

    // ── アンカー（最上位＝ビューポート基準／子＝親キャンバス基準）──
    let anchor_offset = node_anchor_offset(
        parent.anchor_basis,
        transform.anchor,
        parent.cumul_scale,
        eff_viewport,
        env.design_space,
    );

    // ── 有効位置（scale_transform なら親の累積スケールを位置へ掛ける）──
    let eff_position = if transform.scale_transform {
        [
            transform.position[0] * parent.cumul_scale[0] + anchor_offset[0],
            transform.position[1] * parent.cumul_scale[1] + anchor_offset[1],
        ]
    } else {
        [
            transform.position[0] + anchor_offset[0],
            transform.position[1] + anchor_offset[1],
        ]
    };
    let eff_transform = CanvasTransform {
        position: eff_position,
        rotation: transform.rotation,
        scale: transform.scale,
        pivot: transform.pivot,
        anchor: [0.0, 0.0],
        ..transform.clone()
    };

    // ── 描画ゾーン（ルートは自身の draw_zone、それ以外は親から継承）──
    let zone = if is_root {
        node.canvas.map(|cc| cc.draw_zone).unwrap_or(parent.zone)
    } else {
        parent.zone
    };

    // ── サイズの倍率とキャンバス領域の実効の大きさ ──
    let size_scale = size_scale_of(&transform, parent.cumul_scale);
    let canvas_base = node
        .canvas
        .map(|cc| root_auto.unwrap_or([cc.width, cc.height]));
    let eff_size = canvas_base
        .map(|[bw, bh]| [bw * size_scale[0], bh * size_scale[1]])
        .unwrap_or(NO_CANVAS_EFF_SIZE);

    // ── 自身のワールド行列（スケールは外す。pivot は実際のキャンバスの大きさで効かせる）──
    let world_rs = mat4x4_mul(
        parent.world_rs,
        CanvasTransform {
            scale: [1.0, 1.0],
            ..eff_transform.clone()
        }
        .to_mat4_sized(eff_size[0], eff_size[1]),
    );

    // ── 子への継承（アンカー基準・自動スケール・累積スケール）──
    // スケールモードは各子が自身の CanvasTransform から読み取るため伝播しない。
    let canvas_auto_scale = node.canvas.map(|cc| cc.auto_scale);
    let auto_scale_factor = auto_scale_factor_of(
        is_root,
        eff_viewport,
        canvas_auto_scale,
        eff_size,
        env.auto_scale_divisor,
    );
    let child_cumul_scale = if transform.scale_transform {
        [
            parent.cumul_scale[0] * transform.scale[0] * auto_scale_factor[0],
            parent.cumul_scale[1] * transform.scale[1] * auto_scale_factor[1],
        ]
    } else {
        [
            transform.scale[0] * auto_scale_factor[0],
            transform.scale[1] * auto_scale_factor[1],
        ]
    };
    let child_frame = CanvasParentFrame {
        anchor_basis: child_anchor_basis(canvas_base),
        world_rs,
        cumul_scale: child_cumul_scale,
        zone,
    };

    CanvasNodePlacement {
        root_auto,
        transform,
        eff_viewport,
        anchor_offset,
        eff_transform,
        zone,
        size_scale,
        canvas_base,
        canvas_auto_scale: canvas_auto_scale.unwrap_or(false),
        eff_size,
        world_rs,
        auto_scale_factor,
        child_frame,
    }
}

/// CanvasTransform を持たないノード（3D アクター・3D ワールドキャンバス等）が子へ渡す文脈【純関数】。
///
/// 描画・当たり判定・枠はこのノードの子孫を扱わない（CanvasTransform を持たないノードで打ち切る）。
/// 2D 物理だけが子孫をたどるため、旧 `collect_actor2d_contexts` と同じ規則で素通しする:
/// 行列・累積スケール・ゾーンは親のまま、アンカー基準は自身の CanvasComponent（無ければ無効）。
///
/// # 引数
/// * `parent` - 親から受け取った文脈
/// * `canvas_base` - 自身の CanvasComponent の基準の大きさ（最上位なら自動解像度を反映済み）
pub fn pass_through_frame(
    parent: &CanvasParentFrame,
    canvas_base: Option<[f32; 2]>,
) -> CanvasParentFrame {
    CanvasParentFrame {
        anchor_basis: child_anchor_basis(canvas_base),
        ..*parent
    }
}
