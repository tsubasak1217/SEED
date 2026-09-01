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

    // ─── IPC: リスト行クリックによる点の選択 ─────────────────

    /// インスペクタの点リストから選択された点を、ビューポート側の選択状態へ反映する
    /// （SELECT_CONTROL_POINT IPC）。
    ///
    /// ランタイム → エディタの `CONTROL_POINT_SELECTED` と対になる逆方向の経路で、
    /// 「リストで選ぶ」「ビューポートで点を掴む」のどちらからでも同じ点が選ばれる状態にする。
    ///
    /// 何もしない条件（＝存在しない点や、見えていない点にギズモを出さないためのガード）:
    ///   ・スロットが ControlPointComponent でない（誤配）
    ///   ・`index` が点列の長さの外
    ///   ・**要求されたアクタが現在の選択アクタでない**
    ///
    /// 3 つ目が重要な理由: 点キューブの表示条件 `control_points_visible()` は
    /// 「アクタが 1 つ選択されていること（`actor_virtual_selected_idx` が Some）」を要求し、
    /// さらに `resolved_control_point_paths()` は**選択中アクタの点しか集めない**。
    /// そのため別アクタの点を選択状態にすると、ビューポートに点キューブが 1 つも出ていない
    /// のに移動ギズモだけが宙に浮く、という不整合な状態になる。ここで弾いておく。
    pub(super) fn handle_select_control_point(&mut self, actor_dfs_id: u32, slot_idx: u32, index: u32) {
        // 選択アクタと一致しない要求は無視する（上記の不整合防止）。
        if self.actor_virtual_selected_idx != Some(actor_dfs_id as usize) { return; }

        let Some(entity) = self.control_point_slot_entity(actor_dfs_id, slot_idx) else { return };
        let Some(scene)  = self.scene.as_ref() else { return };
        let Some(comp)   = scene.world.get::<ControlPointComponent>(entity) else { return };
        // 点列の範囲外なら選択しない（存在しない点にギズモを出さない）。
        if (index as usize) >= comp.points.len() { return; }

        self.select_control_point(SelectedControlPoint { actor_dfs_id, slot_idx, index });
    }

    // ─── ビューポートへの D&D による点の追加 ─────────────────

    /// 画面 D&D の着弾ワールド座標に制御点を 1 つ追加する
    /// （ADD_CONTROL_POINT_AT_SCREEN の解決後に呼ばれる）。
    ///
    /// `world` は解決済みの**ワールド座標**。制御点はアクタ相対で保持する契約なので、
    /// アクタのワールド行列の逆行列でローカルへ戻してから格納する。
    ///
    /// 何もしない条件:
    ///   ・スロットが ControlPointComponent でない
    ///   ・点数がすでに `MAX_CONTROL_POINTS` に達している（上限超過を弾く）
    pub(super) fn handle_add_control_point_world(
        &mut self,
        actor_dfs_id: u32,
        slot_idx:     u32,
        world:        [f32; 3],
    ) {
        use super::find_actor_by_dfs;

        let Some(entity) = self.control_point_slot_entity(actor_dfs_id, slot_idx) else { return };

        // アクタのワールド行列の逆行列（ワールド → アクタ相対）を先に作る。
        // 可変借用の前に不変借用を終わらせるため、ここでブロックを閉じる。
        let actor_inv = {
            let Some(scene) = self.scene.as_ref() else { return };
            let mut c = 0u32;
            let Some(actor) = find_actor_by_dfs(
                &scene.actors, self.active_world_line, actor_dfs_id, &mut c) else { return };
            let tf = scene.world.get::<Transform>(actor.entity).cloned().unwrap_or_default();
            mat4x4_inv(tf.to_mat4())
        };
        let local = transform_point(&actor_inv, world);

        // Undo 用に「変更前」を先に取る（handle_set_control_points と同じ流儀）。
        let wl = self.active_world_line;
        let before_slots = self.snapshot_actor_slots(wl, actor_dfs_id);

        // 追加後の点の添字（選択に使う）。可変借用の中で決めて外へ持ち出す。
        let new_index = {
            let Some(scene) = &mut self.scene else { return };
            let Some(c) = scene.world.get_mut::<ControlPointComponent>(entity) else { return };
            // 上限に達していたら追加しない（描画・ピッキングのコスト上限を守る）。
            if !can_push_control_point(c.points.len()) { return; }
            // 時刻は push の**前**に決める（push 後だと自分自身を「最後の点」として読んでしまう）。
            let time = c.next_default_time();
            c.points.push(ControlPoint { position: local, time, ..Default::default() });
            (c.points.len() - 1) as u32
        };

        let after_slots = self.snapshot_actor_slots(wl, actor_dfs_id);
        self.undo_history.record(Box::new(ComponentSlotsSnapshotCommand {
            world_line: wl, actor_dfs_id, before_slots, after_slots,
        }));
        self.send_actor_components(actor_dfs_id, slot_idx as usize);
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }

        // 今置いた点を選択状態にする（連続ドロップでも「直前に置いた点」が分かるように）。
        // ただし `handle_select_control_point` と同じ理由で、対象アクタが選択中でない場合は
        // 選択しない（点キューブが出ていないのにギズモだけ出る不整合を防ぐ）。
        if self.actor_virtual_selected_idx == Some(actor_dfs_id as usize) {
            self.select_control_point(SelectedControlPoint {
                actor_dfs_id, slot_idx, index: new_index,
            });
        }
    }

    /// 画面 D&D の着弾点を、GPU ピック結果と地形レイマーチ結果から 1 点に決める。
    ///
    /// - `gpu_hit`: ID パスの RGB に焼かれたワールド座標（メッシュ・水面。背景なら None）
    /// - `(sx, sy)`: ビューポートのピクセル座標（地形レイマーチに使う）
    ///
    /// 地形は ID パスに描かれないため、GPU ピックだけでは地形の上に点を置けない。
    /// 逆にメッシュは CPU レイマーチの対象外なので、**両方を試して近い方を採る**。
    /// 比較はカメラからの距離の**二乗**で行う（順序が同じなので sqrt は不要）。
    ///
    /// どちらにも当たらなければ None を返す。呼び出し側はこの場合**点を追加しない**
    /// （空をドロップしたら何も起きない、という直感的な挙動にするため）。
    pub(super) fn resolve_control_point_drop_hit(
        &self,
        gpu_hit: Option<[f32; 3]>,
        sx:      u32,
        sy:      u32,
    ) -> Option<[f32; 3]> {
        let terrain_hit = self.terrain_raymarch_hit(sx as f32, sy as f32);
        let cam_v = self.camera.position();
        nearer_hit([cam_v.x, cam_v.y, cam_v.z], gpu_hit, terrain_hit)
    }

    // ─── 点選択状態 ──────────────────────────────────────────

    /// 点を選択し、エディタへ通知する（`CONTROL_POINT_SELECTED:{actor},{slot},{index}`）。
    pub(super) fn select_control_point(&mut self, sel: SelectedControlPoint) {
        self.selected_control_point = Some(sel);
        // 常設の計器: 「選択された点」と「そこにギズモを出せるか」を 1 行で残す。
        // world=None なら点の解決に失敗している（＝ギズモは描かれない）。
        // tool_mode=Select ではギズモそのものが生成されない仕様なので、
        // 「選択されたのに何も出ない」ときの最有力候補として必ず出しておく。
        println!("[CTRL_POINT] selected={},{},{} world={:?} tool_mode={:?}",
            sel.actor_dfs_id, sel.slot_idx, sel.index,
            self.selected_control_point_world_pos(), self.tool_mode);
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
    pub(super) fn build_control_point_line_batch(&self) -> Option<control_point_scene_gizmo::ControlPointGizmoLines> {
        let paths = self.resolved_control_point_paths();
        // 配置予定マーカー（D&D 中のみ）。点キューブと同じ表示条件でだけ出す
        //（Play 中や 2D ビューでマーカーだけが浮くのを防ぐ）。
        let preview = if self.control_points_visible() {
            self.control_point_drop_preview
        } else {
            None
        };
        // 点も線もマーカーも無ければ GPU バッファを作らない。
        // ※ 点が 0 個のスロットへ初めてドロップする場合は paths が空でも
        //    マーカーだけを描く必要があるため、`paths.is_empty()` 単独では打ち切れない。
        if paths.is_empty() && preview.is_none() { return None; }
        let selected = self.selected_control_point.map(|s| (s.slot_idx, s.index));
        control_point_scene_gizmo::build_control_point_lines(
            &paths, selected, preview, |p| self.control_point_cube_half(p),
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
        // ── 常設の計器（[CTRL_POINT]）──────────────────────────
        // 「キューブをクリックしてもギズモが出ない」系の切り分けは、
        //   ① そもそもピックが呼ばれているか（＝ギズモ軸ヒットに先取りされていないか）
        //   ② 候補パスが集まっているか（表示条件・選択アクタ）
        //   ③ レイが当たったか
        //   ④ ギズモが描かれる条件（ツールモード）を満たすか
        // の 4 段を 1 行ずつ見れば必ず特定できる。頻度はクリック 1 回につき 1 行なので
        // 常設しても実害が無い（毎フレームのログは絶対に足さないこと）。
        let visible    = self.control_points_visible();
        let point_total: usize = paths.iter().map(|p| p.eval.len()).sum();
        if paths.is_empty() {
            println!("[CTRL_POINT] pick skip: visible={visible} paths=0 \
                      (表示条件かアクタ選択、または ControlPoint スロットが無い)");
            return false;
        }
        let Some(ws) = self.window.as_ref().map(|w| w.inner_size()) else { return false };
        let (ro, rd) = self.editor_3d_ray(cx, cy, ws.width as f32, ws.height as f32);

        let hit = control_point_scene_gizmo::pick_control_point(
            &paths, ro, rd, |p| self.control_point_cube_half(p),
        );
        let Some(hit) = hit else {
            println!("[CTRL_POINT] pick hit=none paths={} points={point_total} \
                      cursor=({cx:.0},{cy:.0})", paths.len());
            return false;
        };
        let Some(dfs) = self.actor_virtual_selected_idx else { return false };

        println!("[CTRL_POINT] pick hit=slot{} index{} dist={:.2} tool_mode={:?}",
            hit.slot_idx, hit.index, hit.distance, self.tool_mode);
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

/// 点をもう 1 つ追加できるか（現在の点数から判定する）。
///
/// 上限判定を関数として切り出しておくのは、`App` を組み立てられないテスト環境でも
/// 「上限で止まる」という契約だけは検証できるようにするため。
fn can_push_control_point(current_len: usize) -> bool {
    current_len < MAX_CONTROL_POINTS
}

/// 2 つの候補ヒットのうち、`cam` に近い方を返す（両方 None なら None）。
///
/// 距離は**二乗**で比べる（平方根は単調増加なので大小関係が変わらず、計算が無駄）。
/// 片方だけが Some ならそれをそのまま採る。
pub(super) fn nearer_hit(cam: [f32; 3], a: Option<[f32; 3]>, b: Option<[f32; 3]>) -> Option<[f32; 3]> {
    /// カメラからの距離の二乗。
    fn dist_sq(cam: [f32; 3], p: [f32; 3]) -> f32 {
        let d = [p[0] - cam[0], p[1] - cam[1], p[2] - cam[2]];
        d[0] * d[0] + d[1] * d[1] + d[2] * d[2]
    }
    match (a, b) {
        (Some(pa), Some(pb)) => Some(if dist_sq(cam, pa) <= dist_sq(cam, pb) { pa } else { pb }),
        (Some(pa), None)     => Some(pa),
        (None,     Some(pb)) => Some(pb),
        (None,     None)     => None,
    }
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

    /// 上限に達するまでは追加でき、達したら追加できないこと（D&D 追加のコスト上限保証）。
    #[test]
    fn can_push_control_point_respects_limit() {
        assert!(can_push_control_point(0));
        assert!(can_push_control_point(MAX_CONTROL_POINTS - 1), "あと 1 個は入る");
        assert!(!can_push_control_point(MAX_CONTROL_POINTS), "上限ちょうどで打ち止め");
        assert!(!can_push_control_point(MAX_CONTROL_POINTS + 1), "壊れたデータでも増やさない");
    }

    /// 着弾点の候補が 2 つあるとき、カメラに近い方が採られること
    ///（地形の手前にメッシュがあれば地形ではなくメッシュの上に点が乗る）。
    #[test]
    fn nearer_hit_picks_closest_to_camera() {
        let cam  = [0.0, 0.0, 0.0];
        let near = [0.0, 0.0, 5.0];
        let far  = [0.0, 0.0, 50.0];
        assert_eq!(nearer_hit(cam, Some(near), Some(far)), Some(near));
        // 引数の順序を入れ替えても結果が変わらないこと
        assert_eq!(nearer_hit(cam, Some(far), Some(near)), Some(near));
    }

    /// 候補が片方しか無い／両方無い場合の扱い。
    #[test]
    fn nearer_hit_handles_missing_candidates() {
        let cam = [10.0, 20.0, 30.0];
        let p   = [1.0, 2.0, 3.0];
        assert_eq!(nearer_hit(cam, Some(p), None), Some(p), "GPU ヒットのみ");
        assert_eq!(nearer_hit(cam, None, Some(p)), Some(p), "地形ヒットのみ");
        // 何にも当たらなければ None（＝呼び出し側は点を追加しない）
        assert_eq!(nearer_hit(cam, None, None), None);
    }

    /// カメラ位置が原点でなくても、カメラからの距離で比較されること
    ///（ワールド原点からの距離で比べていたら通らない）。
    #[test]
    fn nearer_hit_measures_from_camera_not_origin() {
        let cam = [100.0, 0.0, 0.0];
        // ワールド原点には近いが、カメラからは遠い点
        let near_origin = [0.0, 0.0, 0.0];
        // ワールド原点からは遠いが、カメラのすぐ手前にある点
        let near_camera = [95.0, 0.0, 0.0];
        assert_eq!(nearer_hit(cam, Some(near_origin), Some(near_camera)), Some(near_camera));
    }
}
