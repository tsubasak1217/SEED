// ============================================================
//  undo_selection.rs — 木の組み直しを伴う Undo/Redo をまたいで、アクタの選択を同じアクタへ引き直す
//
//  【なぜ要るか】（docs/reviews/2026-10-02_code_review.md の #1）
//  アクタの追加・削除・付け替え・プレビューの出し入れの Undo/Redo は、前後の木の写し
//  （ActorTreeSnapshotCommand）から世界線の木を丸ごと組み直す（actor_ops.rs の rebuild_actors_for_wl）。
//  組み直すと手前のアクタの増減ぶん DFS 番号がずれるのに、以前は選択（selected_actor_dfs_ids）を
//  古い番号のまま残し、エディタへ SELECTED も ACTOR_COMPONENTS も送っていなかった。
//  エディタのインスペクタは古い番号を持ったままになり、次の値の編集（SET_*:{古い番号}）が
//  その番号に今いる **別の実アクタ** へ当たって、普通の編集として保存されてしまう。
//
//  【同じアクタの見分け方】
//  組み直しは entity も全部作り直すので、プレビューの順方向の操作（editor_preview/ops.rs）のような
//  entity での引き直しはできない。そこで **エディタのヒエラルキーの安定キーと同じ規則**
//  （HierarchyPanel.Expansion.cs の AssignStableKeys:「ルートからの名前の道筋」＋「同じ親の下の同じ名前の
//  兄弟の中で何番目か」）で控え、組み直した木で引き直す。エディタも同期のたびに同じ鍵で選択を
//  追いかけるので、ランタイムの選択とエディタの選択が食い違わない。
//
//  【引き直せないとき】
//  道筋で引けなくても、組み直しの前後で **木の形**（DFS 順に並べた各アクタの子の数）が同じなら、同じ番号のまま残す
//  （名前の変更の Undo/Redo。形が同じなら同じ番号は同じ位置なので、以前どおり同じアクタを選んだままにする）。
//  形も道筋も変わった（別の親への付け替えの Undo/Redo）・選んでいたアクタが消えた
//  （プレビュー・追加したアクタを戻した）ときは、そのアクタを選択から外す。何も残らなければ未選択
//  （SELECTED:-1）を送り、エディタのインスペクタを空にさせる（古い番号で別のアクタを編集させない）。
//
//  【送る順番】ヒエラルキー → SELECTED →（主があれば）ACTOR_COMPONENTS。エディタが先に行を作るため
//  （ipc_handler.rs の Undo / Redo の腕が send_hierarchy_after_undo の後に restore_selection_from_path_keys を呼ぶ）。
// ============================================================

use std::collections::HashMap;

use crate::engine::structs::objects::Actor;

use super::{actor_subtree_size, App};

// ============================================================
//  道筋の鍵（App に依存しない純粋な部分）
// ============================================================

/// 道筋の 1 段（名前と、同じ親の下の同じ名前の兄弟の中で何番目か）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct PathStep {
    /// アクタの名前
    name: String,
    /// 同じ親の下に同じ名前の兄弟が並ぶとき、その中で何番目か（0 始まり。木の並び順）
    occurrence: usize,
}

/// アクタの道筋の鍵（トップレベルから順。エディタの StableKey と同じ情報）。
pub(super) type ActorPathKey = Vec<PathStep>;

/// 兄弟の並びに「同じ名前の中で何番目か」を振って、(アクタ, 段) の並びで返す。
///
/// トップレベルは世界線で絞った後の並びに振る（エディタの木のルート階層と同じ）。
fn steps_of<'a>(siblings: impl Iterator<Item = &'a Actor>) -> Vec<(&'a Actor, PathStep)> {
    let mut seen: HashMap<&'a str, usize> = HashMap::new();
    siblings
        .map(|actor| {
            let occurrence = seen.entry(actor.name.as_str()).or_insert(0);
            let step = PathStep { name: actor.name.clone(), occurrence: *occurrence };
            *occurrence += 1;
            (actor, step)
        })
        .collect()
}

/// 世界線 `wl` の木の DFS 番号 `dfs` のアクタの道筋の鍵を作る（番号の数え方は find_actor_by_dfs と同じ）。
///
/// 見つからなければ None。
pub(super) fn path_key_of_dfs(actors: &[Actor], wl: u32, dfs: u32) -> Option<ActorPathKey> {
    let mut counter = 0u32;
    let mut path = ActorPathKey::new();
    let top_level = steps_of(actors.iter().filter(|actor| actor.world_line == wl));
    key_in(&top_level, dfs, &mut counter, &mut path).then_some(path)
}

/// `path_key_of_dfs` の本体: 兄弟の並びを DFS 順にたどり、`target` 番目に着いたら道筋を残して true。
fn key_in(siblings: &[(&Actor, PathStep)], target: u32, counter: &mut u32, path: &mut ActorPathKey) -> bool {
    for (actor, step) in siblings {
        path.push(step.clone());
        if *counter == target {
            return true;
        }
        *counter += 1;
        let children = steps_of(actor.children().iter());
        if key_in(&children, target, counter, path) {
            return true;
        }
        path.pop();
    }
    false
}

/// 道筋の鍵から、世界線 `wl` の木での DFS 番号を引く。たどれなければ None。
pub(super) fn dfs_of_path_key(actors: &[Actor], wl: u32, key: &ActorPathKey) -> Option<u32> {
    let top_level = steps_of(actors.iter().filter(|actor| actor.world_line == wl));
    let mut counter = 0u32;
    dfs_in(&top_level, key, &mut counter)
}

/// `dfs_of_path_key` の本体: 1 段ずつ鍵に合う兄弟を探し、手前の兄弟の部分木の大きさだけ番号を進める。
fn dfs_in(siblings: &[(&Actor, PathStep)], key: &[PathStep], counter: &mut u32) -> Option<u32> {
    let (head, rest) = key.split_first()?;
    for (actor, step) in siblings {
        if step != head {
            // 鍵に合わない兄弟は部分木ごと飛ばす（その中の番号を数え進める）
            *counter += actor_subtree_size(actor);
            continue;
        }
        if rest.is_empty() {
            return Some(*counter);
        }
        *counter += 1;
        let children = steps_of(actor.children().iter());
        return dfs_in(&children, rest, counter);
    }
    None
}

/// 世界線 `wl` の木の形（DFS 順に各アクタの子の数を並べたもの）。
///
/// 前順の子の数の並びは森の形を一意に表すので、並びが同じなら形も同じで、同じ DFS 番号は同じ位置を指す。
pub(super) fn tree_shape(actors: &[Actor], wl: u32) -> Vec<u32> {
    /// 自分の子の数を足してから子へ降りる（前順）。
    fn push(actor: &Actor, out: &mut Vec<u32>) {
        out.push(actor.children().len() as u32);
        for child in actor.children() {
            push(child, out);
        }
    }
    let mut out = Vec::new();
    for actor in actors.iter().filter(|actor| actor.world_line == wl) {
        push(actor, &mut out);
    }
    out
}

/// 控えた選択の 1 体（組み直す前の番号と、道筋の鍵）。
#[derive(Clone, Debug)]
pub(super) struct SavedActor {
    /// 組み直す前の DFS 番号（木の形が変わらなかったときの引き直しに使う）
    dfs: usize,
    /// 道筋の鍵（番号が木に無かったときは None）
    key: Option<ActorPathKey>,
}

/// 控えた 1 体を、組み直した後の木の番号へ引く（純関数）。
///
/// 1. 道筋の鍵で引ければその番号
/// 2. 引けなくても、木の形が前後で同じ（`same_shape`）なら同じ番号（名前の変更の Undo/Redo）。木の外の番号は捨てる
/// 3. どちらでもなければ None（消えた・別の親へ移った）
pub(super) fn resolve_saved_actor(
    saved: &SavedActor,
    actors: &[Actor],
    wl: u32,
    same_shape: bool,
    node_count: usize,
) -> Option<usize> {
    saved
        .key
        .as_ref()
        .and_then(|key| dfs_of_path_key(actors, wl, key))
        .map(|dfs| dfs as usize)
        .or_else(|| (same_shape && saved.dfs < node_count).then_some(saved.dfs))
}

// ============================================================
//  App の窓口
// ============================================================

/// 木の組み直しの前に、選択を道筋の鍵で控えたもの。
#[derive(Default)]
pub(super) struct SelectionPathKeys {
    /// 控える前に何かを選んでいたか（何も選んでいなければ、組み直しの後も何も送らない＝従来どおり）
    had_selection: bool,
    /// 選択中のアクタ（並びは元の選択の順）
    all: Vec<SavedActor>,
    /// 主の選択（インスペクタに出ている 1 体）
    primary: Option<SavedActor>,
    /// 組み直す前の木の形（tree_shape。前後で同じなら、道筋で引けない選択も同じ番号のまま残す）
    shape: Vec<u32>,
}

impl App {
    /// いまのアクタの選択（表示中の世界線の DFS）を道筋の鍵で控える（木を組み直す **前** に呼ぶ）。
    pub(super) fn capture_selection_path_keys(&self) -> SelectionPathKeys {
        let had_selection = !self.selected_actor_dfs_ids.is_empty() || self.actor_virtual_selected_idx.is_some();
        let Some(scene) = self.scene.as_ref() else {
            return SelectionPathKeys { had_selection, ..Default::default() };
        };
        let wl = self.active_world_line;
        let saved_of = |dfs: usize| SavedActor {
            dfs,
            key: u32::try_from(dfs).ok().and_then(|dfs| path_key_of_dfs(&scene.actors, wl, dfs)),
        };
        SelectionPathKeys {
            had_selection,
            all: self.selected_actor_dfs_ids.iter().map(|&dfs| saved_of(dfs)).collect(),
            primary: self.actor_virtual_selected_idx.map(saved_of),
            shape: tree_shape(&scene.actors, wl),
        }
    }

    /// 控えた選択を、組み直した後の木の DFS 番号へ引き直して戻し、エディタへ送る（ヒエラルキーを送った **後** に呼ぶ）。
    ///
    /// 引けなかった（消えた・道筋と形の両方が変わった）アクタは選択から外す（引き方は resolve_saved_actor）。
    /// 主が引けなければ残った選択の最後を主にする。
    /// 控える前に何も選んでいなければ何もしない（組み直しの前と同じく何も送らない）。
    pub(super) fn restore_selection_from_path_keys(&mut self, saved: SelectionPathKeys) {
        if !saved.had_selection {
            return;
        }
        let (all, primary) = match self.scene.as_ref() {
            Some(scene) => {
                let wl = self.active_world_line;
                let shape = tree_shape(&scene.actors, wl);
                let same_shape = shape == saved.shape;
                let resolve =
                    |actor: &SavedActor| resolve_saved_actor(actor, &scene.actors, wl, same_shape, shape.len());
                let all: Vec<usize> = saved.all.iter().filter_map(resolve).collect();
                let primary = saved.primary.as_ref().and_then(resolve).or_else(|| all.last().copied());
                (all, primary)
            }
            None => (Vec::new(), None),
        };
        self.apply_actor_selection(all, primary);
    }

    /// アクタの選択を書き換えてエディタへ送る（SELECTED / SELECTED_MULTI、主があればその ACTOR_COMPONENTS）。
    ///
    /// 何も選ばれていなければ SELECTED:-1（send_selected の規則）。
    /// Undo/Redo の引き直し（上）とプレビューの順方向の操作（editor_preview/ops.rs の restore_selection）で共用する。
    pub(super) fn apply_actor_selection(&mut self, all: Vec<usize>, primary: Option<usize>) {
        self.selected_actor_dfs_ids = all;
        self.actor_virtual_selected_idx = primary;
        self.selected_instances.clear();
        self.send_selected();
        if let Some(primary) = primary {
            self.send_actor_components(primary as u32, self.actor_virtual_selected_slot_idx);
        }
    }

    /// Undo/Redo の後のヒエラルキーを送る（構造の変わる Undo/Redo のときだけ ipc_handler.rs から呼ぶ）。
    ///
    /// - プレビューの出し入れ（保存されるシーンを変えない操作）… 未保存にしない印付きで即時（send_hierarchy_quiet）
    /// - 木を組み直した … 即時（send_hierarchy_now）。直後に引き直した選択（SELECTED）を送るので、
    ///   ヒエラルキーを間引きで遅らせるとエディタが古い木の行を選んでしまう（Ctrl+Z の長押しで起きる）
    /// - それ以外（表示・アクティブの切り替えなど）… 従来どおり間引き付き（send_hierarchy）
    pub(super) fn send_hierarchy_after_undo(&mut self, scene_neutral: bool, tree_rebuilt: bool) {
        if scene_neutral {
            self.send_hierarchy_quiet();
        } else if tree_rebuilt {
            self.send_hierarchy_now();
        } else {
            self.send_hierarchy();
        }
    }
}

// ============================================================
//  テスト — 道筋の鍵の作り方・引き直し（プレビューの出し入れ・普通の追加の Undo の形）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::ecs::World;

    /// テスト用: 名前と子からアクタを作る（世界線は `wl`。子も同じ世界線）。
    fn actor(world: &mut World, name: &str, wl: u32, children: Vec<Actor>) -> Actor {
        let mut actor = Actor::new(world.spawn(), name);
        actor.world_line = wl;
        for child in children {
            actor.add_child(child);
        }
        actor
    }

    /// テスト用: App.scene に似た木（世界線 0）。DFS:
    /// 0 App, 1 Background, 2 RootStack, 3 Screens, 4 Shell, 5 Header, 6 Veil, 7 Modals
    fn app_like_tree(world: &mut World) -> Vec<Actor> {
        let header = actor(world, "Header", 0, vec![]);
        let shell = actor(world, "Shell", 0, vec![header]);
        let screens = actor(world, "Screens", 0, vec![shell]);
        let veil = actor(world, "Veil", 0, vec![]);
        let root_stack = actor(world, "RootStack", 0, vec![screens, veil]);
        let background = actor(world, "Background", 0, vec![]);
        let modals = actor(world, "Modals", 0, vec![]);
        vec![actor(world, "App", 0, vec![background, root_stack, modals])]
    }

    /// テスト用: 名前で子を探す（直下だけ）。
    fn child_mut<'a>(actor: &'a mut Actor, name: &str) -> &'a mut Actor {
        actor.children_mut().iter_mut().find(|c| c.name == name).expect("子があること")
    }

    /// 道筋の鍵から同じ番号へ戻ること（木を変えなければ往復で同じ）。
    #[test]
    fn key_round_trips_on_the_same_tree() {
        let mut world = World::new();
        let actors = app_like_tree(&mut world);
        for dfs in 0..8 {
            let key = path_key_of_dfs(&actors, 0, dfs).expect("鍵が作れること");
            assert_eq!(dfs_of_path_key(&actors, 0, &key), Some(dfs), "DFS {dfs}");
        }
        assert!(path_key_of_dfs(&actors, 0, 8).is_none(), "木の外の番号は None");
        assert!(path_key_of_dfs(&actors, 1, 0).is_none(), "別の世界線には無い");
    }

    /// レビュー #1 の手順: Background の下へ部分木（プレビュー）を足した木で選んだ Shell を、
    /// 部分木を除いた木（Ctrl+Z の後）で引くと、ずれた番号ではなく Shell の新しい番号になる。逆（Redo）も同じ。
    #[test]
    fn selection_follows_the_same_actor_across_subtree_add_and_remove() {
        let mut world = World::new();
        let without = app_like_tree(&mut world);
        let mut with = app_like_tree(&mut world);
        // プレビュー（3 ノード: ScreenFrame / Body / AlarmEdit）を Background の末尾の子として足す
        let alarm_edit = actor(&mut world, "AlarmEdit", 0, vec![]);
        let body = actor(&mut world, "Body", 0, vec![alarm_edit]);
        let frame = actor(&mut world, "ScreenFrame", 0, vec![body]);
        child_mut(&mut with[0], "Background").add_child(frame);

        // 足した木: Shell は 4 + 3 = 7
        let key = path_key_of_dfs(&with, 0, 7).expect("Shell の鍵");
        assert_eq!(key.last().map(|s| s.name.as_str()), Some("Shell"));
        // Undo の後（部分木なし）: Shell は 4。古い番号 7 は別のアクタ（Modals）を指す
        assert_eq!(dfs_of_path_key(&without, 0, &key), Some(4));
        assert_eq!(path_key_of_dfs(&without, 0, 7).and_then(|k| k.last().cloned()).map(|s| s.name), Some("Modals".into()));
        // Redo の後（部分木あり）: 4 の Shell は 7 へ戻る
        let key_undone = path_key_of_dfs(&without, 0, 4).expect("Shell の鍵");
        assert_eq!(dfs_of_path_key(&with, 0, &key_undone), Some(7));
        // 部分木の中のノード（消えたアクタ）は引けない（足した木の DFS: 0 App, 1 Background, 2 ScreenFrame, 3 Body, 4 AlarmEdit）
        let inner = path_key_of_dfs(&with, 0, 2).expect("ScreenFrame の鍵");
        assert_eq!(inner.last().map(|s| s.name.as_str()), Some("ScreenFrame"));
        assert_eq!(dfs_of_path_key(&without, 0, &inner), None);
    }

    /// 同じ名前の兄弟は「何番目か」で見分ける（後ろへ同じ名前を足しても前の兄弟の鍵は変わらない）。
    #[test]
    fn same_named_siblings_are_told_apart_by_occurrence() {
        let mut world = World::new();
        let a = actor(&mut world, "Item", 0, vec![]);
        let b = actor(&mut world, "Item", 0, vec![]);
        let list = actor(&mut world, "List", 0, vec![a, b]);
        let mut actors = vec![list];
        let second = path_key_of_dfs(&actors, 0, 2).expect("2 つ目の Item");
        assert_eq!(second.last().map(|s| s.occurrence), Some(1));

        // 末尾へもう 1 つ足しても、2 つ目の鍵は 2 つ目を指したまま
        let c = actor(&mut world, "Item", 0, vec![]);
        actors[0].add_child(c);
        assert_eq!(dfs_of_path_key(&actors, 0, &second), Some(2));
    }

    /// 名前の変わったアクタは引けない（選択から外す側に倒す）。
    #[test]
    fn renamed_actor_is_not_found() {
        let mut world = World::new();
        let mut actors = app_like_tree(&mut world);
        let key = path_key_of_dfs(&actors, 0, 4).expect("Shell の鍵");
        child_mut(child_mut(child_mut(&mut actors[0], "RootStack"), "Screens"), "Shell").name = "Renamed".to_string();
        assert_eq!(dfs_of_path_key(&actors, 0, &key), None);
    }

    /// 名前の変更の Undo/Redo（木の形は同じ）は、道筋で引けなくても同じ番号のまま残す。
    /// 形も道筋も変わった（付け替え・消えた）ときは外す。木の外の番号も捨てる。
    #[test]
    fn same_shape_keeps_the_number_when_the_path_changed() {
        let mut world = World::new();
        let before = app_like_tree(&mut world);
        let saved = SavedActor { dfs: 4, key: path_key_of_dfs(&before, 0, 4) };

        // 名前の変更（Shell → Renamed）: 形は同じ → 同じ番号
        let mut renamed = app_like_tree(&mut world);
        child_mut(child_mut(child_mut(&mut renamed[0], "RootStack"), "Screens"), "Shell").name = "Renamed".to_string();
        let shape = tree_shape(&renamed, 0);
        assert_eq!(shape, tree_shape(&before, 0), "名前だけの違いは形を変えない");
        assert_eq!(resolve_saved_actor(&saved, &renamed, 0, true, shape.len()), Some(4));

        // 形も変わり（Background の下に 1 体足した）、名前も変わった: 外す
        let extra = actor(&mut world, "Extra", 0, vec![]);
        child_mut(&mut renamed[0], "Background").add_child(extra);
        let changed = tree_shape(&renamed, 0);
        assert_ne!(changed, shape, "1 体足すと形が変わる");
        assert_eq!(resolve_saved_actor(&saved, &renamed, 0, false, changed.len()), None);

        // 道筋で引けるなら形によらずその番号（Undo の前後で Shell が 1 つずれた）
        let mut shifted = app_like_tree(&mut world);
        let extra = actor(&mut world, "Extra", 0, vec![]);
        child_mut(&mut shifted[0], "Background").add_child(extra);
        let shifted_count = tree_shape(&shifted, 0).len();
        assert_eq!(resolve_saved_actor(&saved, &shifted, 0, false, shifted_count), Some(5));

        // 木の外の番号は、形が同じでも捨てる
        let outside = SavedActor { dfs: 99, key: None };
        assert_eq!(resolve_saved_actor(&outside, &before, 0, true, tree_shape(&before, 0).len()), None);
    }

    /// トップレベルは世界線で絞ってから数える（別の世界線のアクタが間に挟まっても番号も鍵もずれない）。
    #[test]
    fn top_level_is_filtered_by_world_line() {
        let mut world = World::new();
        let mut actors = app_like_tree(&mut world);
        actors.insert(0, actor(&mut world, "App", 2, vec![]));   // 別の世界線の同じ名前
        let key = path_key_of_dfs(&actors, 0, 4).expect("Shell の鍵");
        assert_eq!(key.first().map(|s| s.occurrence), Some(0), "別の世界線の App は数えない");
        assert_eq!(dfs_of_path_key(&actors, 0, &key), Some(4));
    }
}
