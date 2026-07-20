// ============================================================
//  terrain/paint.rs — 球ブラシによるレイヤペイント（スプラット場に作用）
//
//  【責務】
//    球状ブラシで「手ペイントレイヤ重み」を編集する純粋なアルゴリズム。
//    密度ブラシ（brush.rs）と同じく、単一チャンクではなく
//    「グローバルスプラット場」(PaintField トレイト) に対して作用するため、
//    チャンク境界をまたいでも継ぎ目のレイヤが破綻しない。
//    このファイルはチャンクの格納方式を一切知らない（純粋・境界安全）。
//
//  【ペイントの意味論】
//    各サンプルは (paint_weights, paint_amount) の 2 値を持つ。
//      - paint_weights : 手で塗ったレイヤ重み（総和 1 に正規化）
//      - paint_amount  : どれだけ手描きを優先するか（0=ルール任せ、1=完全手描き）
//    最終的な描画用重みは layers::blend_rule_and_paint で
//      lerp(ルール自動重み, paint_weights, paint_amount)
//    として合成される。ブラシは両者を同じ減衰係数で押し上げる:
//      paint_weights[layer] += delta → 正規化（他レイヤは相対的に減衰）
//      paint_amount        += delta → clamp(0,1)
//    これにより「塗るほどルールから離れて、その層一色へ寄っていく」挙動になる。
// ============================================================

use std::collections::HashSet;

use super::brush::{falloff, SphereBrush};
use super::chunk_coord::ChunkCoord;
use super::layers::{normalize_weights, LayerWeights, TERRAIN_LAYER_COUNT};
use super::settings::TerrainSettings;

/// 実効ペイント量がこの値以下のサンプルは書き込みをスキップする。
/// 球の縁で 0 に漸近する減衰の裾を切り、無意味な dirty 化を防ぐ。
const PAINT_MIN_AMOUNT: f32 = 1.0e-4;

/// エンジン層が実装する「グローバルスプラット場」インターフェース。
///
/// brush.rs の `SampleField` と対になる、スプラット（レイヤ重み）用の口。
/// 密度と同じグリッド・同じ境界重複規約に従う。
pub trait PaintField {
    /// 地形設定への参照。
    fn settings(&self) -> &TerrainSettings;
    /// グローバルサンプル整数座標の (手ペイント重み, ペイント量) を読む。
    /// 未初期化・範囲外は ([0;L], 0.0) を返すこと。
    fn read_paint_global(&self, gx: i32, gy: i32, gz: i32) -> (LayerWeights, f32);
    /// グローバルサンプル整数座標へ (手ペイント重み, ペイント量) を書く。
    /// 境界で重複する全チャンクに同一値を書き込むこと（同期）。
    fn write_paint_global(&mut self, gx: i32, gy: i32, gz: i32, w: LayerWeights, amount: f32);
    /// グローバルサンプル整数座標のワールド空間位置（メートル）。
    fn world_of_global(&self, gx: i32, gy: i32, gz: i32) -> [f32; 3];
}

/// 指定軸のグローバルサンプル `g` を所有するチャンクインデックス集合を返す。
///
/// 境界サンプル（g が chunk_cells の倍数）は隣り合う 2 チャンクが共有する。
/// brush.rs の同名ロジックと同一規約（重複所有）。
fn owning_chunks_on_axis(g: i32, chunk_cells: i32, out: &mut Vec<i32>) {
    out.clear();
    let primary = g.div_euclid(chunk_cells);
    out.push(primary);
    if g.rem_euclid(chunk_cells) == 0 {
        out.push(primary - 1);
    }
}

/// 球ブラシで指定レイヤをペイントし、変化したサンプルを所有するチャンク集合を返す。
///
/// - `layer_index`: 塗るレイヤ番号（0..TERRAIN_LAYER_COUNT）。範囲外は何もしない。
/// - `dt`:          フレーム時間相当（strength と掛けて 1 回の押し上げ量になる）。
///
/// 手順は brush::apply と同じ 2 フェーズ（計算 → 一括書き込み）。
/// スプラットは隣接読みを伴わないため厳密には 1 フェーズで足りるが、
/// 密度ブラシと同じ構造にしておくことで将来の平滑化ペイント追加に備える。
pub fn apply_paint(
    field: &mut impl PaintField,
    brush: &SphereBrush,
    layer_index: usize,
    dt: f32,
) -> Vec<ChunkCoord> {
    // ─── レイヤ番号の検証（不正なら無操作）───
    if layer_index >= TERRAIN_LAYER_COUNT {
        return Vec::new();
    }

    // ─── 必要なスカラを先にコピー（settings の借用を長く保持しない）───
    let (voxel, chunk_cells) = {
        let s = field.settings();
        (s.voxel_size, s.chunk_cells as i32)
    };

    // ─── 球を覆うグローバルサンプル AABB（world = g * voxel_size より g = world/voxel）───
    let inv_voxel = 1.0 / voxel;
    let min_g = |axis: usize| ((brush.center[axis] - brush.radius) * inv_voxel).floor() as i32;
    let max_g = |axis: usize| ((brush.center[axis] + brush.radius) * inv_voxel).ceil() as i32;
    let (gx0, gx1) = (min_g(0), max_g(0));
    let (gy0, gy1) = (min_g(1), max_g(1));
    let (gz0, gz1) = (min_g(2), max_g(2));
    let r2 = brush.radius * brush.radius;

    // ─── フェーズ 1：新スプラット値を計算する ───
    let mut writes: Vec<([i32; 3], LayerWeights, f32)> = Vec::new();
    for gz in gz0..=gz1 {
        for gy in gy0..=gy1 {
            for gx in gx0..=gx1 {
                // サンプルのワールド位置と中心からの距離²。
                let wp = field.world_of_global(gx, gy, gz);
                let dx = wp[0] - brush.center[0];
                let dy = wp[1] - brush.center[1];
                let dz = wp[2] - brush.center[2];
                let dist2 = dx * dx + dy * dy + dz * dz;
                if dist2 > r2 {
                    // 球の外 → スキップ。
                    continue;
                }
                // 密度ブラシと同じ減衰カーブ（中心 1 → 半径 0）。
                let f = falloff(dist2.sqrt(), brush.radius);
                let delta = brush.strength * f * dt;
                if delta <= PAINT_MIN_AMOUNT {
                    continue;
                }

                // ── 対象レイヤの重みを押し上げ、正規化で他レイヤを減衰させる ──
                let (mut w, amount) = field.read_paint_global(gx, gy, gz);
                w[layer_index] = (w[layer_index] + delta).min(1.0);
                normalize_weights(&mut w);
                // ── ペイント量も同じ減衰で 1 へ寄せる（ルールからの離脱度）──
                let new_amount = (amount + delta).clamp(0.0, 1.0);

                writes.push(([gx, gy, gz], w, new_amount));
            }
        }
    }

    // ─── フェーズ 2：まとめて書き込み、触れたチャンクを集計する ───
    let mut touched: HashSet<ChunkCoord> = HashSet::new();
    let mut owners_x = Vec::new();
    let mut owners_y = Vec::new();
    let mut owners_z = Vec::new();
    for ([gx, gy, gz], w, amount) in writes {
        field.write_paint_global(gx, gy, gz, w, amount);
        owning_chunks_on_axis(gx, chunk_cells, &mut owners_x);
        owning_chunks_on_axis(gy, chunk_cells, &mut owners_y);
        owning_chunks_on_axis(gz, chunk_cells, &mut owners_z);
        for &cx in &owners_x {
            for &cy in &owners_y {
                for &cz in &owners_z {
                    touched.insert(ChunkCoord::new(cx, cy, cz));
                }
            }
        }
    }
    touched.into_iter().collect()
}
