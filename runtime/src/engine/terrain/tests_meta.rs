// ============================================================
//  terrain/tests_meta.rs — 地形メタデータ（meta.rs）専用のユニットテスト
//
//  守る不変条件:
//    1. チャンク単位の当たり判定 ON/OFF が往復（書き→読み）で完全に復元される。
//    2. 出力が決定的（同じ状態からは常に同じ JSON。差分が湧かない）。
//    3. **旧ファイル・ファイル欠落は「すべて有効・デシメート無し」で読める**（後方互換）。
// ============================================================

use std::collections::HashSet;

use super::chunk_coord::ChunkCoord;
use super::meta::{TerrainMeta, clamp_strength, read_meta, write_meta};

/// 当たり判定を無効にしたチャンク集合が、書き→読みで完全に復元されること。
#[test]
fn collision_flags_round_trip() {
    let mut disabled: HashSet<ChunkCoord> = HashSet::new();
    disabled.insert(ChunkCoord::new(0, 0, 0));
    disabled.insert(ChunkCoord::new(-2, 1, 3));
    disabled.insert(ChunkCoord::new(5, 0, -7));

    let meta = TerrainMeta::from_state(&disabled, 0.35);
    let text = write_meta(&meta);
    let (back, ok) = read_meta(&text);
    assert!(ok, "自分で書いた JSON が読めない");
    assert_eq!(back.collision_disabled_set(), disabled, "無効チャンク集合が往復しない");
    assert!(
        (back.clamped_decimate_strength() - 0.35).abs() < 1.0e-6,
        "デシメート強度が往復しない: {}",
        back.clamped_decimate_strength()
    );
}

/// 同じ状態からは常に同じ JSON が出る（HashSet の走査順に依存しない）。
#[test]
fn output_is_deterministic() {
    let coords = [
        ChunkCoord::new(3, 0, 1),
        ChunkCoord::new(-1, 2, 0),
        ChunkCoord::new(0, 0, 0),
        ChunkCoord::new(3, 0, 0),
    ];
    // 挿入順を変えた 2 つの集合から同じ文字列が出ること。
    let a: HashSet<ChunkCoord> = coords.iter().copied().collect();
    let b: HashSet<ChunkCoord> = coords.iter().rev().copied().collect();
    let ta = write_meta(&TerrainMeta::from_state(&a, 0.5));
    let tb = write_meta(&TerrainMeta::from_state(&b, 0.5));
    assert_eq!(ta, tb, "同じ状態から違う JSON が出ている（差分が湧く）");
}

/// 空の状態は「無効チャンク無し・強度 0」として往復する。
#[test]
fn empty_state_round_trips() {
    let empty: HashSet<ChunkCoord> = HashSet::new();
    let text = write_meta(&TerrainMeta::from_state(&empty, 0.0));
    let (back, ok) = read_meta(&text);
    assert!(ok);
    assert!(back.collision_disabled_set().is_empty());
    assert_eq!(back.clamped_decimate_strength(), 0.0);
}

/// **後方互換**: メタファイルが無い（＝既定値）と、項目が欠けた古い JSON の両方で、
/// 「全チャンク当たり判定あり・デシメート無し」として読めること。
#[test]
fn missing_or_partial_file_defaults_to_all_enabled() {
    // ① ファイルが無い場合に呼び出し側が使う既定値。
    let default = TerrainMeta::default();
    assert!(
        default.collision_disabled_set().is_empty(),
        "既定は全チャンク当たり判定あり"
    );
    assert_eq!(default.clamped_decimate_strength(), 0.0);

    // ② version だけの古い JSON（今後項目が増えても読めることの担保）。
    let (partial, ok) = read_meta("{\"version\":1}");
    assert!(ok, "項目が欠けた JSON が読めない");
    assert!(partial.collision_disabled_set().is_empty());
    assert_eq!(partial.clamped_decimate_strength(), 0.0);

    // ③ collision_disabled だけを持つ JSON（デシメート導入前のファイル）。
    let (only_collision, ok) =
        read_meta("{\"version\":1,\"collision_disabled\":[[1,0,2]]}");
    assert!(ok);
    assert_eq!(
        only_collision.collision_disabled_set(),
        HashSet::from([ChunkCoord::new(1, 0, 2)])
    );
    assert_eq!(only_collision.clamped_decimate_strength(), 0.0);
}

/// 壊れた JSON はエラーにせず既定値へ倒す（地形そのものは開けること）。
#[test]
fn broken_json_falls_back_to_defaults() {
    let (meta, ok) = read_meta("{ this is not json ");
    assert!(!ok, "壊れた JSON が成功として報告された");
    assert!(meta.collision_disabled_set().is_empty());
    assert_eq!(meta.clamped_decimate_strength(), 0.0);
}

/// 手書き・破損した強度値は値域へ丸められる。
#[test]
fn strength_is_clamped() {
    assert_eq!(clamp_strength(-3.0), 0.0);
    assert_eq!(clamp_strength(7.5), 1.0);
    assert_eq!(clamp_strength(f32::NAN), 0.0);
    assert_eq!(clamp_strength(0.25), 0.25);

    let (meta, _) = read_meta("{\"version\":1,\"decimate_strength\":9.0}");
    assert_eq!(meta.clamped_decimate_strength(), 1.0);
}
