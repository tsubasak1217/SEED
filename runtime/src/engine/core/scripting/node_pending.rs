// ============================================================
//  scripting/node_pending.rs — スクリプトがこのフレームに足した・外したコンポーネントのスロットの「保留」表
//
//  【責務】
//  `GameObject.AddComponent<T>()` はスロット専用のエンティティとコンポーネントをその場で World へ入れる
//  （World は可変のポインタで公開されているので、戻り値のハンドルへ同じフレームに値を書ける）。
//  しかしスロットの目録（`Actor::slots`）は Actor ツリーにあり、スクリプトのフェーズ中は読み取り専用
//  （`host_api::with_actors`）なので、目録への登録はフレーム末尾の遅延コマンド（`ScriptSceneCommand::AttachSlot`）で行う。
//  `RemoveComponent<T>()` も同じく目録からの取り外しと後始末をフレーム末尾に行う。
//
//  その間（同じフレームの残り）に `GetComponent<T>()` / `HasComponent(...)` / 名前指定のアクセスが
//  「足したのに無い」「外したのにある」と答えないよう、この表を引く:
//    - 足したとき: (ルート, スロットのエンティティ, スロット名) を記録する
//    - 外したとき: スロットのエンティティを「取り外し予約」に記録する（足したばかりのものも含む）
//    - 引くとき: 目録のスロット（取り外し予約を除く）＋このフレームに足したスロット（取り外し予約を除く）
//    - フレーム末尾に遅延コマンドを取り出すとき（`take_scene_commands`）: 表を空にする（以後は実ツリーが正）
//
//  name_pending.rs / visible_pending.rs と同じ寿命・同じ考え方（扱う値が違うのでファイルを分ける）。
//
//  【スレッド】スクリプトのフェーズはメインスレッドだけで走るので thread_local で足りる。
//  【費用】何も保留していないフレームでは、引く側は空の判定 1 回だけ（locate の毎回の呼び出しに乗るため）。
// ============================================================

use std::cell::RefCell;

use crate::engine::ecs::Entity;

/// このフレームに足したスロット 1 つ。
#[derive(Clone, Debug, PartialEq)]
pub struct PendingSlot {
    /// 足した先のアクタのルートエンティティ。
    pub root: Entity,
    /// スロット専用のエンティティ（コンポーネントは World へ入れ済み）。
    pub entity: Entity,
    /// スロット名。
    pub name: String,
}

/// 保留の表の中身。
#[derive(Default)]
struct NodePending {
    /// このフレームに足したスロット（足した順）。
    added: Vec<PendingSlot>,
    /// このフレームに取り外しを予約したスロットのエンティティ。
    removed: Vec<Entity>,
}

thread_local! {
    /// このフレームの保留の表（遅延コマンドを取り出すときに空になる）。
    static PENDING: RefCell<NodePending> = RefCell::new(NodePending::default());
}

/// 足したスロットを記録する。
///
/// # 引数
/// - `root`: 足した先のアクタのルートエンティティ
/// - `entity`: スロット専用のエンティティ
/// - `name`: スロット名
pub fn add_slot(root: Entity, entity: Entity, name: &str) {
    PENDING.with(|p| {
        p.borrow_mut().added.push(PendingSlot { root, entity, name: name.to_string() });
    });
}

/// スロットの取り外しを予約したことを記録する（同じフレームの以後の引き当てに出さない）。
///
/// # 引数
/// - `slot_entity`: 取り外すスロット専用のエンティティ
pub fn mark_removed(slot_entity: Entity) {
    PENDING.with(|p| {
        let mut p = p.borrow_mut();
        if !p.removed.contains(&slot_entity) {
            p.removed.push(slot_entity);
        }
    });
}

/// 何か保留しているか（引く側の速い道の判定）。
pub fn has_any() -> bool {
    PENDING.with(|p| {
        let p = p.borrow();
        !p.added.is_empty() || !p.removed.is_empty()
    })
}

/// そのスロットの取り外しを予約済みか。
///
/// # 引数
/// - `slot_entity`: スロット専用のエンティティ
pub fn is_removed(slot_entity: Entity) -> bool {
    PENDING.with(|p| {
        let p = p.borrow();
        !p.removed.is_empty() && p.removed.contains(&slot_entity)
    })
}

/// そのアクタへこのフレームに足したスロット（取り外し予約を除く。足した順）を (エンティティ, 名前) で返す。
///
/// # 引数
/// - `root`: アクタのルートエンティティ
pub fn added_slots_of(root: Entity) -> Vec<(Entity, String)> {
    PENDING.with(|p| {
        let p = p.borrow();
        if p.added.is_empty() {
            return Vec::new();
        }
        p.added
            .iter()
            .filter(|s| s.root == root && !p.removed.contains(&s.entity))
            .map(|s| (s.entity, s.name.clone()))
            .collect()
    })
}

/// 保留をすべて捨てる。遅延コマンドを取り出した（＝実ツリーへ反映される）直後に呼ぶ。
pub fn clear() {
    PENDING.with(|p| {
        let mut p = p.borrow_mut();
        p.added.clear();
        p.removed.clear();
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 足す → 引く → 外す → 引けない → 空にする、の往復。
    #[test]
    fn add_remove_clear_roundtrip() {
        clear();
        let root = Entity::from_raw(10, 0);
        let other = Entity::from_raw(11, 0);
        let s1 = Entity::from_raw(20, 0);
        let s2 = Entity::from_raw(21, 0);
        assert!(!has_any(), "空なら速い道");

        add_slot(root, s1, "Sprite");
        add_slot(root, s2, "Text");
        assert!(has_any());
        assert_eq!(
            added_slots_of(root),
            vec![(s1, "Sprite".to_string()), (s2, "Text".to_string())],
            "足した順"
        );
        assert!(added_slots_of(other).is_empty(), "別のアクタには出ない");

        mark_removed(s1);
        mark_removed(s1); // 重ねても 1 件
        assert!(is_removed(s1));
        assert!(!is_removed(s2));
        assert_eq!(added_slots_of(root), vec![(s2, "Text".to_string())], "外したものは出ない");

        clear();
        assert!(!has_any());
        assert!(added_slots_of(root).is_empty());
        assert!(!is_removed(s1));
    }
}
