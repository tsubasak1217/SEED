// ============================================================
//  terrain/sharpness.rs — 球ブラシによる法線シャープネスペイント
//
//  【責務】
//    「スムーズ法線（SDF 勾配）と面法線をどの比率で混ぜるか」を表すスカラー場
//    （0=完全スムーズ、1=完全な面法線）を、球ブラシで編集する純粋アルゴリズム。
//    レイヤペイント（paint.rs）とまったく同じ構造・同じ境界規約に従い、
//    チャンクの格納方式には一切依存しない（PaintField / SampleField と同じ流儀）。
//
//  【ブラシの意味論 — "目標値へ寄せる" 方式】
//    レイヤペイント（押し上げ＋正規化）とは違い、シャープネスは 1 本のスカラーなので
//    「押し上げ」だと 1.0 へ張り付くだけで下げられない。そこで
//      new = current + (target - current) * clamp(delta, 0, 1)
//    という **目標値への漸近**にする。これ 1 本で
//      ・岩場を作る       … target = 1 でなぞる
//      ・砂地へ戻す（消去）… target = 0 でなぞる
//      ・中間の質感       … target = 0.4 などでなぞる
//    のすべてが表せ、しかも「なぞり続けると target に収束して止まる」という
//    ペイントツールとして自然な挙動になる（カバーブラシ I3.1 と同じ考え方）。
//
//  【なぜ密度グリッドと同じ格子に持つのか】
//    頂点ではなくサンプルに持つことで、再メッシュ（掘削・盛り上げ・LOD 切替）でも
//    値が生き残り、チャンク境界サンプルの重複所有によって継ぎ目が割れない。
//    詳しくは chunk_data.rs のヘッダコメントを参照。
// ============================================================

use std::collections::HashSet;

use super::brush::SphereBrush;
use super::brush_mask::{brush_mask_is_active, brush_shape_factor};
use super::chunk_coord::ChunkCoord;
use super::cover::CoverMask;
use super::settings::TerrainSettings;

/// 実効ブラシ係数がこの値以下のサンプルは書き込みをスキップする。
/// 球の縁で 0 に漸近する減衰の裾を切り、無意味な dirty 化を防ぐ。
/// レイヤペイント（paint.rs::PAINT_MIN_AMOUNT）と同じ値・同じ意図。
const SHARPNESS_MIN_DELTA: f32 = 1.0e-4;

/// エンジン層が実装する「グローバル法線シャープネス場」インターフェース。
///
/// `paint.rs::PaintField` と対になる、シャープネス用の口。
/// 密度・スプラットと同じグリッド・同じ境界重複規約に従う。
///
/// 【なぜ PaintField を拡張せず別トレイトにしたか】
///   PaintField はレイヤペイント（スロット重み＋ペイント量）という 1 つの関心事を
///   表しており、そこへシャープネスを足すと「1 トレイト＝1 責務」が崩れる。
///   また既存の PaintField 実装（テスト用のダミー場を含む）へメソッド追加を強制する
///   ことになり、無関係な箇所へ変更が波及する。別トレイトなら実装したい場だけが
///   実装すればよい。
pub trait SharpnessField {
    /// 地形設定への参照。
    fn settings(&self) -> &TerrainSettings;
    /// グローバルサンプル整数座標のシャープネス（0..=1）を読む。
    /// 未初期化・範囲外は 0.0（＝完全スムーズ）を返すこと。
    fn read_sharpness_global(&self, gx: i32, gy: i32, gz: i32) -> f32;
    /// グローバルサンプル整数座標へシャープネス（0..=1）を書く。
    /// 境界で重複する全チャンクに同一値を書き込むこと（同期）。
    fn write_sharpness_global(&mut self, gx: i32, gy: i32, gz: i32, w: f32);
    /// グローバルサンプル整数座標のワールド空間位置（メートル）。
    fn world_of_global(&self, gx: i32, gy: i32, gz: i32) -> [f32; 3];
}

/// 指定軸のグローバルサンプル `g` を所有するチャンクインデックス集合を返す。
///
/// 境界サンプル（g が chunk_cells の倍数）は隣り合う 2 チャンクが共有する。
/// paint.rs / brush.rs の同名ロジックと同一規約（重複所有）。
fn owning_chunks_on_axis(g: i32, chunk_cells: i32, out: &mut Vec<i32>) {
    out.clear();
    let primary = g.div_euclid(chunk_cells);
    out.push(primary);
    if g.rem_euclid(chunk_cells) == 0 {
        out.push(primary - 1);
    }
}

/// 現在値を目標値へ `delta` の割合だけ近づける（ブラシ 1 発ぶんの純関数）。
///
/// - `delta` は 0..=1 にクランプされる（1 で即座に目標値へ到達）。
/// - 戻り値は 0..=1 にクランプされる。
///
/// 上げ（target > current）にも下げ（消去。target < current）にも同じ式が効き、
/// なぞり続けると必ず `target` へ収束して止まる。
#[inline]
pub fn approach_target(current: f32, target: f32, delta: f32) -> f32 {
    let t = delta.clamp(0.0, 1.0);
    let tgt = target.clamp(0.0, 1.0);
    let cur = current.clamp(0.0, 1.0);
    (cur + (tgt - cur) * t).clamp(0.0, 1.0)
}

/// 球ブラシで法線シャープネスを目標値へ寄せ、変化したサンプルを所有するチャンク集合を返す。
///
/// - `target`: 目標シャープネス（0=スムーズ〜1=面法線）。0 を渡せば消去になる。
/// - `dt`:     フレーム時間相当（strength と掛けて 1 回の寄せ量になる）。
///
/// 本番経路（エディタのブラシ）はマスク付きの `apply_sharpness_with_mask` を直接呼ぶため、
/// この薄いラッパは現在テストからしか使われない。`apply_paint` と対になる API 形状を
/// 保つ意味があるので残す（マスク無しで呼びたい将来の呼び出し側のための入口）。
#[allow(dead_code)]
pub fn apply_sharpness(
    field: &mut impl SharpnessField,
    brush: &SphereBrush,
    target: f32,
    dt: f32,
) -> Vec<ChunkCoord> {
    apply_sharpness_with_mask(field, brush, target, dt, None)
}

/// 形状マスク付きの法線シャープネスペイント。
///
/// `mask` 以外の引数と戻り値の意味は `apply_sharpness` と同一。
/// `mask` が `None`（未指定）または無効なら `apply_sharpness` と同じ結果になる。
///
/// マスク指定時にサンプルの棄却範囲が「球」から「XZ 正方形 × Y 半径」へ変わる点は
/// `paint::apply_paint_with_mask` とまったく同じ規約である（詳細はそちらのコメント）。
pub fn apply_sharpness_with_mask(
    field: &mut impl SharpnessField,
    brush: &SphereBrush,
    target: f32,
    dt: f32,
    mask: Option<&CoverMask>,
) -> Vec<ChunkCoord> {
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
    let mask_active = brush_mask_is_active(mask);

    // ─── フェーズ 1：新しいシャープネス値を計算する ───
    let mut writes: Vec<([i32; 3], f32)> = Vec::new();
    for gz in gz0..=gz1 {
        for gy in gy0..=gy1 {
            for gx in gx0..=gx1 {
                let wp = field.world_of_global(gx, gy, gz);
                let dx = wp[0] - brush.center[0];
                let dy = wp[1] - brush.center[1];
                let dz = wp[2] - brush.center[2];
                let dist2 = dx * dx + dy * dy + dz * dz;
                if mask_active {
                    // マスクは XZ 正方形いっぱいに貼るので XZ の棄却は行わない。
                    if dy.abs() > brush.radius {
                        continue;
                    }
                } else if dist2 > r2 {
                    continue;
                }
                let f = brush_shape_factor(
                    mask,
                    [brush.center[0], brush.center[2]],
                    brush.radius,
                    [wp[0], wp[2]],
                    dist2.sqrt(),
                );
                let delta = brush.strength * f * dt;
                if delta <= SHARPNESS_MIN_DELTA {
                    continue;
                }

                let current = field.read_sharpness_global(gx, gy, gz);
                let next = approach_target(current, target, delta);
                writes.push(([gx, gy, gz], next));
            }
        }
    }

    // ─── フェーズ 2：まとめて書き込み、触れたチャンクを集計する ───
    let mut touched: HashSet<ChunkCoord> = HashSet::new();
    let mut owners_x = Vec::new();
    let mut owners_y = Vec::new();
    let mut owners_z = Vec::new();
    for ([gx, gy, gz], w) in writes {
        field.write_sharpness_global(gx, gy, gz, w);
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
