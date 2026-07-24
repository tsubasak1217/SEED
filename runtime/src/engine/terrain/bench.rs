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

use rayon::prelude::*;

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
/// ロード計測で並べるチャンク枚数（典型シーン相当。4×4 グラウンド×3 段の目安）。
const BENCH_LOAD_CHUNKS: usize = 48;

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

/// 「1 チャンクの CPU メッシュ生成」（等値面抽出 ＋ Model 変換）を 1 関数にまとめる。
///
/// ロード経路（`rebuild_terrain_after_load` フェーズ2a）が並列に回す純粋処理の代表。
/// 実機の `build_chunk_cpu_model` は隣接チャンクの密度も読むが、単独チャンク（境界は
/// clamp）でも MC の三角形数・変換コストは同オーダーであり、直列 vs 並列の比較には十分。
fn build_one_chunk_cpu(
    chunk: &TerrainChunkData,
    settings: &TerrainSettings,
    layers: &TerrainLayerSet,
) -> (usize, usize) {
    let mesh = generate_standalone(chunk, settings);
    let (model, _palette) = terrain_mesh_to_model(&mesh, "bench_load", BENCH_ORIGIN, layers);
    // 決定性検証で突き合わせる代表値（三角形数・頂点数）を返す。
    let tris = model.meshes[0].primitives[0].indices.len();
    let verts = model.meshes[0].primitives[0].vertices.len();
    (tris, verts)
}

/// シーンロードのメッシュ生成（複数チャンク）を **直列 vs 並列** で計測する。
///
/// ロード経路の支配項は「全チャンクの CPU メッシュ生成が直列」だった点。ここでは
/// `BENCH_LOAD_CHUNKS` 枚を直列（`map`）と並列（`par_iter().map()`）で作り、
/// 所要時間と実効スピードアップを分割数ごとに出す。GPU・エディタは起動しない。
#[test]
#[ignore = "計測専用。cargo test terrain::bench::bench_terrain_load_cpu -- --ignored --nocapture で実行"]
fn bench_terrain_load_cpu() {
    let layers = TerrainLayerSet::default();

    for cells in BENCH_CELL_COUNTS {
        let settings = bench_settings(cells);
        // 全チャンク同一形状（起伏あり）でよい。狙いは 1 枚の絶対コスト×枚数の直並列差。
        let chunks: Vec<TerrainChunkData> = (0..BENCH_LOAD_CHUNKS)
            .map(|_| bench_chunk(&settings))
            .collect();

        // ── 直列（旧ロード経路相当）──
        let mut t_serial = std::time::Duration::ZERO;
        for _ in 0..BENCH_ITERS {
            let t = Instant::now();
            let out: Vec<(usize, usize)> = chunks
                .iter()
                .map(|c| build_one_chunk_cpu(c, &settings, &layers))
                .collect();
            t_serial += t.elapsed();
            std::hint::black_box(out);
        }

        // ── 並列（新ロード経路：par_iter）──
        let mut t_par = std::time::Duration::ZERO;
        for _ in 0..BENCH_ITERS {
            let t = Instant::now();
            let out: Vec<(usize, usize)> = chunks
                .par_iter()
                .map(|c| build_one_chunk_cpu(c, &settings, &layers))
                .collect();
            t_par += t.elapsed();
            std::hint::black_box(out);
        }

        let serial_ms = avg_ms(t_serial, BENCH_ITERS);
        let par_ms = avg_ms(t_par, BENCH_ITERS);
        println!(
            "[BENCH load] cells={cells:>3} chunks={BENCH_LOAD_CHUNKS} \
             serial={serial_ms:.2}ms parallel={par_ms:.2}ms speedup={:.2}x",
            serial_ms / par_ms.max(f64::EPSILON),
        );
    }
}

/// 並列メッシュ生成の出力が直列と**完全一致**することを固定する（決定性ロック）。
///
/// ロード経路を `par_iter` へ切り替えた根拠は「`build_chunk_cpu_model` は純粋関数で、
/// rayon の `IndexedParallelIterator` が入力順を保存する」こと。ここでは複数チャンクを
/// 直列 `map` と並列 `par_iter().map()` の両方で作り、三角形数・頂点数の列が一致する
/// ことを検証して、この不変条件が将来のリファクタで壊れないよう固定する。
#[test]
fn parallel_mesh_matches_serial() {
    let layers = TerrainLayerSet::default();
    // 分割数は代表として既定 32 で十分（並びの決定性は cells に依存しない）。
    let settings = bench_settings(BENCH_CELL_COUNTS[0]);
    // チャンクごとに少し形の違う密度にして「順序が入れ替わったら検知できる」ようにする。
    let chunks: Vec<TerrainChunkData> = (0..BENCH_LOAD_CHUNKS)
        .map(|i| {
            let extent = settings.chunk_extent();
            let freq = std::f32::consts::TAU / (extent * 0.25);
            let mid = extent * 0.5;
            // i でわずかに位相をずらし、各チャンクの三角形数が別々になるようにする。
            let phase = i as f32 * 0.13;
            TerrainChunkData::from_fn(&settings, ChunkCoord::new(0, 0, 0), move |x, y, z| {
                let height = mid
                    + ((x * freq) + phase).sin() * (extent * 0.1)
                    + ((z * freq) + phase).cos() * (extent * 0.1);
                y - height
            })
        })
        .collect();

    let serial: Vec<(usize, usize)> = chunks
        .iter()
        .map(|c| build_one_chunk_cpu(c, &settings, &layers))
        .collect();
    let parallel: Vec<(usize, usize)> = chunks
        .par_iter()
        .map(|c| build_one_chunk_cpu(c, &settings, &layers))
        .collect();

    assert_eq!(
        serial, parallel,
        "並列メッシュ生成の出力（三角形数・頂点数の列）が直列と一致しません（順序保存が壊れています）"
    );
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
            let pairs: Vec<_> = mesh
                .edges
                .iter()
                .map(|e| interp_vertex_paint(&chunk, e))
                .collect();
            paint = pairs.iter().map(|p| p.0).collect();
            paint_amount = pairs.iter().map(|p| p.1).collect();
            t_recalc += t.elapsed();
        }

        // ── ② レイヤ重み → 頂点カラー＋パレット ──
        let mut t_colors = std::time::Duration::ZERO;
        for _ in 0..BENCH_ITERS {
            let t = Instant::now();
            let _ = compute_layer_colors(
                &mesh.positions,
                &mesh.normals,
                &paint,
                &paint_amount,
                BENCH_ORIGIN,
                &layers,
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
