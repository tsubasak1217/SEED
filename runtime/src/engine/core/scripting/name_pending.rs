// ============================================================
//  scripting/name_pending.rs — スクリプトからのアクタ名変更の「保留値」テーブル
//
//  【責務】
//  `GameObject.Name` の set は、Actor ツリーが読み取り専用ポインタでしか
//  公開されていない（`host_api::with_actors`）ため、その場では書き換えられない。
//  そこで `ScriptSceneCommand::SetName` としてフレーム末尾へ遅延適用する。
//
//  visible_pending.rs と完全に同じ役割・同じ寿命で、扱う値が bool ではなく
//  String という点だけが異なる。素朴に遅延させるだけだと「名前を付けた直後に
//  Name を読むと古い名前が返る」ことになり、
//    var go = GameObject.Instantiate(path); go.Name = "BeatIcon00";
//  の直後に `go.Name` で確認できないという直感に反する挙動になる。
//  この保留テーブルはその 1 点だけを解決する:
//    - set 時: (entity -> 名前) をここへ記録し、同時に遅延コマンドも積む
//    - get 時: まずここを引き、無ければ Actor ツリーの実値を読む
//    - フレーム末尾に遅延コマンドを取り出すとき: ここもまとめて空にする
//
//  【なぜ visible_pending と統合しないか】
//  値の型が違うため 1 つのテーブルにするとタグ付き列挙が要り、
//  「保留値を 1 種類だけ持つ」という各ファイルの単純さが失われる。
//  行数も小さく、責務も独立しているためファイルを分けている。
//
//  【スレッド】
//  スクリプトフェーズはメインスレッドでのみ実行されるため thread_local で足りる
//  （host_api の SCENE_COMMANDS などと同じ前提）。
// ============================================================

use std::cell::RefCell;

use crate::engine::ecs::Entity;

thread_local! {
    /// このフレーム中にスクリプトが設定したアクタ名（ルートエンティティ -> 名前）。
    /// 遅延コマンドを取り出すタイミングで空になる。
    static PENDING_NAME: RefCell<Vec<(Entity, String)>> = const { RefCell::new(Vec::new()) };
}

/// 保留値を記録する（同一エンティティへの再設定は最後の値で上書きする）。
///
/// # 引数
/// - `entity`: 対象アクターのルートエンティティ。
/// - `name`: 設定したいアクタ名。
pub fn set(entity: Entity, name: &str) {
    PENDING_NAME.with(|c| {
        let mut v = c.borrow_mut();
        match v.iter_mut().find(|(e, _)| *e == entity) {
            Some(slot) => slot.1 = name.to_string(),
            None       => v.push((entity, name.to_string())),
        }
    });
}

/// 保留値を引く。まだ設定されていなければ `None`（呼び出し側は実値を読む）。
///
/// # 引数
/// - `entity`: 対象アクターのルートエンティティ。
pub fn get(entity: Entity) -> Option<String> {
    PENDING_NAME.with(|c| c.borrow().iter().find(|(e, _)| *e == entity).map(|(_, v)| v.clone()))
}

/// 保留値をすべて捨てる。遅延コマンドを取り出した（＝実ツリーへ反映される）直後に呼ぶ。
pub fn clear() {
    PENDING_NAME.with(|c| c.borrow_mut().clear());
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 記録 → 取得 → クリアの基本往復。同一エンティティは上書きされる。
    #[test]
    fn set_get_clear_roundtrip() {
        clear();
        let e1 = Entity::from_raw(1, 0);
        let e2 = Entity::from_raw(2, 0);

        assert_eq!(get(e1), None, "未設定は None");

        set(e1, "BeatIcon00");
        assert_eq!(get(e1).as_deref(), Some("BeatIcon00"));
        assert_eq!(get(e2), None, "他のエンティティには影響しない");

        // 同一エンティティへの再設定は最後の値で上書き（重複エントリを作らない）。
        set(e1, "BeatIcon01");
        assert_eq!(get(e1).as_deref(), Some("BeatIcon01"));

        clear();
        assert_eq!(get(e1), None);
    }
}
