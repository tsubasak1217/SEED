// ============================================================
//  terrain/brush.rs — 球ブラシによる地形編集（グローバルサンプル場に作用）
//
//  【責務】
//    球状ブラシで密度場を編集する純粋なアルゴリズム。
//    単一チャンクではなく「グローバルサンプル場」(SampleField トレイト)に
//    対して作用するため、チャンク境界をまたいでも継ぎ目が破綻しない。
//    このファイルはチャンクの格納方式を一切知らない（純粋・境界安全）。
//
//  【継ぎ目の水密性】
//    読み書きはすべて read_global / write_global を経由する。
//    write_global は境界で重複する（複数チャンクが所有する）サンプルを
//    すべてのチャンクへ同一値で書き込むため、隣接チャンクの境界サンプルは
//    ビット一致に保たれる。Smooth の隣接平均も read_global 経由なので、
//    継ぎ目の両側で同一の平滑値が計算される。
// ============================================================

use std::collections::HashSet;

use serde::{Deserialize, Serialize};

use super::chunk_coord::ChunkCoord;
use super::settings::TerrainSettings;

// ─── ブラシ操作種別の IPC マッピング用定数 ───────────────────────────────────
const BRUSH_OP_ADD: u32 = 0;
const BRUSH_OP_SUBTRACT: u32 = 1;
const BRUSH_OP_SMOOTH: u32 = 2;
const BRUSH_OP_FLATTEN: u32 = 3;

/// ブラシ操作の種別。
///
/// IPC 用の数値マッピングは as_u32 / from_u32 を参照
/// （Add=0, Subtract=1, Smooth=2, Flatten=3）。
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Debug)]
pub enum BrushOp {
    /// 密度を下げて solid を増やす（盛る）
    Add,
    /// 密度を上げて air を増やす（削る・洞窟）
    Subtract,
    /// 6 近傍平均へ寄せて平滑化する
    Smooth,
    /// ブラシ中心を通る水平面へ寄せる（整地）
    Flatten,
}

impl BrushOp {
    /// IPC 送信用の u32 表現を返す。
    pub fn as_u32(self) -> u32 {
        match self {
            BrushOp::Add => BRUSH_OP_ADD,
            BrushOp::Subtract => BRUSH_OP_SUBTRACT,
            BrushOp::Smooth => BRUSH_OP_SMOOTH,
            BrushOp::Flatten => BRUSH_OP_FLATTEN,
        }
    }

    /// IPC 受信した u32 を BrushOp に復元する（未知の値は None）。
    pub fn from_u32(v: u32) -> Option<BrushOp> {
        match v {
            BRUSH_OP_ADD => Some(BrushOp::Add),
            BRUSH_OP_SUBTRACT => Some(BrushOp::Subtract),
            BRUSH_OP_SMOOTH => Some(BrushOp::Smooth),
            BRUSH_OP_FLATTEN => Some(BrushOp::Flatten),
            _ => None,
        }
    }
}

/// 球状ブラシのパラメータ。
#[derive(Clone, Copy, Serialize, Deserialize, Debug)]
pub struct SphereBrush {
    /// ブラシ中心（ワールド空間メートル）
    pub center: [f32; 3],
    /// ブラシ半径（メートル）
    pub radius: f32,
    /// 1 秒あたりの編集強度
    pub strength: f32,
}

/// エンジン層が実装する「グローバルサンプル場」インターフェース。
///
/// ブラシはこのトレイト越しにしか場へアクセスしないため、
/// チャンク格納方式や境界重複の同期はエンジン層に隠蔽される。
pub trait SampleField {
    /// 地形設定への参照。
    fn settings(&self) -> &TerrainSettings;
    /// グローバルサンプル整数座標の密度を読む。
    fn read_global(&self, gx: i32, gy: i32, gz: i32) -> f32;
    /// グローバルサンプル整数座標へ密度を書く。
    /// 境界で重複する全チャンクに同一値を書き込むこと（同期）。
    fn write_global(&mut self, gx: i32, gy: i32, gz: i32, v: f32);
    /// グローバルサンプル整数座標のワールド空間位置（メートル）。
    fn world_of_global(&self, gx: i32, gy: i32, gz: i32) -> [f32; 3];
}

/// smoothstep（3t²-2t³）。t は [0,1] にクランプして評価する。
#[inline]
fn smoothstep(t: f32) -> f32 {
    let x = t.clamp(0.0, 1.0);
    x * x * (3.0 - 2.0 * x)
}

/// ブラシ中心からの距離 `dist` に対する減衰係数（中心 1 → 半径 0）。
#[inline]
fn falloff(dist: f32, radius: f32) -> f32 {
    if radius <= 0.0 {
        // 半径 0 は減衰未定義 → 影響なしとする。
        return 0.0;
    }
    // 中心で 1、半径で 0 になるよう (1 - dist/radius) を smoothstep する。
    smoothstep(1.0 - dist / radius)
}

/// 指定軸のグローバルサンプル `g` を所有するチャンクインデックス集合を返す。
///
/// 境界サンプル（g が chunk_cells の倍数）は隣り合う 2 チャンクが共有する。
fn owning_chunks_on_axis(g: i32, chunk_cells: i32, out: &mut Vec<i32>) {
    out.clear();
    // ─── 主となるチャンク（負数対応の floor 除算） ───
    let primary = g.div_euclid(chunk_cells);
    out.push(primary);
    // ─── 境界サンプルなら 1 つ手前のチャンクもローカル末尾として所有 ───
    if g.rem_euclid(chunk_cells) == 0 {
        out.push(primary - 1);
    }
}

/// 球ブラシを密度場に適用し、密度が変化したサンプルを所有するチャンク集合を返す。
///
/// 手順:
///   1. 球を覆うグローバルサンプル AABB を求める。
///   2. 半径内の各サンプルについて操作種別ごとに新密度を計算（スナップショット）。
///   3. 計算後にまとめて write_global（Smooth の隣接読みが順序非依存になる）。
///   4. 書き込んだサンプルを所有するチャンク座標を集めて返す。
///
/// 戻り値のチャンクをエンジン層で再メッシュ化する。
pub fn apply(
    field: &mut impl SampleField,
    brush: &SphereBrush,
    op: BrushOp,
    dt: f32,
) -> Vec<ChunkCoord> {
    // ─── 必要なスカラを先にコピー（settings の借用を長く保持しない） ───
    let (voxel, chunk_cells, clamp) = {
        let s = field.settings();
        (s.voxel_size, s.chunk_cells as i32, s.density_clamp)
    };

    // ─── 球を覆うグローバルサンプル AABB（world = g * voxel_size より g = world/voxel） ───
    let inv_voxel = 1.0 / voxel;
    let min_g = |axis: usize| ((brush.center[axis] - brush.radius) * inv_voxel).floor() as i32;
    let max_g = |axis: usize| ((brush.center[axis] + brush.radius) * inv_voxel).ceil() as i32;
    let (gx0, gx1) = (min_g(0), max_g(0));
    let (gy0, gy1) = (min_g(1), max_g(1));
    let (gz0, gz1) = (min_g(2), max_g(2));

    let r2 = brush.radius * brush.radius;

    // ─── フェーズ 1：新密度をスナップショットとして計算（(g, newval) のリスト） ───
    let mut writes: Vec<([i32; 3], f32)> = Vec::new();
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
                let dist = dist2.sqrt();
                let f = falloff(dist, brush.radius);
                // 実効編集量（0 なら書き込み不要）。
                let amount = brush.strength * f * dt;
                if amount == 0.0 {
                    continue;
                }

                let cur = field.read_global(gx, gy, gz);
                // ─── 操作種別ごとに新密度を計算 ───
                let new_val = match op {
                    // Add: 密度を下げて solid を増やす。
                    BrushOp::Add => cur - amount,
                    // Subtract: 密度を上げて air を増やす（掘る）。
                    BrushOp::Subtract => cur + amount,
                    // Flatten: ブラシ中心を通る水平面 (world_y - center_y) へ寄せる。
                    BrushOp::Flatten => {
                        let target = wp[1] - brush.center[1];
                        cur + (target - cur) * amount
                    }
                    // Smooth: 6 近傍平均へ寄せる（read_global 経由で境界も一貫）。
                    BrushOp::Smooth => {
                        let avg = (field.read_global(gx + 1, gy, gz)
                            + field.read_global(gx - 1, gy, gz)
                            + field.read_global(gx, gy + 1, gz)
                            + field.read_global(gx, gy - 1, gz)
                            + field.read_global(gx, gy, gz + 1)
                            + field.read_global(gx, gy, gz - 1))
                            / 6.0;
                        cur + (avg - cur) * amount
                    }
                };
                // 密度クランプ（勾配の発散防止）。
                let clamped = new_val.clamp(-clamp, clamp);
                writes.push(([gx, gy, gz], clamped));
            }
        }
    }

    // ─── フェーズ 2：まとめて書き込み（境界重複は write_global 側で同期） ───
    let mut touched: HashSet<ChunkCoord> = HashSet::new();
    let mut owners_x = Vec::new();
    let mut owners_y = Vec::new();
    let mut owners_z = Vec::new();
    for (g, v) in &writes {
        field.write_global(g[0], g[1], g[2], *v);
        // このサンプルを所有する全チャンクを集計（境界は複数所有）。
        owning_chunks_on_axis(g[0], chunk_cells, &mut owners_x);
        owning_chunks_on_axis(g[1], chunk_cells, &mut owners_y);
        owning_chunks_on_axis(g[2], chunk_cells, &mut owners_z);
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
