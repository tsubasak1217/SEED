// ============================================================
//  terrain/tests_simplify.rs — その場デシメート（simplify.rs）専用のユニットテスト
//
//  ここで守る不変条件は 4 つ。
//    1. チャンク境界頂点は 1 つも消えず、位置も動かない（＝継ぎ目が開かない）。
//    2. 出力インデックスが必ず出力頂点数の範囲内で、縮退・重複面を含まない。
//    3. 頂点属性（法線・スプラット・由来辺）の長さが位置と常に一致する。
//    4. 平坦なメッシュは大きく削減される（＝アルゴリズムが実際に働いている）。
// ============================================================

use std::collections::HashSet;

use super::chunk_data::TerrainChunkData;
use super::marching_cubes::{TerrainMesh, generate, generate_standalone};
use super::settings::TerrainSettings;
use super::simplify::{is_boundary_vertex, simplify_mesh};

// ─── テスト用の地形（密度場）を組むヘルパ ─────────────────────────────────

/// 小さめのテスト設定（1 チャンク = 16 セル × 0.5m = 8m 角）。
fn test_settings() -> TerrainSettings {
    TerrainSettings {
        chunk_cells: 16,
        voxel_size: 0.5,
        ..TerrainSettings::default()
    }
}

/// 指定ワールド高さに水平な地面を持つチャンクを作る。
///
/// 密度 = y - surface_y（`density < iso(0)` が SOLID）。チャンク原点は
/// `origin_y` で、ローカル格子座標からワールド Y を組み立てる。
fn flat_ground_chunk(settings: &TerrainSettings, origin_y: f32, surface_y: f32) -> TerrainChunkData {
    let s = settings.samples_per_axis();
    let mut chunk = TerrainChunkData::new_filled(settings, 0.0);
    for iz in 0..s {
        for iy in 0..s {
            let wy = origin_y + iy as f32 * settings.voxel_size;
            for ix in 0..s {
                chunk.set_sample(ix, iy, iz, wy - surface_y);
            }
        }
    }
    chunk
}

/// なだらかな起伏（正弦波）の地面を持つチャンクを作る。
///
/// 平坦一様だと「削って当然」の退化ケースなので、形状を保つ必要がある入力も併せて試す。
fn wavy_ground_chunk(
    settings: &TerrainSettings,
    origin: [f32; 3],
    base_y: f32,
    amp: f32,
) -> TerrainChunkData {
    let s = settings.samples_per_axis();
    let mut chunk = TerrainChunkData::new_filled(settings, 0.0);
    for iz in 0..s {
        let wz = origin[2] + iz as f32 * settings.voxel_size;
        for iy in 0..s {
            let wy = origin[1] + iy as f32 * settings.voxel_size;
            for ix in 0..s {
                let wx = origin[0] + ix as f32 * settings.voxel_size;
                let h = base_y + amp * ((wx * 0.7).sin() + (wz * 0.5).cos());
                chunk.set_sample(ix, iy, iz, wy - h);
            }
        }
    }
    chunk
}

/// 境界頂点の集合（位置をビット列にして順序非依存で比較できる形にする）。
///
/// f32 の位置をそのままキーにできないので `to_bits` で正確一致比較にする。
/// デシメートは境界頂点の位置を **1 ビットも** 動かさないので、これで十分厳密に検証できる。
fn boundary_key_set(mesh: &TerrainMesh, extent: f32) -> HashSet<(u32, u32, u32)> {
    mesh.positions
        .iter()
        .filter(|p| is_boundary_vertex(**p, extent))
        .map(|p| (p[0].to_bits(), p[1].to_bits(), p[2].to_bits()))
        .collect()
}

/// 出力メッシュの基本整合（インデックス範囲・縮退なし・属性長一致）を検査する。
fn assert_mesh_is_consistent(mesh: &TerrainMesh, label: &str) {
    let n = mesh.positions.len();
    assert_eq!(mesh.indices.len() % 3, 0, "{label}: インデックスが 3 の倍数でない");
    for (i, &idx) in mesh.indices.iter().enumerate() {
        assert!(
            (idx as usize) < n,
            "{label}: インデックス {idx}（{i} 番目）が頂点数 {n} を超えている"
        );
    }
    for t in mesh.indices.chunks_exact(3) {
        assert!(
            t[0] != t[1] && t[1] != t[2] && t[2] != t[0],
            "{label}: 縮退三角形が残っている: {t:?}"
        );
    }
    assert_eq!(mesh.normals.len(), n, "{label}: 法線の数が位置と一致しない");
    assert_eq!(mesh.paint.len(), n, "{label}: スプラットの数が位置と一致しない");
    assert_eq!(
        mesh.paint_amount.len(),
        n,
        "{label}: ペイント量の数が位置と一致しない"
    );
    assert_eq!(mesh.edges.len(), n, "{label}: 由来辺の数が位置と一致しない");
    // 法線は単位ベクトルのまま引き継がれること（潰しは位置も法線も新造しない）。
    for (i, nrm) in mesh.normals.iter().enumerate() {
        let len = (nrm[0] * nrm[0] + nrm[1] * nrm[1] + nrm[2] * nrm[2]).sqrt();
        assert!(
            (len - 1.0).abs() < 1.0e-3,
            "{label}: 頂点 {i} の法線が単位長でない（len={len}）"
        );
    }
}

/// 同じ面（頂点 3 つの集合）が 2 枚以上出ていないこと。
fn assert_no_duplicate_faces(mesh: &TerrainMesh, label: &str) {
    let mut seen: HashSet<[u32; 3]> = HashSet::new();
    for t in mesh.indices.chunks_exact(3) {
        let mut k = [t[0], t[1], t[2]];
        k.sort_unstable();
        assert!(seen.insert(k), "{label}: 同一の三角形が重複している: {k:?}");
    }
}

// ─── テスト本体 ─────────────────────────────────────────────────────────

/// 強度 0 は完全な無操作（ビット単位で同じメッシュが返る）。
#[test]
fn zero_strength_is_identity() {
    let settings = test_settings();
    let chunk = wavy_ground_chunk(&settings, [0.0, 0.0, 0.0], 4.0, 0.8);
    let mesh = generate_standalone(&chunk, &settings);
    let (out, stats) = simplify_mesh(&mesh, settings.chunk_extent(), 0.0);
    assert_eq!(out.positions, mesh.positions, "強度 0 で頂点が変わっている");
    assert_eq!(out.indices, mesh.indices, "強度 0 でインデックスが変わっている");
    assert_eq!(stats.vertices_before, stats.vertices_after);
    assert_eq!(stats.vertex_reduction(), 0.0);
}

/// 空メッシュを渡しても壊れない（全 AIR チャンク相当）。
#[test]
fn empty_mesh_is_safe() {
    let empty = TerrainMesh::default();
    let (out, stats) = simplify_mesh(&empty, 8.0, 1.0);
    assert!(out.positions.is_empty());
    assert!(out.indices.is_empty());
    assert_eq!(stats.vertices_before, 0);
    assert_eq!(stats.vertex_reduction(), 0.0, "空メッシュの削減率は 0");
}

/// **平面メッシュは大幅に削減される**（アルゴリズムが実際に効いていることの確認）。
///
/// 完全に平らな地面はどの頂点を潰しても二次誤差 0 なので、
/// 境界を除くほぼ全頂点が消えるはずである。
#[test]
fn flat_mesh_is_reduced_drastically() {
    let settings = test_settings();
    let chunk = flat_ground_chunk(&settings, 0.0, 4.0);
    let mesh = generate_standalone(&chunk, &settings);
    assert!(mesh.positions.len() > 100, "テスト前提: 十分な頂点がある");

    let (out, stats) = simplify_mesh(&mesh, settings.chunk_extent(), 1.0);
    assert_mesh_is_consistent(&out, "平面メッシュ");
    assert_no_duplicate_faces(&out, "平面メッシュ");
    assert!(
        stats.vertex_reduction() > 0.5,
        "平面メッシュの削減率が低すぎる: {:.3}（{} -> {} 頂点）",
        stats.vertex_reduction(),
        stats.vertices_before,
        stats.vertices_after
    );
    assert!(
        stats.triangles_after < stats.triangles_before,
        "三角形が減っていない"
    );
}

/// 起伏のあるメッシュでも整合を崩さずに削減できる。
#[test]
fn wavy_mesh_reduces_and_stays_consistent() {
    let settings = test_settings();
    let chunk = wavy_ground_chunk(&settings, [0.0, 0.0, 0.0], 4.0, 0.9);
    let mesh = generate_standalone(&chunk, &settings);

    for strength in [0.25f32, 0.5, 0.75, 1.0] {
        let (out, stats) = simplify_mesh(&mesh, settings.chunk_extent(), strength);
        let label = format!("起伏メッシュ strength={strength}");
        assert_mesh_is_consistent(&out, &label);
        assert_no_duplicate_faces(&out, &label);
        assert!(
            stats.vertices_after <= stats.vertices_before,
            "{label}: 頂点が増えている"
        );
        assert!(
            stats.vertices_after > 0,
            "{label}: 頂点が全部消えた（潰しすぎ）"
        );
    }
}

/// 強度を上げるほど削減が進む（単調性。順位付けが機能している証拠）。
#[test]
fn stronger_setting_removes_more_vertices() {
    let settings = test_settings();
    let chunk = wavy_ground_chunk(&settings, [0.0, 0.0, 0.0], 4.0, 0.9);
    let mesh = generate_standalone(&chunk, &settings);
    let extent = settings.chunk_extent();

    let (_, weak) = simplify_mesh(&mesh, extent, 0.2);
    let (_, strong) = simplify_mesh(&mesh, extent, 0.9);
    assert!(
        strong.vertices_after <= weak.vertices_after,
        "強度を上げたのに削減が進んでいない: weak={} strong={}",
        weak.vertices_after,
        strong.vertices_after
    );
}

/// **境界頂点は 1 つも消えず、位置も動かない**（継ぎ目保証の中核）。
#[test]
fn boundary_vertices_are_never_removed_or_moved() {
    let settings = test_settings();
    let chunk = wavy_ground_chunk(&settings, [0.0, 0.0, 0.0], 4.0, 0.9);
    let mesh = generate_standalone(&chunk, &settings);
    let extent = settings.chunk_extent();

    let before = boundary_key_set(&mesh, extent);
    assert!(!before.is_empty(), "テスト前提: 境界頂点が存在する");

    let (out, _) = simplify_mesh(&mesh, extent, 1.0);
    let after = boundary_key_set(&out, extent);
    assert_eq!(
        before, after,
        "境界頂点集合が変化した（継ぎ目が開く）: before={} after={}",
        before.len(),
        after.len()
    );
}

/// **隣り合う 2 チャンクの境界頂点集合が、デシメート後も一致する**。
///
/// 継ぎ目保証の本命テスト。X 方向に隣接する 2 チャンクを同じ密度関数から作り、
/// 共有面（左チャンクの x=extent 面 ＝ 右チャンクの x=0 面）に載る頂点集合を
/// ワールド座標へ直して突き合わせる。強度を変えても一致し続けること。
#[test]
fn adjacent_chunks_share_identical_boundary_after_simplify() {
    let settings = test_settings();
    let extent = settings.chunk_extent();
    // 同一の密度関数から、原点だけずらして 2 チャンクを作る。
    let left = wavy_ground_chunk(&settings, [0.0, 0.0, 0.0], 4.0, 0.9);
    let right = wavy_ground_chunk(&settings, [extent, 0.0, 0.0], 4.0, 0.9);

    // 隣接サンプラ付きで生成する（境界の勾配・位置が隣と一致する本番経路と同じ）。
    let base_y = 4.0f32;
    let amp = 0.9f32;
    let density = |wx: f32, wy: f32, wz: f32| -> f32 {
        wy - (base_y + amp * ((wx * 0.7).sin() + (wz * 0.5).cos()))
    };
    let v = settings.voxel_size;
    let left_mesh = generate(&left, &settings, |lx, ly, lz| {
        density(lx as f32 * v, ly as f32 * v, lz as f32 * v)
    });
    let right_mesh = generate(&right, &settings, |lx, ly, lz| {
        density(extent + lx as f32 * v, ly as f32 * v, lz as f32 * v)
    });

    // 共有面上の頂点をワールド座標のビット列で集める。
    //   左チャンク: ローカル x == extent、右チャンク: ローカル x == 0。
    //   どちらもワールド x == extent なので、Y/Z だけを比較すればよい。
    let eps = extent * 1.0e-4;
    let shared_yz = |mesh: &TerrainMesh, local_x: f32| -> HashSet<(u32, u32)> {
        mesh.positions
            .iter()
            .filter(|p| (p[0] - local_x).abs() <= eps)
            .map(|p| (p[1].to_bits(), p[2].to_bits()))
            .collect()
    };

    let before_left = shared_yz(&left_mesh, extent);
    let before_right = shared_yz(&right_mesh, 0.0);
    assert!(!before_left.is_empty(), "テスト前提: 共有面に頂点がある");
    assert_eq!(
        before_left, before_right,
        "テスト前提: デシメート前から共有面の頂点が一致していること"
    );

    for strength in [0.3f32, 0.7, 1.0] {
        let (l, _) = simplify_mesh(&left_mesh, extent, strength);
        let (r, _) = simplify_mesh(&right_mesh, extent, strength);
        let after_left = shared_yz(&l, extent);
        let after_right = shared_yz(&r, 0.0);
        assert_eq!(
            after_left, after_right,
            "strength={strength}: 隣接チャンクの共有面頂点が食い違った（継ぎ目が開く）"
        );
        assert_eq!(
            after_left, before_left,
            "strength={strength}: 共有面の頂点がデシメートで消えた／動いた"
        );
    }
}

/// 由来辺（`TerrainVertexEdge`）が、生き残った頂点の元の値のまま運ばれること。
///
/// ハーフエッジコラプスの前提が崩れるとレイヤペイント高速パスが壊れるので、
/// 「出力の各頂点の (位置, 由来辺) の組が、入力のどこかに必ず同じ組で存在する」ことを見る。
#[test]
fn vertex_attributes_are_inherited_from_originals() {
    let settings = test_settings();
    let chunk = wavy_ground_chunk(&settings, [0.0, 0.0, 0.0], 4.0, 0.9);
    let mesh = generate_standalone(&chunk, &settings);
    let extent = settings.chunk_extent();

    // 入力の (位置ビット, 由来辺) 対応表。
    let src: std::collections::HashMap<(u32, u32, u32), (u16, u16, u16, u8, u32)> = mesh
        .positions
        .iter()
        .zip(mesh.edges.iter())
        .map(|(p, e)| {
            (
                (p[0].to_bits(), p[1].to_bits(), p[2].to_bits()),
                (e.lo[0], e.lo[1], e.lo[2], e.axis, e.t.to_bits()),
            )
        })
        .collect();

    let (out, _) = simplify_mesh(&mesh, extent, 0.8);
    for (p, e) in out.positions.iter().zip(out.edges.iter()) {
        let key = (p[0].to_bits(), p[1].to_bits(), p[2].to_bits());
        let got = (e.lo[0], e.lo[1], e.lo[2], e.axis, e.t.to_bits());
        assert_eq!(
            src.get(&key),
            Some(&got),
            "出力頂点が入力に無い／由来辺が書き換わっている: pos={p:?}"
        );
    }
}

/// 由来辺を持たないメッシュ（LOD>0 相当）でも落ちず、長さの不整合も作らない。
#[test]
fn mesh_without_edges_is_handled() {
    let settings = test_settings();
    let chunk = wavy_ground_chunk(&settings, [0.0, 0.0, 0.0], 4.0, 0.9);
    let mut mesh = generate_standalone(&chunk, &settings);
    // 由来辺だけを落とした状態（`build_chunk_cpu_model` が LOD>0 で作る形）。
    mesh.edges.clear();

    let (out, stats) = simplify_mesh(&mesh, settings.chunk_extent(), 0.8);
    assert!(out.edges.is_empty(), "無い由来辺が生えている");
    assert_eq!(out.normals.len(), out.positions.len());
    assert!(stats.vertices_after <= stats.vertices_before);
    // 由来辺以外の整合は通常どおり保たれること。
    for &idx in &out.indices {
        assert!((idx as usize) < out.positions.len());
    }
}

/// 境界判定そのものの単体確認（許容誤差の向き）。
#[test]
fn boundary_predicate_matches_faces_only() {
    let extent = 8.0f32;
    assert!(is_boundary_vertex([0.0, 3.0, 3.0], extent), "x=0 面は境界");
    assert!(is_boundary_vertex([3.0, 8.0, 3.0], extent), "y=extent 面は境界");
    assert!(is_boundary_vertex([3.0, 3.0, 8.0], extent), "z=extent 面は境界");
    assert!(!is_boundary_vertex([3.0, 3.0, 3.0], extent), "内部は境界でない");
    assert!(
        !is_boundary_vertex([0.01, 3.0, 3.0], extent),
        "許容誤差より離れた点は境界でない"
    );
}

/// 実運用サイズ（chunk_cells=32・voxel 0.5m＝16m 角）での所要時間と削減率の実測。
///
/// `#[ignore]` 付き。`cargo test -- --ignored --nocapture measure_realistic_chunk_cost`。
/// デシメートは全チャンク一括で走るため、1 チャンクの所要時間 × チャンク数が
/// そのままボタン押下の待ち時間になる。ここが跳ねていないかを見るための常設フック。
#[test]
#[ignore]
fn measure_realistic_chunk_cost() {
    let settings = TerrainSettings {
        chunk_cells: 32,
        voxel_size: 0.5,
        ..TerrainSettings::default()
    };
    let extent = settings.chunk_extent();
    let chunk = wavy_ground_chunk(&settings, [0.0, 0.0, 0.0], 8.0, 1.5);
    let mesh = generate_standalone(&chunk, &settings);
    for strength in [0.5f32, 1.0] {
        let t = std::time::Instant::now();
        let (_, s) = simplify_mesh(&mesh, extent, strength);
        println!(
            "[simplify] cells=32 strength={strength:.2}: 頂点 {} -> {} ({:.1}% 削減) 所要 {:.1}ms",
            s.vertices_before,
            s.vertices_after,
            s.vertex_reduction() * 100.0,
            t.elapsed().as_secs_f64() * 1000.0
        );
    }
}

/// 削減率の実測（`#[ignore]` 付き。通常のテスト実行では走らない）。
///
/// `cargo test -- --ignored --nocapture measure_reduction_rates` で数値を見る。
/// アルゴリズムを触ったときに「品質と削減率の釣り合い」を目視で確認するための常設フック
/// （`bench.rs` と同じ流儀）。
#[test]
#[ignore]
fn measure_reduction_rates() {
    let settings = test_settings();
    let extent = settings.chunk_extent();
    let cases: [(&str, TerrainChunkData); 3] = [
        ("平坦", flat_ground_chunk(&settings, 0.0, 4.0)),
        ("ゆるい起伏", wavy_ground_chunk(&settings, [0.0, 0.0, 0.0], 4.0, 0.4)),
        ("強い起伏", wavy_ground_chunk(&settings, [0.0, 0.0, 0.0], 4.0, 1.5)),
    ];
    for (label, chunk) in cases {
        let mesh = generate_standalone(&chunk, &settings);
        for strength in [0.25f32, 0.5, 0.75, 1.0] {
            let (_, s) = simplify_mesh(&mesh, extent, strength);
            println!(
                "[simplify] {label} strength={strength:.2}: 頂点 {} -> {} ({:.1}% 削減) / 三角形 {} -> {}",
                s.vertices_before,
                s.vertices_after,
                s.vertex_reduction() * 100.0,
                s.triangles_before,
                s.triangles_after
            );
        }
    }
}

// ─── 水密性（穴を開けない）インバリアント ───────────────────────────────

/// 内部に球状の空洞（洞窟）を持つ地面チャンクを作る。
///
/// 平坦・起伏だけでは「1 枚の高さ場」しか試せず、閉じた曲面（すべての辺が 2 枚の面に
/// 共有される形）が現れない。穴あきバグは閉曲面でこそ露見するため、空洞ケースを用意する。
fn cave_ground_chunk(
    settings: &TerrainSettings,
    base_y: f32,
    cave_center: [f32; 3],
    cave_radius: f32,
) -> TerrainChunkData {
    let s = settings.samples_per_axis();
    let v = settings.voxel_size;
    let mut chunk = TerrainChunkData::new_filled(settings, 0.0);
    for iz in 0..s {
        let z = iz as f32 * v;
        for iy in 0..s {
            let y = iy as f32 * v;
            for ix in 0..s {
                let x = ix as f32 * v;
                // 地面（y < base_y が solid）。
                let ground = y - base_y;
                // 空洞（球の内側が air = 正）。max で球の内側だけを air に彫る。
                let d = ((x - cave_center[0]).powi(2)
                    + (y - cave_center[1]).powi(2)
                    + (z - cave_center[2]).powi(2))
                .sqrt();
                let cave = cave_radius - d;
                chunk.set_sample(ix, iy, iz, ground.max(cave));
            }
        }
    }
    chunk
}

/// 各無向辺を参照する三角形の枚数を数える。
fn edge_face_counts(mesh: &TerrainMesh) -> std::collections::HashMap<(u32, u32), u32> {
    let mut counts: std::collections::HashMap<(u32, u32), u32> = std::collections::HashMap::new();
    for t in mesh.indices.chunks_exact(3) {
        for k in 0..3 {
            let (a, b) = (t[k], t[(k + 1) % 3]);
            let key = if a <= b { (a, b) } else { (b, a) };
            *counts.entry(key).or_insert(0) += 1;
        }
    }
    counts
}

/// 辺 → 共有面数を、辺の両端の **位置ビット列** をキーにして集める。
///
/// デシメート前後で頂点番号は変わるので、番号ではなく位置で突き合わせる。
/// 同じ位置ペアに複数の辺が対応した場合（＝同一位置の重複頂点があるメッシュ）は
/// 枚数を合算する。溶接後の見た目のトポロジを見たいのでこれが正しい。
type EdgePosKey = ((u32, u32, u32), (u32, u32, u32));
fn edge_face_counts_by_pos(mesh: &TerrainMesh) -> std::collections::HashMap<EdgePosKey, u32> {
    let key = |v: u32| {
        let p = mesh.positions[v as usize];
        (p[0].to_bits(), p[1].to_bits(), p[2].to_bits())
    };
    let mut out: std::collections::HashMap<EdgePosKey, u32> = std::collections::HashMap::new();
    for ((a, b), n) in edge_face_counts(mesh) {
        let (ka, kb) = (key(a), key(b));
        let k = if ka <= kb { (ka, kb) } else { (kb, ka) };
        *out.entry(k).or_insert(0) += n;
    }
    out
}

/// 荒れた（高周波・急峻な）地形チャンクを作る。
///
/// 実機のスカルプト済み地形は正弦波よりずっと荒く、MC が曖昧セル構成
/// （非多様体辺・薄いシート）を大量に踏む。穴あきはそこで出るので、
/// テストにも「荒い」ケースを入れる。
fn rough_ground_chunk(settings: &TerrainSettings, base_y: f32) -> TerrainChunkData {
    let s = settings.samples_per_axis();
    let v = settings.voxel_size;
    let mut chunk = TerrainChunkData::new_filled(settings, 0.0);
    for iz in 0..s {
        let z = iz as f32 * v;
        for iy in 0..s {
            let y = iy as f32 * v;
            for ix in 0..s {
                let x = ix as f32 * v;
                // 多周波の重ね合わせ。高周波成分がセルサイズ近辺の起伏を生む。
                let h = base_y
                    + 1.2 * (x * 0.9).sin() * (z * 1.1).cos()
                    + 0.6 * (x * 2.3 + 1.0).sin()
                    + 0.6 * (z * 2.7 + 2.0).cos()
                    + 0.35 * (x * 5.1).sin() * (z * 4.7).sin();
                chunk.set_sample(ix, iy, iz, y - h);
            }
        }
    }
    chunk
}

/// テストに使う全地形ケース。
fn watertight_cases(settings: &TerrainSettings) -> Vec<(&'static str, TerrainChunkData)> {
    vec![
        ("平坦", flat_ground_chunk(settings, 0.0, 4.0)),
        ("起伏", wavy_ground_chunk(settings, [0.0, 0.0, 0.0], 4.0, 0.9)),
        (
            "洞窟あり",
            cave_ground_chunk(settings, 5.0, [4.0, 3.0, 4.0], 2.0),
        ),
        ("荒い起伏", rough_ground_chunk(settings, 4.0)),
    ]
}

/// **デシメートは穴を開けない**（水密性インバリアント）。
///
/// 各辺（両端の位置で同定）について、デシメート前に 2 枚以上の面に共有されていた
/// ならデシメート後も 2 枚以上であること。1 枚に落ちた辺＝そこで面が 1 枚欠けた＝
/// 三角形サイズの穴が開いた、ということになる。
/// 地形チャンクはチャンク境界で切られた開曲面なので、元から 1 枚の辺（外周）は存在する。
/// それらは境界ロックにより消えも増えもしないはずで、判定対象外にする。
#[test]
fn simplify_never_opens_holes() {
    let settings = test_settings();
    let extent = settings.chunk_extent();
    for (label, chunk) in watertight_cases(&settings) {
        let mesh = generate_standalone(&chunk, &settings);
        assert!(mesh.positions.len() > 50, "{label}: テスト前提の頂点数が不足");
        let before = edge_face_counts_by_pos(&mesh);
        for strength in [0.25f32, 0.5, 0.75, 1.0] {
            let (out, _) = simplify_mesh(&mesh, extent, strength);
            let after = edge_face_counts_by_pos(&out);
            // デシメート後に残っている辺のうち、「前は閉じていたのに後で 1 枚」のもの。
            let opened: Vec<_> = after
                .iter()
                .filter(|(k, n)| **n < 2 && before.get(*k).copied().unwrap_or(0) >= 2)
                .collect();
            assert!(
                opened.is_empty(),
                "{label} strength={strength}: デシメートで {} 本の辺が開いた（穴）。例: {:?}",
                opened.len(),
                opened.iter().take(3).collect::<Vec<_>>()
            );
        }
    }
}

/// **ランダム密度場でのファズ（水密性の回帰テスト）**。
///
/// 解析的に滑らかな地形（正弦波・球）は MC が素直な多様体メッシュを出すので、
/// 折り返し（四面体構成）がほとんど現れず、穴あきバグを再現できなかった。
/// 実機のスカルプト済み地形はセルサイズ近辺で density が上下する荒い場であり、
/// そこで初めて四面体構成が大量に出る。この乱数地形はそれを再現するためのもの。
/// 固定シードの xorshift なので結果は完全に決定的（＝CI で揺れない）。
#[test]
fn fuzz_watertight() {
    let settings = TerrainSettings {
        chunk_cells: 16,
        voxel_size: 0.5,
        ..TerrainSettings::default()
    };
    let extent = settings.chunk_extent();
    let s = settings.samples_per_axis();
    let mut state: u64 = 0x1234_5678_9abc_def0;
    let mut rng = move || {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        (state >> 11) as f32 / (1u64 << 53) as f32
    };
    let mut fail = 0;
    let (mut sum_before, mut sum_after) = (0usize, 0usize);
    for seed in 0..40 {
        let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);
        for iz in 0..s {
            for iy in 0..s {
                for ix in 0..s {
                    // 完全ランダムだと穴だらけの薄膜になるので、高さ場＋強いノイズにする。
                    let y = iy as f32 * settings.voxel_size;
                    let h = 4.0 + 2.0 * (rng() - 0.5);
                    chunk.set_sample(ix, iy, iz, y - h);
                }
            }
        }
        let mesh = generate_standalone(&chunk, &settings);
        if mesh.positions.len() < 20 {
            continue;
        }
        let before = edge_face_counts_by_pos(&mesh);
        let nonmanifold_before = before.values().filter(|&&n| n > 2).count();
        let open_before = before.values().filter(|&&n| n < 2).count();
        for strength in [0.5f32, 1.0] {
            let (out, st) = simplify_mesh(&mesh, extent, strength);
            sum_before += st.vertices_before;
            sum_after += st.vertices_after;
            let label = format!("乱数地形 seed={seed} strength={strength}");
            assert_mesh_is_consistent(&out, &label);
            assert_no_duplicate_faces(&out, &label);
            assert_eq!(
                boundary_key_set(&out, extent),
                boundary_key_set(&mesh, extent),
                "{label}: 境界頂点集合が変化した"
            );
            let after = edge_face_counts_by_pos(&out);
            let opened = after
                .iter()
                .filter(|(k, n)| **n < 2 && before.get(*k).copied().unwrap_or(0) >= 2)
                .count();
            if opened > 0 {
                fail += 1;
                println!(
                    "seed={seed} strength={strength}: opened={opened} (入力: 非多様体辺={nonmanifold_before} 開辺={open_before})"
                );
            }
        }
    }
    println!(
        "fuzz_watertight: 失敗ケース {fail} / 80、合計頂点 {sum_before} -> {sum_after} ({:.1}% 削減)",
        (sum_before - sum_after) as f32 / sum_before as f32 * 100.0
    );
    assert_eq!(fail, 0, "ファズで穴が開いた");
}

// ─── 向き（巻き順）一貫性のインバリアント ───────────────────────────────

// 【採用しなかった判定について】
//   「2 枚の面が共有する辺は (a,b) と (b,a) の逆向きペアで現れる」という
//   組み合わせ的な向き一貫性は、**このエンジンでは不変条件にならない**。
//   マーチングキューブス出力そのものが乱数地形で 300 本規模の同方向ペアを持つ
//   （`push_triangle` は面ごとに独立して頂点法線と突き合わせて向きを決めるため）。
//   規約は組み合わせ的なものではなく「面ごとに平均頂点法線と突き合わせる」ものなので、
//   下の `backfacing_triangles` が唯一の正しい判定である。

/// **エンジンの巻き順規約に違反している（＝裏返っている）三角形**を数える。
///
/// 規約は `marching_cubes::push_triangle` が全三角形に課しているもので、
/// `dot(cross(b-a, c-a), 頂点法線の平均) <= 0`。
/// これが正のものは背面カリングで抜けて見え、下から覗くとその面だけが見える。
/// `terrain_gbuffer_write.wgsl` の front_facing 判定もこの規約に依存している。
///
/// マーチングキューブスの出力は構成上この違反が 0 枚なので、
/// デシメート後に 1 枚でも出たらデシメートが裏返したということ。
fn backfacing_triangles(mesh: &TerrainMesh) -> usize {
    let mut count = 0usize;
    for t in mesh.indices.chunks_exact(3) {
        let (pa, pb, pc) = (
            mesh.positions[t[0] as usize],
            mesh.positions[t[1] as usize],
            mesh.positions[t[2] as usize],
        );
        let ab = [pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2]];
        let ac = [pc[0] - pa[0], pc[1] - pa[1], pc[2] - pa[2]];
        let geo = [
            ab[1] * ac[2] - ab[2] * ac[1],
            ab[2] * ac[0] - ab[0] * ac[2],
            ab[0] * ac[1] - ab[1] * ac[0],
        ];
        let (na, nb, nc) = (
            mesh.normals[t[0] as usize],
            mesh.normals[t[1] as usize],
            mesh.normals[t[2] as usize],
        );
        let avg = [na[0] + nb[0] + nc[0], na[1] + nb[1] + nc[1], na[2] + nb[2] + nc[2]];
        if geo[0] * avg[0] + geo[1] * avg[1] + geo[2] * avg[2] > 0.0 {
            count += 1;
        }
    }
    count
}

/// **デシメートは三角形を裏返さない**（向き一貫性インバリアント・解析地形）。
///
/// 実機で見えていた「三角形状の抜け」は穴ではなく巻き順の反転だった。
/// 表からは背面カリングで抜けて見え、下から覗くとその面だけが見える、という症状である。
/// デシメート前に食い違っていた辺の本数を超えないことを要求する。
#[test]
fn simplify_never_flips_winding() {
    let settings = test_settings();
    let extent = settings.chunk_extent();
    for (label, chunk) in watertight_cases(&settings) {
        let mesh = generate_standalone(&chunk, &settings);
        assert_eq!(
            backfacing_triangles(&mesh),
            0,
            "{label}: テスト前提（MC 出力に規約違反の面は無い）"
        );
        for strength in [0.25f32, 0.5, 0.75, 1.0] {
            let (out, _) = simplify_mesh(&mesh, extent, strength);
            assert_eq!(
                backfacing_triangles(&out),
                0,
                "{label} strength={strength}: 規約違反（裏返り）の三角形が出た"
            );
        }
    }
}

/// **乱数密度場でも三角形を裏返さない**（向き一貫性の本命テスト）。
///
/// `fuzz_watertight` と同じ固定シードの乱数地形 40 個 × 強度 2 段に掛ける。
/// 荒い密度場でこそ薄い三角形が出て、幾何法線の符号判定が不安定になる。
#[test]
fn fuzz_orientation_is_preserved() {
    let settings = TerrainSettings {
        chunk_cells: 16,
        voxel_size: 0.5,
        ..TerrainSettings::default()
    };
    let extent = settings.chunk_extent();
    let s = settings.samples_per_axis();
    let mut state: u64 = 0x1234_5678_9abc_def0;
    let mut rng = move || {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        (state >> 11) as f32 / (1u64 << 53) as f32
    };
    let (mut fail, mut flipped_total) = (0usize, 0usize);
    for seed in 0..40 {
        let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);
        for iz in 0..s {
            for iy in 0..s {
                for ix in 0..s {
                    let y = iy as f32 * settings.voxel_size;
                    let h = 4.0 + 2.0 * (rng() - 0.5);
                    chunk.set_sample(ix, iy, iz, y - h);
                }
            }
        }
        let mesh = generate_standalone(&chunk, &settings);
        if mesh.positions.len() < 20 {
            continue;
        }
        assert_eq!(
            backfacing_triangles(&mesh),
            0,
            "seed={seed}: テスト前提（MC 出力に規約違反の面は無い）"
        );
        for strength in [0.5f32, 1.0] {
            let (out, _) = simplify_mesh(&mesh, extent, strength);
            let back = backfacing_triangles(&out);
            if back > 0 {
                fail += 1;
                flipped_total += back;
                println!("seed={seed} strength={strength}: 規約違反(裏面) 三角形 {back} 枚");
            }
        }
    }
    println!("fuzz_orientation: 失敗ケース {fail} / 80、増えた食い違い辺 合計 {flipped_total}");
    assert_eq!(fail, 0, "ファズで面が裏返った");
}
