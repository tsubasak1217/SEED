// ============================================================
//  terrain/tests_sharpness.rs — 法線シャープネスペイント（T4）専用テスト
//
//  【対象】
//    - 目標値へ寄せる純関数（範囲・収束・消去）
//    - 球ブラシのフォールオフ（中心ほど強く／球外は不変）
//    - .tvox v4 のシリアライズ往復
//    - 旧バージョン（v1 / v2 / v3）の後方互換（シャープネス = 0 で読める）
//    - 再メッシュでの引き継ぎ（グリッドに持つので頂点を作り直しても残る）
//    - デシメート（simplify_mesh）での生存頂点の値の継承
//
//  レイヤペイント（tests_layers.rs）と同じ流儀。他のテストファイルと同様、
//  エンジン非依存の純粋層だけを対象にする（GPU / ECS には触れない）。
// ============================================================

use std::collections::HashMap;

use super::brush::SphereBrush;
use super::chunk_coord::ChunkCoord;
use super::chunk_data::TerrainChunkData;
use super::marching_cubes::{generate_standalone, interp_vertex_sharpness};
use super::settings::TerrainSettings;
use super::sharpness::{SharpnessField, apply_sharpness, approach_target};
use super::simplify::simplify_mesh;
use super::tvox::{read_chunk, write_chunk, write_chunk_v1, write_chunk_v2, write_chunk_v3};
use super::layers::TERRAIN_BLEND_SLOTS;

// ─── テスト用定数（マジックナンバー回避） ───────────────────────────────────

/// u8 量子化を経る比較の許容誤差（1/255 ≒ 0.0039 相当）。
const QUANT_EPS: f32 = 5.0e-3;
/// f32 演算の丸めを許容する誤差。
const EPS: f32 = 1.0e-5;
/// ブラシテスト用の中心（チャンク中央・ワールド座標メートル）。
///
/// チャンク 1 辺が 8m なのでその中央に置く。**端に置いてはいけない**:
/// このテスト用の場は境界サンプルの重複所有を実装していない素朴な実装なので、
/// 端を塗ると隣（存在しない）チャンクへ書きに行って何も起きない。
/// 境界同期そのものはエンジン層（terrain_ops.rs の FieldView）の責務である。
const BRUSH_CENTER: [f32; 3] = [4.0, 4.0, 4.0];
/// ブラシテスト用の半径（メートル）。中心 ± 半径がチャンク内に収まる値にする。
const BRUSH_RADIUS: f32 = 1.5;
/// ブラシ 1 発ぶんの dt（strength と掛けて寄せ量になる）。
const BRUSH_DT: f32 = 1.0;
/// 収束テストで打つブラシ回数（十分に多い＝目標値へ到達しているはず）。
const CONVERGE_STROKES: usize = 64;
/// 小さめのテスト設定に使うチャンクセル数。
const TEST_CHUNK_CELLS: u32 = 16;
/// 小さめのテスト設定に使うボクセルサイズ（メートル）。
const TEST_VOXEL_SIZE: f32 = 0.5;
/// デシメートテストで使う削減強度（0〜1）。
const DECIMATE_STRENGTH: f32 = 0.6;

/// 小さめのテスト設定（1 チャンク = 16 セル × 0.5m = 8m 角）。
fn test_settings() -> TerrainSettings {
    TerrainSettings {
        chunk_cells: TEST_CHUNK_CELLS,
        voxel_size: TEST_VOXEL_SIZE,
        ..TerrainSettings::default()
    }
}

/// 指定ワールド高さに水平な地面を持つチャンク（density = y - surface_y）。
fn flat_ground_chunk(settings: &TerrainSettings, surface_y: f32) -> TerrainChunkData {
    let s = settings.samples_per_axis();
    let mut chunk = TerrainChunkData::new_filled(settings, 0.0);
    for iz in 0..s {
        for iy in 0..s {
            let wy = iy as f32 * settings.voxel_size;
            for ix in 0..s {
                chunk.set_sample(ix, iy, iz, wy - surface_y);
            }
        }
    }
    chunk
}

// ============================================================
//  テスト用の SharpnessField 実装（1 チャンク集合の素朴な場）
// ============================================================

/// テスト用のシャープネス場。エンジン層の `FieldView` と同じ責務の最小実装。
struct SharpnessView<'a> {
    settings: &'a TerrainSettings,
    chunks: &'a mut HashMap<ChunkCoord, TerrainChunkData>,
}

impl<'a> SharpnessView<'a> {
    /// グローバルサンプル座標 → (チャンク座標, ローカル添字)。
    fn split(&self, gx: i32, gy: i32, gz: i32) -> (ChunkCoord, usize, usize, usize) {
        let cells = self.settings.chunk_cells as i32;
        (
            ChunkCoord::new(
                gx.div_euclid(cells),
                gy.div_euclid(cells),
                gz.div_euclid(cells),
            ),
            gx.rem_euclid(cells) as usize,
            gy.rem_euclid(cells) as usize,
            gz.rem_euclid(cells) as usize,
        )
    }
}

impl<'a> SharpnessField for SharpnessView<'a> {
    fn settings(&self) -> &TerrainSettings {
        self.settings
    }
    fn read_sharpness_global(&self, gx: i32, gy: i32, gz: i32) -> f32 {
        let (c, lx, ly, lz) = self.split(gx, gy, gz);
        match self.chunks.get(&c) {
            Some(chunk) => chunk.sharpness(lx, ly, lz),
            None => 0.0,
        }
    }
    fn write_sharpness_global(&mut self, gx: i32, gy: i32, gz: i32, w: f32) {
        let (c, lx, ly, lz) = self.split(gx, gy, gz);
        if let Some(chunk) = self.chunks.get_mut(&c) {
            chunk.set_sharpness(lx, ly, lz, w);
        }
    }
    fn world_of_global(&self, gx: i32, gy: i32, gz: i32) -> [f32; 3] {
        let vs = self.settings.voxel_size;
        [gx as f32 * vs, gy as f32 * vs, gz as f32 * vs]
    }
}

/// 1 チャンクだけを持つテスト用の場を作る。
fn single_chunk_world(settings: &TerrainSettings) -> HashMap<ChunkCoord, TerrainChunkData> {
    let mut chunks = HashMap::new();
    chunks.insert(
        ChunkCoord::new(0, 0, 0),
        TerrainChunkData::new_filled(settings, 0.0),
    );
    chunks
}

// ============================================================
//  ① 純関数: 目標値へ寄せる
// ============================================================

/// `approach_target` が範囲・向き・収束の 3 点で仕様どおりであること。
#[test]
fn approach_target_moves_toward_target_in_both_directions() {
    // 上げ: 0 → 1 を係数 0.5 で 1 回。
    assert!((approach_target(0.0, 1.0, 0.5) - 0.5).abs() < EPS);
    // 下げ（消去）: 1 → 0 を係数 0.25 で 1 回。
    assert!((approach_target(1.0, 0.0, 0.25) - 0.75).abs() < EPS);
    // 係数 0 は無変化、係数 1 は即到達。
    assert!((approach_target(0.3, 1.0, 0.0) - 0.3).abs() < EPS);
    assert!((approach_target(0.3, 1.0, 1.0) - 1.0).abs() < EPS);
    // 既に目標値なら動かない（何度なぞっても止まる）。
    assert!((approach_target(0.7, 0.7, 1.0) - 0.7).abs() < EPS);
}

/// 範囲外の入力（負値・1 超・係数の範囲外）を必ず 0..=1 へ押し込むこと。
#[test]
fn approach_target_clamps_all_inputs() {
    assert!((approach_target(-5.0, 2.0, 3.0) - 1.0).abs() < EPS);
    assert!((approach_target(2.0, -1.0, 1.0) - 0.0).abs() < EPS);
    // 負の係数は「無変化」へ縮退する（逆走させない）。
    assert!((approach_target(0.4, 1.0, -1.0) - 0.4).abs() < EPS);
}

/// なぞり続けると目標値へ収束して止まること（発散・行き過ぎが無い）。
#[test]
fn approach_target_converges_to_target() {
    let target = 0.35;
    let mut w = 1.0f32;
    for _ in 0..CONVERGE_STROKES {
        w = approach_target(w, target, 0.3);
    }
    assert!(
        (w - target).abs() < EPS,
        "目標値へ収束していない: w={w} target={target}"
    );
}

// ============================================================
//  ② ブラシ: 範囲とフォールオフ
// ============================================================

/// 球ブラシが中心を最も強く塗り、球外のサンプルには一切触れないこと。
#[test]
fn sharpness_brush_falls_off_and_respects_radius() {
    let settings = test_settings();
    let mut chunks = single_chunk_world(&settings);
    let brush = SphereBrush {
        center: BRUSH_CENTER,
        radius: BRUSH_RADIUS,
        strength: 0.5,
    };

    {
        let mut view = SharpnessView {
            settings: &settings,
            chunks: &mut chunks,
        };
        let touched = apply_sharpness(&mut view, &brush, 1.0, BRUSH_DT);
        assert!(
            touched.contains(&ChunkCoord::new(0, 0, 0)),
            "ブラシが当たったチャンクが返っていない"
        );
    }

    let chunk = &chunks[&ChunkCoord::new(0, 0, 0)];
    // サンプル添字 = ワールド座標 / voxel_size（チャンク原点が 0 なので一致）。
    let to_index = |m: f32| (m / TEST_VOXEL_SIZE).round() as usize;
    let cx = to_index(BRUSH_CENTER[0]);
    let cy = to_index(BRUSH_CENTER[1]);
    let cz = to_index(BRUSH_CENTER[2]);

    let center_w = chunk.sharpness(cx, cy, cz);
    // 中心から半径の約 2/3 だけ X 方向へずれた点。
    let mid_w = chunk.sharpness(cx + to_index(BRUSH_RADIUS * 2.0 / 3.0), cy, cz);
    // 半径の外（+1 サンプルぶん外側）。
    let outside_w = chunk.sharpness(cx + to_index(BRUSH_RADIUS) + 1, cy, cz);

    assert!(center_w > 0.0, "中心が塗られていない: {center_w}");
    assert!(
        mid_w < center_w,
        "フォールオフしていない（縁のほうが濃い）: center={center_w} mid={mid_w}"
    );
    assert!(
        outside_w.abs() < QUANT_EPS,
        "球の外まで塗られている: {outside_w}"
    );
}

/// 同じ場所を目標値 0 でなぞると消去（完全スムーズへ復帰）できること。
#[test]
fn sharpness_brush_erases_back_to_zero() {
    let settings = test_settings();
    let mut chunks = single_chunk_world(&settings);
    let brush = SphereBrush {
        center: BRUSH_CENTER,
        radius: BRUSH_RADIUS,
        // 1 発で目標値へ到達する強度（収束の速さはこのテストの関心ではない）。
        strength: 1.0,
    };

    let coord = ChunkCoord::new(0, 0, 0);
    let cx = (BRUSH_CENTER[0] / TEST_VOXEL_SIZE).round() as usize;
    let cy = (BRUSH_CENTER[1] / TEST_VOXEL_SIZE).round() as usize;
    let cz = (BRUSH_CENTER[2] / TEST_VOXEL_SIZE).round() as usize;

    // ① 面法線側へ塗る。
    {
        let mut view = SharpnessView { settings: &settings, chunks: &mut chunks };
        apply_sharpness(&mut view, &brush, 1.0, BRUSH_DT);
    }
    assert!(
        (chunks[&coord].sharpness(cx, cy, cz) - 1.0).abs() < QUANT_EPS,
        "目標値 1 へ到達していない"
    );

    // ② 目標値 0 でなぞる＝消去。
    {
        let mut view = SharpnessView { settings: &settings, chunks: &mut chunks };
        apply_sharpness(&mut view, &brush, 0.0, BRUSH_DT);
    }
    assert!(
        chunks[&coord].sharpness(cx, cy, cz).abs() < QUANT_EPS,
        "消去できていない: {}",
        chunks[&coord].sharpness(cx, cy, cz)
    );
}

// ============================================================
//  ③ 永続化（.tvox v4）と後方互換
// ============================================================

/// シャープネスを含むチャンクが .tvox v4 で往復すること。
#[test]
fn tvox_roundtrip_preserves_sharpness() {
    let settings = test_settings();
    let mut chunk = TerrainChunkData::new_filled(&settings, 0.0);
    let s = settings.samples_per_axis();

    // 添字から決まる再現可能な模様を書き込む（全サンプルが同値だと取り違えに気付けない）。
    for iz in 0..s {
        for iy in 0..s {
            for ix in 0..s {
                let w = ((ix + iy * 2 + iz * 3) % 256) as f32 / 255.0;
                chunk.set_sharpness(ix, iy, iz, w);
            }
        }
    }

    let coord = ChunkCoord::new(1, -2, 3);
    let bytes = write_chunk(&chunk, coord, &settings);
    let (restored, restored_coord) = read_chunk(&bytes).expect("v4 の読み込みに失敗");
    assert_eq!(restored_coord, coord);

    for iz in 0..s {
        for iy in 0..s {
            for ix in 0..s {
                let expected = chunk.sharpness(ix, iy, iz);
                let actual = restored.sharpness(ix, iy, iz);
                assert!(
                    (expected - actual).abs() < QUANT_EPS,
                    "({ix},{iy},{iz}) のシャープネスが往復で変化した: {expected} -> {actual}"
                );
            }
        }
    }
}

/// v3（シャープネス無し）の .tvox を読むと、全サンプルが 0＝完全スムーズになること。
///
/// 旧セーブデータが「法線シャープネス導入前の見た目のまま」開けることの保証。
#[test]
fn tvox_v3_reads_with_zero_sharpness() {
    let settings = test_settings();
    let mut chunk = flat_ground_chunk(&settings, 4.0);
    // v3 で書き出す前にシャープネスを立てておく（＝v3 には載らないことの確認）。
    chunk.set_sharpness(1, 2, 3, 1.0);

    let coord = ChunkCoord::new(0, 0, 0);
    let bytes = write_chunk_v3(&chunk, coord, &settings);
    let (restored, restored_coord) = read_chunk(&bytes).expect("v3 の読み込みに失敗");
    assert_eq!(restored_coord, coord);

    // 密度は保たれ、シャープネスだけが 0 に落ちる。
    assert!((restored.sample(1, 2, 3) - chunk.sample(1, 2, 3)).abs() < EPS);
    assert!(
        restored.raw_sharpness().iter().all(|&q| q == 0),
        "v3 読み込みでシャープネスが 0 になっていない"
    );
}

/// v2 / v1 の .tvox も引き続き読め、シャープネスは 0 になること。
#[test]
fn tvox_v2_and_v1_read_with_zero_sharpness() {
    let settings = test_settings();
    let chunk = flat_ground_chunk(&settings, 4.0);
    let coord = ChunkCoord::new(0, 0, 0);
    let total = chunk.raw_density().len();

    // v2: レイヤ番号なしの密重み配列を持つ形式。
    let dense = vec![[0u8; TERRAIN_BLEND_SLOTS]; total];
    let v2 = write_chunk_v2(&chunk, coord, &settings, &dense);
    let (r2, _) = read_chunk(&v2).expect("v2 の読み込みに失敗");
    assert!(r2.raw_sharpness().iter().all(|&q| q == 0));

    // v1: 密度のみ。
    let v1 = write_chunk_v1(&chunk, coord, &settings);
    let (r1, _) = read_chunk(&v1).expect("v1 の読み込みに失敗");
    assert!(r1.raw_sharpness().iter().all(|&q| q == 0));
}

// ============================================================
//  ④ 再メッシュでの引き継ぎ
// ============================================================

/// シャープネスをグリッドへ持つため、密度を編集して再メッシュしても値が残ること。
///
/// レイヤペイントとまったく同じ引き継ぎ規約（頂点ではなくサンプルに持つ）の確認。
#[test]
fn sharpness_survives_remesh() {
    let settings = test_settings();
    let mut chunk = flat_ground_chunk(&settings, 4.0);
    let s = settings.samples_per_axis();

    // 全サンプルを面法線側（1.0）にしてから、地表を横切るメッシュを作る。
    for iz in 0..s {
        for iy in 0..s {
            for ix in 0..s {
                chunk.set_sharpness(ix, iy, iz, 1.0);
            }
        }
    }

    let mesh = generate_standalone(&chunk, &settings);
    assert!(!mesh.positions.is_empty(), "テスト地形からメッシュが出ていない");
    assert_eq!(
        mesh.sharpness.len(),
        mesh.positions.len(),
        "sharpness の長さが positions と一致していない"
    );
    assert!(
        mesh.sharpness.iter().all(|&w| (w - 1.0).abs() < QUANT_EPS),
        "生成直後の頂点シャープネスが 1 になっていない"
    );

    // ── 密度を変えて（＝地表を持ち上げて）再メッシュしても、値は引き継がれる ──
    for iz in 0..s {
        for iy in 0..s {
            for ix in 0..s {
                let d = chunk.sample(ix, iy, iz);
                chunk.set_sample(ix, iy, iz, d - 1.0);
            }
        }
    }
    let remeshed = generate_standalone(&chunk, &settings);
    assert!(!remeshed.positions.is_empty());
    assert!(
        remeshed.sharpness.iter().all(|&w| (w - 1.0).abs() < QUANT_EPS),
        "再メッシュでシャープネスが失われた"
    );
}

/// 頂点の由来辺からシャープネスを引き直す関数が、フル生成の結果と一致すること。
///
/// ペイント高速パス（メッシュを作り直さず頂点属性だけ差し替える経路）が
/// フル再メッシュと同じ値になることの根拠。
#[test]
fn interp_vertex_sharpness_matches_full_generate() {
    let settings = test_settings();
    let mut chunk = flat_ground_chunk(&settings, 4.0);
    let s = settings.samples_per_axis();

    // 高さで変化する模様にして、辺補間の t が効いていることを見えるようにする。
    for iz in 0..s {
        for iy in 0..s {
            let w = (iy as f32 / (s - 1) as f32).clamp(0.0, 1.0);
            for ix in 0..s {
                chunk.set_sharpness(ix, iy, iz, w);
            }
        }
    }

    let mesh = generate_standalone(&chunk, &settings);
    assert_eq!(mesh.edges.len(), mesh.sharpness.len());
    for (i, edge) in mesh.edges.iter().enumerate() {
        let recomputed = interp_vertex_sharpness(&chunk, edge);
        assert!(
            (recomputed - mesh.sharpness[i]).abs() < EPS,
            "頂点 {i}: 由来辺からの引き直しがフル生成と一致しない: {recomputed} != {}",
            mesh.sharpness[i]
        );
    }
}

// ============================================================
//  ⑤ デシメートでの継承
// ============================================================

/// デシメート後も生存頂点がシャープネスを保ち、配列長が positions と揃うこと。
#[test]
fn decimate_inherits_sharpness() {
    let settings = test_settings();
    let mut chunk = flat_ground_chunk(&settings, 4.0);
    let s = settings.samples_per_axis();

    // X 方向で 0 / 1 に分かれる模様（「消えた頂点の値が混ざる」不具合を検出できる）。
    for iz in 0..s {
        for iy in 0..s {
            for ix in 0..s {
                let w = if ix < s / 2 { 0.0 } else { 1.0 };
                chunk.set_sharpness(ix, iy, iz, w);
            }
        }
    }

    let mesh = generate_standalone(&chunk, &settings);
    let before: Vec<f32> = mesh.sharpness.clone();
    assert!(!before.is_empty());

    let extent = settings.chunk_extent();
    let (out, stats) = simplify_mesh(&mesh, DECIMATE_STRENGTH, extent);

    assert_eq!(
        out.sharpness.len(),
        out.positions.len(),
        "デシメート後の sharpness 長が positions と一致していない"
    );
    assert!(
        stats.vertices_after <= stats.vertices_before,
        "デシメートで頂点が増えている"
    );
    // ハーフエッジコラプスなので、残った値は必ず入力に存在した値のいずれか
    // （新しい頂点を作らない＝値を合成しない、という設計の確認）。
    for &w in &out.sharpness {
        assert!(
            before.iter().any(|&b| (b - w).abs() < EPS),
            "デシメートで入力に無い値が生まれた: {w}"
        );
    }
}
