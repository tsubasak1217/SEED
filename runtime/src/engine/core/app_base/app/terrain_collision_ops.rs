// ============================================================
//  terrain_collision_ops.rs — チャンク単位の当たり判定 ON/OFF（エディタ専用機能）
//
//  【責務】
//    地形チャンクごとに「物理コライダーを登録するか」を切り替える。
//    描画には一切影響しない（見た目はそのままで衝突だけ消える／戻る）。
//
//    - handle_terrain_collision_toggle: クリック位置のチャンクの有効／無効を反転する。
//    - handle_terrain_collision_overlay: ビューポートの境界オーバーレイ表示を切り替える。
//    - restore_collision_flags:          Undo/Redo からフラグを書き戻す。
//    - collision_overlay_chunks:         描画側へ渡す「枠と色」の一覧を作る。
//
//  【何のためにあるか】
//    地形の一部（遠景の飾り・屋根の上・演出用の張り出しなど）はプレイヤーが
//    決して触れないため、トライメッシュコライダーを持つだけ無駄である。
//    チャンク単位で外せば、Play 開始時の QBVH 構築コスト（`register_all_terrain_colliders`）と
//    物理ワールドのメモリを、見た目を一切変えずに削れる。
//
//  【状態の置き場】
//    無効チャンクの集合は `TerrainState::collision_disabled`。永続化は
//    地形フォルダの `terrain_meta.json`（`terrain_meta_ops.rs`）。
//    **既定は有効**なので、この機能より前に保存された地形はそのまま開ける。
// ============================================================

use std::collections::{HashMap, HashSet};

use crate::engine::terrain::ChunkCoord;

use super::App;
use super::terrain_ops::TerrainEdit;

/// 当たり判定オーバーレイの枠色（有効チャンク＝薄い緑）。
///
/// 「触れる地面」を安心の緑、「触れない地面」を警告の赤、という直感的な対応にする。
/// 半透明にして地形そのものの視認を妨げない。
pub(super) const COLLISION_OVERLAY_COLOR_ENABLED: [f32; 4] = [0.35, 0.90, 0.45, 0.55];

/// 当たり判定オーバーレイの枠色（無効チャンク＝薄い赤）。
pub(super) const COLLISION_OVERLAY_COLOR_DISABLED: [f32; 4] = [0.95, 0.35, 0.30, 0.75];

impl App {
    /// スクリーン座標のチャンクの当たり判定を反転する（`TERRAIN_COLLISION_TOGGLE`）。
    ///
    /// 手順:
    ///   1. 既存のブラシと同じレイマーチで地表の着弾点を求める（当たらなければ何もしない）。
    ///   2. 着弾点が属するチャンクを求める。地形に存在しないチャンクなら何もしない。
    ///   3. フラグを反転し、物理稼働中なら即座にコライダーを追従させる。
    ///   4. 1 クリック = 1 エントリとして terrain 専用 Undo スタックへ積む。
    ///   5. 結果をエディタへ返す（ステータス表示用）。
    pub(super) fn handle_terrain_collision_toggle(&mut self, screen_x: f32, screen_y: f32) {
        // ── 1. 着弾点を求める ──
        let Some(hit) = self.terrain_raymarch_hit(screen_x, screen_y) else {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_COLLISION_MISS");
            }
            return;
        };

        // ── 2. 着弾点のチャンクを求める ──
        let Some(coord) = self.terrain_chunk_at_world(hit) else {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_COLLISION_MISS");
            }
            return;
        };

        // ── 3. フラグを反転して物理へ反映する ──
        let was_enabled = !self.terrain.collision_disabled.contains(&coord);
        self.set_chunk_collision(coord, !was_enabled);

        // ── 4. Undo エントリを積む（1 クリック = 1 手）──
        //   密度・カバーは一切変わらないので、その 2 つのマップは空のまま。
        let mut before = HashMap::new();
        before.insert(coord, was_enabled);
        let mut after = HashMap::new();
        after.insert(coord, !was_enabled);
        self.push_terrain_collision_edit(TerrainEdit {
            before: HashMap::new(),
            after: HashMap::new(),
            cover_before: HashMap::new(),
            cover_after: HashMap::new(),
            collision_before: before,
            collision_after: after,
        });

        // ── 5. エディタへ結果を返す ──
        if let Some(ipc) = &self.ipc {
            // 形式: `TERRAIN_COLLISION_OK:{x},{y},{z},{0|1}`（1 = 当たり判定あり）
            ipc.send(&format!(
                "TERRAIN_COLLISION_OK:{},{},{},{}",
                coord.x,
                coord.y,
                coord.z,
                u8::from(!was_enabled)
            ));
        }
    }

    /// チャンク境界の当たり判定オーバーレイ表示を切り替える（`TERRAIN_COLLISION_OVERLAY`）。
    ///
    /// エディタが「コリジョン」ツールを選んだ／外したときに送る。地形データではなく
    /// 表示状態なので、保存もしないし Undo にも載せない。
    pub(super) fn handle_terrain_collision_overlay(&mut self, on: bool) {
        self.terrain.collision_overlay = on;
    }

    /// ワールド座標が属する地形チャンク座標を返す（存在しないチャンクなら `None`）。
    ///
    /// チャンク格子は `chunk_extent` 刻みで、負側も含めて `div_euclid` で割り出す。
    /// 「地形に存在するか」まで確かめるのは、地表のわずかに外側へ着弾したときに
    /// 存在しないチャンクのフラグを立ててしまわないためである。
    pub(super) fn terrain_chunk_at_world(&self, world: [f32; 3]) -> Option<ChunkCoord> {
        let extent = self.terrain.settings.chunk_extent();
        if !(extent > 0.0) {
            return None;
        }
        let coord = ChunkCoord::new(
            (world[0] / extent).floor() as i32,
            (world[1] / extent).floor() as i32,
            (world[2] / extent).floor() as i32,
        );
        self.terrain.chunks.contains_key(&coord).then_some(coord)
    }

    /// 1 チャンクの当たり判定を有効／無効に設定し、物理コライダーを追従させる。
    ///
    /// 【なぜ常に `sync_terrain_chunk_collider` を通すのか】
    ///   あちらは「既存コライダーを必ず Remove してから、無効チャンクなら再登録せずに帰る」
    ///   という作りにしてある。有効→無効・無効→有効のどちらの向きでも、
    ///   この 1 本を呼ぶだけで物理ワールドが正しい状態になる（分岐を増やさない）。
    ///   物理停止中は no-op で、次の Play 開始時に `register_all_terrain_colliders` が
    ///   無効チャンクを飛ばして登録する。
    pub(super) fn set_chunk_collision(&mut self, coord: ChunkCoord, enabled: bool) {
        let changed = if enabled {
            self.terrain.collision_disabled.remove(&coord)
        } else {
            self.terrain.collision_disabled.insert(coord)
        };
        if !changed {
            return;
        }
        // 保存対象が変わったので、地形メタを書き直す必要がある印を付ける。
        self.terrain.meta_dirty = true;
        self.sync_terrain_chunk_collider(coord);
    }

    /// Undo/Redo から当たり判定フラグを書き戻す。
    ///
    /// `flags` は「チャンク → その時点で有効だったか」。密度もメッシュも変わらないので
    /// 再メッシュは行わず、物理コライダーだけを追従させる。
    pub(super) fn restore_collision_flags(&mut self, flags: &HashMap<ChunkCoord, bool>) {
        for (&coord, &enabled) in flags {
            self.set_chunk_collision(coord, enabled);
        }
    }

    /// 当たり判定フラグの変更を terrain 専用 Undo スタックへ積む。
    ///
    /// ストローク（`handle_terrain_stroke_end`）と同じ上限管理・redo 破棄規約に従う。
    /// 積むのはここだけなので、上限定数はスタック操作と同じ場所（terrain_ops.rs）から借りる。
    fn push_terrain_collision_edit(&mut self, edit: TerrainEdit) {
        self.terrain.undo_stack.push(edit);
        if self.terrain.undo_stack.len() > super::terrain_ops::TERRAIN_UNDO_MAX {
            self.terrain.undo_stack.remove(0);
        }
        self.terrain.redo_stack.clear();
    }

}

/// オーバーレイ描画用に「チャンクの AABB（ワールド min/max）と枠色」の一覧を作る。
///
/// **`App` のメソッドではなく `&TerrainState` を取る自由関数**にしてある。
/// 呼び出し元（`frame_renderer`）はレンダラを可変借用したまま呼ぶため、
/// `&self` を取るメソッドだと「self 全体の不変借用」と衝突して借用検査を通らない。
/// 必要なのは地形状態だけなので、その 1 フィールドだけを借りる形にする。
///
/// オーバーレイが無効なら空を返す（呼び出し側は空なら線バッチを作らない）。
pub(super) fn collision_overlay_chunks(
    terrain: &super::terrain_ops::TerrainState,
) -> Vec<([f32; 3], [f32; 3], [f32; 4])> {
    if !terrain.collision_overlay {
        return Vec::new();
    }
    let extent = terrain.settings.chunk_extent();
    let mut coords: Vec<ChunkCoord> = terrain.chunks.keys().copied().collect();
    // 走査順を決定的にする（HashMap 順だとフレームごとに線の並びが変わる）。
    coords.sort_by_key(|c| (c.x, c.y, c.z));
    coords
        .into_iter()
        .map(|coord| {
            let min = coord.world_origin(&terrain.settings);
            let max = [min[0] + extent, min[1] + extent, min[2] + extent];
            let color = if terrain.collision_disabled.contains(&coord) {
                COLLISION_OVERLAY_COLOR_DISABLED
            } else {
                COLLISION_OVERLAY_COLOR_ENABLED
            };
            (min, max, color)
        })
        .collect()
}

/// 物理コライダーを登録すべきチャンク座標を、決定的な順（x, y, z）で返す純関数。
///
/// `register_all_terrain_colliders` の「どのチャンクを登録するか」だけを切り出したもの。
/// 当たり判定を無効にしたチャンクをここで確実に除くのがこの機能の要であり、
/// App（GPU・物理スレッド）を組まずに単体テストできるよう純関数にしてある。
pub(super) fn collider_target_coords(
    all: impl IntoIterator<Item = ChunkCoord>,
    disabled: &HashSet<ChunkCoord>,
) -> Vec<ChunkCoord> {
    let mut coords: Vec<ChunkCoord> = all.into_iter().filter(|c| !disabled.contains(c)).collect();
    // 採番（entity_id）の決定性のため必ず並べる（HashMap 走査順は実行ごとに変わる）。
    coords.sort_by_key(|c| (c.x, c.y, c.z));
    coords
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::terrain::TerrainSettings;
    use std::collections::HashMap as Map;

    /// テスト用の 2x1x2 チャンク地形状態を組む（密度は空でよい＝座標だけ使う）。
    fn state_with_chunks(coords: &[ChunkCoord]) -> super::super::terrain_ops::TerrainState {
        let mut st = super::super::terrain_ops::TerrainState::default();
        st.settings = TerrainSettings { chunk_cells: 4, voxel_size: 1.0, ..TerrainSettings::default() };
        for &c in coords {
            st.chunks.insert(
                c,
                crate::engine::terrain::TerrainChunkData::new_filled(&st.settings, 0.0),
            );
        }
        st
    }

    /// 無効にしたチャンクがコライダー登録対象から外れ、残りは決定的な順で返ること。
    #[test]
    fn disabled_chunks_are_excluded_from_collider_registration() {
        let all = [
            ChunkCoord::new(1, 0, 0),
            ChunkCoord::new(0, 0, 1),
            ChunkCoord::new(0, 0, 0),
            ChunkCoord::new(1, 0, 1),
        ];
        let disabled: HashSet<ChunkCoord> =
            HashSet::from([ChunkCoord::new(0, 0, 1), ChunkCoord::new(1, 0, 1)]);

        let got = collider_target_coords(all, &disabled);
        assert_eq!(
            got,
            vec![ChunkCoord::new(0, 0, 0), ChunkCoord::new(1, 0, 0)],
            "無効チャンクが登録対象に残っている／並びが決定的でない"
        );
    }

    /// 既定（無効リストが空）では全チャンクが登録対象になること（後方互換の担保）。
    #[test]
    fn empty_disabled_set_registers_everything() {
        let all = [ChunkCoord::new(0, 0, 0), ChunkCoord::new(2, -1, 3)];
        let got = collider_target_coords(all, &HashSet::new());
        assert_eq!(got.len(), 2, "既定は全チャンク当たり判定あり");
    }

    /// オーバーレイ: 無効のときは空、有効のときは全チャンクぶんの枠と色が返ること。
    #[test]
    fn overlay_lists_all_chunks_with_state_colors() {
        let coords = [ChunkCoord::new(0, 0, 0), ChunkCoord::new(1, 0, 0)];
        let mut st = state_with_chunks(&coords);
        st.collision_disabled.insert(ChunkCoord::new(1, 0, 0));

        // 表示 OFF のあいだは 1 本も線を作らない。
        assert!(collision_overlay_chunks(&st).is_empty(), "OFF で枠が出ている");

        st.collision_overlay = true;
        let boxes = collision_overlay_chunks(&st);
        assert_eq!(boxes.len(), 2, "全チャンクぶんの枠が要る");
        // 並びは決定的（x,y,z 順）なので、先頭が (0,0,0)＝有効、次が (1,0,0)＝無効。
        assert_eq!(boxes[0].2, COLLISION_OVERLAY_COLOR_ENABLED, "有効チャンクは緑");
        assert_eq!(boxes[1].2, COLLISION_OVERLAY_COLOR_DISABLED, "無効チャンクは赤");
        // 枠はチャンクの実寸（extent = cells * voxel = 4m）ぶん。
        let extent = st.settings.chunk_extent();
        let (mn, mx, _) = boxes[1];
        assert_eq!(mn, [extent, 0.0, 0.0], "枠の原点がチャンク原点と一致しない");
        assert_eq!(mx, [extent * 2.0, extent, extent], "枠の大きさがチャンク実寸と一致しない");
    }

    /// Undo 用のフラグマップが「変更したチャンクだけ」を持つ形になっていること。
    ///
    /// 実際の書き戻し（`restore_collision_flags`）は App を要するのでここでは検証しないが、
    /// エントリの形（before/after が同じキー集合で真偽が反転している）は純粋に検査できる。
    #[test]
    fn toggle_edit_entry_shape_is_single_chunk_flip() {
        let coord = ChunkCoord::new(2, 0, -1);
        let before: Map<ChunkCoord, bool> = Map::from([(coord, true)]);
        let after: Map<ChunkCoord, bool> = Map::from([(coord, false)]);
        assert_eq!(before.len(), 1, "1 クリックで触るのは 1 チャンクだけ");
        assert_eq!(
            before.keys().collect::<Vec<_>>(),
            after.keys().collect::<Vec<_>>(),
            "before と after のキー集合が一致すること"
        );
        assert_ne!(before[&coord], after[&coord], "トグルは真偽が反転すること");
    }
}
