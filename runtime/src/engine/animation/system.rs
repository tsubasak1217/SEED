// ============================================================
//  system.rs — アニメーション評価システムのコアロジック（純関数）
//
//  【役割】
//  AnimationSystem（App 統合は app/animation_ops.rs）が使う、状態を持たない
//  再利用可能なヘルパー群:
//    - normalize_time: loop_mode に応じた再生時刻の正規化と再生継続判定
//    - resolve_actor_path: Animator 保持アクタからの相対パスで子アクタを解決
//
//  実際の毎フレーム収集・World 書き込みは app/animation_ops.rs が担う
//  （アクター木は scene.actors、コンポーネント実体は World にあり、
//   その両者を跨ぐ処理は App 側でしか行えないため）。
// ============================================================

use super::clip::LoopMode;
use crate::engine::structs::objects::Actor;

/// 再生時刻を loop_mode に応じて [0, duration] 範囲のサンプル時刻へ正規化する。
///
/// 戻り値: (サンプルに使う時刻, 再生を継続すべきか)。
/// - Once: duration を超えたら末尾で停止（sample=duration, playing=false）。
/// - Loop: duration で剰余（常に playing=true）。
/// - PingPong: 2*duration 周期で往復（常に playing=true）。
///
/// duration が 0 以下のクリップは常に (0.0, false)。
pub fn normalize_time(loop_mode: LoopMode, time: f32, duration: f32) -> (f32, bool) {
    if duration <= 0.0 {
        return (0.0, false);
    }
    match loop_mode {
        LoopMode::Once => {
            if time >= duration {
                (duration, false)
            } else if time < 0.0 {
                (0.0, true)
            } else {
                (time, true)
            }
        }
        LoopMode::Loop => {
            // rem_euclid で負時刻も正しく巻き戻す
            (time.rem_euclid(duration), true)
        }
        LoopMode::PingPong => {
            let cycle = duration * 2.0;
            let t = time.rem_euclid(cycle);
            // 前半は順再生、後半は逆再生
            let sample = if t <= duration { t } else { cycle - t };
            (sample, true)
        }
    }
}

/// 1 セグメント（子アクタ名）を、フォルダノードを透過して解決する。
///
/// フォルダノード（`Actor::is_folder`）は Hierarchy 整理用のグループであり、
/// 論理的な親子階層には現れない（キャンバスレイアウトでも透明に扱う）。
/// そのためアクタパスの照合も次の優先順で行う:
///
/// 1. **直接の子**に同名があればそれを採用する。
///    フォルダ名そのものを明示指定したパス（`"Folder/Child"`）もこの規則で通る。
/// 2. 見つからなければ、直接の子のうち**フォルダだけ**を再帰的に潜り、
///    同じ論理階層にあるノードとして同名を探す（ネストしたフォルダも辿る）。
///
/// 1 が 2 に優先するため、フォルダ配下と直下に同名が並んでも直下が勝つ
/// （＝ 既存シーンの解決結果は変わらない）。
fn find_child_transparent<'a>(parent: &'a Actor, name: &str) -> Option<&'a Actor> {
    // 1) 直接の子（フォルダ名の明示指定もここで一致する）
    if let Some(c) = parent.children().iter().find(|c| c.name == name) {
        return Some(c);
    }
    // 2) フォルダを透過して同一論理階層を探す（ネストしたフォルダも辿る）
    parent
        .children()
        .iter()
        .filter(|c| c.is_folder())
        .find_map(|f| find_child_transparent(f, name))
}

/// Animator 保持アクタからの相対パス（"/" 区切りの子アクタ名）で対象アクタを解決する。
///
/// 空文字は自分自身。各セグメントは `find_child_transparent` で照合するため、
/// **フォルダノードは階層に存在しないもの**として扱われる
/// （`"HitBandBlackTop"` は `HitBannerItems/HitBandBlackTop` にも一致する）。
/// フォルダ名を含む明示パスもそのまま通る。見つからなければ None。
pub fn resolve_actor_path<'a>(root: &'a Actor, path: &str) -> Option<&'a Actor> {
    if path.is_empty() {
        return Some(root);
    }
    let mut cur = root;
    for seg in path.split('/') {
        if seg.is_empty() {
            continue;
        }
        cur = find_child_transparent(cur, seg)?;
    }
    Some(cur)
}

// ============================================================
//  テスト — フォルダ透過のアクタパス解決
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::ecs::World;

    /// テスト用に「通常アクタ」を作る（World から entity を採番する）。
    fn actor(world: &mut World, name: &str) -> Actor {
        Actor::new(world.spawn(), name)
    }

    /// テスト用に「2D フォルダノード」を作る（is_folder=true）。
    fn folder(world: &mut World, name: &str) -> Actor {
        Actor::new_folder_2d(world.spawn(), name)
    }

    /// 直接の子はこれまでどおり 1 セグメントで解決できる。
    #[test]
    fn resolves_direct_child() {
        let mut w = World::new();
        let child = actor(&mut w, "Sprite");
        let mut root = actor(&mut w, "Root");
        root.add_child(child);

        assert_eq!(resolve_actor_path(&root, "Sprite").map(|a| a.name.as_str()), Some("Sprite"));
        assert_eq!(resolve_actor_path(&root, "").map(|a| a.name.as_str()), Some("Root"));
        assert!(resolve_actor_path(&root, "Missing").is_none());
    }

    /// フォルダ配下の子は、フォルダ名を書かないパスでも解決できる（本修正の主目的）。
    #[test]
    fn resolves_child_under_folder_without_folder_name() {
        let mut w = World::new();
        let target = actor(&mut w, "HitBandBlackTop");
        let mut f = folder(&mut w, "HitBannerItems");
        f.add_child(target);
        let mut root = actor(&mut w, "FishingUI");
        root.add_child(f);

        assert_eq!(
            resolve_actor_path(&root, "HitBandBlackTop").map(|a| a.name.as_str()),
            Some("HitBandBlackTop")
        );
    }

    /// ネストしたフォルダも透過して解決できる。
    #[test]
    fn resolves_child_under_nested_folders() {
        let mut w = World::new();
        let target = actor(&mut w, "Deep");
        let mut inner = folder(&mut w, "Inner");
        inner.add_child(target);
        let mut outer = folder(&mut w, "Outer");
        outer.add_child(inner);
        let mut root = actor(&mut w, "Root");
        root.add_child(outer);

        assert_eq!(resolve_actor_path(&root, "Deep").map(|a| a.name.as_str()), Some("Deep"));
    }

    /// フォルダ名を明示したパスも従来どおり通る（後方互換）。
    #[test]
    fn resolves_explicit_folder_path() {
        let mut w = World::new();
        let target = actor(&mut w, "Seg");
        let mut f = folder(&mut w, "Segments");
        f.add_child(target);
        let mut root = actor(&mut w, "Root");
        root.add_child(f);

        assert_eq!(resolve_actor_path(&root, "Segments/Seg").map(|a| a.name.as_str()), Some("Seg"));
        // フォルダ自身も 1 ノードとして解決できる
        assert!(resolve_actor_path(&root, "Segments").is_some_and(|a| a.is_folder()));
    }

    /// 直下と フォルダ配下に同名がある場合は直下が勝つ（既存シーンの解決結果を変えない）。
    #[test]
    fn direct_child_wins_over_folder_child() {
        let mut w = World::new();
        let inside = actor(&mut w, "Dup");
        let mut f = folder(&mut w, "Group");
        f.add_child(inside);
        let direct = actor(&mut w, "Dup");
        let mut root = actor(&mut w, "Root");
        // フォルダを先に追加しても、直下の "Dup" が優先される
        root.add_child(f);
        root.add_child(direct);

        let hit = resolve_actor_path(&root, "Dup").expect("Dup は解決できる");
        assert!(!hit.is_folder());
        // フォルダ配下の同名ではなく直下の方（= root.children の直接一致）が返る
        let direct_entity = root.children().iter().find(|c| c.name == "Dup").unwrap().entity;
        assert_eq!(hit.entity, direct_entity);
    }

    /// フォルダ配下に無い名前は従来どおり None。
    #[test]
    fn missing_name_under_folder_is_none() {
        let mut w = World::new();
        let mut f = folder(&mut w, "Group");
        f.add_child(actor(&mut w, "A"));
        let mut root = actor(&mut w, "Root");
        root.add_child(f);

        assert!(resolve_actor_path(&root, "B").is_none());
    }
}
