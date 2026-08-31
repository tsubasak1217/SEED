// ============================================================
//  terrain_meta_ops.rs — 地形フォルダの付随メタデータ（terrain_meta.json）の入出力
//
//  【責務】
//    チャンク単位の当たり判定 ON/OFF と、その場デシメートの強度を、
//    地形フォルダ直下の 1 ファイル（terrain_meta.json）へ保存／復元する。
//    直列化そのものは `terrain::meta`（純粋層）が担い、ここはファイル IO と
//    `TerrainState` への出し入れだけを行う。
//
//  【なぜ .tvox と別ファイルなのか】
//    .tvox はチャンク 1 枚ぶんの固定長バイナリで、ヘッダに項目を足すと
//    バージョンを上げて全チャンクを書き直すことになる。ここで持つのは
//    「無効チャンクの一覧」と「スライダー 1 個」だけなので、
//    フォルダに小さな JSON を 1 枚置くほうが安く、後方互換も自明である
//    （**ファイルが無い＝全チャンク有効・デシメート無し**）。
//
//  【保存タイミング】
//    .tvox / .tscatter / .tcover と同じ 3 経路すべて:
//      - TERRAIN_SAVE（全保存）
//      - TERRAIN_SAVE_AS（別名保存。移し忘れると当たり判定設定だけ元フォルダに残る）
//      - シーン保存（Ctrl+S）の差分フラッシュ
// ============================================================

use std::collections::HashSet;

use crate::engine::terrain::{
    ChunkCoord, TERRAIN_META_FILE_NAME, TerrainMeta, read_meta, write_meta,
};

use super::App;

impl App {
    /// 地形メタデータ（当たり判定 ON/OFF・デシメート強度）を地形フォルダへ書き出す。
    ///
    /// - `dir`: 書き出し先の地形フォルダ（OS 絶対パス。呼び出し側が作成済み）。
    /// - `only_dirty`: true なら「変更が無ければ 1 バイトも書かない」（シーン保存の差分経路）。
    ///
    /// 戻り値は書き出したファイル数（0 か 1）。失敗はログのみで、保存全体は落とさない
    /// （メタは補助情報であり、これが書けないからといって地形の保存を失敗にはしない）。
    pub(super) fn save_terrain_meta(&mut self, dir: &std::path::Path, only_dirty: bool) -> u32 {
        if only_dirty && !self.terrain.meta_dirty {
            return 0;
        }
        let meta = TerrainMeta::from_state(
            &self.terrain.collision_disabled,
            self.terrain.decimate_strength,
        );
        let path = dir.join(TERRAIN_META_FILE_NAME);
        match std::fs::write(&path, write_meta(&meta)) {
            Ok(()) => {
                self.terrain.meta_dirty = false;
                1
            }
            Err(e) => {
                eprintln!("[SEED terrain] terrain_meta save failed: {path:?} err={e}");
                0
            }
        }
    }

    /// 地形フォルダの terrain_meta.json を読み、`TerrainState` へ取り込む。
    ///
    /// - `terrain_dir`: アセットルート相対の地形フォルダ参照。
    ///
    /// **ファイルが無いのはエラーではない**（この機能より前に保存された地形、
    /// および一度も当たり判定を切っていない地形には存在しない）。その場合は
    /// 「全チャンク当たり判定あり・デシメート無し」という既定値のままにする。
    ///
    /// 読み込んだ無効チャンクのうち、実際には存在しないチャンク座標は捨てる
    /// （地形を作り直して小さくしたあとに古いメタが残っていると、
    ///  ありもしないチャンクの無効フラグが保存され続けるため）。
    pub(super) fn load_terrain_meta(&mut self, terrain_dir: &str) {
        let virtual_path = format!(
            "{}/{}",
            crate::engine::terrain::dir_ref::dir_virtual_path(terrain_dir),
            TERRAIN_META_FILE_NAME
        );
        let Ok(bytes) = crate::engine::asset_fs::read_bytes(&virtual_path) else {
            // 未保存／旧地形。既定値（全チャンク有効・デシメート無し）のままでよい。
            return;
        };
        let text = String::from_utf8_lossy(&bytes);
        let (meta, ok) = read_meta(&text);
        if !ok {
            eprintln!(
                "[SEED terrain] terrain_meta decode failed, using defaults: {virtual_path}"
            );
            return;
        }
        // 存在するチャンクのぶんだけ取り込む。
        let existing: HashSet<ChunkCoord> = self.terrain.chunks.keys().copied().collect();
        self.terrain.collision_disabled = meta
            .collision_disabled_set()
            .into_iter()
            .filter(|c| existing.contains(c))
            .collect();
        self.terrain.decimate_strength = meta.clamped_decimate_strength();
        // 読んだ直後は保存済みの内容と一致している。
        self.terrain.meta_dirty = false;
    }
}
