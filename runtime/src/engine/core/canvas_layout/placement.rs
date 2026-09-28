// ============================================================
//  canvas_layout/placement.rs — ノード 1 つの配置を求める純関数（W2-1a の一本化の本体）
//
//  旧実装の 5 か所（collect_sprite_items / collect_canvas_rects / collect_canvas_id_items /
//  pick_2d::walk_pick_candidates_2d / physics2d_ops::collect_actor2d_contexts）が複製していた
//    root_auto 上書き → eff_viewport → アンカー → eff_ct → size_scale → self_world_rs → 子への継承
//  をここ 1 か所にまとめた。**浮動小数の演算の順序まで旧実装と同じ**にしてある
//  （同じ入力なら旧実装とビット単位で同じ結果になる。tests/equivalence.rs で確かめている）。
//  ここを変えると描画・当たり判定・枠・物理のすべてが同時に変わる。
//
//  W2-1b で足したもの（どれも新しい部品を使うノードにだけ効き、既存のシーンの結果は変えない）:
//    - dp のルートキャンバス（CanvasComponent.unit = Dp）… `resolve` の中の分岐（units.rs）
//    - 矩形を割り当てる（コンテナ・親に合わせる）… `resolve_in_rect`
//    - 箱を縮める・中身に合わせる（安全領域・fit）… `resize_box`
// ============================================================

use crate::engine::components::{
    AspectRatioAxis, CanvasComponent, CanvasDrawZone, CanvasTransform, CanvasUnit,
};
use crate::engine::ecs::Entity;
use crate::engine::methods::gizmo_interact::mat4x4_mul;

use super::anchor::{child_anchor_basis, node_anchor_offset};
use super::containers::LayoutSlot;
use super::frame::{AutoScaleDivisor, CanvasLayoutEnv, CanvasParentFrame};
use super::units::dp_canvas_size;

/// CanvasComponent を持たないノードの「キャンバス領域の大きさ」（pivot の基準）。
///
/// 旧実装どおり 1×1 として扱う（pivot × 1px の平行移動が残る既知の規約。
/// docs/backlog.md「CanvasComponent を持たないノードの pivot は基準サイズ 1x1 で解決される」）。
pub const NO_CANVAS_EFF_SIZE: [f32; 2] = [1.0, 1.0];

/// 自動スケールが掛からないときの倍率。
pub const NO_AUTO_SCALE: [f32; 2] = [1.0, 1.0];

/// 倍率が 0 とみなせる大きさ（キャンバスの基準の大きさを求めるときに 0 除算しない）。
const SCALE_EPSILON: f32 = 1e-12;

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
    /// レイアウトが大きさを決めた軸で、スプライトを描く大きさ（親のローカルの画素。W2-1b）。
    /// コンテナが伸ばした・セルいっぱい・親に合わせた軸だけ Some。スプライトはこの大きさで矩形いっぱいに描く。
    pub sprite_fill: [Option<f32>; 2],
    /// レイアウト（コンテナ・親に合わせる）が割り当てた矩形の大きさ（親のローカルの画素。W2-1b）。
    /// 割り当てられていなければ None（従来どおり自分のアンカー・位置で置いたノード）。
    pub layout_rect: Option<[f32; 2]>,
    /// レイアウトの部品（W2-1b: コンテナ・親に合わせる・安全領域・中身に合わせる・dp のルート）が
    /// このノードの矩形を決めたか。false のノードは W2-1a までとまったく同じ計算で置かれている。
    /// 読み手（キャンバス枠・2D 物理とギズモ）は true のときだけ表の矩形（eff_size・有効位置）を読む。
    pub layout_adjusted: bool,
    /// このノードの表示（スプライト・テキスト・パーティクル・図形）のレイヤーに足す値（W2-7）。
    /// 祖先と自分の `CanvasLayoutItem.layer_bias` の和。読み手は `コンポーネントの layer + layer_bias` で並べる（`biased_layer`）。
    pub layer_bias: i32,
}

/// コンポーネントのレイヤーにノードの底上げを足す（W2-7。飽和して i32 の範囲に収める）【純関数】。
///
/// 描画の並び・ポインタの最前面・ジェスチャーの遮り（R3）が同じ値で比べるための 1 か所。
#[inline]
pub fn biased_layer(layer: i32, layer_bias: i32) -> i32 {
    layer.saturating_add(layer_bias)
}

impl CanvasNodePlacement {
    /// スプライトを描く大きさ（親のローカルの画素）。
    ///
    /// レイアウトが大きさを決めた軸は矩形の大きさ、それ以外は従来どおりスプライトの大きさ × サイズ倍率。
    /// 描画・枠・ID 描画・当たり判定・切り抜きの矩形がすべてこれを通る（見た目と当たり判定を一致させる）。
    ///
    /// # 引数
    /// * `width` / `height` - SpriteComponent の大きさ（キャンバスの単位）
    #[inline]
    pub fn sprite_size(&self, width: f32, height: f32) -> [f32; 2] {
        [
            self.sprite_fill[0].unwrap_or(width * self.size_scale[0]),
            self.sprite_fill[1].unwrap_or(height * self.size_scale[1]),
        ]
    }

    /// 子が並ぶ箱の大きさ（このノードのローカルの画素）。
    ///
    /// CanvasComponent があれば「基準の大きさ × 子の累積スケール」（子のアンカーの基準と同じ）、
    /// 無ければレイアウトが割り当てた矩形 × 箱の換算、どちらも無ければ None。
    pub fn box_size(&self) -> Option<[f32; 2]> {
        let cumul = self.child_frame.cumul_scale;
        if let Some(base) = self.canvas_base {
            return Some([base[0] * cumul[0], base[1] * cumul[1]]);
        }
        self.layout_rect.map(|rect| {
            [0, 1].map(|a| {
                if self.size_scale[a].abs() > SCALE_EPSILON {
                    rect[a] * cumul[a] / self.size_scale[a]
                } else {
                    rect[a]
                }
            })
        })
    }

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
pub fn size_scale_of(transform: &CanvasTransform, parent_cumul_scale: [f32; 2]) -> [f32; 2] {
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

/// 子へ渡す累積スケールを求める【純関数】（scale_transform なら親の累積 × 自分の scale × 自動スケール）。
///
/// 浮動小数の演算の順序は旧実装と同じ（(親 × scale) × 自動スケール）。
///
/// # 引数
/// * `transform`          - ノードの CanvasTransform
/// * `parent_cumul_scale` - 親までの累積スケール
/// * `auto_scale_factor`  - ルートキャンバスの自動スケール（それ以外は `NO_AUTO_SCALE`）
pub fn child_cumul_scale_of(
    transform: &CanvasTransform,
    parent_cumul_scale: [f32; 2],
    auto_scale_factor: [f32; 2],
) -> [f32; 2] {
    if transform.scale_transform {
        [
            parent_cumul_scale[0] * transform.scale[0] * auto_scale_factor[0],
            parent_cumul_scale[1] * transform.scale[1] * auto_scale_factor[1],
        ]
    } else {
        [
            transform.scale[0] * auto_scale_factor[0],
            transform.scale[1] * auto_scale_factor[1],
        ]
    }
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

    // ── dp のルートキャンバス（W2-1b）: 大きさは「基準ビューポート ÷ 1 dp の画素数」、子は dp の倍率で画素へ ──
    // px のルート（既定）・子のキャンバス・ビューポートの無い文脈ではこの分岐に入らない（従来の計算のまま）。
    let dp_root_viewport = match (is_root, node.canvas, eff_viewport) {
        (true, Some(cc), Some(vp)) if cc.unit == CanvasUnit::Dp => Some(vp),
        _ => None,
    };
    let dp_scale = env.screen.dp_scale();

    // ── サイズの倍率とキャンバス領域の実効の大きさ ──
    let size_scale = size_scale_of(&transform, parent.cumul_scale);
    let canvas_base = match dp_root_viewport {
        Some(vp) => Some(dp_canvas_size(vp, dp_scale)),
        None => node
            .canvas
            .map(|cc| root_auto.unwrap_or([cc.width, cc.height])),
    };
    let eff_size = match dp_root_viewport {
        // dp のルートの矩形は画面（基準ビューポート）そのもの（キャンバス枠・切り抜き・当たり判定の矩形）
        Some(vp) => vp,
        None => canvas_base
            .map(|[bw, bh]| [bw * size_scale[0], bh * size_scale[1]])
            .unwrap_or(NO_CANVAS_EFF_SIZE),
    };

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
    let auto_scale_factor = if dp_root_viewport.is_some() {
        // dp のルートは縦横同じ倍率（1 dp の画素数）。auto_scale（縦横別の倍率）は使わない
        [dp_scale, dp_scale]
    } else {
        auto_scale_factor_of(
            is_root,
            eff_viewport,
            canvas_auto_scale,
            eff_size,
            env.auto_scale_divisor,
        )
    };
    let child_cumul_scale = child_cumul_scale_of(&transform, parent.cumul_scale, auto_scale_factor);
    let child_frame = CanvasParentFrame {
        anchor_basis: child_anchor_basis(canvas_base),
        world_rs,
        cumul_scale: child_cumul_scale,
        zone,
        // レイヤーの底上げと見た目の平行移動は親のまま受け継ぐ（自分の分は走査の place_node が足す。W2-7）
        layer_bias: parent.layer_bias,
        visual_shift: parent.visual_shift,
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
        sprite_fill: [None, None],
        layout_rect: None,
        layout_adjusted: dp_root_viewport.is_some(),
        layer_bias: parent.layer_bias,
    }
}

/// レイアウトが割り当てた矩形（コンテナのセル・親に合わせる）へノードを置く【純関数・W2-1b】。
///
/// 位置はノード自身のアンカー・位置・scale_transform を使わず、矩形で決める:
///   有効位置 = 矩形の左上 + pivot × 矩形の大きさ（回転と scale はノードの値のまま、pivot の周りに掛かる）
/// 矩形がノードの領域になる: CanvasComponent を持つノードはキャンバス領域（枠・切り抜き）が矩形の大きさになり、
/// 持たないノードも矩形を領域として扱う（子のアンカーの基準・ローカルの原点＝矩形の左上）。
/// 矩形が「伸ばした」軸では、ノードのスプライトを矩形の大きさで描く（`sprite_fill`）。
/// ルート（親が居ない）には使わない（ルートの矩形はビューポート）。
///
/// # 引数
/// * `parent` - 親から受け取った文脈（矩形はこの文脈のローカルの画素）
/// * `node`   - ノードの入力
/// * `env`    - 走査全体の入力
/// * `slot`   - 割り当てられた矩形
pub fn resolve_in_rect(
    parent: &CanvasParentFrame,
    node: &CanvasNodeInput<'_>,
    env: &CanvasLayoutEnv<'_>,
    slot: &LayoutSlot,
) -> CanvasNodePlacement {
    let transform = node.transform.clone();
    let size_scale = size_scale_of(&transform, parent.cumul_scale);
    // 矩形の基準の大きさ（矩形 ÷ サイズ倍率。倍率 0 の退化した軸は矩形のまま）。子のアンカーの基準になる
    let rect_base = [0, 1].map(|a| {
        if size_scale[a].abs() > SCALE_EPSILON {
            slot.size[a] / size_scale[a]
        } else {
            slot.size[a]
        }
    });
    let canvas_base = node.canvas.map(|_| rect_base);
    // 行列の pivot の基準も矩形（CanvasComponent が無くてもローカルの原点が矩形の左上になる）
    let eff_size = slot.size;
    let eff_transform = CanvasTransform {
        position: [
            slot.origin[0] + transform.pivot[0] * slot.size[0],
            slot.origin[1] + transform.pivot[1] * slot.size[1],
        ],
        anchor: [0.0, 0.0],
        ..transform.clone()
    };
    let world_rs = mat4x4_mul(
        parent.world_rs,
        CanvasTransform {
            scale: [1.0, 1.0],
            ..eff_transform.clone()
        }
        .to_mat4_sized(eff_size[0], eff_size[1]),
    );
    let child_cumul_scale = child_cumul_scale_of(&transform, parent.cumul_scale, NO_AUTO_SCALE);
    let child_frame = CanvasParentFrame {
        anchor_basis: child_anchor_basis(Some(rect_base)),
        world_rs,
        cumul_scale: child_cumul_scale,
        zone: parent.zone,
        // レイヤーの底上げと見た目の平行移動は親のまま受け継ぐ（自分の分は走査の place_node が足す。W2-7）
        layer_bias: parent.layer_bias,
        visual_shift: parent.visual_shift,
    };
    CanvasNodePlacement {
        root_auto: None,
        transform,
        eff_viewport: env.viewport_size,
        anchor_offset: [0.0, 0.0],
        eff_transform,
        zone: parent.zone,
        size_scale,
        canvas_base,
        canvas_auto_scale: node.canvas.is_some_and(|cc| cc.auto_scale),
        eff_size,
        world_rs,
        auto_scale_factor: NO_AUTO_SCALE,
        child_frame,
        sprite_fill: [0, 1].map(|a| slot.fill[a].then_some(slot.size[a])),
        layout_rect: Some(slot.size),
        layout_adjusted: true,
        layer_bias: parent.layer_bias,
    }
}

/// 置いたノードを、親のローカルの画素で `offset` だけ平行移動する【純関数・W2-7】。
///
/// `CanvasLayoutItem.translate*`（見た目の平行移動）の本体。レイアウト（大きさ・並び・安全領域・子の箱）は変えず、
/// 有効位置とワールド行列だけをずらす（子へ渡す文脈の行列も同じだけずれるので、子孫はそのまま付いてくる）。
/// 子へ渡す `visual_shift` へワールドの画素のずれを足す（子孫の安全領域をずらす前の位置で求めるため）。
/// 読み手（キャンバス枠・2D 物理・ScreenPosition）がずらした位置を読むよう `layout_adjusted` を立てる。
///
/// # 引数
/// * `placement` - 置いた結果（安全領域を当てた後）
/// * `parent`    - 親から受け取った文脈
/// * `offset`    - 平行移動（親のローカルの画素）
pub fn translate_placement(
    placement: &CanvasNodePlacement,
    parent: &CanvasParentFrame,
    offset: [f32; 2],
) -> CanvasNodePlacement {
    let eff = &placement.eff_transform;
    let eff_transform = CanvasTransform {
        position: [eff.position[0] + offset[0], eff.position[1] + offset[1]],
        ..eff.clone()
    };
    let world_rs = mat4x4_mul(
        parent.world_rs,
        CanvasTransform {
            scale: [1.0, 1.0],
            ..eff_transform.clone()
        }
        .to_mat4_sized(placement.eff_size[0], placement.eff_size[1]),
    );
    // 親のローカルのずれ → ワールドのずれ（親の行列の回転の部分を掛ける。行列は列ベクトルの流儀で [行][列]）
    let m = &parent.world_rs;
    let world_offset = [
        m[0][0] * offset[0] + m[0][1] * offset[1],
        m[1][0] * offset[0] + m[1][1] * offset[1],
    ];
    let shift = placement.child_frame.visual_shift;
    CanvasNodePlacement {
        eff_transform,
        world_rs,
        layout_adjusted: true,
        child_frame: CanvasParentFrame {
            world_rs,
            visual_shift: [shift[0] + world_offset[0], shift[1] + world_offset[1]],
            ..placement.child_frame
        },
        ..placement.clone()
    }
}

/// ノードの箱（子が並ぶ領域）を、ローカル座標の矩形へ置き換える【純関数・W2-1b】。
///
/// 安全領域で縮める・コンテナの中身に合わせる（fit）に使う。箱の左上をローカルの `new_origin` へ動かし、
/// 大きさを `new_box` にする。子の累積スケールは変えない（自動スケール・dp の倍率を掛け直さない）。
/// キャンバス領域の実効の大きさ（枠・切り抜き）は箱と同じ割合で変え、ノードの行列は新しい左上が
/// 元のローカルの `new_origin` に重なるように有効位置をずらす（回転していても同じ点に重なる）。
///
/// # 引数
/// * `placement`  - 元の配置
/// * `parent`     - 親から受け取った文脈
/// * `new_origin` - 新しい箱の左上（元のローカル座標）
/// * `new_box`    - 新しい箱の大きさ（ローカルの画素）
pub fn resize_box(
    placement: &CanvasNodePlacement,
    parent: &CanvasParentFrame,
    new_origin: [f32; 2],
    new_box: [f32; 2],
) -> CanvasNodePlacement {
    let cumul = placement.child_frame.cumul_scale;
    let old_box = placement.box_size().unwrap_or(new_box);
    // 基準の大きさ（子のアンカーの基準）= 箱 ÷ 子の累積スケール
    let new_base = [0, 1].map(|a| if cumul[a].abs() > SCALE_EPSILON { new_box[a] / cumul[a] } else { new_box[a] });
    let has_region = placement.canvas_base.is_some() || placement.layout_rect.is_some();
    let canvas_base = placement.canvas_base.map(|_| new_base);
    // 実効の大きさ（枠・切り抜き・行列の pivot の基準）は箱と同じ割合で（元の箱が 0 の軸は箱の大きさのまま）
    let eff_size = if has_region {
        [0, 1].map(|a| {
            if old_box[a].abs() > SCALE_EPSILON {
                placement.eff_size[a] * (new_box[a] / old_box[a])
            } else {
                new_box[a]
            }
        })
    } else {
        placement.eff_size
    };
    let layout_rect = placement.layout_rect.map(|rect| {
        [0, 1].map(|a| if old_box[a].abs() > SCALE_EPSILON { rect[a] * (new_box[a] / old_box[a]) } else { rect[a] })
    });
    // 新しい左上が元のローカルの new_origin に重なるよう有効位置をずらす:
    //   位置' = 位置 + R × (new_origin − pivot × 元の大きさ + pivot × 新しい大きさ)
    let eff = &placement.eff_transform;
    let local = [
        new_origin[0] - eff.pivot[0] * placement.eff_size[0] + eff.pivot[0] * eff_size[0],
        new_origin[1] - eff.pivot[1] * placement.eff_size[1] + eff.pivot[1] * eff_size[1],
    ];
    let (sin, cos) = eff.rotation.to_radians().sin_cos();
    let eff_transform = CanvasTransform {
        position: [
            eff.position[0] + cos * local[0] - sin * local[1],
            eff.position[1] + sin * local[0] + cos * local[1],
        ],
        ..eff.clone()
    };
    let world_rs = mat4x4_mul(
        parent.world_rs,
        CanvasTransform {
            scale: [1.0, 1.0],
            ..eff_transform.clone()
        }
        .to_mat4_sized(eff_size[0], eff_size[1]),
    );
    CanvasNodePlacement {
        eff_transform,
        canvas_base,
        eff_size,
        world_rs,
        layout_rect,
        layout_adjusted: true,
        child_frame: CanvasParentFrame {
            anchor_basis: if has_region {
                child_anchor_basis(Some(new_base))
            } else {
                placement.child_frame.anchor_basis
            },
            world_rs,
            ..placement.child_frame
        },
        ..placement.clone()
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
