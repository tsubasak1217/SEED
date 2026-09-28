// ============================================================
//  font/text_layout_cache.rs — キャンバスのテキストのレイアウトの使い回し（2 世代の表）
//
//  【なぜ要るか（2026-09-28。docs/app_platform_roadmap.md §3.9 の原因 2）】
//  UI の描画の積み込み（renderer/ui_draw_pass.rs の UiZoneDraw::build）は、毎フレーム見えている全テキストの
//  行分割・字の配置（canvas_text.rs の append_item → resolve_layout_with_images → layout_line）をやり直していた。
//  スクロール中も文字列・書体・大きさ・枠は変わらないので、前のフレームの結果をそのまま使える。
//
//  【何が同じなら使い回すか（キー）＝何が変わったら作り直すか】
//    - 書体（FontRegistry の ID。パス → ID は読み直さない＝同じパスは同じ実体）
//    - 展開済みの本文（記法・スロットの解決後の文字列。1 バイトでも違えば別）
//    - レイアウトの条件（TextLayoutSpec: 大きさ・行送り・揃え・縁取りの太さ・枠の幅と高さ・折り返し。
//      浮動小数はビット列で比べる。-0.0 と 0.0 も別扱い＝計算結果が 1 ビットでも変わりうるものは別）
//    - インライン画像の表（位置・パス・送り幅・高さ。送り幅が変われば行分割が変わる）
//  値（行分割・行ごとの字の配置）はこれらだけの純関数なので、キーが同じなら作り直しと 1 ビットも違わない。
//  字のアトラスの UV は一度入れた字では動かない（アトラスは追い出さない）ので、覚えた配置の UV も古くならない。
//  色・色区間・影・太さ・ピボット・行列（位置・スクロール）は**キーに入れない**（毎フレームの頂点の生成で使う。
//  使い回すのは行列を掛ける前の「キャンバスのローカルの配置」だけ）。
//
//  【捨て方（2 世代）】
//  フレームの頭に `advance_generation` を呼ぶと、前のフレームで一度も使われなかった値を捨てる
//  （今の世代 → 前の世代、前の前の世代は捨てる）。見えなくなったテキストの値は 2 フレームで消えるので、
//  表は「直近 2 フレームに描いたテキストの数」までしか大きくならない。
// ============================================================

use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::rc::Rc;

use crate::engine::core::fast_hash::{FastBuildHasher, FastHasher};

use super::inline::InlineImages;
use super::text_layout::TextLayoutSpec;

/// レイアウトの使い回しのキー（上の【何が同じなら使い回すか】）。値として表に持つ。
#[derive(Clone, Debug)]
struct TextLayoutKey {
    /// 書体の ID。
    font_id: u16,
    /// 展開済みの本文。
    text: String,
    /// レイアウトの条件。
    spec: TextLayoutSpec,
    /// インライン画像の表。
    images: InlineImages,
}

/// 表を引くための借用のキー（引くたびに文字列を複製しない）。
#[derive(Clone, Copy, Debug)]
pub struct TextLayoutProbe<'a> {
    /// 書体の ID。
    pub font_id: u16,
    /// 展開済みの本文。
    pub text: &'a str,
    /// レイアウトの条件。
    pub spec: &'a TextLayoutSpec,
    /// インライン画像の表。
    pub images: &'a InlineImages,
}

impl TextLayoutProbe<'_> {
    /// 表の振り分けに使う 64 ビットの要約（等しいキーは必ず同じ値。違うキーが同じ値になっても `matches` で見分ける）。
    fn digest(&self) -> u64 {
        let mut hasher = FastHasher::default();
        self.font_id.hash(&mut hasher);
        self.text.hash(&mut hasher);
        spec_bits(self.spec).hash(&mut hasher);
        self.images.len().hash(&mut hasher);
        for (offset, image) in self.images.iter_range(0..usize::MAX) {
            offset.hash(&mut hasher);
            image.path.hash(&mut hasher);
            image.advance_em.to_bits().hash(&mut hasher);
            image.height_em.to_bits().hash(&mut hasher);
        }
        hasher.finish()
    }

    /// 覚えたキーと同じか（浮動小数はビット列で比べる）。
    fn matches(&self, key: &TextLayoutKey) -> bool {
        self.font_id == key.font_id
            && self.text == key.text
            && spec_bits(self.spec) == spec_bits(&key.spec)
            && images_equal_bitwise(self.images, &key.images)
    }

    /// 表に持つキーへ写す（新しく覚えるときだけ文字列を複製する）。
    fn to_key(self) -> TextLayoutKey {
        TextLayoutKey {
            font_id: self.font_id,
            text: self.text.to_owned(),
            spec: *self.spec,
            images: self.images.clone(),
        }
    }
}

/// レイアウトの条件のビット列の組（大きさ・行送り・縁取り・枠の幅・枠の高さのビット列、揃え、縦の揃え、折り返し）。
type SpecBits = (
    [u32; 5],
    std::mem::Discriminant<crate::engine::components::TextAlign>,
    std::mem::Discriminant<crate::engine::components::TextVerticalAlign>,
    bool,
);

/// レイアウトの条件をビット列の組にする（比較と要約に使う）。
///
/// TextLayoutSpec の欄を足したら、ここにも足すこと（下の取り出しが全欄を列挙しているのでビルドが止まって気付ける）。
/// 揃えは列挙の判別値（`as u8` の並びに依存しないよう、判別値そのものを比べる）。
fn spec_bits(spec: &TextLayoutSpec) -> SpecBits {
    // `..` を使わずに全欄を取り出す（欄を足すとここでビルドが止まり、キーへの入れ忘れに気付ける）
    let TextLayoutSpec { font_size, line_spacing, align, vertical_align, outline_width, box_width, box_height, wrap } = *spec;
    (
        [
            font_size.to_bits(),
            line_spacing.to_bits(),
            outline_width.to_bits(),
            box_width.to_bits(),
            box_height.to_bits(),
        ],
        std::mem::discriminant(&align),
        std::mem::discriminant(&vertical_align),
        wrap,
    )
}

/// インライン画像の表がビット列として同じか。
fn images_equal_bitwise(a: &InlineImages, b: &InlineImages) -> bool {
    a.len() == b.len()
        && a.iter_range(0..usize::MAX).zip(b.iter_range(0..usize::MAX)).all(|((oa, ia), (ob, ib))| {
            oa == ob
                && ia.path == ib.path
                && ia.advance_em.to_bits() == ib.advance_em.to_bits()
                && ia.height_em.to_bits() == ib.height_em.to_bits()
        })
}

/// 1 つの振り分け先の中身（要約が同じだった値の並び。普通は 1 つ）。
type Bucket<V> = Vec<(TextLayoutKey, Rc<V>)>;

/// レイアウトの使い回しの表（2 世代）。`V` は覚える値（canvas_text.rs の行分割と字の配置）。
pub struct TextLayoutCache<V> {
    /// 今の世代（このフレームで使った・作った値）。要約は FastHasher で作ったものなので、表も速いハッシュで引く。
    current: HashMap<u64, Bucket<V>, FastBuildHasher>,
    /// 前の世代（前のフレームで使った値。このフレームで使えば今の世代へ移す）。
    previous: HashMap<u64, Bucket<V>, FastBuildHasher>,
    /// 使い回した回数（診断とテスト用。世代を進めても消さない）。
    hits: u64,
    /// 作った回数（同上）。
    misses: u64,
}

impl<V> Default for TextLayoutCache<V> {
    fn default() -> Self {
        Self { current: HashMap::default(), previous: HashMap::default(), hits: 0, misses: 0 }
    }
}

impl<V> TextLayoutCache<V> {
    /// 空の表を作る。
    pub fn new() -> Self {
        Self::default()
    }

    /// キーの値を返す。無ければ `build` で作って覚える（`build` が None なら覚えずに None）。
    pub fn get_or_insert_with(&mut self, probe: TextLayoutProbe<'_>, build: impl FnOnce() -> Option<V>) -> Option<Rc<V>> {
        let digest = probe.digest();
        // ① 今の世代にあればそのまま
        if let Some(value) = self.current.get(&digest).and_then(|b| find(b, &probe)) {
            self.hits += 1;
            return Some(value);
        }
        // ② 前の世代にあれば今の世代へ移して使う
        if let Some(bucket) = self.previous.get_mut(&digest) {
            if let Some(pos) = bucket.iter().position(|(key, _)| probe.matches(key)) {
                let entry = bucket.swap_remove(pos);
                let value = Rc::clone(&entry.1);
                self.current.entry(digest).or_default().push(entry);
                self.hits += 1;
                return Some(value);
            }
        }
        // ③ 作って覚える
        let value = Rc::new(build()?);
        self.misses += 1;
        self.current.entry(digest).or_default().push((probe.to_key(), Rc::clone(&value)));
        Some(value)
    }

    /// 世代を進める（フレームの頭に 1 回）。前のフレームで使わなかった値を捨てる。
    pub fn advance_generation(&mut self) {
        self.previous = std::mem::take(&mut self.current);
    }

    /// 覚えている値の数（2 世代の合計。診断とテスト用）。
    pub fn len(&self) -> usize {
        self.current.values().map(Vec::len).sum::<usize>() + self.previous.values().map(Vec::len).sum::<usize>()
    }

    /// 使い回した回数と作った回数（診断とテスト用）。
    pub fn stats(&self) -> (u64, u64) {
        (self.hits, self.misses)
    }
}

/// 振り分け先からキーの値を探す。
fn find<V>(bucket: &Bucket<V>, probe: &TextLayoutProbe<'_>) -> Option<Rc<V>> {
    bucket.iter().find(|(key, _)| probe.matches(key)).map(|(_, value)| Rc::clone(value))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::components::{TextAlign, TextVerticalAlign};
    use crate::engine::core::font::inline::doc::InlineImage;

    fn spec(font_size: f32) -> TextLayoutSpec {
        TextLayoutSpec { font_size, ..TextLayoutSpec::default() }
    }

    fn probe<'a>(text: &'a str, spec: &'a TextLayoutSpec, images: &'a InlineImages) -> TextLayoutProbe<'a> {
        TextLayoutProbe { font_id: 0, text, spec, images }
    }

    /// 同じキーは 2 回目から作らない（値は同じもの）。
    #[test]
    fn same_key_is_reused() {
        let mut cache: TextLayoutCache<String> = TextLayoutCache::new();
        let s = spec(24.0);
        let images = InlineImages::default();
        let mut built = 0;
        for _ in 0..3 {
            let v = cache.get_or_insert_with(probe("こんにちは", &s, &images), || {
                built += 1;
                Some("layout".to_owned())
            });
            assert_eq!(v.as_deref().map(String::as_str), Some("layout"));
        }
        assert_eq!(built, 1);
        assert_eq!(cache.stats(), (2, 1));
    }

    /// 本文・書体・大きさ・揃え・枠・折り返し・画像のどれかが違えば別の値として作る（古い配置を使わない）。
    #[test]
    fn any_layout_input_change_rebuilds() {
        let mut cache: TextLayoutCache<u32> = TextLayoutCache::new();
        let base = spec(24.0);
        let images = InlineImages::default();
        let with_image = InlineImages::from_entries(vec![(
            0,
            InlineImage { path: "assets://icon.png".into(), advance_em: 1.0, height_em: 1.0 },
        )]);
        let mut variants: Vec<(u16, &str, TextLayoutSpec, &InlineImages)> = vec![
            (0, "abc", base, &images),
            (0, "abd", base, &images),
            (1, "abc", base, &images),
            (0, "abc", spec(25.0), &images),
            (0, "abc", TextLayoutSpec { align: TextAlign::Center, ..base }, &images),
            (0, "abc", TextLayoutSpec { vertical_align: TextVerticalAlign::Middle, ..base }, &images),
            (0, "abc", TextLayoutSpec { box_width: 100.0, ..base }, &images),
            (0, "abc", TextLayoutSpec { box_width: 100.0, wrap: true, ..base }, &images),
            (0, "abc", TextLayoutSpec { outline_width: 2.0, ..base }, &images),
            (0, "abc", TextLayoutSpec { line_spacing: 1.5, ..base }, &images),
            (0, "abc", base, &with_image),
        ];
        // -0.0 と 0.0 も別扱い（計算結果が変わりうるものは使い回さない）
        variants.push((0, "abc", TextLayoutSpec { box_height: -0.0, ..base }, &images));
        for (i, (font_id, text, s, imgs)) in variants.iter().enumerate() {
            let p = TextLayoutProbe { font_id: *font_id, text, spec: s, images: imgs };
            let v = cache.get_or_insert_with(p, || Some(i as u32)).unwrap();
            assert_eq!(*v, i as u32, "変種 {i} が前の値を使い回した");
        }
        assert_eq!(cache.stats().1, variants.len() as u64);
    }

    /// 前のフレームで使わなかった値は世代を 2 回進めると消え、使った値は残る。
    #[test]
    fn unused_values_are_dropped_after_two_generations() {
        let mut cache: TextLayoutCache<u32> = TextLayoutCache::new();
        let s = spec(24.0);
        let images = InlineImages::default();
        cache.get_or_insert_with(probe("keep", &s, &images), || Some(1));
        cache.get_or_insert_with(probe("drop", &s, &images), || Some(2));
        cache.advance_generation();
        // 次のフレームは keep だけを描く
        cache.get_or_insert_with(probe("keep", &s, &images), || panic!("前の世代から使い回すはず"));
        cache.advance_generation();
        assert_eq!(cache.len(), 1, "drop は 2 世代で消える");
        let mut rebuilt = false;
        cache.get_or_insert_with(probe("drop", &s, &images), || {
            rebuilt = true;
            Some(2)
        });
        assert!(rebuilt, "消えた値は作り直す");
    }

    /// 作れなかった（None）ものは覚えない。
    #[test]
    fn failed_builds_are_not_cached() {
        let mut cache: TextLayoutCache<u32> = TextLayoutCache::new();
        let s = spec(0.0);
        let images = InlineImages::default();
        assert!(cache.get_or_insert_with(probe("", &s, &images), || None).is_none());
        assert_eq!(cache.len(), 0);
    }
}
