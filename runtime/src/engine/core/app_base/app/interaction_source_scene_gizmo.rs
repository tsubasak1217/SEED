// ============================================================
//  interaction_source_scene_gizmo.rs — インタラクションソースの選択時ギズモ
//
//  選択中のアクターが `InteractionSourceComponent` を持つとき、その
//  「影響範囲」と「今この瞬間に作用しているか」をワイヤーフレームで描画する。
//
//  ## 何を描くか
//    ① 影響半径のワイヤ球（水平リング ＋ 控えめな縦リング 2 枚）
//       … インタラクションフィールド（草の揺れ・水の波紋）へ書き込む範囲であり、
//         轍スタンプが `Circle` 形状のときは痕の広がりそのものでもある。
//    ② 接地スナップの Y 窓（真下へ伸びる線 ＋ 真上の短い線）
//       … 轍スタンプが「どの高さの面まで届くか」。アクタの原点が地面より高い
//         構成で轍が付かない、という不具合の切り分けに直接効く表示である
//         （窓の下端が地面に届いていなければ、絶対に痕は付かない）。
//    ③ `Texture` 形状のときは、スタンプ矩形（進行方向へ回転済み）
//
//  ## 色分け（この機能の要）
//    通常・移動中・轍を押した瞬間で色を変える。
//    「コンポーネントは付いているのに何も起きない」という状況で、
//      ・そもそも動いていない（＝場へ何も書いていない）のか
//      ・動いてはいるが轍だけが押されていないのか
//    を **シーンビューを見るだけで**切り分けられるようにするための表示である。
//    状態は `terrain.cover_stamp_debug`（Play 中のみ更新される観測値）から読む。
//
//  ## Y 窓・矩形の寸法は必ず実装（trample.rs）から引く
//    cover_emitter_scene_gizmo と同じ思想で、ギズモは「実際に効いている範囲」を
//    写す鏡でなければならない。半径から許容差を作る規則も、下方向の探索距離も、
//    `CoverStampSpec` のメソッド／公開定数をそのまま呼んで描く（値を書き写さない）。
//
//  light_scene_gizmo と同じく LineBatch → GpuLineBatch を構築して返す。
//  実際の描画（line パイプラインでの draw）は呼び出し元 frame_renderer が行う。
// ============================================================

use std::collections::HashMap;

use crate::engine::components::interaction_source_component::{
    InteractionSourceComponent, InteractionStampShape,
};
use crate::engine::components::{ComponentKind, Transform};
use crate::engine::ecs::World;
use crate::engine::interaction::source_key;
use crate::engine::methods::drawer::{GpuLineBatch, LineBatch};
use crate::engine::structs::objects::Actor;
use crate::engine::terrain::cover::{
    CoverStampShape, CoverStampSpec, COVER_STAMP_DEFAULT_FORWARD,
};

use super::terrain_cover_ops::{CoverStampDebug, CoverStampTrack};

// ── ギズモ色（マジックナンバー禁止）──────────────────────────
//
// 色相は既存ギズモと衝突させない: ライト = 黄、制御点 = オレンジ、
// カバーエミッタ = 淡い水色／淡い紫。ここは緑〜赤の系統を使う。

/// 通常時（静止している・Play していない）の影響範囲の色。落ち着いた緑。
const IDLE_COLOR: [f32; 4] = [0.35, 0.85, 0.50, 0.90];

/// 移動中（＝草・水面へ速度を書き込んでいる）の色。明るいシアン。
const MOVING_COLOR: [f32; 4] = [0.30, 0.95, 1.00, 0.95];

/// 轍スタンプが実際にカバー場を押した瞬間の色。目を引くオレンジ。
///
/// 「動いている（シアン）のに轍色（オレンジ）にならない」＝
/// 収集は通っているがスタンプ側で落ちている、という切り分けができる。
const STAMPING_COLOR: [f32; 4] = [1.00, 0.55, 0.15, 1.00];

/// 水平リング以外（縦リング・Y 窓の線）に使うアルファ倍率。
///
/// 主役はあくまで接地面の広がり（水平リング）なので、補助線は薄くして
/// 形が読み取りにくくならないようにする。
const SECONDARY_ALPHA_SCALE: f32 = 0.45;

// ── 形状の解像度・寸法（マジックナンバー禁止）────────────────

/// ワイヤ球の 1 リングあたりの分割数。
///
/// 32 分割あれば半径 1m の円が視覚的に滑らかに見え、1 ソースあたりの
/// 線分数も 3 リングで 96 本に収まる（選択中の 1 アクタ分だけなので無視できる）。
const RING_SEGMENTS: usize = 32;

/// 半径・サイズが実質 0 かどうかの判定しきい値（メートル）。
///
/// これ以下は「潰れている」とみなして描かない（線が重なって形にならない）。
const DEGENERATE_EPSILON: f32 = 1.0e-4;

/// Y 窓の下端・上端に描く十字マークの腕の長さ（メートル）。
///
/// 窓の端がどこかを一目で分かるようにするための短い目印。
/// 長くすると影響範囲そのものと誤読されるので、控えめに留める。
const Y_WINDOW_TICK_LEN: f32 = 0.15;

// ── 公開 API ──────────────────────────────────────────────────

/// 選択中アクター（DFS 番号 `selected_dfs`）が `InteractionSourceComponent` を
/// 持つ場合、その影響範囲ギズモの GpuLineBatch を構築して返す。
///
/// バッチが空（InteractionSource なし・非選択・半径 0）の場合は None を返す。
///
/// # 引数
/// - `actors`       : ルートアクターのスライス
/// - `world`        : ECS ワールド（コンポーネント参照に使用）
/// - `wl`           : 対象の世界線番号
/// - `selected_dfs` : 選択中アクターの DFS 番号（未選択なら None）
/// - `debug`        : ソースキー → 作用状況（色分けの根拠。Play 中のみ中身が入る）
/// - `tracks`       : ソースキー → 追跡情報（テクスチャ矩形を回す進行方向）
/// - `device`       : GPU バッファ確保に使うデバイス
#[allow(clippy::too_many_arguments)]
pub fn build_selected_interaction_source_gizmo_batch(
    actors: &[Actor],
    world: &World,
    wl: u32,
    selected_dfs: Option<usize>,
    debug: &HashMap<u64, CoverStampDebug>,
    tracks: &HashMap<u64, CoverStampTrack>,
    device: &wgpu::Device,
) -> Option<GpuLineBatch> {
    let dfs = selected_dfs? as u32;
    let mut lb = LineBatch::new();
    let mut counter = 0u32;
    // ルートの絞り込みと DFS 連番の数え方は `interaction::collect_interaction_sources`
    // と完全に同一にする（ソースキーがずれると色分けが別のソースの状態を指す）。
    for root in actors.iter().filter(|a| a.world_line == wl) {
        if add_gizmo_for_dfs(root, world, dfs, &mut counter, debug, tracks, &mut lb) {
            break;
        }
    }
    if lb.is_empty() {
        None
    } else {
        Some(lb.build(device))
    }
}

/// DFS 走査して対象アクターの全 InteractionSource スロットのギズモを追加する。
///
/// 戻り値は「対象 DFS 番号のアクターを見つけたか」。見つけた時点で走査を打ち切る。
///
/// 【非アクティブなアクタでも連番を進める理由】
///   `collect_in_actor` が「収集対象外のアクタでも必ず加算する」規則なので、
///   ここで数え方を変えるとソースキーが食い違う。
fn add_gizmo_for_dfs(
    actor: &Actor,
    world: &World,
    dfs: u32,
    counter: &mut u32,
    debug: &HashMap<u64, CoverStampDebug>,
    tracks: &HashMap<u64, CoverStampTrack>,
    lb: &mut LineBatch,
) -> bool {
    let dfs_id = *counter;
    *counter += 1;

    if dfs_id == dfs {
        if let Some(tf) = world.get::<Transform>(actor.entity) {
            for (slot_index, slot) in actor.slots().iter().enumerate() {
                // 無効スロットは描かない。収集側も弾いており、そこで何も起きない
                // 範囲を線だけ出すと「効いていないのに効いて見える」ギズモになる。
                if slot.kind != ComponentKind::InteractionSource || !slot.enabled {
                    continue;
                }
                let Some(src) = world.get::<InteractionSourceComponent>(slot.entity) else {
                    continue;
                };
                let key = source_key(dfs_id, slot_index as u32);
                add_one_source_gizmo(
                    lb,
                    tf,
                    src,
                    debug.get(&key).copied().unwrap_or_default(),
                    tracks.get(&key).copied(),
                );
            }
        }
        return true;
    }

    for child in actor.children() {
        if add_gizmo_for_dfs(child, world, dfs, counter, debug, tracks, lb) {
            return true;
        }
    }
    false
}

/// 1 ソース分のギズモを LineBatch に追加する。
///
/// 中心は **アクターのワールド位置のみ**（回転・スケールは使わない）。
/// 収集側（`collect_interaction_sources` / `collect_stamp_in_actor`）が
/// `Transform::position` しか読んでいないため、ここで回転・スケールを掛けると
/// 実際の影響範囲と見た目が食い違う。
fn add_one_source_gizmo(
    lb: &mut LineBatch,
    tf: &Transform,
    src: &InteractionSourceComponent,
    state: CoverStampDebug,
    track: Option<CoverStampTrack>,
) {
    // コンポーネント側の有効フラグ。false の間は場へ一切書き込まないので線も出さない
    // （収集側の `if !src.enabled { continue }` と対応）。
    if !src.enabled {
        return;
    }
    // 半径 0 以下・強さ 0 以下は収集側で落とされる＝何も起きない。
    if !(src.radius > DEGENERATE_EPSILON) || !(src.strength > 0.0) {
        return;
    }

    let center = tf.position;
    let color = state_color(state);
    let sub = with_alpha_scale(color, SECONDARY_ALPHA_SCALE);

    // ─── ① 影響半径のワイヤ球 ───
    //   水平リングを主役（濃い色）に、縦 2 枚を補助（薄い色）にする。
    add_ring(lb, center, src.radius, Axis::Y, color);
    add_ring(lb, center, src.radius, Axis::X, sub);
    add_ring(lb, center, src.radius, Axis::Z, sub);

    // ─── ② 接地スナップの Y 窓 ───
    //   実際に轍を押す判定に使う窓そのものを描く（値は trample.rs から引く）。
    add_ground_snap_window(lb, center, src.radius, sub);

    // ─── ③ Texture 形状のスタンプ矩形 ───
    if src.stamp_shape == InteractionStampShape::Texture {
        // マスク未設定のスタンプは収集側で落ちる（痕が付かない）ので描かない。
        if src.stamp_mask_path.is_empty() {
            return;
        }
        // 進行方向は追跡情報があればそれを使う（Play 中）。無ければ
        // `resolve_forward_xz` と同じ既定（+Z）＝実際に押される向きと一致する。
        let forward = track.map(|t| t.forward).unwrap_or(COVER_STAMP_DEFAULT_FORWARD);
        add_stamp_rect(lb, center, src.stamp_size, forward, color);
    }
}

/// 作用状況から線の色を選ぶ。
///
/// 優先順位は「轍を押した > 移動中 > 通常」。轍が押された瞬間を最優先にするのは、
/// それがもっとも見逃されやすく、かつ確認したい事実だからである。
fn state_color(state: CoverStampDebug) -> [f32; 4] {
    if state.is_stamping() {
        STAMPING_COLOR
    } else if state.is_moving() {
        MOVING_COLOR
    } else {
        IDLE_COLOR
    }
}

/// 色のアルファだけを倍率で薄くする（色相は保つ）。
fn with_alpha_scale(color: [f32; 4], scale: f32) -> [f32; 4] {
    [color[0], color[1], color[2], color[3] * scale]
}

/// リングを張る平面の法線軸。
enum Axis {
    /// 水平リング（XZ 平面）。
    Y,
    /// 縦リング（YZ 平面）。
    X,
    /// 縦リング（XY 平面）。
    Z,
}

/// 中心・半径・法線軸を指定して円のワイヤ（`RING_SEGMENTS` 本の線分）を追加する。
fn add_ring(lb: &mut LineBatch, center: [f32; 3], radius: f32, axis: Axis, color: [f32; 4]) {
    if !(radius > DEGENERATE_EPSILON) {
        return;
    }
    let point_at = |t: f32| -> [f32; 3] {
        let angle = t * std::f32::consts::TAU;
        let (s, c) = angle.sin_cos();
        let (a, b) = (c * radius, s * radius);
        match axis {
            Axis::Y => [center[0] + a, center[1], center[2] + b],
            Axis::X => [center[0], center[1] + a, center[2] + b],
            Axis::Z => [center[0] + a, center[1] + b, center[2]],
        }
    };
    let mut prev = point_at(0.0);
    for i in 1..=RING_SEGMENTS {
        let next = point_at(i as f32 / RING_SEGMENTS as f32);
        lb.add_line(prev, next, color);
        prev = next;
    }
}

/// 接地スナップの Y 窓（上端 / 下端）を縦線と十字マークで描く。
///
/// 窓の寸法は `CoverStampSpec` のメソッドから引く。ここで数式を書き写すと、
/// 実装を直したときにギズモだけが古い範囲を描き続ける（嘘のギズモになる）。
fn add_ground_snap_window(lb: &mut LineBatch, center: [f32; 3], radius: f32, color: [f32; 4]) {
    // 窓の計算に必要なのは半径だけなので、形状はダミーの Circle でよい。
    let probe = CoverStampSpec {
        contact: center,
        radius,
        strength: 1.0,
        shape: CoverStampShape::Circle,
    };
    let top = center[1] + probe.y_tolerance();
    let bottom = center[1] - probe.ground_reach_down();

    // 窓の全長を 1 本の縦線で示す。
    lb.add_line([center[0], bottom, center[2]], [center[0], top, center[2]], color);
    // 上端・下端に十字マークを置いて、窓の端であることを明示する。
    for y in [top, bottom] {
        lb.add_line(
            [center[0] - Y_WINDOW_TICK_LEN, y, center[2]],
            [center[0] + Y_WINDOW_TICK_LEN, y, center[2]],
            color,
        );
        lb.add_line(
            [center[0], y, center[2] - Y_WINDOW_TICK_LEN],
            [center[0], y, center[2] + Y_WINDOW_TICK_LEN],
            color,
        );
    }
}

/// `Texture` 形状のスタンプ矩形（進行方向へ回転済み）を追加する。
///
/// `stamp_size` は `[進行方向に直交する幅, 進行方向の長さ]` の **全サイズ**（m）。
/// `CoverStampSpec::footprint_at` が局所座標を `/ size + 0.5` で UV 化している＝
/// 中心 ± size/2 が矩形の範囲なので、半サイズで四隅を作る。
///
/// 矩形の Y はアクターのワールド位置に置く。実際に押される面はそこから
/// 真下（接地スナップの窓の内側）にあるが、矩形の向きと大きさを示すのが目的なので
/// ソース位置に置くほうが「どのアクタのものか」が読み取りやすい。
fn add_stamp_rect(
    lb: &mut LineBatch,
    center: [f32; 3],
    size: [f32; 2],
    forward_xz: [f32; 2],
    color: [f32; 4],
) {
    let (half_w, half_l) = (size[0] * 0.5, size[1] * 0.5);
    if half_w <= DEGENERATE_EPSILON || half_l <= DEGENERATE_EPSILON {
        return;
    }
    // 進行方向を正規化し、その右手側を作る（`footprint_at` と同じ規則:
    // 前方 f=(fx,fz) に対する右手は (fz, -fx)）。
    let (fx, fz) = (forward_xz[0], forward_xz[1]);
    let len = (fx * fx + fz * fz).sqrt();
    if !(len > DEGENERATE_EPSILON) {
        return;
    }
    let (fx, fz) = (fx / len, fz / len);
    let (rx, rz) = (fz, -fx);

    // 四隅（右後 → 右前 → 左前 → 左後 の順で一周する）。
    let corner = |right: f32, forward: f32| -> [f32; 3] {
        [
            center[0] + rx * right + fx * forward,
            center[1],
            center[2] + rz * right + fz * forward,
        ]
    };
    let corners = [
        corner(half_w, -half_l),
        corner(half_w, half_l),
        corner(-half_w, half_l),
        corner(-half_w, -half_l),
    ];
    for i in 0..corners.len() {
        lb.add_line(corners[i], corners[(i + 1) % corners.len()], color);
    }
    // 進行方向が分かるよう、前辺の中点から短い矢を出す。
    let nose = corner(0.0, half_l);
    let tip = corner(0.0, half_l + half_l * 0.5);
    lb.add_line(nose, tip, color);
}

// ── ユニットテスト ────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 指定コンポーネント 1 個ぶんのギズモを積んだ LineBatch が空かどうか。
    ///
    /// `LineBatch` は頂点列を公開していないため、テストで確かめられるのは
    /// 「描いたか／描かなかったか」まで。寸法・色を決める純粋関数
    /// （`state_color` / `with_alpha_scale`）は直接検証する。
    fn draws_something(src: &InteractionSourceComponent) -> bool {
        let mut lb = LineBatch::new();
        add_one_source_gizmo(
            &mut lb,
            &Transform::identity(),
            src,
            CoverStampDebug::default(),
            None,
        );
        !lb.is_empty()
    }

    /// 既定のソース（半径 1m・有効）は必ず範囲を描くこと。
    #[test]
    fn enabled_source_draws_range() {
        assert!(draws_something(&InteractionSourceComponent::default()));
    }

    /// コンポーネント側 `enabled == false` は何も描かないこと（場へ書き込まないため）。
    #[test]
    fn disabled_component_draws_nothing() {
        let src = InteractionSourceComponent {
            enabled: false,
            ..Default::default()
        };
        assert!(!draws_something(&src));
    }

    /// 半径 0 / 強さ 0 のソースは何も描かないこと（収集側で落ちる＝何も起きない）。
    #[test]
    fn degenerate_source_draws_nothing() {
        let zero_radius = InteractionSourceComponent {
            radius: 0.0,
            ..Default::default()
        };
        assert!(!draws_something(&zero_radius));

        let zero_strength = InteractionSourceComponent {
            strength: 0.0,
            ..Default::default()
        };
        assert!(!draws_something(&zero_strength));
    }

    /// 色分けの優先順位が「轍を押した > 移動中 > 通常」であること。
    #[test]
    fn state_color_prefers_stamping_over_moving() {
        let idle = CoverStampDebug::default();
        assert_eq!(state_color(idle), IDLE_COLOR);

        let moving = CoverStampDebug { stamped_hold: 0.0, moving_hold: 0.1 };
        assert_eq!(state_color(moving), MOVING_COLOR);

        // 移動中かつ轍も押している場合は轍の色が勝つ。
        let stamping = CoverStampDebug { stamped_hold: 0.1, moving_hold: 0.1 };
        assert_eq!(state_color(stamping), STAMPING_COLOR);
    }

    /// アルファ倍率は色相を変えずアルファだけを薄くすること。
    #[test]
    fn alpha_scale_keeps_rgb() {
        let c = with_alpha_scale([0.1, 0.2, 0.3, 0.8], 0.5);
        assert_eq!([c[0], c[1], c[2]], [0.1, 0.2, 0.3]);
        assert!((c[3] - 0.4).abs() < 1.0e-6);
    }

    /// Y 窓は「上は許容差ぶん・下は許容差 + 接地スナップ距離ぶん」＝
    /// 実装（`CoverStampSpec`）と厳密に同じ非対称な窓であること。
    ///
    /// ここが実装とずれると、ギズモが嘘の範囲を描いて切り分けの役に立たなくなる。
    #[test]
    fn ground_snap_window_matches_implementation() {
        let radius = 0.4;
        let probe = CoverStampSpec {
            contact: [0.0; 3],
            radius,
            strength: 1.0,
            shape: CoverStampShape::Circle,
        };
        assert!(
            probe.ground_reach_down() > probe.y_tolerance(),
            "窓は下方向のほうが広い（接地スナップ）"
        );
        // 窓の内側／外側の面が、実装の判定と一致すること。
        assert!(probe.matches_surface_y(-probe.ground_reach_down() * 0.99));
        assert!(!probe.matches_surface_y(-probe.ground_reach_down() * 1.01));
        assert!(!probe.matches_surface_y(probe.y_tolerance() * 1.01));
    }

    /// テクスチャ形状はマスク未設定なら矩形を描かないこと（痕が付かないため）。
    ///
    /// ただし影響半径（草・波紋）は形状に依らず効くので、範囲そのものは描かれる。
    #[test]
    fn texture_without_mask_still_draws_radius_but_no_rect() {
        let src = InteractionSourceComponent {
            stamp_shape: InteractionStampShape::Texture,
            stamp_mask_path: String::new(),
            ..Default::default()
        };
        let mut lb_no_mask = LineBatch::new();
        add_one_source_gizmo(
            &mut lb_no_mask, &Transform::identity(), &src,
            CoverStampDebug::default(), None,
        );
        assert!(!lb_no_mask.is_empty(), "影響半径は形状に依らず描く");

        let with_mask = InteractionSourceComponent {
            stamp_mask_path: "assets://tex/boot.png".to_string(),
            ..src
        };
        let mut lb_mask = LineBatch::new();
        add_one_source_gizmo(
            &mut lb_mask, &Transform::identity(), &with_mask,
            CoverStampDebug::default(), None,
        );
        // マスクを設定すると、矩形（4 辺）＋ 進行方向の矢（1 本）ぶん線が増える。
        assert_eq!(
            lb_mask.line_count(),
            lb_no_mask.line_count() + 5,
            "マスクが設定されていればスタンプ矩形（4 辺）と進行方向の矢が増える"
        );
    }

    /// 影響半径のワイヤ球と Y 窓の線分数が、定数から決まる本数どおりであること。
    ///
    /// 見た目の作りを変えたときに、意図しない線の増減へ気付くための固定。
    #[test]
    fn range_gizmo_line_count_is_deterministic() {
        let mut lb = LineBatch::new();
        add_one_source_gizmo(
            &mut lb,
            &Transform::identity(),
            &InteractionSourceComponent::default(),
            CoverStampDebug::default(),
            None,
        );
        // リング 3 枚（各 RING_SEGMENTS 本）＋ Y 窓の縦線 1 本 ＋ 上下端の十字 4 本。
        assert_eq!(lb.line_count(), RING_SEGMENTS * 3 + 1 + 4);
    }
}
