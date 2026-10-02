// ============================================================
//  prefab_live_patch/write_back.rs — Play 中の変更をプレハブへ書き戻す（IPC PREFAB_WRITE_BACK）
//
//  Play 中にインスペクタで詰めた見た目は、Play を止めると Play 前の写しへ戻って消える。
//  インスタンスの根を 1 個選んで、その部分木を `.actor` へ書き戻せるようにする。
//
//  【書き方】アクタファイル化（actor_ops.rs の handle_export_actor）と同じ:
//   - 直列化は Actor::to_data、テンプレートの規則は actor_ops.rs の prepare_prefab_template
//     （根の prefab_source / prefab_hash を外す・根の位置を原点へ・入れ子のプレハブは 1 段の参照のまま）
//   - 書き込みは actor_file::save（形式の版の刻印・safe_write の世代バックアップ・プレビューの濾過）
//  加えて **スクリプトが Play 中に生成した部分木（`Actor::spawned_by_script`）は書かない**
//  （ScreenStack が積んだ画面・リストの行など。ファイルに焼くと次から二重に出る）。根自身は印があっても書く
//  （スクリプトが積んだ画面そのものを詰めて書き戻すのが主な使い道）。
//  また **根の名前・active・visible と 2D の根の CanvasTransform は今のファイルの値を保つ**
//  （配置側の持ち物。再展開・当て直しも触らない値。シーンの "CardPlaced" という名前や、ScreenStack の
//  出入りの動きの途中の位置・大きさをプレハブへ焼かないため）。3D の根はアクタファイル化と同じ規則
//  （位置だけ原点へ。子・インスタンス行列はワールド空間なので根の回転・拡大を差し替えると位置がずれる）。
//
//  【書いた後】同じパスの Play 中のインスタンス全部へ当て直す（ops.rs の live_patch_prefab_path）。
//  当て直しが各インスタンスの prefab_hash を新しい版へ揃える（書いた本人は今の状態＝新しい版なので値は動かない）。
//  Edit のシーンへの反映は Play 停止後にエディタが PREFAB_REAPPLY_PATH を送る（Play の世界は停止で捨てるため）。
// ============================================================

use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::{ActorData, ActorKind};

use super::super::actor_ops::prepare_prefab_template;
use super::super::{find_actor_by_dfs, App, RuntimeMode};
use super::wire;

impl App {
    /// IPC `PREFAB_WRITE_BACK:{actor_dfs}` を処理して応答を返す。
    ///
    /// 応答: `PREFAB_WRITE_BACK_DONE:{件数},{仮想パス}`（件数は続けて当て直した Play 中のインスタンス数）／
    /// `PREFAB_WRITE_BACK_ERROR:{理由}`。
    pub(crate) fn handle_prefab_write_back(&mut self, actor_dfs: u32) {
        let reply = match self.write_back_prefab_instance(actor_dfs) {
            Ok((count, vpath)) => wire::write_back_done(count, &vpath),
            Err(reason) => wire::write_back_error(&reason),
        };
        if let Some(ipc) = &self.ipc {
            ipc.send(&reply);
        }
    }

    /// 書き戻しの本体。戻り値は（当て直したインスタンス数, 書いたファイルの仮想パス）。
    fn write_back_prefab_instance(&mut self, actor_dfs: u32) -> Result<(usize, String), String> {
        if self.mode != RuntimeMode::Play {
            return Err("Play 中だけ書き戻せます".to_string());
        }
        let wl = self.active_world_line;

        // ── 書く中身を作る ──
        let (vpath, data) = {
            let scene = self.scene.as_ref().ok_or(wire::REASON_NO_SCENE)?;
            let mut counter = 0u32;
            let actor = find_actor_by_dfs(&scene.actors, wl, actor_dfs, &mut counter)
                .ok_or_else(|| format!("DFS ID {actor_dfs} のアクタが見つかりません"))?;
            let vpath = actor
                .prefab_source
                .clone()
                .ok_or_else(|| format!("{} はプレハブのインスタンスの根ではありません", actor.name))?;
            let mut data = actor.to_data(&scene.world);
            prune_script_spawned(actor, &mut data);
            prepare_prefab_template(&mut data);
            (vpath, data)
        };
        // 根の配置側の値は今のファイルの値を保つ（読めなければアクタファイル化と同じ規則のまま書く）
        let mut data = data;
        match crate::engine::core::app_base::actor_file::load(&vpath) {
            Ok(previous) => keep_template_root(&mut data, &previous),
            Err(e) => eprintln!("[PrefabWriteBack] 今のファイルを読めないので根の値はインスタンスのまま書きます: {vpath} {e}"),
        }

        // ── 上書きする前の版を控える（ほかのインスタンスを 3 方向で当て直すため）──
        self.remember_prefab_before_overwrite(&vpath);

        // ── 書く（actor_file が版の刻印・バックアップ・プレビューの濾過を受け持つ）──
        let target = crate::engine::asset_fs::resolve(&vpath);
        match crate::engine::core::app_base::actor_file::save(&target, &data) {
            Ok(Some(warning)) => eprintln!("[PrefabWriteBack] {warning}"),
            Ok(None) => {}
            Err(e) => return Err(format!("{vpath} へ書けませんでした: {e}")),
        }
        eprintln!("[PrefabWriteBack] Play 中の {} を {vpath} へ書き戻しました", data.name);

        // ── 同じパスの Play 中のインスタンスへ当て直す（prefab_hash も新しい版へ揃う）──
        let count = self.live_patch_prefab_path(&vpath)?;
        Ok((count, vpath))
    }
}

/// 根の配置側の値（名前・active・visible・2D の CanvasTransform）を今のファイルの値へ戻す。
///
/// 3D の根の Transform は戻さない（子の Transform・インスタンス行列がワールド空間なので、根だけ差し替えると
/// 子の位置がずれる。アクタファイル化と同じ「位置だけ原点」の規則に任せる）。
pub(crate) fn keep_template_root(data: &mut ActorData, previous: &ActorData) {
    data.name = previous.name.clone();
    data.active = previous.active;
    data.visible = previous.visible;
    let both_2d = data.actor_kind == ActorKind::Actor2D && previous.actor_kind == ActorKind::Actor2D;
    if both_2d && previous.canvas_transform.is_some() {
        data.canvas_transform = previous.canvas_transform.clone();
    }
}

/// 書き戻す直列化から、スクリプトが Play 中に生成した部分木を取り除く（根自身は残す）。
///
/// `Actor::to_data` は子の並びを 1 対 1 で保つので、同じ添字で `Actor` と `ActorData` を並べてたどる。
pub(crate) fn prune_script_spawned(actor: &Actor, data: &mut ActorData) {
    debug_assert_eq!(actor.children().len(), data.children.len(), "to_data は子の並びを保つ");
    for (i, child) in actor.children().iter().enumerate().rev() {
        if child.spawned_by_script {
            if i < data.children.len() {
                data.children.remove(i);
            }
        } else if let Some(child_data) = data.children.get_mut(i) {
            prune_script_spawned(child, child_data);
        }
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::ecs::World;

    /// スクリプトが生成した部分木（孫の位置も含む）だけを取り除き、根自身の印は無視する。
    #[test]
    fn prune_removes_script_spawned_subtrees_only() {
        let mut world = World::new();
        // 根はスクリプトが積んだ画面（印があっても書く）
        let mut root = Actor::new(world.spawn(), "Screen");
        root.spawned_by_script = true;
        let mut list = Actor::new(world.spawn(), "List");
        let mut row = Actor::new(world.spawn(), "Row0");
        row.spawned_by_script = true;
        list.children.push(Actor::new(world.spawn(), "Header"));
        list.children.push(row);
        root.children.push(list);
        let mut toast = Actor::new(world.spawn(), "Toast");
        toast.spawned_by_script = true;
        root.children.push(toast);
        root.children.push(Actor::new(world.spawn(), "Footer"));

        let mut data = root.to_data(&world);
        prune_script_spawned(&root, &mut data);

        let names: Vec<&str> = data.children.iter().map(|c| c.name.as_str()).collect();
        assert_eq!(names, ["List", "Footer"], "根直下の生成物を外す");
        let list_children: Vec<&str> = data.children[0].children.iter().map(|c| c.name.as_str()).collect();
        assert_eq!(list_children, ["Header"], "孫の位置の生成物も外す");
        assert_eq!(data.name, "Screen", "根自身は書く");
    }

    /// 根の名前・active・visible・2D の CanvasTransform は今のファイルの値を保つ（中身はインスタンスの値）。
    #[test]
    fn keep_template_root_restores_placement_values() {
        let previous: ActorData = serde_json::from_str(
            r#"{"name":"Card","actor_kind":"Actor2D","active":true,
                "canvas_transform":{"position":[0,0],"rotation":0,"scale":[1,1],"pivot":[0.5,0.5]},
                "components":[],"children":[]}"#,
        )
        .unwrap();
        let mut live: ActorData = serde_json::from_str(
            r#"{"name":"CardPlaced","actor_kind":"Actor2D","active":false,"visible":false,
                "canvas_transform":{"position":[0,0],"rotation":0,"scale":[0.9,0.9],"pivot":[0,0]},
                "components":[],"children":[{"name":"Title","components":[],"children":[]}]}"#,
        )
        .unwrap();
        keep_template_root(&mut live, &previous);
        assert_eq!(live.name, "Card");
        assert!(live.active && live.visible);
        let ct = live.canvas_transform.as_ref().unwrap();
        assert_eq!(ct.scale, [1.0, 1.0], "出入りの動きの途中の大きさを焼かない");
        assert_eq!(ct.pivot, [0.5, 0.5]);
        assert_eq!(live.children.len(), 1, "中身はインスタンスの値のまま");
    }
}
