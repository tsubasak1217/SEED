// ============================================================
//  font/inline/doc.rs — 記法つき本文の「レイアウト用中間表現」
//
//  【役割】
//  記法パーサ（`markup.rs`）が返したトークン列を、レイアウト計算がそのまま
//  食える形へ変換する。すなわち
//    ・画像トークンを **1 文字ぶんの代替文字**（U+FFFC）へ潰した文字列
//    ・その代替文字のバイト位置 → 画像情報（パス・送り幅・高さ）の表
//  の 2 つにする。
//
//  【なぜ「1 文字へ潰す」のか】
//  折り返し（`text_wrap`）・寸法計算（`text_layout`）・描画（`canvas_text`）は
//  すべて「文字列とそのバイト範囲」で動いている。画像を 1 文字として埋め込めば、
//  行分割・禁則・整列・枠・pivot・影といった既存規則が**そのまま**適用でき、
//  画像専用の分岐をレイアウト側に増やさずに済む。
//  副次的に「描画要素数 = 文字数」となるため、`MAX_TEXT_CHARS` の切り詰めが
//  記法の途中で切れる事故も構造的に起きない（1 画像は必ず 1 文字）。
//
//  【単位】
//  画像の送り幅・高さは **em 単位**（フォントサイズ 1.0 のときの値）で持つ。
//  グリフのメトリクス（`text_layout::advance_em`）と同じ単位にそろえることで、
//  フォントサイズを掛けるだけで px になる。
//
//  【未解決のとき】
//  アイコン名が無い／画像ファイルが読めない場合は「描かないが場所は取る」
//  トークンにする（送り幅 `UNRESOLVED_ADVANCE_EM` = 1em、高さ 0）。
//  本文のレイアウトが崩れず、警告はキー単位で 1 度だけ出る。
// ============================================================

use std::collections::HashSet;
use std::ops::Range;
use std::sync::{Mutex, OnceLock};

use super::icon_set;
use super::image_meta;
use super::markup::{self, InlineToken};

// ─── 定数 ──────────────────────────────────────────────────────

/// 画像 1 つを表す代替文字（Unicode OBJECT REPLACEMENT CHARACTER）。
///
/// 「ここに図がある」ことを表すために Unicode が用意している文字なので、
/// 独自の私用領域を使うより意味が明確で、他システムへ漏れても解釈が壊れない。
pub const IMAGE_PLACEHOLDER: char = '\u{FFFC}';

/// 未解決画像が確保する送り幅（em）。1em = フォントサイズと同じ幅の空白。
pub const UNRESOLVED_ADVANCE_EM: f32 = 1.0;

// ─── 画像情報 ──────────────────────────────────────────────────

/// 本文中に埋め込まれた画像 1 件（レイアウト解決済み）。
#[derive(Clone, Debug, PartialEq)]
pub struct InlineImage {
    /// 画像の assets:// パス。**空文字 = 未解決**（場所だけ取り、描かない）。
    pub path: String,
    /// 送り幅（em）。= 高さ(em) × アスペクト比。未解決時は `UNRESOLVED_ADVANCE_EM`。
    pub advance_em: f32,
    /// 描画する高さ（em）。**0 = 描かない**（未解決）。
    pub height_em: f32,
}

impl InlineImage {
    /// 描画対象か（未解決・高さ 0 は描かない）。
    #[inline]
    pub fn is_drawable(&self) -> bool {
        !self.path.is_empty() && self.height_em > 0.0
    }

    /// 送り幅（px）。
    #[inline]
    pub fn advance_px(&self, font_size: f32) -> f32 {
        self.advance_em * font_size
    }

    /// 描画高さ（px）。
    #[inline]
    pub fn height_px(&self, font_size: f32) -> f32 {
        self.height_em * font_size
    }
}

// ─── 画像位置表 ────────────────────────────────────────────────

/// 「本文のバイト位置 → 画像」の表（位置の昇順）。
///
/// 代替文字は本文中に高々数十個しか現れないため、`HashMap` ではなく
/// 昇順ベクタ + 二分探索にする（範囲走査が O(log n) で始められ、
/// 行ごとの走査がキャッシュに乗る）。
#[derive(Clone, Debug, Default, PartialEq)]
pub struct InlineImages {
    /// (代替文字のバイト位置, 画像) の昇順リスト。
    entries: Vec<(usize, InlineImage)>,
}

/// 画像を 1 つも含まない共有インスタンス（従来経路のための既定値）。
static EMPTY_IMAGES: OnceLock<InlineImages> = OnceLock::new();

impl InlineImages {
    /// 画像を 1 つも含まない共有インスタンスを返す。
    ///
    /// 「記法を使わないテキスト」はエンジン内の大多数（ギズモ・操作ガイド等）なので、
    /// そのたびに空ベクタを作らずに済ませる。
    pub fn empty() -> &'static InlineImages {
        EMPTY_IMAGES.get_or_init(InlineImages::default)
    }

    /// (バイト位置, 画像) の並びから表を作る（位置の昇順へ整列する）。
    ///
    /// 通常は `build_doc` が組み立てるが、
    /// 「実ファイルを用意せずにレイアウト規則を検証する」テストや、
    /// 将来スクリプト側から画像列を直接与える経路のために公開しておく。
    pub fn from_entries(mut entries: Vec<(usize, InlineImage)>) -> Self {
        entries.sort_by_key(|(o, _)| *o);
        Self { entries }
    }

    /// 1 つも画像を含まないか。
    #[inline]
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// 画像の件数。
    #[inline]
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    /// 指定バイト位置にある画像を引く。無ければ `None`。
    pub fn get(&self, offset: usize) -> Option<&InlineImage> {
        if self.entries.is_empty() {
            return None;
        }
        self.entries
            .binary_search_by_key(&offset, |(o, _)| *o)
            .ok()
            .map(|i| &self.entries[i].1)
    }

    /// 指定バイト範囲に含まれる画像を昇順に走査する。
    pub fn iter_range(&self, range: Range<usize>) -> impl Iterator<Item = (usize, &InlineImage)> {
        // 範囲の開始位置へ二分探索で寄せてから線形に舐める。
        let start = self
            .entries
            .partition_point(|(o, _)| *o < range.start);
        self.entries[start..]
            .iter()
            .take_while(move |(o, _)| *o < range.end)
            .map(|(o, img)| (*o, img))
    }

    /// 本文を `byte_len` バイトで切り詰めたときに残る画像だけを持つ表を返す。
    ///
    /// `MAX_TEXT_CHARS` の切り詰めで消えた画像を残すと、
    /// 「描かれない文字の画像」が枠計算に混ざってしまうため必ず落とす。
    pub fn truncated(&self, byte_len: usize) -> Self {
        Self {
            entries: self
                .entries
                .iter()
                .filter(|(o, _)| *o < byte_len)
                .cloned()
                .collect(),
        }
    }
}

// ─── 解決済みドキュメント ──────────────────────────────────────

/// 記法を解決した本文（レイアウトへ渡す唯一の中間表現）。
#[derive(Clone, Debug, Default, PartialEq)]
pub struct InlineDoc {
    /// 改行正規化済み・画像を代替文字へ潰した本文。
    pub text: String,
    /// `text` に対する画像位置表。
    pub images: InlineImages,
}

impl InlineDoc {
    /// 画像を 1 つも含まない（＝従来どおりの純テキスト）か。
    #[inline]
    pub fn has_no_image(&self) -> bool {
        self.images.is_empty()
    }
}

// ─── 警告の重複抑止 ────────────────────────────────────────────

/// 既に警告を出したキーの集合（プロセス内で 1 つ）。
///
/// 未解決の画像は毎フレーム同じ場所で失敗するため、
/// 抑止しないとログが 1 秒で数千行になる。
static WARNED: OnceLock<Mutex<HashSet<String>>> = OnceLock::new();

/// 同じキーで 2 度目以降は何もしない警告出力。
fn warn_once(key: String, message: &str) {
    let Ok(mut set) = WARNED.get_or_init(|| Mutex::new(HashSet::new())).lock() else {
        return;
    };
    if set.insert(key) {
        eprintln!("[SEED TEXT] {message}");
    }
}

/// 警告の抑止状態を捨てる（アセットのホットリロード用）。
pub fn reset_warnings() {
    if let Ok(mut set) = WARNED.get_or_init(|| Mutex::new(HashSet::new())).lock() {
        set.clear();
    }
}

// ─── 構築 ──────────────────────────────────────────────────────

/// 本文に記法が含まれ得るか（含まないなら解析を丸ごと省く）。
///
/// 記法の入口は `[` とエスケープ用バックスラッシュだけなので、
/// そのどちらも無ければトークン化の結果は「本文そのもの」で確定する。
fn may_contain_markup(text: &str) -> bool {
    text.contains(markup::TOKEN_OPEN) || text.contains(markup::ESCAPE_CHAR)
}

/// 本文とアイコンセットのパスから、レイアウト用ドキュメントを組み立てる。
///
/// - 改行表記を `\n` へ正規化してから解析する
///   （以降の行分割・描画は正規化後の文字列を基準にする）
/// - `icon_set_path` が空でもよい（`[img:...]` の直接指定だけは使える）
pub fn build_doc(content: &str, icon_set_path: &str) -> InlineDoc {
    // 改行正規化は行分割と同じ 1 関数（text_wrap::normalize_newlines）に集約する。
    let normalized = super::super::text_wrap::normalize_newlines(content);

    // 記法が無い本文はトークン化せず、そのまま返す（大多数の経路）。
    if !may_contain_markup(&normalized) {
        return InlineDoc {
            text: normalized.into_owned(),
            images: InlineImages::default(),
        };
    }

    let set = icon_set::load_cached(icon_set_path);
    let mut text = String::with_capacity(normalized.len());
    let mut entries: Vec<(usize, InlineImage)> = Vec::new();

    for token in markup::parse_markup(&normalized) {
        match token {
            InlineToken::Text(s) => text.push_str(&s),
            InlineToken::Icon { name, height_scale } => {
                let img = resolve_icon(set.as_deref(), icon_set_path, &name, height_scale);
                entries.push((text.len(), img));
                text.push(IMAGE_PLACEHOLDER);
            }
            InlineToken::Image { path, height_scale } => {
                let img = resolve_image(&path, height_scale);
                entries.push((text.len(), img));
                text.push(IMAGE_PLACEHOLDER);
            }
        }
    }

    InlineDoc {
        text,
        images: InlineImages { entries },
    }
}

/// アイコン名を画像へ解決する（名前が引けない場合は未解決画像）。
fn resolve_icon(
    set: Option<&icon_set::IconSet>,
    set_path: &str,
    name: &str,
    markup_scale: Option<f32>,
) -> InlineImage {
    let Some(entry) = set.and_then(|s| s.get(name)) else {
        warn_once(
            format!("icon:{set_path}:{name}"),
            &format!(
                "アイコン名を解決できません（1em の空白で代用します）: [icon:{name}] set={}",
                if set_path.is_empty() { "(未設定)" } else { set_path }
            ),
        );
        return unresolved();
    };
    // 倍率の優先順位: 記法の h= > アイコンセットの既定 h > 全体既定。
    let scale = markup_scale
        .or(entry.height_scale)
        .unwrap_or(markup::DEFAULT_HEIGHT_SCALE);
    make_image(&entry.path, scale)
}

/// 画像パスを解決する（読めない場合は未解決画像）。
fn resolve_image(path: &str, markup_scale: Option<f32>) -> InlineImage {
    let scale = markup_scale.unwrap_or(markup::DEFAULT_HEIGHT_SCALE);
    make_image(path, scale)
}

/// パスとアスペクト比から画像情報を作る。寸法が取れなければ未解決扱い。
fn make_image(path: &str, height_scale: f32) -> InlineImage {
    let Some(aspect) = image_meta::aspect_cached(path) else {
        warn_once(
            format!("img:{path}"),
            &format!("インライン画像を解決できません（1em の空白で代用します）: {path}"),
        );
        return unresolved();
    };
    InlineImage {
        path: path.to_string(),
        advance_em: height_scale * aspect,
        height_em: height_scale,
    }
}

/// 未解決画像（描かないが 1em ぶんの場所を取る）。
fn unresolved() -> InlineImage {
    InlineImage {
        path: String::new(),
        advance_em: UNRESOLVED_ADVANCE_EM,
        height_em: 0.0,
    }
}

// ============================================================
//  単体テスト
//
//  実ファイルを置けないため「解決できる画像」は作れない。
//  ここでは *記法 → 代替文字 + 位置表* の変換規則と、未解決時に
//  レイアウトが崩れないこと（1em の場所を取ること）を検証する。
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 記法を含まない本文はそのまま通り、画像表は空になる。
    #[test]
    fn plain_text_has_no_images() {
        let d = build_doc("ただの文字列", "");
        assert_eq!(d.text, "ただの文字列");
        assert!(d.has_no_image());
    }

    /// CRLF は `\n` へ正規化される（行分割と同じ規則）。
    #[test]
    fn newlines_are_normalized() {
        let d = build_doc("a\r\nb\rc", "");
        assert_eq!(d.text, "a\nb\nc");
    }

    /// 画像トークンは 1 文字の代替文字になり、位置表へ登録される。
    #[test]
    fn image_token_becomes_single_placeholder_char() {
        let d = build_doc("押す[icon:key_w]で移動", "");
        // 代替文字はちょうど 1 つ。
        assert_eq!(d.text.matches(IMAGE_PLACEHOLDER).count(), 1);
        assert_eq!(d.images.len(), 1);
        // 位置表のオフセットは代替文字の位置と一致する。
        let off = d.text.find(IMAGE_PLACEHOLDER).expect("代替文字がある");
        assert!(d.images.get(off).is_some());
        // 文字数は「押す」+ 画像 1 + 「で移動」= 6 文字（＝描画要素数）。
        assert_eq!(d.text.chars().count(), 6);
    }

    /// 未解決の画像は「描かないが 1em の場所を取る」トークンになる。
    #[test]
    fn unresolved_image_reserves_one_em() {
        let d = build_doc("[img:assets://__no_such_image__.png]", "");
        let img = d.images.get(0).expect("位置 0 に登録される");
        assert!(!img.is_drawable(), "未解決は描かない");
        assert_eq!(img.advance_em, UNRESOLVED_ADVANCE_EM);
        assert_eq!(img.height_em, 0.0);
    }

    /// 記法でない角括弧・エスケープは通常文字として本文へ残る。
    #[test]
    fn invalid_markup_stays_as_text() {
        let d = build_doc(r"配列[0] と \[icon:x]", "");
        assert_eq!(d.text, "配列[0] と [icon:x]");
        assert!(d.has_no_image());
    }

    /// 範囲走査は指定バイト範囲の画像だけを返す。
    #[test]
    fn iter_range_selects_images_in_range() {
        let d = build_doc("[icon:a]あ[icon:b]", "");
        assert_eq!(d.images.len(), 2);
        let first = d.text.find(IMAGE_PLACEHOLDER).unwrap();
        let in_first = d.images.iter_range(0..first + 1).count();
        assert_eq!(in_first, 1, "先頭の画像だけが範囲に入る");
        assert_eq!(d.images.iter_range(0..d.text.len()).count(), 2);
    }

    /// 切り詰めると、切り捨てられた位置の画像は表からも消える。
    #[test]
    fn truncated_drops_images_beyond_limit() {
        let d = build_doc("[icon:a]あ[icon:b]", "");
        let second = d.text.rfind(IMAGE_PLACEHOLDER).unwrap();
        let t = d.images.truncated(second);
        assert_eq!(t.len(), 1, "2 つ目の画像は落ちる");
    }
}
