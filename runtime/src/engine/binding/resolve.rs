// ============================================================
//  binding/resolve.rs — バインド先文字列の解析と、シーンからの実値解決
//
//  ## 保存形式
//  1 本のバインドは **`"アクタ名|スロット名|変数名"`** の 1 文字列で保存する。
//  3 つの要素を別キーに分けないのは、シーンファイル上で 1 行に収まり、
//  アクタ改名の追従（`rename_refs.rs`）が 1 か所の書き換えで済むためである。
//
//  区切りに `|` を選んだのは、アクタ名・スロット名・変数名のいずれにも
//  現れにくく、かつ JSON エスケープを増やさないため。
//  **要素に `|` が含まれる場合はバインドを保存できない**（設定側で弾く）。
//
//  ## 解決の流れ（毎フレーム）
//    ① アクタ名 → 世界線を DFS して最初の一致（`find_actor_by_name`）
//    ② スロット名 → そのアクタの有効スロットのうち名前が一致する最初のもの
//                   （特例: 変数が Transform のものならアクタのルート実体）
//    ③ 変数名 → 組込カタログ、またはスクリプトの `[Bindable]` フィールド
//    ④ 型が要求と厳密一致したときだけ値を返す
//  どこかで外れたら `None`（＝呼び出し側は保存値／既定値へフォールバックし、
//  インスペクタに ⚠ を出す）。
//
//  ## スクリプトの値は「C# 側の実行中の値」が正典
//  Rust の `ScriptComponent::fields` は**編集時のシリアライズ値**であり、
//  Play 中にスクリプトが書き換えた値は反映されない。したがって解決は必ず
//  FFI（`ScriptComponent::read_bindable_field`）で CLR 側の実インスタンスを読む。
//  `[Bindable]` の検証も C# 側で行う（＝ランタイム読み取り時に毎回検証される）。
// ============================================================

use crate::engine::components::script_component::ScriptComponent;
use crate::engine::components::{ComponentKind, Transform};
use crate::engine::ecs::World;
use crate::engine::structs::objects::Actor;

use super::catalog::{
    self, BindableHost, BindableValueType, BINDING_VALUE_COMPONENTS,
};

/// バインド先文字列の要素区切り。
pub const BINDING_SEPARATOR: char = '|';

/// バインド先文字列の要素数（アクタ名・スロット名・変数名）。
const BINDING_PART_COUNT: usize = 3;

/// 解決済みのバインド先（保存文字列を 3 要素へ割ったもの）。
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct BindingTarget {
    /// 参照先アクタ名（世界線内で DFS 最初の一致が使われる）。
    pub actor: String,
    /// 参照先スロット名（ユーザーが付けたスロット名。Transform はスロットを持たないため
    /// 組込カタログの `ActorRoot` 行が使う「Transform」という表示名がそのまま入る）。
    pub slot: String,
    /// 参照先の変数名。
    pub variable: String,
}

/// バインド先文字列を 3 要素へ割る。
///
/// 要素数が違う・いずれかが空の文字列は「壊れたバインド」として `None`
/// （＝未設定と同じ扱いにはせず、呼び出し側が ⚠ を出せるよう解決失敗にする）。
pub fn parse_binding(text: &str) -> Option<BindingTarget> {
    let parts: Vec<&str> = text.split(BINDING_SEPARATOR).collect();
    if parts.len() != BINDING_PART_COUNT { return None; }
    if parts.iter().any(|p| p.trim().is_empty()) { return None; }
    Some(BindingTarget {
        actor:    parts[0].trim().to_string(),
        slot:     parts[1].trim().to_string(),
        variable: parts[2].trim().to_string(),
    })
}

/// 3 要素をバインド先文字列へ組み立てる。
///
/// いずれかの要素が空、または区切り文字を含む場合は `None`
/// （保存すると読み戻せなくなるため、設定の時点で弾く）。
pub fn format_binding(actor: &str, slot: &str, variable: &str) -> Option<String> {
    let parts = [actor.trim(), slot.trim(), variable.trim()];
    if parts.iter().any(|p| p.is_empty() || p.contains(BINDING_SEPARATOR)) { return None; }
    Some(parts.join(&BINDING_SEPARATOR.to_string()))
}

/// 指定世界線のアクタツリーを DFS し、**名前が一致する最初のアクタ**を返す。
///
/// 同名アクタを禁止していないエンジンなので一致は複数ありうる。
/// 「DFS で最初に見つかったもの」という決定的な規則を明文化しておくことで、
/// 参照の指す先がフレームごとに揺れないことを保証する。
///
/// 名前が空の要求は常に `None`（「未設定」を「名前が空のアクタ」に解決させない）。
pub fn find_actor_by_name<'a>(
    actors:     &'a [Actor],
    world_line: u32,
    name:       &str,
) -> Option<&'a Actor> {
    if name.is_empty() { return None; }
    /// サブツリーを DFS して名前一致を探す。
    fn dfs<'a>(actor: &'a Actor, name: &str) -> Option<&'a Actor> {
        if actor.name == name { return Some(actor); }
        actor.children().iter().find_map(|c| dfs(c, name))
    }
    actors.iter()
        .filter(|a| a.world_line == world_line)
        .find_map(|root| dfs(root, name))
}

/// バインド 1 本を解決して実値を得る（毎フレーム呼ばれる想定）。
///
/// `want` は要求する型。**供給値の型と厳密一致**しなければ `None` を返す
/// （成分の部分取り出しは行わない）。
///
/// 解決できないケースはすべて `None`:
///   ・アクタが見つからない／スロットが見つからない／無効スロット
///   ・変数がカタログにも `[Bindable]` フィールドにも無い
///   ・型が一致しない
///   ・スクリプトが CLR 未起動のプレースホルダである
pub fn resolve_binding(
    actors:     &[Actor],
    world:      &World,
    world_line: u32,
    target:     &BindingTarget,
    want:       BindableValueType,
) -> Option<[f32; BINDING_VALUE_COMPONENTS]> {
    let actor = find_actor_by_name(actors, world_line, &target.actor)?;

    // ── ① アクタのルート直付け（Transform）─────────────────
    //     スロット一覧には現れないので、スロット走査より先に見る。
    //     スロット名は組込カタログの表示名（"Transform"）と一致していること。
    if world.get::<Transform>(actor.entity).is_some() {
        for var in catalog::variables_for(BindableHost::ActorRoot, want) {
            if var.component_label == target.slot && var.variable == target.variable {
                return catalog::read_builtin(world, actor.entity, var);
            }
        }
    }

    // ── ② スロット（名前一致・有効なもののみ）───────────────
    let slot = actor.slots().iter()
        .find(|s| s.enabled && s.name == target.slot)?;

    // ── ③-a スクリプト（C# 側の実行中の値を FFI で読む）──────
    if slot.kind == ComponentKind::Script {
        let sc = world.get::<ScriptComponent>(slot.entity)?;
        return sc.read_bindable_field(&target.variable, want.components());
    }

    // ── ③-b 組込コンポーネント（カタログ）──────────────────
    for var in catalog::variables_for(BindableHost::Slot(slot.kind), want) {
        if var.variable == target.variable {
            return catalog::read_builtin(world, slot.entity, var);
        }
    }
    None
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::light_component::LightComponent;

    /// バインド先文字列が往復すること。
    #[test]
    fn binding_string_roundtrips() {
        let text = format_binding("Sun", "Light", "intensity").expect("組み立てられる");
        assert_eq!(text, "Sun|Light|intensity");
        let t = parse_binding(&text).expect("読み戻せる");
        assert_eq!(t.actor, "Sun");
        assert_eq!(t.slot, "Light");
        assert_eq!(t.variable, "intensity");
    }

    /// 空要素・区切り文字混入・要素数違いが弾かれること。
    #[test]
    fn malformed_bindings_are_rejected() {
        assert_eq!(format_binding("", "Light", "intensity"), None, "空要素");
        assert_eq!(format_binding("A|B", "Light", "intensity"), None, "区切り文字混入");
        assert_eq!(parse_binding("Sun|Light"), None, "要素が足りない");
        assert_eq!(parse_binding("Sun|Light|a|b"), None, "要素が多い");
        assert_eq!(parse_binding("Sun||intensity"), None, "空要素");
        assert_eq!(parse_binding(""), None, "空文字列");
    }

    /// テスト用: Light スロットを 1 つ持つアクタを組む。
    fn light_actor(world: &mut World, actor_name: &str, slot_name: &str, intensity: f32) -> Actor {
        let entity = world.spawn();
        world.insert(entity, Transform::default());
        let mut actor = Actor::new(entity, actor_name);
        let slot_entity = world.spawn();
        let mut l = LightComponent::default();
        l.intensity = intensity;
        l.color     = [0.1, 0.2, 0.3];
        world.insert(slot_entity, l);
        actor.add_slot_typed::<LightComponent>(slot_name, ComponentKind::Light, slot_entity);
        actor
    }

    /// 組込コンポーネントのスカラーが解決できること。
    #[test]
    fn resolves_builtin_scalar() {
        let mut world = World::new();
        let actors = vec![light_actor(&mut world, "Sun", "MainLight", 4.5)];
        let t = parse_binding("Sun|MainLight|intensity").unwrap();
        assert_eq!(
            resolve_binding(&actors, &world, 0, &t, BindableValueType::F32),
            Some([4.5, 0.0, 0.0, 0.0]));
    }

    /// 型が違えば解決できないこと（成分の部分取り出しをしない契約）。
    #[test]
    fn rejects_type_mismatch() {
        let mut world = World::new();
        let actors = vec![light_actor(&mut world, "Sun", "MainLight", 4.5)];
        // 強度（f32）を vec3 として要求 → 不一致
        let t = parse_binding("Sun|MainLight|intensity").unwrap();
        assert_eq!(resolve_binding(&actors, &world, 0, &t, BindableValueType::Vec3), None);
        // 色（vec3）を f32 として要求 → 不一致
        let t = parse_binding("Sun|MainLight|color").unwrap();
        assert_eq!(resolve_binding(&actors, &world, 0, &t, BindableValueType::F32), None);
    }

    /// アクタのルート直付け（Transform）が解決できること。
    #[test]
    fn resolves_actor_root_transform() {
        let mut world = World::new();
        let entity = world.spawn();
        let mut tf = Transform::default();
        tf.position = [1.0, 2.0, 3.0];
        world.insert(entity, tf);
        let actors = vec![Actor::new(entity, "Player")];

        let t = parse_binding("Player|Transform|position").unwrap();
        assert_eq!(
            resolve_binding(&actors, &world, 0, &t, BindableValueType::Vec3),
            Some([1.0, 2.0, 3.0, 0.0]));
    }

    /// アクタ・スロット・変数のいずれが欠けても解決失敗になること。
    #[test]
    fn missing_pieces_fail_to_resolve() {
        let mut world = World::new();
        let actors = vec![light_actor(&mut world, "Sun", "MainLight", 4.5)];
        for text in ["NoActor|MainLight|intensity", "Sun|NoSlot|intensity", "Sun|MainLight|nope"] {
            let t = parse_binding(text).unwrap();
            assert_eq!(resolve_binding(&actors, &world, 0, &t, BindableValueType::F32), None,
                "{text} は解決できてはならない");
        }
    }

    /// 無効スロットは解決対象外（スロットのトグルでバインドを一時的に切れる）。
    #[test]
    fn disabled_slot_fails_to_resolve() {
        let mut world = World::new();
        let mut a = light_actor(&mut world, "Sun", "MainLight", 4.5);
        a.slots_mut()[0].enabled = false;
        let actors = vec![a];
        let t = parse_binding("Sun|MainLight|intensity").unwrap();
        assert_eq!(resolve_binding(&actors, &world, 0, &t, BindableValueType::F32), None);
    }

    /// 世界線が違うアクタは見えないこと。
    #[test]
    fn other_world_line_is_invisible() {
        let mut world = World::new();
        let mut a = light_actor(&mut world, "Sun", "MainLight", 4.5);
        a.world_line = 7;
        let actors = vec![a];
        let t = parse_binding("Sun|MainLight|intensity").unwrap();
        assert_eq!(resolve_binding(&actors, &world, 0, &t, BindableValueType::F32), None);
        assert!(resolve_binding(&actors, &world, 7, &t, BindableValueType::F32).is_some());
    }

    /// 名前検索は子孫まで見て、空名・別世界線は見つからないこと。
    #[test]
    fn find_actor_by_name_searches_descendants() {
        let mut world = World::new();
        let child_entity = world.spawn();
        let child = Actor::new(child_entity, "Deep");
        let parent_entity = world.spawn();
        let mut parent = Actor::new(parent_entity, "Root");
        parent.add_child(child);
        let actors = vec![parent];

        assert!(find_actor_by_name(&actors, 0, "Deep").is_some());
        assert!(find_actor_by_name(&actors, 0, "Root").is_some());
        assert!(find_actor_by_name(&actors, 0, "missing").is_none());
        assert!(find_actor_by_name(&actors, 0, "").is_none());
        assert!(find_actor_by_name(&actors, 7, "Deep").is_none());
    }
}
