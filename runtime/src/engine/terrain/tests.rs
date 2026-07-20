// ============================================================
//  terrain/tests.rs — 地形モジュールのユニットテスト
//
//  1. 球 SDF の水密性（すべての辺が正確に 2 三角形で共有される）
//  2. 法線の向き（各頂点法線が球中心から放射状に外向き）
//  3. 境界同期（隣接 2 チャンクの共有面がビット一致・継ぎ目連続）
//  4. tvox ラウンドトリップ（ビット一致・破損検出）
// ============================================================

use std::collections::HashMap;

use super::brush::{apply, chunks_in_brush_aabb, BrushOp, SampleField, SphereBrush};
use super::chunk_coord::ChunkCoord;
use super::chunk_data::TerrainChunkData;
use super::heightmap::HeightmapField;
use super::marching_cubes::generate_standalone;
use super::settings::TerrainSettings;
use super::tvox::{read_chunk, write_chunk, TvoxError, TVOX_MAGIC};

// ─── テスト用定数（マジックナンバー回避） ───────────────────────────────────
/// 球 SDF テストの球中心（ローカル座標メートル、チャンク中央）
const SPHERE_CENTER: [f32; 3] = [8.0, 8.0, 8.0];
/// 球 SDF テストの半径（チャンク内に完全に収まる）
const SPHERE_RADIUS: f32 = 5.0;

/// density = |p - center| - radius の符号付き距離場でチャンクを埋める。
/// inside(<0)=solid, outside(>0)=air（規約に一致）。
fn make_sphere_chunk(settings: &TerrainSettings) -> TerrainChunkData {
    let mut chunk = TerrainChunkData::new_filled(settings, 0.0);
    let s = settings.samples_per_axis();
    let voxel = settings.voxel_size;
    for iz in 0..s {
        for iy in 0..s {
            for ix in 0..s {
                // サンプルのローカルワールド位置。
                let p = [ix as f32 * voxel, iy as f32 * voxel, iz as f32 * voxel];
                let dx = p[0] - SPHERE_CENTER[0];
                let dy = p[1] - SPHERE_CENTER[1];
                let dz = p[2] - SPHERE_CENTER[2];
                let dist = (dx * dx + dy * dy + dz * dz).sqrt();
                chunk.set_sample(ix, iy, iz, dist - SPHERE_RADIUS);
            }
        }
    }
    chunk
}

/// テスト1：球メッシュが水密（closed）であることを検証する。
#[test]
fn sphere_mesh_is_watertight() {
    let settings = TerrainSettings::default();
    let chunk = make_sphere_chunk(&settings);
    let mesh = generate_standalone(&chunk, &settings);

    // 三角形が生成されていること。
    assert!(mesh.triangle_count() > 0, "mesh should be non-empty");

    // ─── 無向辺ごとの出現回数を数える ───
    let mut edge_count: HashMap<(u32, u32), u32> = HashMap::new();
    for tri in mesh.indices.chunks_exact(3) {
        let verts = [tri[0], tri[1], tri[2]];
        for k in 0..3 {
            let a = verts[k];
            let b = verts[(k + 1) % 3];
            // 無向辺 = (小, 大)。
            let key = if a < b { (a, b) } else { (b, a) };
            *edge_count.entry(key).or_insert(0) += 1;
        }
    }

    // ─── すべての辺が正確に 2 回共有されていること（closed manifold） ───
    for (&(a, b), &count) in &edge_count {
        assert_eq!(
            count, 2,
            "edge ({a},{b}) shared {count} times, expected 2 (mesh not watertight)"
        );
    }
}

/// テスト2：各頂点法線が球中心から放射状に外向きであることを検証する。
#[test]
fn sphere_normals_point_outward() {
    let settings = TerrainSettings::default();
    let chunk = make_sphere_chunk(&settings);
    let mesh = generate_standalone(&chunk, &settings);

    let mut ok = 0usize;
    let total = mesh.positions.len();
    for (pos, nrm) in mesh.positions.iter().zip(mesh.normals.iter()) {
        // 中心からの放射方向。
        let dir = [
            pos[0] - SPHERE_CENTER[0],
            pos[1] - SPHERE_CENTER[1],
            pos[2] - SPHERE_CENTER[2],
        ];
        let len = (dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]).sqrt();
        if len <= f32::EPSILON {
            continue;
        }
        let d = (nrm[0] * dir[0] + nrm[1] * dir[1] + nrm[2] * dir[2]) / len;
        if d > 0.0 {
            ok += 1;
        }
    }

    // ─── ほぼすべて（>=98%）の頂点で外向きであること ───
    let ratio = ok as f32 / total as f32;
    assert!(
        ratio >= 0.98,
        "only {ok}/{total} ({:.1}%) normals point outward",
        ratio * 100.0
    );
}

// ─── テスト3 用：隣接 2 チャンクを束ねる SampleField 実装 ─────────────────────

/// x 方向に隣接する 2 チャンク (0,0,0) と (1,0,0) を束ねたグローバル場。
/// グローバル x が [0..C] は chunk0（ローカル x）、[C..2C] は chunk1（ローカル x-C）。
/// x=C（境界面）は両チャンクが共有する。
struct TwoChunkField {
    settings: TerrainSettings,
    chunk0: TerrainChunkData,
    chunk1: TerrainChunkData,
}

impl TwoChunkField {
    fn new(settings: TerrainSettings) -> Self {
        // 両チャンクとも平坦地面で初期化。
        let chunk0 = TerrainChunkData::from_ground_plane(&settings, ChunkCoord::new(0, 0, 0));
        let chunk1 = TerrainChunkData::from_ground_plane(&settings, ChunkCoord::new(1, 0, 0));
        Self { settings, chunk0, chunk1 }
    }

    /// y,z を [0, C] に、x はそのまま返す（境界外読みの安全化）。
    fn clamp_yz(&self, g: i32) -> usize {
        let s = self.settings.samples_per_axis() as i32;
        g.clamp(0, s - 1) as usize
    }
}

impl SampleField for TwoChunkField {
    fn settings(&self) -> &TerrainSettings {
        &self.settings
    }

    fn read_global(&self, gx: i32, gy: i32, gz: i32) -> f32 {
        let c = self.settings.chunk_cells as i32; // 1 軸のセル数（=境界ローカルインデックス）
        let ly = self.clamp_yz(gy);
        let lz = self.clamp_yz(gz);
        if gx <= c {
            // chunk0 側（ローカル x = gx）。
            let lx = gx.clamp(0, c) as usize;
            self.chunk0.sample(lx, ly, lz)
        } else {
            // chunk1 側（ローカル x = gx - c）。
            let lx = (gx - c).clamp(0, c) as usize;
            self.chunk1.sample(lx, ly, lz)
        }
    }

    fn write_global(&mut self, gx: i32, gy: i32, gz: i32, v: f32) {
        let c = self.settings.chunk_cells as i32;
        let ly = self.clamp_yz(gy);
        let lz = self.clamp_yz(gz);
        // chunk0 が所有するなら書く（gx in [0, c]）。
        if (0..=c).contains(&gx) {
            self.chunk0.set_sample(gx as usize, ly, lz, v);
        }
        // chunk1 が所有するなら書く（gx in [c, 2c]）→ 境界 gx==c は両方へ。
        if (c..=2 * c).contains(&gx) {
            self.chunk1.set_sample((gx - c) as usize, ly, lz, v);
        }
    }

    fn world_of_global(&self, gx: i32, gy: i32, gz: i32) -> [f32; 3] {
        // グローバルサンプル g のワールド位置 = g * voxel_size。
        let v = self.settings.voxel_size;
        [gx as f32 * v, gy as f32 * v, gz as f32 * v]
    }
}

/// テスト3：境界同期。ブラシが継ぎ目をまたいでも共有サンプルがビット一致し、
/// 両チャンクの共有面メッシュ頂点が一致することを検証する。
#[test]
fn boundary_samples_stay_synced() {
    let settings = TerrainSettings::default();
    let c = settings.chunk_cells as i32;
    let voxel = settings.voxel_size;
    let mut field = TwoChunkField::new(settings.clone());

    // ─── 境界面（world x = c*voxel = 16）を中心に Add ブラシを適用 ───
    let seam_x = c as f32 * voxel; // 境界のワールド x
    let brush = SphereBrush {
        center: [seam_x, 8.0, 8.0],
        radius: 3.0,
        strength: 20.0,
    };
    let touched = apply(&mut field, &brush, BrushOp::Add, 1.0);
    // 両チャンクが編集対象に含まれること。
    assert!(touched.contains(&ChunkCoord::new(0, 0, 0)));
    assert!(touched.contains(&ChunkCoord::new(1, 0, 0)));

    // ─── 共有境界サンプル（chunk0 の x=c と chunk1 の x=0）がビット一致 ───
    let s = settings.samples_per_axis();
    for iz in 0..s {
        for iy in 0..s {
            let a = field.chunk0.sample(c as usize, iy, iz);
            let b = field.chunk1.sample(0, iy, iz);
            assert_eq!(
                a.to_bits(),
                b.to_bits(),
                "boundary sample mismatch at (iy={iy}, iz={iz}): {a} vs {b}"
            );
        }
    }

    // ─── 継ぎ目メッシュ連続：両チャンクの共有面上の頂点(y,z)集合が一致 ───
    let mesh0 = generate_standalone(&field.chunk0, &settings);
    let mesh1 = generate_standalone(&field.chunk1, &settings);
    let face_x0 = c as f32 * voxel; // chunk0 の +x 面ローカル x
    let eps = voxel * 1e-3;

    // (y,z) を丸めてキー化するヘルパ。
    let key = |p: [f32; 3]| -> (i64, i64) {
        ((p[1] / eps).round() as i64, (p[2] / eps).round() as i64)
    };
    let mut face0 = std::collections::HashSet::new();
    for p in &mesh0.positions {
        if (p[0] - face_x0).abs() < eps {
            face0.insert(key(*p));
        }
    }
    let mut face1 = std::collections::HashSet::new();
    for p in &mesh1.positions {
        if p[0].abs() < eps {
            face1.insert(key(*p));
        }
    }
    assert!(!face0.is_empty(), "chunk0 should have seam-face vertices");
    assert_eq!(face0, face1, "seam-face vertex (y,z) sets must match");
}

/// テスト4：tvox の書き込み→読み込みでビット一致し、破損を検出する。
#[test]
fn tvox_round_trip_and_corruption() {
    let settings = TerrainSettings::default();
    let coord = ChunkCoord::new(3, -2, 7);

    // ─── 変化に富んだ密度でチャンクを作る ───
    let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);
    let s = settings.samples_per_axis();
    for iz in 0..s {
        for iy in 0..s {
            for ix in 0..s {
                // 負値・小数を含むパターン。
                let v = ix as f32 * 0.1 - iy as f32 * 0.37 + iz as f32 * 1.23;
                chunk.set_sample(ix, iy, iz, v);
            }
        }
    }

    // ─── ラウンドトリップ ───
    let bytes = write_chunk(&chunk, coord, &settings);
    let (restored, rcoord) = read_chunk(&bytes).expect("read_chunk should succeed");

    assert_eq!(rcoord, coord, "coord must round-trip");
    assert_eq!(
        restored.samples_per_axis(),
        chunk.samples_per_axis(),
        "dims must match"
    );
    assert_eq!(
        restored.raw_density().len(),
        chunk.raw_density().len(),
        "sample count must match"
    );
    // 全サンプルがビット一致。
    for (a, b) in chunk.raw_density().iter().zip(restored.raw_density().iter()) {
        assert_eq!(a.to_bits(), b.to_bits(), "density sample bit mismatch");
    }

    // ─── 破損マジック → BadMagic ───
    //   Ok 側の TerrainChunkData は PartialEq を持たないため matches! で判定する。
    let mut bad_magic = bytes.clone();
    bad_magic[0] = TVOX_MAGIC[0].wrapping_add(1);
    assert!(matches!(read_chunk(&bad_magic), Err(TvoxError::BadMagic)));

    // ─── 誤バージョン → BadVersion ───
    let mut bad_ver = bytes.clone();
    // version フィールド（オフセット4）を 999 に書き換える。
    bad_ver[4..8].copy_from_slice(&999u32.to_le_bytes());
    assert!(matches!(read_chunk(&bad_ver), Err(TvoxError::BadVersion)));
}

/// テスト5：undo スナップショット往復（set_raw_density で editable-before を復元できること）。
///
/// terrain 専用 undo/redo は「編集前密度を丸ごと Vec<f32> として控え、undo 時に
/// set_raw_density で書き戻す」方式（terrain_ops.rs の TerrainEdit）。その往復が
/// ビット一致で成立することを検証する。
#[test]
fn raw_density_snapshot_round_trip() {
    let settings = TerrainSettings::default();
    let chunk = make_sphere_chunk(&settings);

    // ─── 編集前を控える ───
    let before: Vec<f32> = chunk.raw_density().to_vec();

    // ─── 数点を変更する（ブラシ編集を模擬）───
    let mut edited = chunk.clone();
    let s = settings.samples_per_axis();
    edited.set_sample(0, 0, 0, 123.456);
    edited.set_sample(s / 2, s / 2, s / 2, -78.9);
    edited.set_sample(s - 1, s - 1, s - 1, 42.0);
    // 編集後は before と異なっていること（テストの前提が成立していることの確認）。
    assert_ne!(
        before.iter().map(|f| f.to_bits()).collect::<Vec<_>>(),
        edited.raw_density().iter().map(|f| f.to_bits()).collect::<Vec<_>>(),
        "precondition failed: edit did not change density"
    );

    // ─── set_raw_density で before を書き戻す（undo 相当）───
    edited.set_raw_density(before.clone());

    // ─── 全サンプルがビット一致で復元されていること ───
    for (a, b) in before.iter().zip(edited.raw_density().iter()) {
        assert_eq!(a.to_bits(), b.to_bits(), "density sample bit mismatch after undo restore");
    }
}

/// テスト6：ハイトマップ→SDF 変換。既知の 1 軸グラデーションでバイリニア補間の
/// 中点値と、density_at の符号反転（worldY と height の大小関係）を検証する。
/// image クレートを使わず、正規化済み luma01 配列を直接組んで HeightmapField をテストする。
#[test]
fn heightmap_field_bilinear_and_density_sign() {
    // w=2,h=1 の 1 軸グラデーション: x=0 で輝度0.0（高さ0）、x=1 で輝度1.0（高さ=height_scale）。
    let field = HeightmapField {
        luma01: vec![0.0, 1.0],
        w: 2,
        h: 1,
        footprint_w: 10.0,
        footprint_d: 10.0,
        height_scale: 4.0,
    };

    // ─── 中点（world x=5, フットプリント中央）のバイリニア高さは 0.5*4.0=2.0 に近いこと ───
    let mid_height = field.height_at(5.0, 5.0);
    assert!(
        (mid_height - 2.0).abs() < 1e-3,
        "mid height should be ~2.0, was {mid_height}"
    );

    // ─── worldY が height より大きい → density>0（AIR の規約） ───
    assert!(
        field.density_at(5.0, 3.0, 5.0) > 0.0,
        "worldY above height should be AIR (density>0)"
    );
    // ─── worldY が height より小さい → density<0（SOLID の規約） ───
    assert!(
        field.density_at(5.0, 1.0, 5.0) < 0.0,
        "worldY below height should be SOLID (density<0)"
    );
}

/// テスト7：chunks_in_brush_aabb がチャンクの継ぎ目をまたぐブラシで両側のチャンクを
/// 返すこと（apply() の touched チャンク集合と整合する superset であること）を確認する。
#[test]
fn chunks_in_brush_aabb_covers_seam() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent(); // チャンク1辺（既定16.0m）

    // 境界（x=extent）をまたぐ位置にブラシを置く。
    let brush = SphereBrush {
        center: [extent, 8.0, 8.0],
        radius: 3.0,
        strength: 1.0,
    };
    let coords = chunks_in_brush_aabb(&brush, &settings);

    assert!(coords.contains(&ChunkCoord::new(0, 0, 0)), "should include chunk on the near side of the seam");
    assert!(coords.contains(&ChunkCoord::new(1, 0, 0)), "should include chunk on the far side of the seam");
}

/// 1 チャンク（33³ サンプル）の marching cubes 再生成時間を実測する計測用テスト。
///
/// 通常のテスト実行では走らせず（`#[ignore]`）、`cargo test --release mc_regen_timing
/// -- --ignored --nocapture` で実測する。地形編集（ブラシ）1 回あたりの再メッシュコストの目安。
#[test]
#[ignore]
fn mc_regen_timing() {
    let settings = TerrainSettings::default();
    let chunk = make_sphere_chunk(&settings);
    // ─── ウォームアップ（キャッシュ・分岐予測を温める）───
    for _ in 0..8 {
        let _ = generate_standalone(&chunk, &settings);
    }
    // ─── 本計測 ───
    let iterations: u32 = 200;
    let start = std::time::Instant::now();
    let mut tri_total = 0usize;
    for _ in 0..iterations {
        let mesh = generate_standalone(&chunk, &settings);
        tri_total += mesh.indices.len() / 3;
    }
    let elapsed = start.elapsed();
    let per = elapsed / iterations;
    eprintln!(
        "[terrain mc_regen_timing] 1チャンク再生成 平均 {:?}（{} 回平均, 三角形 {}）",
        per,
        iterations,
        tri_total / iterations as usize,
    );
}
