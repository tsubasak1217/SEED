// ============================================================
//  cover_emitter_scene_gizmo.rs — カバーエミッタの選択時ギズモ
//
//  選択中のアクターが `CoverEmitterComponent` を持つとき、その「降る範囲」を
//  ワイヤーフレームでシーンへ描画する。範囲種別ごとに描くものが変わる。
//    - Global      : 何も描かない（範囲がワールド全域＝形を持たないため）
//    - Region      : 軸平行の直方体ワイヤ（＋境界フェード幅を示す内側の二重枠）
//    - TextureMask : XZ 平面の矩形枠（＋投影の断面であることを示す縦の目印線）
//
//  light_scene_gizmo と同じく LineBatch → GpuLineBatch を構築して返す。
//  実際の描画（line パイプラインでの draw）は呼び出し元 frame_renderer が行う。
//
//  ## なぜ「軸平行」で描くのか（嘘のギズモを出さないための重要な前提）
//  積算側 `terrain_cover_ops.rs::collect_cover_emitters` は、エミッタの範囲を
//    `CoverEmitRange::Region { center: r.world_pos, half_extents: r.extents, fade }`
//  として組み立てており、**アクターの回転もスケールも一切適用していない**
//  （`RawCoverEmitter` が持つのは `Transform::position` のみ）。
//  さらに `CoverEmitSpec::coverage_at` はワールド座標を軸ごとに
//  `(world[axis] - center[axis]).abs()` と比較する＝完全な軸平行判定である。
//  したがってギズモ側で回転・スケールを掛けると、**実際に降る範囲と見た目が食い違う**。
//  ギズモは「実際に効いている範囲」を写す鏡でなければならないので、ここでは
//  アクターのワールド位置のみを使い、軸平行 AABB として描く。
// ============================================================

use crate::engine::components::{
    ComponentKind, CoverEmitterComponent, CoverEmitterRangeKind, Transform,
};
use crate::engine::ecs::World;
use crate::engine::methods::drawer::{GpuLineBatch, LineBatch};
use crate::engine::structs::objects::Actor;
use crate::engine::structs::primitives::Aabb;
use crate::engine::structs::tensor::Vector3;

// ── ギズモ色・寸法定数（マジックナンバー禁止）────────────────

/// Region の外枠（＝実際の範囲境界）の色。
///
/// 雪・落ち葉といった「降り積もるもの」の範囲なので、ライト（黄）・
/// 制御点（オレンジ）・スカイボックスと色相で衝突しない淡い水色にする。
const REGION_OUTER_COLOR: [f32; 4] = [0.55, 0.85, 1.0, 0.95];

/// Region の内側フェード枠（`extents - fade` の位置）の色。
///
/// 外枠と同じ色相のままアルファだけ落とす。二重枠の内側が「ここから外へ向かって
/// 被覆が 1→0 に落ちていく」境目であることを、色の濃さの差で示す。
const REGION_FADE_COLOR: [f32; 4] = [0.55, 0.85, 1.0, 0.40];

/// TextureMask の矩形枠の色（マスク投影であることを色相で区別する淡い紫）。
const MASK_RECT_COLOR: [f32; 4] = [0.80, 0.65, 1.0, 0.95];

/// TextureMask の枠の四隅から上下へ伸ばす目印線の色（枠より薄く）。
const MASK_TICK_COLOR: [f32; 4] = [0.80, 0.65, 1.0, 0.45];

/// TextureMask の四隅の目印線を上下へ伸ばす長さ（メートル）。
///
/// マスクは Y 方向に制限を持たない（真上から投影する）ため、矩形枠は
/// 「無限に高い柱の断面」でしかない。それが伝わる程度の短い線に留める
/// （長くすると範囲が Y 方向に限定されているという逆の誤解を生む）。
const MASK_TICK_LEN: f32 = 1.5;

/// 半径・サイズが実質 0 かどうかの判定しきい値（メートル）。
///
/// これ以下の軸は「潰れている」とみなし、内側フェード枠を描かない。
/// 潰れた枠は線が重なって二重枠に見えず、フェード幅を示すという役目を果たさない。
const DEGENERATE_EPSILON: f32 = 1.0e-4;

// ── 公開 API ──────────────────────────────────────────────────

/// 選択中アクター（DFS 番号 `selected_dfs`）が `CoverEmitterComponent` を持つ場合、
/// その範囲種別に応じたギズモの GpuLineBatch を構築して返す。
///
/// バッチが空（CoverEmitter なし・非選択・Global のみ）の場合は None を返す。
///
/// # 引数
/// - `actors`       : ルートアクターのスライス
/// - `world`        : ECS ワールド（コンポーネント参照に使用）
/// - `wl`           : 対象の世界線番号
/// - `selected_dfs` : 選択中アクターの DFS 番号（未選択なら None）
/// - `device`       : GPU バッファ確保に使うデバイス
pub fn build_selected_cover_emitter_gizmo_batch(
    actors: &[Actor],
    world: &World,
    wl: u32,
    selected_dfs: Option<usize>,
    device: &wgpu::Device,
) -> Option<GpuLineBatch> {
    let dfs = selected_dfs? as u32;
    let mut lb = LineBatch::new();
    let mut counter = 0u32;
    add_cover_emitter_gizmo_for_dfs(actors, world, wl, dfs, &mut counter, &mut lb);
    if lb.is_empty() {
        None
    } else {
        Some(lb.build(device))
    }
}

/// DFS 走査して対象アクターの全 CoverEmitter スロットのギズモを追加する。
///
/// 戻り値は「対象 DFS 番号のアクターを見つけたか」。見つけた時点で走査を打ち切る。
fn add_cover_emitter_gizmo_for_dfs(
    actors: &[Actor],
    world: &World,
    wl: u32,
    dfs: u32,
    counter: &mut u32,
    lb: &mut LineBatch,
) -> bool {
    for actor in actors {
        if actor.world_line != wl {
            continue;
        }
        let current = *counter;
        *counter += 1;

        if current == dfs {
            if let Some(tf) = world.get::<Transform>(actor.entity) {
                for slot in actor.slots() {
                    // スロットが無効なら描かない。積算側 `collect_cover_in_actor` も
                    // `!slot.enabled` を弾いており、そこで降らない範囲を線だけ出すと
                    // 「効いていないのに効いて見える」ギズモになるため。
                    if slot.kind != ComponentKind::CoverEmitter || !slot.enabled {
                        continue;
                    }
                    if let Some(ce) = world.get::<CoverEmitterComponent>(slot.entity) {
                        add_one_cover_emitter_gizmo(lb, tf, ce);
                    }
                }
            }
            return true;
        }

        if add_cover_emitter_gizmo_for_dfs(actor.children(), world, wl, dfs, counter, lb) {
            return true;
        }
    }
    false
}

/// 1 エミッタ分のギズモを LineBatch に追加する。
///
/// 中心は **アクターのワールド位置のみ**（回転・スケールは使わない）。理由は
/// ファイル冒頭のコメントを参照（積算側が位置しか見ていないため）。
fn add_one_cover_emitter_gizmo(lb: &mut LineBatch, tf: &Transform, ce: &CoverEmitterComponent) {
    // コンポーネント側の有効フラグ。false の間は一切降らないので線も出さない
    // （積算側 `collect_cover_in_actor` の `if !e.enabled { continue }` と対応）。
    if !ce.enabled {
        return;
    }

    let center = tf.position;

    match ce.range_kind {
        // ワールド全域。中心も大きさも意味を持たないので描く形が無い。
        // 「線が出ない」こと自体が「範囲が無限である」ことの表示になる。
        CoverEmitterRangeKind::Global => {}

        CoverEmitterRangeKind::Region => {
            // 外枠 = extents（各軸の半径）そのもの。
            add_axis_aligned_box(lb, center, ce.extents, REGION_OUTER_COLOR);

            // 内側フェード枠（描ける場合のみ）。二重枠の間隔がフェード幅を表す。
            if let Some(inner) = region_fade_inner_half(ce.extents, ce.fade) {
                add_axis_aligned_box(lb, center, inner, REGION_FADE_COLOR);
            }
        }

        CoverEmitterRangeKind::TextureMask => {
            // マスク画像が未設定のエミッタは積算側で弾かれる
            // （`CoverMask::empty()` → `is_valid()` が false → `continue`）ため、
            // 何も降らない。降らないものの範囲を描くと嘘になるので、ここでも描かない。
            if ce.mask_path.is_empty() {
                return;
            }
            let Some((half_x, half_z)) = mask_half_size(ce.mask_size) else {
                return;
            };
            add_mask_rect(lb, center, half_x, half_z);
        }
    }
}

/// Region の内側フェード枠の各軸半径を返す（描かない場合は None）。
///
/// `coverage_at` は境界から内側へ `fade` 分だけ被覆を 0→1 に立ち上げるので、
/// `extents - fade` の内枠より内側が「満額で降る領域」になる。
///
/// `fade <= 0`（硬い境界）と、1 軸でも潰れる（フェード幅が半径以上）場合は None。
/// 潰れた枠は外枠と重なって二重枠に見えず、フェード幅を示すという役目を果たさない。
fn region_fade_inner_half(extents: [f32; 3], fade: f32) -> Option<[f32; 3]> {
    if !(fade > 0.0) {
        return None;
    }
    let inner = [extents[0] - fade, extents[1] - fade, extents[2] - fade];
    if inner.iter().any(|&h| h <= DEGENERATE_EPSILON) {
        return None;
    }
    Some(inner)
}

/// TextureMask 矩形の (X 半サイズ, Z 半サイズ) を返す（描かない場合は None）。
///
/// `mask_size` は **全サイズ（m）** であって半径ではない。
/// `coverage_at` が `(world.x - center.x) / sx + 0.5` で UV 化している＝
/// 中心 ± size/2 が矩形の範囲。ここを半径として扱うと 2 倍の枠になる。
fn mask_half_size(mask_size: [f32; 2]) -> Option<(f32, f32)> {
    let half_x = mask_size[0] * 0.5;
    let half_z = mask_size[1] * 0.5;
    if half_x <= DEGENERATE_EPSILON || half_z <= DEGENERATE_EPSILON {
        return None;
    }
    Some((half_x, half_z))
}

/// 中心と各軸半径から軸平行の直方体ワイヤ（12 辺）を追加する。
///
/// `LineBatch::add_aabb` が軸平行専用の 12 辺ワイヤを既に持っているため、
/// 自前で頂点を組まずそちらへ委譲する（今回の範囲は前述の理由で常に軸平行）。
fn add_axis_aligned_box(lb: &mut LineBatch, center: [f32; 3], half: [f32; 3], color: [f32; 4]) {
    let aabb = Aabb::from_center_half_size(
        Vector3::new(center[0], center[1], center[2]),
        Vector3::new(half[0], half[1], half[2]),
    );
    lb.add_aabb(&aabb, color);
}

/// TextureMask の XZ 矩形枠（4 辺）と、四隅の縦目印線を追加する。
///
/// 矩形の Y はアクターのワールド位置の Y に固定する（マスクは Y 方向に制限を
/// 持たないため、この 1 枚は「真上からの投影の断面」を示す目安でしかない）。
/// 四隅から上下へ短い線を伸ばすことで、その断面性を見た目に含める。
fn add_mask_rect(lb: &mut LineBatch, center: [f32; 3], half_x: f32, half_z: f32) {
    let y = center[1];
    // 四隅（-X-Z → +X-Z → +X+Z → -X+Z の順で一周する）。
    let corners = [
        [center[0] - half_x, y, center[2] - half_z],
        [center[0] + half_x, y, center[2] - half_z],
        [center[0] + half_x, y, center[2] + half_z],
        [center[0] - half_x, y, center[2] + half_z],
    ];
    // 矩形の 4 辺。
    for i in 0..corners.len() {
        let next = (i + 1) % corners.len();
        lb.add_line(corners[i], corners[next], MASK_RECT_COLOR);
    }
    // 四隅の縦目印線（上下へ MASK_TICK_LEN ずつ）。
    for c in &corners {
        lb.add_line(
            [c[0], c[1] - MASK_TICK_LEN, c[2]],
            [c[0], c[1] + MASK_TICK_LEN, c[2]],
            MASK_TICK_COLOR,
        );
    }
}

// ── ユニットテスト ────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 指定コンポーネント 1 個ぶんのギズモを積んだ LineBatch が空かどうか。
    ///
    /// `LineBatch` は頂点列を公開していないため、テストで確かめられるのは
    /// 「描いたか／描かなかったか」まで。寸法の正しさは寸法を決める純粋関数
    /// （`region_fade_inner_half` / `mask_half_size`）側で直接検証する。
    fn draws_something(ce: &CoverEmitterComponent, tf: &Transform) -> bool {
        let mut lb = LineBatch::new();
        add_one_cover_emitter_gizmo(&mut lb, tf, ce);
        !lb.is_empty()
    }

    /// Global は範囲が無限なので何も描かないこと。
    #[test]
    fn global_draws_nothing() {
        let ce = CoverEmitterComponent {
            range_kind: CoverEmitterRangeKind::Global,
            ..Default::default()
        };
        assert!(!draws_something(&ce, &Transform::identity()));
    }

    /// 無効なコンポーネント（`enabled == false`）は何も描かないこと。
    #[test]
    fn disabled_component_draws_nothing() {
        let ce = CoverEmitterComponent {
            range_kind: CoverEmitterRangeKind::Region,
            enabled: false,
            ..Default::default()
        };
        assert!(!draws_something(&ce, &Transform::identity()));
    }

    /// 有効な Region は（回転・スケールが付いていても）必ず枠を描くこと。
    #[test]
    fn enabled_region_draws_box() {
        let ce = CoverEmitterComponent {
            range_kind: CoverEmitterRangeKind::Region,
            ..Default::default()
        };
        let rotated_scaled = Transform {
            position: [1.0, 2.0, 3.0],
            rotation: [0.5, 0.5, 0.5],
            scale: [2.0, 3.0, 4.0],
        };
        assert!(draws_something(&ce, &rotated_scaled));
    }

    /// TextureMask はマスク未設定なら何も描かないこと（積算側でも降らないため）。
    #[test]
    fn texture_mask_without_path_draws_nothing() {
        let ce = CoverEmitterComponent {
            range_kind: CoverEmitterRangeKind::TextureMask,
            mask_path: String::new(),
            ..Default::default()
        };
        assert!(!draws_something(&ce, &Transform::identity()));
    }

    /// TextureMask はマスクが設定されていれば枠を描くこと。
    #[test]
    fn texture_mask_with_path_draws_rect() {
        let ce = CoverEmitterComponent {
            range_kind: CoverEmitterRangeKind::TextureMask,
            mask_path: "assets://masks/snow.png".to_string(),
            mask_size: [32.0, 32.0],
            ..Default::default()
        };
        assert!(draws_something(&ce, &Transform::identity()));
    }

    /// 内側フェード枠は `extents - fade` になること。
    #[test]
    fn fade_inner_half_is_extents_minus_fade() {
        assert_eq!(
            region_fade_inner_half([8.0, 8.0, 8.0], 1.0),
            Some([7.0, 7.0, 7.0])
        );
    }

    /// fade == 0（硬い境界）では内側フェード枠を描かないこと。
    #[test]
    fn fade_inner_half_is_none_without_fade() {
        assert_eq!(region_fade_inner_half([8.0, 8.0, 8.0], 0.0), None);
    }

    /// 1 軸でも潰れる（フェード幅が半径以上）なら内側フェード枠を描かないこと。
    #[test]
    fn fade_inner_half_is_none_when_any_axis_collapses() {
        // Y 軸だけが潰れるケース。
        assert_eq!(region_fade_inner_half([8.0, 2.0, 8.0], 2.0), None);
        // 全軸が潰れるケース。
        assert_eq!(region_fade_inner_half([1.0, 1.0, 1.0], 5.0), None);
    }

    /// **`mask_size` は半径ではなく全サイズ**であること
    /// （`coverage_at` の `/ sx + 0.5` と一致する半サイズ = size * 0.5）。
    #[test]
    fn mask_half_size_is_half_of_mask_size() {
        assert_eq!(mask_half_size([32.0, 16.0]), Some((16.0, 8.0)));
    }

    /// サイズ 0（実質潰れている）のマスク矩形は描かないこと。
    #[test]
    fn mask_half_size_is_none_when_degenerate() {
        assert_eq!(mask_half_size([0.0, 32.0]), None);
        assert_eq!(mask_half_size([32.0, 0.0]), None);
    }
}
