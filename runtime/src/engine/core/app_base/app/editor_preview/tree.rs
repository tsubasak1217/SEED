// ============================================================
//  editor_preview/tree.rs — プレビューの木の処理（App に依存しない純粋な関数）
//
//  【DFS の数え方】find_actor_by_dfs と同じ（世界線が一致するトップレベルを順に、各ルートは自分 → 子の深さ優先）。
//  エディタのヒエラルキーの行番号・IPC の宛先と同じ番号になる。
//
//  【名前のパス】'/' 区切りの名前を直下の子から順にたどる（各段は最初に一致した子。空の段は飛ばす。
//  パス全体が空ならそのアクタ自身）。入れ子のプレビューの作り直しは名前の列（Vec<String>）でたどる
//  （名前に '/' を含んでもよい）。
// ============================================================

use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::EditorPreviewInfo;

/// 名前のパスの区切り。
const PATH_SEPARATOR: char = '/';

// ── 名前のパスでたどる ─────────────────────────────────────────

/// '/' 区切りのパスを名前の段に分ける（空の段は飛ばす。"A//B" や "/A/" も A → B / A として読む）。
fn path_segments(path: &str) -> impl Iterator<Item = &str> {
    path.split(PATH_SEPARATOR).filter(|segment| !segment.is_empty())
}

/// 名前の段を直下の子から順にたどる（各段は最初に一致した子）。たどれなかった段の名前を Err で返す。
fn walk_names<'a, 'n>(actor: &'a Actor, names: impl IntoIterator<Item = &'n str>) -> Result<&'a Actor, &'n str> {
    let mut node = actor;
    for name in names {
        node = node.children().iter().find(|child| child.name == name).ok_or(name)?;
    }
    Ok(node)
}

/// `walk_names` の可変版（たどれなければ None）。
fn walk_names_mut<'a, 'n>(actor: &'a mut Actor, names: impl IntoIterator<Item = &'n str>) -> Option<&'a mut Actor> {
    let mut node = actor;
    for name in names {
        node = node.children_mut().iter_mut().find(|child| child.name == name)?;
    }
    Some(node)
}

/// '/' 区切りのパスで子孫をたどる。たどれなかったら、見つからなかった段の名前を返す（利用者向けの文に使う）。
pub(super) fn walk_child_path<'a>(actor: &'a Actor, path: &str) -> Result<&'a Actor, String> {
    walk_names(actor, path_segments(path)).map_err(str::to_string)
}

/// '/' 区切りのパスで子孫をたどる（空なら actor 自身。たどれなければ None）。
pub(super) fn find_child_by_path<'a>(actor: &'a Actor, path: &str) -> Option<&'a Actor> {
    walk_names(actor, path_segments(path)).ok()
}

/// `find_child_by_path` の可変版。
pub(super) fn find_child_by_path_mut<'a>(actor: &'a mut Actor, path: &str) -> Option<&'a mut Actor> {
    walk_names_mut(actor, path_segments(path))
}

/// 名前の列で子孫をたどる（空なら actor 自身）。入れ子のプレビューの親の道筋（`NestedPreview::parent_path`）に使う。
pub(super) fn find_child_by_names<'a>(actor: &'a Actor, names: &[String]) -> Option<&'a Actor> {
    walk_names(actor, names.iter().map(String::as_str)).ok()
}

/// `find_child_by_names` の可変版。
pub(super) fn find_child_by_names_mut<'a>(actor: &'a mut Actor, names: &[String]) -> Option<&'a mut Actor> {
    walk_names_mut(actor, names.iter().map(String::as_str))
}

// ── DFS 番号からプレビューの根を引く ─────────────────────────────

/// あるアクタを含む、いちばん近いプレビューの根の情報。
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct PreviewRootRef {
    /// 根の DFS 番号（その世界線の中で数える）
    pub(crate) dfs: u32,
    /// 根のエンティティ
    pub(crate) entity: Entity,
    /// 問い合わせたアクタ自身が根か
    pub(crate) is_self: bool,
    /// 根の印（作り直しの材料）
    pub(crate) info: EditorPreviewInfo,
}

/// 部分木を DFS 順にたどり、目的の番号のアクタを含むいちばん近いプレビューの根を探す（実体）。
///
/// # 戻り値
/// 目的の番号をこの部分木で見つけたら `Some(答え)`（答えは根の (DFS, アクタ) か、プレビューの外なら None）。
/// 見つからなければ None（counter は部分木のアクタ数だけ進む）。
fn nearest_root_in<'a>(
    actor: &'a Actor,
    counter: &mut u32,
    target: u32,
    nearest: Option<(u32, &'a Actor)>,
) -> Option<Option<(u32, &'a Actor)>> {
    let my_dfs = *counter;
    *counter += 1;
    // 自分が根なら、自分と子孫にとっていちばん近い根は自分
    let nearest = if actor.editor_preview.is_some() { Some((my_dfs, actor)) } else { nearest };
    if my_dfs == target {
        return Some(nearest);
    }
    actor
        .children()
        .iter()
        .find_map(|child| nearest_root_in(child, counter, target, nearest))
}

/// 世界線 `wl` の DFS 番号 `dfs` のアクタを含む、いちばん近いプレビューの根（自分か祖先）を (DFS, アクタ) で返す。
fn nearest_root_of_dfs(actors: &[Actor], wl: u32, dfs: u32) -> Option<(u32, &Actor)> {
    let mut counter = 0u32;
    actors
        .iter()
        .filter(|actor| actor.world_line == wl)
        .find_map(|root| nearest_root_in(root, &mut counter, dfs, None))
        .flatten()
}

/// 世界線 `wl` の DFS 番号 `dfs` のアクタ自身か祖先のうち、いちばん近いプレビューの根を返す
/// （プレビューの外・番号のアクタが無いときは None）。
pub(crate) fn preview_root_of_dfs(actors: &[Actor], wl: u32, dfs: u32) -> Option<PreviewRootRef> {
    let (root_dfs, root) = nearest_root_of_dfs(actors, wl, dfs)?;
    let info = root.editor_preview.clone()?;
    Some(PreviewRootRef { dfs: root_dfs, entity: root.entity, is_self: root_dfs == dfs, info })
}

/// 世界線 `wl` の DFS 番号 `dfs` のアクタがプレビューの部分木の中か（根を含む）。
pub(crate) fn is_dfs_in_preview(actors: &[Actor], wl: u32, dfs: u32) -> bool {
    nearest_root_of_dfs(actors, wl, dfs).is_some()
}

// ── 根を集める ─────────────────────────────────────────────

/// 世界線 `wl` のプレビューの根のうち、いちばん外側のものだけを DFS 順に集める（入れ子の内側は数えない＝外側と一緒に消える）。
pub(super) fn outermost_preview_roots(actors: &[Actor], wl: u32) -> Vec<Entity> {
    fn collect(actor: &Actor, out: &mut Vec<Entity>) {
        if actor.editor_preview.is_some() {
            out.push(actor.entity);
            return;
        }
        for child in actor.children() {
            collect(child, out);
        }
    }
    let mut out = Vec::new();
    for root in actors.iter().filter(|actor| actor.world_line == wl) {
        collect(root, &mut out);
    }
    out
}

/// 印が `matches` に当てはまるプレビューの根を、世界線を問わず (世界線, エンティティ) で DFS 順に集める。
///
/// 当てはまる根の内側にある当てはまる根は数えない（外側を作り直すと入れ子も作り直される）。
/// 当てはまらない根の内側は探す（その中の当てはまる根は、それ自身が外側になる）。
pub(super) fn matching_preview_roots(
    actors: &[Actor],
    matches: impl Fn(&EditorPreviewInfo) -> bool,
) -> Vec<(u32, Entity)> {
    fn collect(
        actor: &Actor,
        wl: u32,
        matches: &dyn Fn(&EditorPreviewInfo) -> bool,
        out: &mut Vec<(u32, Entity)>,
    ) {
        if actor.editor_preview.as_ref().is_some_and(|info| matches(info)) {
            out.push((wl, actor.entity));
            return;
        }
        for child in actor.children() {
            collect(child, wl, matches, out);
        }
    }
    let mut out = Vec::new();
    for root in actors {
        // 世界線はトップレベルが持つ（子は親と同じ世界線）
        collect(root, root.world_line, &matches, &mut out);
    }
    out
}

/// (世界線, エンティティ) の並びを世界線ごとにまとめる（世界線は最初に現れた順、各世界線の中は元の順）。
///
/// 作り直しの Undo を世界線ごとに 1 件だけ積むために使う。
pub(super) fn group_roots_by_world_line(roots: Vec<(u32, Entity)>) -> Vec<(u32, Vec<Entity>)> {
    let mut groups: Vec<(u32, Vec<Entity>)> = Vec::new();
    for (wl, entity) in roots {
        match groups.iter_mut().find(|(group_wl, _)| *group_wl == wl) {
            Some((_, entities)) => entities.push(entity),
            None => groups.push((wl, vec![entity])),
        }
    }
    groups
}

// ── 入れ子のプレビュー ─────────────────────────────────────────

/// プレビューの根の内側にある、入れ子のプレビュー 1 つ分（作り直した後に同じ場所へ作り直すための控え）。
#[derive(Clone, Debug, PartialEq)]
pub(super) struct NestedPreview {
    /// 根からその親までの名前（根自身は含まない。根の直下なら空）
    pub(super) parent_path: Vec<String>,
    /// 入れ子のプレビューの印（作り直しの材料）
    pub(super) info: EditorPreviewInfo,
}

/// `root` の内側（root 自身は除く）にあるプレビューの根を、内側の入れ子も含めて DFS 順に集める。
///
/// DFS 順なので、外側の入れ子を先に作り直せば、その中を通る道筋の入れ子も後でたどれる。
pub(super) fn nested_previews(root: &Actor) -> Vec<NestedPreview> {
    fn collect(actor: &Actor, path: &mut Vec<String>, out: &mut Vec<NestedPreview>) {
        if let Some(info) = &actor.editor_preview {
            out.push(NestedPreview { parent_path: path.clone(), info: info.clone() });
        }
        path.push(actor.name.clone());
        for child in actor.children() {
            collect(child, path, out);
        }
        path.pop();
    }
    let mut out = Vec::new();
    let mut path = Vec::new();
    for child in root.children() {
        collect(child, &mut path, &mut out);
    }
    out
}

/// 木全体の prefab_source / prefab_hash / editor_preview を外す（読み込んだプレハブの中の印を消す）。
///
/// 外さないと、プレビューの中身が「プレハブから更新」「版ずれ」の対象に数えられ、
/// 入れ子の印がプレビューの根として扱われてしまう。
pub(super) fn clear_links_recursive(actor: &mut Actor) {
    actor.prefab_source = None;
    actor.prefab_hash = None;
    actor.editor_preview = None;
    for child in actor.children_mut() {
        clear_links_recursive(child);
    }
}

// ── 木の中の位置 ─────────────────────────────────────────────

/// アクタの木の中の位置（親と、親の子の並びの中の番号）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct TreeLocation {
    /// 親のエンティティ（None = トップレベル。`Scene::actors` の中）
    pub(super) parent: Option<Entity>,
    /// 親の子の並び（トップレベルなら `Scene::actors`）の中の番号
    pub(super) index: usize,
}

/// エンティティのアクタが木のどこにあるかを返す（世界線を問わない。見つからなければ None）。
///
/// 作り直したプレビューの根を、古い根があった親の同じ位置へ差し替えるために使う。
pub(super) fn locate_in_parent(actors: &[Actor], entity: Entity) -> Option<TreeLocation> {
    fn locate_in_children(parent: &Actor, entity: Entity) -> Option<TreeLocation> {
        if let Some(index) = parent.children().iter().position(|child| child.entity == entity) {
            return Some(TreeLocation { parent: Some(parent.entity), index });
        }
        parent.children().iter().find_map(|child| locate_in_children(child, entity))
    }
    if let Some(index) = actors.iter().position(|actor| actor.entity == entity) {
        return Some(TreeLocation { parent: None, index });
    }
    actors.iter().find_map(|actor| locate_in_children(actor, entity))
}

/// エンティティのアクタ自身か、その祖先のどれかが `matches` に当たるか（世界線を問わない。見つからなければ false）。
///
/// プレビューの差し込み先が地形の部分木の中かを確かめるのに使う（ops.rs の resolve_insert_target。レビュー #7）。
pub(super) fn entity_or_ancestor_matches(actors: &[Actor], entity: Entity, matches: impl Fn(&Actor) -> bool + Copy) -> bool {
    /// `actor` の部分木に `entity` があるかを探し、あれば道筋（`actor` から `entity` まで）のどれかが当たるかを返す。
    fn search(actor: &Actor, entity: Entity, matches: impl Fn(&Actor) -> bool + Copy, ancestor_matched: bool) -> Option<bool> {
        let matched = ancestor_matched || matches(actor);
        if actor.entity == entity {
            return Some(matched);
        }
        actor.children().iter().find_map(|child| search(child, entity, matches, matched))
    }
    actors.iter().find_map(|actor| search(actor, entity, matches, false)).unwrap_or(false)
}

// ============================================================
//  テスト — 名前のパス・DFS からの根の引き方・根の集め方・入れ子の控え・印の外し方・位置
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::ecs::World;

    /// テスト用: 名前だけのアクタ（エンティティは World から払い出す）。
    fn actor(world: &mut World, name: &str) -> Actor {
        Actor::new(world.spawn(), name)
    }

    /// テスト用: 先に作った親へ子を足す（World の借用が重ならないよう、親は呼び出し側で作る）。
    fn with_children(mut parent: Actor, children: Vec<Actor>) -> Actor {
        for child in children {
            parent.add_child(child);
        }
        parent
    }

    /// テスト用: プレビューの印（中身のプレハブだけ）。
    fn info(prefab: &str) -> EditorPreviewInfo {
        EditorPreviewInfo { prefab: prefab.to_string(), frame: None, frame_body: String::new(), layer_bias: 0 }
    }

    /// テスト用: プレビューの根にする。
    fn preview(mut actor: Actor, prefab: &str) -> Actor {
        actor.editor_preview = Some(info(prefab));
        actor
    }

    /// 名前のパス: 空なら自分、'/' で降りる、各段は最初に一致した子、空の段は飛ばす、見つからない段の名前を返す。
    #[test]
    fn paths_walk_children_by_name() {
        let mut world = World::new();
        let first_a = with_children(actor(&mut world, "A"), vec![actor(&mut world, "B")]);
        let second_a = with_children(actor(&mut world, "A"), vec![actor(&mut world, "Other")]);
        let mut root = with_children(actor(&mut world, "Root"), vec![first_a, second_a]);

        assert_eq!(find_child_by_path(&root, "").map(|a| a.name.as_str()), Some("Root"), "空なら自分");
        assert_eq!(find_child_by_path(&root, "A/B").map(|a| a.name.as_str()), Some("B"));
        assert_eq!(find_child_by_path(&root, "/A//B/").map(|a| a.name.as_str()), Some("B"), "空の段は飛ばす");
        assert!(find_child_by_path(&root, "A/Other").is_none(), "各段は最初に一致した子（2 つ目の A は見ない）");
        assert_eq!(walk_child_path(&root, "A/Missing/C").err().as_deref(), Some("Missing"), "見つからない段の名前");

        // 可変版と名前の列版
        find_child_by_path_mut(&mut root, "A/B").expect("たどれること").name = "Renamed".to_string();
        let names = vec!["A".to_string(), "Renamed".to_string()];
        assert!(find_child_by_names(&root, &names).is_some(), "名前の列でたどれること");
        assert!(find_child_by_names_mut(&mut root, &[]).is_some_and(|a| a.name == "Root"), "空の列は自分");
    }

    /// DFS 番号から、いちばん近いプレビューの根を引く（自分が根・中のノード・外・入れ子・別の世界線）。
    #[test]
    fn preview_root_is_found_by_dfs() {
        let mut world = World::new();
        // 世界線 0:
        //   0 Scene
        //   1 ├─ Screens
        //   2 │   └─ Home（根）
        //   3 │       ├─ Header
        //   4 │       └─ Dialog（入れ子の根）
        //   5 │           └─ Ok
        //   6 └─ Hud
        // 世界線 1（別のタブ。数えない）: Tab
        let dialog = preview(with_children(actor(&mut world, "Dialog"), vec![actor(&mut world, "Ok")]), "assets://dialog.actor");
        let home = preview(
            with_children(actor(&mut world, "Home"), vec![actor(&mut world, "Header"), dialog]),
            "assets://home.actor",
        );
        let screens = with_children(actor(&mut world, "Screens"), vec![home]);
        let scene_root = with_children(actor(&mut world, "Scene"), vec![screens, actor(&mut world, "Hud")]);
        let mut tab = preview(actor(&mut world, "Tab"), "assets://tab.actor");
        tab.world_line = 1;
        let actors = vec![tab, scene_root];

        let home_ref = preview_root_of_dfs(&actors, 0, 2).expect("根自身");
        assert_eq!((home_ref.dfs, home_ref.is_self), (2, true));
        assert_eq!(home_ref.info.prefab, "assets://home.actor");
        let header = preview_root_of_dfs(&actors, 0, 3).expect("中のノード");
        assert_eq!((header.dfs, header.is_self, header.entity), (2, false, home_ref.entity));
        let ok = preview_root_of_dfs(&actors, 0, 5).expect("入れ子の中");
        assert_eq!((ok.dfs, ok.info.prefab.as_str()), (4, "assets://dialog.actor"), "いちばん近い根（入れ子）");
        assert!(preview_root_of_dfs(&actors, 0, 1).is_none(), "外（親）");
        assert!(preview_root_of_dfs(&actors, 0, 6).is_none(), "外（後ろの兄弟）");
        assert!(preview_root_of_dfs(&actors, 0, 99).is_none(), "番号のアクタが無い");
        assert!(preview_root_of_dfs(&actors, 1, 0).is_some_and(|r| r.is_self), "世界線ごとに 0 から数える");

        assert!(is_dfs_in_preview(&actors, 0, 2) && is_dfs_in_preview(&actors, 0, 5));
        assert!(!is_dfs_in_preview(&actors, 0, 0) && !is_dfs_in_preview(&actors, 0, 6));
    }

    /// いちばん外側の根だけを集める（入れ子の内側は数えない・世界線で絞る）。
    #[test]
    fn outermost_roots_skip_nested_and_other_world_lines() {
        let mut world = World::new();
        let inner = preview(actor(&mut world, "Inner"), "assets://inner.actor");
        let outer = preview(with_children(actor(&mut world, "Outer"), vec![inner]), "assets://outer.actor");
        let outer_entity = outer.entity;
        let second = preview(actor(&mut world, "Second"), "assets://second.actor");
        let second_entity = second.entity;
        let root = with_children(actor(&mut world, "Root"), vec![outer, actor(&mut world, "Plain"), second]);
        let mut tab = preview(actor(&mut world, "Tab"), "assets://tab.actor");
        tab.world_line = 2;
        let tab_entity = tab.entity;
        let actors = vec![root, tab];

        assert_eq!(outermost_preview_roots(&actors, 0), vec![outer_entity, second_entity]);
        assert_eq!(outermost_preview_roots(&actors, 2), vec![tab_entity]);
        assert!(outermost_preview_roots(&actors, 5).is_empty());
    }

    /// 当てはまる根の外側だけを集める（当てはまらない根の中は探す・世界線を問わない）。世界線ごとのまとめ方。
    #[test]
    fn matching_roots_keep_only_outer_matches() {
        let mut world = World::new();
        // Root
        // ├─ A（home・当てはまる）
        // │   └─ A2（home・当てはまるが外側が当てはまるので数えない）
        // └─ B（other・当てはまらない）
        //     └─ B2（home・当てはまる。外側が当てはまらないので自分が外側）
        let a2 = preview(actor(&mut world, "A2"), "assets://home.actor");
        let a = preview(with_children(actor(&mut world, "A"), vec![a2]), "assets://home.actor");
        let a_entity = a.entity;
        let b2 = preview(actor(&mut world, "B2"), "assets://home.actor");
        let b2_entity = b2.entity;
        let b = preview(with_children(actor(&mut world, "B"), vec![b2]), "assets://other.actor");
        let root = with_children(actor(&mut world, "Root"), vec![a, b]);
        let mut tab = preview(actor(&mut world, "Tab"), "assets://home.actor");
        tab.world_line = 3;
        let tab_entity = tab.entity;
        let actors = vec![root, tab];

        let found = matching_preview_roots(&actors, |info| info.prefab == "assets://home.actor");
        assert_eq!(found, vec![(0, a_entity), (0, b2_entity), (3, tab_entity)]);

        let grouped = group_roots_by_world_line(found);
        assert_eq!(grouped, vec![(0, vec![a_entity, b2_entity]), (3, vec![tab_entity])], "世界線ごと・現れた順");
    }

    /// 入れ子のプレビューを DFS 順に、根からその親までの名前の道筋つきで控える（根自身は除く）。
    #[test]
    fn nested_previews_are_listed_in_dfs_order_with_parent_paths() {
        let mut world = World::new();
        // Root（根。控えない）
        // ├─ Body
        // │   └─ A（入れ子）
        // │       └─ Slot
        // │           └─ B（入れ子の入れ子）
        // └─ C（入れ子。根の直下）
        let b = preview(actor(&mut world, "B"), "assets://b.actor");
        let slot = with_children(actor(&mut world, "Slot"), vec![b]);
        let a = preview(with_children(actor(&mut world, "A"), vec![slot]), "assets://a.actor");
        let body = with_children(actor(&mut world, "Body"), vec![a]);
        let c = preview(actor(&mut world, "C"), "assets://c.actor");
        let root = preview(with_children(actor(&mut world, "Root"), vec![body, c]), "assets://root.actor");

        let nested = nested_previews(&root);
        let as_strings = |path: &[&str]| path.iter().map(|s| s.to_string()).collect::<Vec<_>>();
        assert_eq!(
            nested,
            vec![
                NestedPreview { parent_path: as_strings(&["Body"]), info: info("assets://a.actor") },
                NestedPreview { parent_path: as_strings(&["Body", "A", "Slot"]), info: info("assets://b.actor") },
                NestedPreview { parent_path: Vec::new(), info: info("assets://c.actor") },
            ]
        );
    }

    /// 木全体のプレハブの印・プレビューの印が外れること。
    #[test]
    fn clear_links_removes_every_mark() {
        let mut world = World::new();
        let mut child = preview(actor(&mut world, "Child"), "assets://inner.actor");
        child.prefab_source = Some("assets://nested.actor".to_string());
        child.prefab_hash = Some("0123456789abcdef".to_string());
        let mut root = preview(with_children(actor(&mut world, "Root"), vec![child]), "assets://root.actor");
        root.prefab_source = Some("assets://root.actor".to_string());

        clear_links_recursive(&mut root);
        assert!(root.prefab_source.is_none() && root.prefab_hash.is_none() && root.editor_preview.is_none());
        let child = &root.children()[0];
        assert!(child.prefab_source.is_none() && child.prefab_hash.is_none() && child.editor_preview.is_none());
    }

    /// 木の中の位置（トップレベル・子・見つからない）。
    #[test]
    fn locate_in_parent_returns_parent_and_index() {
        let mut world = World::new();
        let target = actor(&mut world, "Target");
        let target_entity = target.entity;
        let parent = with_children(actor(&mut world, "Parent"), vec![actor(&mut world, "First"), target]);
        let parent_entity = parent.entity;
        let top = actor(&mut world, "Top");
        let top_entity = top.entity;
        let actors = vec![parent, top];

        assert_eq!(
            locate_in_parent(&actors, target_entity),
            Some(TreeLocation { parent: Some(parent_entity), index: 1 })
        );
        assert_eq!(locate_in_parent(&actors, top_entity), Some(TreeLocation { parent: None, index: 1 }));
        assert_eq!(locate_in_parent(&actors, world.spawn()), None);
    }

    /// 自分か祖先が条件に当たるか（地形の部分木の中への差し込みを断るため。レビュー #7）。
    #[test]
    fn ancestor_match_covers_self_descendants_and_not_siblings() {
        let mut world = World::new();
        // Group ─ Terrain ─ Chunk ─ Deep / Group ─ Other
        let deep = actor(&mut world, "Deep");
        let deep_entity = deep.entity;
        let chunk = with_children(actor(&mut world, "Chunk"), vec![deep]);
        let chunk_entity = chunk.entity;
        let terrain = with_children(actor(&mut world, "Terrain"), vec![chunk]);
        let terrain_entity = terrain.entity;
        let other = actor(&mut world, "Other");
        let other_entity = other.entity;
        let group = with_children(actor(&mut world, "Group"), vec![terrain, other]);
        let group_entity = group.entity;
        let actors = vec![group];
        let is_terrain = |a: &Actor| a.name == "Terrain";

        assert!(entity_or_ancestor_matches(&actors, terrain_entity, is_terrain), "自分が地形");
        assert!(entity_or_ancestor_matches(&actors, chunk_entity, is_terrain), "親が地形");
        assert!(entity_or_ancestor_matches(&actors, deep_entity, is_terrain), "祖先が地形");
        assert!(!entity_or_ancestor_matches(&actors, other_entity, is_terrain), "地形の兄弟は外");
        assert!(!entity_or_ancestor_matches(&actors, group_entity, is_terrain), "地形の親は外");
        let stray = world.spawn();   // 木に入れていないエンティティ（Entity::default() は最初のアクタと同じ番号になりうるので使わない）
        assert!(!entity_or_ancestor_matches(&actors, stray, is_terrain), "木に無ければ false");
    }
}
