// ============================================================
//  terrain_cover_brush_ops.rs — カバーブラシ（地形編集モードの手編集）
//
//  【責務】
//    地形編集モードの「カバー」ブラシ（消しゴム／塗り）の IPC ハンドラと、
//    純粋層（`engine::terrain::cover::brush`）へ渡すためのワールド解決を担う。
//      ① 画面座標 → 着弾点（`terrain_raymarch_hit`。密度ブラシと共通）
//      ② 素材 ID → 素材添字（cover_materials.json の並び）
//      ③ ブラシ球に触れるチャンクだけを選んで `brush_cover_chunk` を呼ぶ
//      ④ terrain 専用 Undo スタックへ載せるためのストローク集約
//
//  【なぜ terrain_cover_ops.rs と別ファイルなのか】
//    あちらは「エミッタが降らせる／轍が押す／.tcover を読み書きする」という
//    シミュレーションと永続化の層であり、既に 1600 行を超えている。
//    ブラシは入口（IPC の画面座標）も Undo の載せ先（terrain 専用スタック）も
//    別系統なので、責務ごとにファイルを分ける。
//
//  【Undo は terrain 専用スタック（`TERRAIN_UNDO`）へ載せる】
//    カバーのエミッタ系操作（シミュレート・全消去）は **メイン履歴** の管轄である
//    （入口が `CoverEmitterComponent` のインスペクタだから。docs/cover_field.md §5）。
//    しかしブラシは地形編集モードのツールバーから使う道具であり、
//    エディタはそのモード中の Ctrl+Z を `TERRAIN_UNDO` として送る
//    （`MainWindow.Input.cs`）。**操作した場所で戻る**という原則を守るため、
//    ブラシのぶんは密度・ペイントと同じ terrain 専用スタックへ載せる。
//    1 ストローク = 1 Undo 単位という区切り方も密度ブラシと完全に同じで、
//    `TERRAIN_STROKE_END`（左ボタン解放）で確定する。
// ============================================================

use std::collections::HashMap;

use crate::engine::terrain::chunk_coord::ChunkCoord;
use crate::engine::terrain::cover::{
    brush_cover_chunk_with_mask, CoverBrushMode, CoverBrushSpec, CoverField,
};

use super::terrain_brush_mask_ops::resolve_brush_mask;
use super::terrain_cover_ops::COVER_APPLY_INTERVAL_SEC;
use super::App;

// ─── 定数（マジックナンバー禁止）─────────────────────────────────────────────

/// 未知の素材 ID が来たときに消しゴムが使う素材添字。
///
/// 消しゴムは「そこにある物を消す」だけで素材添字を読まないため、
/// 未定義 ID でもエラーにせず通す（散布ブラシの消去が未知 prop_id を通すのと同じ流儀）。
const COVER_BRUSH_ERASE_FALLBACK_MATERIAL: u8 = 0;

impl App {
    // ─── IPC ハンドラ ───────────────────────────────────────────────────────

    /// カバーブラシ（`TERRAIN_COVER_BRUSH`）。画面座標から地形へレイを飛ばして適用する。
    ///
    /// - `material_id`: 塗る素材の ID（cover_materials.json の `id`）。消去時は無視される。
    /// - `target_amount`: 塗りの目標量（0..1）。消去時は無視される。
    /// - `erase`: true で消しゴム、false で塗り。
    ///
    /// 命中しなかった（空を指した）場合は `TERRAIN_COVER_BRUSH_MISS` を返して何もしない。
    #[allow(clippy::too_many_arguments)]
    pub(super) fn handle_terrain_cover_brush(
        &mut self,
        material_id: String,
        screen_x: f32,
        screen_y: f32,
        radius: f32,
        strength: f32,
        target_amount: f32,
        erase: bool,
    ) {
        // 素材定義がまだ読まれていなければここで読む（散布ブラシの ensure_terrain_props と同じ流儀）。
        if self.terrain.cover_materials.materials.is_empty() {
            self.ensure_cover_materials();
        }
        if self.terrain.chunks.is_empty() {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_COVER_BRUSH_MISS");
            }
            return;
        }

        // ─── 素材 ID を添字へ解決する ───
        //   消去は素材を読まないので未知 ID でも通す。塗りは何を塗るか決まらないので弾く。
        let material_index = match self.terrain.cover_materials.index_of(&material_id) {
            Some(index) => index as u8,
            None if erase => COVER_BRUSH_ERASE_FALLBACK_MATERIAL,
            None => {
                if let Some(ipc) = &self.ipc {
                    ipc.send(&format!(
                        "TERRAIN_COVER_BRUSH_ERROR:unknown cover material '{material_id}'"
                    ));
                }
                return;
            }
        };

        // ─── 着弾点（密度ブラシ・プレビュー球とまったく同じレイマーチ）───
        let Some(center) = self.terrain_raymarch_hit(screen_x, screen_y) else {
            if let Some(ipc) = &self.ipc {
                ipc.send("TERRAIN_COVER_BRUSH_MISS");
            }
            return;
        };

        let spec = CoverBrushSpec {
            center,
            radius,
            strength,
            mode: if erase { CoverBrushMode::Erase } else { CoverBrushMode::Paint },
            material_index,
            target_amount,
        };
        self.handle_terrain_cover_brush_world(&spec);

        // ドラッグ中はエディタがプレビューを送らないので、着弾点へ球を追従させる
        // （密度ブラシ `handle_terrain_brush` の ④ と同じ理由・同じ扱い）。
        self.terrain.brush_preview = Some((center, radius, strength));

        if let Some(ipc) = &self.ipc {
            ipc.send(&format!(
                "TERRAIN_COVER_BRUSH_OK:{},{},{}",
                center[0], center[1], center[2]
            ));
        }
    }

    /// ワールド座標を直接指定するカバーブラシ（IPC とテストの共通実体）。
    ///
    /// 【触れるチャンクだけを舐める理由】
    ///   ブラシの届く範囲は着弾点の周囲 1〜8m であり、既定 48 チャンクのうち
    ///   実際に触るのは 1〜4 個で済む。全チャンクを舐めるとドラッグ中の
    ///   1 フレームで 5 万テクセルを何度も走査することになる。
    ///
    /// 【境界の一貫性】
    ///   変化したチャンクは既存の `queue_cover_apply` 経路へ載せる。
    ///   これが 26 近傍を焼き直し待ちへ入れるので、チャンク境界の複製頂点が
    ///   同じ世代のカバー場を読む（＝隙間・段差が出ない）という規約は
    ///   エミッタ・轍とまったく同じ 1 本の経路で守られる。
    pub(super) fn handle_terrain_cover_brush_world(&mut self, spec: &CoverBrushSpec) {
        if self.terrain.chunks.is_empty() {
            return;
        }
        // ブラシ形状マスクを（指定されていれば）デコード済みにしておく。
        // 未指定なら 1 命令も走らず、この先の挙動は従来とまったく同じになる。
        self.ensure_terrain_brush_mask();
        let settings = self.terrain.settings.clone();
        let extent = settings.chunk_extent();

        // ─── ① ブラシ球に触れるチャンクを選ぶ ───
        let touched: Vec<ChunkCoord> = self
            .terrain
            .chunks
            .keys()
            .copied()
            .filter(|coord| {
                let origin = coord.world_origin(&settings);
                let max = [origin[0] + extent, origin[1] + extent, origin[2] + extent];
                !spec.is_outside_aabb(origin, max)
            })
            .collect();
        if touched.is_empty() {
            return;
        }

        // ─── ② Undo 用のストローク開始（暗黙）＆ 編集前スナップショット ───
        //   密度ブラシと同じ規約: ストロークは「stroke_active でない状態で最初の
        //   ブラシが届いたとき」に暗黙的に始まり、`TERRAIN_STROKE_END` で確定する。
        //   まだ控えていないチャンクだけを控える（2 発目以降で上書きすると
        //   「ストローク開始時点」でなくなる）。
        //
        //   カバー場がまだ無いチャンクは **空の場**を控える。「カバー場が無い」と
        //   「全テクセルが量 0」は保存規約（量 0 のチャンクはファイル削除）の上で
        //   等価なので、空の場を書き戻せば元どおりになる。
        self.terrain.stroke_active = true;
        for &coord in &touched {
            if self.terrain.cover_stroke_before.contains_key(&coord) {
                continue;
            }
            let before = self.terrain.cover.get(&coord).cloned().unwrap_or_default();
            self.terrain.cover_stroke_before.insert(coord, before);
        }

        // ─── ③ 地表情報を用意する（密度場からの派生。カバー場の書き換えに必須）───
        for &coord in &touched {
            self.ensure_cover_surface(coord);
        }

        // ─── ④ 各チャンクへブラシを当てる ───
        //   `&mut self.terrain` を 1 本取り、面情報（cover_surface）・カバー場（cover）・
        //   マスクキャッシュ（mask_cache）を **別フィールドとして**同時に借りる。
        //   フィールド単位なら不変借用と可変借用が両立するので、
        //   面情報の複製（32×32 の f32 が 2 枚 = 8KB／チャンク）もマスクの複製も要らない。
        let mut changed: Vec<ChunkCoord> = Vec::new();
        {
            let terrain = &mut self.terrain;
            let mask = resolve_brush_mask(&terrain.mask_cache, &terrain.brush_mask_path);
            for coord in touched {
                // 消しゴムは「カバー場が無いチャンク」には何もすることが無い。
                // 塗りは新しく器を作る必要があるので `entry` で用意する。
                if spec.mode == CoverBrushMode::Erase && !terrain.cover.contains_key(&coord) {
                    continue;
                }
                let Some(surface) = terrain.cover_surface.get(&coord) else { continue };
                let origin = coord.world_origin(&settings);
                let field = terrain.cover.entry(coord).or_default();
                if brush_cover_chunk_with_mask(field, surface, origin, extent, spec, mask) {
                    changed.push(coord);
                }
            }
        }

        // ─── ⑤ 変化したチャンクを保存対象・焼き直し待ちへ載せる ───
        for coord in changed {
            self.terrain.cover_dirty.insert(coord);
            self.queue_cover_apply(coord);
        }
        // 手で描いている最中は待たせる意味が無いので、次フレームで即座に焼き直す
        // （全消去・Undo の巻き戻しと同じ流儀）。
        self.terrain.cover_apply_timer = COVER_APPLY_INTERVAL_SEC;
    }

    // ─── ストロークの確定（terrain 専用 Undo スタック）──────────────────────

    /// 現在のストロークで触ったカバー場の「編集前 / 編集後」を取り出す。
    ///
    /// `handle_terrain_stroke_end` から呼ばれ、`TerrainEdit` の一部として
    /// terrain 専用 Undo スタックへ積まれる。
    ///
    /// 【実際に変化したチャンクだけを残す理由】
    ///   ブラシ球は容易に隣のチャンクへはみ出すため、スナップショットには
    ///   「触れたが 1 テクセルも変わらなかった」チャンクが必ず混ざる。
    ///   そのまま積むと Undo エントリが無駄に太り、`restore_cover_snapshots` も
    ///   無関係なチャンクを焼き直し待ちへ入れてしまう。
    ///
    /// 戻り値は `(before, after)`。両者は同じキー集合を持つ。
    /// 変化が 1 件も無ければ空のマップ 2 つ（呼び出し側は空なら Undo へ積まない）。
    pub(super) fn take_cover_stroke_snapshots(
        &mut self,
    ) -> (HashMap<ChunkCoord, CoverField>, HashMap<ChunkCoord, CoverField>) {
        let candidates = std::mem::take(&mut self.terrain.cover_stroke_before);
        let mut before: HashMap<ChunkCoord, CoverField> = HashMap::new();
        let mut after: HashMap<ChunkCoord, CoverField> = HashMap::new();
        for (coord, snapshot) in candidates {
            // 現在のカバー場（無ければ空の場）と突き合わせる。
            let current = self.terrain.cover.get(&coord).cloned().unwrap_or_default();
            if current == snapshot {
                continue;
            }
            before.insert(coord, snapshot);
            after.insert(coord, current);
        }
        (before, after)
    }
}
