// ============================================================
//  actor_ref_path.rs — スクリプト参照文字列（アクタ参照）のパス解決
//
//  【役割】
//  `[SerializeField]` の参照フィールドや `GameObject.FindChild` が使う
//  「アクタ参照文字列」を、シーンの Actor ツリー上の 1 アクタへ解決する。
//
//  参照文字列はシーン（.scene / .actor）へ **文字列** で保存される。
//  従来はアクタ名 1 つだけで、解決は「シーン全体を DFS して最初の一致」だった。
//  この規則は同名アクタを許すエンジンでは決定的ではあるものの、
//  **同じプレハブを複数並べると 2 個目以降の子参照が 1 個目へ吸われる**
//  という致命的な制限があった（図鑑のカードをプレハブ化できなかった理由）。
//
//  そこで参照文字列に「パス形式」を導入する。
//
//  ## 参照文字列の書式（優先順）
//  | 書式                 | 意味                                                        |
//  |----------------------|-------------------------------------------------------------|
//  | `./Child`            | **自分（スクリプトが乗るアクタ）のサブツリー**から探す      |
//  | `./A/B`              | 自分の子 `A` の子 `B`（セグメントごとに子をたどる）          |
//  | `.`                  | 自分自身                                                    |
//  | `../Sibling`         | 親のサブツリーから探す（`../../` と重ねて祖先へ登れる）      |
//  | `Root/Child/Grand`   | **シーンのルートから**の絶対パス                            |
//  | `Name`（`/` なし）   | 従来どおりの名前指定。**自分のサブツリー優先**、無ければ全体 |
//
//  ## 「自分のサブツリー優先」の precedence（互換性のための規約）
//  `/` を含まない素の名前は、後方互換のためこれまでどおり使える。
//  ただし解決順を次のように定める:
//    1. スクリプトが乗るアクタ自身とその子孫を DFS（自分 → 子 → 孫…）
//    2. 見つからなければ、シーン全体を DFS して最初の一致（従来の挙動）
//  こうすることで、既存シーン（サブツリー外を指す参照）は挙動が変わらないまま、
//  プレハブ内の子参照だけがインスタンスごとに正しく解決される。
//
//  ## フォルダの透過
//  2D フォルダノード（`Actor::is_folder`）は Hierarchy 整理用のグループであり
//  論理階層には現れない。セグメントの照合は
//  `animation::find_child_transparent` と同じ規則で行うため、
//  `./Image` は `./Items/Image`（Items がフォルダ）にも一致する。
//
//  ## 見つからない場合
//  すべて `None`（呼び出し側は「未設定」として扱う）。
// ============================================================

use crate::engine::animation::{find_child_transparent, resolve_actor_path};
use crate::engine::ecs::Entity;
use crate::engine::structs::objects::Actor;

/// パスのセグメント区切り。
pub const PATH_SEPARATOR: char = '/';

/// 「自分自身」を表すセグメント。
const SELF_SEGMENT: &str = ".";

/// 「親」を表すセグメント。
const PARENT_SEGMENT: &str = "..";

/// 解決のモード（呼び出し元の意味論の違いを表す）。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum RefScope {
    /// `[SerializeField]` の参照フィールド解決。
    /// 素の名前は「自分のサブツリー優先 → シーン全体」へフォールバックする。
    /// `Root/Child` 形式の絶対パスも受け付ける。
    Reference,
    /// `GameObject.FindChild` の解決。
    /// **必ず自分のサブツリー内**で探す（シーン全体へのフォールバックはしない）。
    /// 絶対パス形式は使えない（`A/B` は「子 A の子 B」の意味になる）。
    Subtree,
}

/// アクタ参照文字列を解決して対象アクタを返す。
///
/// # 引数
/// - `actors`: シーンのトップレベルアクタ配列（world_line 混在。従来の
///   `find_actor` と同じく world_line による絞り込みは行わない）。
/// - `owner`: 参照を持つ側のアクタ（＝スクリプトが乗るアクタ）のルート entity。
///   `None`（未束縛）のときは相対形式が使えないため、素の名前・絶対パスのみ解決できる。
/// - `path`: 参照文字列（前後の空白は無視する）。
/// - `scope`: 解決モード（[`RefScope`]）。
///
/// # 戻り値
/// 解決できたアクタ。見つからない・書式が壊れている場合は `None`。
pub fn resolve_actor_ref<'a>(
    actors: &'a [Actor],
    owner: Option<Entity>,
    path: &str,
    scope: RefScope,
) -> Option<&'a Actor> {
    let path = path.trim();
    if path.is_empty() {
        return None;
    }

    // 所有アクタの祖先チェーン（[ルート, …, 自分]）。相対形式でのみ必要なので遅延取得する。
    let chain = owner.and_then(|e| ancestor_chain(actors, e));

    // ── ① 明示的な相対形式（"." / "./…" / ".." / "../…"）────────────
    if let Some((up_count, rest)) = split_relative(path) {
        let chain = chain?;
        // up_count 段だけ祖先へ登る（登りすぎたら解決失敗）
        if up_count >= chain.len() {
            return None;
        }
        let base = chain[chain.len() - 1 - up_count];
        return resolve_in_subtree(base, rest);
    }

    // ── ② サブツリー限定モード（FindChild）────────────────────────
    //     絶対パス・全体フォールバックは持たない。
    if scope == RefScope::Subtree {
        let base = *chain?.last()?;
        return resolve_in_subtree(base, path);
    }

    // ── ③ 絶対パス（"/" を含む）───────────────────────────────────
    if path.contains(PATH_SEPARATOR) {
        return resolve_from_roots(actors, path);
    }

    // ── ④ 素の名前: 自分のサブツリー優先 → シーン全体 ─────────────
    if let Some(chain) = &chain {
        if let Some(hit) = dfs_including_self(chain[chain.len() - 1], path) {
            return Some(hit);
        }
    }
    dfs_in_roots(actors, path)
}

/// 参照文字列が「明示的な相対形式」かを判定し、`(登る段数, 残りのパス)` へ割る。
///
/// - `"."` / `"./"`            → `(0, "")`
/// - `"./Child"`               → `(0, "Child")`
/// - `"../Sibling"`            → `(1, "Sibling")`
/// - `"../../A/B"`             → `(2, "A/B")`
/// 相対形式でなければ `None`。
fn split_relative(path: &str) -> Option<(usize, &str)> {
    let mut rest = path;
    let mut up = 0usize;
    let mut matched = false;

    loop {
        // 先頭セグメントを取り出す
        let (seg, tail) = match rest.split_once(PATH_SEPARATOR) {
            Some((s, t)) => (s, t),
            None => (rest, ""),
        };
        match seg {
            SELF_SEGMENT => {
                // "." は段数を増やさない（自分基準）。先頭以外にも書けるが意味は同じ。
                matched = true;
                rest = tail;
            }
            PARENT_SEGMENT => {
                matched = true;
                up += 1;
                rest = tail;
            }
            _ => break,
        }
        if rest.is_empty() {
            break;
        }
    }

    matched.then_some((up, rest))
}

/// 基準アクタのサブツリー内でパスを解決する。
///
/// 1. まず `resolve_actor_path`（セグメントごとに子をたどる。フォルダ透過）で照合する。
/// 2. 1 で外れ、かつパスが 1 セグメントだけなら、サブツリー全体を DFS して名前一致を探す
///    （`./Image` を「どこかにある子孫 Image」として拾えるようにするため）。
///    自分自身は対象外（`./X` は「子孫の X」を意味するため。自分自身は `.` で指す）。
fn resolve_in_subtree<'a>(base: &'a Actor, rest: &str) -> Option<&'a Actor> {
    if rest.is_empty() {
        return Some(base);
    }
    if let Some(hit) = resolve_actor_path(base, rest) {
        return Some(hit);
    }
    if rest.contains(PATH_SEPARATOR) {
        return None;
    }
    dfs_in_children(base, rest)
}

/// シーンのルート配列を起点に絶対パスを解決する。
///
/// 先頭セグメントはルート配列から（フォルダ透過で）探し、残りは
/// `resolve_actor_path` と同じ規則でたどる。
fn resolve_from_roots<'a>(actors: &'a [Actor], path: &str) -> Option<&'a Actor> {
    let (first, rest) = match path.split_once(PATH_SEPARATOR) {
        Some((f, r)) => (f, r),
        None => (path, ""),
    };
    if first.is_empty() {
        return None;
    }
    let root = find_root_transparent(actors, first)?;
    if rest.is_empty() {
        return Some(root);
    }
    resolve_actor_path(root, rest)
}

/// ルート配列から名前一致のアクタを探す（フォルダ透過）。
///
/// `find_child_transparent` のルート配列版:
/// 1. ルート直下に同名があればそれ
/// 2. 無ければルートのフォルダノードを透過して同名を探す
fn find_root_transparent<'a>(actors: &'a [Actor], name: &str) -> Option<&'a Actor> {
    if let Some(a) = actors.iter().find(|a| a.name == name) {
        return Some(a);
    }
    actors
        .iter()
        .filter(|a| a.is_folder())
        .find_map(|f| find_child_transparent(f, name))
}

/// 指定アクタ**自身と子孫**を DFS（行きがけ順）して名前一致を探す。
fn dfs_including_self<'a>(actor: &'a Actor, name: &str) -> Option<&'a Actor> {
    if actor.name == name {
        return Some(actor);
    }
    dfs_in_children(actor, name)
}

/// 指定アクタの**子孫のみ**を DFS（行きがけ順）して名前一致を探す。
fn dfs_in_children<'a>(actor: &'a Actor, name: &str) -> Option<&'a Actor> {
    actor
        .children()
        .iter()
        .find_map(|c| dfs_including_self(c, name))
}

/// シーン全体（ルート配列とその子孫）を DFS して名前一致を探す（従来の挙動）。
fn dfs_in_roots<'a>(actors: &'a [Actor], name: &str) -> Option<&'a Actor> {
    actors.iter().find_map(|a| dfs_including_self(a, name))
}

/// 指定 entity をルート entity に持つアクタまでの祖先チェーンを返す。
///
/// 戻り値は `[最上位ルート, …, 対象アクタ]` の順。見つからなければ `None`。
/// 相対形式（`./` / `../`）の解決にしか使わないため、素の名前解決の
/// 実行コストには影響しない。
fn ancestor_chain<'a>(actors: &'a [Actor], entity: Entity) -> Option<Vec<&'a Actor>> {
    /// 1 本のサブツリーを DFS し、見つかったら `stack` に経路を積んで true を返す。
    fn walk<'a>(actor: &'a Actor, entity: Entity, stack: &mut Vec<&'a Actor>) -> bool {
        stack.push(actor);
        if actor.entity == entity {
            return true;
        }
        for c in actor.children() {
            if walk(c, entity, stack) {
                return true;
            }
        }
        stack.pop();
        false
    }

    let mut stack = Vec::new();
    for root in actors {
        if walk(root, entity, &mut stack) {
            return Some(stack);
        }
    }
    None
}

// ============================================================
//  ユニットテスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::ecs::World;

    /// テスト用の通常アクタ。
    fn actor(world: &mut World, name: &str) -> Actor {
        Actor::new(world.spawn(), name)
    }

    /// テスト用の 2D フォルダノード。
    fn folder(world: &mut World, name: &str) -> Actor {
        Actor::new_folder_2d(world.spawn(), name)
    }

    /// テスト用シーン:
    /// ```text
    /// Stage
    ///   Card0 [Script]      … owner0
    ///     Image
    ///     Name
    ///   Card1               … owner1
    ///     Image
    ///     Name
    /// Image                 … シーン全体 DFS だと Card0/Image より後ろ
    /// ```
    /// 戻り値は (アクタ配列, Card0 の entity, Card1 の entity)。
    fn make_scene() -> (Vec<Actor>, Entity, Entity) {
        let mut w = World::new();

        let mut card0 = actor(&mut w, "Card0");
        card0.add_child(actor(&mut w, "Image"));
        card0.add_child(actor(&mut w, "Name"));
        let e0 = card0.entity;

        let mut card1 = actor(&mut w, "Card1");
        card1.add_child(actor(&mut w, "Image"));
        card1.add_child(actor(&mut w, "Name"));
        let e1 = card1.entity;

        let mut stage = actor(&mut w, "Stage");
        stage.add_child(card0);
        stage.add_child(card1);

        let global_image = actor(&mut w, "Image");

        (vec![stage, global_image], e0, e1)
    }

    /// 解決結果を (名前, entity) で取り出すヘルパー。
    fn resolve(
        actors: &[Actor], owner: Option<Entity>, path: &str, scope: RefScope,
    ) -> Option<(String, Entity)> {
        resolve_actor_ref(actors, owner, path, scope)
            .map(|a| (a.name.clone(), a.entity))
    }

    /// `./Child` は自分のサブツリーだけを見る（インスタンスごとに別の子を返す）。
    #[test]
    fn relative_self_path_resolves_per_instance() {
        let (actors, e0, e1) = make_scene();
        let a = resolve(&actors, Some(e0), "./Image", RefScope::Reference).unwrap();
        let b = resolve(&actors, Some(e1), "./Image", RefScope::Reference).unwrap();
        assert_eq!(a.0, "Image");
        assert_eq!(b.0, "Image");
        assert_ne!(a.1, b.1, "同名の子でもインスタンスごとに別 entity になること");
    }

    /// `.` は自分自身。
    #[test]
    fn dot_resolves_to_self() {
        let (actors, e0, _) = make_scene();
        assert_eq!(resolve(&actors, Some(e0), ".", RefScope::Reference).unwrap().1, e0);
        assert_eq!(resolve(&actors, Some(e0), "./", RefScope::Reference).unwrap().1, e0);
    }

    /// `../Sibling` は親のサブツリーから探す。
    #[test]
    fn parent_relative_path_finds_sibling() {
        let (actors, e0, e1) = make_scene();
        let hit = resolve(&actors, Some(e0), "../Card1", RefScope::Reference).unwrap();
        assert_eq!(hit.1, e1);
        // 2 段上（Stage の親）は存在しないので解決失敗
        assert!(resolve(&actors, Some(e0), "../../Card1", RefScope::Reference).is_none());
    }

    /// ルートからの絶対パスで指定できる。
    #[test]
    fn absolute_path_from_roots() {
        let (actors, e0, e1) = make_scene();
        let hit = resolve(&actors, Some(e0), "Stage/Card1/Image", RefScope::Reference).unwrap();
        // Card1 の子であること（Card0 の子ではない）
        let card1_image = actors[0].children()[1].children()[0].entity;
        assert_eq!(hit.1, card1_image);
        assert_ne!(hit.1, e1);
        assert!(resolve(&actors, Some(e0), "Stage/NoSuch", RefScope::Reference).is_none());
    }

    /// 素の名前は「自分のサブツリー優先」。同名のシーン全体アクタより自分の子が勝つ。
    #[test]
    fn plain_name_prefers_own_subtree() {
        let (actors, e0, _) = make_scene();
        let own_image = actors[0].children()[0].children()[0].entity;
        let hit = resolve(&actors, Some(e0), "Image", RefScope::Reference).unwrap();
        assert_eq!(hit.1, own_image, "自分の子の Image が優先されること");

        // 所有者を持たない（未束縛）ときは従来どおりシーン全体 DFS の先頭
        let global = resolve(&actors, None, "Image", RefScope::Reference).unwrap();
        assert_eq!(global.1, own_image, "DFS 先頭は Stage 配下の Image");
    }

    /// サブツリー外の名前は、素の名前ならシーン全体へフォールバックする（互換性）。
    #[test]
    fn plain_name_falls_back_to_scene_wide_search() {
        let (actors, e0, e1) = make_scene();
        let hit = resolve(&actors, Some(e0), "Card1", RefScope::Reference).unwrap();
        assert_eq!(hit.1, e1);
    }

    /// フォルダノードは階層に存在しないものとして透過する。
    #[test]
    fn folder_nodes_are_transparent() {
        let mut w = World::new();
        let mut group = folder(&mut w, "Items");
        let target = actor(&mut w, "Image");
        let target_e = target.entity;
        group.add_child(target);

        let mut card = actor(&mut w, "Card");
        card.add_child(group);
        let owner = card.entity;

        let mut root_folder = folder(&mut w, "UIFolder");
        root_folder.add_child(card);
        let actors = vec![root_folder];

        // 相対: フォルダ名を書かなくても届く
        assert_eq!(
            resolve(&actors, Some(owner), "./Image", RefScope::Reference).unwrap().1,
            target_e);
        // 相対: フォルダ名を書いた明示パスも通る
        assert_eq!(
            resolve(&actors, Some(owner), "./Items/Image", RefScope::Reference).unwrap().1,
            target_e);
        // 絶対: ルート側のフォルダも透過する
        assert_eq!(
            resolve(&actors, None, "Card/Image", RefScope::Reference).unwrap().1,
            target_e);
    }

    /// FindChild（Subtree モード）はシーン全体へフォールバックしない。
    #[test]
    fn subtree_scope_does_not_fall_back_globally() {
        let (actors, e0, _) = make_scene();
        // 自分の子は引ける
        assert!(resolve(&actors, Some(e0), "Image", RefScope::Subtree).is_some());
        // サブツリー外（兄弟）は引けない
        assert!(resolve(&actors, Some(e0), "Card1", RefScope::Subtree).is_none());
    }

    /// 見つからない・空文字・所有者不明の相対指定はすべて None。
    #[test]
    fn not_found_cases_return_none() {
        let (actors, e0, _) = make_scene();
        assert!(resolve(&actors, Some(e0), "", RefScope::Reference).is_none());
        assert!(resolve(&actors, Some(e0), "   ", RefScope::Reference).is_none());
        assert!(resolve(&actors, Some(e0), "./NoSuch", RefScope::Reference).is_none());
        assert!(resolve(&actors, Some(e0), "NoSuch", RefScope::Reference).is_none());
        // 所有者未束縛では相対形式は解決できない
        assert!(resolve(&actors, None, "./Image", RefScope::Reference).is_none());
    }
}
