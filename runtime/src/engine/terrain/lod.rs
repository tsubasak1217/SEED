// ============================================================
//  terrain/lod.rs — チャンク単位の地形 LOD（低解像度メッシュ生成）
//
//  【責務】
//    遠いチャンクを「低解像度の marching cubes メッシュ」で描くための、
//    エンジン非依存な純粋アルゴリズム層。密度グリッド（33³）を stride で
//    間引いた粗いグリッドへ写し、既存の marching cubes をそのまま回すことで、
//    頂点数が 1/4・1/16 に減ったメッシュを得る。
//
//  【設計方針 — 既存フル解像度経路は不変】
//    marching_cubes.rs には一切手を入れない。LOD は「密度グリッドを間引いた
//    別チャンク（TerrainChunkData）を組み、voxel_size を stride 倍した設定で
//    既存の generate_standalone を呼ぶ」だけで実現する。
//      - 間引きチャンクの 1 セル = 元の stride セルぶんの実寸（voxel_size*stride）。
//      - セル数 = chunk_cells/stride。両者の積（＝チャンクの広がり）は元と一致する
//        ので、出力頂点は元とまったく同じチャンクローカル空間（[0, extent]）に載る。
//      → LOD0（フル解像度）は呼び出し側が従来どおり generate(neighbor_sampler) を
//        使い続けるため、既存のフル解像度出力・水密性・全テストは 1 ビットも変わらない。
//
//  【チャンク境界の継ぎ目（クラック）対策 — スカート】
//    隣接チャンクが別 LOD だと、境界面でテッセレーションが食い違い、粗い側の面と
//    細かい側の面のあいだに隙間（見通せる穴）が出る（LOD 遷移の古典問題）。
//    第 1 段では **スカート（境界面から真下へ伸ばす垂直カーテン）** で隙間を隠す。
//    チャンクの 4 つの垂直側面（x=0 / x=extent / z=0 / z=extent）上に載る表面辺を
//    検出し、そこから skirt_depth だけ下へ幕を張る。隣り合う 2 チャンクの境界面には
//    必ずどちらか（粗い側）のカーテンが立つため、隙間は背後のカーテンで塞がれる。
//
//    スカートは **LOD>=1（間引いた）チャンクにだけ**付ける。LOD0 は従来経路のままで
//    （境界サンプル共有により隣接 LOD0 とは水密・§docs terrain 5）、かつ LOD0 の
//    頂点列にスカート頂点を混ぜるとペイント高速パス（由来辺キャッシュ）と食い違うため、
//    LOD0 には一切足さない。LOD0↔LOD1 の継ぎ目は LOD1 側のカーテンが受け持つ。
//
//    【第 1 段の限界（申し送り）】
//      - スカートは 4 つの垂直側面のみ（上下 Y 面＝縦積みチャンクや洞窟の継ぎ目は非対応）。
//      - 高さ差が skirt_depth を超えると穴が残り得る（skirt_depth は LOD セル寸に比例させる）。
// ============================================================

use super::chunk_data::TerrainChunkData;
use super::layers::BlendSlots;
use super::marching_cubes::{generate_standalone, TerrainMesh, TerrainVertexEdge};
use super::settings::TerrainSettings;

// ─── LOD 段の定義（データドリブン・マジックナンバー禁止） ─────────────────────

/// LOD 段ごとのサンプル間引き幅（stride）。添字 = LOD レベル。
///   LOD0 = 1（フル解像度）、LOD1 = 2（頂点 ≒ 1/4）、LOD2 = 4（頂点 ≒ 1/16）。
/// これ自体がデータ：段を増減したければこの配列を変えるだけでよい
///   （将来は props/settings 化する。ここは名前付き const で集約）。
pub const TERRAIN_LOD_STRIDES: [u32; 3] = [1, 2, 4];

/// スカートの深さを「LOD セル 1 個の実寸（voxel_size*stride）」の何倍にするか。
/// 高さ差は最悪でも LOD セル 1 個ぶん（≒ voxel*stride）なので、その 2 倍あれば
/// 露骨な穴はまず出ない。深すぎると斜めから幕が見えるため、控えめの倍率にする。
pub const SKIRT_DEPTH_IN_LOD_CELLS: f32 = 2.0;

/// 「頂点がチャンク境界面に載っている」と判定する許容誤差（LOD voxel に対する割合）。
/// 境界面上の MC 頂点は面に垂直な座標が厳密に 0.0 か extent になる（格子線上のため）ので、
/// ごく小さな相対誤差で十分に判定できる。
const BOUNDARY_EPS_FRACTION: f32 = 1.0e-3;

/// LOD 段数を返す。
#[inline]
pub fn lod_count() -> usize {
    TERRAIN_LOD_STRIDES.len()
}

/// LOD レベル（0..lod_count）に対応する間引き stride を返す。範囲外は最粗段へ丸める。
#[inline]
pub fn stride_for_lod(lod: usize) -> u32 {
    let idx = lod.min(TERRAIN_LOD_STRIDES.len() - 1);
    TERRAIN_LOD_STRIDES[idx]
}

/// 密度グリッドを stride で間引いた「粗いチャンク」と、その粗いチャンク用の設定を返す。
///
/// 間引きチャンクは、元グリッドを stride おきにサンプルしたもの（density・手ペイント
/// スロット・ペイント量すべてを最近サンプルからコピー）。設定は voxel_size を stride 倍・
/// chunk_cells を 1/stride にして、チャンクの実寸（extent）を保つ。
///
/// `chunk_cells` が stride で割り切れない場合は `None`（間引きが定義できない）。
/// 呼び出し側はより細かい LOD（または LOD0）へフォールバックする。
pub fn decimate_chunk(
    chunk: &TerrainChunkData,
    settings: &TerrainSettings,
    stride: u32,
) -> Option<(TerrainChunkData, TerrainSettings)> {
    // stride=1 は「間引かない」＝この関数の守備範囲外（LOD0 は従来経路を使う）。
    if stride < 2 {
        return None;
    }
    let cells = settings.chunk_cells;
    if cells % stride != 0 {
        return None;
    }

    // ─── 粗いチャンク用の設定（実寸を保つ）───
    let mut lod_settings = settings.clone();
    lod_settings.voxel_size = settings.voxel_size * stride as f32;
    lod_settings.chunk_cells = cells / stride;

    // ─── 粗いグリッドへ density・ペイントを間引きコピー ───
    //   粗いサンプル (ix,iy,iz) ← 元サンプル (ix*stride, ...)。
    //   元サンプル数 = cells+1、strided 最大添字 = (cells/stride)*stride = cells ≤ cells。
    //   よって境界サンプル（添字 cells）まで正しく取り込める（範囲外アクセスは起きない）。
    let s_new = lod_settings.samples_per_axis();
    let mut lod = TerrainChunkData::new_filled(&lod_settings, 0.0);
    for iz in 0..s_new {
        let sz = iz * stride as usize;
        for iy in 0..s_new {
            let sy = iy * stride as usize;
            for ix in 0..s_new {
                let sx = ix * stride as usize;
                lod.set_sample(ix, iy, iz, chunk.sample(sx, sy, sz));
                // 手ペイントも間引いて運ぶ（遠景でも塗り分けの色が大きく飛ばないように）。
                let slots = chunk.paint_slots(sx, sy, sz);
                lod.set_paint_slots(ix, iy, iz, &slots);
                lod.set_paint_amount(ix, iy, iz, chunk.paint_amount(sx, sy, sz));
            }
        }
    }
    Some((lod, lod_settings))
}

/// チャンクの LOD メッシュ（間引き＋スカート）を生成する。stride>=2 専用。
///
/// LOD0（stride=1）はここでは扱わない（呼び出し側が neighbor_sampler 付きの
/// `terrain::generate` を使うため）。stride が chunk_cells を割り切らない場合は `None`。
///
/// 戻り値のメッシュは元チャンクと同じローカル空間（[0, extent]）に載るので、
/// 描画・ワールド配置・レイヤ色計算はフル解像度と同じ扱いでよい。
pub fn generate_lod_mesh(
    chunk: &TerrainChunkData,
    settings: &TerrainSettings,
    stride: u32,
) -> Option<TerrainMesh> {
    let (lod_chunk, lod_settings) = decimate_chunk(chunk, settings, stride)?;
    // 粗いグリッドで marching cubes（既存関数をそのまま流用）。
    let mut mesh = generate_standalone(&lod_chunk, &lod_settings);
    // スカート深さ = LOD セル 1 個の実寸 × 係数。
    let lod_cell = lod_settings.voxel_size; // = settings.voxel_size * stride
    let skirt_depth = lod_cell * SKIRT_DEPTH_IN_LOD_CELLS;
    let extent = lod_settings.chunk_extent(); // = settings.chunk_extent()（不変）
    let eps = lod_cell * BOUNDARY_EPS_FRACTION;
    add_boundary_skirt(&mut mesh, extent, skirt_depth, eps);
    Some(mesh)
}

// ─── スカート生成の内部ヘルパ ───────────────────────────────────────────────

/// チャンクの 4 つの垂直側面に載る表面辺から、真下へ伸びる幕（スカート）を張る。
///
/// `extent` はチャンク 1 軸の広がり（m）。`skirt_depth` は幕の深さ（m）。
/// `eps` は「面に載っている」判定の許容誤差（m）。
///
/// 面は x=0 / x=extent / z=0 / z=extent の 4 つ（垂直側面）。三角形の各辺について、
/// 両端点が同じ側面に載っていればその辺を境界辺とみなし、辺の 2 頂点を真下へ
/// `skirt_depth` 下ろした 2 頂点を追加して、四角形（＝2 三角形）で幕を張る。
/// 背面カリングでどちら側からも消えないよう、幕は両巻きで出す（境界辺は周長オーダで
/// 少数のため、両面化のコストは無視できる）。
fn add_boundary_skirt(mesh: &mut TerrainMesh, extent: f32, skirt_depth: f32, eps: f32) {
    // 4 つの側面の (「面上か」を判定する関数, その面の外向き水平法線)。
    //   axis: 0=x, 2=z。value: 0.0 か extent。
    let faces: [(usize, f32, [f32; 3]); 4] = [
        (0, 0.0, [-1.0, 0.0, 0.0]),    // x=0     面（外向き -X）
        (0, extent, [1.0, 0.0, 0.0]),  // x=extent 面（外向き +X）
        (2, 0.0, [0.0, 0.0, -1.0]),    // z=0     面（外向き -Z）
        (2, extent, [0.0, 0.0, 1.0]),  // z=extent 面（外向き +Z）
    ];

    // 頂点が指定面に載っているか。
    let on_face = |pos: [f32; 3], axis: usize, value: f32| -> bool {
        (pos[axis] - value).abs() <= eps
    };

    // MC 出力の三角形は今後スカートを足すぶんだけ増えるので、辺の走査は
    // 「元の三角形数」に固定してから行う（スカート自身の辺を再検出しないため）。
    let base_tri_count = mesh.indices.len() / 3;

    for t in 0..base_tri_count {
        let tri = [
            mesh.indices[t * 3],
            mesh.indices[t * 3 + 1],
            mesh.indices[t * 3 + 2],
        ];
        for k in 0..3 {
            let a = tri[k];
            let b = tri[(k + 1) % 3];
            let pa = mesh.positions[a as usize];
            let pb = mesh.positions[b as usize];
            // この辺が 4 面のいずれかに丸ごと載っているか調べる。
            for &(axis, value, face_normal) in faces.iter() {
                if on_face(pa, axis, value) && on_face(pb, axis, value) {
                    push_skirt_quad(mesh, a, b, skirt_depth, face_normal);
                    // 1 つの辺が同時に 2 面へ載る（角の縦稜）ことは稀だが、
                    // 二重に幕を張っても害は無いので break はしない。
                }
            }
        }
    }
}

/// 境界辺 (top_a, top_b) の下へ深さ `depth` の幕（四角形＝両巻き 4 三角形）を張る。
fn push_skirt_quad(
    mesh: &mut TerrainMesh,
    top_a: u32,
    top_b: u32,
    depth: f32,
    face_normal: [f32; 3],
) {
    let pa = mesh.positions[top_a as usize];
    let pb = mesh.positions[top_b as usize];
    // 下端 2 頂点（真下へ depth）。
    let ba = [pa[0], pa[1] - depth, pa[2]];
    let bb = [pb[0], pb[1] - depth, pb[2]];

    // 追加頂点は「上端頂点のペイント/ペイント量を引き継ぐ」（色が縦へ連続するように）。
    // 由来辺（edges）はペイント高速パスでしか使わず、LOD メッシュはその対象外なので
    // ダミー値で埋める（長さだけ positions と揃える）。
    let paint_a = mesh.paint.get(top_a as usize).copied().unwrap_or_default();
    let paint_b = mesh.paint.get(top_b as usize).copied().unwrap_or_default();
    let amount_a = mesh.paint_amount.get(top_a as usize).copied().unwrap_or(0.0);
    let amount_b = mesh.paint_amount.get(top_b as usize).copied().unwrap_or(0.0);
    let dummy_edge = TerrainVertexEdge { lo: [0, 0, 0], axis: 0, t: 0.0 };

    let idx_ba = mesh.positions.len() as u32;
    mesh.positions.push(ba);
    mesh.normals.push(face_normal);
    mesh.paint.push(paint_a);
    mesh.paint_amount.push(amount_a);
    mesh.edges.push(dummy_edge);

    let idx_bb = mesh.positions.len() as u32;
    mesh.positions.push(bb);
    mesh.normals.push(face_normal);
    mesh.paint.push(paint_b);
    mesh.paint_amount.push(amount_b);
    mesh.edges.push(dummy_edge);

    // 四角形 (top_a, top_b, bot_b, bot_a) を 2 三角形で。背面カリング対策に両巻きで出す。
    let quad = [top_a, top_b, idx_bb, idx_ba];
    // 表: (0,1,2),(0,2,3)
    mesh.indices.extend_from_slice(&[quad[0], quad[1], quad[2]]);
    mesh.indices.extend_from_slice(&[quad[0], quad[2], quad[3]]);
    // 裏: (0,2,1),(0,3,2)
    mesh.indices.extend_from_slice(&[quad[0], quad[2], quad[1]]);
    mesh.indices.extend_from_slice(&[quad[0], quad[3], quad[2]]);
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::terrain::chunk_coord::ChunkCoord;

    /// 球 SDF（チャンク内に完全収容）でチャンクを埋める。境界面は横切らない。
    fn sphere_chunk(settings: &TerrainSettings) -> TerrainChunkData {
        let s = settings.samples_per_axis();
        let voxel = settings.voxel_size;
        let center = [
            settings.chunk_extent() * 0.5,
            settings.chunk_extent() * 0.5,
            settings.chunk_extent() * 0.5,
        ];
        let radius = settings.chunk_extent() * 0.3;
        let mut chunk = TerrainChunkData::new_filled(settings, 0.0);
        for iz in 0..s {
            for iy in 0..s {
                for ix in 0..s {
                    let p = [ix as f32 * voxel, iy as f32 * voxel, iz as f32 * voxel];
                    let d = [p[0] - center[0], p[1] - center[1], p[2] - center[2]];
                    let dist = (d[0] * d[0] + d[1] * d[1] + d[2] * d[2]).sqrt();
                    chunk.set_sample(ix, iy, iz, dist - radius);
                }
            }
        }
        chunk
    }

    /// 水平な地面（density = worldY - h）でチャンクを埋める。表面が 4 つの垂直側面を横切る。
    fn ground_chunk(settings: &TerrainSettings, surface_y: f32) -> TerrainChunkData {
        let s = settings.samples_per_axis();
        let voxel = settings.voxel_size;
        let mut chunk = TerrainChunkData::new_filled(settings, 0.0);
        for iz in 0..s {
            for iy in 0..s {
                let world_y = iy as f32 * voxel;
                for ix in 0..s {
                    chunk.set_sample(ix, iy, iz, world_y - surface_y);
                }
            }
        }
        chunk
    }

    /// 間引きにより三角形数が段階的に減ること（≒ 1/4・1/16）。
    #[test]
    fn decimation_reduces_triangle_count() {
        let settings = TerrainSettings::default(); // cells=32（2・4 で割り切れる）
        let chunk = sphere_chunk(&settings);

        let full = generate_standalone(&chunk, &settings).triangle_count();
        let lod1 = generate_lod_mesh(&chunk, &settings, 2).unwrap().triangle_count();
        let lod2 = generate_lod_mesh(&chunk, &settings, 4).unwrap().triangle_count();

        assert!(full > 0, "フルメッシュが空");
        // 球は境界を横切らないためスカートは 0。純粋に間引き効果だけを見る。
        assert!(lod1 < full, "LOD1 がフルより減っていない: {lod1} vs {full}");
        assert!(lod2 < lod1, "LOD2 が LOD1 より減っていない: {lod2} vs {lod1}");
        // おおよそ面積比（1/4・1/16）に乗る。係数は緩めに取り、桁が合うことだけ固定する。
        assert!(
            (lod1 as f32) < (full as f32) * 0.5,
            "LOD1 の削減が弱すぎる: {lod1} vs {full}"
        );
        assert!(
            (lod2 as f32) < (full as f32) * 0.25,
            "LOD2 の削減が弱すぎる: {lod2} vs {full}"
        );
    }

    /// stride が chunk_cells を割り切らないときは None（フォールバックを促す）。
    #[test]
    fn indivisible_stride_returns_none() {
        let mut settings = TerrainSettings::default();
        settings.chunk_cells = 6; // 4 で割り切れない
        let chunk = ground_chunk(&settings, settings.chunk_extent() * 0.5);
        assert!(generate_lod_mesh(&chunk, &settings, 4).is_none());
        // 2 では割り切れるので生成できる。
        assert!(generate_lod_mesh(&chunk, &settings, 2).is_some());
    }

    /// 境界を横切る地面では、スカート頂点が生成され、すべて表面より下（-Y）に来ること。
    #[test]
    fn skirt_is_generated_below_surface() {
        let settings = TerrainSettings::default();
        let surface_y = settings.chunk_extent() * 0.5;
        let chunk = ground_chunk(&settings, surface_y);

        // スカート無しの純間引き三角形数（decimate + generate のみ）を基準に取る。
        let (lod_chunk, lod_settings) = decimate_chunk(&chunk, &settings, 2).unwrap();
        let no_skirt = generate_standalone(&lod_chunk, &lod_settings).triangle_count();

        let mesh = generate_lod_mesh(&chunk, &settings, 2).unwrap();
        assert!(
            mesh.triangle_count() > no_skirt,
            "スカートが 1 枚も張られていない（境界辺検出が死んでいる）: {} <= {}",
            mesh.triangle_count(),
            no_skirt
        );

        // スカート最下端は surface_y - skirt_depth 付近まで下がる。表面（≒ surface_y）より
        // 明確に下に頂点が存在すること。
        let min_y = mesh
            .positions
            .iter()
            .map(|p| p[1])
            .fold(f32::INFINITY, f32::min);
        assert!(
            min_y < surface_y - 0.1,
            "スカートが下へ伸びていない: min_y={min_y}, surface_y={surface_y}"
        );
    }

    /// LOD メッシュの頂点はすべてチャンクローカル空間の広がり（[−skirt, extent]）内に収まり、
    /// フル解像度と同じ原点・スケールで描けること（X/Z は [0,extent] を超えない）。
    #[test]
    fn lod_mesh_stays_in_chunk_footprint() {
        let settings = TerrainSettings::default();
        let extent = settings.chunk_extent();
        let chunk = ground_chunk(&settings, extent * 0.5);
        let mesh = generate_lod_mesh(&chunk, &settings, 4).unwrap();

        for p in &mesh.positions {
            assert!(
                p[0] >= -1.0e-3 && p[0] <= extent + 1.0e-3,
                "X がフットプリント外: {}",
                p[0]
            );
            assert!(
                p[2] >= -1.0e-3 && p[2] <= extent + 1.0e-3,
                "Z がフットプリント外: {}",
                p[2]
            );
        }
        // ChunkCoord は使わないが、原点計算がフル解像度と共通であることの確認用に触れておく。
        let _ = ChunkCoord::new(0, 0, 0);
    }

    /// stride=1 は decimate の守備範囲外（None）。LOD0 は従来経路を使うという契約を固定する。
    #[test]
    fn stride_one_is_rejected() {
        let settings = TerrainSettings::default();
        let chunk = sphere_chunk(&settings);
        assert!(decimate_chunk(&chunk, &settings, 1).is_none());
    }
}
