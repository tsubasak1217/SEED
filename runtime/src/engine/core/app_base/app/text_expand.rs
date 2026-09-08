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
use crate::engine::core::font::inline;
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
/// **解決器の差し込み口はここ 1 か所である。** 新しい解決器を足すときは、
/// この trait を実装した型を `current_provider()` が返すようにするだけでよい
/// （展開器・描画・IPC はいずれも `SlotValue` しか見ないので、他を触る必要は無い）。
pub(super) trait SlotValueProvider {
    /// バインド文字列（"アクタ名|スロット名|変数名"）から実値を引く。
    ///
    /// 解決できない（未設定・アクタが無い・型違い）場合は `None` を返し、
    /// 呼び出し側はスロット自身のフォールバック値を使う。
    fn resolve(&self, bind: &str, kind: TextSlotKind) -> Option<SlotValue>;
}

thread_local! {
    /// このフレームで解決済みの**数値**バインド（バインド文字列 → 値）。
    static BOUND_NUMS: RefCell<HashMap<String, f32>> = RefCell::new(HashMap::new());
    /// このフレームで解決済みの**文字列**バインド（バインド文字列 → 値）。
    static BOUND_STRS: RefCell<HashMap<String, String>> = RefCell::new(HashMap::new());
}

/// 解決済みバインド表を引く供給器（実運用の供給器）。
///
/// ## なぜ「その場で解決」ではなく表引きなのか
/// 展開の入口（`expanded_for`）は描画・実測・画像収集の 3 経路から呼ばれ、
/// そのうち 2 経路は既にシーンを不変借用した内側に居るため、
/// ここから Actor ツリー・World へ触りに行くとシグネチャが芋づるに動く。
/// また 3 経路が別々のタイミングで解決すると、
/// **同じフレーム内で値が変わって展開キャッシュが毎回作り直される**。
/// そこで「フレームの頭で 1 回だけ解決して表にする」方式にしてある
/// （表を作るのは `App::build_text_expand_map` の先頭 = シーンを触れる場所）。
pub(super) struct ResolvedValueProvider;

impl SlotValueProvider for ResolvedValueProvider {
    fn resolve(&self, bind: &str, kind: TextSlotKind) -> Option<SlotValue> {
        if bind.is_empty() { return None; }
        match kind {
            // 数値スロットは f32 の供給値だけを受け取る。
            TextSlotKind::Num =>
                BOUND_NUMS.with(|m| m.borrow().get(bind).copied()).map(SlotValue::Num),
            // 文字列スロットは str の供給値だけを受け取る。
            TextSlotKind::String =>
                BOUND_STRS.with(|m| m.borrow().get(bind).cloned()).map(SlotValue::Str),
            // 画像・色スロットは差し込む「値」を持たない（パス／RGBA は
            // スロット自身のフィールドが正典）。バインドの対象外。
            TextSlotKind::Image | TextSlotKind::Color => None,
        }
    }
}

/// 現在有効な供給器を返す（**差し込み口はこの 1 関数**）。
#[inline]
fn current_provider() -> impl SlotValueProvider {
    ResolvedValueProvider
}

/// このフレームぶんのバインド解決表を作り直す（**フレームに 1 回**）。
///
/// 対象は「アクティブ世界線の全 Text スロットに書かれたバインド文字列」だけで、
/// 同じバインド文字列は何個のスロットから指されていても **1 回しか解決しない**
/// （スクリプトの `[Bindable]` メソッド呼び出しがスロット数ぶん走るのを防ぐ）。
///
/// 解決できなかったバインドは表に載せない ＝ 展開時にスロットの
/// フォールバック値が使われる（「未設定」と「解決失敗」が同じ挙動になる契約）。
fn refresh_bound_values(actors: &[Actor], world: &World, world_line: u32) {
    use crate::engine::binding::resolve::{parse_binding, resolve_binding, resolve_binding_str};
    use crate::engine::binding::catalog::BindableValueType;

    // ── ① 解決すべきバインドを集める（種類ごとに重複排除）──
    let mut want_nums: HashSet<String> = HashSet::new();
    let mut want_strs: HashSet<String> = HashSet::new();
    /// アクタとその子孫の Text スロットからバインド文字列を集める。
    fn collect(
        actor: &Actor,
        world: &World,
        nums:  &mut HashSet<String>,
        strs:  &mut HashSet<String>,
    ) {
        for slot in actor.slots() {
            if slot.kind != ComponentKind::Text { continue; }
            let Some(tc) = world.get::<TextComponent>(slot.entity) else { continue };
            for s in &tc.slots {
                if s.bind.is_empty() { continue; }
                match s.kind {
                    TextSlotKind::Num    => { nums.insert(s.bind.clone()); }
                    TextSlotKind::String => { strs.insert(s.bind.clone()); }
                    TextSlotKind::Image | TextSlotKind::Color => {}
                }
            }
        }
        for child in actor.children() {
            collect(child, world, nums, strs);
        }
    }
    for actor in actors.iter().filter(|a| a.world_line == world_line) {
        collect(actor, world, &mut want_nums, &mut want_strs);
    }

    // ── ② 実際に解決して表を作り直す（前フレームの結果は捨てる）──
    BOUND_NUMS.with(|m| {
        let mut map = m.borrow_mut();
        map.clear();
        for bind in want_nums {
            let Some(target) = parse_binding(&bind) else { continue };
            if let Some(v) =
                resolve_binding(actors, world, world_line, &target, BindableValueType::F32)
            {
                // スカラーは第 1 成分のみ（`pack_scalar` の逆）。
                map.insert(bind, v[0]);
            }
        }
    });
    BOUND_STRS.with(|m| {
        let mut map = m.borrow_mut();
        map.clear();
        for bind in want_strs {
            let Some(target) = parse_binding(&bind) else { continue };
            if let Some(v) = resolve_binding_str(actors, world, world_line, &target) {
                map.insert(bind, v);
            }
        }
    });
}

/// バインド解決表を空にする（シーンが無いとき・テスト用）。
fn clear_bound_values() {
    BOUND_NUMS.with(|m| m.borrow_mut().clear());
    BOUND_STRS.with(|m| m.borrow_mut().clear());
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
        // ── `.icons` / 画像寸法のライブ編集を確認する（フレーム頭で 1 回）──
        // 実際のディスク確認は `ICON_SET_POLL_INTERVAL` に間引かれるため、
        // 毎フレーム呼んでもコストは無視できる。変化があった場合のみ、
        // 本文の展開結果キャッシュ（ハッシュに `.icons` の中身を含まない）を
        // 明示的に破棄しないと、直した内容が永久に反映されないままになる。
        if inline::poll_asset_changes() {
            invalidate_text_expand_cache();
        }
        let Some(scene) = self.scene.as_ref() else {
            invalidate_text_expand_cache();
            clear_bound_values();
            return;
        };
        let wl = self.active_world_line;
        // 展開より**先に**バインドを解決して表にする。
        // ここがシーン（Actor ツリー・World）へ触れる唯一の場所であり、
        // 以降の展開・描画・実測は表引きだけで済む。
        refresh_bound_values(&scene.actors, &scene.world, wl);
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

    /// 解決表が空なら、バインドが書かれていてもフォールバック値が使われること。
    #[test]
    fn unresolved_binding_falls_back() {
        clear_bound_values();
        assert!(!slot_bind_resolves("Player|Status|hp", TextSlotKind::Num));
        assert!(!slot_bind_resolves("", TextSlotKind::Num));
        invalidate_text_expand_cache();
        let e = Entity::from_raw(9004, 0);
        let mut slot = TextSlotData::new_of_kind(TextSlotKind::Num);
        slot.bind = "Player|Status|hp".into();
        slot.num = 5.0;
        assert_eq!(expanded_for(e, &text_with("{num}", vec![slot])).doc.text, "5");
    }

    /// 解決表に値があれば、フォールバック値ではなくそちらが差し込まれること（数値）。
    #[test]
    fn resolved_number_binding_wins_over_fallback() {
        clear_bound_values();
        BOUND_NUMS.with(|m| m.borrow_mut().insert("Player|Status|hp".into(), 42.0));
        assert!(slot_bind_resolves("Player|Status|hp", TextSlotKind::Num));

        invalidate_text_expand_cache();
        let e = Entity::from_raw(9005, 0);
        let mut slot = TextSlotData::new_of_kind(TextSlotKind::Num);
        slot.bind = "Player|Status|hp".into();
        slot.num  = 5.0; // フォールバック値（使われないはず）
        assert_eq!(expanded_for(e, &text_with("{num}", vec![slot])).doc.text, "42");
        clear_bound_values();
    }

    /// 解決表に値があれば、フォールバック文字列ではなくそちらが差し込まれること。
    #[test]
    fn resolved_string_binding_wins_over_fallback() {
        clear_bound_values();
        BOUND_STRS.with(|m| m.borrow_mut().insert("Player|Info|Title".into(), "勇者".into()));
        assert!(slot_bind_resolves("Player|Info|Title", TextSlotKind::String));

        invalidate_text_expand_cache();
        let e = Entity::from_raw(9006, 0);
        let mut slot = TextSlotData::new_of_kind(TextSlotKind::String);
        slot.bind = "Player|Info|Title".into();
        slot.text = "名無し".into(); // フォールバック値（使われないはず）
        assert_eq!(expanded_for(e, &text_with("{string}", vec![slot])).doc.text, "勇者");
        clear_bound_values();
    }

    /// 種類が違えば解決しないこと（数値の表に文字列スロットが繋がらない）。
    #[test]
    fn binding_tables_are_separated_by_slot_kind() {
        clear_bound_values();
        BOUND_NUMS.with(|m| m.borrow_mut().insert("A|B|c".into(), 1.0));
        assert!(slot_bind_resolves("A|B|c", TextSlotKind::Num));
        assert!(!slot_bind_resolves("A|B|c", TextSlotKind::String), "数値の表は文字列へ流れない");
        // 画像・色スロットはバインドの対象外。
        assert!(!slot_bind_resolves("A|B|c", TextSlotKind::Image));
        assert!(!slot_bind_resolves("A|B|c", TextSlotKind::Color));
        clear_bound_values();
    }

    /// 空文字列の解決は「成功して空」であり、フォールバックへ落ちないこと。
    #[test]
    fn resolved_empty_string_is_not_a_failure() {
        clear_bound_values();
        BOUND_STRS.with(|m| m.borrow_mut().insert("A|B|c".into(), String::new()));
        invalidate_text_expand_cache();
        let e = Entity::from_raw(9007, 0);
        let mut slot = TextSlotData::new_of_kind(TextSlotKind::String);
        slot.bind = "A|B|c".into();
        slot.text = "落ちてはいけない".into();
        assert_eq!(expanded_for(e, &text_with("[{string}]", vec![slot])).doc.text, "[]");
        clear_bound_values();
    }
}
