// ============================================================
//  terrain/tests_dir_ref.rs — 「地形フォルダ」保存先任意化の往復テスト
//
//  【何を守るテストか】
//    地形一式（密度 .tvox / 散布 .tscatter / カバー .tcover）は 1 つのフォルダに
//    まとまって置かれる。保存先を任意化したことで、この「フォルダ単位でひと揃い」
//    という不変条件が壊れやすくなった（別名保存で片方だけ移る、旧フォルダを
//    読み続ける、など）。ここでは実ファイルを一時ディレクトリへ書いて
//
//      保存（フォルダ A）→ 別名保存（フォルダ B）→ 読込（B から）
//
//    の往復で 3 種すべてが揃い、内容が保たれることを確かめる。
//
//  【App を通さない理由】
//    実際の保存経路（`App::handle_terrain_save` / `handle_terrain_save_as`）は
//    GPU デバイスを持つ `App` に生えており、CI・テストでは構築できない。
//    そこで「どのフォルダのどのファイル名へ書くか」を決める層（`dir_ref`）と
//    シリアライザ（tvox / tscatter / tcover）を直接組み合わせ、App が行うのと
//    同じ手順を再現する。パス規則の取り違えはここで必ず落ちる。
// ============================================================

use super::chunk_coord::ChunkCoord;
use super::chunk_data::TerrainChunkData;
use super::cover::{COVER_FIELD_RESOLUTION, CoverField, tcover};
use super::dir_ref;
use super::scatter::{ScatterInstance, tscatter};
use super::settings::TerrainSettings;
use super::tvox;

// ─── テスト用定数（マジックナンバー禁止） ───────────────────────────────

/// 往復テストで使うチャンク分割数（小さくして I/O を軽くする）。
const TEST_CHUNK_CELLS: u32 = 4;
/// 往復テストで使うボクセルサイズ（m）。
const TEST_VOXEL_SIZE: f32 = 0.5;
/// テスト地形の初期密度（負＝SOLID。値そのものに意味は無いが 0 以外にして往復差分を見る）。
const TEST_FILL_DENSITY: f32 = -1.5;
/// 往復に使うチャンク座標（負の成分を含めてファイル名の符号漏れも同時に見る）。
const TEST_CHUNK: (i32, i32, i32) = (-2, 1, 3);
/// カバー場に書き込むテスト用の量（0〜1）。
const TEST_COVER_AMOUNT: f32 = 0.75;
/// カバー場に書き込むテスト用の素材 ID。
const TEST_COVER_MATERIAL: u8 = 1;
/// 別名保存先として使うフォルダ参照（アセットルート相対・多階層）。
const TEST_SAVE_AS_DIR: &str = "levels/forest/ground";

// ─── ヘルパー ───────────────────────────────────────────────────────────

/// テスト専用の一時ディレクトリを作って返す（プロセス・スレッドごとに一意）。
///
/// ユーザーの assets を一切汚さないために `std::env::temp_dir()` の下に作る
/// （`tscatter_round_trips_through_file_path` と同じ流儀）。
fn make_temp_root(tag: &str) -> std::path::PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "seed_terrain_dir_ref_{tag}_{}_{:?}",
        std::process::id(),
        std::thread::current().id()
    ));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).expect("一時ディレクトリを作れない");
    dir
}

/// 地形フォルダ参照（アセットルート相対）を、テスト用ルート下の実パスへ落とす。
///
/// 本番の `terrain_ops::terrain_dir_abs` と同じ変換（`/` を OS 区切りへ）。
fn dir_abs(root: &std::path::Path, dir_rel: &str) -> std::path::PathBuf {
    root.join(dir_rel.replace('/', std::path::MAIN_SEPARATOR_STR))
}

/// テスト用の地形一式（密度・散布・カバー）を作る。
fn make_terrain_fixture() -> (TerrainSettings, TerrainChunkData, Vec<ScatterInstance>, CoverField) {
    let mut settings = TerrainSettings::default();
    settings.apply_chunk_config(
        settings.ground_chunks_x,
        settings.ground_chunks_z,
        TEST_CHUNK_CELLS,
        TEST_VOXEL_SIZE,
    );

    let chunk = TerrainChunkData::new_filled(&settings, TEST_FILL_DENSITY);

    let instances = vec![ScatterInstance {
        pos: [1.0, 2.0, 3.0],
        normal: [0.0, 1.0, 0.0],
        yaw: 0.25,
        scale: 1.5,
        prop_id: 0,
        seed: 1234,
    }];

    // カバー場は全テクセルへ同じ量を敷く（往復で量・素材が保たれることを見る）。
    // 内部配列は非公開なので、通常の堆積 API（deposit）で積む。
    let mut cover = CoverField::new();
    for iz in 0..COVER_FIELD_RESOLUTION {
        for ix in 0..COVER_FIELD_RESOLUTION {
            cover.deposit(ix, iz, TEST_COVER_MATERIAL, TEST_COVER_AMOUNT);
        }
    }

    (settings, chunk, instances, cover)
}

/// 地形一式を 1 つのフォルダへ書き出す（App の保存経路が行うのと同じ 3 種セット）。
fn save_all(
    root: &std::path::Path,
    dir_rel: &str,
    coord: ChunkCoord,
    settings: &TerrainSettings,
    chunk: &TerrainChunkData,
    instances: &[ScatterInstance],
    cover: &CoverField,
) {
    let dir = dir_abs(root, dir_rel);
    std::fs::create_dir_all(&dir).expect("保存先フォルダを作れない");
    let stem = dir_ref::chunk_stem(coord);
    std::fs::write(
        dir.join(format!("{stem}{}", dir_ref::TVOX_EXT)),
        tvox::write_chunk(chunk, coord, settings),
    )
    .expect(".tvox 書き出し失敗");
    std::fs::write(
        dir.join(format!("{stem}{}", dir_ref::TSCATTER_EXT)),
        tscatter::write_chunk(instances, coord),
    )
    .expect(".tscatter 書き出し失敗");
    std::fs::write(
        dir.join(format!("{stem}{}", dir_ref::TCOVER_EXT)),
        tcover::write_chunk(cover, coord),
    )
    .expect(".tcover 書き出し失敗");
}

/// 仮想パス（`assets://...`）をテスト用ルート基準の実パスへ解決する。
///
/// `asset_fs::resolve` はプロセス全体で 1 度しか初期化できない `OnceLock` を見るため、
/// テストからは使えない（他テストと衝突する）。ここでは同じ規則を局所的に再現する。
fn resolve_virtual(root: &std::path::Path, virtual_path: &str) -> std::path::PathBuf {
    let rel = virtual_path
        .strip_prefix(crate::engine::asset_fs::ASSETS_SCHEME)
        .expect("仮想パスであること");
    root.join(rel.replace('/', std::path::MAIN_SEPARATOR_STR))
}

// ─── テスト本体 ─────────────────────────────────────────────────────────

/// 保存 → 別名保存 → 読込 の往復で、地形一式 3 種がすべて新フォルダに揃うこと。
///
/// あわせて、読込側が手掛かりにする `.tvox` 仮想パスから
/// 「フォルダ参照が逆算できる」ことも見る（旧シーン互換の要）。
#[test]
fn save_then_save_as_then_load_keeps_all_files() {
    let root = make_temp_root("roundtrip");
    let coord = ChunkCoord::new(TEST_CHUNK.0, TEST_CHUNK.1, TEST_CHUNK.2);
    let (settings, chunk, instances, cover) = make_terrain_fixture();

    // ── 1. 「地形を保存」: 参照を持たないシーンの既定フォルダへ書く ──
    let scene_name = "Scene1";
    let default_dir = dir_ref::resolve_or_default(None, scene_name);
    assert_eq!(default_dir, "terrain/Scene1", "既定フォルダが従来の固定パスと違う");
    save_all(&root, &default_dir, coord, &settings, &chunk, &instances, &cover);

    // ── 2. 「名前を付けて保存」: 別フォルダへ一式を書き直す ──
    let save_as_dir = dir_ref::normalize(TEST_SAVE_AS_DIR).expect("正規化できるはず");
    save_all(&root, &save_as_dir, coord, &settings, &chunk, &instances, &cover);

    // 3 種そろって存在すること（片方だけ移っていたらここで落ちる）。
    for virtual_path in [
        dir_ref::tvox_virtual_path(&save_as_dir, coord),
        dir_ref::sibling_path(
            &dir_ref::tvox_virtual_path(&save_as_dir, coord),
            dir_ref::TSCATTER_EXT,
        ),
        dir_ref::sibling_path(
            &dir_ref::tvox_virtual_path(&save_as_dir, coord),
            dir_ref::TCOVER_EXT,
        ),
    ] {
        let abs = resolve_virtual(&root, &virtual_path);
        assert!(abs.is_file(), "別名保存先にファイルが無い: {virtual_path}");
    }

    // 元のフォルダは消さない仕様（バックアップとして残す）。
    assert!(
        resolve_virtual(&root, &dir_ref::tvox_virtual_path(&default_dir, coord)).is_file(),
        "別名保存が元フォルダを消してしまっている"
    );

    // ── 3. 「読込」: 新フォルダの .tvox パスだけを手掛かりに一式を読み戻す ──
    //   実行時の読込経路と同じく、.tscatter / .tcover は .tvox パスの
    //   拡張子差し替えで導く（`tscatter_path_from_tvox` / `tcover_path_from_tvox`）。
    let tvox_virtual = dir_ref::tvox_virtual_path(&save_as_dir, coord);
    let tvox_bytes =
        std::fs::read(resolve_virtual(&root, &tvox_virtual)).expect(".tvox を読めない");
    let (loaded_chunk, loaded_coord) = tvox::read_chunk(&tvox_bytes).expect(".tvox デコード失敗");
    assert_eq!(loaded_coord, coord, "チャンク座標が往復で変わった");
    assert_eq!(loaded_chunk.raw_density(), chunk.raw_density(), "密度が往復で変わった");

    let scatter_bytes = std::fs::read(resolve_virtual(
        &root,
        &dir_ref::sibling_path(&tvox_virtual, dir_ref::TSCATTER_EXT),
    ))
    .expect(".tscatter を読めない");
    let (loaded_instances, _) =
        tscatter::read_chunk(&scatter_bytes).expect(".tscatter デコード失敗");
    assert_eq!(loaded_instances, instances, "散布が往復で変わった");

    let cover_bytes = std::fs::read(resolve_virtual(
        &root,
        &dir_ref::sibling_path(&tvox_virtual, dir_ref::TCOVER_EXT),
    ))
    .expect(".tcover を読めない");
    let (loaded_cover, _) = tcover::read_chunk(&cover_bytes).expect(".tcover デコード失敗");
    assert_eq!(loaded_cover.raw_material(), cover.raw_material(), "カバー素材が往復で変わった");
    assert_eq!(loaded_cover.raw_amount(), cover.raw_amount(), "カバー量が往復で変わった");

    // ── 4. .tvox パスからフォルダ参照が逆算できること（旧シーン互換の要）──
    assert_eq!(
        dir_ref::dir_from_tvox_path(&tvox_virtual).as_deref(),
        Some(save_as_dir.as_str()),
        "保存したフォルダを .tvox パスから逆算できない"
    );

    let _ = std::fs::remove_dir_all(&root);
}

/// 参照フィールドを持たない旧シーンが、従来の固定パスで読めること。
///
/// 「保存先の任意化」で最も壊しやすいのがここ。旧 `.scene`（`terrain_dir` キー無し・
/// `tvox_path` は `assets://terrain/<シーン名>/...`）が、これまでと 1 バイトも
/// 違わない場所を指し続けることを確かめる。
#[test]
fn legacy_scene_without_reference_resolves_to_fixed_path() {
    let root = make_temp_root("legacy");
    let coord = ChunkCoord::new(0, 0, 0);
    let (settings, chunk, instances, cover) = make_terrain_fixture();

    // 旧実装が書いていた場所（root/terrain/<シーン名>/）へ直接置く。
    let scene_name = "OldScene";
    let legacy_dir = dir_abs(&root, "terrain").join(scene_name);
    std::fs::create_dir_all(&legacy_dir).expect("旧フォルダを作れない");
    save_all(
        &root,
        &format!("terrain/{scene_name}"),
        coord,
        &settings,
        &chunk,
        &instances,
        &cover,
    );

    // 参照 None（旧シーン）→ 既定パスへ解決され、そこに .tvox が実在する。
    let resolved = dir_ref::resolve_or_default(None, scene_name);
    assert_eq!(resolved, "terrain/OldScene");
    let tvox_virtual = dir_ref::tvox_virtual_path(&resolved, coord);
    assert_eq!(tvox_virtual, "assets://terrain/OldScene/chunk_0_0_0.tvox");
    assert!(
        resolve_virtual(&root, &tvox_virtual).is_file(),
        "旧シーンの既定パスに .tvox が見つからない（後方互換が壊れている）"
    );

    // 旧シーンの `tvox_path` からフォルダを逆算しても同じ場所を指す
    //   （＝読んだ場所へそのまま上書き保存できる）。
    assert_eq!(
        dir_ref::dir_from_tvox_path(&tvox_virtual).as_deref(),
        Some(resolved.as_str())
    );

    let _ = std::fs::remove_dir_all(&root);
}
