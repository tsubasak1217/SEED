// ============================================================
//  control_point_ops.rs — ControlPointComponent の編集操作
//
//  ・handle_set_control_points   : SET_CONTROL_POINTS（点列の全置換）
//  ・handle_set_control_point_pos: SET_CONTROL_POINT_POS（1 点の位置更新・軽量）
//  ・点選択状態（ビューポートでどの点を掴んでいるか）の保持と通知
//
//  ## なぜ「全置換」と「1 点更新」の 2 経路なのか
//  点の追加・削除・並べ替え・属性編集は**頻度が低く、内容が複雑**なので、
//  JSON でリストごと置き換えるのが最も単純で壊れにくい（水の spline_points と同流儀）。
//  一方、ギズモのドラッグは**毎フレーム飛ぶ**ので、点列全体を JSON 化して往復させると
//  文字列生成だけでドラッグがもたつく。位置のみの軽量経路を別に用意する。
//
//  ## Undo
//  点の編集は「スロットのスナップショット差し替え」（`ComponentSlotsSnapshotCommand`）で
//  戻す。点 1 個ぶんの差分コマンドを作らないのは、点の追加・削除・並べ替えまで
//  1 つの仕組みで戻せるうえ、点列は数百点でもスナップショットが十分小さいため。
//  ドラッグは「押下時の状態」と「離した時の状態」の 2 枚だけを記録し、
//  ドラッグ中の中間フレームは記録しない（Undo 1 回で掴む前へ戻る）。
// ============================================================

use crate::engine::components::control_point_component::{
    ControlPoint, ControlPointComponent, MAX_CONTROL_POINTS,
};
use crate::engine::components::{ComponentKind, Transform};
use crate::engine::core::app_base::undo::ComponentSlotsSnapshotCommand;
use crate::engine::ecs::Entity;
use crate::engine::methods::drawer::LineBatch;
use crate::engine::methods::gizmo_interact::mat4x4_inv;
use crate::engine::structs::objects::actor::ComponentSlotData;

use super::control_point_scene_gizmo::{
    self, ResolvedPathSlot, POINT_CUBE_RADIUS_RATIO,
};
use super::{App, RuntimeMode};

// ─── ControlPointDragStart ────────────────────────────────────

/// コントロールポイントのギズモドラッグ開始スナップショット。
///
/// ドラッグ中は「押下時のワールド位置」に毎フレームのデルタ行列を掛けて新しい
/// ワールド位置を作り、アクタの逆行列でローカルへ戻して書き込む。
/// 累積誤差を避けるため、**前フレームの結果ではなく常に押下時の位置**から計算する。
#[derive(Clone)]
pub struct ControlPointDragStart {
    /// 掴んでいる点。
    pub sel: SelectedControlPoint,
    /// 押下時の点のワールド座標。
    pub start_world: [f32; 3],
    /// 押下時のアクタのワールド行列の逆行列（ワールド → アクタ相対の戻し変換）。
    pub actor_inv: [[f32; 4]; 4],
    /// 押下時の点のアクタ相対位置。
    ///
    /// 離した時にこれと現在値を比べ、**動いていなければ Undo を記録しない**
    /// （点をクリックしただけで履歴が 1 件増えるのを防ぐ）。
    pub start_local: [f32; 3],
    /// 押下時のスロットスナップショット（Undo 用。離した時に after と組で記録する）。
    pub before_slots: Vec<ComponentSlotData>,
}

// ─── SelectedControlPoint ─────────────────────────────────────

/// ビューポートで選択中のコントロールポイント 1 個の在り処。
///
/// 「どのアクタの・どのスロットの・何番目の点か」の 3 つ組で一意に定まる。
/// アクタ選択（`actor_virtual_selected_idx`）とは独立に持ち、
/// **点が選択されている間だけ**移動ギズモの対象がアクタから点へ切り替わる。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SelectedControlPoint {
    /// アクタの DFS インデックス。
    pub actor_dfs_id: u32,
    /// そのアクタ内の ControlPointComponent スロット添字。
    pub slot_idx: u32,
    /// 点列内の添字。
    pub index: u32,
}

impl App {
    // ─── スロット解決 ────────────────────────────────────────

    /// `(actor_dfs_id, slot_idx)` から ControlPointComponent スロットのエンティティを引く。
    ///
    /// スロットの種別が ControlPoint でなければ None（誤配を弾く）。
    pub(super) fn control_point_slot_entity(&self, actor_dfs_id: u32, slot_idx: u32) -> Option<Entity> {
        use super::find_actor_by_dfs;
        let scene = self.scene.as_ref()?;
        let mut c = 0u32;
        let actor = find_actor_by_dfs(&scene.actors, self.active_world_line, actor_dfs_id, &mut c)?;
        actor.slots().get(slot_idx as usize)
            .filter(|s| s.kind == ComponentKind::ControlPoint)
            .map(|s| s.entity)
    }

    // ─── IPC: 点列の全置換 ───────────────────────────────────

    /// インスペクタからの点列全置換（SET_CONTROL_POINTS IPC）。
    ///
    /// `json` は `ControlPoint` の配列。**1 点でもパースに失敗したら丸ごと無視する**
    /// （半端な点列でパスを壊さない。水の spline_points と同じ判断）。
    /// 点数が上限を超える入力は超過分を切り捨てる。
    pub(super) fn handle_set_control_points(&mut self, actor_dfs_id: u32, slot_idx: u32, json: &str) {
        let Some(entity) = self.control_point_slot_entity(actor_dfs_id, slot_idx) else { return };
        let Ok(mut points) = serde_json::from_str::<Vec<ControlPoint>>(json) else { return };
        points.truncate(MAX_CONTROL_POINTS);

        // Undo 用に「変更前」を先に取る（スナップショットは可変借用の前に済ませる）。
        let wl = self.active_world_line;
        let before_slots = self.snapshot_actor_slots(wl, actor_dfs_id);

        {
            let Some(scene) = &mut self.scene else { return };
            let Some(c) = scene.world.get_mut::<ControlPointComponent>(entity) else { return };
            c.points = points;
        }

        // 点が減って選択中の点が消えた場合は、選択を解除する（存在しない点にギズモを出さない）。
        self.clamp_control_point_selection();

        let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
        self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
            world_line: wl, actor_dfs_id, before_slots, after_slots,
        }));
        self.send_actor_components(actor_dfs_id, slot_idx as usize);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    // ─── IPC: 1 点の位置更新（軽量経路）──────────────────────

    /// 1 点の位置のみを更新する（SET_CONTROL_POINT_POS IPC）。
    ///
    /// ギズモのドラッグ中に毎フレーム飛ぶ想定なので、
    /// **点列の再送も Undo 記録も行わない**（ドラッグの確定時にまとめて記録する）。
    /// 添字が範囲外なら何もしない。
    ///
    /// 戻り値: 実際に更新できたか（呼び出し側が再描画要求の要否に使う）。
    pub(super) fn handle_set_control_point_pos(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        index:        u32,
        position:     [f32; 3],
    ) -> bool {
        let Some(entity) = self.control_point_slot_entity(actor_dfs_id, slot_idx) else { return false };
        let Some(scene) = &mut self.scene else { return false };
        let Some(c) = scene.world.get_mut::<ControlPointComponent>(entity) else { return false };
        let Some(p) = c.points.get_mut(index as usize) else { return false };
        p.position = position;
        true
    }

    // ─── 点選択状態 ──────────────────────────────────────────

    /// 点を選択し、エディタへ通知する（`CONTROL_POINT_SELECTED:{actor},{slot},{index}`）。
    pub(super) fn select_control_point(&mut self, sel: SelectedControlPoint) {
        self.selected_control_point = Some(sel);
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!(
                "CONTROL_POINT_SELECTED:{},{},{}",
                sel.actor_dfs_id, sel.slot_idx, sel.index
            ));
        }
    }

    /// 点の選択を解除し、エディタへ通知する（`CONTROL_POINT_DESELECTED`）。
    ///
    /// すでに未選択なら何もしない（無意味な IPC を毎フレーム流さないため）。
    pub(super) fn clear_control_point_selection(&mut self) {
        if self.selected_control_point.is_none() { return; }
        self.selected_control_point = None;
        if let Some(ipc) = &self.ipc { ipc.send("CONTROL_POINT_DESELECTED"); }
    }

    /// 選択中の点が「まだ存在するか」を検証し、消えていれば選択を解除する。
    ///
    /// 点列の全置換・コンポーネント削除・シーン読み込みの後に呼ぶ。
    pub(super) fn clamp_control_point_selection(&mut self) {
        let Some(sel) = self.selected_control_point else { return };
        let alive = self.control_point_slot_entity(sel.actor_dfs_id, sel.slot_idx)
            .and_then(|e| self.scene.as_ref()
                .and_then(|s| s.world.get::<ControlPointComponent>(e))
                .map(|c| (sel.index as usize) < c.points.len()))
            .unwrap_or(false);
        if !alive { self.clear_control_point_selection(); }
    }

    // ─── ビューポート: 収集・ピック ──────────────────────────

    /// 「点キューブを出す条件」を満たしているか。
    ///
    /// Edit（またはポーズ中）の 3D シーンビューで、アクタが 1 つ選ばれていること。
    /// 2D キャンバス編集タブ・2D シーンビューでは点キューブを扱わない
    /// （パスは 3D の概念であり、2D ビューのレイと座標系が噛み合わないため）。
    pub(super) fn control_points_visible(&self) -> bool {
        let in_editor = self.mode == RuntimeMode::Edit || self.paused;
        in_editor
            && !self.edit_view_is_2d()
            && !self.actor_edit_canvas_wls.contains(&self.active_world_line)
            && self.actor_virtual_selected_idx.is_some()
    }

    /// 選択中アクタの `ControlPointComponent` をワールド解決して集める。
    ///
    /// 表示条件を満たさない場合は空（描画もピックも起きない）。
    pub(super) fn resolved_control_point_paths(&self) -> Vec<ResolvedPathSlot> {
        if !self.control_points_visible() { return Vec::new(); }
        let (Some(scene), Some(dfs)) = (self.scene.as_ref(), self.actor_virtual_selected_idx)
            else { return Vec::new() };
        control_point_scene_gizmo::collect_paths_for_actor(
            &scene.actors, &scene.world, self.active_world_line, dfs as u32,
        )
    }

    /// ある位置に置く点キューブの半辺長（画面上の見かけの大きさを一定に保つ）。
    ///
    /// 移動ギズモの見かけ半径に比例させるので、投影方式（透視／正射）や
    /// ズームが変わっても点キューブとギズモの相対的な大きさが崩れない。
    pub(super) fn control_point_cube_half(&self, pos: [f32; 3]) -> f32 {
        self.editor_3d_gizmo_radius(pos) * POINT_CUBE_RADIUS_RATIO
    }

    /// 選択中アクタの制御点ギズモ（点キューブ＋区間ライン）を CPU 側で組む。
    ///
    /// フレームループがレンダラを可変借用する**前**に呼ぶこと
    /// （`&self` しか使わないので、可変借用と同時には呼べない）。
    /// 描くものが無ければ None。
    pub(super) fn build_control_point_line_batch(&self) -> Option<LineBatch> {
        let paths = self.resolved_control_point_paths();
        if paths.is_empty() { return None; }
        let selected = self.selected_control_point.map(|s| (s.slot_idx, s.index));
        control_point_scene_gizmo::build_control_point_lines(
            &paths, selected, |p| self.control_point_cube_half(p),
        )
    }

    /// カーソル位置で制御点キューブのピックを試みる。
    ///
    /// 当たったらその点を選択し、エディタへ通知して `true` を返す。
    /// 当たらなければ何もせず `false`（呼び出し側は通常のピックへ進む）。
    ///
    /// **ギズモの軸ハンドルより後、通常のオブジェクトピックより先**に呼ぶこと
    /// （掴んでいる点を動かす操作が、点の選び直しに奪われないようにするため）。
    pub(super) fn try_pick_control_point(&mut self, cx: f32, cy: f32) -> bool {
        let paths = self.resolved_control_point_paths();
        if paths.is_empty() { return false; }
        let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) else { return false };
        let (ro, rd) = self.editor_3d_ray(cx, cy, ws.width as f32, ws.height as f32);

        let hit = control_point_scene_gizmo::pick_control_point(
            &paths, ro, rd, |p| self.control_point_cube_half(p),
        );
        let Some(hit) = hit else { return false };
        let Some(dfs) = self.actor_virtual_selected_idx else { return false };

        self.select_control_point(SelectedControlPoint {
            actor_dfs_id: dfs as u32,
            slot_idx:     hit.slot_idx,
            index:        hit.index,
        });
        true
    }

    // ─── ビューポート: ギズモドラッグ ────────────────────────

    /// 選択中の点のギズモドラッグを開始する（押下時に 1 度だけ呼ぶ）。
    ///
    /// 開始できなければ何もしない（＝通常のアクタドラッグとして扱われる）。
    pub(super) fn begin_control_point_drag(&mut self) {
        use super::find_actor_by_dfs;

        let Some(sel) = self.selected_control_point else { return };
        let Some(start_world) = self.selected_control_point_world_pos() else { return };

        // アクタのワールド行列の逆行列を押下時に 1 度だけ作る
        //（ドラッグ中にアクタは動かないので、毎フレーム作り直す理由が無い）。
        let actor_inv = {
            let Some(scene) = self.scene.as_ref() else { return };
            let mut c = 0u32;
            let Some(actor) = find_actor_by_dfs(
                &scene.actors, self.active_world_line, sel.actor_dfs_id, &mut c) else { return };
            let tf = scene.world.get::<Transform>(actor.entity).cloned().unwrap_or_default();
            mat4x4_inv(tf.to_mat4())
        };

        let start_local  = transform_point(&actor_inv, start_world);
        let before_slots = self.snapshot_actor_slots(self.active_world_line, sel.actor_dfs_id);
        self.drag.control_point_drag = Some(ControlPointDragStart {
            sel, start_world, actor_inv, start_local, before_slots,
        });
    }

    /// ドラッグ中のデルタ行列を選択中の点へ適用する（カーソル移動のたびに呼ぶ）。
    ///
    /// `delta` はギズモが算出したワールド空間の差分行列。
    /// 押下時のワールド位置に掛けてからアクタ相対へ戻して書き込む。
    /// ドラッグ中でなければ何もせず `false` を返す。
    pub(super) fn apply_control_point_drag(&mut self, delta: [[f32; 4]; 4]) -> bool {
        let Some(d) = self.drag.control_point_drag.clone() else { return false };
        let world = transform_point(&delta, d.start_world);
        let local = transform_point(&d.actor_inv, world);
        self.handle_set_control_point_pos(d.sel.actor_dfs_id, d.sel.slot_idx, d.sel.index, local)
    }

    /// 点のギズモドラッグを終了し、Undo を 1 件だけ記録する（ボタンを離した時に呼ぶ）。
    ///
    /// 位置が動いていなければ何も記録しない（クリックしただけで履歴を汚さない）。
    /// ドラッグ中でなければ `false`（呼び出し側は通常のドラッグ終了処理へ進む）。
    pub(super) fn finish_control_point_drag(&mut self) -> bool {
        let Some(d) = self.drag.control_point_drag.take() else { return false };
        let wl = self.active_world_line;
        // 動いていなければ履歴を汚さずに終わる（点をクリックしただけのケース）。
        let moved = self.control_point_local_pos(d.sel)
            .map(|now| now != d.start_local)
            .unwrap_or(false);
        if !moved { return true; }
        let after_slots = self.snapshot_actor_slots(wl, d.sel.actor_dfs_id);

        self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
            world_line:   wl,
            actor_dfs_id: d.sel.actor_dfs_id,
            before_slots: d.before_slots,
            after_slots,
        }));
        self.send_actor_components(d.sel.actor_dfs_id, d.sel.slot_idx as usize);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
        true
    }

    /// 指定した点の**アクタ相対**位置を返す（存在しなければ None）。
    pub(super) fn control_point_local_pos(&self, sel: SelectedControlPoint) -> Option<[f32; 3]> {
        let entity = self.control_point_slot_entity(sel.actor_dfs_id, sel.slot_idx)?;
        let comp   = self.scene.as_ref()?.world.get::<ControlPointComponent>(entity)?;
        comp.points.get(sel.index as usize).map(|p| p.position)
    }

    /// 選択中の点のワールド座標を返す（移動ギズモの表示位置）。
    ///
    /// 点はアクタ相対で保持されているため、そのアクタの Transform を掛けて解決する。
    /// 選択が無い・点が消えている場合は None（＝ギズモは通常どおりアクタを対象にする）。
    pub(super) fn selected_control_point_world_pos(&self) -> Option<[f32; 3]> {
        use super::find_actor_by_dfs;
        use crate::engine::components::Transform;
        use crate::engine::path::PathEval;

        let sel   = self.selected_control_point?;
        let scene = self.scene.as_ref()?;
        let mut c = 0u32;
        let actor = find_actor_by_dfs(&scene.actors, self.active_world_line, sel.actor_dfs_id, &mut c)?;
        let slot  = actor.slots().get(sel.slot_idx as usize)?;
        if slot.kind != ComponentKind::ControlPoint { return None; }
        let comp  = scene.world.get::<ControlPointComponent>(slot.entity)?;
        let tf    = scene.world.get::<Transform>(actor.entity).cloned().unwrap_or_default();
        let eval  = PathEval::from_points(&comp.points, &tf);
        eval.points().get(sel.index as usize).map(|p| p.position)
    }
}

// ─── ヘルパー ────────────────────────────────────────────────

/// 行優先 4x4 行列で点（w = 1）を変換する。
///
/// `mat4x4_mul` は行列同士の積なので、点 1 個の変換のためだけに
/// 平行移動行列を組み立てるのは無駄が大きい。ここで直接展開する。
fn transform_point(m: &[[f32; 4]; 4], p: [f32; 3]) -> [f32; 3] {
    [
        m[0][0] * p[0] + m[0][1] * p[1] + m[0][2] * p[2] + m[0][3],
        m[1][0] * p[0] + m[1][1] * p[1] + m[1][2] * p[2] + m[1][3],
        m[2][0] * p[0] + m[2][1] * p[1] + m[2][2] * p[2] + m[2][3],
    ]
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 点の行列変換が「回転 → スケール → 平行移動」を正しく効かせること。
    #[test]
    fn transform_point_applies_full_trs() {
        let tf = Transform {
            position: [10.0, 0.0, 0.0],
            rotation: [0.0, 90.0, 0.0], // ローカル +X → ワールド -Z
            scale:    [2.0, 2.0, 2.0],
        };
        let m = tf.to_mat4();
        let w = transform_point(&m, [1.0, 0.0, 0.0]);
        assert!((w[0] - 10.0).abs() < 1.0e-3, "{w:?}");
        assert!((w[2] + 2.0).abs()  < 1.0e-3, "{w:?}");
    }

    /// 逆行列で戻すと元のローカル座標に一致すること
    /// （ドラッグ書き戻し「ワールド → アクタ相対」の要）。
    #[test]
    fn inverse_transform_round_trips() {
        let tf = Transform {
            position: [-3.0, 5.0, 7.0],
            rotation: [15.0, 40.0, -25.0],
            scale:    [1.5, 0.5, 2.0],
        };
        let m   = tf.to_mat4();
        let inv = mat4x4_inv(m);
        let local = [1.0, -2.0, 3.0];
        let back  = transform_point(&inv, transform_point(&m, local));
        for a in 0..3 {
            assert!((back[a] - local[a]).abs() < 1.0e-3, "往復すること: {back:?}");
        }
    }
}
