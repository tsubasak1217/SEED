// ============================================================
//  font/atlas_pages.rs — グリフアトラスのページ（同じ大きさの層）への棚詰めの配置（純粋な計算。wgpu を使わない）
//
//  【役割】
//  グリフアトラス（atlas.rs の GlyphAtlas）は GPU では `texture_2d_array`（1 層 = 1 ページ）で、
//  どのページのどこへ字を置くかをここで決める。テクスチャ・CPU のバッファは持たない（atlas.rs が持つ）ので、
//  配置の規則（棚詰め・ページの追加・上限・使用率）を wgpu 無しで単体テストできる。
//
//  【棚詰め（shelf packing）】
//  ページの上から「棚（段）」を積み、棚の中は左から右へ詰める。字は「高さが足りて右に空きのある最初の棚」へ入り、
//  どの棚にも入らなければ最後の棚の下に新しい棚を作る（棚の高さ = 最初に入った字の高さ）。2026-10-02 までの
//  1 枚のアトラスと同じ規則を、ページごとに持つ。
//
//  【ページの追加と上限（docs/ui_components.md §12.15）】
//  新しい字は **いちばん新しいページ**（最後に足した・最後に空にしたページ。`alloc`）へだけ置く。入らなければ上限（`max_pages`）まで
//  ページを 1 枚足してそこへ置く。上限なら None（呼び出し側が追い出しを試す）。追い出せるページも無いときの最後の手段として、
//  古いページの棚の隙間へ置く（`alloc_in_gaps`）。古いページへ今の字をまばらに入れると、そのページがいつまでも「使われている」
//  ことになって追い出せなくなるため、隙間は最後に使う（上限まで使ったときの詰まり具合は前から順に詰めるのと同じ）。
//
//  【ページの世代と追い出し（2026-10-03）】
//  ページごとに「最後に使われたフレーム」（`touch`）を持つ。上限まで満杯のとき、呼び出し側は
//  `least_recently_used_idle_page` で **一定のフレーム数（`min_idle_frames`）以上使われていないページのうち最も古いもの** を選び、
//  `clear_page` で丸ごと空にして使い直す（グリフ単位の穴の再利用はしない）。今のフレームや直前に使ったページは選ばない
//  （そのフレームの頂点が読む・使い回しのレイアウトが覚えている）。どのページも使われていれば追い出さない（入れ替えの往復を作らない）。
// ============================================================

/// 配置の結果（どのページの、左上のテクセルの位置）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct AtlasSlot {
    /// ページの番号（0 起点。テクスチャの層の番号と同じ）。
    pub page: u32,
    /// 左上の x（テクセル）。
    pub x: u32,
    /// 左上の y（テクセル）。
    pub y: u32,
}

/// 棚 1 段（左から順に字を詰める）。
#[derive(Clone, Copy, Debug)]
struct Shelf {
    /// 棚の上端の y（テクセル）。
    y: u32,
    /// 棚の高さ（最初に入った字の高さ。テクセル）。
    height: u32,
    /// 次に置く字の左端の x（テクセル）。
    cursor: u32,
}

/// 1 ページの棚の並び。
#[derive(Clone, Debug, Default)]
struct PageShelves {
    /// 上から積んだ棚。
    shelves: Vec<Shelf>,
    /// このページに置いた字の数。
    glyphs: u32,
    /// 置いた字の面積の合計（テクセル²。使用率の計測用）。
    used_area: u64,
    /// 最後に使われた（字を置いた・描いた）フレーム（追い出しの順）。
    last_used_frame: u64,
}

impl PageShelves {
    /// 幅 `w`・高さ `h` の矩形を置く場所を探す（一辺 `size` のページ）。置けなければ None。
    fn alloc(&mut self, w: u32, h: u32, size: u32) -> Option<(u32, u32)> {
        // ── 1. 既存の棚: 高さが足り、右に空きのある最初の棚 ──
        let found = self
            .shelves
            .iter_mut()
            .find(|shelf| shelf.height >= h && shelf.cursor + w <= size)
            .map(|shelf| {
                let x = shelf.cursor;
                shelf.cursor += w;
                (x, shelf.y)
            });
        let placed = match found {
            Some(pos) => Some(pos),
            None => {
                // ── 2. 最後の棚の下に新しい棚を作る（ページの下端を越えるなら置けない）──
                let new_y = self.bottom();
                if w > size || new_y + h > size {
                    None
                } else {
                    self.shelves.push(Shelf { y: new_y, height: h, cursor: w });
                    Some((0, new_y))
                }
            }
        };
        if placed.is_some() {
            self.glyphs += 1;
            self.used_area += u64::from(w) * u64::from(h);
        }
        placed
    }

    /// 使った高さ（最後の棚の下端。テクセル）。
    fn bottom(&self) -> u32 {
        self.shelves.last().map(|s| s.y + s.height).unwrap_or(0)
    }
}

/// ページの状態の写し（ログ・計測用）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PageUsage {
    /// 置いた字の数。
    pub glyphs: u32,
    /// 使った高さの割合（0..1。最後の棚の下端 ÷ 一辺。これが 1 に近いと新しい棚が作れない）。
    pub height_fraction: f32,
    /// 置いた字の面積の割合（0..1。字の矩形の合計 ÷ ページの面積。棚の隙間は数えない）。
    pub area_fraction: f32,
}

/// グリフアトラスのページへの配置（ページ = 一辺 `page_size` の正方形。上限 `max_pages` 枚）。
#[derive(Clone, Debug)]
pub struct AtlasPageAllocator {
    /// 1 ページの一辺（テクセル）。
    page_size: u32,
    /// ページの上限（1 以上）。
    max_pages: u32,
    /// 今あるページ（最初の 1 枚は作ったときからある）。
    pages: Vec<PageShelves>,
    /// いちばん新しいページ（新しい字を置くページ。最後に足した・最後に空にしたページ）。
    newest: u32,
}

impl AtlasPageAllocator {
    /// 一辺 `page_size`・上限 `max_pages` 枚（0 は 1 枚として扱う）の配置を作る。最初のページを 1 枚持って始める。
    pub fn new(page_size: u32, max_pages: u32) -> Self {
        Self { page_size, max_pages: max_pages.max(1), pages: vec![PageShelves::default()], newest: 0 }
    }

    /// 幅 `w`・高さ `h`（テクセル。字の間の隙間込み）の矩形を置く。
    ///
    /// いちばん新しいページへ置き、入らなければ上限までページを 1 枚足してそこへ置く（足したページがいちばん新しくなる）。
    /// 上限（またはページより大きい矩形）なら None（呼び出し側は追い出しか `alloc_in_gaps` を試す）。
    pub fn alloc(&mut self, w: u32, h: u32) -> Option<AtlasSlot> {
        let size = self.page_size;
        // ── 1. いちばん新しいページ ──
        let newest = self.newest;
        if let Some((x, y)) = self.pages[newest as usize].alloc(w, h, size) {
            return Some(AtlasSlot { page: newest, x, y });
        }
        // ── 2. 上限までならページを足す（ページより大きい矩形は足しても入らないので足さない）──
        if self.page_count() >= self.max_pages || w > size || h > size {
            return None;
        }
        let mut fresh = PageShelves::default();
        let (x, y) = fresh.alloc(w, h, size)?;
        self.pages.push(fresh);
        self.newest = self.page_count() - 1;
        Some(AtlasSlot { page: self.newest, x, y })
    }

    /// 最後の手段: いちばん新しいページ以外の、古いページの棚の隙間へ置く（前のページから）。入らなければ None。
    ///
    /// 置いたページは今の字を持つので追い出しにくくなる。追い出せるページが無いときだけ使う。
    pub fn alloc_in_gaps(&mut self, w: u32, h: u32) -> Option<AtlasSlot> {
        let size = self.page_size;
        let newest = self.newest as usize;
        self.pages
            .iter_mut()
            .enumerate()
            .filter(|(page, _)| *page != newest)
            .find_map(|(page, shelves)| shelves.alloc(w, h, size).map(|(x, y)| AtlasSlot { page: page as u32, x, y }))
    }

    /// いちばん新しいページ（新しい字を置くページ）。
    pub fn newest_page(&self) -> u32 {
        self.newest
    }

    /// 今あるページの数。
    pub fn page_count(&self) -> u32 {
        self.pages.len() as u32
    }

    /// ページの上限。
    pub fn max_pages(&self) -> u32 {
        self.max_pages
    }

    /// 1 ページの一辺（テクセル）。
    pub fn page_size(&self) -> u32 {
        self.page_size
    }

    /// ページごとの使用状況（ページの順）。
    pub fn usage(&self) -> Vec<PageUsage> {
        let size = f64::from(self.page_size);
        self.pages
            .iter()
            .map(|p| PageUsage {
                glyphs: p.glyphs,
                height_fraction: (f64::from(p.bottom()) / size) as f32,
                area_fraction: (p.used_area as f64 / (size * size)) as f32,
            })
            .collect()
    }

    /// 全体の使った高さの割合（0..1。今あるページの使った高さの合計 ÷ 上限のページ数ぶんの高さ）。
    ///
    /// 上限に対する割合なので、これが 1 に近いと新しい字はほぼ入らない。
    pub fn used_fraction_of_limit(&self) -> f32 {
        let used: u64 = self.pages.iter().map(|p| u64::from(p.bottom())).sum();
        (used as f64 / (f64::from(self.page_size) * f64::from(self.max_pages))) as f32
    }

    /// ページ `page` をフレーム `frame` に使った印を付ける（無いページは何もしない。古いフレームでは戻さない）。
    pub fn touch(&mut self, page: u32, frame: u64) {
        if let Some(p) = self.pages.get_mut(page as usize) {
            p.last_used_frame = p.last_used_frame.max(frame);
        }
    }

    /// ページ `page` が最後に使われたフレーム（無いページは None）。
    pub fn last_used_frame(&self, page: u32) -> Option<u64> {
        self.pages.get(page as usize).map(|p| p.last_used_frame)
    }

    /// 追い出してよいページ: 上限までページがあり、`min_idle_frames` 以上使われていないページのうち、最後に使われたのが最も古いもの
    /// （同じなら番号の小さいもの）。どれも最近使われていれば None。
    ///
    /// `current_frame` は今のフレームの番号（`min_idle_frames` が 1 以上なら、今のフレームに使ったページは必ず選ばない）。
    pub fn least_recently_used_idle_page(&self, current_frame: u64, min_idle_frames: u64) -> Option<u32> {
        if self.page_count() < self.max_pages {
            return None;
        }
        self.pages
            .iter()
            .enumerate()
            .filter(|(_, p)| p.last_used_frame.saturating_add(min_idle_frames) <= current_frame)
            .min_by_key(|(i, p)| (p.last_used_frame, *i))
            .map(|(i, _)| i as u32)
    }

    /// ページ `page` を丸ごと空にする（棚・字の数・面積を消し、`frame` に使った印を付ける＝すぐには再び選ばれない）。
    /// 空にしたページがいちばん新しいページになる（次の字からそこへ置く）。
    pub fn clear_page(&mut self, page: u32, frame: u64) {
        if let Some(p) = self.pages.get_mut(page as usize) {
            *p = PageShelves { last_used_frame: frame, ..PageShelves::default() };
            self.newest = page;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 試験のページの一辺（小さくして満杯を作りやすくする）。
    const SIZE: u32 = 64;

    /// 同じ棚へ左から詰め、高さの足りない字・右に入らない字は新しい棚へ行く。
    #[test]
    fn shelves_fill_left_to_right_then_down() {
        let mut a = AtlasPageAllocator::new(SIZE, 1);
        assert_eq!(a.alloc(20, 10), Some(AtlasSlot { page: 0, x: 0, y: 0 }));
        assert_eq!(a.alloc(20, 8), Some(AtlasSlot { page: 0, x: 20, y: 0 }), "低い字は高い棚へ入る");
        assert_eq!(a.alloc(20, 12), Some(AtlasSlot { page: 0, x: 0, y: 10 }), "高い字は新しい棚");
        assert_eq!(a.alloc(30, 10), Some(AtlasSlot { page: 0, x: 20, y: 10 }), "最初の棚は右に 24 しか無いので次の棚");
        assert_eq!(a.page_count(), 1);
        let u = a.usage();
        assert_eq!(u[0].glyphs, 4);
        assert!((u[0].height_fraction - 22.0 / 64.0).abs() < 1e-6);
        assert!((u[0].area_fraction - (200.0 + 160.0 + 240.0 + 300.0) / 4096.0).abs() < 1e-6);
    }

    /// ページが満杯なら次のページを足して置き、上限まで満杯なら None。
    #[test]
    fn full_page_adds_next_page_until_limit() {
        let mut a = AtlasPageAllocator::new(SIZE, 2);
        // 1 ページに 32×32 は 4 つ入る
        for i in 0..4 {
            let s = a.alloc(32, 32).expect("1 ページ目");
            assert_eq!(s.page, 0, "{i} 個目");
        }
        assert_eq!(a.page_count(), 1, "満杯になるまでページを足さない");
        let s = a.alloc(32, 32).expect("2 ページ目へ");
        assert_eq!(s, AtlasSlot { page: 1, x: 0, y: 0 });
        assert_eq!(a.page_count(), 2);
        for _ in 0..3 {
            assert_eq!(a.alloc(32, 32).map(|s| s.page), Some(1));
        }
        assert_eq!(a.alloc(32, 32), None, "上限 2 ページまで満杯");
        assert_eq!(a.page_count(), 2, "上限を超えて足さない");
        // 上限ならいちばん新しいページにしか置かない（古いページの隙間は alloc_in_gaps だけ。ここではどちらも隙間なし）
        assert_eq!(a.alloc(1, 1), None);
        assert!((a.used_fraction_of_limit() - 1.0).abs() < 1e-6);
    }

    /// 新しい字はいちばん新しいページへだけ置く（古いページの隙間には入れない）。古いページの隙間は `alloc_in_gaps`（最後の手段）だけが使う。
    #[test]
    fn new_glyphs_go_to_newest_page_and_gaps_are_last_resort() {
        let mut a = AtlasPageAllocator::new(SIZE, 2);
        assert_eq!(a.alloc(40, 60).map(|s| s.page), Some(0));
        // 高さ 60 の棚の右は 24 しか空いていない。幅 30・高さ 30 の字は 1 ページ目の下にも入らない（下端 60 + 30 > 64）→ 2 ページ目
        assert_eq!(a.alloc(30, 30).map(|s| s.page), Some(1));
        assert_eq!(a.newest_page(), 1);
        // 幅 20 の字は 1 ページ目の隙間（x = 40）に入るが、新しい字は 2 ページ目へ
        assert_eq!(a.alloc(20, 20), Some(AtlasSlot { page: 1, x: 30, y: 0 }));
        // 2 ページ目を埋める（上限 2 なので足せない）
        while a.alloc(20, 20).is_some() {}
        assert_eq!(a.alloc(20, 20), None, "いちばん新しいページが満杯で上限");
        // 最後の手段: 1 ページ目の隙間（x = 40）へ
        assert_eq!(a.alloc_in_gaps(20, 20), Some(AtlasSlot { page: 0, x: 40, y: 0 }));
        assert_eq!(a.alloc_in_gaps(20, 20), None, "隙間も埋まった");
        // 空にしたページがいちばん新しいページになる
        a.clear_page(0, 5);
        assert_eq!(a.newest_page(), 0);
        assert_eq!(a.alloc(20, 20), Some(AtlasSlot { page: 0, x: 0, y: 0 }));
    }

    /// ページより大きい字はページを足さずに None、上限 0 は 1 ページとして扱う。
    #[test]
    fn oversized_rect_and_zero_limit() {
        let mut a = AtlasPageAllocator::new(SIZE, 0);
        assert_eq!(a.max_pages(), 1);
        assert_eq!(a.alloc(SIZE + 1, 4), None);
        assert_eq!(a.alloc(4, SIZE + 1), None);
        assert_eq!(a.page_count(), 1);
        let mut b = AtlasPageAllocator::new(SIZE, 4);
        assert_eq!(b.alloc(SIZE + 1, 4), None, "上限に余裕があっても大きすぎる字ではページを足さない");
        assert_eq!(b.page_count(), 1);
        assert_eq!(b.alloc(SIZE, SIZE), Some(AtlasSlot { page: 0, x: 0, y: 0 }), "ちょうどの大きさは入る");
    }

    /// 【追い出し】上限までページがあるときだけ、一定のフレーム数以上使われていないページのうち最も古いものを選ぶ。
    /// 空にしたページはまた先頭から詰められ、使った印が付く（すぐには再び選ばれない）。
    #[test]
    fn least_recently_used_idle_page_is_evicted_and_reused() {
        let mut a = AtlasPageAllocator::new(SIZE, 3);
        // 1 ページ 64×64 を 1 字で埋めて 3 ページにする（フレーム 1・2・3 に置いた）
        for frame in 1..=3u64 {
            let slot = a.alloc(SIZE, SIZE).expect("置ける");
            a.touch(slot.page, frame);
        }
        assert_eq!(a.page_count(), 3);
        assert_eq!(a.alloc(1, 1), None, "上限まで満杯");
        // フレーム 10・最低 5 フレーム使われていない: 全部 5 以上前 → 最も古いページ 0
        assert_eq!(a.least_recently_used_idle_page(10, 5), Some(0));
        // ページ 0 をフレーム 8 に描いた → 古い順はページ 1（フレーム 2）
        a.touch(0, 8);
        assert_eq!(a.least_recently_used_idle_page(10, 5), Some(1));
        // 古いフレームの印では戻らない
        a.touch(0, 4);
        assert_eq!(a.last_used_frame(0), Some(8));
        // どれも最近（5 フレーム以内）なら選ばない
        a.touch(1, 9);
        a.touch(2, 9);
        assert_eq!(a.least_recently_used_idle_page(10, 5), None);
        // 今のフレームに使ったページは最低 1 フレームでも選ばない
        assert_eq!(a.least_recently_used_idle_page(9, 1), Some(0), "フレーム 8 のページ 0 だけが 1 フレーム以上前");
        // 空にすると先頭から詰め直せ、字の数・面積も 0 から
        a.clear_page(0, 10);
        assert_eq!(a.usage()[0].glyphs, 0);
        assert_eq!(a.alloc(32, 32), Some(AtlasSlot { page: 0, x: 0, y: 0 }));
        assert_eq!(a.last_used_frame(0), Some(10), "空にしたフレームの印");
        assert_eq!(a.least_recently_used_idle_page(12, 5), None, "空にしたばかりのページはすぐ選ばない");
    }

    /// 上限までページが無ければ追い出さない（足せばよい）。
    #[test]
    fn no_eviction_before_reaching_the_page_limit() {
        let mut a = AtlasPageAllocator::new(SIZE, 2);
        a.alloc(SIZE, SIZE);
        assert_eq!(a.page_count(), 1);
        assert_eq!(a.least_recently_used_idle_page(1000, 1), None);
    }

    /// 使用率: 上限に対する割合は、今あるページの使った高さの合計 ÷ 上限ぶんの高さ。
    #[test]
    fn usage_fraction_of_limit() {
        let mut a = AtlasPageAllocator::new(SIZE, 4);
        a.alloc(SIZE, 32);
        assert!((a.used_fraction_of_limit() - 32.0 / 256.0).abs() < 1e-6);
        a.alloc(SIZE, 32);
        a.alloc(SIZE, 16); // 2 ページ目へ
        assert_eq!(a.page_count(), 2);
        assert!((a.used_fraction_of_limit() - 80.0 / 256.0).abs() < 1e-6);
        assert_eq!(a.page_size(), SIZE);
    }
}
