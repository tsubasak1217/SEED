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
use super::emit::{CoverEmitRange, CoverEmitSpec, CoverMask};
use super::field::{
    slope_scale, CoverField, CoverSurface, COVER_FIELD_RESOLUTION, COVER_SLOPE_UP_FULL,
    COVER_SLOPE_UP_MIN, COVER_SURFACE_ABSENT,
};
use super::material::CoverMaterialSet;
use super::tcover::{read_chunk, write_chunk, TcoverError, TCOVER_MAGIC};
use crate::engine::terrain::chunk_coord::ChunkCoord;
use crate::engine::terrain::chunk_data::TerrainChunkData;
use crate::engine::terrain::settings::TerrainSettings;

// ─── テスト用ヘルパ ──────────────────────────────────────────────────────────

/// 素材添字（テストで使う値。0 = 雪相当、1 = 落ち葉相当）。
const MAT_SNOW: u8 = 0;
const MAT_LEAF: u8 = 1;

/// 全テクセルが水平面（up = 1.0・高さ y）である地表情報を作る。
fn flat_surface(y: f32) -> CoverSurface {
    // `CoverSurface` のフィールドは private なので、密度チャンクから作るのが正道。
    // 「水平地面のチャンク」を作って from_chunk に通す（実装経路と同じ道を通す）。
    let settings = TerrainSettings::default();
    let chunk = TerrainChunkData::from_ground_plane(&settings, ChunkCoord::new(0, 0, 0));
    CoverSurface::from_chunk(&chunk, &settings, y)
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
    let coord = ChunkCoord::new(0, 0, 0);

    // ─── 平地: density = worldY（既定の地面平面）───
    let flat = TerrainChunkData::from_ground_plane(&settings, coord);
    let flat_surface = CoverSurface::from_chunk(&flat, &settings, 0.0);

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
    accumulate_chunk(&mut flat_field, &flat_surface, [0.0; 3], extent, &emitters, 0.5);
    accumulate_chunk(&mut steep_field, &steep_surface, [0.0; 3], extent, &emitters, 0.5);

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
    assert!(!accumulate_chunk(&mut f, &surface, [0.0; 3], extent, &global_emitter(MAT_SNOW, 0.0), 1.0));
    assert_eq!(f, before);

    // dt 0。
    let mut f = before.clone();
    assert!(!accumulate_chunk(&mut f, &surface, [0.0; 3], extent, &global_emitter(MAT_SNOW, 1.0), 0.0));
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
    assert!(!accumulate_chunk(&mut f, &surface, [0.0; 3], extent, &far, 1.0));
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

/// 一様な場をどこで読んでも同じ値が返ること（バイリニアの重みが総和 1 である保証）。
#[test]
fn uniform_field_samples_uniformly() {
    let mut f = CoverField::new();
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            f.deposit(ix, iz, MAT_SNOW, 0.5);
        }
    }
    for &(u, v) in &[(0.0, 0.0), (0.5, 0.5), (1.0, 1.0), (0.123, 0.987)] {
        let (a, m) = f.sample(u, v);
        assert!((a - 0.5).abs() < 0.01, "一様な場は一様に読めること (u={u},v={v},a={a})");
        assert_eq!(m, MAT_SNOW);
    }
}

/// 範囲外の UV は端のテクセルへクランプされること（NaN も 0 側へ落ちる）。
#[test]
fn sample_clamps_out_of_range_uv() {
    let mut f = CoverField::new();
    f.deposit(0, 0, MAT_LEAF, 1.0);
    let (a, m) = f.sample(-5.0, -5.0);
    assert_eq!(a, 1.0);
    assert_eq!(m, MAT_LEAF);
    let (a_nan, _) = f.sample(f32::NAN, f32::NAN);
    assert_eq!(a_nan, 1.0, "NaN は 0 側の端へクランプされること");
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
    for id in ["snow", "leaf_carpet", "wet"] {
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
    for id in ["snow", "leaf_carpet", "wet"] {
        assert!(set.index_of(id).is_some(), "サンプルアセットに `{id}` があること");
    }
    // 雪は盛り上がり、濡れは盛り上がらない（仕様上の約束）。
    let snow = set.get(set.index_of("snow").unwrap()).unwrap();
    let wet = set.get(set.index_of("wet").unwrap()).unwrap();
    assert!(snow.displacement > 0.0, "雪は変位を持つ");
    assert_eq!(wet.displacement, 0.0, "濡れは変位ゼロ");
    assert!(wet.roughness < snow.roughness, "濡れは粗さが低い（鏡面が立つ）");
}
