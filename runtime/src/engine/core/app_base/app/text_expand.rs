// ============================================================
//  text_expand.rs — TextComponent の「展開結果」フレーム内キャッシュ
//
//  【役割】
//  本文 + スロット値 → 展開済みドキュメント（`InlineDoc`）と色区間表
//  （`ColorRuns`）への変換を **1 フレームにつき 1 回**へまとめる。
//
//  【なぜ要るのか】
//  展開結果は 1 フレームのうち 3 か所で必要になる:
//    ① 選択枠・ピックの実測（`canvas_text_bounds`）
//    ② インライン画像のスプライト収集（`canvas_collect`）
//    ③ グリフの頂点生成（`font::canvas_text::append_item`）
//  以前は 3 か所がそれぞれ `build_doc` を呼んでいたため、
//  記法解析・アイコンセット参照・画像寸法解決が 1 テキストにつき 3 回走っていた。
//  スロットが増えると解析コストも増えるので、ここで 1 回にまとめる。
//
//  【なぜ引数で配らずスレッドローカルなのか】
//  ② の収集関数（`collect_sprite_items`）は既に 17 引数を持つ再帰関数で、
//  さらに DFS の途中から呼ばれる 4 本の関数へ表を配ると、
//  「テキストの都合」でキャンバス収集の全シグネチャが動くことになる。
//  展開結果は**描画スレッド内で 1 フレームだけ生きる純粋なキャッシュ**なので、
//  スレッドローカルに置き、参照側は `expanded_for` の 1 関数だけを見る。
//  キャッシュに無いエンティティは `expanded_for` がその場で展開して埋めるため、
//  「事前構築を呼び忘れると描画が消える」という壊れ方はしない（遅くなるだけ）。
//
//  【ハッシュによる再構築の省略】
//  入力（本文・アイコンセット・スロット・解決済みの値）のハッシュが前回と
//  同じなら、展開結果をそのまま使い回す。HUD の大半は毎フレーム同じ文字列なので、
//  実際にはほとんどのフレームで解析が 0 回になる。
// ============================================================

use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::hash::{Hash, Hasher};
use std::rc::Rc;

use crate::engine::components::text_slots::{SlotValue, TextSlotData, TextSlotKind};
use crate::engine::components::{ComponentKind, TextComponent};
use crate::engine::core::font::inline::color_runs::ColorRuns;
use crate::engine::core::font::inline::doc::{InlineDoc, build_doc_with_slots};
use crate::engine::ecs::{Entity, World};
use crate::engine::structs::objects::Actor;

use super::App;

// ─── 展開結果 ─────────────────────────────────────────────────

/// 1 つの TextComponent の展開結果。
pub(super) struct ExpandedText {
    /// レイアウト・描画へ渡す展開済み本文（画像は代替文字へ潰れている）。
    pub doc: InlineDoc,
    /// 本文に対する色付き区間表（空 = 単色）。
    pub runs: ColorRuns,
    /// 展開の入力から作ったハッシュ（同じなら再構築を省く）。
    hash: u64,
}

/// Text スロット entity → 展開結果。
type TextExpandMap = HashMap<Entity, Rc<ExpandedText>>;

thread_local! {
    /// 描画スレッド内の展開キャッシュ（フレームをまたいで持ち越し、
    /// ハッシュ一致なら再利用する）。
    static EXPAND_CACHE: RefCell<TextExpandMap> = RefCell::new(TextExpandMap::new());
}

// ─── バインド値の解決口 ───────────────────────────────────────

/// スロットのバインド先から実値を解決する口。
///
/// **後続実装（スクリプト／組込コンポーネントのバインド解決）の差し込み口は
/// ここ 1 か所である。** 新しい解決器を足すときは、この trait を実装した型を
/// `current_provider()` が返すようにするだけでよい（展開器・描画・IPC は
/// いずれも `SlotValue` しか見ないので、他を触る必要は無い）。
pub(super) trait SlotValueProvider {
    /// バインド文字列（"アクタ名|スロット名|変数名"）から実値を引く。
    ///
    /// 解決できない（未設定・アクタが無い・型違い）場合は `None` を返し、
    /// 呼び出し側はスロット自身のフォールバック値を使う。
    fn resolve(&self, bind: &str, kind: TextSlotKind) -> Option<SlotValue>;
}

/// 何も解決しない既定の供給器（＝常にフォールバック値を使う）。
///
/// バインドの実解決は後続タスクで差し込む。ここを空実装のままにしておくことで、
/// 「バインド未設定と解決失敗が同じ挙動になる」ことを構造的に保証している。
pub(super) struct FallbackValueProvider;

impl SlotValueProvider for FallbackValueProvider {
    fn resolve(&self, _bind: &str, _kind: TextSlotKind) -> Option<SlotValue> {
        None
    }
}

/// 現在有効な供給器を返す（**差し込み口はこの 1 関数**）。
#[inline]
fn current_provider() -> impl SlotValueProvider {
    FallbackValueProvider
}

/// バインド文字列が解決できるか（インスペクタの警告表示用）。
///
/// 展開と同じ供給器を通すので、「インスペクタは緑なのに表示はフォールバック」
/// という食い違いが起きない。
pub(super) fn slot_bind_resolves(bind: &str, kind: TextSlotKind) -> bool {
    !bind.is_empty() && current_provider().resolve(bind, kind).is_some()
}

// ─── 展開 ─────────────────────────────────────────────────────

/// スロット配列を解決済みの値へ変換する。
///
/// バインドが解決できたらその値、できなければスロット自身のフォールバック値。
fn resolve_values(slots: &[TextSlotData], provider: &impl SlotValueProvider) -> Vec<SlotValue> {
    slots
        .iter()
        .map(|s| {
            provider
                .resolve(&s.bind, s.kind)
                .unwrap_or_else(|| s.fallback_value())
        })
        .collect()
}

/// 展開の入力からハッシュを作る（同値なら再構築を省くための鍵）。
///
/// f32 はビットパターンで混ぜる（NaN を含んでも安定した鍵になる）。
fn expand_hash(
    content: &str,
    icon_set: &str,
    slots: &[TextSlotData],
    values: &[SlotValue],
) -> u64 {
    let mut h = std::collections::hash_map::DefaultHasher::new();
    content.hash(&mut h);
    icon_set.hash(&mut h);
    slots.len().hash(&mut h);
    for s in slots {
        s.kind.hash(&mut h);
        s.path.hash(&mut h);
        for c in s.rgba {
            c.to_bits().hash(&mut h);
        }
        // bind / text / num は values 側にも効くが、書式の再マッピング判定にも
        // 使うのでそのまま鍵へ入れる（取りこぼしより余分な再構築のほうが安全）。
        s.bind.hash(&mut h);
    }
    for v in values {
        match v {
            SlotValue::Str(s) => {
                0u8.hash(&mut h);
                s.hash(&mut h);
            }
            SlotValue::Num(n) => {
                1u8.hash(&mut h);
                n.to_bits().hash(&mut h);
            }
        }
    }
    h.finish()
}

/// 指定 Text スロットの展開結果を得る（キャッシュ優先・無ければその場で展開）。
///
/// 描画・実測・画像収集はすべてこの 1 関数を通ること
/// （3 経路が別々に展開すると、位置と色が食い違う経路が生まれる）。
pub(super) fn expanded_for(entity: Entity, tc: &TextComponent) -> Rc<ExpandedText> {
    let values = resolve_values(&tc.slots, &current_provider());
    let hash = expand_hash(&tc.content, &tc.icon_set, &tc.slots, &values);
    EXPAND_CACHE.with(|cache| {
        let mut map = cache.borrow_mut();
        if let Some(found) = map.get(&entity)
            && found.hash == hash
        {
            return found.clone();
        }
        let (doc, runs) = build_doc_with_slots(&tc.content, &tc.icon_set, &tc.slots, &values);
        let entry = Rc::new(ExpandedText { doc, runs, hash });
        map.insert(entity, entry.clone());
        entry
    })
}

/// キャッシュを丸ごと捨てる（アセットのホットリロード・シーン切り替え用）。
///
/// アイコンセットや画像の差し替えは入力ハッシュに現れないため、
/// アセットのホットリロードを実装するときは `inline::invalidate_caches` と
/// 対で呼ぶこと（現状はどちらも起動中に呼ばれる経路が無い）。
pub(super) fn invalidate_text_expand_cache() {
    EXPAND_CACHE.with(|cache| cache.borrow_mut().clear());
}

impl App {
    /// アクティブ世界線の全 TextComponent を展開し、キャッシュを最新にする。
    ///
    /// 実測（`build_text_bounds_map`）と描画収集より**前**に 1 回だけ呼ぶこと。
    /// 呼ばなくても表示は壊れない（`expanded_for` がその場で展開する）が、
    /// 1 フレームに同じ展開が複数回走る。
    pub(super) fn build_text_expand_map(&self) {
        let Some(scene) = self.scene.as_ref() else {
            invalidate_text_expand_cache();
            return;
        };
        let wl = self.active_world_line;
        let mut live: HashSet<Entity> = HashSet::new();
        for actor in scene.actors.iter().filter(|a| a.world_line == wl) {
            warm_text_slots(actor, &scene.world, &mut live);
        }
        // 消えた（別世界線へ移った・削除された）テキストの結果は捨てる。
        EXPAND_CACHE.with(|cache| cache.borrow_mut().retain(|e, _| live.contains(e)));
    }
}

/// アクタとその子孫の Text スロットを展開し、生存エンティティを記録する。
///
/// 無効スロット（`enabled = false`）も対象にする。エディタでは非表示のものも
/// 選択できる規約（`canvas_text_bounds` と同じ）に合わせるため。
fn warm_text_slots(actor: &Actor, world: &World, live: &mut HashSet<Entity>) {
    for slot in actor.slots() {
        if slot.kind != ComponentKind::Text {
            continue;
        }
        let Some(tc) = world.get::<TextComponent>(slot.entity) else {
            continue;
        };
        expanded_for(slot.entity, tc);
        live.insert(slot.entity);
    }
    for child in actor.children() {
        warm_text_slots(child, world, live);
    }
}

// ============================================================
//  単体テスト
//
//  App（GPU・シーン）を要さない部分だけを検証する。
//  キャッシュはスレッドローカルなので、テストごとに別エンティティを使う。
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::text_slots::TextSlotData;

    /// テスト用の Text コンポーネントを組む。
    fn text_with(content: &str, slots: Vec<TextSlotData>) -> TextComponent {
        let mut tc = TextComponent::default();
        tc.content = content.to_string();
        tc.slots = slots;
        tc
    }

    /// 入力が同じなら同じ実体（Rc）が返る＝再展開されない。
    #[test]
    fn same_input_reuses_the_cached_expansion() {
        invalidate_text_expand_cache();
        let e = Entity::from_raw(9001, 0);
        let tc = text_with("所持金 {num} 円", vec![TextSlotData::new_of_kind(TextSlotKind::Num)]);
        let a = expanded_for(e, &tc);
        let b = expanded_for(e, &tc);
        assert!(Rc::ptr_eq(&a, &b), "ハッシュ一致なら再構築しない");
    }

    /// 本文が変わればハッシュが変わり、展開し直される。
    #[test]
    fn content_change_invalidates() {
        invalidate_text_expand_cache();
        let e = Entity::from_raw(9002, 0);
        let a = expanded_for(e, &text_with("あ", Vec::new()));
        let b = expanded_for(e, &text_with("い", Vec::new()));
        assert!(!Rc::ptr_eq(&a, &b));
        assert_eq!(b.doc.text, "い");
    }

    /// スロットの値が変わればハッシュが変わり、表示も変わる。
    #[test]
    fn slot_value_change_invalidates() {
        invalidate_text_expand_cache();
        let e = Entity::from_raw(9003, 0);
        let mut slot = TextSlotData::new_of_kind(TextSlotKind::Num);
        slot.num = 1.0;
        let a = expanded_for(e, &text_with("{num}", vec![slot.clone()]));
        assert_eq!(a.doc.text, "1");
        slot.num = 2.0;
        let b = expanded_for(e, &text_with("{num}", vec![slot]));
        assert!(!Rc::ptr_eq(&a, &b));
        assert_eq!(b.doc.text, "2");
    }

    /// 既定の供給器では、バインドが書かれていても解決されない
    /// （＝フォールバック値が使われる。後続タスクの差し込み前提の確認）。
    #[test]
    fn fallback_provider_never_resolves() {
        assert!(!slot_bind_resolves("Player|Status|hp", TextSlotKind::Num));
        assert!(!slot_bind_resolves("", TextSlotKind::Num));
        invalidate_text_expand_cache();
        let e = Entity::from_raw(9004, 0);
        let mut slot = TextSlotData::new_of_kind(TextSlotKind::Num);
        slot.bind = "Player|Status|hp".into();
        slot.num = 5.0;
        assert_eq!(expanded_for(e, &text_with("{num}", vec![slot])).doc.text, "5");
    }
}
