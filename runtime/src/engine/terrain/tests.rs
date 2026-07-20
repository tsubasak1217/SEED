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
/// 巻き順判定で「縮退（面積ほぼ 0）」とみなす |cross(b-a, c-a)|² の閾値。
/// MC の辺補間で 3 頂点がほぼ同一点へ潰れた三角形は向きが定義できず、
/// ラスタライザでも面積 0 で描かれないため巻き順の検証対象から除く。
const DEGENERATE_AREA_EPSILON: f32 = 1.0e-18;

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

/// テスト2b：三角形の巻き順がエンジンのフロントフェイス規約に一致することを検証する。
///
/// 【規約】本エンジンは左手系（look_at_lh / perspective_lh）＋ front_face=Ccw /
/// cull_mode=Back。glTF ローダが右手系データを Z 反転で取り込み巻き順を据え置く結果、
/// エンジン内の「表面」三角形は
///   `dot(cross(b - a, c - a), 外向き法線) < 0`
/// を満たす（gltf_loader.rs の MESHLET_CONE_AXIS_SIGN のドキュメント参照）。
/// 球 SDF のメッシュ全三角形がこの条件を満たすことを固定する。
///
/// この向きが逆だと地形フラグメントが全面裏面判定になり、
/// terrain_gbuffer_write.wgsl の front_facing 反転で法線が丸ごと反転する
/// （＝ライト方向に対して陰影が逆転し、シャドウアクネが出る）。
#[test]
fn sphere_winding_matches_engine_front_face_convention() {
    let settings = TerrainSettings::default();
    let chunk = make_sphere_chunk(&settings);
    let mesh = generate_standalone(&chunk, &settings);

    assert!(mesh.triangle_count() > 0, "mesh should be non-empty");

    let mut ok = 0usize;
    let mut total = 0usize;
    for tri in mesh.indices.chunks_exact(3) {
        let pa = mesh.positions[tri[0] as usize];
        let pb = mesh.positions[tri[1] as usize];
        let pc = mesh.positions[tri[2] as usize];

        // ─── 代数的外積 cross(b-a, c-a) ───
        let ab = [pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2]];
        let ac = [pc[0] - pa[0], pc[1] - pa[1], pc[2] - pa[2]];
        let geo = [
            ab[1] * ac[2] - ab[2] * ac[1],
            ab[2] * ac[0] - ab[0] * ac[2],
            ab[0] * ac[1] - ab[1] * ac[0],
        ];

        // 縮退（面積ほぼ 0）三角形は向きが定義できず描画もされないので除外する。
        let glen2 = geo[0] * geo[0] + geo[1] * geo[1] + geo[2] * geo[2];
        if glen2 < DEGENERATE_AREA_EPSILON {
            continue;
        }
        total += 1;

        // ─── 三角形重心から見た球の外向き方向（＝正しい外向き法線） ───
        let cx = (pa[0] + pb[0] + pc[0]) / 3.0 - SPHERE_CENTER[0];
        let cy = (pa[1] + pb[1] + pc[1]) / 3.0 - SPHERE_CENTER[1];
        let cz = (pa[2] + pb[2] + pc[2]) / 3.0 - SPHERE_CENTER[2];

        // 規約: 外積は外向き法線と「逆向き」であること。
        if geo[0] * cx + geo[1] * cy + geo[2] * cz < 0.0 {
            ok += 1;
        }
    }

    assert!(total > 0, "non-degenerate triangles should exist");
    // 縮退を除いた三角形は 1 枚残らず規約を満たすこと（100%）。
    assert_eq!(
        ok, total,
        "{}/{total} triangles violate the engine front-face winding convention",
        total - ok
    );
}

/// 空洞（洞窟）SDF でチャンクを埋める。
/// density = radius - |p - center| ⇒ 球の内側は density>0 = **air**、外側は density<0 = **solid**。
/// つまり「固体の中にくり抜かれた球状の空洞」であり、掘削で生じる内壁と同じ形状。
fn make_cavity_chunk(settings: &TerrainSettings) -> TerrainChunkData {
    let mut chunk = TerrainChunkData::new_filled(settings, 0.0);
    let s = settings.samples_per_axis();
    let voxel = settings.voxel_size;
    for iz in 0..s {
        for iy in 0..s {
            for ix in 0..s {
                let p = [ix as f32 * voxel, iy as f32 * voxel, iz as f32 * voxel];
                let dx = p[0] - SPHERE_CENTER[0];
                let dy = p[1] - SPHERE_CENTER[1];
                let dz = p[2] - SPHERE_CENTER[2];
                let dist = (dx * dx + dy * dy + dz * dz).sqrt();
                // 固体球の符号を反転＝空洞。
                chunk.set_sample(ix, iy, iz, SPHERE_RADIUS - dist);
            }
        }
    }
    chunk
}

/// テスト2c：**空洞（洞窟内壁）** でも巻き順がエンジン規約を満たすことを検証する。
///
/// 空洞では air が球の内側にあるため、密度勾配（＝air 方向）は球中心を向く。
/// エンジン規約 `dot(cross(b-a, c-a), air 側法線) < 0` は固体球と同じ規則で
/// 成り立たねばならない（＝洞窟内壁が表面として描かれる）。
/// これが破れると `cull_face=Back` の地形では内壁が丸ごと消え、穴の中が真っ黒になる。
///
/// 【縮退三角形の除外】空洞側は固体球より縮退（面積ほぼ 0）三角形が多く出る
/// （MC の辺補間で 3 頂点がほぼ同一点に潰れるケース）。外積が 0 の三角形は
/// 向きが定義できず、描画上も面積 0 でラスタライズされないため判定から除く。
/// 除いた残り（＝実際に描かれる三角形）は **例外なく** 規約を満たさねばならない。
#[test]
fn cavity_winding_matches_engine_front_face_convention() {
    let settings = TerrainSettings::default();
    let chunk = make_cavity_chunk(&settings);
    let mesh = generate_standalone(&chunk, &settings);

    assert!(mesh.triangle_count() > 0, "cavity mesh should be non-empty");

    let mut ok = 0usize;
    let mut total = 0usize;
    for tri in mesh.indices.chunks_exact(3) {
        let pa = mesh.positions[tri[0] as usize];
        let pb = mesh.positions[tri[1] as usize];
        let pc = mesh.positions[tri[2] as usize];

        let ab = [pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2]];
        let ac = [pc[0] - pa[0], pc[1] - pa[1], pc[2] - pa[2]];
        let geo = [
            ab[1] * ac[2] - ab[2] * ac[1],
            ab[2] * ac[0] - ab[0] * ac[2],
            ab[0] * ac[1] - ab[1] * ac[0],
        ];

        // 縮退（面積ほぼ 0）三角形は向きが定義できず描画もされないので除外する。
        let glen2 = geo[0] * geo[0] + geo[1] * geo[1] + geo[2] * geo[2];
        if glen2 < DEGENERATE_AREA_EPSILON {
            continue;
        }
        total += 1;

        // 空洞の air 側方向 = 重心から球中心へ向かう向き（＝法線が向くべき向き）。
        let ix = SPHERE_CENTER[0] - (pa[0] + pb[0] + pc[0]) / 3.0;
        let iy = SPHERE_CENTER[1] - (pa[1] + pb[1] + pc[1]) / 3.0;
        let iz = SPHERE_CENTER[2] - (pa[2] + pb[2] + pc[2]) / 3.0;

        if geo[0] * ix + geo[1] * iy + geo[2] * iz < 0.0 {
            ok += 1;
        }
    }

    assert!(total > 0, "cavity: non-degenerate triangles should exist");
    // 縮退を除いた三角形は 1 枚残らず規約を満たすこと（100%）。
    assert_eq!(
        ok, total,
        "cavity: {}/{total} triangles violate the engine front-face winding convention",
        total - ok
    );
}

/// テスト2d：空洞の頂点法線が空洞内部（air 側＝球中心）を向くこと。
#[test]
fn cavity_normals_point_into_the_void() {
    let settings = TerrainSettings::default();
    let chunk = make_cavity_chunk(&settings);
    let mesh = generate_standalone(&chunk, &settings);

    let mut ok = 0usize;
    let total = mesh.positions.len();
    for (pos, nrm) in mesh.positions.iter().zip(mesh.normals.iter()) {
        // 球中心へ向かう方向（＝空洞の内側＝air 側）。
        let dir = [
            SPHERE_CENTER[0] - pos[0],
            SPHERE_CENTER[1] - pos[1],
            SPHERE_CENTER[2] - pos[2],
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

    let ratio = ok as f32 / total as f32;
    assert!(
        ratio >= 0.98,
        "cavity: only {ok}/{total} ({:.1}%) normals point into the void",
        ratio * 100.0
    );
}

/// 掘削テストのブラシ半径（メートル）。チャンク中央に収まる大きさ。
const DIG_BRUSH_RADIUS: f32 = 5.0;
/// 掘削テストのブラシ強度×dt（地表を確実に貫くだけの量）。
const DIG_BRUSH_STRENGTH: f32 = 8.0;
/// 掘削テストのブラシ適用時間（秒）。
const DIG_BRUSH_DT: f32 = 1.0;
/// 法線の向きを密度場で検証するときのプローブ距離（サンプル間隔単位）。
const NORMAL_PROBE_SAMPLES: f32 = 1.0;
/// 「掘った穴の内壁」とみなす法線の水平成分の下限（平坦地表は 0）。
const WALL_HORIZONTAL_MIN: f32 = 0.3;

/// 掘削テスト用の単一チャンク場。地表をチャンク中央の高さに置く。
///
/// `from_ground_plane` は密度＝ワールド Y なので、チャンク (0,0,0) では地表が
/// チャンク最下面（y=0）に来てしまい、セル内を surface が横切らず**メッシュが空**になる。
/// 掘削の内壁を見たいので、ここでは地表がチャンク中央に来るよう密度をオフセットする。
struct MidGroundField {
    settings: TerrainSettings,
    chunk:    TerrainChunkData,
    /// 地表のワールド Y（＝チャンク中央高さ）。
    ground_y: f32,
}

impl MidGroundField {
    fn new(settings: TerrainSettings) -> Self {
        let ground_y = settings.chunk_extent() * 0.5;
        // density = world_y - ground_y ⇒ 地表より下（density<0）が solid。
        let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);
        let s = settings.samples_per_axis();
        let voxel = settings.voxel_size;
        for iz in 0..s {
            for iy in 0..s {
                let d = iy as f32 * voxel - ground_y;
                for ix in 0..s {
                    chunk.set_sample(ix, iy, iz, d);
                }
            }
        }
        Self { settings, chunk, ground_y }
    }

    /// グローバルサンプル座標をチャンク内へクランプする。
    fn clamped(&self, g: i32) -> usize {
        let s = self.settings.samples_per_axis() as i32;
        g.clamp(0, s - 1) as usize
    }
}

impl SampleField for MidGroundField {
    fn settings(&self) -> &TerrainSettings {
        &self.settings
    }

    fn read_global(&self, gx: i32, gy: i32, gz: i32) -> f32 {
        self.chunk.sample(self.clamped(gx), self.clamped(gy), self.clamped(gz))
    }

    fn write_global(&mut self, gx: i32, gy: i32, gz: i32, v: f32) {
        // 範囲外は捨てる（クランプして書くと壁面が歪むため）。
        let s = self.settings.samples_per_axis() as i32;
        if (0..s).contains(&gx) && (0..s).contains(&gy) && (0..s).contains(&gz) {
            self.chunk.set_sample(gx as usize, gy as usize, gz as usize, v);
        }
    }

    fn world_of_global(&self, gx: i32, gy: i32, gz: i32) -> [f32; 3] {
        let v = self.settings.voxel_size;
        [gx as f32 * v, gy as f32 * v, gz as f32 * v]
    }
}

/// テスト2e：**実際の掘削（Subtract ブラシ）で生じた穴の内壁** が
/// 巻き順・法線ともエンジン規約を満たすことを検証する。
///
/// 空洞 SDF テスト（2c/2d）は解析的な球で理想的な密度場を与えるが、こちらは
/// 「平坦地面 → ブラシ編集 → 再メッシュ」というエディタと同じ経路を通る。
/// 掘った穴の内壁が `cull_face=Back` で消えない（＝穴の中が真っ黒にならない）ことの
/// 直接的な回帰テストである。
///
/// 【法線の検証方法】穴の形は解析式で書けないので、密度場そのものを基準にする。
/// 法線 n が air 側を向くなら、頂点から +n へ進んだ点の密度は -n へ進んだ点より
/// 大きい（density 大 = air）。これを実測して確かめる。
#[test]
fn dug_hole_walls_match_winding_and_normal_convention() {
    let settings = TerrainSettings::default();
    let mut field = MidGroundField::new(settings.clone());

    // ── 掘削前のメッシュ（平坦地表）が存在することを確かめる ──
    let before = generate_standalone(&field.chunk, &settings);
    assert!(before.triangle_count() > 0, "掘削前の地表メッシュが空（前提の崩れ）");

    // ── 地表中央を掘る ──
    let extent = settings.chunk_extent();
    let brush = SphereBrush {
        center:   [extent * 0.5, field.ground_y, extent * 0.5],
        radius:   DIG_BRUSH_RADIUS,
        strength: DIG_BRUSH_STRENGTH,
    };
    let touched = apply(&mut field, &brush, BrushOp::Subtract, DIG_BRUSH_DT);
    assert!(!touched.is_empty(), "掘削がどのチャンクにも影響していない");

    // ── 編集後を再メッシュする ──
    let mesh = generate_standalone(&field.chunk, &settings);
    assert!(mesh.triangle_count() > 0, "掘削後のメッシュが空");

    // ── ① 巻き順：非縮退な三角形はすべてエンジン規約を満たすこと ──
    //    基準の「air 側法線」は 3 頂点法線の平均（法線自体は②で独立に検証する）。
    let mut win_ok = 0usize;
    let mut win_total = 0usize;
    for tri in mesh.indices.chunks_exact(3) {
        let pa = mesh.positions[tri[0] as usize];
        let pb = mesh.positions[tri[1] as usize];
        let pc = mesh.positions[tri[2] as usize];
        let ab = [pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2]];
        let ac = [pc[0] - pa[0], pc[1] - pa[1], pc[2] - pa[2]];
        let geo = [
            ab[1] * ac[2] - ab[2] * ac[1],
            ab[2] * ac[0] - ab[0] * ac[2],
            ab[0] * ac[1] - ab[1] * ac[0],
        ];
        if geo[0] * geo[0] + geo[1] * geo[1] + geo[2] * geo[2] < DEGENERATE_AREA_EPSILON {
            continue;
        }
        win_total += 1;

        let na = mesh.normals[tri[0] as usize];
        let nb = mesh.normals[tri[1] as usize];
        let nc = mesh.normals[tri[2] as usize];
        let avg = [na[0] + nb[0] + nc[0], na[1] + nb[1] + nc[1], na[2] + nb[2] + nc[2]];

        // 規約: 外積は air 側法線と「逆向き」。
        if geo[0] * avg[0] + geo[1] * avg[1] + geo[2] * avg[2] < 0.0 {
            win_ok += 1;
        }
    }
    assert!(win_total > 0, "非縮退な三角形が無い");
    assert_eq!(
        win_ok, win_total,
        "掘削後: {}/{win_total} 枚の三角形が front-face 規約に違反（穴の内壁が消える）",
        win_total - win_ok
    );

    // ── ② 法線：密度場を直接プローブして air 側を向いていることを確かめる ──
    //    穴の内壁（下向き成分を持つ法線）が実際に生成されていることも同時に確認し、
    //    テストが平坦な地表だけを見て通ってしまうのを防ぐ。
    let voxel = settings.voxel_size;
    let s = settings.samples_per_axis() as i32;
    let sample_clamped = |x: i32, y: i32, z: i32| -> f32 {
        field.chunk.sample(
            x.clamp(0, s - 1) as usize,
            y.clamp(0, s - 1) as usize,
            z.clamp(0, s - 1) as usize,
        )
    };

    let mut n_ok = 0usize;
    let mut n_total = 0usize;
    let mut wall_vertices = 0usize;
    for (pos, nrm) in mesh.positions.iter().zip(mesh.normals.iter()) {
        // 穴の内壁＝法線が明確な水平成分を持つ頂点。
        // 平坦な地表は n=(0,1,0) なので水平成分 0。掘って初めて傾いた壁面が生まれる。
        let horiz = (nrm[0] * nrm[0] + nrm[2] * nrm[2]).sqrt();
        if horiz > WALL_HORIZONTAL_MIN {
            wall_vertices += 1;
        }

        // 頂点のサンプル座標。
        let gp = [pos[0] / voxel, pos[1] / voxel, pos[2] / voxel];
        let probe = |sign: f32| -> f32 {
            let p = [
                gp[0] + sign * nrm[0] * NORMAL_PROBE_SAMPLES,
                gp[1] + sign * nrm[1] * NORMAL_PROBE_SAMPLES,
                gp[2] + sign * nrm[2] * NORMAL_PROBE_SAMPLES,
            ];
            sample_clamped(p[0].round() as i32, p[1].round() as i32, p[2].round() as i32)
        };
        let d_air   = probe(1.0);   // +n 方向（air 側のはず＝密度大）
        let d_solid = probe(-1.0);  // -n 方向（solid 側のはず＝密度小）

        // 密度差が無い（クランプ域など）頂点は判定不能なので除外する。
        if (d_air - d_solid).abs() <= f32::EPSILON {
            continue;
        }
        n_total += 1;
        if d_air > d_solid {
            n_ok += 1;
        }
    }

    assert!(
        wall_vertices > 0,
        "傾いた内壁頂点が生成されていない（掘削できていない＝テストが形骸化）"
    );
    assert!(n_total > 0, "法線の向きを判定できる頂点が無い");
    // 離散サンプルへの丸めがあるため 98% を閾値とする。
    let ratio = n_ok as f32 / n_total as f32;
    assert!(
        ratio >= 0.98,
        "掘削後: {n_ok}/{n_total} ({:.1}%) の法線しか air 側を向いていない",
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


// ============================================================
//  メッシュ出力のバイト一致回帰（辺キャッシュ実装の差し替えを守る）
// ============================================================

/// FNV-1a 64bit のオフセット基底（正典値）。
const FNV_OFFSET_BASIS: u64 = 0xcbf2_9ce4_8422_2325;
/// FNV-1a 64bit の素数（正典値）。
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

/// 球 SDF メッシュの positions/normals/indices を丸ごと畳んだ FNV-1a ハッシュ（既定設定）。
///
/// **この値は「辺キャッシュを HashMap からフラット配列へ置換する前」の出力から採取した。**
/// 頂点の push 順・インデックス値・三角形の並びが 1 つでも変われば必ず変化する。
const SPHERE_MESH_FNV: u64 = 0x4d81_819d_ff2f_4575;

/// 4 バイトを FNV-1a へ畳み込む。
#[inline]
fn fnv_feed_u32(h: &mut u64, v: u32) {
    for b in v.to_le_bytes() {
        *h ^= b as u64;
        *h = h.wrapping_mul(FNV_PRIME);
    }
}

/// メッシュの位置・法線・インデックスを **ビットレベルで** 畳み込んだハッシュを返す。
///
/// f32 は `to_bits()` で扱うため、丸め誤差を許容せず完全一致だけを通す。
fn mesh_fingerprint(mesh: &super::marching_cubes::TerrainMesh) -> u64 {
    let mut h = FNV_OFFSET_BASIS;
    // 長さも混ぜる（要素数が変わったのに畳み込み結果が偶然一致するのを防ぐ）。
    fnv_feed_u32(&mut h, mesh.positions.len() as u32);
    fnv_feed_u32(&mut h, mesh.indices.len() as u32);
    for p in &mesh.positions {
        for c in p {
            fnv_feed_u32(&mut h, c.to_bits());
        }
    }
    for n in &mesh.normals {
        for c in n {
            fnv_feed_u32(&mut h, c.to_bits());
        }
    }
    for &i in &mesh.indices {
        fnv_feed_u32(&mut h, i);
    }
    h
}

/// テスト8：球 SDF チャンクのメッシュ出力が既知のハッシュとビット一致すること。
///
/// 辺の溶接キャッシュ（HashMap → フラット配列）のような内部実装差し替えが、
/// 出力を 1 ビットも変えていないことを固定するための回帰テスト。
#[test]
fn sphere_mesh_output_is_bit_stable() {
    let settings = TerrainSettings::default();
    let chunk = make_sphere_chunk(&settings);
    let mesh = generate_standalone(&chunk, &settings);

    // 前提: メッシュが空でないこと（空だとハッシュが定数になり何も守れない）。
    assert!(mesh.triangle_count() > 0, "球メッシュが空（前提の崩れ）");

    let fp = mesh_fingerprint(&mesh);
    eprintln!("[sphere_mesh_output_is_bit_stable] fingerprint = 0x{fp:016x}");
    assert_eq!(
        fp, SPHERE_MESH_FNV,
        "メッシュ出力が変化した（頂点順・インデックス・座標のいずれかが非互換）"
    );
}

// ─── ペイント差分更新の前提テスト用パラメータ ──────────────────────────────
/// テスト用スプラットのレイヤ数（この数で剰余を取ってサンプルごとに塗る層を変える）。
const PAINT_TEST_LAYER_COUNT: u32 = 4;
/// テスト用スプラットの主スロット重み（残りは副スロットへ回す）。
const PAINT_TEST_PRIMARY_WEIGHT: f32 = 0.7;
/// テスト用スプラットの副スロット重み（主 + 副 = 1）。
const PAINT_TEST_SECONDARY_WEIGHT: f32 = 1.0 - PAINT_TEST_PRIMARY_WEIGHT;
/// テスト用ペイント量のサンプル方向の刻み（サンプルごとに 0..1 を巡回させる）。
const PAINT_TEST_AMOUNT_STEP: f32 = 0.017;

/// 球 SDF チャンクへ、サンプルごとに異なるスプラットを塗り込む。
///
/// 一様に塗ると「補間が効いていなくてもテストが通る」ため、レイヤ番号・重み・
/// ペイント量のすべてがサンプル座標に依存して変化するようにする。
fn paint_sphere_chunk(settings: &TerrainSettings) -> TerrainChunkData {
    use super::layers::BlendSlots;

    let mut chunk = make_sphere_chunk(settings);
    let s = settings.samples_per_axis();
    for iz in 0..s {
        for iy in 0..s {
            for ix in 0..s {
                // サンプル座標から主レイヤ・副レイヤを決める（隣接サンプルで必ず変わる）。
                let primary = (ix + iy * 2 + iz * 3) as u32 % PAINT_TEST_LAYER_COUNT;
                let secondary = (primary + 1) % PAINT_TEST_LAYER_COUNT;
                let mut slots = BlendSlots::default();
                slots.index = [primary, secondary, 0, 0];
                slots.weight =
                    [PAINT_TEST_PRIMARY_WEIGHT, PAINT_TEST_SECONDARY_WEIGHT, 0.0, 0.0];
                chunk.set_paint_slots(ix, iy, iz, &slots);
                // ペイント量もサンプルごとに 0..1 を巡回させる。
                let amount = ((ix + iy + iz) as f32 * PAINT_TEST_AMOUNT_STEP).fract();
                chunk.set_paint_amount(ix, iy, iz, amount);
            }
        }
    }
    chunk
}

/// テスト9：`interp_vertex_paint` で TerrainMesh.paint を再構築した結果が、
/// `generate_standalone` が返した paint / paint_amount と全要素一致すること。
///
/// これは「ペイント高速パス」の前提そのものである。ペイントは密度を変えないので
/// 頂点の位置・法線・インデックスは不変であり、記録しておいた由来辺（TerrainVertexEdge）
/// からスプラットを引き直すだけで、フル再メッシュと同じ頂点属性が得られなければならない。
/// ここが崩れると、差分更新した箇所だけ色が食い違う。
#[test]
fn interp_vertex_paint_reproduces_mesh_paint() {
    use super::marching_cubes::interp_vertex_paint;

    let settings = TerrainSettings::default();
    let chunk = paint_sphere_chunk(&settings);
    let mesh = generate_standalone(&chunk, &settings);

    // 前提: メッシュが空でなく、由来辺が頂点数ぶん記録されていること。
    assert!(mesh.triangle_count() > 0, "球メッシュが空（前提の崩れ）");
    assert_eq!(
        mesh.edges.len(), mesh.positions.len(),
        "edges は positions と同じ長さでなければならない"
    );

    for (i, edge) in mesh.edges.iter().enumerate() {
        let (paint, amount) = interp_vertex_paint(&chunk, edge);

        // ── レイヤ番号は完全一致、重みはビット一致（同じ式・同じ順序で計算されるため）──
        assert_eq!(
            paint.index, mesh.paint[i].index,
            "頂点 {i}: 再構築したレイヤ番号が不一致 (edge={edge:?})"
        );
        for k in 0..super::layers::TERRAIN_BLEND_SLOTS {
            assert_eq!(
                paint.weight[k].to_bits(), mesh.paint[i].weight[k].to_bits(),
                "頂点 {i} スロット {k}: 再構築した重みがビット不一致"
            );
        }
        assert_eq!(
            amount.to_bits(), mesh.paint_amount[i].to_bits(),
            "頂点 {i}: 再構築したペイント量がビット不一致"
        );
    }
}

/// テスト9b：由来辺（TerrainVertexEdge）が実際に頂点位置を再現できること。
///
/// テスト9 だけだと「edges が正しい辺を指しているか」は検証されない
/// （generate_core と同じ edge を使い回しているため定義上必ず一致する）。
/// ここでは辺の記述子から位置を独立に組み立て直し、mesh.positions と突き合わせる。
#[test]
fn vertex_edge_descriptor_reconstructs_positions() {
    let settings = TerrainSettings::default();
    let chunk = paint_sphere_chunk(&settings);
    let mesh = generate_standalone(&chunk, &settings);
    let voxel = settings.voxel_size;

    for (i, edge) in mesh.edges.iter().enumerate() {
        // hi = lo + unit(axis)。位置は lo→hi を t で内分したもの（メートル換算）。
        let mut p = [
            edge.lo[0] as f32,
            edge.lo[1] as f32,
            edge.lo[2] as f32,
        ];
        p[edge.axis as usize] += edge.t;
        for k in 0..3 {
            let expected = mesh.positions[i][k];
            let actual = p[k] * voxel;
            assert_eq!(
                actual.to_bits(), expected.to_bits(),
                "頂点 {i} 軸 {k}: 由来辺から復元した位置がビット不一致 \
                 ({actual} vs {expected}, edge={edge:?})"
            );
        }
    }
}

// ============================================================
//  チャンク構成の設定（apply_chunk_config）と tvox ヘッダ読み取り
// ============================================================

/// 設定範囲外の値が上下限へクランプされること（エディタからの不正入力に対する防御）。
#[test]
fn apply_chunk_config_clamps_out_of_range_values() {
    use super::settings::{
        MAX_CHUNK_CELLS, MAX_GROUND_CHUNKS, MAX_VOXEL_SIZE, MIN_CHUNK_CELLS, MIN_GROUND_CHUNKS,
        MIN_VOXEL_SIZE,
    };

    // 下限側: 0 枚・0 分割・0m はいずれも成立しない。
    let mut small = TerrainSettings::default();
    small.apply_chunk_config(0, 0, 0, 0.0);
    assert_eq!(small.ground_chunks_x, MIN_GROUND_CHUNKS);
    assert_eq!(small.ground_chunks_z, MIN_GROUND_CHUNKS);
    assert_eq!(small.chunk_cells, MIN_CHUNK_CELLS);
    assert_eq!(small.voxel_size, MIN_VOXEL_SIZE);

    // 上限側: メモリを食い潰す巨大値は上限で止まる。
    let mut big = TerrainSettings::default();
    big.apply_chunk_config(9999, 9999, 9999, 9999.0);
    assert_eq!(big.ground_chunks_x, MAX_GROUND_CHUNKS);
    assert_eq!(big.ground_chunks_z, MAX_GROUND_CHUNKS);
    assert_eq!(big.chunk_cells, MAX_CHUNK_CELLS);
    assert_eq!(big.voxel_size, MAX_VOXEL_SIZE);
}

/// NaN を渡してもパニックせず既定値へ落ちること（f32::clamp は NaN でパニックするため）。
#[test]
fn apply_chunk_config_rejects_nan_voxel_size() {
    let mut s = TerrainSettings::default();
    let before = s.voxel_size;
    s.apply_chunk_config(4, 4, 32, f32::NAN);
    assert_eq!(s.voxel_size, before, "NaN は既定値へフォールバックする");
}

/// 構成変更に合わせて派生値（chunk_extent / density_clamp / samples_per_axis）が
/// 追随すること。density_clamp が古い構成のまま残ると、大きなチャンクでブラシ編集の
/// 密度が頭打ちになって掘削・盛土が効かなくなる。
#[test]
fn apply_chunk_config_updates_derived_values() {
    let mut s = TerrainSettings::default();
    s.apply_chunk_config(6, 8, 16, 1.0);
    assert_eq!(s.ground_chunks_x, 6);
    assert_eq!(s.ground_chunks_z, 8);
    assert_eq!(s.samples_per_axis(), 17, "サンプル数 = cells + 1");
    assert_eq!(s.chunk_extent(), 16.0, "実寸 = voxel_size * chunk_cells");
    assert_eq!(s.density_clamp, s.chunk_extent(), "density_clamp は実寸に追随する");
}

/// 既定以外のチャンク分割数で作ったチャンクが .tvox を往復して壊れないこと。
#[test]
fn tvox_roundtrip_with_custom_chunk_cells() {
    let mut settings = TerrainSettings::default();
    settings.apply_chunk_config(2, 2, 8, 0.25);
    let coord = ChunkCoord::new(-1, 2, 3);
    let chunk = TerrainChunkData::from_ground_plane(&settings, coord);

    let bytes = write_chunk(&chunk, coord, &settings);
    let (restored, restored_coord) = read_chunk(&bytes).expect("読み込めること");
    assert_eq!(restored_coord, coord);
    assert_eq!(restored.samples_per_axis(), settings.samples_per_axis());
    assert_eq!(restored.raw_density(), chunk.raw_density(), "密度がビット一致すること");
}

/// tvox ヘッダだけを読む read_header が、書き出したチャンク構成をそのまま返すこと。
/// シーンロード時の「保存された地形の分割数を復元する」経路の土台。
#[test]
fn tvox_read_header_reports_chunk_config() {
    use super::tvox::read_header;

    let mut settings = TerrainSettings::default();
    settings.apply_chunk_config(2, 2, 12, 0.75);
    let coord = ChunkCoord::new(4, -2, 7);
    let chunk = TerrainChunkData::from_ground_plane(&settings, coord);
    let bytes = write_chunk(&chunk, coord, &settings);

    let header = read_header(&bytes).expect("ヘッダが読めること");
    assert_eq!(header.coord, coord);
    assert_eq!(header.samples_per_axis, settings.samples_per_axis() as u32);
    assert_eq!(header.voxel_size, settings.voxel_size);
    assert_eq!(header.chunk_cells(), settings.chunk_cells, "cells = samples - 1 が復元できる");
}

/// 壊れた／短すぎるバイト列では read_header がエラーを返すこと（本体を読む前に弾ける）。
#[test]
fn tvox_read_header_detects_bad_input() {
    use super::tvox::read_header;

    assert_eq!(read_header(&[]), Err(TvoxError::Truncated));
    let mut bad = vec![0u8; 32];
    bad[0..4].copy_from_slice(b"XXXX");
    assert_eq!(read_header(&bad), Err(TvoxError::BadMagic));
    let mut bad_version = vec![0u8; 32];
    bad_version[0..4].copy_from_slice(&TVOX_MAGIC);
    bad_version[4..8].copy_from_slice(&99u32.to_le_bytes());
    assert_eq!(read_header(&bad_version), Err(TvoxError::BadVersion));
}
