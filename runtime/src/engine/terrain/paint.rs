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
//    各サンプルは (paint_slots, paint_amount) の 2 値を持つ。
//      - paint_slots  : 手で塗った「レイヤ番号＋重み」を最大 4 スロット（総和 1）
//      - paint_amount : どれだけ手描きを優先するか（0=ルール任せ、1=完全手描き）
//    最終的な描画用重みは layers::blend_rule_and_paint_all で
//      lerp(ルール自動重み, paint_slots を展開した重み, paint_amount)
//    として合成される。ブラシは両者を同じ減衰係数で押し上げる:
//      対象レイヤのスロット重み += delta → 正規化（他スロットは相対的に減衰）
//      paint_amount            += delta → clamp(0,1)
//    これにより「塗るほどルールから離れて、その層一色へ寄っていく」挙動になる。
//
//  【スロットが埋まっているとき】
//    対象レイヤが既存スロットに無く、4 スロットすべてが埋まっている場合は
//    **最小重みスロットを置換**する（そのサンプルで最も影響の小さい層を捨てる）。
//    1 サンプルが同時に持てる層は 4 までという設計上の制約に由来する。
// ============================================================

use std::collections::HashSet;

use super::brush::{SphereBrush, falloff};
use super::chunk_coord::ChunkCoord;
use super::layers::{BlendSlots, TERRAIN_BLEND_SLOTS, TERRAIN_MAX_LAYERS};
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
    /// グローバルサンプル整数座標の (手ペイントスロット, ペイント量) を読む。
    /// 未初期化・範囲外は (重み全 0 の BlendSlots, 0.0) を返すこと。
    fn read_paint_global(&self, gx: i32, gy: i32, gz: i32) -> (BlendSlots, f32);
    /// グローバルサンプル整数座標へ (手ペイントスロット, ペイント量) を書く。
    /// 境界で重複する全チャンクに同一値を書き込むこと（同期）。
    fn write_paint_global(&mut self, gx: i32, gy: i32, gz: i32, slots: &BlendSlots, amount: f32);
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

/// スロットへ delta を加算する（対象レイヤが無ければ最小重みスロットを置換）。
///
/// 戻り値は正規化済みの新しいスロット。
///
/// 【置換規則】
///   対象レイヤが既存スロットにあれば単にその重みを押し上げる。
///   無い場合は最も重みの小さいスロット（＝そのサンプルで最も影響の薄い層）を
///   対象レイヤで置き換え、重みを delta にする。未使用スロット（重み 0）が
///   あれば必然的にそれが最小になるので、同じ 1 本の規則で両ケースを扱える。
fn add_layer_weight(slots: &BlendSlots, layer_index: u32, delta: f32) -> BlendSlots {
    let mut out = *slots;

    // ─── 既存スロットを探しつつ、最小重みスロットも記録する ───
    let mut found: Option<usize> = None;
    let mut min_slot = 0usize;
    for k in 0..TERRAIN_BLEND_SLOTS {
        if out.index[k] == layer_index && out.weight[k] > 0.0 {
            found = Some(k);
            break;
        }
        if out.weight[k] < out.weight[min_slot] {
            min_slot = k;
        }
    }

    match found {
        // 既にこの層を塗っている → 重みを押し上げる。
        Some(k) => out.weight[k] = (out.weight[k] + delta).min(1.0),
        // 新しい層 → 最小重みスロットを置き換える（最も影響の薄い層を捨てる）。
        None => {
            out.index[min_slot] = layer_index;
            out.weight[min_slot] = delta.min(1.0);
        }
    }

    // ─── 総和 1 へ戻す（他スロットが相対的に減衰する）───
    //   select_top_slots は正規化＋降順ソート＋縮退補正を一括で行う。
    //   4 スロットしか無いので上位 4 選択は恒等変換になる。
    let dense_len = (out.index.iter().copied().max().unwrap_or(0) as usize) + 1;
    let dense = super::layers::expand_slots(&out, dense_len);
    super::layers::select_top_slots(&dense)
}

/// 球ブラシで指定レイヤをペイントし、変化したサンプルを所有するチャンク集合を返す。
///
/// - `layer_index`: 塗るレイヤ番号（0..TERRAIN_MAX_LAYERS）。範囲外は何もしない。
/// - `dt`:          フレーム時間相当（strength と掛けて 1 回の押し上げ量になる）。
///
/// 手順は brush::apply と同じ 2 フェーズ（計算 → 一括書き込み）。
/// スプラットは隣接読みを伴わないため厳密には 1 フェーズで足りるが、
/// 密度ブラシと同じ構造にしておくことで将来の平滑化ペイント追加に備える。
pub fn apply_paint(
    field: &mut impl PaintField,
    brush: &SphereBrush,
    layer_index: u32,
    dt: f32,
) -> Vec<ChunkCoord> {
    // ─── レイヤ番号の検証（不正なら無操作）───
    //   レイヤ定義の実数はここでは分からないため、定義上限で弾く。
    if layer_index as usize >= TERRAIN_MAX_LAYERS {
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
    let mut writes: Vec<([i32; 3], BlendSlots, f32)> = Vec::new();
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
                let (slots, amount) = field.read_paint_global(gx, gy, gz);
                let new_slots = add_layer_weight(&slots, layer_index, delta);
                // ── ペイント量も同じ減衰で 1 へ寄せる（ルールからの離脱度）──
                let new_amount = (amount + delta).clamp(0.0, 1.0);

                writes.push(([gx, gy, gz], new_slots, new_amount));
            }
        }
    }

    // ─── フェーズ 2：まとめて書き込み、触れたチャンクを集計する ───
    let mut touched: HashSet<ChunkCoord> = HashSet::new();
    let mut owners_x = Vec::new();
    let mut owners_y = Vec::new();
    let mut owners_z = Vec::new();
    for ([gx, gy, gz], slots, amount) in writes {
        field.write_paint_global(gx, gy, gz, &slots, amount);
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
