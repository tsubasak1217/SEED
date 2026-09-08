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

use crate::engine::components::text_slots::{
    DEFAULT_SLOT_COLOR, SlotValue, TextSlotData, TextSlotKind,
};

use super::color_runs::ColorRuns;
use super::icon_set;
use super::image_meta;
use super::markup::{self, InlineToken};
use super::slot_format::{clamp_injected, format_number};
use super::slot_markup::{self, SlotSpec, SlotToken};

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
    // スロットを持たない呼び出し（ギズモ・操作ガイド等）の薄いラッパ。
    // 実装を 1 本に保つため、必ずスロット版へ委譲する。
    build_doc_with_slots(content, icon_set_path, &[], &[]).0
}

/// 通常テキスト区間へ角括弧記法（`[icon:]` / `[img:]`）を適用して積む。
///
/// プレースホルダ記法（波括弧）で切り出した**通常文字の区間だけ**に適用する。
/// スロットが差し込んだ値はここを通らない（＝再パースされない）。
fn append_markup_segment(
    segment: &str,
    set: Option<&icon_set::IconSet>,
    set_path: &str,
    text: &mut String,
    entries: &mut Vec<(usize, InlineImage)>,
) {
    // 角括弧もエスケープも無い区間は解析せずそのまま積む（大多数の経路）。
    if !may_contain_markup(segment) {
        text.push_str(segment);
        return;
    }
    for token in markup::parse_markup(segment) {
        match token {
            InlineToken::Text(s) => text.push_str(&s),
            InlineToken::Icon { name, height_scale } => {
                let img = resolve_icon(set, set_path, &name, height_scale);
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
    // アイコン名は見つかったが「解決先の画像自体」が読めない場合の警告に
    // アイコン名も出せるよう、経由元をここで渡しておく。
    make_image(&entry.path, scale, Some(name))
}

/// 画像パスを解決する（読めない場合は未解決画像）。
fn resolve_image(path: &str, markup_scale: Option<f32>) -> InlineImage {
    let scale = markup_scale.unwrap_or(markup::DEFAULT_HEIGHT_SCALE);
    // `[img:]` 記法の直接指定にはアイコン名という概念が無い。
    make_image(path, scale, None)
}

/// パスとアスペクト比から画像情報を作る。寸法が取れなければ未解決扱い。
///
/// `icon_name`: `[icon:名前]` 経由で辿り着いた場合の元の名前（`[img:]` 直接指定なら `None`）。
/// 警告メッセージに「アイコン名」と「解決先パス」の**両方**を出すことで、
/// `.icons` のどのエントリがどのファイルを指していて壊れているのかを
/// ログだけで特定できるようにする。
fn make_image(path: &str, height_scale: f32, icon_name: Option<&str>) -> InlineImage {
    let Some(aspect) = image_meta::aspect_cached(path) else {
        let message = match icon_name {
            Some(name) => format!(
                "インライン画像を解決できません（1em の空白で代用します）: icon={name} path={path}"
            ),
            None => format!("インライン画像を解決できません（1em の空白で代用します）: path={path}"),
        };
        warn_once(format!("img:{path}"), &message);
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

// ============================================================
//  プレースホルダ記法（スロット）の展開
//
//  【この層の位置づけ】
//  「記法の解析」（`slot_markup`）と「値の解決」（`app::text_expand` の
//  ValueProvider）の間に立ち、**解決済みの値を受け取って本文へ焼き込む**
//  純関数だけを置く。ここは World もスクリプトも一切見ない。
//
//  【注入値を再パースしない理由】
//  差し込む文字列にユーザーデータ（プレイヤー名など）が入る以上、
//  そこに `[` や `{` が含まれても本文の記法として解釈してはならない
//  （見た目が壊れるだけでなく、意図しない画像読み込みを誘発する）。
//  そこで注入値は `InlineDoc.text` へ**そのまま**積み、
//  角括弧記法は「本文の通常文字区間」にだけ適用する。
// ============================================================

/// スロットのパスを「画像パス直接指定」と判定するための印。
///
/// これを含めば assets:// などのスキーム付きパス、含まなければ
/// アイコンセットのアイコン名として扱う（`[img:]` / `[icon:]` と同じ規則）。
pub const PATH_SCHEME_MARK: &str = "://";

/// 記法つき本文をスロットの値で展開し、レイアウト用ドキュメントと色区間表を返す。
///
/// # 引数
/// - `content`       : 本文（プレースホルダ記法と角括弧記法を含みうる）
/// - `icon_set_path` : アイコンセット（.icons）の assets:// パス。空でもよい
/// - `slots`         : スロット配列（本文の添字で引く）
/// - `values`        : スロットごとの**解決済みの値**（`slots` と同じ添字）。
///                     空でもよい（そのときは各スロットのフォールバック値を使う）
///
/// # スロットが足りないとき
/// 添字に対応するスロットが無い場合は「その種類の既定値」で描く
/// （色 = 既定色 / 画像 = 未解決（1em の空白） / 文字列 = 空 / 数値 = 0）。
/// スクリプトが本文だけを差し替えた直後でも描画が壊れないようにするため。
pub fn build_doc_with_slots(
    content: &str,
    icon_set_path: &str,
    slots: &[TextSlotData],
    values: &[SlotValue],
) -> (InlineDoc, ColorRuns) {
    // 改行正規化は行分割と同じ 1 関数（text_wrap::normalize_newlines）に集約する。
    let normalized = super::super::text_wrap::normalize_newlines(content);
    let has_bracket = may_contain_markup(&normalized);
    let has_brace = slot_markup::may_contain_slot_markup(&normalized);

    // どちらの記法も無い本文は解析せずそのまま返す（大多数の経路）。
    if !has_bracket && !has_brace {
        return (
            InlineDoc {
                text: normalized.into_owned(),
                images: InlineImages::default(),
            },
            ColorRuns::default(),
        );
    }

    let set = icon_set::load_cached(icon_set_path);
    let mut text = String::with_capacity(normalized.len());
    let mut entries: Vec<(usize, InlineImage)> = Vec::new();

    // 波括弧が無いなら従来経路（角括弧記法だけ）。色区間は生じない。
    if !has_brace {
        append_markup_segment(
            &normalized,
            set.as_deref(),
            icon_set_path,
            &mut text,
            &mut entries,
        );
        return (
            InlineDoc {
                text,
                images: InlineImages { entries },
            },
            ColorRuns::default(),
        );
    }

    let mut runs: Vec<(Range<usize>, [f32; 4])> = Vec::new();
    // 開いている色区間（開始バイト位置, 色）。ネストは持たない（確定仕様）。
    let mut open: Option<(usize, [f32; 4])> = None;

    for token in slot_markup::parse_slot_markup(&normalized) {
        match token {
            SlotToken::Text(s) => append_markup_segment(
                &s,
                set.as_deref(),
                icon_set_path,
                &mut text,
                &mut entries,
            ),
            // 明示的な閉じタグ。ここだけは行末で切らずに指定位置で閉じる。
            SlotToken::ColorEnd => {
                if let Some((start, color)) = open.take() {
                    runs.push((start..text.len(), color));
                }
            }
            SlotToken::Slot(spec) => {
                let slot = slots.get(spec.index);
                match spec.kind {
                    TextSlotKind::Color => {
                        // ネスト不可: 内側の {color} は前の区間を打ち切る。
                        // 閉じ忘れとみなして行末規則を適用する。
                        if let Some((start, color)) = open.take() {
                            runs.push((start..line_end_from(&text, start), color));
                        }
                        let rgba = slot.map(|s| s.rgba).unwrap_or(DEFAULT_SLOT_COLOR);
                        open = Some((text.len(), rgba));
                    }
                    TextSlotKind::Image => {
                        let path = slot.map(|s| s.path.as_str()).unwrap_or("");
                        let img =
                            resolve_slot_image(set.as_deref(), icon_set_path, path, spec.height_scale);
                        entries.push((text.len(), img));
                        text.push(IMAGE_PLACEHOLDER);
                    }
                    // 文字列・数値は**そのまま**積む（再パースしない）。
                    TextSlotKind::String | TextSlotKind::Num => {
                        text.push_str(&injected_text(&spec, values.get(spec.index), slot));
                    }
                }
            }
        }
    }

    // 閉じ忘れの色区間は行末（次の改行の直前）まで。改行が無ければ本文末尾まで。
    if let Some((start, color)) = open.take() {
        runs.push((start..line_end_from(&text, start), color));
    }

    (
        InlineDoc {
            text,
            images: InlineImages { entries },
        },
        ColorRuns::from_runs(runs),
    )
}

/// `start` から見て「その行の終わり」のバイト位置を返す。
///
/// 次の改行の直前、無ければ本文の末尾。閉じ忘れの色区間が次の行まで
/// 染み出さないようにするための規則（確定仕様）。
fn line_end_from(text: &str, start: usize) -> usize {
    match text[start..].find('\n') {
        Some(rel) => start + rel,
        None => text.len(),
    }
}

/// スロットのパス（アイコン名 or 画像パス）を画像へ解決する。
///
/// 空文字は「未解決」（描かないが 1em の場所を取る）。
/// スキーム印を含めば画像パス直接指定、含まなければアイコン名として引く。
fn resolve_slot_image(
    set: Option<&icon_set::IconSet>,
    set_path: &str,
    path: &str,
    height_scale: Option<f32>,
) -> InlineImage {
    if path.is_empty() {
        return unresolved();
    }
    if path.contains(PATH_SCHEME_MARK) {
        resolve_image(path, height_scale)
    } else {
        resolve_icon(set, set_path, path, height_scale)
    }
}

/// 文字列・数値スロットが差し込む文字列を作る。
///
/// 優先順位は「解決済みの値 > スロットのフォールバック値」。
/// 値の型が種類と食い違う場合も落とさずに変換する
/// （バインド先の型が後から変わっても表示が消えないようにする）。
/// 最後に必ず `MAX_SLOT_INJECTED_CHARS` で切り詰める。
fn injected_text(
    spec: &SlotSpec,
    value: Option<&SlotValue>,
    slot: Option<&TextSlotData>,
) -> String {
    // フォールバック（スロット自身が持つ値）。スロットが無ければ空 / 0。
    let fallback = slot.map(|s| s.fallback_value());
    let value = value.or(fallback.as_ref());

    let raw = match spec.kind {
        TextSlotKind::Num => {
            let n = match value {
                Some(SlotValue::Num(n)) => *n,
                // 文字列がバインドされていても数値として読めるなら使う。
                Some(SlotValue::Str(s)) => s.trim().parse::<f32>().unwrap_or_default(),
                None => f32::default(),
            };
            format_number(n, spec.decimals)
        }
        _ => match value {
            Some(SlotValue::Str(s)) => s.clone(),
            // 数値がバインドされていれば本文の書式で文字列化する。
            Some(SlotValue::Num(n)) => format_number(*n, spec.decimals),
            None => String::new(),
        },
    };
    clamp_injected(&raw).into_owned()
}

// ============================================================
//  単体テスト（スロット展開）
//
//  実ファイルを置けないため「解決できる画像」は作れない。
//  ここでは *記法 + スロット値 → 本文・画像位置・色区間* の変換規則を検証する。
// ============================================================

#[cfg(test)]
mod slot_tests {
    use super::*;

    /// 種類と値を指定してスロットを 1 件作るヘルパ。
    fn slot(kind: TextSlotKind) -> TextSlotData {
        TextSlotData::new_of_kind(kind)
    }

    /// 記法もスロットも無い本文は従来どおり（ビット互換の確認）。
    #[test]
    fn plain_text_is_unchanged() {
        let (doc, runs) = build_doc_with_slots("ただの文字列", "", &[], &[]);
        assert_eq!(doc.text, "ただの文字列");
        assert!(doc.has_no_image());
        assert!(runs.is_empty());
        // ラッパ（build_doc）と完全に一致すること。
        assert_eq!(build_doc("ただの文字列", "").text, doc.text);
    }

    /// 既知キーワード以外の波括弧は 1 文字も変えずに残る（既存本文の互換）。
    #[test]
    fn unknown_braces_are_preserved() {
        let src = "JSON は {\"a\": 1} です";
        let (doc, runs) = build_doc_with_slots(src, "", &[], &[]);
        assert_eq!(doc.text, src);
        assert!(runs.is_empty());
    }

    /// 画像スロットは 1 文字の代替文字になり、位置表へ登録される。
    #[test]
    fn image_slot_becomes_placeholder() {
        let mut s = slot(TextSlotKind::Image);
        s.path = "assets://__no_such_image__.png".into();
        let (doc, _) = build_doc_with_slots("押す{image}で移動", "", &[s], &[]);
        assert_eq!(doc.text.matches(IMAGE_PLACEHOLDER).count(), 1);
        assert_eq!(doc.images.len(), 1);
        // 未解決でも 1em の場所は取る（レイアウトが崩れない）。
        let off = doc.text.find(IMAGE_PLACEHOLDER).unwrap();
        let img = doc.images.get(off).unwrap();
        assert!(!img.is_drawable());
        assert_eq!(img.advance_em, UNRESOLVED_ADVANCE_EM);
    }

    /// スロットが足りなくても描画は壊れない（画像は 1em の空白になる）。
    #[test]
    fn missing_slot_falls_back_safely() {
        let (doc, runs) = build_doc_with_slots("{image}{num}{color}あ", "", &[], &[]);
        assert_eq!(doc.images.len(), 1);
        // 数値スロットが無ければ 0 として描く。
        assert!(doc.text.contains('0'));
        // 色スロットが無ければ既定色の区間になる（描画は変わらない）。
        assert_eq!(runs.len(), 1);
    }

    /// 数値スロットの書式（整数の四捨五入・小数桁）。
    #[test]
    fn num_slot_formats_by_content_spec() {
        let mut s = slot(TextSlotKind::Num);
        s.num = 2.5;
        let (doc, _) = build_doc_with_slots("{num}", "", &[s.clone()], &[]);
        assert_eq!(doc.text, "3", "half-away-from-zero で丸める");

        s.num = 1.23456;
        let (doc, _) = build_doc_with_slots("{num.3}", "", &[s], &[]);
        assert_eq!(doc.text, "1.235");
    }

    /// NaN / 無限大 / 負のゼロが読める表記になる。
    #[test]
    fn num_slot_special_values() {
        for (v, want) in [
            (f32::NAN, "NaN"),
            (f32::INFINITY, "∞"),
            (f32::NEG_INFINITY, "-∞"),
            (-0.0f32, "0"),
        ] {
            let mut s = slot(TextSlotKind::Num);
            s.num = v;
            let (doc, _) = build_doc_with_slots("{num}", "", &[s], &[]);
            assert_eq!(doc.text, want, "{v} の表示");
        }
    }

    /// 解決済みの値はスロットのフォールバックより優先される。
    #[test]
    fn resolved_value_overrides_fallback() {
        let mut s = slot(TextSlotKind::Num);
        s.num = 1.0;
        let (doc, _) = build_doc_with_slots("{num}", "", &[s], &[SlotValue::Num(9.0)]);
        assert_eq!(doc.text, "9");
    }

    /// 注入した値は再パースされない（記法に見える文字がそのまま出る）。
    #[test]
    fn injected_value_is_not_reparsed() {
        let mut s = slot(TextSlotKind::String);
        s.text = "[icon:key_w] と {num}".into();
        let (doc, runs) = build_doc_with_slots("{string}", "", &[s], &[]);
        assert_eq!(doc.text, "[icon:key_w] と {num}");
        assert!(doc.has_no_image(), "注入値の角括弧は画像にならない");
        assert!(runs.is_empty());
    }
    /// 注入文字列は上限文字数で切り詰められる。
    #[test]
    fn injected_value_is_length_capped() {
        use super::super::slot_format::MAX_SLOT_INJECTED_CHARS;
        let mut s = slot(TextSlotKind::String);
        s.text = std::iter::repeat_n('a', MAX_SLOT_INJECTED_CHARS + 50).collect();
        let (doc, _) = build_doc_with_slots("{string}", "", &[s], &[]);
        assert_eq!(doc.text.chars().count(), MAX_SLOT_INJECTED_CHARS);
    }

    /// 色区間は「{color} の位置から {/color} の直前まで」。
    #[test]
    fn color_run_covers_explicit_range() {
        let mut s = slot(TextSlotKind::Color);
        s.rgba = [1.0, 0.0, 0.0, 1.0];
        let (doc, runs) = build_doc_with_slots("あ{color}赤{/color}い", "", &[s], &[]);
        assert_eq!(doc.text, "あ赤い");
        assert_eq!(runs.len(), 1);
        let start = "あ".len();
        let mut c = 0;
        assert_eq!(runs.color_at(start, &mut c, [1.0; 4]), [1.0, 0.0, 0.0, 1.0]);
        assert_eq!(runs.color_at(start - 1, &mut c, [1.0; 4]), [1.0; 4]);
        assert_eq!(runs.color_at(start + "赤".len(), &mut c, [1.0; 4]), [1.0; 4]);
    }

    /// 閉じタグを省いたら行末（次の改行の直前）までで打ち切られる。
    #[test]
    fn unclosed_color_run_ends_at_line_end() {
        let mut s = slot(TextSlotKind::Color);
        s.rgba = [0.0, 1.0, 0.0, 1.0];
        let (doc, runs) = build_doc_with_slots("{color}あ
い", "", &[s], &[]);
        assert_eq!(doc.text, "あ
い");
        assert_eq!(runs.len(), 1);
        let mut c = 0;
        assert_eq!(runs.color_at(0, &mut c, [1.0; 4]), [0.0, 1.0, 0.0, 1.0], "1 行目は色付き");
        let second_line = "あ
".len();
        assert_eq!(runs.color_at(second_line, &mut c, [1.0; 4]), [1.0; 4], "2 行目は既定色");
    }

    /// 改行が無ければ本文末尾まで色が続く。
    #[test]
    fn unclosed_color_run_reaches_text_end() {
        let s = slot(TextSlotKind::Color);
        let (doc, runs) = build_doc_with_slots("{color}あい", "", &[s], &[]);
        let mut c = 0;
        assert_eq!(runs.len(), 1);
        assert_eq!(runs.color_at(doc.text.len() - 1, &mut c, [0.0; 4]), DEFAULT_SLOT_COLOR);
    }

    /// ネストは出来ず、内側の {color} が前の区間を打ち切る。
    #[test]
    fn nested_color_terminates_previous_run() {
        let mut a = slot(TextSlotKind::Color);
        a.rgba = [1.0, 0.0, 0.0, 1.0];
        let mut b = slot(TextSlotKind::Color);
        b.rgba = [0.0, 0.0, 1.0, 1.0];
        let (doc, runs) = build_doc_with_slots("{color}赤{color}青", "", &[a, b], &[]);
        assert_eq!(doc.text, "赤青");
        assert_eq!(runs.len(), 2);
        let mut c = 0;
        assert_eq!(runs.color_at(0, &mut c, [1.0; 4]), [1.0, 0.0, 0.0, 1.0]);
        assert_eq!(runs.color_at("赤".len(), &mut c, [1.0; 4]), [0.0, 0.0, 1.0, 1.0]);
    }

    /// 番号指定で同じスロットを複数回参照できる。
    #[test]
    fn explicit_index_reuses_the_same_slot() {
        let mut s = slot(TextSlotKind::Num);
        s.num = 7.0;
        // 2 つ目は 0 番を参照する（配列長は 2 だが値は 0 番のもの）。
        let slots = vec![s, TextSlotData::default()];
        let (doc, _) = build_doc_with_slots("{num}-{num:0}", "", &slots, &[]);
        assert_eq!(doc.text, "7-7");
    }

    /// 既存の角括弧記法（[icon:] / [img:]）はプレースホルダと共存する。
    #[test]
    fn bracket_markup_still_works_alongside_slots() {
        let s = slot(TextSlotKind::String);
        let (doc, _) = build_doc_with_slots("[img:assets://x.png]{string}", "", &[s], &[]);
        assert_eq!(doc.images.len(), 1, "角括弧の画像は従来どおり解決される");
    }

    /// 波括弧のエスケープはリテラルの「{」になる。
    #[test]
    fn escaped_brace_is_literal() {
        let (doc, _) = build_doc_with_slots(r"\{num}", "", &[], &[]);
        assert_eq!(doc.text, "{num}");
    }
}
