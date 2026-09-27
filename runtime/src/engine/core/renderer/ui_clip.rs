// ============================================================
//  ui_clip.rs — UI の切り抜き（クリップ）: 領域の収集・ランの分割・scissor の矩形（W2-0 の試作）
//
//  【方式】（決定の根拠と計測は docs/app_platform_roadmap.md §3.8）
//  軸に沿った矩形の切り抜き（スクロール領域・一覧）は GPU の scissor で切る。シェーダーもパイプラインも変えない。
//    1. 収集: キャンバスの DFS で「切り抜きの根」のノードに入ったら、その矩形（ワールド座標の 4 隅）を表へ積み、
//       子孫の描画アイテムへ表の番号（UiClipId）を持たせる（UiClipCollector）。入れ子は外側の番号（parent）を持つ
//    2. 分割: 描画順のラン（ui_draw_order）を、切り抜きの番号が変わるところで分ける（split_run_by_clip）
//       → 描画呼び出しは「切り抜きの境目の数」だけ増える（同じ切り抜きの中の同じ種別は従来どおり 1 ランにまとまる）
//    3. 描画: ランごとに、番号から画素の矩形（4 隅を射影した AABB と祖先の交差）を求めて set_scissor_rect する（scissor_px）
//  角丸・円の切り抜きは scissor ではできないので、シェーダーの SDF で行う（W2-4。ここでは扱わない）。
//  回転したノード・3D ワールドキャンバス（透視）では 4 隅の AABB になり正確に切れない（試作の制限。§3.8）。
//
//  【試作での根の決め方】名前で指定する（engine::core::ui_spike の clip=<名前>）。根の矩形は
//  そのノードの最初の有効なスプライトの矩形。W2-1 で専用のコンポーネント（レイアウトの矩形）に置き換える。
//
//  【座標の規約】（primitive2d/pass.rs・font/canvas_text.rs の project と同じ）
//    - 4 隅はワールド座標（スプライトの GPU 行列〈列優先 model[col][row]〉でユニットクワッドの隅を写したもの）
//    - ビュー射影は行優先（vp[row][col]）
//    - NDC → 画素: x = (ndc.x + 1) / 2 × 幅、y = (1 − ndc.y) / 2 × 高さ（wgpu のフレームバッファは y 下向き）
// ============================================================

use crate::engine::core::renderer::ui_draw_order::UiDrawRun;

/// 切り抜きの領域の番号（1 フレームの表の添字）。
pub type UiClipId = u16;

/// 1 フレームに積める切り抜きの領域の数の上限（番号が u16 に収まる数）。
pub const MAX_CLIP_REGIONS: usize = UiClipId::MAX as usize;

/// ユニットクワッドの 4 隅（スプライトの GPU 行列で写すとスプライトの矩形の 4 隅になる）。
const UNIT_QUAD_CORNERS: [[f32; 2]; 4] = [[0.0, 0.0], [1.0, 0.0], [0.0, 1.0], [1.0, 1.0]];

/// クリップ空間の w がこれ以下なら射影できない（カメラの後ろ・退化した行列）とみなす。
const MIN_CLIP_W: f32 = 1e-6;

/// NDC の半分の幅（[-1, 1] → [0, 1] の換算に使う）。
const NDC_HALF: f32 = 0.5;

/// 画素の境界の丸めの許容量（px）。浮動小数の誤差で境界ちょうどの矩形が 1 画素太らないようにする。
const PIXEL_SNAP_EPSILON: f32 = 1e-3;

/// 切り抜きの 1 領域。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct UiClipRegion {
    /// 矩形の 4 隅（ワールド座標 xyz）。
    pub corners: [[f32; 3]; 4],
    /// 外側の切り抜き（入れ子の親。無ければ None）。描画の直前に交差を取る。
    pub parent: Option<UiClipId>,
}

/// 収集中の切り抜きの表と「今どの切り抜きの中にいるか」のスタック（キャンバスの DFS と一緒に進める）。
pub struct UiClipCollector<'a> {
    /// 切り抜きの根にするノードの名前（試作。空なら何もしない）。
    root_names: &'a [String],
    /// 積んだ領域（番号 = 添字）。
    regions: Vec<UiClipRegion>,
    /// 今いる切り抜きの番号のスタック（DFS の入れ子と同じ深さで積み下ろす）。
    stack: Vec<UiClipId>,
}

impl<'a> UiClipCollector<'a> {
    /// 収集を始める。
    ///
    /// # 引数
    /// * `root_names` - 切り抜きの根にするノードの名前（空なら何も切り抜かない＝従来どおり）
    pub fn new(root_names: &'a [String]) -> Self {
        Self { root_names, regions: Vec::new(), stack: Vec::new() }
    }

    /// 何も切り抜かない収集（カメラプレビュー・3D ワールドキャンバスなど、切り抜きを扱わない経路用）。
    pub fn disabled() -> UiClipCollector<'static> {
        UiClipCollector { root_names: &[], regions: Vec::new(), stack: Vec::new() }
    }

    /// 今いる切り抜きの番号（描画アイテムへ持たせる。切り抜きの外なら None）。
    pub fn current(&self) -> Option<UiClipId> {
        self.stack.last().copied()
    }

    /// キャンバスノードに入る。根のノードなら矩形を表へ積み、子孫をその中に入れる。
    ///
    /// # 引数
    /// * `name`           - ノードの名前
    /// * `sprite_gpu_mat` - ノードの最初の有効なスプライトの GPU 行列（列優先。無ければ根にできない）
    ///
    /// # 戻り値
    /// 積んだら true（呼び出し側は子孫を集め終えたら必ず `exit_node` を呼ぶ）。
    pub fn enter_node(&mut self, name: &str, sprite_gpu_mat: Option<&[[f32; 4]; 4]>) -> bool {
        if self.root_names.is_empty() || !self.root_names.iter().any(|root| root == name) {
            return false;
        }
        let Some(model) = sprite_gpu_mat else { return false };
        if self.regions.len() >= MAX_CLIP_REGIONS {
            return false;
        }
        let id = self.regions.len() as UiClipId;
        self.regions.push(UiClipRegion { corners: unit_quad_world_corners(model), parent: self.current() });
        self.stack.push(id);
        true
    }

    /// `enter_node` が true を返したノードの子孫を集め終えた。
    pub fn exit_node(&mut self) {
        self.stack.pop();
    }

    /// 積んだ領域の表（描画アイテムの番号はこの添字）。
    pub fn into_regions(self) -> Vec<UiClipRegion> {
        self.regions
    }
}

/// スプライトの GPU 行列（列優先）でユニットクワッドの 4 隅をワールド座標へ写す【純関数】。
fn unit_quad_world_corners(model: &[[f32; 4]; 4]) -> [[f32; 3]; 4] {
    UNIT_QUAD_CORNERS.map(|[u, v]| {
        let mut world = [0.0f32; 3];
        for (row, w) in world.iter_mut().enumerate() {
            *w = model[0][row] * u + model[1][row] * v + model[3][row];
        }
        world
    })
}

/// NDC の軸に沿った矩形（min が左下・max が右上）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NdcRect {
    /// 最小の角（x, y）。
    pub min: [f32; 2],
    /// 最大の角（x, y）。
    pub max: [f32; 2],
}

impl NdcRect {
    /// 2 つの矩形の交差（重ならなければ幅か高さが 0 以下の矩形になる）。
    pub fn intersect(&self, other: &NdcRect) -> NdcRect {
        NdcRect {
            min: [self.min[0].max(other.min[0]), self.min[1].max(other.min[1])],
            max: [self.max[0].min(other.max[0]), self.max[1].min(other.max[1])],
        }
    }
}

/// 画素の矩形（scissor。左上が原点）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ScissorRect {
    /// 左端（px）。
    pub x: u32,
    /// 上端（px）。
    pub y: u32,
    /// 幅（px）。
    pub width: u32,
    /// 高さ（px）。
    pub height: u32,
}

impl ScissorRect {
    /// 描画先の全体。
    pub fn full(target: [u32; 2]) -> Self {
        Self { x: 0, y: 0, width: target[0], height: target[1] }
    }

    /// 1 画素も含まないか（描画を丸ごと飛ばしてよい）。
    pub fn is_empty(&self) -> bool {
        self.width == 0 || self.height == 0
    }
}

/// ワールド座標の点を NDC へ射影する【純関数】（射影できなければ None）。
fn project_to_ndc(point: &[f32; 3], view_proj: &[[f32; 4]; 4]) -> Option<[f32; 2]> {
    let world = [point[0], point[1], point[2], 1.0];
    let mut clip = [0.0f32; 4];
    for (row, c) in clip.iter_mut().enumerate() {
        let r = &view_proj[row];
        *c = r[0] * world[0] + r[1] * world[1] + r[2] * world[2] + r[3] * world[3];
    }
    if clip[3] <= MIN_CLIP_W {
        return None;
    }
    Some([clip[0] / clip[3], clip[1] / clip[3]])
}

/// 1 つの領域（祖先を含まない）の 4 隅を射影した AABB【純関数】。
fn region_ndc_aabb(region: &UiClipRegion, view_proj: &[[f32; 4]; 4]) -> Option<NdcRect> {
    let mut rect = NdcRect { min: [f32::INFINITY; 2], max: [f32::NEG_INFINITY; 2] };
    for corner in &region.corners {
        let [x, y] = project_to_ndc(corner, view_proj)?;
        rect.min = [rect.min[0].min(x), rect.min[1].min(y)];
        rect.max = [rect.max[0].max(x), rect.max[1].max(y)];
    }
    Some(rect)
}

/// 切り抜きの番号から、祖先との交差を取った NDC の矩形を求める【純関数】。
///
/// # 戻り値
/// 射影できない領域（カメラの後ろ）が鎖の中にあれば None（呼び出し側は切り抜かずに描く）。
/// 番号が表の外なら None。
pub fn clip_ndc_rect(regions: &[UiClipRegion], id: UiClipId, view_proj: &[[f32; 4]; 4]) -> Option<NdcRect> {
    let mut current = Some(id);
    let mut result: Option<NdcRect> = None;
    // 親は必ず子より先に積まれる（番号が小さい）ので、鎖は表の長さ以内で終わる
    for _ in 0..regions.len() {
        let Some(index) = current else { break };
        let region = regions.get(index as usize)?;
        let rect = region_ndc_aabb(region, view_proj)?;
        result = Some(match result {
            Some(inner) => inner.intersect(&rect),
            None => rect,
        });
        current = region.parent;
    }
    result
}

/// NDC の矩形を描画先の画素の scissor にする【純関数】。
///
/// 端の画素を欠かさないよう外側へ丸め、描画先の外は切り詰める。重ならなければ空（幅か高さが 0）。
pub fn scissor_px(rect: &NdcRect, target: [u32; 2]) -> ScissorRect {
    let (width, height) = (target[0] as f32, target[1] as f32);
    let to_px_x = |ndc: f32| (ndc + 1.0) * NDC_HALF * width;
    let to_px_y = |ndc: f32| (1.0 - ndc) * NDC_HALF * height;
    let left = (to_px_x(rect.min[0]) + PIXEL_SNAP_EPSILON).floor().clamp(0.0, width);
    let right = (to_px_x(rect.max[0]) - PIXEL_SNAP_EPSILON).ceil().clamp(0.0, width);
    // NDC の y は上向きなので、max.y が画素の上端になる
    let top = (to_px_y(rect.max[1]) + PIXEL_SNAP_EPSILON).floor().clamp(0.0, height);
    let bottom = (to_px_y(rect.min[1]) - PIXEL_SNAP_EPSILON).ceil().clamp(0.0, height);
    ScissorRect {
        x: left as u32,
        y: top as u32,
        width: (right - left).max(0.0) as u32,
        height: (bottom - top).max(0.0) as u32,
    }
}

/// 1 本のランを、切り抜きの番号が変わるところで分ける【純関数】。
///
/// # 引数
/// * `run`     - 分けるラン（その種別のリストの区間）
/// * `clip_of` - その種別のリストの添字 → そのアイテムの切り抜きの番号
///
/// # 戻り値
/// (区間, 切り抜きの番号) の並び。番号が区間の中で変わらなければ 1 本のまま。
pub fn split_run_by_clip(
    run: UiDrawRun,
    clip_of: impl Fn(usize) -> Option<UiClipId>,
) -> Vec<(UiDrawRun, Option<UiClipId>)> {
    let mut pieces: Vec<(UiDrawRun, Option<UiClipId>)> = Vec::new();
    for index in run.start..run.end {
        let clip = clip_of(index);
        match pieces.last_mut() {
            Some((piece, piece_clip)) if *piece_clip == clip => piece.end = index + 1,
            _ => pieces.push((UiDrawRun { kind: run.kind, start: index, end: index + 1 }, clip)),
        }
    }
    pieces
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::renderer::ui_draw_order::UiDrawKind;

    /// 単位行列（行優先・列優先どちらでも同じ）。
    const IDENTITY: [[f32; 4]; 4] = [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ];

    /// 列優先のスプライト行列（ユニットクワッド → 左下 (x, y)・幅 w・高さ h の矩形）。
    fn sprite_mat(x: f32, y: f32, w: f32, h: f32) -> [[f32; 4]; 4] {
        [[w, 0.0, 0.0, 0.0], [0.0, h, 0.0, 0.0], [0.0, 0.0, 1.0, 0.0], [x, y, 0.0, 1.0]]
    }

    fn run(kind: UiDrawKind, start: usize, end: usize) -> UiDrawRun {
        UiDrawRun { kind, start, end }
    }

    /// 根の名前が無い・一致しない・スプライトが無いノードは積まない。
    #[test]
    fn collector_ignores_non_roots() {
        let names = vec!["Clip".to_string()];
        let mut collector = UiClipCollector::new(&names);
        let mat = sprite_mat(0.0, 0.0, 1.0, 1.0);
        assert!(!collector.enter_node("Other", Some(&mat)));
        assert!(!collector.enter_node("Clip", None), "スプライトが無いと矩形が決まらない");
        assert_eq!(collector.current(), None);
        let mut disabled = UiClipCollector::disabled();
        assert!(!disabled.enter_node("Clip", Some(&mat)));
        assert!(collector.into_regions().is_empty());
    }

    /// 入れ子の根は外側の番号を親に持ち、出ると外側へ戻る。
    #[test]
    fn collector_tracks_nesting() {
        let names = vec!["Outer".to_string(), "Inner".to_string()];
        let mut collector = UiClipCollector::new(&names);
        assert!(collector.enter_node("Outer", Some(&sprite_mat(-1.0, -1.0, 2.0, 2.0))));
        assert_eq!(collector.current(), Some(0));
        assert!(collector.enter_node("Inner", Some(&sprite_mat(0.0, 0.0, 0.5, 0.5))));
        assert_eq!(collector.current(), Some(1));
        collector.exit_node();
        assert_eq!(collector.current(), Some(0));
        collector.exit_node();
        assert_eq!(collector.current(), None);
        let regions = collector.into_regions();
        assert_eq!(regions.len(), 2);
        assert_eq!(regions[0].parent, None);
        assert_eq!(regions[1].parent, Some(0));
        assert_eq!(regions[1].corners[3], [0.5, 0.5, 0.0], "右上の隅");
    }

    /// 入れ子の NDC の矩形は祖先との交差になる。
    #[test]
    fn nested_rect_is_intersection() {
        let regions = vec![
            UiClipRegion { corners: unit_quad_world_corners(&sprite_mat(-0.5, -0.5, 1.0, 1.0)), parent: None },
            UiClipRegion { corners: unit_quad_world_corners(&sprite_mat(0.0, 0.0, 1.0, 1.0)), parent: Some(0) },
        ];
        let rect = clip_ndc_rect(&regions, 1, &IDENTITY).unwrap();
        assert_eq!(rect, NdcRect { min: [0.0, 0.0], max: [0.5, 0.5] });
        assert_eq!(clip_ndc_rect(&regions, 7, &IDENTITY), None, "表の外");
    }

    /// NDC → 画素（y は下向き）。全体は描画先いっぱい、はみ出しは切り詰め、重ならなければ空。
    #[test]
    fn scissor_maps_ndc_to_pixels() {
        let target = [800, 600];
        let full = scissor_px(&NdcRect { min: [-1.0, -1.0], max: [1.0, 1.0] }, target);
        assert_eq!(full, ScissorRect::full(target));
        // 右上の 4 分の 1（NDC の y が上向きなので、画素では上半分）
        let quarter = scissor_px(&NdcRect { min: [0.0, 0.0], max: [1.0, 1.0] }, target);
        assert_eq!(quarter, ScissorRect { x: 400, y: 0, width: 400, height: 300 });
        // 描画先の外へはみ出す分は切り詰める
        let over = scissor_px(&NdcRect { min: [0.5, -3.0], max: [3.0, 0.0] }, target);
        assert_eq!(over, ScissorRect { x: 600, y: 300, width: 200, height: 300 });
        // 重ならない（交差が負の幅）なら空
        let empty = NdcRect { min: [0.5, 0.0], max: [0.2, 0.5] };
        assert!(scissor_px(&empty, target).is_empty());
    }

    /// 端数は外側へ丸める（端の画素を欠かさない）。
    #[test]
    fn scissor_rounds_outward() {
        // 幅 10 px の描画先で x = 2.5〜7.5 px → 2〜8 px
        let rect = NdcRect { min: [-0.5, -1.0], max: [0.5, 1.0] };
        let px = scissor_px(&rect, [10, 10]);
        assert_eq!((px.x, px.width), (2, 6));
    }

    /// 番号が変わるところでだけランが分かれ、同じ番号の連続は 1 本のまま。
    #[test]
    fn runs_split_only_at_clip_changes() {
        let clips = [None, Some(0), Some(0), None, Some(1), Some(1)];
        let pieces = split_run_by_clip(run(UiDrawKind::Sprite, 0, 6), |i| clips[i]);
        assert_eq!(
            pieces,
            vec![
                (run(UiDrawKind::Sprite, 0, 1), None),
                (run(UiDrawKind::Sprite, 1, 3), Some(0)),
                (run(UiDrawKind::Sprite, 3, 4), None),
                (run(UiDrawKind::Sprite, 4, 6), Some(1)),
            ]
        );
        // 切り抜きが無ければ 1 本のまま（従来と同じ描画呼び出しの数）
        let pieces = split_run_by_clip(run(UiDrawKind::Text, 2, 5), |_| None);
        assert_eq!(pieces, vec![(run(UiDrawKind::Text, 2, 5), None)]);
    }
}
