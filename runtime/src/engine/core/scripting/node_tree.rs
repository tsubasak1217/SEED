// ============================================================
//  scripting/node_tree.rs — スクリプトの子の列挙と兄弟の順番（動的ノード API）【純関数】
//
//  `GameObject.ChildCount` / `GetChild` / `Children` / `SiblingIndex`（読み。host_api/nodes.rs）と、
//  `SetSiblingIndex` / `SetAsFirstSibling` / `SetAsLastSibling`（書き。フレーム末尾に app/script_node_ops.rs が当てる）が
//  共有する木の計算を置く。World にも App にも触れない（Actor の木だけを見る）ので単体テストで固定する。
//
//  【フォルダの透過】（`GameObject.FindChild` の規則・キャンバスのレイアウトと同じ）
//  フォルダノード（`Actor::is_folder`）はヒエラルキーの整理用で論理の階層に現れない。
//    - 子の列挙は「論理の子」: 直接の子のうちフォルダはそれ自身を出さず、中の子を（入れ子のフォルダも）その位置へ展開する。
//      並びは木の順そのもの＝レイアウト（CanvasStack 等）が並べる順。
//    - 論理の親: 自分の上のフォルダを飛ばした最初の非フォルダの祖先（無ければシーンのルート）。
//    - 兄弟の順番（SiblingIndex）: 論理の親の論理の子の中での位置。フォルダ自身は兄弟の順番を持たない（-1・並べ替えは断る）。
//    - ルートの兄弟: 同じ world_line のルート（フォルダは透過）。
//  フォルダを使わない木（スクリプトで組み立てる木）では、論理の子＝直接の子・`GetChild(i).SiblingIndex == i`。
//
//  【並べ替え】`move_to_logical_index`
//  対象を木から取り出し、残りの論理の兄弟の i 番目（取り出した後の数え方）の**直前**へ、その兄弟が実際に居る入れ物
//  （論理の親か、その下のフォルダ）へ差し込む。i が兄弟の数以上（または末尾の指定）なら論理の親の直接の子の末尾へ足す。
//  論理の親は変わらない（フォルダの出入りはありうる。フォルダは単位変換なので 2D の位置も見た目も変わらない）。
// ============================================================

use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;

/// 論理の子（フォルダを透過して平らにした子。木の順）を返す。
///
/// # 引数
/// * `children` - 親の直接の子（ルートの一覧を渡すときは `logical_roots` を使う）
pub fn logical_children(children: &[Actor]) -> Vec<&Actor> {
    let mut out = Vec::new();
    push_logical(children, &mut out);
    out
}

/// 同じ world_line のルートの論理の子（フォルダを透過。木の順）を返す。
///
/// # 引数
/// * `actors`     - シーンのトップレベルのアクタ（world_line 混在）
/// * `world_line` - 世界線
pub fn logical_roots(actors: &[Actor], world_line: u32) -> Vec<&Actor> {
    let mut out = Vec::new();
    for actor in actors.iter().filter(|a| a.world_line == world_line) {
        push_one_logical(actor, &mut out);
    }
    out
}

/// 子の並びを論理の子として out へ足す（フォルダは中身を展開する）。
fn push_logical<'a>(children: &'a [Actor], out: &mut Vec<&'a Actor>) {
    for child in children {
        push_one_logical(child, out);
    }
}

/// 1 つのノードを論理の子として足す（フォルダなら中身を展開する）。
fn push_one_logical<'a>(actor: &'a Actor, out: &mut Vec<&'a Actor>) {
    if actor.is_folder() {
        push_logical(actor.children(), out);
    } else {
        out.push(actor);
    }
}

/// ノードの論理の場所（論理の親と、その論理の子の中の順番）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct LogicalPlace {
    /// 論理の親のルートエンティティ（シーンのルートなら None）。
    pub parent: Option<Entity>,
    /// ノードの世界線（ルートの兄弟を数えるときに使う）。
    pub world_line: u32,
    /// 論理の兄弟の中の順番（0 始まり）。
    pub index: usize,
    /// 論理の兄弟の数（自分を含む）。
    pub count: usize,
}

/// ノードの論理の場所を求める（見つからない・フォルダなら None）。
///
/// # 引数
/// * `actors` - シーンのトップレベルのアクタ
/// * `entity` - ノードのルートエンティティ
pub fn logical_place(actors: &[Actor], entity: Entity) -> Option<LogicalPlace> {
    let mut chain: Vec<&Actor> = Vec::new();
    let target = find_with_ancestors(actors, entity, &mut chain)?;
    if target.is_folder() {
        return None;
    }
    // 論理の親: 自分の上のフォルダを飛ばした最初の非フォルダの祖先
    let parent = chain.iter().rev().find(|a| !a.is_folder()).copied();
    let siblings = match parent {
        Some(p) => logical_children(p.children()),
        None => logical_roots(actors, target.world_line),
    };
    let index = siblings.iter().position(|a| a.entity == entity)?;
    Some(LogicalPlace {
        parent: parent.map(|p| p.entity),
        world_line: target.world_line,
        index,
        count: siblings.len(),
    })
}

/// 並べ替えを断った理由。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SiblingMoveError {
    /// 対象がシーンに無い（破棄済み・このフレームに作ったばかりで未構築など）。
    NotFound,
    /// 対象がフォルダ（論理の階層に現れないので兄弟の順番を持たない）。
    Folder,
}

impl SiblingMoveError {
    /// ログに出す理由。
    pub fn message(self) -> &'static str {
        match self {
            SiblingMoveError::NotFound => "対象アクターがシーンに存在しません",
            SiblingMoveError::Folder => "フォルダは兄弟の順番を持ちません（中の子を並べ替えてください）",
        }
    }
}

/// ノードを論理の兄弟の index 番目へ移す（None・兄弟の数以上なら末尾）。
///
/// # 戻り値
/// 移した後の論理の順番。断ったら理由（木は変えない）。
pub fn move_to_logical_index(
    actors: &mut Vec<Actor>,
    entity: Entity,
    index: Option<usize>,
) -> Result<usize, SiblingMoveError> {
    // 1. 場所を調べる（取り出す前に断る理由を確かめ、木を変えない）
    let place = {
        let mut chain: Vec<&Actor> = Vec::new();
        let target = find_with_ancestors(actors, entity, &mut chain).ok_or(SiblingMoveError::NotFound)?;
        if target.is_folder() {
            return Err(SiblingMoveError::Folder);
        }
        logical_place(actors, entity).ok_or(SiblingMoveError::NotFound)?
    };

    // 2. 取り出す
    let node = extract(actors, entity).ok_or(SiblingMoveError::NotFound)?;

    // 3. 論理の親の直接の子の並び（ルートならトップレベルの一覧）
    let container = container_mut(actors, place.parent);

    // 4. 取り出した後の論理の兄弟から、差し込む位置（その兄弟の直前）を決める
    let siblings: Vec<Entity> = match place.parent {
        Some(_) => logical_children(container).iter().map(|a| a.entity).collect(),
        None => logical_roots(container, place.world_line).iter().map(|a| a.entity).collect(),
    };
    let wanted = index.unwrap_or(siblings.len()).min(siblings.len());
    if wanted == siblings.len() {
        // 末尾: 論理の親の直接の子の末尾（フォルダの中の兄弟より後ろ＝論理でも末尾）
        container.push(node);
        return Ok(wanted);
    }
    let before = siblings[wanted];
    match insert_before(container, before, node) {
        Ok(()) => Ok(wanted),
        // 直前に数えた兄弟なので通常は来ない（保険として末尾へ）
        Err(node) => {
            container.push(node);
            Ok(siblings.len())
        }
    }
}

/// ノードを探し、見つかったら祖先の並び（ルート側から直接の親まで）を chain に残して返す。
fn find_with_ancestors<'a>(actors: &'a [Actor], entity: Entity, chain: &mut Vec<&'a Actor>) -> Option<&'a Actor> {
    for actor in actors {
        if actor.entity == entity {
            return Some(actor);
        }
        chain.push(actor);
        if let Some(found) = find_with_ancestors(actor.children(), entity, chain) {
            return Some(found);
        }
        chain.pop();
    }
    None
}

/// 論理の親の直接の子の並びを可変で返す（親が None か見つからなければトップレベルの一覧）。
///
/// 「見つかれば子の並び、無ければ一覧」を 1 回の可変の探索で書くと借用が通らないので、先に不変で在ることを確かめる。
fn container_mut(actors: &mut Vec<Actor>, parent: Option<Entity>) -> &mut Vec<Actor> {
    match parent {
        Some(p) if find_with_ancestors(actors, p, &mut Vec::new()).is_some() => {
            // 直前に在ることを確かめたので必ず見つかる
            find_mut(actors, p).map(Actor::children_mut).expect("直前に在ることを確かめた親")
        }
        // 取り出す前に居た親なので通常は来ない（保険としてルートへ）
        _ => actors,
    }
}

/// ノードを可変で探す。
fn find_mut(actors: &mut [Actor], entity: Entity) -> Option<&mut Actor> {
    for actor in actors.iter_mut() {
        if actor.entity == entity {
            return Some(actor);
        }
        if let Some(found) = find_mut(actor.children_mut(), entity) {
            return Some(found);
        }
    }
    None
}

/// ノードを木から取り出す（部分木ごと）。
fn extract(actors: &mut Vec<Actor>, entity: Entity) -> Option<Actor> {
    if let Some(i) = actors.iter().position(|a| a.entity == entity) {
        return Some(actors.remove(i));
    }
    for actor in actors.iter_mut() {
        if let Some(found) = extract(actor.children_mut(), entity) {
            return Some(found);
        }
    }
    None
}

/// `before` の直前へ node を差し込む（`before` が中のフォルダに居ればそのフォルダの中へ）。見つからなければ node を返す。
fn insert_before(children: &mut Vec<Actor>, before: Entity, node: Actor) -> Result<(), Actor> {
    if let Some(i) = children.iter().position(|a| a.entity == before) {
        children.insert(i, node);
        return Ok(());
    }
    let mut node = node;
    for child in children.iter_mut().filter(|c| c.is_folder()) {
        match insert_before(child.children_mut(), before, node) {
            Ok(()) => return Ok(()),
            Err(back) => node = back,
        }
    }
    Err(node)
}

// ============================================================
//  単体テスト（World 不要。エンティティは番号で作る）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 番号 n の 2D ノードを作る。
    fn node(n: u32, name: &str) -> Actor {
        Actor::new_2d(Entity::from_raw(n, 0), name)
    }

    /// 番号 n の 2D フォルダを作る。
    fn folder(n: u32, name: &str, children: Vec<Actor>) -> Actor {
        let mut f = Actor::new_folder_2d(Entity::from_raw(n, 0), name);
        for c in children {
            f.add_child(c);
        }
        f
    }

    /// 子を持つノードを作る。
    fn with_children(mut a: Actor, children: Vec<Actor>) -> Actor {
        for c in children {
            a.add_child(c);
        }
        a
    }

    /// 名前の並びにする。
    fn names(list: &[&Actor]) -> Vec<String> {
        list.iter().map(|a| a.name.clone()).collect()
    }

    /// 直接の子の名前の並び。
    fn direct_names(a: &Actor) -> Vec<String> {
        a.children().iter().map(|c| c.name.clone()).collect()
    }

    /// P [A, F(folder)[B, G(folder)[C]], D] の木（番号: P=1 A=2 F=3 B=4 G=5 C=6 D=7）。
    fn sample() -> Vec<Actor> {
        vec![with_children(
            node(1, "P"),
            vec![
                node(2, "A"),
                folder(3, "F", vec![node(4, "B"), folder(5, "G", vec![node(6, "C")])]),
                node(7, "D"),
            ],
        )]
    }

    /// エンティティの番号。
    fn e(n: u32) -> Entity {
        Entity::from_raw(n, 0)
    }

    #[test]
    fn logical_children_flatten_folders_in_tree_order() {
        let tree = sample();
        assert_eq!(names(&logical_children(tree[0].children())), vec!["A", "B", "C", "D"], "フォルダは中身を展開");
    }

    #[test]
    fn logical_place_skips_folders_for_parent_and_index() {
        let tree = sample();
        let c = logical_place(&tree, e(6)).unwrap();
        assert_eq!(c.parent, Some(e(1)), "入れ子のフォルダを飛ばした親");
        assert_eq!((c.index, c.count), (2, 4));
        let a = logical_place(&tree, e(2)).unwrap();
        assert_eq!((a.index, a.count), (0, 4));
        assert_eq!(logical_place(&tree, e(3)), None, "フォルダ自身は順番を持たない");
        assert_eq!(logical_place(&tree, e(99)), None, "無いノード");
    }

    #[test]
    fn root_siblings_are_counted_per_world_line() {
        let mut other = node(20, "Other");
        other.world_line = 1;
        let tree = vec![node(10, "R0"), other, node(11, "R1")];
        let r1 = logical_place(&tree, e(11)).unwrap();
        assert_eq!(r1.parent, None);
        assert_eq!((r1.index, r1.count), (1, 2), "世界線 1 のルートは数えない");
    }

    #[test]
    fn move_without_folders_reorders_direct_children() {
        let mut tree = vec![with_children(node(1, "P"), vec![node(2, "A"), node(3, "B"), node(4, "C")])];
        assert_eq!(move_to_logical_index(&mut tree, e(4), Some(0)), Ok(0));
        assert_eq!(direct_names(&tree[0]), vec!["C", "A", "B"], "先頭へ");
        assert_eq!(move_to_logical_index(&mut tree, e(4), None), Ok(2));
        assert_eq!(direct_names(&tree[0]), vec!["A", "B", "C"], "末尾へ");
        assert_eq!(move_to_logical_index(&mut tree, e(2), Some(1)), Ok(1));
        assert_eq!(direct_names(&tree[0]), vec!["B", "A", "C"], "途中へ（取り出した後の数え方で 1 番目の直前）");
        assert_eq!(move_to_logical_index(&mut tree, e(2), Some(100)), Ok(2), "大きすぎる番号は末尾");
        assert_eq!(direct_names(&tree[0]), vec!["B", "C", "A"]);
    }

    #[test]
    fn move_across_folders_keeps_logical_parent() {
        let mut tree = sample();
        // D を論理の 1 番目（B の直前＝フォルダ F の中）へ
        assert_eq!(move_to_logical_index(&mut tree, e(7), Some(1)), Ok(1));
        assert_eq!(names(&logical_children(tree[0].children())), vec!["A", "D", "B", "C"]);
        let f = &tree[0].children()[1];
        assert_eq!(direct_names(f), vec!["D", "B", "G"], "B が居るフォルダの中へ入る");
        // C（入れ子のフォルダの中）を末尾へ → 論理の親 P の直接の子の末尾
        assert_eq!(move_to_logical_index(&mut tree, e(6), None), Ok(3));
        assert_eq!(names(&logical_children(tree[0].children())), vec!["A", "D", "B", "C"]);
        assert_eq!(direct_names(&tree[0]), vec!["A", "F", "C"], "フォルダから出て末尾");
        // A を先頭のまま（0 番目）→ 変わらない
        assert_eq!(move_to_logical_index(&mut tree, e(2), Some(0)), Ok(0));
        assert_eq!(names(&logical_children(tree[0].children())), vec!["A", "D", "B", "C"]);
    }

    #[test]
    fn move_root_keeps_other_world_lines_in_place() {
        let mut other = node(20, "Other");
        other.world_line = 1;
        let mut tree = vec![node(10, "R0"), other, node(11, "R1"), node(12, "R2")];
        assert_eq!(move_to_logical_index(&mut tree, e(12), Some(0)), Ok(0));
        let order: Vec<&str> = tree.iter().map(|a| a.name.as_str()).collect();
        assert_eq!(order, vec!["R2", "R0", "Other", "R1"]);
        assert_eq!(names(&logical_roots(&tree, 0)), vec!["R2", "R0", "R1"]);
    }

    #[test]
    fn move_refuses_folders_and_missing_without_touching_tree() {
        let mut tree = sample();
        assert_eq!(move_to_logical_index(&mut tree, e(3), Some(0)), Err(SiblingMoveError::Folder));
        assert_eq!(move_to_logical_index(&mut tree, e(99), Some(0)), Err(SiblingMoveError::NotFound));
        assert_eq!(names(&logical_children(tree[0].children())), vec!["A", "B", "C", "D"], "木は変わらない");
    }
}
