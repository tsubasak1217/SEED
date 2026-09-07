// ============================================================
//  actor/visibility.rs — アクターの「実効表示（effective visible）」計算
//
//  【責務】
//  `Actor::visible`（自分自身の表示フラグ）から「実際に描画されるか」を求める処理だけを
//  集約する。単一責任のため、ここには描画そのものやシリアライズは一切書かない。
//
//  【なぜ独立モジュールにするか】
//  実効表示は「祖先すべてが visible かつ自分も visible」という単純な規則だが、
//  描画収集は canvas_collect / light_ops / particle_system / actor_utils など
//  多数の場所に分散している。規則をここ 1 か所に置き、各収集処理は
//  `effective_visible` を呼ぶだけにすることで、規則が食い違うのを防ぐ。
//
//  【active との違い】
//  `active` は「更新も描画も止める」（Unity の GameObject.SetActive 相当）。
//  `visible` は「描画だけ止める」（Unity の Renderer.enabled / Godot の visible 相当）で、
//  スクリプト・アニメーション・物理は動き続ける。したがって両者は独立に AND される
//  （描画されるのは active かつ visible のときだけ）。
// ============================================================

use super::Actor;
use crate::engine::ecs::Entity;

/// 実効表示フラグを 1 段ぶん伝播させる。
///
/// DFS 走査中の各ノードで `let visible = effective_visible(parent_visible, actor);` と書き、
/// 子へは `visible` をそのまま渡す。ルート呼び出しの `parent_visible` は常に true。
///
/// # 引数
/// - `parent_visible`: 親までの実効表示（ルートでは true）。
/// - `actor`: 判定対象のアクター。
///
/// # 戻り値
/// このアクター（とその配下の既定値）が描画対象かどうか。
#[inline]
pub fn effective_visible(parent_visible: bool, actor: &Actor) -> bool {
    parent_visible && actor.visible
}

/// 実効表示フラグを 1 段ぶん伝播させる（アクティブ状態と合成した「描画される」判定）。
///
/// 描画収集の多くは既に `parent_active && actor.active` を計算しているため、
/// 「描画してよいか」は結局 `active かつ visible` になる。両方を一度に扱えるように
/// 合成版を用意しておく（呼び出し側で AND を書き忘れる事故を防ぐ）。
///
/// # 引数
/// - `parent_active` / `parent_visible`: 親までの実効アクティブ／実効表示。
/// - `actor`: 判定対象のアクター。
///
/// # 戻り値
/// `(実効アクティブ, 実効表示, 描画対象か)` の 3 つ組。
#[inline]
pub fn effective_active_visible(
    parent_active:  bool,
    parent_visible: bool,
    actor:          &Actor,
) -> (bool, bool, bool) {
    let active  = parent_active && actor.active;
    let visible = parent_visible && actor.visible;
    (active, visible, active && visible)
}

/// ルートエンティティで指定したアクターの実効表示を、アクターツリー全体から求める。
///
/// DFS 走査の文脈を持たない呼び出し元（スクリプト FFI の `GameObject.Visible` 取得や、
/// `SEED.Draw` の座標空間アクターの判定）向け。祖先を辿るためツリーを 1 度走査する
/// （O(アクタ総数)）。毎フレーム大量に呼ぶ経路では使わないこと。
///
/// # 引数
/// - `actors`: 探索対象のルートアクター列（世界線フィルタは呼び出し側の責務）。
/// - `entity`: 探したいアクターの **ルートエンティティ**。
///
/// # 戻り値
/// 見つかったら実効表示、見つからなければ `None`。
pub fn effective_visible_of_entity(actors: &[Actor], entity: Entity) -> Option<bool> {
    /// 再帰本体。`parent_visible` は親までの実効表示。
    fn walk(actors: &[Actor], entity: Entity, parent_visible: bool) -> Option<bool> {
        for a in actors {
            let visible = effective_visible(parent_visible, a);
            if a.entity == entity {
                return Some(visible);
            }
            if let Some(found) = walk(a.children(), entity, visible) {
                return Some(found);
            }
        }
        None
    }
    walk(actors, entity, true)
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::ecs::World;

    /// 親が非表示なら子も非表示になる（祖先伝播）。
    #[test]
    fn visibility_propagates_to_descendants() {
        let mut world = World::new();
        let mut root  = Actor::new(world.spawn(), "root");
        let mut mid   = Actor::new(world.spawn(), "mid");
        let leaf      = Actor::new(world.spawn(), "leaf");
        mid.add_child(leaf);
        root.add_child(mid);

        // 既定は全部表示。
        let leaf_entity = root.children()[0].children()[0].entity;
        assert_eq!(effective_visible_of_entity(std::slice::from_ref(&root), leaf_entity), Some(true));

        // 中間ノードを非表示にすると、その子孫も非表示になる。
        root.children_mut()[0].visible = false;
        assert_eq!(effective_visible_of_entity(std::slice::from_ref(&root), leaf_entity), Some(false));

        // 中間を戻してルートを非表示にしても、やはり子孫は非表示。
        root.children_mut()[0].visible = true;
        root.visible = false;
        assert_eq!(effective_visible_of_entity(std::slice::from_ref(&root), leaf_entity), Some(false));
    }

    /// 自分だけ非表示にしても親・兄弟には影響しない。
    #[test]
    fn hiding_child_does_not_affect_parent() {
        let mut world = World::new();
        let mut root  = Actor::new(world.spawn(), "root");
        let child     = Actor::new(world.spawn(), "child");
        root.add_child(child);

        let root_entity  = root.entity;
        let child_entity = root.children()[0].entity;
        root.children_mut()[0].visible = false;

        let actors = std::slice::from_ref(&root);
        assert_eq!(effective_visible_of_entity(actors, root_entity),  Some(true));
        assert_eq!(effective_visible_of_entity(actors, child_entity), Some(false));
    }

    /// active と visible は独立に AND される（描画されるのは両方 true のときだけ）。
    #[test]
    fn active_and_visible_are_independent() {
        let mut world = World::new();
        let mut actor = Actor::new(world.spawn(), "a");

        actor.active = true;  actor.visible = true;
        assert_eq!(effective_active_visible(true, true, &actor), (true, true, true));

        actor.active = false; actor.visible = true;
        assert_eq!(effective_active_visible(true, true, &actor), (false, true, false));

        actor.active = true;  actor.visible = false;
        assert_eq!(effective_active_visible(true, true, &actor), (true, false, false));

        // 祖先が非アクティブ／非表示なら自分の値によらず false。
        actor.active = true;  actor.visible = true;
        assert_eq!(effective_active_visible(false, true,  &actor), (false, true,  false));
        assert_eq!(effective_active_visible(true,  false, &actor), (true,  false, false));
    }

    /// 存在しないエンティティを問い合わせたら None。
    #[test]
    fn unknown_entity_returns_none() {
        let mut world = World::new();
        let root = Actor::new(world.spawn(), "root");
        let orphan = world.spawn();
        assert_eq!(effective_visible_of_entity(std::slice::from_ref(&root), orphan), None);
    }
}
