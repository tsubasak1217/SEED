// ============================================================
//  terrain/cover/tests_cover.rs — カバー場（I3.1）のユニットテスト
//
//  本ファイルが固定する契約:
//    1. 傾斜ルール — 急斜面には積もらず、緩斜面には満額積もる
//    2. 素材置き換え規則 — 後から積もる素材が古い素材を削って置き換わる
//    3. シリアライズ往復 — .tcover はビット単位で往復する
//    4. **エミッタ無し＝完全不変** — カバー場も、そこから作る絵も 1 ビットも変わらない
// ============================================================

use super::accumulate::accumulate_chunk;
use super::brush::{
    brush_chunk as brush_cover_chunk, brush_chunk_with_mask, CoverBrushMode, CoverBrushSpec,
};
use super::emit::{CoverEmitRange, CoverEmitSpec, CoverMask};
use super::field::{
    cover_y_match_tolerance, slope_scale, CoverField, CoverNeighborhood, CoverSurface,
    COVER_BASE_Y_ABSENT, COVER_FIELD_RESOLUTION, COVER_SLOPE_UP_FULL, COVER_SLOPE_UP_MIN,
    COVER_SURFACE_ABSENT,
};
use super::material::{CoverMaterial, CoverMaterialSet, COVER_MATERIAL_NONE};
use super::tcover::{read_chunk, write_chunk, TcoverError, TCOVER_MAGIC, TCOVER_VERSION};
use super::trample::{
    resolve_forward_xz, stamp_chunk, stamp_chunk_tracked, CoverStampShape, CoverStampSpec,
    COVER_STAMP_GROUND_SNAP_DROP,
};
use crate::engine::terrain::chunk_coord::ChunkCoord;
use crate::engine::terrain::chunk_data::TerrainChunkData;
use crate::engine::terrain::settings::TerrainSettings;

// ─── テスト用ヘルパ ──────────────────────────────────────────────────────────

/// テストで使う「面のワールド Y」。既定の地面平面（density = ワールド Y）の面は y=0。
const TEST_SURFACE_Y: f32 = 0.0;

/// テスト用の素材セット（雪 = 添字 0・落ち葉 = 添字 1）。
///
/// 積算・スタンプは素材から「埋め戻し速度」「足跡の残りやすさ」を引くため、
/// 素材セット無しには走らない。轍が確実に付く値を入れてある。
fn test_materials() -> CoverMaterialSet {
    CoverMaterialSet {
        materials: vec![
            CoverMaterial {
                id: "snow".to_string(),
                displacement: 0.15,
                refill_rate: 0.0,
                footprint_persistence: 1.0,
                trample_darkening: 0.2,
                ..CoverMaterial::default()
            },
            CoverMaterial {
                id: "leaf".to_string(),
                displacement: 0.04,
                refill_rate: 0.0,
                footprint_persistence: 0.5,
                trample_darkening: 0.1,
                ..CoverMaterial::default()
            },
        ],
        // 積算ティック間隔はこれらのテストの対象外なので既定のままでよい。
        ..CoverMaterialSet::default()
    }
}

/// 既定チャンクの Y 照合許容差を使う 3×3×3 ビューを作る（テストの定型）。
fn test_view<'a>(
    lookup: impl FnMut(i32, i32, i32) -> Option<&'a CoverField>,
) -> CoverNeighborhood<'a> {
    let tolerance = cover_y_match_tolerance(TerrainSettings::default().chunk_extent());
    CoverNeighborhood::from_lookup(tolerance, lookup)
}

/// 素材添字（テストで使う値。0 = 雪相当、1 = 落ち葉相当）。
const MAT_SNOW: u8 = 0;
const MAT_LEAF: u8 = 1;

/// 全テクセルが水平面（up = 1.0・高さ y）である地表情報を作る。
fn flat_surface(y: f32) -> CoverSurface {
    // `CoverSurface` のフィールドは private なので、密度チャンクから作るのが正道。
    // 「水平地面のチャンク」を作って from_chunk に通す（実装経路と同じ道を通す）。
    //
    // 【なぜ y=-1 のチャンクを使うのか】
    //   `from_ground_plane` は density = ワールド Y なので、面（density = iso = 0）は
    //   ちょうど y=0 に来る。個体は density < iso 側、すなわち **y<0 側のチャンク**であり、
    //   メッシュ（マーチングキューブのセル）も面情報もそちらのチャンクが持つ
    //   （y=0..16 のチャンクは完全に空気で、面もメッシュも持たない）。
    let settings = TerrainSettings::default();
    let chunk = TerrainChunkData::from_ground_plane(&settings, ChunkCoord::new(0, -1, 0));
    // 面がちょうど y へ来るよう、チャンク原点は 1 チャンクぶん下に置く。
    CoverSurface::from_chunk(&chunk, &settings, y - settings.chunk_extent())
}

/// 全域エミッタ（素材 `mat`・強度 `rate`）を 1 個だけ持つ配列を作る。
fn global_emitter(mat: u8, rate: f32) -> Vec<CoverEmitSpec> {
    vec![CoverEmitSpec {
        range: CoverEmitRange::Global,
        material_index: mat,
        rate,
    }]
}

// ============================================================
//  1. 傾斜ルール
// ============================================================

/// 閾値の外側では 0 / 1 に張り付き、間は単調増加であること。
#[test]
fn slope_scale_is_monotonic_between_thresholds() {
    assert_eq!(slope_scale(0.0), 0.0, "垂直な崖には積もらない");
    assert_eq!(slope_scale(COVER_SLOPE_UP_MIN), 0.0, "閾値ちょうどは 0");
    assert_eq!(slope_scale(1.0), 1.0, "完全な水平面は満額");
    assert_eq!(slope_scale(COVER_SLOPE_UP_FULL), 1.0, "満額閾値ちょうどは 1");

    // 中間は狭義単調増加。
    let mut prev = 0.0;
    let steps = 20;
    for i in 1..steps {
        let up = COVER_SLOPE_UP_MIN
            + (COVER_SLOPE_UP_FULL - COVER_SLOPE_UP_MIN) * (i as f32 / steps as f32);
        let v = slope_scale(up);
        assert!(v > prev, "傾斜スケールは単調増加であること (up={up})");
        prev = v;
    }
}

/// 面が無いテクセル（番兵値）と NaN は 0 になること（積算の NaN 汚染を防ぐ）。
#[test]
fn slope_scale_rejects_absent_and_nan() {
    assert_eq!(slope_scale(COVER_SURFACE_ABSENT), 0.0);
    assert_eq!(slope_scale(f32::NAN), 0.0);
    // 非有限値は「壊れた法線」であって「完全な水平面」ではない。
    // 満額側へ倒すと 1 テクセルの異常値が最大量の積雪として可視化するため、
    // 安全側（積もらない）へ落とす。
    assert_eq!(slope_scale(f32::INFINITY), 0.0, "非有限値は積もらせない");
    assert_eq!(slope_scale(f32::NEG_INFINITY), 0.0);
}

/// 急斜面のチャンクでは、平地チャンクより積もる量が明確に少ないこと。
///
/// 傾斜ルールが「実際の密度場から導いた法線」で効いていることの検証
/// （`slope_scale` 単体ではなく積算経路を通す）。
#[test]
fn steep_slope_accumulates_less_than_flat() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    // 面（density = 0）を含むのは y<0 側のチャンクである（`flat_surface` のコメント参照）。
    let coord = ChunkCoord::new(0, -1, 0);

    // ─── 平地: density = worldY（既定の地面平面）───
    let flat = TerrainChunkData::from_ground_plane(&settings, coord);
    let flat_surface = CoverSurface::from_chunk(&flat, &settings, -extent);

    // ─── 急斜面: density = worldY - 4*worldX（傾き 4 ≒ 76 度）───
    let mut steep = TerrainChunkData::new_filled(&settings, 0.0);
    let samples = settings.samples_per_axis();
    for iz in 0..samples {
        for iy in 0..samples {
            for ix in 0..samples {
                let wx = ix as f32 * settings.voxel_size;
                let wy = iy as f32 * settings.voxel_size;
                steep.set_sample(ix, iy, iz, wy - 4.0 * wx);
            }
        }
    }
    let steep_surface = CoverSurface::from_chunk(&steep, &settings, 0.0);

    let emitters = global_emitter(MAT_SNOW, 1.0);
    let mut flat_field = CoverField::new();
    let mut steep_field = CoverField::new();
    accumulate_chunk(
        &mut flat_field, &flat_surface, [0.0; 3], extent, &emitters, &test_materials(), 0.5,
    );
    accumulate_chunk(
        &mut steep_field, &steep_surface, [0.0; 3], extent, &emitters, &test_materials(), 0.5,
    );

    let flat_total: u32 = flat_field.raw_amount().iter().map(|&a| a as u32).sum();
    let steep_total: u32 = steep_field.raw_amount().iter().map(|&a| a as u32).sum();
    assert!(flat_total > 0, "平地には積もること");
    assert!(
        steep_total < flat_total / 2,
        "急斜面の積算量は平地の半分未満であること (flat={flat_total}, steep={steep_total})"
    );
}

// ============================================================
//  2. 素材置き換え規則
// ============================================================

/// 同素材は素直に加算され、上限 1.0 で飽和すること。
#[test]
fn same_material_accumulates_and_saturates() {
    let mut f = CoverField::new();
    f.deposit(0, 0, MAT_SNOW, 0.25);
    assert!((f.amount_at(0, 0) - 0.25).abs() < 0.01);
    f.deposit(0, 0, MAT_SNOW, 0.25);
    assert!((f.amount_at(0, 0) - 0.5).abs() < 0.01);
    // 飽和（上限を超えて積んでも 1.0 で止まる）。
    f.deposit(0, 0, MAT_SNOW, 10.0);
    assert_eq!(f.amount_at(0, 0), 1.0);
    assert_eq!(f.material_at(0, 0), MAT_SNOW);
}

/// 異素材は「まず古い素材を削り、削り切ってから新素材が乗る」こと（1 層仕様の要）。
#[test]
fn different_material_erodes_then_replaces() {
    let mut f = CoverField::new();
    // 落ち葉を 0.5 積む。
    f.deposit(0, 0, MAT_LEAF, 0.5);
    assert_eq!(f.material_at(0, 0), MAT_LEAF);

    // 雪を 0.2 降らせる → 落ち葉が 0.3 まで削れるだけで、素材はまだ落ち葉。
    f.deposit(0, 0, MAT_SNOW, 0.2);
    assert_eq!(f.material_at(0, 0), MAT_LEAF, "削り切るまでは素材は変わらない");
    assert!((f.amount_at(0, 0) - 0.3).abs() < 0.01);

    // さらに雪を 0.5 降らせる → 落ち葉 0.3 を削り切り、余り 0.2 が雪として乗る。
    f.deposit(0, 0, MAT_SNOW, 0.5);
    assert_eq!(f.material_at(0, 0), MAT_SNOW, "削り切ったら新素材へ置き換わる");
    assert!(
        (f.amount_at(0, 0) - 0.2).abs() < 0.01,
        "余りぶんだけが新素材の初期量になる (got {})",
        f.amount_at(0, 0)
    );
}

/// 空のテクセルへ積むと、その素材が即座に入ること（削る対象が無いため）。
#[test]
fn empty_texel_takes_material_immediately() {
    let mut f = CoverField::new();
    f.deposit(3, 7, MAT_LEAF, 0.1);
    assert_eq!(f.material_at(3, 7), MAT_LEAF);
    assert!(f.amount_at(3, 7) > 0.0);
}

/// 0 以下・非有限の delta は何もしないこと（NaN が場へ入らない保証）。
#[test]
fn deposit_ignores_non_positive_and_non_finite() {
    let mut f = CoverField::new();
    f.deposit(0, 0, MAT_SNOW, 0.0);
    f.deposit(0, 0, MAT_SNOW, -1.0);
    f.deposit(0, 0, MAT_SNOW, f32::NAN);
    f.deposit(0, 0, MAT_SNOW, f32::INFINITY);
    assert!(f.is_empty(), "不正な delta では場が変化しないこと");
}

// ============================================================
//  3. シリアライズ往復
// ============================================================

/// 書き出し → 読み戻しで、カバー場と座標がビット単位で往復すること。
#[test]
fn tcover_round_trips_exactly() {
    let mut f = CoverField::new();
    // 決定的な擬似乱数で場を埋める（乱数クレートに依存しない）。
    let mut state: u32 = 0x9E37_79B9;
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
            let mat = ((state >> 16) % 4) as u8;
            let amt = ((state >> 8) & 0xFF) as f32 / 255.0;
            f.deposit(ix, iz, mat, amt);
        }
    }

    let coord = ChunkCoord::new(-3, 7, 11);
    let bytes = write_chunk(&f, coord);
    assert_eq!(&bytes[0..4], &TCOVER_MAGIC, "マジックが先頭に来ること");

    let (back, back_coord) = read_chunk(&bytes).expect("読み戻せること");
    assert_eq!(back_coord, coord);
    assert_eq!(back, f, "カバー場がビット単位で往復すること");
}

/// 空のカバー場も往復できること（保存対象外の判定は上位層の責務）。
#[test]
fn tcover_round_trips_empty_field() {
    let f = CoverField::new();
    let coord = ChunkCoord::new(0, 0, 0);
    let (back, _) = read_chunk(&write_chunk(&f, coord)).expect("読み戻せること");
    assert_eq!(back, f);
    assert!(back.is_empty());
}

/// 壊れたバイト列は黙って読まずエラーになること。
#[test]
fn tcover_rejects_corrupt_bytes() {
    let f = CoverField::new();
    let coord = ChunkCoord::new(0, 0, 0);
    let good = write_chunk(&f, coord);

    // マジック違い。
    let mut bad_magic = good.clone();
    bad_magic[0] = b'X';
    assert_eq!(read_chunk(&bad_magic), Err(TcoverError::BadMagic));

    // バージョン違い。
    let mut bad_version = good.clone();
    bad_version[4] = 99;
    assert_eq!(read_chunk(&bad_version), Err(TcoverError::BadVersion));

    // 途中で切れている。
    assert_eq!(read_chunk(&good[..10]), Err(TcoverError::Truncated));

    // 末尾に余分なバイト（＝サイズ不一致）。
    let mut extra = good.clone();
    extra.push(0);
    assert_eq!(read_chunk(&extra), Err(TcoverError::SizeMismatch));

    // 解像度違い（ヘッダの resolution だけ書き換える）。
    let mut bad_res = good;
    bad_res[20] = 99;
    assert_eq!(read_chunk(&bad_res), Err(TcoverError::ResolutionMismatch));
}

// ============================================================
//  4. エミッタ無し＝完全不変
// ============================================================

/// エミッタが 1 つも無ければカバー場は 1 ビットも変わらず、変化フラグも立たないこと。
///
/// これが「カバー場を持たないチャンク・量ゼロのチャンクは従来と完全同一」の根拠。
#[test]
fn no_emitters_leaves_field_bit_identical() {
    let settings = TerrainSettings::default();
    let surface = flat_surface(0.0);
    let before = CoverField::new();
    let mut field = before.clone();

    let changed = accumulate_chunk(
        &mut field,
        &surface,
        [0.0; 3],
        settings.chunk_extent(),
        &[],
        &test_materials(),
        1.0 / 60.0,
    );
    assert!(!changed, "エミッタ無しでは変化フラグが立たないこと");
    assert_eq!(field, before, "エミッタ無しでは場が 1 ビットも変わらないこと");
}

/// 強度 0 のエミッタ・dt 0・範囲外のエミッタでも場は変わらないこと。
#[test]
fn zero_rate_or_zero_dt_or_out_of_range_changes_nothing() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(0.0);
    let before = CoverField::new();

    // 強度 0。
    let mut f = before.clone();
    assert!(!accumulate_chunk(&mut f, &surface, [0.0; 3], extent, &global_emitter(MAT_SNOW, 0.0), &test_materials(), 1.0));
    assert_eq!(f, before);

    // dt 0。
    let mut f = before.clone();
    assert!(!accumulate_chunk(&mut f, &surface, [0.0; 3], extent, &global_emitter(MAT_SNOW, 1.0), &test_materials(), 0.0));
    assert_eq!(f, before);

    // 遠くの Region（チャンク AABB にかからない）。
    let far = vec![CoverEmitSpec {
        range: CoverEmitRange::Region {
            center: [1000.0, 0.0, 1000.0],
            half_extents: [1.0, 1.0, 1.0],
            fade: 0.0,
        },
        material_index: MAT_SNOW,
        rate: 1.0,
    }];
    let mut f = before.clone();
    assert!(!accumulate_chunk(&mut f, &surface, [0.0; 3], extent, &far, &test_materials(), 1.0));
    assert_eq!(f, before);
}

/// 面が無いチャンク（全て空気）には積もらないこと。
#[test]
fn chunk_without_surface_accumulates_nothing() {
    let settings = TerrainSettings::default();
    // 全サンプルが空気（density > iso）＝面が 1 枚も無い。
    let air = TerrainChunkData::new_filled(&settings, settings.density_clamp);
    let surface = CoverSurface::from_chunk(&air, &settings, 0.0);
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            assert!(!surface.has_surface(ix, iz), "空気だけのチャンクに面は無い");
        }
    }

    let mut f = CoverField::new();
    let changed = accumulate_chunk(
        &mut f,
        &surface,
        [0.0; 3],
        settings.chunk_extent(),
        &global_emitter(MAT_SNOW, 1.0),
        &test_materials(),
        1.0,
    );
    assert!(!changed);
    assert!(f.is_empty());
}

// ============================================================
//  エミッタ範囲の評価
// ============================================================

/// Region の境界フェードが、内側で 1・外側で 0・その間で単調に落ちること。
#[test]
fn region_fade_falls_off_at_boundary() {
    let spec = CoverEmitSpec {
        range: CoverEmitRange::Region {
            center: [0.0, 0.0, 0.0],
            half_extents: [10.0, 10.0, 10.0],
            fade: 4.0,
        },
        material_index: MAT_SNOW,
        rate: 1.0,
    };
    assert_eq!(spec.coverage_at([0.0, 0.0, 0.0]), 1.0, "中心は満額");
    assert_eq!(spec.coverage_at([20.0, 0.0, 0.0]), 0.0, "範囲外は 0");
    // 境界から内側 2m（フェード幅 4m の半分）＝ 0.5。
    let mid = spec.coverage_at([8.0, 0.0, 0.0]);
    assert!((mid - 0.5).abs() < 1.0e-5, "フェード中間は 0.5 (got {mid})");
    // 境界ちょうどは 0（連続）。
    assert!(spec.coverage_at([10.0, 0.0, 0.0]).abs() < 1.0e-5);
}

/// フェード 0 の Region は硬い境界（内側は常に満額）になること。
#[test]
fn region_without_fade_has_hard_edge() {
    let spec = CoverEmitSpec {
        range: CoverEmitRange::Region {
            center: [0.0, 0.0, 0.0],
            half_extents: [5.0, 5.0, 5.0],
            fade: 0.0,
        },
        material_index: MAT_SNOW,
        rate: 1.0,
    };
    assert_eq!(spec.coverage_at([4.99, 0.0, 0.0]), 1.0);
    assert_eq!(spec.coverage_at([5.01, 0.0, 0.0]), 0.0);
}

/// TextureMask が白=満額・黒=0 で読まれ、矩形外は 0 になること。
#[test]
fn texture_mask_reads_white_as_full_and_black_as_zero() {
    // 左半分が黒・右半分が白の 2×1 マスク。
    let mask = CoverMask { width: 2, height: 1, pixels: vec![0, 255] };
    let spec = CoverEmitSpec {
        range: CoverEmitRange::TextureMask {
            center: [0.0, 0.0, 0.0],
            size_xz: [10.0, 10.0],
            mask,
        },
        material_index: MAT_SNOW,
        rate: 1.0,
    };
    assert_eq!(spec.coverage_at([-4.0, 0.0, 0.0]), 0.0, "左半分（黒）は 0");
    assert_eq!(spec.coverage_at([4.0, 0.0, 0.0]), 1.0, "右半分（白）は満額");
    assert_eq!(spec.coverage_at([100.0, 0.0, 0.0]), 0.0, "矩形外は 0");
}

/// 無効なマスク（画素なし）は常に 0 を返すこと（読み込み失敗時の安全な縮退）。
#[test]
fn invalid_mask_yields_zero_coverage() {
    let spec = CoverEmitSpec {
        range: CoverEmitRange::TextureMask {
            center: [0.0, 0.0, 0.0],
            size_xz: [10.0, 10.0],
            mask: CoverMask::empty(),
        },
        material_index: MAT_SNOW,
        rate: 1.0,
    };
    assert_eq!(spec.coverage_at([0.0, 0.0, 0.0]), 0.0);
}

// ============================================================
//  サンプリング（頂点へ載せるときの読み方）
// ============================================================

/// 一様な場を（隣接チャンクも同じ場なら）どこで読んでも同じ値が返ること。
///
/// バイリニアの重みの総和が 1 であること＋境界でも隣を読めていることの保証。
#[test]
fn uniform_field_samples_uniformly() {
    let f = uniform_field(MAT_SNOW, 0.5);
    // 周囲 8 チャンクにも同じ場がある状況（＝広い雪原の内部）。
    let view = test_view(|dx, dy, dz| if dy == 0 { Some(&f) } else { None });
    for &(u, v) in &[(0.0, 0.0), (0.5, 0.5), (1.0, 1.0), (0.123, 0.987)] {
        let s = view.sample(u, v, TEST_SURFACE_Y);
        let (a, m) = (s.amount, s.material);
        assert!((a - 0.5).abs() < 0.01, "一様な場は一様に読めること (u={u},v={v},a={a})");
        assert_eq!(m, MAT_SNOW);
    }
}

/// 範囲外・非有限の UV は 0..1 へクランプされ、パニックしないこと。
#[test]
fn sample_clamps_out_of_range_uv() {
    let f = uniform_field(MAT_LEAF, 1.0);
    let view = test_view(|_, dy, _| if dy == 0 { Some(&f) } else { None });
    let s = view.sample(-5.0, -5.0, TEST_SURFACE_Y);
    assert_eq!(s.amount, 1.0, "範囲外 UV は端（u=0）として読まれること");
    assert_eq!(s.material, MAT_LEAF);
    let nan = view.sample(f32::NAN, f32::NAN, TEST_SURFACE_Y);
    assert_eq!(nan.amount, 1.0, "NaN は 0 側の端へクランプされること");
}

/// 隣接チャンクにカバー場が無い側は「量 0」として読まれること（世界の端の規約）。
#[test]
fn missing_neighbour_reads_as_zero() {
    let f = uniform_field(MAT_SNOW, 1.0);
    let isolated = CoverNeighborhood::isolated(&f);
    // 中央は満量のまま。
    let center = isolated.sample(0.5, 0.5, TEST_SURFACE_Y);
    assert_eq!(center.amount, 1.0, "内部は隣の有無に影響されないこと");
    // 端（u=1.0）は「自分のテクセル 31」と「存在しない隣のテクセル 0」の中点＝半分。
    let edge = isolated.sample(1.0, 0.5, TEST_SURFACE_Y);
    assert!(
        (edge.amount - 0.5).abs() < 1e-6,
        "隣が無い側は 0 へ向かって落ちること (a={})", edge.amount
    );
    assert_eq!(edge.material, MAT_SNOW, "量のあるテクセルの素材が選ばれること（空側は選ばない）");
    // 角は 4 隅のうち 1 つだけが存在する＝1/4。
    let corner = isolated.sample(1.0, 1.0, TEST_SURFACE_Y);
    assert!(
        (corner.amount - 0.25).abs() < 1e-6,
        "角は 1/4 になること (a={})", corner.amount
    );
}

// ============================================================
//  6. チャンク境界の共有頂点（段差・隙間の回帰テスト）
// ============================================================

/// 境界を共有する 2 チャンクが、同じ 1 点を **f32 のビット単位で同じ値**として読むこと。
///
/// 【何を守っているか】
///   チャンク境界上の地形メッシュ頂点は隣り合うチャンク双方のメッシュへ複製されている。
///   カバーの変位は頂点位置へ焼くので、両者が違う量を読んだ瞬間に複製頂点が
///   別々の場所へ動き、**メッシュに隙間（段差）が開く**。
///   「ほぼ同じ」では不十分で、ビット単位で一致していなければならない
///   （0.001 の差でも 0.15m の変位に掛かれば目に見える段差になりうる）。
///
///   水面グリッド（W5.1）の `grid_lines_are_shared_exactly_between_neighbours` と同じ原則。
#[test]
fn boundary_sample_is_bit_identical_between_neighbours() {
    // 隣り合う 2 チャンク A（左）・B（右）に、わざと違う模様のカバーを積む。
    let a = patterned_field(7);
    let b = patterned_field(31);

    // A から見た B は dx=+1、B から見た A は dx=-1。
    let view_a = test_view(|dx, dy, dz| match (dx, dy, dz) {
        (0, 0, 0) => Some(&a),
        (1, 0, 0) => Some(&b),
        _ => None,
    });
    let view_b = test_view(|dx, dy, dz| match (dx, dy, dz) {
        (0, 0, 0) => Some(&b),
        (-1, 0, 0) => Some(&a),
        _ => None,
    });

    // 境界（A の u=1.0 と B の u=0.0）は同じワールド上の 1 本の線である。
    // v は共有頂点が取りうる任意の位置（端・中央・半端な位置）を広く試す。
    let steps = 64;
    for i in 0..=steps {
        let v = i as f32 / steps as f32;
        let sa = view_a.sample(1.0, v, TEST_SURFACE_Y);
        let sb = view_b.sample(0.0, v, TEST_SURFACE_Y);
        let (amount_a, mat_a) = (sa.amount, sa.material);
        let (amount_b, mat_b) = (sb.amount, sb.material);
        assert_eq!(
            amount_a.to_bits(),
            amount_b.to_bits(),
            "境界の共有点はビット単位で同じ量を読むこと (v={v}, a={amount_a}, b={amount_b})"
        );
        assert_eq!(mat_a, mat_b, "境界の共有点は同じ素材を読むこと (v={v})");
    }
}

/// 境界の共有点で、**法線用の前後サンプル**もビット単位で一致すること。
///
/// 【何を守っているか】
///   カバーの盛り上がりから法線を作るには、頂点の周りを ±半テクセルずらして
///   高さを読む（`sample_extended`）。ここで自チャンクへクランプしてしまうと、
///   境界上の複製頂点は片側からしか差分を取れず、A のメッシュと B のメッシュで
///   **違う法線**になって境界に照明の筋が出る。
///   位置（量）だけでなく勾配まで一致していて初めて継ぎ目が消える。
#[test]
fn boundary_gradient_samples_are_bit_identical_between_neighbours() {
    let a = patterned_field(7);
    let b = patterned_field(31);
    let view_a = test_view(|dx, dy, dz| match (dx, dy, dz) {
        (0, 0, 0) => Some(&a),
        (1, 0, 0) => Some(&b),
        _ => None,
    });
    let view_b = test_view(|dx, dy, dz| match (dx, dy, dz) {
        (0, 0, 0) => Some(&b),
        (-1, 0, 0) => Some(&a),
        _ => None,
    });

    // 法線計算のずらし幅（`COVER_NORMAL_STEP_UV` と同じ半テクセル）。
    let step = 0.5 / COVER_FIELD_RESOLUTION as f32;
    let steps = 64;
    for i in 0..=steps {
        let v = i as f32 / steps as f32;
        // A から見た境界は u=1.0、B から見た境界は u=0.0。前後どちらへずらしても
        // 同じワールド位置を指すので、同じ値でなければならない。
        for offset in [-step, step] {
            let sa = view_a.sample_extended(1.0 + offset, v, TEST_SURFACE_Y);
            let sb = view_b.sample_extended(0.0 + offset, v, TEST_SURFACE_Y);
            assert_eq!(
                sa.amount.to_bits(),
                sb.amount.to_bits(),
                "法線用サンプルもビット単位で一致すること (v={v}, offset={offset})"
            );
            assert_eq!(sa.material, sb.material, "法線用サンプルの素材も一致すること (v={v})");
        }
    }
}

/// 角（4 チャンクが集まる 1 点）でも 4 者が同じ値を読むこと。
///
/// 辺の共有（2 チャンク）が合っていても、角で 4 者が食い違えば
/// そこだけ穴が開く（実際に起きた壊れ方の再現防止）。
#[test]
fn corner_sample_is_bit_identical_between_four_chunks() {
    // 4 チャンク: 00（自分）・10（+X）・01（+Z）・11（+X+Z）。
    let f00 = patterned_field(3);
    let f10 = patterned_field(11);
    let f01 = patterned_field(23);
    let f11 = patterned_field(29);
    let pick = |dx: i32, dz: i32, ox: i32, oz: i32| -> Option<&CoverField> {
        // (ox,oz) は「そのビューにとっての自分」のワールド上のチャンク位置。
        match (ox + dx, oz + dz) {
            (0, 0) => Some(&f00),
            (1, 0) => Some(&f10),
            (0, 1) => Some(&f01),
            (1, 1) => Some(&f11),
            _ => None,
        }
    };
    let view00 = test_view(|dx, dy, dz| if dy == 0 { pick(dx, dz, 0, 0) } else { None });
    let view10 = test_view(|dx, dy, dz| if dy == 0 { pick(dx, dz, 1, 0) } else { None });
    let view01 = test_view(|dx, dy, dz| if dy == 0 { pick(dx, dz, 0, 1) } else { None });
    let view11 = test_view(|dx, dy, dz| if dy == 0 { pick(dx, dz, 1, 1) } else { None });

    // ワールド上の同じ 1 点（4 チャンクが接する角）を、それぞれのローカル UV で読む。
    let samples = [
        view00.sample(1.0, 1.0, TEST_SURFACE_Y),
        view10.sample(0.0, 1.0, TEST_SURFACE_Y),
        view01.sample(1.0, 0.0, TEST_SURFACE_Y),
        view11.sample(0.0, 0.0, TEST_SURFACE_Y),
    ];
    for (i, s) in samples.iter().enumerate().skip(1) {
        assert_eq!(
            s.amount.to_bits(),
            samples[0].amount.to_bits(),
            "角の共有点はビット単位で同じ量を読むこと (view={i})"
        );
        assert_eq!(s.material, samples[0].material, "角の共有点は同じ素材を読むこと (view={i})");
    }
}

/// 積算 → 焼き込みの往路を通しても境界が一致すること（実運用に近い経路の検証）。
///
/// 2 チャンクにまたがる Region エミッタで積算し、境界線上の共有点を両側から読む。
/// 積算側（`accumulate_chunk`）は各チャンクのローカル情報で走るので、
/// 「積算そのものに非対称が入っていないか」もここで一緒に押さえる。
#[test]
fn accumulated_boundary_is_bit_identical_across_two_chunks() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(0.0);

    // 2 チャンクの境界（x = extent）をまたぐ Region エミッタ。
    let emitters = vec![CoverEmitSpec {
        range: CoverEmitRange::Region {
            center: [extent, 0.0, extent * 0.5],
            half_extents: [extent * 0.75, extent, extent],
            fade: extent * 0.5,
        },
        material_index: MAT_SNOW,
        rate: 0.3,
    }];

    let mut left = CoverField::new();
    let mut right = CoverField::new();
    accumulate_chunk(
        &mut left, &surface, [0.0, 0.0, 0.0], extent, &emitters, &test_materials(), 1.0,
    );
    accumulate_chunk(
        &mut right, &surface, [extent, 0.0, 0.0], extent, &emitters, &test_materials(), 1.0,
    );
    assert!(!left.is_empty() && !right.is_empty(), "両チャンクに積もっていること");

    let view_left = test_view(|dx, dy, dz| match (dx, dy, dz) {
        (0, 0, 0) => Some(&left),
        (1, 0, 0) => Some(&right),
        _ => None,
    });
    let view_right = test_view(|dx, dy, dz| match (dx, dy, dz) {
        (0, 0, 0) => Some(&right),
        (-1, 0, 0) => Some(&left),
        _ => None,
    });

    let steps = 32;
    for i in 0..=steps {
        let v = i as f32 / steps as f32;
        let sl = view_left.sample(1.0, v, TEST_SURFACE_Y);
        let sr = view_right.sample(0.0, v, TEST_SURFACE_Y);
        let (al, ml) = (sl.amount, sl.material);
        let (ar, mr) = (sr.amount, sr.material);
        assert_eq!(al.to_bits(), ar.to_bits(), "境界の変位量が一致すること (v={v})");
        assert_eq!(ml, mr, "境界の素材が一致すること (v={v})");
        assert!(al > 0.0, "エミッタの真下なので実際に積もっていること (v={v})");
    }
}

/// カバー場を持つチャンクと、メッシュ（マーチングキューブのセル）を持つチャンクが一致すること。
///
/// 【なぜこれが要るか】
///   面情報の air/solid 判定がマーチングキューブと 1 段ずれていると、
///   「雪はチャンク A のカバー場に積もるのに、その面のメッシュはチャンク B が持つ」
///   という状態になり、積もった雪がどの頂点にも焼かれず**一切見えない**。
///   既定の平坦地面（density = ワールド Y、面がちょうど y=0）はまさにこの境界例で、
///   実際に「雪が見えない／チャンク境界で途切れる」不具合の原因だった。
#[test]
fn cover_surface_chunk_matches_mesh_chunk() {
    use crate::engine::terrain::marching_cubes;
    let settings = TerrainSettings::default();

    // 面（density = iso = 0）がちょうど境界 y=0 に来る既定の地面平面。
    for (coord, expect_surface) in [(ChunkCoord::new(0, -1, 0), true), (ChunkCoord::new(0, 0, 0), false)] {
        let chunk = TerrainChunkData::from_ground_plane(&settings, coord);
        let mesh = marching_cubes::generate_standalone(&chunk, &settings);
        let has_mesh = !mesh.positions.is_empty();
        let surface = CoverSurface::from_chunk(&chunk, &settings, coord.world_origin(&settings)[1]);
        let has_surface = (0..COVER_FIELD_RESOLUTION)
            .any(|iz| (0..COVER_FIELD_RESOLUTION).any(|ix| surface.has_surface(ix, iz)));
        assert_eq!(
            has_mesh, expect_surface,
            "メッシュを持つのは個体側のチャンクだけであること (coord={coord:?})"
        );
        assert_eq!(
            has_surface, has_mesh,
            "面情報を持つチャンクとメッシュを持つチャンクは一致すること (coord={coord:?})"
        );
    }
}

// ─── 境界テスト用のヘルパ ────────────────────────────────────────────────────

/// 全テクセルが同じ素材・同じ量のカバー場を作る。
fn uniform_field(material: u8, amount: f32) -> CoverField {
    let mut f = CoverField::new();
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            f.deposit(ix, iz, material, amount);
            // 量を持つテクセルの基準 Y は必ず有限、という不変条件を満たしておく
            // （実経路では積算がこれを設定する）。
            f.set_base_y(ix, iz, TEST_SURFACE_Y);
        }
    }
    f
}

/// テクセルごとに量も素材も変わる模様入りのカバー場を作る。
///
/// 境界の一致を「たまたま両側が同じ値だった」で通してしまわないよう、
/// 隣り合うテクセルの値が必ず異なるようにする（`seed` で模様をずらす）。
fn patterned_field(seed: usize) -> CoverField {
    let mut f = CoverField::new();
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            // 0 でない量を全テクセルへ（0 だと素材選択が縮退して差が出ない）。
            let n = (ix * 7 + iz * 13 + seed) % COVER_FIELD_RESOLUTION;
            let amount = (n + 1) as f32 / (COVER_FIELD_RESOLUTION + 1) as f32;
            let material = if (ix + iz + seed) % 2 == 0 { MAT_SNOW } else { MAT_LEAF };
            f.deposit(ix, iz, material, amount);
            f.set_base_y(ix, iz, TEST_SURFACE_Y);
        }
    }
    f
}

// ============================================================
//  素材セット（データドリブンの入口）
// ============================================================

/// 既定セットの ID が、サンプルアセットの ID と一致していること。
///
/// ここがずれると「アセットを置いた瞬間に見た目が変わる」ため、
/// 組み込み既定とアセットは同じ ID・同じ意味でなければならない。
#[test]
fn default_material_ids_match_sample_asset_ids() {
    let set = CoverMaterialSet::default();
    for id in ["snow", "wet"] {
        assert!(set.index_of(id).is_some(), "既定セットに `{id}` があること");
    }
}

/// **同梱のサンプルアセットが実際にパースできること**。
///
/// `assets/terrain/cover_materials.json` を実ファイルとして取り込んで読む。
/// アセットを手で編集して壊した場合（キーのタイポ・カンマ抜け・型違い）に、
/// 実行して初めて「既定セットへ落ちて見た目が変わった」と気付くのを防ぐ。
#[test]
fn bundled_sample_asset_parses() {
    const SAMPLE: &str = include_str!("../../../../assets/terrain/cover_materials.json");
    let set = CoverMaterialSet::from_json_str(SAMPLE).expect("サンプルアセットが読めること");
    // 組み込み既定と同じ ID が揃っていること（既定 ⇄ アセットの意味の一致）。
    for id in ["snow", "wet"] {
        assert!(set.index_of(id).is_some(), "サンプルアセットに `{id}` があること");
    }
    // 雪は盛り上がり、濡れは盛り上がらない（仕様上の約束）。
    let snow = set.get(set.index_of("snow").unwrap()).unwrap();
    let wet = set.get(set.index_of("wet").unwrap()).unwrap();
    assert!(snow.displacement > 0.0, "雪は変位を持つ");
    assert_eq!(wet.displacement, 0.0, "濡れは変位ゼロ");
    assert!(wet.roughness < snow.roughness, "濡れは粗さが低い（鏡面が立つ）");
}

// ============================================================
//  7. 轍（I3.2）— 踏み固め・Y 照合・埋め戻し・形状
// ============================================================

/// テスト用: 全テクセルに満量の雪が乗り、基準 Y が `y` のカバー場を作る。
fn snowy_field_at(y: f32) -> CoverField {
    let mut f = CoverField::new();
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            f.deposit(ix, iz, MAT_SNOW, 1.0);
            f.set_base_y(ix, iz, y);
        }
    }
    f
}

/// チャンク中央を円形に踏むスタンプ仕様。
fn circle_stamp(contact: [f32; 3], radius: f32, strength: f32) -> CoverStampSpec {
    CoverStampSpec { contact, radius, strength, shape: CoverStampShape::Circle }
}

/// 円形スタンプが接地点の周囲だけを踏み固め、実効の量を減らすこと。
#[test]
fn circle_stamp_presses_only_around_contact() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(0.0);
    let mut field = snowy_field_at(TEST_SURFACE_Y);

    // チャンク中央（8m, 0m, 8m）を半径 1m で踏む。
    let stamps = vec![circle_stamp([extent * 0.5, TEST_SURFACE_Y, extent * 0.5], 1.0, 1.0)];
    let changed = stamp_chunk(
        &mut field, &surface, [0.0; 3], extent, &stamps, &test_materials(),
    );
    assert!(changed, "接地点の周囲は踏み固められること");

    // 中央のテクセル（16,16 = 8m 付近）は踏まれ、隅（0,0）は踏まれない。
    let mid = COVER_FIELD_RESOLUTION / 2;
    assert!(field.trample_at(mid, mid) > 0.0, "接地点は踏み固められる");
    assert_eq!(field.trample_at(0, 0), 0.0, "半径の外は踏まれない");
}

/// 素材に `rim_ratio` があるとき、痕の **外周**の量が増える（縁が盛り上がる）こと。
///
/// 【何を守っているか】
///   凹ませるだけの痕は、真上から光が当たると溝の底も周りも同じ向きを向いていて
///   陰影が出ない。踏んだぶんの素材が縁へ押しのけられて盛り上がることで、
///   痕の周囲に必ず「傾いた面」ができ、どの角度から照らしても輪郭が立つ。
///   縁は踏み固めチャネルではなく **量チャネル**へ足す（＝変位・色・法線の
///   既存経路がそのまま扱う）ので、ここでは量の増加を直接確認する。
#[test]
fn rim_raises_amount_around_footprint() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(0.0);

    // 縁が盛れる余地を残すため、量は 0.5（満額 1.0 だと飽和して差が出ない）。
    let mut field = CoverField::new();
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            field.deposit(ix, iz, MAT_SNOW, 0.5);
            field.set_base_y(ix, iz, TEST_SURFACE_Y);
        }
    }

    // 縁を持つ素材セット（雪の rim_ratio だけを立てる）。
    let mut materials = test_materials();
    materials.materials[MAT_SNOW as usize].rim_ratio = 0.6;

    let mid = COVER_FIELD_RESOLUTION / 2;
    let stamps = vec![circle_stamp([extent * 0.5, TEST_SURFACE_Y, extent * 0.5], 1.0, 1.0)];
    assert!(stamp_chunk(&mut field, &surface, [0.0; 3], extent, &stamps, &materials));

    // 痕の輪郭付近（中心から 1 テクセル外）は量が増えている＝縁が盛れた。
    assert!(
        field.amount_at(mid + 1, mid) > field.amount_at(0, 0),
        "痕の縁は素の面より量が多いこと（rim={} base={}）",
        field.amount_at(mid + 1, mid),
        field.amount_at(0, 0),
    );
    // 遠く離れたテクセルは 1 ビットも触られない。
    assert_eq!(field.amount_at(0, 0), field.amount_at(0, 1), "痕の外は素のまま");
    assert_eq!(field.trample_at(0, 0), 0.0, "痕の外は踏み固められない");
}

/// `rim_ratio` が 0 の素材では縁が一切立たないこと（データ駆動の既定が効く保証）。
///
/// 既存シーンの素材定義（新フィールドを持たない JSON）は既定 0 で読まれるため、
/// **勝手に地形が膨らまない**ことをここで固定する。
#[test]
fn rim_is_absent_when_material_has_no_rim_ratio() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(0.0);
    let mut field = CoverField::new();
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            field.deposit(ix, iz, MAT_SNOW, 0.5);
            field.set_base_y(ix, iz, TEST_SURFACE_Y);
        }
    }

    // test_materials() は rim_ratio を指定していない＝既定 0。
    let materials = test_materials();
    assert_eq!(materials.get(MAT_SNOW as usize).unwrap().rim_ratio, 0.0);

    // 量は u8 量子化されるので、比較の基準は「積んだ直後の実値」を採る。
    let base_amount = field.amount_at(0, 0);
    let mid = COVER_FIELD_RESOLUTION / 2;
    let stamps = vec![circle_stamp([extent * 0.5, TEST_SURFACE_Y, extent * 0.5], 1.0, 1.0)];
    stamp_chunk(&mut field, &surface, [0.0; 3], extent, &stamps, &materials);

    for ix in 0..COVER_FIELD_RESOLUTION {
        assert_eq!(
            field.amount_at(ix, mid),
            base_amount,
            "量チャネルは 1 テクセルも増えないこと (ix={ix})"
        );
    }
}

/// 踏み固めが量を超えると実効の量が 0 になる（＝下地が露出する）こと。
#[test]
fn trample_beyond_amount_exposes_bare_ground() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(0.0);
    // 薄く積もった雪（量 0.3）を満額の強さで踏む。
    let mut field = CoverField::new();
    let mid = COVER_FIELD_RESOLUTION / 2;
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            field.deposit(ix, iz, MAT_SNOW, 0.3);
            field.set_base_y(ix, iz, TEST_SURFACE_Y);
        }
    }
    let stamps = vec![circle_stamp([extent * 0.5, TEST_SURFACE_Y, extent * 0.5], 2.0, 1.0)];
    stamp_chunk(&mut field, &surface, [0.0; 3], extent, &stamps, &test_materials());

    assert!(field.trample_at(mid, mid) > field.amount_at(mid, mid), "踏み固めが量を超えること");
    let view = CoverNeighborhood::isolated(&field);
    let sample = view.sample(0.5, 0.5, TEST_SURFACE_Y);
    assert_eq!(sample.amount, 0.0, "実効の量は 0（下地が露出）");
    assert!(sample.trample > 0.0, "轍としての踏み固めは残る（暗くするために要る）");
    assert_eq!(sample.trample_material, MAT_SNOW, "踏み固めた素材は分かる（色係数を引くため）");
}

/// 接地 Y が離れているテクセルは踏まれないこと（洞窟の床 → 頭上の地表への漏れ防止）。
#[test]
fn stamp_ignores_surfaces_far_in_y() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(0.0);
    let mut field = snowy_field_at(TEST_SURFACE_Y);

    // 面（y=0）から 10m 上を歩いた場合（半径 1m ＝ 許容差 1m）。
    let stamps = vec![circle_stamp([extent * 0.5, 10.0, extent * 0.5], 1.0, 1.0)];
    let changed = stamp_chunk(
        &mut field, &surface, [0.0; 3], extent, &stamps, &test_materials(),
    );
    assert!(!changed, "Y が離れた面は踏まれないこと");
    assert_eq!(field.trample_at(COVER_FIELD_RESOLUTION / 2, COVER_FIELD_RESOLUTION / 2), 0.0);
}

/// 足跡の残りやすさが 0 の素材（濡れなど）には痕が付かないこと。
#[test]
fn stamp_does_nothing_on_non_persistent_material() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(0.0);
    let mut field = snowy_field_at(TEST_SURFACE_Y);

    // 残りやすさ 0 の素材だけを持つセット。
    let materials = CoverMaterialSet {
        materials: vec![CoverMaterial {
            id: "wet".to_string(),
            footprint_persistence: 0.0,
            ..CoverMaterial::default()
        }],
        ..CoverMaterialSet::default()
    };
    let stamps = vec![circle_stamp([extent * 0.5, TEST_SURFACE_Y, extent * 0.5], 2.0, 1.0)];
    assert!(!stamp_chunk(&mut field, &surface, [0.0; 3], extent, &stamps, &materials));
}

/// 積算（降雪）が轍を埋め戻すこと。
#[test]
fn accumulation_refills_trample() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(0.0);
    let mut field = snowy_field_at(TEST_SURFACE_Y);

    // まず深く踏む。
    let stamps = vec![circle_stamp([extent * 0.5, TEST_SURFACE_Y, extent * 0.5], 4.0, 1.0)];
    stamp_chunk(&mut field, &surface, [0.0; 3], extent, &stamps, &test_materials());
    let mid = COVER_FIELD_RESOLUTION / 2;
    let before = field.trample_at(mid, mid);
    assert!(before > 0.0);

    // 降雪を 1 秒ぶん当てる（強度 0.5 → 轍が 0.5 ぶん埋まる）。
    accumulate_chunk(
        &mut field,
        &surface,
        [0.0; 3],
        extent,
        &global_emitter(MAT_SNOW, 0.5),
        &test_materials(),
        1.0,
    );
    let after = field.trample_at(mid, mid);
    assert!(after < before, "降った分だけ轍が浅くなること (before={before}, after={after})");
}

/// テクスチャ形状が進行方向へ回ること（前後に長い痕が向きに追従する）。
#[test]
fn texture_stamp_rotates_with_direction() {
    // 中央 1 列だけが白い縦長のマスク（3×3 の中央列）。
    let mask = CoverMask {
        width: 3,
        height: 3,
        pixels: vec![0, 255, 0, 0, 255, 0, 0, 255, 0],
    };
    // 進行方向 +Z のときは、痕は Z 方向に伸びる（X へずらすと当たらない）。
    let forward_z = CoverStampSpec {
        contact: [0.0, 0.0, 0.0],
        radius: 1.0,
        strength: 1.0,
        shape: CoverStampShape::Texture {
            size: [3.0, 3.0],
            forward_xz: [0.0, 1.0],
            mask: mask.clone(),
        },
    };
    assert!(forward_z.footprint_at(0.0, 1.0) > 0.0, "進行方向には痕が伸びる");
    assert_eq!(forward_z.footprint_at(1.0, 0.0), 0.0, "横方向には痕が無い");

    // 進行方向 +X へ回すと、当たり方がちょうど入れ替わる。
    let forward_x = CoverStampSpec {
        shape: CoverStampShape::Texture {
            size: [3.0, 3.0],
            forward_xz: [1.0, 0.0],
            mask,
        },
        ..forward_z.clone()
    };
    assert!(forward_x.footprint_at(1.0, 0.0) > 0.0, "回した先に痕が伸びる");
    assert_eq!(forward_x.footprint_at(0.0, 1.0), 0.0, "回した後の横方向には痕が無い");
}

// ============================================================
//  7-2. 「動かしても轍が残らない」不具合の再現と回帰（I3.2 修正）
//
//  実機で報告された症状:
//    「雪の上で InteractionSource を持つアクタを動かしても跡が残らない」
//
//  原因は Y 照合の窓が **上下対称**（|面の Y − ソースの Y| ≤ 半径）だったこと。
//  ソースの Y はアクタの原点であり足裏ではないため、原点が腰にある人型・
//  カプセル中心にあるキャラでは常に窓の外へ落ち、1 テクセルも押されなかった。
//
//  以下のテストはエンジン層（app/terrain_cover_ops.rs）の経路を素材だけ差し替えて
//  再現する:
//    ① 実チャンク座標のワールド原点で `is_outside_aabb` によるチャンク選別
//    ② 選ばれたチャンクへ `stamp_chunk` を適用
//  修正前はどちらかの段で必ず落ちる（＝痕が付かない）。
// ============================================================

/// 既定設定における「面を持つチャンク」の座標（地面平面 density = ワールド Y）。
///
/// 面がちょうど y=0 に来るので、個体側＝ y=-16..0 のチャンク（y 添字 -1）が
/// メッシュも面情報も持つ（`flat_surface` のコメントと同じ理由）。
const TEST_GROUND_CHUNK: ChunkCoord = ChunkCoord { x: 0, y: -1, z: 0 };

/// 人型キャラクターのモデル原点が地面より高い典型値（腰の高さ・メートル）。
const TEST_ACTOR_ORIGIN_HEIGHT: f32 = 0.9;

/// 足跡サイズのソース半径（メートル）。許容差の下限より大きく、原点の浮きより小さい。
const TEST_FOOT_RADIUS: f32 = 0.4;

/// エンジン層と同じチャンク選別を行い、選ばれた場合だけスタンプを適用する。
///
/// `apply_cover_stamps` の ①（AABB 交差でチャンクを絞る）と
/// ②（`stamp_cover_chunk` を呼ぶ）を、そのままの順序で再現したもの。
/// 戻り値は「このチャンクのカバー場が変化したか」。
fn engine_like_apply(
    field: &mut CoverField,
    surface: &CoverSurface,
    coord: ChunkCoord,
    settings: &TerrainSettings,
    stamps: &[CoverStampSpec],
    materials: &CoverMaterialSet,
) -> bool {
    let extent = settings.chunk_extent();
    let origin = coord.world_origin(settings);
    let max = [origin[0] + extent, origin[1] + extent, origin[2] + extent];
    // ① チャンク選別（1 つも掛からなければエンジン層はこのチャンクに触らない）。
    if !stamps.iter().any(|s| !s.is_outside_aabb(origin, max)) {
        return false;
    }
    // ② 押し当て。
    stamp_chunk(field, surface, origin, extent, stamps, materials)
}

/// **再現テスト**: アクタの原点が地面より高くても轍が押されること。
///
/// 修正前はチャンク選別を通っても Y 照合で全テクセルが落ち、`changed == false`
/// になっていた（＝実機で「跡が残らない」）。
#[test]
fn stamp_reaches_ground_from_raised_actor_origin() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let mut field = snowy_field_at(TEST_SURFACE_Y);

    // 面は y=0。アクタの原点は腰の高さ（0.9m）にあり、足跡サイズの半径を持つ。
    let stamps = vec![circle_stamp(
        [extent * 0.5, TEST_SURFACE_Y + TEST_ACTOR_ORIGIN_HEIGHT, extent * 0.5],
        TEST_FOOT_RADIUS,
        1.0,
    )];
    // 前提の確認: 原点の浮きは上下対称の許容差（= 半径）を確かに超えている。
    assert!(
        TEST_ACTOR_ORIGIN_HEIGHT > stamps[0].y_tolerance(),
        "この浮き幅が対称窓を超えていないと再現テストにならない"
    );

    let changed = engine_like_apply(
        &mut field, &surface, TEST_GROUND_CHUNK, &settings, &stamps, &test_materials(),
    );
    assert!(changed, "原点が地面より高いアクタでも轍が押されること");
    let mid = COVER_FIELD_RESOLUTION / 2;
    assert!(field.trample_at(mid, mid) > 0.0, "接地点の真下が踏み固められること");
}

/// 接地スナップの下方向は `半径 + COVER_STAMP_GROUND_SNAP_DROP` までであること。
///
/// これ以上浮いたソース（空を飛んでいる・高い足場の上）は地面を踏まない。
#[test]
fn ground_snap_stops_beyond_documented_drop() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let materials = test_materials();

    // 窓のちょうど内側 / ちょうど外側を、境界の両側で確かめる。
    let reach = TEST_FOOT_RADIUS.max(0.25) + COVER_STAMP_GROUND_SNAP_DROP;
    let inside = [extent * 0.5, TEST_SURFACE_Y + reach * 0.99, extent * 0.5];
    let outside = [extent * 0.5, TEST_SURFACE_Y + reach * 1.01, extent * 0.5];

    let mut f_in = snowy_field_at(TEST_SURFACE_Y);
    assert!(
        engine_like_apply(
            &mut f_in, &surface, TEST_GROUND_CHUNK, &settings,
            &[circle_stamp(inside, TEST_FOOT_RADIUS, 1.0)], &materials,
        ),
        "探索距離の内側は踏む"
    );

    let mut f_out = snowy_field_at(TEST_SURFACE_Y);
    assert!(
        !engine_like_apply(
            &mut f_out, &surface, TEST_GROUND_CHUNK, &settings,
            &[circle_stamp(outside, TEST_FOOT_RADIUS, 1.0)], &materials,
        ),
        "探索距離の外側は踏まない（浮いている物は地面に痕を残さない）"
    );
}

/// 面がソースより **上** にある場合は踏まないこと（Y 照合の目的は保たれている）。
///
/// 洞窟の床（下段）を歩いた轍が頭上の地表（上段）へ写らないための不変条件。
/// 接地スナップは下方向にしか広げていないので、この性質は変わらない。
#[test]
fn ground_snap_never_stamps_ceiling_above_source() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let mut field = snowy_field_at(TEST_SURFACE_Y);

    // ソースは面の 3m 下（洞窟の中）。上方向の許容差は半径どまりなので届かない。
    let stamps = vec![circle_stamp(
        [extent * 0.5, TEST_SURFACE_Y - 3.0, extent * 0.5],
        TEST_FOOT_RADIUS,
        1.0,
    )];
    assert!(
        !engine_like_apply(
            &mut field, &surface, TEST_GROUND_CHUNK, &settings, &stamps, &test_materials(),
        ),
        "ソースより上の面は踏まない（頭上の地表への漏れ防止）"
    );
}

/// **再現テスト（統合）**: 雪のチャンクの上を原点の浮いたソースが歩くと、
/// 経路に沿って複数テクセルの轍が残ること。
///
/// エンジン層の毎フレーム処理（前フレーム位置の追跡 → 移動量 → 進行方向 →
/// スタンプ → チャンク選別 → 押し当て）を 1 本のループで再現する。
#[test]
fn moving_source_leaves_a_trail_of_footprints() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let mut field = snowy_field_at(TEST_SURFACE_Y);
    let materials = test_materials();

    // 60Hz で 1m/s ＝ 1 フレーム 1/60 m 進む。チャンク中央を X 方向へ横断する。
    let dt = 1.0 / 60.0;
    let speed = 1.0;
    let frames = 120; // 2 秒ぶん = 2m
    let start_x = extent * 0.5 - 1.0;
    let z = extent * 0.5;

    let mut previous: Option<[f32; 3]> = None;
    let mut forward: Option<[f32; 2]> = None;
    let mut stamped_frames = 0u32;
    for i in 0..frames {
        let pos = [
            start_x + speed * dt * i as f32,
            TEST_SURFACE_Y + TEST_ACTOR_ORIGIN_HEIGHT,
            z,
        ];
        // 初出フレームは移動量が無いのでスタンプしない（エンジン層と同じ規則）。
        let Some(prev) = previous.replace(pos) else { continue };
        let delta = [pos[0] - prev[0], pos[2] - prev[2]];
        forward = Some(resolve_forward_xz(delta, dt, forward));
        let stamps = vec![circle_stamp(pos, TEST_FOOT_RADIUS, 1.0)];
        if engine_like_apply(
            &mut field, &surface, TEST_GROUND_CHUNK, &settings, &stamps, &materials,
        ) {
            stamped_frames += 1;
        }
    }

    assert!(stamped_frames > 0, "移動中のどこかで必ず場が変わること");

    // 経路（z 一定・x が 2m ぶん）に沿って複数のテクセルが踏まれていること。
    let row = COVER_FIELD_RESOLUTION / 2;
    let trail = (0..COVER_FIELD_RESOLUTION)
        .filter(|&ix| field.trample_at(ix, row) > 0.0)
        .count();
    assert!(
        trail >= 4,
        "2m の移動経路に沿って轍が続くこと（踏まれたテクセル数 = {trail}）"
    );
    // 経路から離れた隅は踏まれない（＝全面塗りつぶしではない）。
    assert_eq!(field.trample_at(0, 0), 0.0, "経路の外は踏まれない");
}

/// `stamp_chunk_tracked` が「実際に場を変えたスタンプ」だけを記録すること。
///
/// ギズモの「作用中」色替えが、押した瞬間だけを意味するための契約。
#[test]
fn tracked_stamp_reports_only_effective_sources() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let mut field = snowy_field_at(TEST_SURFACE_Y);
    let materials = test_materials();

    // 0 番: チャンク中央を踏むソース。1 番: 遠く離れていて何も踏まないソース。
    let stamps = vec![
        circle_stamp([extent * 0.5, TEST_SURFACE_Y, extent * 0.5], 1.0, 1.0),
        circle_stamp([extent * 10.0, TEST_SURFACE_Y, extent * 10.0], 1.0, 1.0),
    ];
    let mut hits = vec![false; stamps.len()];
    assert!(stamp_chunk_tracked(
        &mut field, &surface, [0.0, -extent, 0.0], extent, &stamps, &materials, &mut hits,
    ));
    assert!(hits[0], "踏んだソースは記録される");
    assert!(!hits[1], "届かないソースは記録されない");

    // 同じ場所をもう一度踏んでも深さが変わらなければ「作用中」にはならない。
    let mut hits2 = vec![false; stamps.len()];
    let changed = stamp_chunk_tracked(
        &mut field, &surface, [0.0, -extent, 0.0], extent, &stamps, &materials, &mut hits2,
    );
    assert!(!changed, "同じ深さの踏み直しは場を変えない");
    assert!(!hits2[0], "場が変わらないフレームは作用中にしない");
}

// ============================================================
//  8. Y（上下）チャンク境界の一致（I3.1 §7-8 の回帰テスト）
// ============================================================

/// 地表が Y のチャンク境界面を横切る場所で、上下段のメッシュが同じ面を読むこと。
///
/// 【何を守っているか】
///   境界面（world_y = extent）上の頂点は上下段のメッシュへ複製されている。
///   下段の視点（自分 = 下段）と上段の視点（自分 = 上段）で **同じ 1 点**を読み、
///   ビット単位で一致することを確認する。これが崩れると I3.1 §7-8 の筋が出る。
#[test]
fn y_boundary_sample_is_bit_identical_between_stacked_chunks() {
    let extent = TerrainSettings::default().chunk_extent();
    // 下段のチャンクに、境界面のすぐ下（y = extent - 0.1）の面を持つカバーがある。
    let lower = snowy_field_at(extent - 0.1);
    // 上段のチャンクには、境界面のすぐ上（y = extent + 0.1）の面を持つカバーがある。
    let upper = patterned_field_at(5, extent + 0.1);

    // 下段から見ると upper は dy=+1、上段から見ると lower は dy=-1。
    let view_lower = test_view(|dx, dy, dz| match (dx, dy, dz) {
        (0, 0, 0) => Some(&lower),
        (0, 1, 0) => Some(&upper),
        _ => None,
    });
    let view_upper = test_view(|dx, dy, dz| match (dx, dy, dz) {
        (0, 0, 0) => Some(&upper),
        (0, -1, 0) => Some(&lower),
        _ => None,
    });

    // 境界面上（world_y = extent）の点を、上下段それぞれの視点で読む。
    let steps = 32;
    for i in 0..=steps {
        let u = i as f32 / steps as f32;
        let sl = view_lower.sample(u, 0.5, extent);
        let su = view_upper.sample(u, 0.5, extent);
        assert_eq!(
            sl.amount.to_bits(),
            su.amount.to_bits(),
            "Y 境界の共有点はビット単位で同じ量を読むこと (u={u})"
        );
        assert_eq!(sl.material, su.material, "Y 境界の共有点は同じ素材を読むこと (u={u})");
    }
}

/// テスト用: 模様入りで基準 Y が `y` のカバー場。
fn patterned_field_at(seed: usize, y: f32) -> CoverField {
    let mut f = patterned_field(seed);
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            f.set_base_y(ix, iz, y);
        }
    }
    f
}

/// 1 段以上離れた面は候補にならない（＝洞窟の床の轍が地表へ漏れない）こと。
#[test]
fn distant_y_layer_is_not_sampled() {
    let extent = TerrainSettings::default().chunk_extent();
    // 自分の段には面が無く、1 段下（16m 下）にだけ雪がある。
    let below = snowy_field_at(-1.0);
    let view = test_view(|dx, dy, dz| match (dx, dy, dz) {
        (0, -1, 0) => Some(&below),
        _ => None,
    });
    // 上段の面の高さ（y = extent）で読む＝17m 離れているので候補外。
    let s = view.sample(0.5, 0.5, extent);
    assert_eq!(s.amount, 0.0, "許容差を超えて離れた面は読まれないこと");
}

// ============================================================
//  9. TCOVER v2 と v1 互換
// ============================================================

/// 踏み固め・基準 Y を含めて .tcover が往復すること。
#[test]
fn tcover_v2_round_trips_trample_and_base_y() {
    let mut field = snowy_field_at(3.5);
    field.stamp_trample(4, 5, 0.6);
    let coord = ChunkCoord::new(1, -2, 3);

    let bytes = write_chunk(&field, coord);
    assert_eq!(bytes[0..4], TCOVER_MAGIC);
    assert_eq!(u32::from_le_bytes([bytes[4], bytes[5], bytes[6], bytes[7]]), TCOVER_VERSION);

    let (back, back_coord) = read_chunk(&bytes).expect("v2 が読めること");
    assert_eq!(back_coord, coord);
    assert_eq!(back.raw_trample(), field.raw_trample(), "踏み固めが往復すること");
    assert_eq!(
        back.raw_base_y().iter().map(|y| y.to_bits()).collect::<Vec<_>>(),
        field.raw_base_y().iter().map(|y| y.to_bits()).collect::<Vec<_>>(),
        "基準 Y がビット単位で往復すること"
    );
}

/// TCOVER v1（素材＋量だけ）のファイルが読め、轍 0・基準 Y 未知へ移行すること。
#[test]
fn tcover_v1_is_readable_and_migrates() {
    // v1 のバイト列を手で組み立てる（ヘッダのバージョンを 1 にし、本体は素材＋量のみ）。
    let field = snowy_field_at(0.0);
    let coord = ChunkCoord::new(0, 0, 0);
    let mut v1: Vec<u8> = Vec::new();
    v1.extend_from_slice(&TCOVER_MAGIC);
    v1.extend_from_slice(&1u32.to_le_bytes());
    v1.extend_from_slice(&coord.x.to_le_bytes());
    v1.extend_from_slice(&coord.y.to_le_bytes());
    v1.extend_from_slice(&coord.z.to_le_bytes());
    v1.extend_from_slice(&(COVER_FIELD_RESOLUTION as u32).to_le_bytes());
    v1.extend_from_slice(field.raw_material());
    v1.extend_from_slice(field.raw_amount());

    let (back, _) = read_chunk(&v1).expect("v1 が読めること");
    assert_eq!(back.raw_amount(), field.raw_amount(), "量はそのまま読めること");
    assert!(back.raw_trample().iter().all(|&t| t == 0), "轍は 0 として移行すること");
    assert!(
        back.raw_base_y().iter().all(|&y| y == COVER_BASE_Y_ABSENT),
        "基準 Y は未知として移行すること（ロード後に再計算される）"
    );

    // 未知の基準 Y は地表情報から再計算できる。
    let mut migrated = back;
    migrated.refresh_base_y(&flat_surface(0.0));
    assert!(
        migrated.raw_base_y().iter().all(|&y| y.is_finite()),
        "再計算後は全テクセルの基準 Y が有限になること"
    );
}

// ============================================================
//  5. カバーブラシ（地形編集モードの手編集）
// ============================================================
//
//  本節が固定する契約:
//    ・消しゴムはブラシ範囲だけを削り、範囲外を 1 ビットも変えない
//    ・消し切ったテクセルは素材も空へ戻る（.tcover の削除規約と整合する）
//    ・塗りは目標量へ収束する（何往復しても同じ絵になる）
//    ・Y 照合により、地表をなぞったブラシが真下の洞窟の雪を消さない

/// チャンク中央へ当てる消しゴムブラシ仕様。
fn erase_brush(center: [f32; 3], radius: f32, strength: f32) -> CoverBrushSpec {
    CoverBrushSpec {
        center,
        radius,
        strength,
        mode: CoverBrushMode::Erase,
        material_index: MAT_SNOW,
        target_amount: 0.0,
    }
}

/// チャンク中央へ当てる塗りブラシ仕様。
fn paint_brush(center: [f32; 3], radius: f32, strength: f32, mat: u8, target: f32) -> CoverBrushSpec {
    CoverBrushSpec {
        center,
        radius,
        strength,
        mode: CoverBrushMode::Paint,
        material_index: mat,
        target_amount: target,
    }
}

/// 消しゴムはブラシ範囲内だけを削り、範囲外は 1 ビットも変えないこと。
#[test]
fn cover_brush_erase_only_inside_radius() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let mut field = snowy_field_at(TEST_SURFACE_Y);

    // チャンク中央（原点は [0,0,0]）に半径 2m のブラシを当てる。
    let center = [extent * 0.5, TEST_SURFACE_Y, extent * 0.5];
    let spec = erase_brush(center, 2.0, 1.0);
    let changed = brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &spec);
    assert!(changed, "満量の雪へ消しゴムを当てたら変化すること");

    // 中央のテクセルは減っている。
    let mid = COVER_FIELD_RESOLUTION / 2;
    assert!(field.amount_at(mid, mid) < 1.0, "ブラシ中心は削れること");
    // 角（ブラシから 10m 以上離れている）は無傷。
    assert_eq!(field.amount_at(0, 0), 1.0, "ブラシ範囲外は 1 ビットも変えないこと");
    assert_eq!(
        field.material_at(0, 0), MAT_SNOW,
        "ブラシ範囲外の素材も変えないこと"
    );
}

/// 消し切ったテクセルは量 0・轍 0 になり、場全体が空になれば `is_empty` が真になること。
///
/// 「量 0 のチャンクは .tcover を削除する」保存規約（§3.4）が成立する前提を固定する。
///
/// 【素材添字は消えないことも同時に固定する】
///   素材添字は頂点属性（uv0.y）として線形補間されるため、消した所だけ添字が
///   飛ぶと、消した領域の縁で「塗っていない別素材」が解決されて黒い縁が出る
///   （カバー消去で地面が黒くなるバグの一因）。量 0 のテクセルは読み取り側が
///   必ず「量 0」として扱うので、添字を残しても描画には一切寄与しない。
#[test]
fn cover_brush_erase_empties_field_but_keeps_material_index() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let mut field = snowy_field_at(TEST_SURFACE_Y);
    // 轍も付けておく（量だけでなく踏み固めも消えることの確認）。
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            field.stamp_trample(ix, iz, 0.5);
        }
    }

    // チャンク全体を包む大きなブラシで、変化しなくなるまで当て続ける。
    let center = [extent * 0.5, TEST_SURFACE_Y, extent * 0.5];
    let spec = erase_brush(center, extent, 1.0);
    for _ in 0..64 {
        if !brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &spec) {
            break;
        }
    }

    assert!(field.is_empty(), "消しゴムで撫で切ったチャンクは空になること");
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            assert_eq!(field.amount_at(ix, iz), 0.0);
            assert_eq!(field.trample_at(ix, iz), 0.0, "轍も一緒に消えること");
            assert_eq!(
                field.material_at(ix, iz), MAT_SNOW,
                "消え切っても素材添字は保つこと（補間で縁が黒くならないための不変条件）"
            );
        }
    }
}

/// 塗りブラシは目標量へ収束し、それを超えて積み上がらないこと（何往復しても同じ絵）。
#[test]
fn cover_brush_paint_converges_to_target_amount() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let mut field = CoverField::new();

    let center = [extent * 0.5, TEST_SURFACE_Y, extent * 0.5];
    let target = 0.5;
    let spec = paint_brush(center, 4.0, 1.0, MAT_SNOW, target);
    for _ in 0..64 {
        if !brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &spec) {
            break;
        }
    }

    let mid = COVER_FIELD_RESOLUTION / 2;
    // 量子化（1/255）の丸め幅を許容する。
    let quant = 1.0 / 255.0;
    assert!(
        (field.amount_at(mid, mid) - target).abs() <= quant,
        "ブラシ中心は目標量へ収束すること（実測 {}）",
        field.amount_at(mid, mid)
    );
    assert_eq!(field.material_at(mid, mid), MAT_SNOW, "塗った素材になること");

    // ─── 目標量を超えて積み上がらないこと ───
    //   ブラシの縁は falloff がほぼ 0 なので収束が極端に遅い（＝上のループは
    //   打ち切りで抜ける）。「これ以上 1 ビットも変わらない」を場全体へ課すと
    //   縁の 1 テクセルのせいで落ちるため、契約は「上限を超えない」で固定する。
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            assert!(
                field.amount_at(ix, iz) <= target + quant,
                "塗りは目標量を超えないこと ({ix},{iz})"
            );
        }
    }
    // 収束済みの中心テクセルは、さらに当てても動かない。
    let center_before = field.amount_at(mid, mid);
    brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &spec);
    assert_eq!(
        field.amount_at(mid, mid), center_before,
        "収束した中心テクセルはこれ以上変化しないこと"
    );
    // 「量を持つテクセルの基準 Y は必ず有限」の不変条件。
    assert!(field.base_y_at(mid, mid).is_finite(), "塗ったテクセルの基準 Y は有限であること");
}

/// 塗りブラシは目標量より厚い場所を削って目標へ寄せること（両方向へ収束する）。
#[test]
fn cover_brush_paint_reduces_when_above_target() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let mut field = snowy_field_at(TEST_SURFACE_Y); // 満量（1.0）から始める

    let center = [extent * 0.5, TEST_SURFACE_Y, extent * 0.5];
    let target = 0.25;
    let spec = paint_brush(center, 4.0, 1.0, MAT_SNOW, target);
    for _ in 0..64 {
        if !brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &spec) {
            break;
        }
    }

    let mid = COVER_FIELD_RESOLUTION / 2;
    let quant = 1.0 / 255.0;
    assert!(
        (field.amount_at(mid, mid) - target).abs() <= quant,
        "厚すぎる場所は目標量まで削れること（実測 {}）",
        field.amount_at(mid, mid)
    );
}

/// 異素材を塗ると、まず古い素材が削れてから新素材が乗ること（`deposit` と同じ置き換え規則）。
#[test]
fn cover_brush_paint_replaces_other_material_by_eroding_first() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let mut field = snowy_field_at(TEST_SURFACE_Y); // 雪が満量

    let center = [extent * 0.5, TEST_SURFACE_Y, extent * 0.5];
    let spec = paint_brush(center, 4.0, 1.0, MAT_LEAF, 1.0);
    let mid = COVER_FIELD_RESOLUTION / 2;

    // 1 発目: 雪がまだ残っているので素材は雪のまま、量だけ減る。
    brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &spec);
    assert_eq!(field.material_at(mid, mid), MAT_SNOW, "削っている間は素材が変わらないこと");
    assert!(field.amount_at(mid, mid) < 1.0, "古い素材が削れること");

    // 削り切るまで当て続けると落ち葉へ置き換わる。
    for _ in 0..64 {
        if !brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &spec) {
            break;
        }
    }
    assert_eq!(field.material_at(mid, mid), MAT_LEAF, "削り切ったら新素材へ置き換わること");
}

/// 地表をなぞったブラシが、Y 照合により別の段（真下の洞窟の床）へ効かないこと。
#[test]
fn cover_brush_does_not_reach_other_y_level() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    // 面が y=0 にあるカバー場に対し、ブラシは 1 チャンク下（y=-16）を指す。
    let surface = flat_surface(TEST_SURFACE_Y);
    let mut field = snowy_field_at(TEST_SURFACE_Y);
    let before = field.raw_amount().to_vec();

    let center = [extent * 0.5, TEST_SURFACE_Y - extent, extent * 0.5];
    // 半径を大きくしても Y 許容差は半径由来なので、チャンク 1 辺ぶん離れれば届かない。
    let spec = erase_brush(center, 2.0, 1.0);
    let changed = brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &spec);

    assert!(!changed, "別の段の面には効かないこと");
    assert_eq!(field.raw_amount(), before.as_slice(), "量が 1 ビットも変わらないこと");
}

/// 強さ 0・半径 0 のブラシは完全に無作用であること（無駄な dirty 化を作らない）。
#[test]
fn cover_brush_is_noop_for_degenerate_spec() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let mut field = snowy_field_at(TEST_SURFACE_Y);
    let before = field.raw_amount().to_vec();
    let center = [extent * 0.5, TEST_SURFACE_Y, extent * 0.5];

    assert!(!brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &erase_brush(center, 2.0, 0.0)));
    assert!(!brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &erase_brush(center, 0.0, 1.0)));
    assert!(!brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &erase_brush(center, f32::NAN, 1.0)));
    assert_eq!(field.raw_amount(), before.as_slice(), "無作用のブラシは場を変えないこと");
}

// ============================================================
//  8. 黒落ち回帰（カバー消去で地面が真っ黒になったバグ）
// ============================================================

/// 消しゴムでできた「量が 0 へ落ちる帯」の全域で、素材添字がぐらつかないこと。
///
/// 【何を守っているか（バグの再現条件）】
///   素材添字は頂点属性（uv0.y）としてラスタライザが**線形補間**する。
///   消した領域だけ添字が別の値（旧実装では「素材なし」＝添字 0）へ飛ぶと、
///   帯の途中で「塗っていない素材」が最近傍として解決される。
///   たとえば添字 3（泥）を塗った所を消すと、帯の中で添字 2（濡れ・ほぼ黒）が
///   選ばれ、その色が量に比例して地表へ混ざって黒い縁になる。
///
///   ここでは「量 > 0 の点で読める素材添字は、塗った素材ただ 1 つ」を固定する。
///   これが成り立てば、どの 2 点を補間しても添字は動かない。
#[test]
fn cover_erase_keeps_single_material_index_across_falloff() {
    // 添字 3 まで使う素材セット（実アセットと同じ 4 素材構成を模す）。
    const MAT_MUD: u8 = 3;

    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let mut field = CoverField::new();

    // ① チャンク全体へ泥（添字 3）を敷く。
    let center = [extent * 0.5, TEST_SURFACE_Y, extent * 0.5];
    let paint = paint_brush(center, extent * 2.0, 1.0, MAT_MUD, 1.0);
    for _ in 0..32 {
        if !brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &paint) {
            break;
        }
    }
    // ② 中央を消しゴムで抜く（縁に「量が 0 へ落ちる帯」ができる）。
    let erase = erase_brush(center, extent * 0.25, 1.0);
    for _ in 0..32 {
        if !brush_cover_chunk(&mut field, &surface, [0.0, 0.0, 0.0], extent, &erase) {
            break;
        }
    }

    // ③ 帯を含むチャンク全域を細かく読み、素材添字を検査する。
    let view = CoverNeighborhood::isolated(&field);
    let steps = COVER_FIELD_RESOLUTION * 4;
    let mut saw_partial = false;
    for iz in 0..=steps {
        for ix in 0..=steps {
            let u = ix as f32 / steps as f32;
            let v = iz as f32 / steps as f32;
            let s = view.sample(u, v, TEST_SURFACE_Y);
            if s.amount > 0.0 {
                assert_eq!(
                    s.material, MAT_MUD,
                    "量のある点で塗っていない素材が選ばれた (u={u}, v={v})"
                );
                if s.amount < 1.0 {
                    saw_partial = true;
                }
            }
            // 頂点へ焼く添字は、量が 0 の点でも近傍と同じ値でなければならない
            // （ここがぐらつくと補間で黒縁になる）。チャンク内はすべて泥である。
            assert_eq!(
                s.blend_material, MAT_MUD,
                "補間用の素材添字が近傍と食い違った (u={u}, v={v})"
            );
        }
    }
    assert!(saw_partial, "消しゴムの縁に『量が 0 へ落ちる帯』が実在すること");
}

/// 「素材なし」は素材セットから決して引けないこと（＝寄与ゼロが構造的に保証されること）。
///
/// 旧実装では `COVER_MATERIAL_NONE == 0` が 1 番目の実素材と衝突しており、
/// 素材なしのはずのテクセルが 1 番目の素材の踏み固め係数
/// （`trample_darkening` / `trample_cavity`）を引いて地表を暗くしていた。
#[test]
fn cover_material_none_never_resolves_to_a_real_material() {
    let set = test_materials();
    assert!(
        set.get(COVER_MATERIAL_NONE as usize).is_none(),
        "素材なしの添字が実在の素材として解決されてはならない"
    );
    // 上限いっぱいに定義しても衝突しないこと（添字の定義域と番兵の分離）。
    let full = CoverMaterialSet {
        materials: (0..crate::engine::terrain::cover::TERRAIN_MAX_COVER_MATERIALS)
            .map(|i| CoverMaterial { id: format!("m{i}"), ..CoverMaterial::default() })
            .collect(),
        ..CoverMaterialSet::default()
    };
    assert!(full.get(COVER_MATERIAL_NONE as usize).is_none());
}

// ============================================================
//  10. カバーブラシの形状マスク（brush_mask.rs との統合）
//
//  本節が固定する契約:
//    ・マスク未指定は従来どおり（`brush_chunk` と `brush_chunk_with_mask(None)` が同一結果）
//    ・全黒マスクはカバー場を 1 ビットも変えない
//    ・全白マスクは正方形の内側（球の四隅を含む）を一様に塗る
//    ・読み込み失敗マスクは「効かない」ではなく円形フォールオフへ縮退する
// ============================================================

/// 単色マスクを作る（4×4 の一様グレースケール）。
fn uniform_brush_mask(value: u8) -> CoverMask {
    const SIZE: usize = 4;
    CoverMask { width: SIZE, height: SIZE, pixels: vec![value; SIZE * SIZE] }
}

/// マスク未指定（`None`）が、マスク導入前の `brush_chunk` とビット単位で同じ結果になること。
///
/// 既にカバーブラシを使って作ったシーンの編集結果を、この機能追加で変えないための固定。
#[test]
fn cover_brush_without_mask_matches_legacy_bit_exact() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let center = [extent * 0.5, TEST_SURFACE_Y, extent * 0.5];
    let spec = erase_brush(center, 3.0, 0.7);

    let mut legacy = snowy_field_at(TEST_SURFACE_Y);
    let mut masked = snowy_field_at(TEST_SURFACE_Y);
    let a = brush_cover_chunk(&mut legacy, &surface, [0.0, 0.0, 0.0], extent, &spec);
    let b = brush_chunk_with_mask(&mut masked, &surface, [0.0, 0.0, 0.0], extent, &spec, None);

    assert_eq!(a, b, "変化フラグが一致すること");
    assert!(legacy == masked, "マスク未指定はマスク導入前と 1 ビットも変わらないこと");
}

/// 全黒マスクではカバーブラシが何も変えないこと（変化フラグも false）。
#[test]
fn cover_brush_black_mask_changes_nothing() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let center = [extent * 0.5, TEST_SURFACE_Y, extent * 0.5];
    let spec = erase_brush(center, 3.0, 1.0);
    let mask = uniform_brush_mask(0);

    let mut field = snowy_field_at(TEST_SURFACE_Y);
    let before = field.clone();
    let changed =
        brush_chunk_with_mask(&mut field, &surface, [0.0, 0.0, 0.0], extent, &spec, Some(&mask));

    assert!(!changed, "全黒マスクでは変化しないこと");
    assert!(field == before, "全黒マスクではカバー場が 1 ビットも変わらないこと");
}

/// 全白マスクは正方形の内側を一様に消し、正方形の外は 1 ビットも変えないこと。
///
/// 円形フォールオフでは縁が滑らかに減衰するのに対し、全白マスクでは
/// 正方形の内側すべてが**同じ量**だけ効く（＝白 = フル強度）ことを固定する。
#[test]
fn cover_brush_white_mask_erases_square_uniformly() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    // 正方形がチャンク中央の一部だけを覆うよう、半径はチャンクの 1/4 に取る。
    let radius = extent * 0.25;
    let center = [extent * 0.5, TEST_SURFACE_Y, extent * 0.5];
    let spec = erase_brush(center, radius, 1.0);
    let mask = uniform_brush_mask(255);

    let mut field = snowy_field_at(TEST_SURFACE_Y);
    assert!(brush_chunk_with_mask(
        &mut field, &surface, [0.0, 0.0, 0.0], extent, &spec, Some(&mask)
    ));

    // テクセル中心のワールド XZ を求めながら、正方形の内外で期待値を変えて検証する。
    let mut inside_amounts: Vec<f32> = Vec::new();
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            let (u, v) = crate::engine::terrain::cover::texel_center_uv(ix, iz);
            let wx = u * extent;
            let wz = v * extent;
            let inside = (wx - center[0]).abs() <= radius && (wz - center[2]).abs() <= radius;
            let amount = field.amount_at(ix, iz);
            if inside {
                inside_amounts.push(amount);
            } else {
                assert_eq!(amount, 1.0, "正方形の外は 1 ビットも変えないこと (ix={ix} iz={iz})");
            }
        }
    }
    assert!(!inside_amounts.is_empty(), "正方形の内側にテクセルが 1 つも無い");
    // 内側はすべて同じ量（一様）であること。
    let first = inside_amounts[0];
    assert!(first < 1.0, "正方形の内側は削れていること");
    for a in &inside_amounts {
        assert_eq!(*a, first, "全白マスクの内側は一様であること");
    }
}

/// 読み込み失敗マスク（`CoverMask::empty()`）は「効かない」ではなく円形へ縮退すること。
#[test]
fn cover_brush_invalid_mask_degrades_to_circular() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let center = [extent * 0.5, TEST_SURFACE_Y, extent * 0.5];
    let spec = erase_brush(center, 3.0, 0.7);
    let broken = CoverMask::empty();

    let mut circular = snowy_field_at(TEST_SURFACE_Y);
    let mut degraded = snowy_field_at(TEST_SURFACE_Y);
    brush_chunk_with_mask(&mut circular, &surface, [0.0, 0.0, 0.0], extent, &spec, None);
    let changed = brush_chunk_with_mask(
        &mut degraded, &surface, [0.0, 0.0, 0.0], extent, &spec, Some(&broken),
    );

    assert!(changed, "読み込み失敗マスクでもブラシは効き続けること（無反応にしない）");
    assert!(degraded == circular, "縮退結果はマスク未指定と 1 ビットも変わらないこと");
}

// ============================================================
//  8. 積算の間引き（性能）— 変化なしスキップと積算ティック
//
//  ここが固定する契約:
//    ・積もり切った（飽和した）チャンクは「変化なし」を返す
//      → 呼び出し側は焼き直しにもダーティにも載せない
//    ・量子化（1/255）の粒より小さい積み増しでは「変化なし」のまま
//      → 浮動小数の微小変化が永久に「変化あり」を出し続けない
//    ・チャンクの事前判定は積算の早期棄却と同じ条件で一致する
//    ・ティックは間隔で発火し、返す秒数の総和は投入した dt の総和と一致する
// ============================================================

use super::accumulate::{advance_accumulate_tick, chunk_has_active_emitter};
use super::material::DEFAULT_ACCUMULATE_INTERVAL_SEC;

/// 飽和したチャンクは「変化なし」を返し、場も 1 ビットも変わらないこと。
///
/// これが「積もりきった雪のチャンクを毎フレーム焼き直さない」ことの根拠。
#[test]
fn saturated_chunk_reports_no_change() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let emitters = global_emitter(MAT_SNOW, 1.0);
    let materials = test_materials();

    // 十分な時間を積んで満量（量 1.0）まで飽和させる。
    let mut field = CoverField::new();
    assert!(accumulate_chunk(
        &mut field, &surface, [0.0; 3], extent, &emitters, &materials, 10.0,
    ));
    let saturated = field.clone();

    // 飽和後はいくら積んでも変化しない（＝焼き直しキューへ載らない）。
    for _ in 0..10 {
        let changed = accumulate_chunk(
            &mut field, &surface, [0.0; 3], extent, &emitters, &materials, 1.0 / 60.0,
        );
        assert!(!changed, "飽和したチャンクは変化フラグを立てないこと");
    }
    assert_eq!(field, saturated, "飽和後は場が 1 ビットも変わらないこと");
}

/// 量子化の粒（1/255）に届かない積み増しでは「変化なし」のままであること。
///
/// 変化判定は **量子化済みの格納値**で行っているという契約。生の f32 を比べていると、
/// どれだけ小さな dt でも毎回「変化あり」になり、スキップが永久に効かなくなる。
#[test]
fn sub_quantum_accumulation_reports_no_change() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let materials = test_materials();
    // 1 回あたりの積み増し = 0.2 × (1/600) ≒ 0.00033 → 量子化すると 0.085 段 → 四捨五入で 0 段。
    let emitters = global_emitter(MAT_SNOW, 0.2);

    let mut field = CoverField::new();
    let before = field.clone();
    let changed = accumulate_chunk(
        &mut field, &surface, [0.0; 3], extent, &emitters, &materials, 1.0 / 600.0,
    );
    assert!(!changed, "量子化の粒に届かない積み増しは変化として扱わないこと");
    assert_eq!(field, before, "場も 1 ビットも変わらないこと");
}

/// チャンクの事前判定が、積算の早期棄却とまったく同じ条件で一致すること。
///
/// 片方だけ緩いと「場を確保したのに何も積もらない」「積もるはずが場が無い」が起きる。
#[test]
fn chunk_pre_filter_matches_accumulate_early_reject() {
    let settings = TerrainSettings::default();
    let extent = settings.chunk_extent();
    let surface = flat_surface(TEST_SURFACE_Y);
    let materials = test_materials();
    let origin = [0.0f32; 3];

    // 全域エミッタ: 必ず触る。
    let global = global_emitter(MAT_SNOW, 1.0);
    assert!(chunk_has_active_emitter(origin, extent, &global));

    // 強度 0: 触らない。
    let zero = global_emitter(MAT_SNOW, 0.0);
    assert!(!chunk_has_active_emitter(origin, extent, &zero));

    // エミッタ無し: 触らない。
    assert!(!chunk_has_active_emitter(origin, extent, &[]));

    // 遠くの Region: 触らない。判定と実処理の結果が一致することも確かめる。
    let far = vec![CoverEmitSpec {
        range: CoverEmitRange::Region {
            center: [1000.0, 0.0, 1000.0],
            half_extents: [1.0, 1.0, 1.0],
            fade: 0.0,
        },
        material_index: MAT_SNOW,
        rate: 1.0,
    }];
    assert!(!chunk_has_active_emitter(origin, extent, &far));
    let mut field = CoverField::new();
    assert!(
        !accumulate_chunk(&mut field, &surface, origin, extent, &far, &materials, 1.0),
        "事前判定が false のとき、実処理も必ず変化なしであること"
    );
}

/// ティックは間隔に達するまで発火せず、達した瞬間に「貯めた全量」を返すこと。
#[test]
fn accumulate_tick_fires_at_interval_boundary() {
    const INTERVAL: f32 = 0.25;
    const FRAME: f32 = 0.1;
    let mut timer = 0.0f32;

    // 0.1 → 0.2 は未達。
    assert_eq!(advance_accumulate_tick(&mut timer, FRAME, INTERVAL), None);
    assert_eq!(advance_accumulate_tick(&mut timer, FRAME, INTERVAL), None);
    // 0.3 で到達 → 貯めた 0.3 秒ぶんをまとめて返す（0.25 ではない）。
    let fired = advance_accumulate_tick(&mut timer, FRAME, INTERVAL)
        .expect("間隔に達したら発火すること");
    assert!((fired - 0.3).abs() < 1.0e-5, "返るのは貯めた全量であること: {fired}");
    assert_eq!(timer, 0.0, "発火後はタイマが 0 へ戻ること");
}

/// ティックが返した秒数の総和が、投入した dt の総和と一致すること（積算総量の保存）。
///
/// これが「ティック化しても毎フレーム積算と同じ量が積もる」ことの根拠。
#[test]
fn accumulate_tick_conserves_total_time() {
    const INTERVAL: f32 = DEFAULT_ACCUMULATE_INTERVAL_SEC;
    const FRAME: f32 = 1.0 / 60.0;
    const FRAMES: usize = 600; // 10 秒ぶん

    let mut timer = 0.0f32;
    let mut fired_total = 0.0f32;
    for _ in 0..FRAMES {
        if let Some(dt) = advance_accumulate_tick(&mut timer, FRAME, INTERVAL) {
            fired_total += dt;
        }
    }
    // 発火した総量 ＋ 未消化の端数 ＝ 投入した総量。
    let injected = FRAME * FRAMES as f32;
    assert!(
        (fired_total + timer - injected).abs() < 1.0e-2,
        "発火総量 {fired_total} ＋ 端数 {timer} が投入総量 {injected} と一致すること"
    );
    // 端数は必ず 1 ティック未満（＝取りこぼしは最大 1 ティックぶん）。
    assert!(timer < INTERVAL, "未消化の端数は 1 ティック未満であること");
}

/// 異常な入力（負の dt・非有限のタイマ／間隔）でティックが壊れないこと。
#[test]
fn accumulate_tick_survives_bad_input() {
    // 負の dt は 0 として扱う（時間が巻き戻らない）。
    let mut timer = 0.1f32;
    assert_eq!(advance_accumulate_tick(&mut timer, -1.0, 0.25), None);
    assert_eq!(timer, 0.1);

    // 壊れたタイマは 0 から復帰する。
    let mut timer = f32::NAN;
    assert_eq!(advance_accumulate_tick(&mut timer, 0.1, 0.25), None);
    assert_eq!(timer, 0.1);

    // 間隔が不正なら毎フレーム発火へ倒す（永久に発火しないより安全）。
    let mut timer = 0.0f32;
    assert!(advance_accumulate_tick(&mut timer, 0.1, f32::NAN).is_some());
}

/// 素材セットの積算間隔が、異常値でも安全な範囲へ丸められること。
#[test]
fn accumulate_interval_is_sanitized() {
    let mut set = CoverMaterialSet::default();
    assert_eq!(set.accumulate_interval_sec(), DEFAULT_ACCUMULATE_INTERVAL_SEC);

    // 0・負値は下限（＝毎フレーム相当）へ。
    set.accumulate_interval_sec = 0.0;
    assert!(set.accumulate_interval_sec() > 0.0);
    set.accumulate_interval_sec = -5.0;
    assert!(set.accumulate_interval_sec() > 0.0);

    // 非有限は既定値へ。
    set.accumulate_interval_sec = f32::NAN;
    assert_eq!(set.accumulate_interval_sec(), DEFAULT_ACCUMULATE_INTERVAL_SEC);

    // 極端に大きい値は上限で頭打ち。
    set.accumulate_interval_sec = 1.0e6;
    assert!(set.accumulate_interval_sec() < 1.0e6);
}

/// 積算間隔フィールドが無い旧 cover_materials.json でも既定値で読めること（serde default）。
#[test]
fn accumulate_interval_defaults_for_old_asset() {
    let set = CoverMaterialSet::from_json_str(r#"{"materials":[{"id":"snow"}]}"#)
        .expect("読めること");
    assert_eq!(set.accumulate_interval_sec(), DEFAULT_ACCUMULATE_INTERVAL_SEC);
}

// ─── 焼き直しの波（bake_wave）— フレームをまたいだ境界整合 ───────────────────

/// **隣接チャンクを別フレームに焼いても境界がビット一致すること**（本改修の核心）。
///
/// 【何を守っているか】
///   旧実装は「26 近傍で連結した待ちチャンクの塊を 1 フレームで焼く」ことで境界整合を
///   守っていた。そのため全域降雪では全チャンクが 1 成分になり、フレーム予算が
///   まったく効かなかった（積算ティックのたびに全チャンクを 1 フレームで焼く）。
///
///   現在は焼き直し開始時にカバー場を凍結（`CoverBakeWave`）し、波に属するチャンクは
///   何フレームに分かれて焼かれても凍結データだけを読む。本テストは
///   「A を焼く → 実データだけ積算が進む → 次フレームに B を焼く」を模し、
///   その間に実データが変わっても A と B の境界がビット一致することを固定する。
#[test]
fn frozen_wave_keeps_boundary_identical_across_frames() {
    use super::bake_wave::CoverBakeWave;
    use std::collections::{HashMap, HashSet};

    let coord_a = ChunkCoord::new(0, 0, 0);
    let coord_b = ChunkCoord::new(1, 0, 0);

    // 実データ（積算がこの後も書き換え続ける器）。
    let mut live: HashMap<ChunkCoord, CoverField> = HashMap::new();
    live.insert(coord_a, patterned_field(7));
    live.insert(coord_b, patterned_field(31));

    // ── 波を張る（対象 ∪ 26 近傍を凍結）──
    let mut wave = CoverBakeWave::default();
    let targets: HashSet<ChunkCoord> = [coord_a, coord_b].into_iter().collect();
    let snapshot: HashMap<ChunkCoord, CoverField> =
        live.iter().map(|(k, v)| (*k, v.clone())).collect();
    wave.start(targets, HashSet::new(), snapshot);

    /// 境界線上の複数点を「そのチャンクのメッシュから読んだ値」として拾う。
    fn sample_boundary(wave: &CoverBakeWave, center: ChunkCoord, u: f32) -> Vec<(u32, u8)> {
        let view = CoverNeighborhood::from_lookup(cover_y_match_tolerance(16.0), |dx, dy, dz| {
            wave.field(ChunkCoord::new(center.x + dx, center.y + dy, center.z + dz))
        });
        (0..=16)
            .map(|i| {
                let s = view.sample(u, i as f32 / 16.0, TEST_SURFACE_Y);
                (s.amount.to_bits(), s.material)
            })
            .collect()
    }

    // ── フレーム 1: A だけを焼く（凍結データから読む）──
    let baked_a = sample_boundary(&wave, coord_a, 1.0);
    wave.mark_baked(coord_a);
    assert!(wave.is_active(), "B がまだ残っているので波は続く");

    // ── フレーム間: 実データだけが積算で進む（次の波が拾うぶん）──
    for f in live.values_mut() {
        for iz in 0..COVER_FIELD_RESOLUTION {
            for ix in 0..COVER_FIELD_RESOLUTION {
                f.deposit(ix, iz, 0, 0.25);
            }
        }
    }

    // ── フレーム 2: B を焼く。凍結データを読むので A と食い違わない ──
    let baked_b = sample_boundary(&wave, coord_b, 0.0);
    wave.mark_baked(coord_b);
    assert!(!wave.is_active(), "全件焼き終わって波が閉じる");

    assert_eq!(
        baked_a, baked_b,
        "別フレームに焼いても境界の共有点はビット単位で一致すること",
    );

    // 念のため、凍結していなければ食い違っていたことも確かめる（テストの有効性の担保）。
    let live_view = CoverNeighborhood::from_lookup(cover_y_match_tolerance(16.0), |dx, dy, dz| {
        live.get(&ChunkCoord::new(coord_b.x + dx, coord_b.y + dy, coord_b.z + dz))
    });
    let live_b: Vec<(u32, u8)> = (0..=16)
        .map(|i| {
            let s = live_view.sample(0.0, i as f32 / 16.0, TEST_SURFACE_Y);
            (s.amount.to_bits(), s.material)
        })
        .collect();
    assert_ne!(
        baked_a, live_b,
        "実データを読んでいたら段差になっていたはず（テストが実際に差を検出できている）",
    );
}
