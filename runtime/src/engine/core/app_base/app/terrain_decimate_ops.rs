// ============================================================
//  terrain_decimate_ops.rs — その場デシメート（地形メッシュの頂点数削減）
//
//  【責務】
//    現在ロード中の地形メッシュを、再設定・再読込なしにその場で簡略化する。
//    アルゴリズム本体は `terrain::simplify`（エンジン非依存の純粋層）にあり、
//    ここはそれを「全チャンクへ一括適用して描画・物理・RT を更新する」統合層。
//
//  【適用の仕組み — 強度を状態に持ち、再メッシュで反映する】
//    デシメートは「頂点バッファを直接いじる」のではなく、
//    `TerrainState::decimate_strength` を設定して**全チャンクを再メッシュ**する。
//    再メッシュ経路（`remesh_chunks(RemeshOptions::immediate())`）は既に
//      - 頂点／インデックスバッファの差し替え
//      - 統合バッチキャッシュの無効化
//      - RT BLAS の prune（再構築予約）
//      - 物理コライダーの Remove→Add
//      - カバー場の派生データ（地表情報・基準メッシュ）の破棄と焼き直し予約
//    を漏れなくやるので、経路を 1 本に保てる。バッファを別経路で書き換えると、
//    この 5 つのどれかを取りこぼす（＝古い当たり判定・古い影が残る）。
//
//    強度は状態なので、この後スカルプトで再メッシュが走っても掛かり続ける
//    （＝掘った跡だけ急に高精細に戻る、という不整合が起きない）。
//
//  【SDF 密度場は変更しない — 非破壊であることの根拠】
//    簡略化はマーチングキューブスの**出力**に対して行う。密度グリッドは 1 ビットも
//    変わらないので、強度を 0 に戻して再メッシュすれば元の頂点数へ完全に戻る。
//
//  【保存されないもの】
//    デシメート結果そのもの（頂点列）はメッシュ由来なので .tvox には入らない。
//    保存されるのは**強度だけ**（地形フォルダの terrain_meta.json）で、
//    ロード時に同じ強度で作り直される。
// ============================================================

use crate::engine::components::ModelComponent;
use crate::engine::terrain::{ChunkCoord, clamp_strength};

use super::App;
use super::terrain_ops::RemeshOptions;

impl App {
    /// 全チャンクへその場デシメートを適用する（`TERRAIN_DECIMATE`）。
    ///
    /// - `strength`: 0.0〜1.0。0 を渡すと簡略化を解除して元のフル解像度へ戻す。
    ///
    /// 適用前後の頂点数を数えてエディタへ返す（ステータス表示用）。
    pub(super) fn handle_terrain_decimate(&mut self, strength: f32) {
        // 地形が無い／描画コンテキストが無い（ヘッドレス）ときは何もしない。
        if self.terrain.chunks.is_empty() || self.draw_ctx.is_none() {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_DECIMATE_ERROR:地形がありません");
            }
            return;
        }

        let strength = clamp_strength(strength);
        let before = self.total_terrain_vertex_count();

        // 強度を確定してから全チャンクを再メッシュする（順序が逆だと旧強度で作られる）。
        self.terrain.decimate_strength = strength;
        self.terrain.meta_dirty = true;

        // 走査順を決定的にする（HashMap 順だとログ・GPU 転送順が実行ごとに変わる）。
        let mut coords: Vec<ChunkCoord> = self.terrain.chunks.keys().copied().collect();
        coords.sort_by_key(|c| (c.x, c.y, c.z));

        // 即時経路: GPU 差し替え・派生キャッシュ無効化・コライダー追従をすべてその場で行う。
        // 一括操作なので、ストローク中のような遅延（`with_deferred_side_effects`）は使わない。
        self.remesh_chunks(&coords, RemeshOptions::immediate());

        let after = self.total_terrain_vertex_count();

        if let Some(ipc) = &self.ipc {
            // 形式: `TERRAIN_DECIMATE_OK:{強度},{適用前頂点数},{適用後頂点数}`
            ipc.send(&format!("TERRAIN_DECIMATE_OK:{strength},{before},{after}"));
        }
        eprintln!(
            "[SEED terrain] decimate: strength={strength:.2} vertices {before} -> {after} \
             ({:.1}% 削減) chunks={}",
            if before == 0 {
                0.0
            } else {
                (before.saturating_sub(after) as f64 / before as f64) * 100.0
            },
            coords.len()
        );
    }

    /// 現在の全地形チャンクメッシュの合計頂点数を数える。
    ///
    /// 描画に使われている `ModelComponent::model`（＝デシメート後の実体）から数えるので、
    /// 「今この瞬間に GPU が持っている頂点数」と一致する。空メッシュのチャンクは 0。
    pub(super) fn total_terrain_vertex_count(&self) -> usize {
        let Some(scene) = self.scene.as_ref() else {
            return 0;
        };
        self.terrain
            .chunk_slot_entity
            .values()
            .filter_map(|&slot| scene.world.get::<ModelComponent>(slot))
            .filter_map(|mc| mc.model.as_ref())
            .map(|model| {
                model
                    .meshes
                    .iter()
                    .flat_map(|m| m.primitives.iter())
                    .map(|p| p.vertices.len())
                    .sum::<usize>()
            })
            .sum()
    }
}
