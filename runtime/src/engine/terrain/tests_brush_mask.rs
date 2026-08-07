// ============================================================
//  terrain/tests_brush_mask.rs — ブラシ形状マスクのユニットテスト
//
//  本ファイルが固定する契約:
//    1. **マスク未指定なら従来の円形フォールオフとビット単位で一致する**
//       （既存シーンの見た目を 1 ビットも変えない、という最重要の約束）
//    2. 全白マスクは正方形の内側で一様フル強度、外側で 0
//    3. 全黒マスクは何も変えない（ペイントが 1 チャンクも触らない）
//    4. マスクの左右・上下がワールドの ±X・±Z へ正しく写る（非対称マスクで確認）
//    5. 無効マスク（読み込み失敗＝`CoverMask::empty()`）は「効かない」ではなく
//       **従来のフォールオフへ縮退する**（ブラシが黙って無反応になる事故を防ぐ）
//    6. マスク指定時はブラシ球の外側にある「正方形の四隅」も塗れる
// ============================================================

use std::collections::HashMap;

use super::brush::SphereBrush;
use super::brush_mask::{brush_mask_is_active, brush_mask_uv, brush_shape_factor};
use super::chunk_coord::ChunkCoord;
use super::chunk_data::TerrainChunkData;
use super::cover::brush::{CoverBrushMode, CoverBrushSpec};
use super::cover::CoverMask;
use super::paint::{apply_paint_with_mask, PaintField};
use super::settings::TerrainSettings;
use super::tests_layers::PaintView;

// ─── テスト用定数（マジックナンバー禁止）─────────────────────────────────────

/// テストで使うブラシ半径（メートル）。チャンク（既定 16m）に十分収まる大きさ。
const TEST_RADIUS: f32 = 4.0;

/// テストで使うブラシ中心（チャンク中央付近のワールド座標）。
const TEST_CENTER: [f32; 3] = [8.0, 8.0, 8.0];

/// 「一様マスク」を作るときの解像度（縦横）。
///
/// 4×4 にしてあるのは、最近傍サンプリングの境界（0.25 刻み）が
/// テストの座標計算と噛み合いやすく、かつ 1 テクセルの支配範囲が広くて
/// 浮動小数の誤差で隣のテクセルへ落ちる事故が起きにくいためである。
const UNIFORM_MASK_SIZE: usize = 4;

/// グレースケールの白（フル強度）。
const MASK_WHITE: u8 = 255;

/// グレースケールの黒（強度 0）。
const MASK_BLACK: u8 = 0;

/// 浮動小数の比較許容誤差（形状係数は 0..1 なので十分に小さい値でよい）。
const SHAPE_EPS: f32 = 1.0e-6;

// ─── テスト用ヘルパ ──────────────────────────────────────────────────────────

/// 単色で埋めたマスクを作る。
fn uniform_mask(value: u8) -> CoverMask {
    CoverMask {
        width: UNIFORM_MASK_SIZE,
        height: UNIFORM_MASK_SIZE,
        pixels: vec![value; UNIFORM_MASK_SIZE * UNIFORM_MASK_SIZE],
    }
}

/// 左上（画像座標の x=0, y=0）1 テクセルだけが白の 2×2 マスク。
///
/// 向きの検証専用。この 1 点がワールドのどの象限へ落ちるかで、
/// U の左右と V の上下の対応が一意に決まる。
fn corner_mask() -> CoverMask {
    CoverMask {
        width: 2,
        height: 2,
        // row-major: [ (0,0), (1,0), (0,1), (1,1) ]
        pixels: vec![MASK_WHITE, MASK_BLACK, MASK_BLACK, MASK_BLACK],
    }
}

/// マスク導入 **以前** のカバーブラシ円形フォールオフ（当時のコードをそのまま写したもの）。
///
/// 回帰の基準値。ここを現行実装から作ってしまうと「両方同時に壊れる」ので、
/// 意図的に式を複製している（テストの独立性）。
fn legacy_circular_falloff(dx: f32, dz: f32, radius: f32) -> f32 {
    if !(radius > 0.0) {
        return 0.0;
    }
    let d = (dx * dx + dz * dz).sqrt();
    if d >= radius {
        return 0.0;
    }
    let t = 1.0 - d / radius;
    t * t * (3.0 - 2.0 * t)
}

/// テスト用のカバーブラシ仕様（塗りモード。形状の検証にはモードは影響しない）。
fn test_cover_spec(radius: f32) -> CoverBrushSpec {
    CoverBrushSpec {
        center: TEST_CENTER,
        radius,
        strength: 1.0,
        mode: CoverBrushMode::Paint,
        material_index: 0,
        target_amount: 1.0,
    }
}

/// 単一チャンクの空スプラット場を作る。
fn empty_chunks(settings: &TerrainSettings) -> HashMap<ChunkCoord, TerrainChunkData> {
    let mut chunks = HashMap::new();
    chunks.insert(
        ChunkCoord::new(0, 0, 0),
        TerrainChunkData::new_filled(settings, 0.0),
    );
    chunks
}

// ============================================================
//  1. マスク未指定 = 従来どおり（ビット単位一致）
// ============================================================

/// マスク未指定の形状係数が、マスク導入前の円形フォールオフと **ビット単位で一致**すること。
///
/// これが崩れると、既にマスクを使っていない全ての地形編集の結果が変わる。
/// 誤差ではなくビット比較（`to_bits`）にしてあるのは、
/// 「ほぼ同じ」を許すと再メッシュ結果の差分が積み上がって見た目に出るためである。
#[test]
fn brush_shape_without_mask_is_bit_identical_to_legacy_falloff() {
    // 半径は 0（無効）・小・大の 3 種、位置は中心・縁・外側まで広く走査する。
    for &radius in &[0.0f32, 0.5, TEST_RADIUS, 12.0] {
        let mut dz = -TEST_RADIUS * 2.0;
        while dz <= TEST_RADIUS * 2.0 {
            let mut dx = -TEST_RADIUS * 2.0;
            while dx <= TEST_RADIUS * 2.0 {
                let expected = legacy_circular_falloff(dx, dz, radius);
                let actual = brush_shape_factor(
                    None,
                    [TEST_CENTER[0], TEST_CENTER[2]],
                    radius,
                    [TEST_CENTER[0] + dx, TEST_CENTER[2] + dz],
                    (dx * dx + dz * dz).sqrt(),
                );
                assert_eq!(
                    expected.to_bits(),
                    actual.to_bits(),
                    "マスク未指定の形状係数が旧実装と一致しない (r={radius} dx={dx} dz={dz})"
                );
                dx += 0.37; // 格子と噛み合わない刻みで、たまたま一致する事故を避ける
            }
            dz += 0.41;
        }
    }
}

/// `CoverBrushSpec::falloff_at`（マスク引数なしの旧 API）も旧実装とビット一致すること。
///
/// 既存の呼び出し側（テスト・轍との縁合わせ）が通る経路をそのまま固定する。
#[test]
fn cover_brush_falloff_at_is_bit_identical_to_legacy() {
    let spec = test_cover_spec(TEST_RADIUS);
    let mut dz = -TEST_RADIUS * 1.5;
    while dz <= TEST_RADIUS * 1.5 {
        let mut dx = -TEST_RADIUS * 1.5;
        while dx <= TEST_RADIUS * 1.5 {
            // 期待値は **ワールド座標を往復させた差分**から作る。
            // 旧実装も `world_x - center[0]` を計算していたので、こちらが正しい基準である
            // （`dx` をそのまま使うと `(c + dx) - c != dx` の丸めで偽陽性になる）。
            let (wx, wz) = (TEST_CENTER[0] + dx, TEST_CENTER[2] + dz);
            let expected =
                legacy_circular_falloff(wx - TEST_CENTER[0], wz - TEST_CENTER[2], TEST_RADIUS);
            let actual = spec.falloff_at(wx, wz);
            assert_eq!(
                expected.to_bits(),
                actual.to_bits(),
                "falloff_at が旧実装と一致しない (dx={dx} dz={dz})"
            );
            dx += 0.29;
        }
        dz += 0.31;
    }
}

// ============================================================
//  2〜3. 全白 / 全黒マスク
// ============================================================

/// 全白マスクは、ブラシ球の XZ バウンディング正方形の内側で一様にフル強度になること。
///
/// 「白 = フル強度」という約束と、「マスクがあれば円形フォールオフを掛けない」
/// （＝四隅が削られない）という設計判断の両方を固定する。
#[test]
fn white_mask_is_uniform_full_strength_inside_square() {
    let mask = uniform_mask(MASK_WHITE);
    let half = TEST_RADIUS; // 正方形の半辺 = 半径

    // 正方形の内側（四隅を含む）は必ず 1.0。
    for &(dx, dz) in &[
        (0.0f32, 0.0f32),
        (half * 0.99, 0.0),
        (0.0, -half * 0.99),
        (half * 0.99, half * 0.99),   // 球の外側にある四隅
        (-half * 0.99, -half * 0.99), // 対角の四隅
    ] {
        let f = brush_shape_factor(
            Some(&mask),
            [TEST_CENTER[0], TEST_CENTER[2]],
            TEST_RADIUS,
            [TEST_CENTER[0] + dx, TEST_CENTER[2] + dz],
            (dx * dx + dz * dz).sqrt(),
        );
        assert!(
            (f - 1.0).abs() < SHAPE_EPS,
            "全白マスクの正方形内は 1.0 のはず (dx={dx} dz={dz} f={f})"
        );
    }

    // 正方形の外側は 0（UV が 0..1 の外 → `CoverMask::sample` が 0 を返す）。
    for &(dx, dz) in &[(half * 1.01, 0.0f32), (0.0, -half * 1.01)] {
        let f = brush_shape_factor(
            Some(&mask),
            [TEST_CENTER[0], TEST_CENTER[2]],
            TEST_RADIUS,
            [TEST_CENTER[0] + dx, TEST_CENTER[2] + dz],
            (dx * dx + dz * dz).sqrt(),
        );
        assert_eq!(f, 0.0, "正方形の外は 0 のはず (dx={dx} dz={dz})");
    }
}

/// 全黒マスクではレイヤペイントが 1 チャンクも触らないこと（＝何も変わらない）。
#[test]
fn black_mask_paints_nothing() {
    let settings = TerrainSettings::default();
    let mut chunks = empty_chunks(&settings);
    let mask = uniform_mask(MASK_BLACK);
    let brush = SphereBrush {
        center: TEST_CENTER,
        radius: TEST_RADIUS,
        strength: 1.0,
    };

    let mut view = PaintView { settings: &settings, chunks: &mut chunks };
    let affected = apply_paint_with_mask(&mut view, &brush, 1, 1.0, Some(&mask));

    assert!(affected.is_empty(), "全黒マスクではどのチャンクも触らないこと");
    // 「触っていない」ことを場の側からも確認する（ブラシ中心のペイント量が 0 のまま）。
    let voxel = settings.voxel_size;
    let g = |v: f32| (v / voxel).round() as i32;
    let (_, amount) = view.read_paint_global(g(TEST_CENTER[0]), g(TEST_CENTER[1]), g(TEST_CENTER[2]));
    assert_eq!(amount, 0.0, "全黒マスクではブラシ中心も 1 ビットも変わらないこと");
}

// ============================================================
//  4. 向き（左右・上下）
// ============================================================

/// UV 規約: `u = (x - cx)/(2r) + 0.5` / `v = (z - cz)/(2r) + 0.5` であること。
#[test]
fn brush_mask_uv_follows_documented_convention() {
    let center = [TEST_CENTER[0], TEST_CENTER[2]];
    // 中心 → (0.5, 0.5)
    let (u, v) = brush_mask_uv(center, TEST_RADIUS, center);
    assert!((u - 0.5).abs() < SHAPE_EPS && (v - 0.5).abs() < SHAPE_EPS, "中心は (0.5,0.5)");

    // +X 側の縁（+r） → u = 1.0 / -Z 側の縁（-r） → v = 0.0
    let (u, _) = brush_mask_uv(center, TEST_RADIUS, [center[0] + TEST_RADIUS, center[1]]);
    assert!((u - 1.0).abs() < SHAPE_EPS, "+X の縁は u=1.0 (u={u})");
    let (_, v) = brush_mask_uv(center, TEST_RADIUS, [center[0], center[1] - TEST_RADIUS]);
    assert!(v.abs() < SHAPE_EPS, "-Z の縁は v=0.0 (v={v})");

    // 半径 0 は UV を定義できない → 範囲外（sample が 0 を返す値）
    let (u, v) = brush_mask_uv(center, 0.0, center);
    assert!(!(0.0..=1.0).contains(&u) && !(0.0..=1.0).contains(&v), "半径 0 は範囲外 UV");
}

/// 非対称マスクの左右・上下がワールドへ正しく写ること。
///
/// 画像の左上（x=0, y=0）1 テクセルだけを白にしたマスクを使う。
/// 規約どおりなら、白い部分はワールドの **-X かつ -Z** の象限に落ちる
/// （u<0.5 が -X、v<0.5 が -Z）。他の 3 象限は 0 でなければならない。
#[test]
fn mask_left_right_and_near_far_map_to_world_axes() {
    let mask = corner_mask();
    let quarter = TEST_RADIUS * 0.5; // 象限の中心あたり（正方形の 1/4 位置）
    let sample = |dx: f32, dz: f32| {
        brush_shape_factor(
            Some(&mask),
            [TEST_CENTER[0], TEST_CENTER[2]],
            TEST_RADIUS,
            [TEST_CENTER[0] + dx, TEST_CENTER[2] + dz],
            (dx * dx + dz * dz).sqrt(),
        )
    };

    assert!(
        (sample(-quarter, -quarter) - 1.0).abs() < SHAPE_EPS,
        "画像の左上は ワールドの -X/-Z 象限へ写ること"
    );
    assert_eq!(sample(quarter, -quarter), 0.0, "+X/-Z 象限は黒のはず");
    assert_eq!(sample(-quarter, quarter), 0.0, "-X/+Z 象限は黒のはず");
    assert_eq!(sample(quarter, quarter), 0.0, "+X/+Z 象限は黒のはず");
}

// ============================================================
//  5. 無効マスクの縮退（安全側）
// ============================================================

/// 読み込みに失敗したマスク（`CoverMask::empty()`）は「効果 0」ではなく、
/// **従来の円形フォールオフへ縮退する**こと。
///
/// 【なぜ 0 にしないのか】
///   パスの打ち間違いや画像の破損でブラシが完全に無反応になると、
///   ユーザーには「地形編集が壊れた」としか見えず原因に辿り着けない。
///   円へ戻るだけなら「マスクが効いていない」と一目で分かり、被害が
///   「形が付かない」だけで済む（安全側）。
#[test]
fn invalid_mask_degrades_to_circular_falloff() {
    let empty = CoverMask::empty();
    assert!(!brush_mask_is_active(Some(&empty)), "空マスクは非アクティブ扱い");

    let spec = test_cover_spec(TEST_RADIUS);
    for &(dx, dz) in &[(0.0f32, 0.0f32), (1.0, 0.5), (TEST_RADIUS * 0.9, 0.0)] {
        let with_empty =
            spec.falloff_at_masked(TEST_CENTER[0] + dx, TEST_CENTER[2] + dz, Some(&empty));
        let without = spec.falloff_at(TEST_CENTER[0] + dx, TEST_CENTER[2] + dz);
        assert_eq!(
            with_empty.to_bits(),
            without.to_bits(),
            "無効マスクはマスク未指定と同じ結果になること (dx={dx} dz={dz})"
        );
        assert!(with_empty > 0.0, "半径内では効き続けること（黙って無反応にしない）");
    }
}

// ============================================================
//  6. マスク時の棄却範囲（正方形）
// ============================================================

/// マスク指定時は、ブラシ球の外側にある「正方形の四隅」も塗られること。
///
/// マスク未指定では球の外なので塗られない位置を選び、**両者の差**で確認する。
/// これが無いと、正方形いっぱいの絵（矩形スタンプ）を貼っても角が丸く欠ける。
#[test]
fn mask_paints_square_corners_outside_the_sphere() {
    let settings = TerrainSettings::default();
    let brush = SphereBrush {
        center: TEST_CENTER,
        radius: TEST_RADIUS,
        strength: 1.0,
    };
    // 四隅寄りのサンプル位置（中心から XZ にそれぞれ 0.9r ＝ 3D 距離 1.27r で球の外）。
    let corner = [
        TEST_CENTER[0] + TEST_RADIUS * 0.9,
        TEST_CENTER[1],
        TEST_CENTER[2] + TEST_RADIUS * 0.9,
    ];
    let voxel = settings.voxel_size;
    let g = |v: f32| (v / voxel).round() as i32;
    let (gx, gy, gz) = (g(corner[0]), g(corner[1]), g(corner[2]));

    // ─── マスク未指定: 球の外なので塗られない ───
    let mut chunks = empty_chunks(&settings);
    {
        let mut view = PaintView { settings: &settings, chunks: &mut chunks };
        apply_paint_with_mask(&mut view, &brush, 1, 1.0, None);
        let (_, amount) = view.read_paint_global(gx, gy, gz);
        assert_eq!(amount, 0.0, "マスク未指定では球の外は塗られないこと");
    }

    // ─── 全白マスク: 正方形の内側なので塗られる ───
    let mask = uniform_mask(MASK_WHITE);
    let mut chunks = empty_chunks(&settings);
    {
        let mut view = PaintView { settings: &settings, chunks: &mut chunks };
        apply_paint_with_mask(&mut view, &brush, 1, 1.0, Some(&mask));
        let (_, amount) = view.read_paint_global(gx, gy, gz);
        assert!(amount > 0.0, "全白マスクでは正方形の四隅も塗られること (amount={amount})");
    }
}
