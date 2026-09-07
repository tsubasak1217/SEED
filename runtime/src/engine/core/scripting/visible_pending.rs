// ============================================================
//  scripting/visible_pending.rs — スクリプトからの表示切替の「保留値」テーブル
//
//  【責務】
//  `GameObject.Visible` の set は、Actor ツリーが読み取り専用ポインタでしか
//  公開されていない（`host_api::with_actors`）ため、その場では書き換えられない。
//  そこで `ScriptSceneCommand::SetVisible` としてフレーム末尾へ遅延適用する。
//
//  ただし素朴に遅延させるだけだと「set した直後に get すると古い値が返る」という
//  直感に反する挙動になる。この保留テーブルはその 1 点だけを解決する:
//    - set 時: (entity -> 値) をここへ記録し、同時に遅延コマンドも積む
//    - get 時: まずここを引き、無ければ Actor ツリーの実値を読む
//    - フレーム末尾に遅延コマンドを取り出すとき: ここもまとめて空にする
//
//  【なぜ独立ファイルか】
//  host_api.rs は既に巨大で、責務も「FFI の入り口」に寄っている。
//  「保留値の一時保存」は独立した小さな責務なのでファイルを分ける。
//
//  【スレッド】
//  スクリプトフェーズはメインスレッドでのみ実行されるため thread_local で足りる
//  （host_api の SCENE_COMMANDS などと同じ前提）。
// ============================================================

use std::cell::RefCell;

use crate::engine::ecs::Entity;

thread_local! {
    /// このフレーム中にスクリプトが設定した表示フラグ（ルートエンティティ -> visible）。
    /// 遅延コマンドを取り出すタイミングで空になる。
    static PENDING_VISIBLE: RefCell<Vec<(Entity, bool)>> = const { RefCell::new(Vec::new()) };
}

/// 保留値を記録する（同一エンティティへの再設定は最後の値で上書きする）。
///
/// # 引数
/// - `entity`: 対象アクターのルートエンティティ。
/// - `visible`: 設定したい表示フラグ。
pub fn set(entity: Entity, visible: bool) {
    PENDING_VISIBLE.with(|c| {
        let mut v = c.borrow_mut();
        match v.iter_mut().find(|(e, _)| *e == entity) {
            Some(slot) => slot.1 = visible,
            None       => v.push((entity, visible)),
        }
    });
}

/// 保留値を引く。まだ設定されていなければ `None`（呼び出し側は実値を読む）。
///
/// # 引数
/// - `entity`: 対象アクターのルートエンティティ。
pub fn get(entity: Entity) -> Option<bool> {
    PENDING_VISIBLE.with(|c| c.borrow().iter().find(|(e, _)| *e == entity).map(|(_, v)| *v))
}

/// 保留値をすべて捨てる。遅延コマンドを取り出した（＝実ツリーへ反映される）直後に呼ぶ。
pub fn clear() {
    PENDING_VISIBLE.with(|c| c.borrow_mut().clear());
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

        set(e1, false);
        assert_eq!(get(e1), Some(false));
        assert_eq!(get(e2), None, "他のエンティティには影響しない");

        // 同一エンティティへの再設定は最後の値で上書き（重複エントリを作らない）。
        set(e1, true);
        assert_eq!(get(e1), Some(true));

        clear();
        assert_eq!(get(e1), None);
    }
}
