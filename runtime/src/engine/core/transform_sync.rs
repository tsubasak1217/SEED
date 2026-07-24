// ============================================================
//  transform_sync.rs — アクタ Transform 変更の集約適用（ワールド伝播）
//
//  【なぜこのモジュールが必要か】
//  SEED の Transform は「ワールド空間」で保持され、ローカル座標系も親子行列合成も
//  存在しない（components/transform.rs 参照）。親子関係は Actor ツリーだけが持ち、
//  実際の描画は ModelComponent.instance_mats（ワールド行列の配列）が担う。
//
//  そのため「アクタの Transform を書き換える」という操作は、単にコンポーネントの
//  数値を書くだけでは不完全で、次の 3 つを必ずセットで行う必要がある。
//    1. 自アクタの Transform を新しい値に置き換える
//    2. 自アクタの全 Model スロットの instance_mats へ差分行列（delta）を左乗算する
//    3. 全子孫アクタの Transform と instance_mats へ同じ delta を伝播する
//
//  この 3 点セットを 1 か所に集約するのが本モジュールである。
//  エディタのインスペクタ数値入力・ギズモドラッグ・C# スクリプト・AI 操作など、
//  すべての「Transform を書き換える経路」はここを通すこと。
//  （個別に実装すると、以前のように「スクリプトから動かすと子カメラが付いてこない」
//    といった経路ごとの取りこぼしが発生する。）
//
//  【適用は即時】
//  フレーム末尾にまとめて流すダーティ方式は採らない。代入直後に子の座標を読んだとき
//  1 フレーム古い値が返る挙動は、デバッグ困難なバグの温床になるため。
//
//  【対象外（今後の課題）】
//  アニメーション・物理の書き戻し・JointAttach からの Transform 更新は本モジュールを
//  経由していない。特に物理は「親も子も剛体」のとき delta の二重適用で破綻するため、
//  除外ルールの設計が必要（別タスク）。
// ============================================================

use crate::engine::components::{ComponentKind, ModelComponent, Transform};
use crate::engine::ecs::{Entity, World};
use crate::engine::methods::gizmo_interact::{mat4x4_inv, mat4x4_mul};
use crate::engine::structs::objects::Actor;

/// 4x4 行列（行優先・GPU 慣習）。instance_mats と同じ表現。
pub type Mat4 = [[f32; 4]; 4];

/// 単位行列。delta が計算できないときの中立値として使う。
pub const MAT4_IDENTITY: Mat4 = [
    [1.0, 0.0, 0.0, 0.0],
    [0.0, 1.0, 0.0, 0.0],
    [0.0, 0.0, 1.0, 0.0],
    [0.0, 0.0, 0.0, 1.0],
];

/// スケールが実質 0（＝行列が特異で逆行列が不定）とみなす閾値。
/// この値未満のスケールを持つ Transform からは delta を算出できない。
pub const SINGULAR_SCALE_EPS: f32 = 1e-7;

/// 子孫アクタ 1 件分の変更記録。
/// タプル形式は Undo コマンド（ActorGroupTransformCommand::child_transforms）が
/// 期待する形と一致させてある: (子 DFS ID, 旧 Transform, 新 Transform, 旧 MC 行列, 新 MC 行列)。
pub type ChildTransformChange = (u32, Transform, Transform, Mat4, Mat4);

/// `set_actor_world_transform` の結果。Undo 記録や IPC 送出は呼び出し側の責務なので、
/// 「何がどう変わったか」だけを返す。
pub struct TransformSyncResult {
    /// 変更前のワールド Transform
    pub old_tf: Transform,
    /// 変更後のワールド Transform
    pub new_tf: Transform,
    /// 適用したワールド差分行列（new * inv(old)）。特異時は単位行列。
    /// 呼び出し側が同じ delta を別対象（マルチ選択など）へ流用できるよう公開している。
    #[allow(dead_code)]
    pub delta: Mat4,
    /// 変更前のスケールが 0 で delta が算出できなかったか。
    /// true のとき子孫への伝播はスキップされている（delta が不定のため）。
    #[allow(dead_code)]
    pub singular: bool,
    /// 自アクタの Model スロットで実際に変化したインスタンス行列
    /// (インスタンス index, 旧行列, 新行列)。
    /// 複数の Model スロットを持つ場合、同じ index が複数回現れ得る
    /// （既存の Undo コマンドの記録形式をそのまま踏襲している）。
    pub self_instance_changes: Vec<(u32, Mat4, Mat4)>,
    /// 子孫アクタの変更一覧（DFS 順）。
    pub child_changes: Vec<ChildTransformChange>,
}

/// Transform が特異（いずれかの軸のスケールが実質 0）かどうかを判定する。
///
/// 特異な行列は逆行列が不定になるため、差分行列 delta = new * inv(old) を計算できない。
pub fn is_transform_singular(tf: &Transform) -> bool {
    tf.scale.iter().any(|&s| s.abs() < SINGULAR_SCALE_EPS)
}

/// アクタのワールド Transform を設定し、描画実体と子孫へ一貫して伝播する【集約関数】。
///
/// 実行内容:
///   1. `actor.entity` の Transform を `new_tf` に置き換える
///   2. 自アクタの全 Model スロットの `instance_mats` 全要素へ delta を左乗算し、
///      バッチを dirty にする
///   3. 全子孫アクタの Transform と Model スロットへ delta を伝播する
///
/// # 引数
/// - `actor`:           対象アクタ（ツリー構造の読み取りのみ。可変参照は不要）
/// - `world`:           コンポーネント実体の格納先
/// - `new_tf`:          設定したいワールド Transform
/// - `child_dfs_start`: 子孫の DFS ID 採番の開始値（＝自アクタの DFS ID + 1）。
///                      Undo 記録が不要な経路（スクリプト・AI）では 0 でよい。
///
/// # 特異行列ガード
/// 変更前のスケールが 0 の場合、逆行列が不定で delta を算出できない。
/// このときは自アクタの `instance_mats` に `new_tf` の行列を直接設定し、
/// 子孫への伝播はスキップする（インスペクタ入力の既存挙動と同じ）。
pub fn set_actor_world_transform(
    actor:           &Actor,
    world:           &mut World,
    new_tf:          Transform,
    child_dfs_start: u32,
) -> TransformSyncResult {
    // ── 1. 自アクタの Transform を置き換え、旧値を退避する ──────────────
    let old_tf = world.get::<Transform>(actor.entity).cloned().unwrap_or_default();
    if let Some(t) = world.get_mut::<Transform>(actor.entity) {
        *t = new_tf.clone();
    }

    // ── 2. 差分行列を求める（特異ガード付き）────────────────────────────
    let singular   = is_transform_singular(&old_tf);
    let new_tf_mat = new_tf.to_mat4();
    let delta = if singular {
        MAT4_IDENTITY
    } else {
        mat4x4_mul(new_tf_mat, mat4x4_inv(old_tf.to_mat4()))
    };

    // ── 3. 自アクタの全 Model スロットへ適用する ────────────────────────
    // ModelComponent は actor.entity ではなくスロット専用 entity に格納されるため、
    // 先にスロット entity を収集してから world を可変借用する。
    let slot_entities: Vec<Entity> = actor.slots().iter()
        .filter(|s| s.kind == ComponentKind::Model)
        .map(|s| s.entity)
        .collect();

    let mut self_instance_changes: Vec<(u32, Mat4, Mat4)> = Vec::new();
    for slot_entity in slot_entities {
        let Some(mc) = world.get_mut::<ModelComponent>(slot_entity) else { continue };
        let old_mats = mc.instance_mats.clone();
        for m in mc.instance_mats.iter_mut() {
            // 特異時は delta が不定なので、新しい Transform の行列を直接設定する
            *m = if singular { new_tf_mat } else { mat4x4_mul(delta, *m) };
        }
        mc.mark_batch_dirty();
        for (i, &old) in old_mats.iter().enumerate() {
            if let Some(new) = mc.instance_mats.get(i).copied() {
                if new != old {
                    self_instance_changes.push((i as u32, old, new));
                }
            }
        }
    }

    // ── 4. 子孫アクタへ delta を伝播する ────────────────────────────────
    // 特異時は delta が不定なので伝播しない（子を原点へ吸い込むなどの破壊を防ぐ）。
    let mut child_changes: Vec<ChildTransformChange> = Vec::new();
    if !singular {
        let mut counter = child_dfs_start;
        propagate_delta_to_children(actor, world, delta, &mut counter, &mut child_changes);
    }

    TransformSyncResult { old_tf, new_tf, delta, singular, self_instance_changes, child_changes }
}

/// ワールド差分行列 `delta` を全子孫アクタへ再帰適用する。
///
/// 各子について次を行う:
///   - Model スロット（最初の 1 スロット）の先頭インスタンス行列へ delta を左乗算
///   - Transform（ワールド空間）へ delta を左乗算
///
/// **Model を持たない子（カメラ・空アクタなど）も必ず対象にする。**
/// Transform は Model の有無と無関係に存在するため、Model の有無で分岐すると
/// モデルを持たない子カメラが親に追従しなくなる。
///
/// `dfs_counter` は呼び出し時点で「最初の子の DFS ID」を指していること。
/// 結果は DFS 順（親 → 子 → 孫）に `result` へ積まれる。
pub fn propagate_delta_to_children(
    actor:       &Actor,
    world:       &mut World,
    delta:       Mat4,
    dfs_counter: &mut u32,
    result:      &mut Vec<ChildTransformChange>,
) {
    for child in actor.children() {
        let child_dfs = *dfs_counter;
        *dfs_counter += 1;

        let child_entity   = child.entity;
        let mc_slot_entity = child.mc_entity();

        // ── Model スロットの先頭インスタンス行列を更新する（無ければ単位行列扱い）──
        let (old_mc_mat, new_mc_mat) = match mc_slot_entity
            .and_then(|e| world.get_mut::<ModelComponent>(e))
        {
            Some(mc) => {
                let old = mc.instance_mats.first().copied().unwrap_or(MAT4_IDENTITY);
                if let Some(m) = mc.instance_mats.first_mut() {
                    *m = mat4x4_mul(delta, *m);
                }
                mc.mark_batch_dirty();
                let new = mc.instance_mats.first().copied().unwrap_or(MAT4_IDENTITY);
                (old, new)
            }
            None => (MAT4_IDENTITY, MAT4_IDENTITY),
        };

        // ── Transform を更新する（Model の有無に依存しない）──────────────
        // Transform を持たないノード（フォルダ・2D アクタ）は get が None を返すため
        // 何も起きない。2D は毎フレーム親キャンバス階層から再計算されるので対象外でよい。
        let old_tf = world.get::<Transform>(child_entity).cloned().unwrap_or_default();
        let new_tf = Transform::from_mat4(&mat4x4_mul(delta, old_tf.to_mat4()));
        if let Some(tf) = world.get_mut::<Transform>(child_entity) {
            *tf = new_tf.clone();
        }

        result.push((child_dfs, old_tf, new_tf, old_mc_mat, new_mc_mat));

        // 孫以下へ再帰する
        propagate_delta_to_children(child, world, delta, dfs_counter, result);
    }
}

// ============================================================
//  ユニットテスト
//
//  GPU・App に依存しない（World + Actor ツリーだけで完結する）範囲を検証する。
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::ecs::World;
    use std::any::TypeId;

    /// テスト用: 位置だけを持つ Transform を作る。
    fn tf_at(p: [f32; 3]) -> Transform {
        Transform { position: p, rotation: [0.0; 3], scale: [1.0; 3] }
    }

    /// テスト用: Transform 付きのアクタを 1 体作って World へ登録する。
    fn spawn_actor(world: &mut World, name: &str, tf: Transform) -> Actor {
        let e = world.spawn();
        world.insert(e, tf);
        Actor::new(e, name)
    }

    /// テスト用: アクタへ Model スロットを 1 つ足し、指定の instance_mats を設定する。
    fn attach_model(world: &mut World, actor: &mut Actor, mats: Vec<Mat4>) {
        let slot_e = world.spawn();
        let mut mc = ModelComponent::empty();
        mc.instance_mats = mats;
        world.insert(slot_e, mc);
        actor.add_slot("Model", ComponentKind::Model, TypeId::of::<ModelComponent>(), slot_e);
    }

    /// 位置だけの平行移動が、自身の instance_mats と子アクタへ正しく伝播すること。
    #[test]
    fn set_world_transform_propagates_translation_to_child() {
        let mut world = World::new();

        let mut parent = spawn_actor(&mut world, "Parent", tf_at([0.0, 0.0, 0.0]));
        attach_model(&mut world, &mut parent, vec![MAT4_IDENTITY]);

        let mut child = spawn_actor(&mut world, "Child", tf_at([1.0, 0.0, 0.0]));
        attach_model(&mut world, &mut child, vec![tf_at([1.0, 0.0, 0.0]).to_mat4()]);
        let child_entity = child.entity;
        let child_mc_e   = child.mc_entity().unwrap();
        parent.children.push(child);

        // 親を +X に 5 動かす
        let res = set_actor_world_transform(&parent, &mut world, tf_at([5.0, 0.0, 0.0]), 1);

        assert!(!res.singular, "スケール 1 の Transform は特異ではない");
        // 親自身の Transform
        let p_tf = world.get::<Transform>(parent.entity).unwrap();
        assert_eq!(p_tf.position, [5.0, 0.0, 0.0]);
        // 親の instance_mats（平行移動成分）
        let p_mc = world.get::<ModelComponent>(parent.mc_entity().unwrap()).unwrap();
        assert!((p_mc.instance_mats[0][0][3] - 5.0).abs() < 1e-5);
        // 子の Transform（ワールド空間なので 1 + 5 = 6）
        let c_tf = world.get::<Transform>(child_entity).unwrap();
        assert!((c_tf.position[0] - 6.0).abs() < 1e-4, "子 Transform が追従していない: {:?}", c_tf.position);
        // 子の instance_mats
        let c_mc = world.get::<ModelComponent>(child_mc_e).unwrap();
        assert!((c_mc.instance_mats[0][0][3] - 6.0).abs() < 1e-4);
        // 変更記録
        assert_eq!(res.child_changes.len(), 1);
        assert_eq!(res.child_changes[0].0, 1, "子の DFS ID は child_dfs_start から採番される");
    }

    /// Model を持たない子（カメラ等）も Transform が追従すること。
    /// これがギズモドラッグで抜けていた穴のリグレッションテスト。
    #[test]
    fn model_less_child_transform_follows_parent() {
        let mut world = World::new();

        let mut parent = spawn_actor(&mut world, "Parent", tf_at([0.0, 0.0, 0.0]));
        attach_model(&mut world, &mut parent, vec![MAT4_IDENTITY]);

        // Model スロットを一切持たない子（カメラ相当）
        let camera = spawn_actor(&mut world, "Camera", tf_at([0.0, 2.0, -3.0]));
        let cam_entity = camera.entity;
        parent.children.push(camera);

        set_actor_world_transform(&parent, &mut world, tf_at([0.0, 0.0, 10.0]), 1);

        let cam_tf = world.get::<Transform>(cam_entity).unwrap();
        assert!((cam_tf.position[2] - 7.0).abs() < 1e-4,
                "Model を持たない子が追従していない: {:?}", cam_tf.position);
        assert!((cam_tf.position[1] - 2.0).abs() < 1e-4);
    }

    /// 孫アクタまで再帰的に伝播すること。
    #[test]
    fn propagation_reaches_grandchild() {
        let mut world = World::new();

        let mut parent = spawn_actor(&mut world, "Parent", tf_at([0.0, 0.0, 0.0]));
        let mut child  = spawn_actor(&mut world, "Child",  tf_at([1.0, 0.0, 0.0]));
        let grandchild = spawn_actor(&mut world, "Grand",  tf_at([2.0, 0.0, 0.0]));
        let g_entity   = grandchild.entity;
        let c_entity   = child.entity;
        child.children.push(grandchild);
        parent.children.push(child);

        let res = set_actor_world_transform(&parent, &mut world, tf_at([0.0, 3.0, 0.0]), 1);

        assert_eq!(res.child_changes.len(), 2, "子と孫の 2 件が記録されるはず");
        assert!((world.get::<Transform>(c_entity).unwrap().position[1] - 3.0).abs() < 1e-4);
        assert!((world.get::<Transform>(g_entity).unwrap().position[1] - 3.0).abs() < 1e-4);
        // DFS 順（子 → 孫）で採番されること
        assert_eq!(res.child_changes[0].0, 1);
        assert_eq!(res.child_changes[1].0, 2);
    }

    /// スケールが回転を伴って子へ伝播すること（delta が単なる平行移動でないケース）。
    #[test]
    fn scale_propagates_to_child_position() {
        let mut world = World::new();

        let mut parent = spawn_actor(&mut world, "Parent", tf_at([0.0, 0.0, 0.0]));
        let child      = spawn_actor(&mut world, "Child",  tf_at([2.0, 0.0, 0.0]));
        let c_entity   = child.entity;
        parent.children.push(child);

        // 親を 3 倍にスケールする（原点中心なので子は 2 → 6 へ）
        let scaled = Transform { position: [0.0; 3], rotation: [0.0; 3], scale: [3.0, 3.0, 3.0] };
        set_actor_world_transform(&parent, &mut world, scaled, 1);

        let c_tf = world.get::<Transform>(c_entity).unwrap();
        assert!((c_tf.position[0] - 6.0).abs() < 1e-4, "スケールが子位置へ伝播していない: {:?}", c_tf.position);
        assert!((c_tf.scale[0] - 3.0).abs() < 1e-4, "スケールが子スケールへ伝播していない");
    }

    /// 旧スケールが 0（特異）のときは delta を使わず、子孫伝播をスキップすること。
    #[test]
    fn singular_old_scale_skips_child_propagation() {
        let mut world = World::new();

        let zero = Transform { position: [0.0; 3], rotation: [0.0; 3], scale: [0.0, 0.0, 0.0] };
        let mut parent = spawn_actor(&mut world, "Parent", zero);
        attach_model(&mut world, &mut parent, vec![MAT4_IDENTITY]);

        let child    = spawn_actor(&mut world, "Child", tf_at([1.0, 0.0, 0.0]));
        let c_entity = child.entity;
        parent.children.push(child);

        let res = set_actor_world_transform(&parent, &mut world, tf_at([4.0, 0.0, 0.0]), 1);

        assert!(res.singular, "スケール 0 は特異と判定されるべき");
        assert!(res.child_changes.is_empty(), "特異時は子孫へ伝播しない");
        // 子は動かない
        assert_eq!(world.get::<Transform>(c_entity).unwrap().position, [1.0, 0.0, 0.0]);
        // 自身の instance_mats には新しい行列が直接設定される
        let p_mc = world.get::<ModelComponent>(parent.mc_entity().unwrap()).unwrap();
        assert!((p_mc.instance_mats[0][0][3] - 4.0).abs() < 1e-5);
    }
}
