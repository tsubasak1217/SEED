// ============================================================
//  template_actor_ops.rs — テンプレートアクタを「まっさらなアクタ」として追加する
//
//  【役割】
//  エディタの「アクタを追加」→「テンプレートアクタ...」（ADD_TEMPLATE_ACTOR）の受け口。
//  エディタは templates/ のテンプレートを読んで入れ子を展開し、プレハブの印を外した木を
//  一時ファイルへ書き出してから、そのパスを送ってくる（docs/template_library.md §9）。
//  ここではそれを読み、追加先の規則に従って木へ入れ、選択し、Undo を 1 件積む。
//
//  【プレハブにしない】
//  .actor のドロップ配置（actor_ops.rs）はルートに prefab_source / prefab_hash を付けて
//  インスタンスにするが、テンプレートアクタは**付けない**（利用者の決定）。
//  エディタも外しているが、ここでも木全体から外す（受け口でも保証する二重の安全）。
//
//  【読み込みの入口】
//  .actor の唯一の読み込み口 `core::app_base::actor_file::load` を通す（版の変換もそこで行われる）。
//
//  【置き場所の規則】（既存の追加と同じ。純粋な判定は decide_root_placement）
//  - 親を指定: 親の子の末尾。2D/3D の組み合わせは validate_reparent_kind
//    （3D は 2D の子にできない・2D は 2D か Canvas を持つ 3D の子にしかできない）。
//  - 親なし（ルート）:
//      3D  … キャンバス編集タブでは拒否、それ以外はトップレベル（ADD_ACTOR と同じ）
//      2D  … キャンバス編集タブならルートキャンバス（DFS 0）の子（ADD_ACTOR_2D と同じ）
//             ルートに Canvas を持つテンプレート（キャンバスそのもの）はトップレベル
//             Canvas を持たない部品は、最初のルートキャンバスの子。
//             キャンバスが無ければ、シーン（世界線 0）では既定のキャンバスを作ってその子
//             （2D ビューへの .actor ドロップと同じ規則）、編集タブではタブのルートの子。
//  位置は変えない（テンプレートに保存された CanvasTransform / Transform のまま。調整は Inspector で行う）。
// ============================================================

use crate::engine::components::{ComponentData, ComponentKind};
use crate::engine::core::app_base::actor_file;
use crate::engine::core::app_base::scene::build_actor;
use crate::engine::core::app_base::undo::ActorTreeSnapshotCommand;
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::{ActorData, ActorKind};

use super::{
    App, despawn_actor_recursive, find_actor_by_dfs, find_actor_by_dfs_mut, find_actor_by_entity_mut,
    validate_reparent_kind,
};

/// キャンバス編集タブのルート（ルート親キャンバス）の DFS 番号。
/// タブの世界線の中で DFS を 0 から数えるので常に 0（actor_ops.rs の handle_add_actor_2d と同じ前提）。
const CANVAS_EDIT_ROOT_DFS: u32 = 0;

/// シーン（ビューポート）の世界線。アクタ編集・キャンバス編集のタブは 1 以上。
const SCENE_WORLD_LINE: u32 = 0;

/// キャンバス編集タブのルートへ 3D を入れようとしたときの文言（エディタの TemplateActorTarget と同じ）。
const REJECT_3D_AT_CANVAS_EDIT_ROOT: &str = "キャンバス編集中はルートに3Dアクターを追加できません";

/// テンプレートアクタの置き場所（決定だけ。木にはまだ触らない）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum TemplatePlacement {
    /// 指定の DFS 番号のアクタの末尾の子。
    UnderDfs(u32),
    /// 指定のルートキャンバス（エンティティ）の末尾の子。
    UnderEntity(Entity),
    /// 既定のルートキャンバスを新しく作り、その子。
    UnderNewDefaultCanvas,
    /// 世界線のルートアクタ（編集タブの唯一のトップレベル）の子。
    UnderWorldLineRoot,
    /// トップレベル（シーン／タブのルート直下）。
    TopLevel,
}

/// ルート指定（親なし）の置き場所を決めるための材料（App の状態から集める）。
#[derive(Debug, Clone, Copy)]
pub(super) struct RootPlacementInput {
    /// テンプレートのルートが 2D アクタか。
    pub(super) is_2d: bool,
    /// テンプレートのルートが Canvas コンポーネントを持つか（＝キャンバスそのもの）。
    pub(super) template_has_canvas: bool,
    /// 追加先がキャンバス編集タブか。
    pub(super) in_canvas_edit_tab: bool,
    /// 追加先がシーン（世界線 0）か。
    pub(super) is_scene: bool,
    /// 追加先の世界線の最初のルートキャンバス。
    pub(super) first_root_canvas: Option<Entity>,
    /// 追加先の世界線のルートアクタが 2D の子を受け付けるか（2D か Canvas を持つ 3D）。
    pub(super) world_line_root_accepts_2d: bool,
}

/// 親なし（ルート）へ入れるときの置き場所を決める（純粋な判定）。
///
/// 規則はファイル冒頭の【置き場所の規則】。入れられないときは利用者向けの文を返す。
pub(super) fn decide_root_placement(input: RootPlacementInput) -> Result<TemplatePlacement, &'static str> {
    if !input.is_2d {
        // 3D: キャンバス編集タブのルートは 2D なので入れられない（ADD_ACTOR と同じ）
        return if input.in_canvas_edit_tab {
            Err(REJECT_3D_AT_CANVAS_EDIT_ROOT)
        } else {
            Ok(TemplatePlacement::TopLevel)
        };
    }
    if input.in_canvas_edit_tab {
        // タブのトップレベルは常に 1 つ（ルートキャンバス）なので、その子へ入れる（ADD_ACTOR_2D と同じ）
        return Ok(TemplatePlacement::UnderDfs(CANVAS_EDIT_ROOT_DFS));
    }
    if input.template_has_canvas {
        // キャンバスそのものは新しいルートキャンバスとして並べる（ドロップと同じ）
        return Ok(TemplatePlacement::TopLevel);
    }
    if let Some(canvas) = input.first_root_canvas {
        return Ok(TemplatePlacement::UnderEntity(canvas));
    }
    if input.is_scene {
        // シーンにキャンバスが 1 つも無い: 既定のキャンバスを作って受け皿にする（ドロップと同じ）
        return Ok(TemplatePlacement::UnderNewDefaultCanvas);
    }
    if input.world_line_root_accepts_2d {
        return Ok(TemplatePlacement::UnderWorldLineRoot);
    }
    // 3D のアクタ編集タブ（Canvas 無し）: 2D は子にできないので、ADD_ACTOR_2D と同じくトップレベルへ
    Ok(TemplatePlacement::TopLevel)
}

/// 世界線 `wl` の最初のルートキャンバス（トップレベルの 2D アクタで Canvas を持つもの）を探す。
///
/// ドロップ配置（`collect_root_canvas_infos().first()`）と同じ「scene.actors の並びで最初」。
pub(super) fn first_root_canvas_entity(actors: &[Actor], wl: u32) -> Option<Entity> {
    actors
        .iter()
        .filter(|a| a.world_line == wl)
        .find(|a| a.is_2d() && a.has_kind(ComponentKind::Canvas))
        .map(|a| a.entity)
}

/// 世界線 `wl` のルートアクタ（最初のトップレベル）が 2D の子を受け付けるか。
pub(super) fn world_line_root_accepts_2d(actors: &[Actor], wl: u32) -> bool {
    actors
        .iter()
        .find(|a| a.world_line == wl)
        .is_some_and(|a| a.is_2d() || a.has_kind(ComponentKind::Canvas))
}

/// アクタの木（ActorData）から prefab_source / prefab_hash を取り除く（まっさらにする）。
///
/// # 戻り値
/// 印を外したノードの数。
pub(super) fn strip_prefab_links(data: &mut ActorData) -> usize {
    let had_source = data.prefab_source.take().is_some();
    let had_hash = data.prefab_hash.take().is_some();
    let mut stripped = usize::from(had_source || had_hash);
    for child in &mut data.children {
        stripped += strip_prefab_links(child);
    }
    stripped
}

/// テンプレートのルートが Canvas コンポーネントを持つか（組み立て前のデータで見る）。
fn data_has_canvas(data: &ActorData) -> bool {
    data.components
        .iter()
        .any(|slot| matches!(slot.component, ComponentData::CanvasComponent(_)))
}

impl App {
    /// テンプレートアクタを「まっさらなアクタ」として追加する（ADD_TEMPLATE_ACTOR の受け口）。
    ///
    /// # 引数
    /// * `world_line`    - ルートへ入れるときの世界線（エディタが表示中のタブ）
    /// * `parent_dfs_id` - 親の DFS 番号（None ならルート）。番号は表示中のタブの木で数える
    /// * `path`          - エディタがまっさらにした木の一時ファイル（.actor / .actor2d）
    ///
    /// 入れられないとき・読めないときは `LOAD_ERROR:` で理由を返し、木には触らない。
    /// 成功したら Undo を 1 件（ActorTreeSnapshotCommand）積み、追加したアクタを選択する。
    pub(super) fn handle_add_template_actor(&mut self, world_line: u32, parent_dfs_id: Option<u32>, path: &str) {
        if self.scene.is_none() || self.draw_ctx.is_none() {
            return;
        }

        // ── 1. 読む（.actor の唯一の入口。版の変換もここ）→ まっさらにする ──
        let mut data = match actor_file::load(path) {
            Ok(data) => data,
            Err(e) => {
                self.reject_template_actor(&format!("テンプレートアクタを読めませんでした: {e}"));
                return;
            }
        };
        strip_prefab_links(&mut data);
        let is_2d = data.actor_kind == ActorKind::Actor2D;

        // ── 2. 置き場所を決める（まだ何も作らない）──────────────────
        let (wl, placement) = match self.resolve_template_placement(world_line, parent_dfs_id, is_2d, &data) {
            Ok(decided) => decided,
            Err(msg) => {
                self.reject_template_actor(msg);
                return;
            }
        };

        // ── 3. 組み立てる（Undo の前後の写しで挟む）────────────────────
        let before_actors = self.snapshot_actors_for_wl(wl);
        let built = {
            let host = self.scripting_host.clone();
            let ctx = self.draw_ctx.as_ref().unwrap();
            let scene = self.scene.as_mut().unwrap();
            build_actor(data, ctx, &mut scene.world, host.as_ref(), None)
        };
        let mut actor = match built {
            Ok(actor) => actor,
            Err(e) => {
                self.reject_template_actor(&format!("テンプレートアクタを組み立てられませんでした: {e}"));
                return;
            }
        };
        actor.set_world_line_recursive(wl);
        let entity = actor.entity;

        // ── 4. 木へ入れる ───────────────────────────────────────────
        if let Err(actor) = self.insert_template_actor(actor, wl, placement) {
            // 決めた親が見つからなかった（想定外）: 作ったエンティティを片付けて何もしない
            let scene = self.scene.as_mut().unwrap();
            despawn_actor_recursive(&actor, &mut scene.world);
            self.reject_template_actor("テンプレートアクタの追加先が見つかりませんでした");
            return;
        }

        // ── 5. Undo・通知・選択（選択はヒエラルキーの送信の後。エディタが先に行を作るため）──
        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));
        self.update_canvas_wl_state_for(wl);
        self.send_hierarchy();
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
        self.select_placed_actors(wl, &[entity]);
    }

    /// 追加先の世界線と置き場所を決める（木には触らない）。
    ///
    /// 親を指定したときは親の世界線（ADD_ACTOR_2D_CHILD と同じく表示中のタブの番号で親を引く）、
    /// ルートのときは命令の世界線を使う。
    fn resolve_template_placement(
        &self,
        world_line: u32,
        parent_dfs_id: Option<u32>,
        is_2d: bool,
        data: &ActorData,
    ) -> Result<(u32, TemplatePlacement), &'static str> {
        let scene = self.scene.as_ref().ok_or("シーンが読み込まれていません")?;

        if let Some(pid) = parent_dfs_id {
            let mut c = 0u32;
            let parent = find_actor_by_dfs(&scene.actors, self.active_world_line, pid, &mut c)
                .ok_or("テンプレートアクタの追加先が見つかりませんでした")?;
            let parent_info = (parent.is_2d(), parent.has_kind(ComponentKind::Canvas));
            validate_reparent_kind(is_2d, Some(parent_info))?;
            return Ok((parent.world_line, TemplatePlacement::UnderDfs(pid)));
        }

        let input = RootPlacementInput {
            is_2d,
            template_has_canvas: data_has_canvas(data),
            in_canvas_edit_tab: self.canvas_edit_sessions.contains_key(&world_line),
            is_scene: world_line == SCENE_WORLD_LINE,
            first_root_canvas: first_root_canvas_entity(&scene.actors, world_line),
            world_line_root_accepts_2d: world_line_root_accepts_2d(&scene.actors, world_line),
        };
        decide_root_placement(input).map(|placement| (world_line, placement))
    }

    /// 決めた置き場所へアクタを入れる。
    ///
    /// # 戻り値
    /// 入れられたら Ok。親が見つからなかったら、入れられなかったアクタを Err で返す（呼び出し側が片付ける）。
    fn insert_template_actor(&mut self, actor: Actor, wl: u32, placement: TemplatePlacement) -> Result<(), Actor> {
        match placement {
            TemplatePlacement::UnderDfs(pid) => {
                let scene = self.scene.as_mut().unwrap();
                let mut c = 0u32;
                match find_actor_by_dfs_mut(&mut scene.actors, wl, pid, &mut c) {
                    Some(parent) => {
                        parent.add_child(actor);
                        Ok(())
                    }
                    None => Err(actor),
                }
            }
            TemplatePlacement::UnderEntity(canvas) => {
                let scene = self.scene.as_mut().unwrap();
                match find_actor_by_entity_mut(&mut scene.actors, canvas) {
                    Some(parent) => {
                        parent.add_child(actor);
                        Ok(())
                    }
                    None => Err(actor),
                }
            }
            TemplatePlacement::UnderNewDefaultCanvas => {
                let canvas = self.spawn_default_root_canvas_in(wl);
                let scene = self.scene.as_mut().unwrap();
                match find_actor_by_entity_mut(&mut scene.actors, canvas) {
                    Some(parent) => {
                        parent.add_child(actor);
                        Ok(())
                    }
                    None => Err(actor),
                }
            }
            TemplatePlacement::UnderWorldLineRoot => {
                self.attach_actor_to_wl_root(actor, wl);
                Ok(())
            }
            TemplatePlacement::TopLevel => {
                self.scene.as_mut().unwrap().actors.push(actor);
                Ok(())
            }
        }
    }

    /// 追加できない理由をエディタへ返す（既存のアクタ追加の拒否と同じ LOAD_ERROR の経路）。
    fn reject_template_actor(&self, msg: &str) {
        eprintln!("[TemplateActor] {msg}");
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("LOAD_ERROR:{msg}"));
        }
    }
}

// ============================================================
//  テスト — まっさらにする・置き場所の判定（App を作らずに確かめられる部分）
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::{CanvasComponent, CanvasTransform};
    use crate::engine::ecs::World;

    /// 入れ子の木（ルート・子・孫）のすべてから印が外れ、数が合うこと。
    #[test]
    fn strip_prefab_links_clears_every_node() {
        let mut data: ActorData = serde_json::from_str(
            r#"{ "name": "Root", "prefab_source": "assets://a.actor", "prefab_hash": "0123456789abcdef",
                 "components": [],
                 "children": [
                   { "name": "Child", "prefab_source": "assets://b.actor", "components": [],
                     "children": [ { "name": "Grand", "prefab_hash": "fedcba9876543210",
                                     "components": [], "children": [] } ] },
                   { "name": "Plain", "components": [], "children": [] } ] }"#,
        )
        .expect("ActorData として読めること");

        assert_eq!(strip_prefab_links(&mut data), 3, "印を持っていたのはルート・Child・Grand の 3 つ");
        assert!(data.prefab_source.is_none() && data.prefab_hash.is_none());
        let child = &data.children[0];
        assert!(child.prefab_source.is_none());
        assert!(child.children[0].prefab_hash.is_none());
        assert_eq!(strip_prefab_links(&mut data), 0, "2 度目は何も外さない");
    }

    /// ルートの置き場所の判定表（ファイル冒頭の規則どおり）。
    #[test]
    fn decide_root_placement_follows_canvas_rules() {
        let mut world = World::new();
        let canvas = world.spawn();
        let base = RootPlacementInput {
            is_2d: true,
            template_has_canvas: false,
            in_canvas_edit_tab: false,
            is_scene: true,
            first_root_canvas: None,
            world_line_root_accepts_2d: false,
        };

        // 3D
        assert_eq!(decide_root_placement(RootPlacementInput { is_2d: false, ..base }), Ok(TemplatePlacement::TopLevel));
        assert_eq!(
            decide_root_placement(RootPlacementInput { is_2d: false, in_canvas_edit_tab: true, ..base }),
            Err(REJECT_3D_AT_CANVAS_EDIT_ROOT)
        );
        // 2D: キャンバス編集タブはルートキャンバスの子
        assert_eq!(
            decide_root_placement(RootPlacementInput { in_canvas_edit_tab: true, ..base }),
            Ok(TemplatePlacement::UnderDfs(CANVAS_EDIT_ROOT_DFS))
        );
        // 2D: キャンバスそのものはトップレベル
        assert_eq!(
            decide_root_placement(RootPlacementInput { template_has_canvas: true, first_root_canvas: Some(canvas), ..base }),
            Ok(TemplatePlacement::TopLevel)
        );
        // 2D 部品: 最初のルートキャンバスの子 / 無ければシーンでは既定のキャンバスを作る
        assert_eq!(
            decide_root_placement(RootPlacementInput { first_root_canvas: Some(canvas), ..base }),
            Ok(TemplatePlacement::UnderEntity(canvas))
        );
        assert_eq!(decide_root_placement(base), Ok(TemplatePlacement::UnderNewDefaultCanvas));
        // 編集タブ（シーンでない）: タブのルートが受け付ければその子、だめならトップレベル
        assert_eq!(
            decide_root_placement(RootPlacementInput { is_scene: false, world_line_root_accepts_2d: true, ..base }),
            Ok(TemplatePlacement::UnderWorldLineRoot)
        );
        assert_eq!(
            decide_root_placement(RootPlacementInput { is_scene: false, ..base }),
            Ok(TemplatePlacement::TopLevel)
        );
    }

    /// 最初のルートキャンバスは世界線ごとに並び順で最初のもの。Canvas の無い 2D は数えない。
    #[test]
    fn first_root_canvas_is_per_world_line() {
        let mut world = World::new();
        let plain_2d = {
            let e = world.spawn();
            world.insert(e, CanvasTransform::default());
            Actor::new_2d(e, "Plain")
        };
        let make_canvas = |world: &mut World, wl: u32| {
            let e = world.spawn();
            world.insert(e, CanvasTransform::default());
            let slot = world.spawn();
            world.insert(slot, CanvasComponent::default());
            let mut a = Actor::new_2d(e, "Canvas");
            a.world_line = wl;
            a.add_slot_typed::<CanvasComponent>("Canvas".to_string(), ComponentKind::Canvas, slot);
            a
        };
        let other_tab = make_canvas(&mut world, 3);
        let scene_canvas = make_canvas(&mut world, 0);
        let scene_canvas_entity = scene_canvas.entity;
        let actors = vec![plain_2d, other_tab, scene_canvas];

        assert_eq!(first_root_canvas_entity(&actors, 0), Some(scene_canvas_entity), "Canvas の無い 2D は飛ばす");
        assert!(first_root_canvas_entity(&actors, 7).is_none(), "無い世界線は None");
        assert!(world_line_root_accepts_2d(&actors, 3), "Canvas を持つタブのルートは 2D を受け付ける");
        assert!(!world_line_root_accepts_2d(&actors, 7), "ルートの無い世界線は受け付けない");
    }
}
