// ============================================================
//  terrain/bench.rs — 地形編集ホットパスの CPU 計測（#[ignore] 付きベンチ）
//
//  【責務】
//    地形編集（ブラシ／ペイント）で毎回走る CPU 処理の所要時間を、
//    GPU・エディタ・IPC を一切起動せずに数値化する。
//    通常の `cargo test` では走らせない（#[ignore]）。計測時のみ
//      cargo test -p seed_runtime terrain::bench -- --ignored --nocapture
//    で明示的に実行する。
//
//  【なぜユニットテストとして置くか】
//    criterion 等のベンチフレームワークを足すと依存とビルド時間が増える。
//    ここで測りたいのは「1 チャンク再メッシュに何 ms かかるか」という
//    桁の把握であり、統計的精度は不要。標準の Instant で十分。
//
//  【計測対象】
//    - mesh_only       : marching_cubes::generate（等値面抽出のみ）
//    - mesh_to_model   : TerrainMesh → Model 変換（レイヤ重み → 頂点カラー）
//    - full_cpu_remesh : 上記 2 つの合計（＝ build_chunk_render の CPU 部分）
//    分割数 32³ と 64³ の両方で測る（64 は MAX_CHUNK_CELLS）。
// ============================================================

use std::time::Instant;

use super::chunk_coord::ChunkCoord;
use super::chunk_data::TerrainChunkData;
use super::layers::TerrainLayerSet;
use super::marching_cubes::generate_standalone;
use super::settings::TerrainSettings;
use crate::engine::core::app_base::app::terrain_mesh_build::terrain_mesh_to_model;

// ─── 計測パラメータ（マジックナンバー回避） ─────────────────────────────────

/// 計測する分割数（チャンク 1 軸のセル数）。32 は既定値、64 は MAX_CHUNK_CELLS。
const BENCH_CELL_COUNTS: [u32; 2] = [32, 64];
/// 各計測の反復回数（1 回だけだとキャッシュ状態のばらつきが大きいため平均を取る）。
const BENCH_ITERS: u32 = 10;
/// ベンチ用チャンクのワールド原点（高度ルールを効かせるため 0 固定）。
const BENCH_ORIGIN: [f32; 3] = [0.0, 0.0, 0.0];

/// 計測用の地形設定を作る（分割数のみ差し替え、他は既定）。
fn bench_settings(cells: u32) -> TerrainSettings {
    let mut s = TerrainSettings::default();
    // apply_chunk_config は density_clamp も派生再計算してくれる。
    s.apply_chunk_config(s.ground_chunks_x, s.ground_chunks_z, cells, s.voxel_size);
    s
}

/// 「起伏のある地面」チャンクを作る。
///
/// 平地（density = y）だと表面が 1 枚の平面になり三角形数が少なすぎて
/// 実際の編集後チャンク（掘削で凹凸が付いた状態）の負荷を代表しない。
/// サイン波で凹凸を付け、チャンク中央付近を等値面が横切るようにする。
fn bench_chunk(settings: &TerrainSettings) -> TerrainChunkData {
    let extent = settings.chunk_extent();
    // 波の周期がチャンク内に数回入るようにする（分割数に依らず同じ形状になる）。
    let freq = std::f32::consts::TAU / (extent * 0.25);
    let mid = extent * 0.5;
    TerrainChunkData::from_fn(settings, ChunkCoord::new(0, 0, 0), |x, y, z| {
        // 高さ場 = 中央 + サイン波の起伏。density = y - height（下が solid）。
        let height = mid + (x * freq).sin() * (extent * 0.1) + (z * freq).cos() * (extent * 0.1);
        y - height
    })
}

/// 経過時間の平均 [ms] を返す。
fn avg_ms(total: std::time::Duration, iters: u32) -> f64 {
    total.as_secs_f64() * 1000.0 / iters as f64
}

/// 1 チャンク再メッシュの CPU 時間を分割数ごとに計測する。
#[test]
#[ignore = "計測専用。cargo test terrain::bench -- --ignored --nocapture で実行"]
fn bench_chunk_remesh_cpu() {
    let layers = TerrainLayerSet::default();

    for cells in BENCH_CELL_COUNTS {
        let settings = bench_settings(cells);
        let chunk = bench_chunk(&settings);

        // ── ① 等値面抽出のみ ──
        let mut t_mesh = std::time::Duration::ZERO;
        let mut tri_count = 0usize;
        let mut vert_count = 0usize;
        for _ in 0..BENCH_ITERS {
            let t = Instant::now();
            let mesh = generate_standalone(&chunk, &settings);
            t_mesh += t.elapsed();
            tri_count = mesh.triangle_count();
            vert_count = mesh.positions.len();
        }

        // ── ② TerrainMesh → Model 変換のみ ──
        let mesh = generate_standalone(&chunk, &settings);
        let mut t_model = std::time::Duration::ZERO;
        for _ in 0..BENCH_ITERS {
            let t = Instant::now();
            let _ = terrain_mesh_to_model(&mesh, "bench", BENCH_ORIGIN, &layers);
            t_model += t.elapsed();
        }

        println!(
            "[BENCH] cells={cells:>3} verts={vert_count:>7} tris={tri_count:>7} \
             mesh={:.3}ms model={:.3}ms total={:.3}ms",
            avg_ms(t_mesh, BENCH_ITERS),
            avg_ms(t_model, BENCH_ITERS),
            avg_ms(t_mesh, BENCH_ITERS) + avg_ms(t_model, BENCH_ITERS),
        );
    }
}

/// ペイント高速パス（頂点カラーだけを作り直す）の CPU 時間を計測する。
///
/// 【何を比べているか】
///   従来のペイントは「フル再メッシュ」＝ `bench_chunk_remesh_cpu` の total と同じ
///   CPU コストを毎回払っていた（さらに GPU 側でバッファ再確保・BLAS 再構築も走る）。
///   高速パスは以下の 3 つだけで済む:
///     ① 由来辺 → スプラット補間（interp_vertex_paint）
///     ② レイヤ重み → 頂点カラー＋パレット（compute_layer_colors）
///     ③ 頂点列の組み直し（memcpy 相当。GPU へは 1 回の write_buffer）
///   ここでは GPU を持たないので ①② を測る（③ は memcpy で、実測上は誤差範囲）。
#[test]
#[ignore = "計測専用。cargo test terrain::bench -- --ignored --nocapture で実行"]
fn bench_paint_fast_path_cpu() {
    use super::marching_cubes::interp_vertex_paint;
    use crate::engine::core::app_base::app::terrain_mesh_build::compute_layer_colors;

    let layers = TerrainLayerSet::default();

    for cells in BENCH_CELL_COUNTS {
        let settings = bench_settings(cells);
        let chunk = bench_chunk(&settings);
        // 高速パスの入力：一度だけ作ったメッシュの「由来辺」と頂点の位置・法線。
        // 実機では chunk_vertex_edges キャッシュと ModelComponent の CPU モデルから得る。
        let mesh = generate_standalone(&chunk, &settings);
        let vert_count = mesh.positions.len();

        // ── ① 由来辺 → スプラット補間 ──
        let mut t_recalc = std::time::Duration::ZERO;
        let mut paint = Vec::new();
        let mut paint_amount = Vec::new();
        for _ in 0..BENCH_ITERS {
            let t = Instant::now();
            // 1 頂点につき interp_vertex_paint は 1 回だけ呼ぶ（実装と同じ形）。
            // 2 回呼ぶと補間コストが二重に乗り、計測値が実態より悪く出る。
            let pairs: Vec<_> = mesh.edges.iter().map(|e| interp_vertex_paint(&chunk, e)).collect();
            paint = pairs.iter().map(|p| p.0).collect();
            paint_amount = pairs.iter().map(|p| p.1).collect();
            t_recalc += t.elapsed();
        }

        // ── ② レイヤ重み → 頂点カラー＋パレット ──
        let mut t_colors = std::time::Duration::ZERO;
        for _ in 0..BENCH_ITERS {
            let t = Instant::now();
            let _ = compute_layer_colors(
                &mesh.positions, &mesh.normals, &paint, &paint_amount, BENCH_ORIGIN, &layers,
            );
            t_colors += t.elapsed();
        }

        println!(
            "[BENCH paint] cells={cells:>3} verts={vert_count:>7} \
             recalc={:.3}ms colors={:.3}ms total={:.3}ms",
            avg_ms(t_recalc, BENCH_ITERS),
            avg_ms(t_colors, BENCH_ITERS),
            avg_ms(t_recalc, BENCH_ITERS) + avg_ms(t_colors, BENCH_ITERS),
        );
    }
}
