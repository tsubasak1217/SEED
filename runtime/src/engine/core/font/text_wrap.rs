// ============================================================
//  font/text_wrap.rs — テキストの自動折り返し（GPU 非依存の純関数）
//
//  【役割】
//  「枠の幅（px）」を与えられたとき、文字列を何行に割るかを決める。
//  行の高さ・整列・枠との関係は `text_layout.rs` の責務で、ここは
//  **横方向にどこで折るか**だけを担当する（単一責任）。
//
//  【なぜ独立モジュールか】
//  折り返しは「クラスタ分割 → 貪欲配置 → 禁則調整」の 3 段があり、
//  レイアウト原点計算（text_layout.rs）とは知識が完全に分かれている。
//  同居させると 1 ファイルが肥大化し、どちらのテストが何を守っているのか
//  読めなくなるため分離する。
//
//  【アルゴリズム（確定仕様）】
//  1. 段落（改行区切り）ごとに **クラスタ**へ分割する。
//     - 連続する ASCII 英数字（語内記号 アポストロフィ・ハイフンを含む）= 1 クラスタ（＝単語）
//     - 空白（半角スペース・タブ）は**直前のクラスタに付属**する
//       （行頭へ送らないため。行末では幅に数えずぶら下げる）
//     - それ以外（日本語など）は 1 文字 1 クラスタ
//  2. 貪欲（greedy）に詰め、max_width を超えたらクラスタ境界で改行する。
//     - 1 クラスタが単独で max_width を超える場合は**文字単位で強制分割**する
//     - 1 行には最低 1 クラスタを必ず載せる（無限ループ防止の要）
//  3. 簡易禁則を適用する。
//     - 行頭禁則（KINSOKU_HEAD）: 次行頭に来る禁則文字を前行末尾へ
//       **ぶら下げ**る（最大 MAX_HANGING_CHARS 文字）
//     - 行末禁則（KINSOKU_TAIL）: 行末に来た禁則文字を次行頭へ**追い出す**
//       （追い出すと行が空になる場合は諦める）
//
//  【座標系・単位】
//  幅はすべてキャンバスローカル px。max_width <= 0 は「折り返さない」を意味し、
//  改行での分割と完全に同一の結果を返す（枠なしテキストの従来経路）。
// ============================================================

use std::borrow::Cow;
use std::ops::Range;

use ab_glyph::FontArc;

use super::inline::{IMAGE_PLACEHOLDER, InlineImages};
use super::text_layout::{advance_em, measure_line_width, measure_range_width};

// ─── 禁則・クラスタ規則の定数（マジックナンバー禁止）─────────────

/// 行頭禁則文字（行の先頭に置かない文字）。
///
/// 句読点・閉じ括弧・長音・小書き仮名・繰り返し記号など。
/// 該当文字が次行の先頭に来る場合は前行末尾へぶら下げる。
pub const KINSOKU_HEAD: &str = "、。，．・？！」』）】〉》”’ーぁぃぅぇぉっゃゅょゎヵヶ々〜:;";

/// 行末禁則文字（行の末尾に置かない文字）。
///
/// 開き括弧類。該当文字が行末に来る場合は次行の先頭へ追い出す。
pub const KINSOKU_TAIL: &str = "「『（【〈《“‘";

/// 行頭禁則のぶら下げで、1 行の末尾へはみ出させられる最大文字数。
///
/// 無制限にすると句点が連続する入力で行が延々と伸びるため上限を設ける。
pub const MAX_HANGING_CHARS: usize = 2;

/// 語内記号（連続 ASCII 英数字と一体で 1 クラスタ＝単語として扱う記号）。
///
/// アポストロフィ（don't）とハイフン（well-known）を単語の途中で切らないため。
const WORD_INNER_SYMBOLS: [char; 2] = ['\'', '-'];

// ─── 改行の正規化 ────────────────────────────────────────────────

/// 改行表記を `\n`（LF）だけに正規化する（純関数）。
///
/// 【なぜ必要か】
/// WPF の `TextBox` は Enter で CRLF（`\r\n`）を挿入する。また構造体リスト
/// 内の string は JSON 経由（`ScriptArray.Quote`）で保存されるため、
/// トップレベル `[TextArea]` 用の CRLF→LF 正規化（エディタ側 `Escape`）を
/// 通らずに `\r\n` のまま渡ってくる経路がある。
/// 本エンジンの行分割・描画は `\n` だけを改行とみなすため、正規化せずに
/// `\r` を渡すと「未定義グリフ（tofu）」として描画されてしまう。
///
/// 【正規化のルール】
/// - `\r\n` → `\n`（CRLF は 1 つの改行として扱う。2 行に分裂させない）
/// - 単独 `\r`（Mac 旧式改行を含む）→ `\n`
///
/// 【なぜここに集約するか】
/// 行分割の唯一の入口は `wrap_lines`（`resolve_layout` が呼ぶ唯一の
/// 行分割関数）なので、ここで正規化すれば描画（canvas_text.rs）・
/// 計測（text_layout.rs の `measure_text_box` / `resolve_layout`）の
/// 両方に自動的に効く。各所で個別に `\r` を除去するとルールがぶれるため、
/// 正規化はこの 1 関数だけが持つ。
///
/// `\r` を含まない場合はアロケーションしない（`Cow::Borrowed`）。
pub fn normalize_newlines(text: &str) -> Cow<'_, str> {
    if !text.contains('\r') {
        return Cow::Borrowed(text);
    }
    let mut out = String::with_capacity(text.len());
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '\r' {
            // "\r\n" はまとめて 1 つの改行として \n に変換する
            // （先読みで \n を消費し、2 行に分裂させない）。
            if chars.peek() == Some(&'\n') {
                chars.next();
            }
            out.push('\n');
        } else {
            out.push(c);
        }
    }
    Cow::Owned(out)
}

// ─── 公開型 ────────────────────────────────────────────────────

/// 折り返し後の 1 行。
///
/// `range` は**元テキスト全体**（改行文字を含む文字列）に対するバイト範囲で、
/// 行末の空白を除いた範囲を指す（描画・幅計測の両方がこの範囲を使う）。
#[derive(Clone, Debug, PartialEq)]
pub struct WrappedLine {
    /// 元テキストに対するバイト範囲（行末空白を含まない）。
    pub range: Range<usize>,
    /// 行の幅（px。`range` の実測値）。
    pub width: f32,
}

// ─── 内部型 ────────────────────────────────────────────────────

/// 折り返し単位（これ以上は分割しない塊）。
struct Cluster {
    /// 元テキストに対するバイト範囲（末尾の空白を**含む**）。
    range: Range<usize>,
    /// 末尾空白を除いたバイト終端（行末に来たときに使う範囲）。
    end_trimmed: usize,
    /// 末尾空白を**含む**送り幅（px。行の途中に来たときの幅）。
    width_full: f32,
    /// 末尾空白を**除いた**送り幅（px。行末に来たときの幅＝収まり判定に使う）。
    width_trimmed: f32,
    /// このクラスタが 1 文字だけで構成される場合のその文字（禁則判定に使う）。
    single: Option<char>,
    /// クラスタ本体（末尾空白を除いた部分）の文字数。強制分割の可否判定に使う。
    core_char_count: usize,
}

// ─── 文字種の判定 ──────────────────────────────────────────────

/// 折り返し上の「空白」か（行末でぶら下げる文字）。
fn is_wrap_space(c: char) -> bool {
    c == ' ' || c == '\t'
}

/// 単語を構成する文字か（ASCII 英数字＋語内記号）。
fn is_word_char(c: char) -> bool {
    c.is_ascii_alphanumeric() || WORD_INNER_SYMBOLS.contains(&c)
}

/// 行頭禁則文字か。
fn is_kinsoku_head(c: char) -> bool {
    KINSOKU_HEAD.contains(c)
}

/// 行末禁則文字か。
fn is_kinsoku_tail(c: char) -> bool {
    KINSOKU_TAIL.contains(c)
}

// ─── クラスタ分割 ──────────────────────────────────────────────

/// 段落（改行を含まない文字列）をクラスタ列へ分割する。
///
/// - `para` : 段落の文字列
/// - `base` : `para` の先頭が元テキスト全体で何バイト目かのオフセット
///
/// 空白は直前クラスタへ吸収する（行頭へ送らないため）。
/// 先頭にいきなり空白が来た場合のみ、空白だけのクラスタになる。
fn split_clusters(
    font: &FontArc,
    full_text: &str,
    para: &str,
    base: usize,
    font_size: f32,
    images: &InlineImages,
) -> Vec<Cluster> {
    let chars: Vec<(usize, char)> = para.char_indices().collect();
    let mut clusters: Vec<Cluster> = Vec::new();
    let mut i = 0usize;

    while i < chars.len() {
        let (start, c) = chars[i];

        // ── 空白: 直前クラスタへ吸収する（無ければ空白だけのクラスタ）──
        if is_wrap_space(c) {
            let mut j = i;
            let mut w = 0.0f32;
            while j < chars.len() && is_wrap_space(chars[j].1) {
                w += advance_em(font, chars[j].1) * font_size;
                j += 1;
            }
            let end = if j < chars.len() { chars[j].0 } else { para.len() };
            match clusters.last_mut() {
                Some(last) => {
                    // 末尾空白は「行の途中に来たときだけ」幅に数える。
                    last.range.end = base + end;
                    last.width_full += w;
                }
                None => clusters.push(Cluster {
                    range: base + start..base + end,
                    end_trimmed: base + end,
                    width_full: w,
                    width_trimmed: w,
                    single: None,
                    core_char_count: j - i,
                }),
            }
            i = j;
            continue;
        }

        // ── 単語（連続 ASCII 英数字）またはそれ以外の 1 文字 ──
        let mut j = i + 1;
        if is_word_char(c) {
            while j < chars.len() && is_word_char(chars[j].1) {
                j += 1;
            }
        }
        let core_end = if j < chars.len() { chars[j].0 } else { para.len() };
        let core = &para[start..core_end];
        let w = measure_range_width(font, full_text, base + start..base + core_end, font_size, images);
        clusters.push(Cluster {
            range: base + start..base + core_end,
            end_trimmed: base + core_end,
            width_full: w,
            width_trimmed: w,
            single: if j == i + 1 { Some(c) } else { None },
            core_char_count: j - i,
        });
        i = j;
    }

    clusters
}

/// クラスタを 1 文字ずつのクラスタ列へ分解する（強制分割用）。
///
/// 末尾空白は最後の文字クラスタへ引き継ぐ（空白だけのクラスタを作らないため）。
fn explode_cluster(
    font: &FontArc,
    text: &str,
    cluster: &Cluster,
    font_size: f32,
    images: &InlineImages,
) -> Vec<Cluster> {
    let core = &text[cluster.range.start..cluster.end_trimmed];
    let mut out: Vec<Cluster> = Vec::with_capacity(cluster.core_char_count);
    for (off, ch) in core.char_indices() {
        let start = cluster.range.start + off;
        let end = start + ch.len_utf8();
        // 画像の代替文字はフォントの送り幅ではなく画像の送り幅を使う。
        let w = match images.get(start).filter(|_| ch == IMAGE_PLACEHOLDER) {
            Some(img) => img.advance_px(font_size),
            None => advance_em(font, ch) * font_size,
        };
        out.push(Cluster {
            range: start..end,
            end_trimmed: end,
            width_full: w,
            width_trimmed: w,
            single: Some(ch),
            core_char_count: 1,
        });
    }
    // 末尾空白ぶんを最後の文字クラスタへ戻す（幅・範囲の情報を落とさない）。
    if let Some(last) = out.last_mut() {
        last.range.end = cluster.range.end;
        last.width_full += cluster.width_full - cluster.width_trimmed;
    }
    out
}

// ─── 折り返し本体 ──────────────────────────────────────────────

/// テキストを `max_width` に収まるよう折り返して行へ分割する（画像なし）。
///
/// - `max_width <= 0` のときは折り返さず、改行での分割と完全に同一の
///   行分割（範囲・幅とも）を返す。枠なしテキストの従来経路がこれ。
/// - 返る `WrappedLine::range` は元テキスト全体に対するバイト範囲。
///
/// 空文字列に対しては「幅 0 の 1 行」を返す（行数 1 = 従来の分割と同じ）。
pub fn wrap_lines(font: &FontArc, text: &str, font_size: f32, max_width: f32) -> Vec<WrappedLine> {
    wrap_lines_with_images(font, text, font_size, max_width, InlineImages::empty())
}

/// インライン画像を含むテキストを折り返して行へ分割する（**折り返しの実体**）。
///
/// 画像は `inline::IMAGE_PLACEHOLDER` 1 文字として本文へ埋まっており、
/// ここでは「送り幅が画像固有の、分割不可な 1 文字クラスタ」として扱われる。
/// したがって語単位／文字単位の折り返しも禁則も、既存規則がそのまま働く。
///
/// # 引数の契約
/// `images` のキーは **正規化後**の本文に対する絶対バイト位置であること
/// （`inline::build_doc` の出力をそのまま渡す）。`text` が正規化済みなら
/// ここでの正規化は何もしない（`Cow::Borrowed`）ので位置はズレない。
pub fn wrap_lines_with_images(
    font: &FontArc,
    text: &str,
    font_size: f32,
    max_width: f32,
    images: &InlineImages,
) -> Vec<WrappedLine> {
    // 改行表記を \n へ正規化する（唯一の入口。詳細は normalize_newlines のコメント）。
    // Cow なので \r を含まない通常入力ではアロケーションが発生しない。
    let normalized = normalize_newlines(text);
    let text: &str = &normalized;

    let mut out: Vec<WrappedLine> = Vec::new();
    let mut base = 0usize;
    for para in text.split('\n') {
        if max_width <= 0.0 || font_size <= 0.0 {
            // 折り返しなし: 段落 = 1 行（従来経路とビット一致させる）。
            out.push(WrappedLine {
                range: base..base + para.len(),
                width: measure_range_width(font, text, base..base + para.len(), font_size, images),
            });
        } else {
            wrap_paragraph(font, text, para, base, font_size, max_width, images, &mut out);
        }
        // 改行 1 バイトぶん進める（段落間の区切り文字）。
        base += para.len() + 1;
    }
    out
}

/// 1 段落を折り返して `out` へ行を追加する。
#[allow(clippy::too_many_arguments)]
fn wrap_paragraph(
    font: &FontArc,
    full_text: &str,
    para: &str,
    base: usize,
    font_size: f32,
    max_width: f32,
    images: &InlineImages,
    out: &mut Vec<WrappedLine>,
) {
    let mut clusters = split_clusters(font, full_text, para, base, font_size, images);
    if clusters.is_empty() {
        // 空段落は幅 0 の 1 行（行数を保つ）。
        out.push(WrappedLine {
            range: base..base,
            width: 0.0,
        });
        return;
    }

    let mut s = 0usize;
    while s < clusters.len() {
        // ── 1. 貪欲に詰める ──────────────────────────────────
        let mut e = s;
        // 行の途中までの幅（末尾空白を含む。次のクラスタの開始 X になる）
        let mut w_full = 0.0f32;
        while e < clusters.len() {
            // 収まり判定は「末尾空白を除いた幅」で行う（空白はぶら下げる）。
            let fits = w_full + clusters[e].width_trimmed <= max_width;
            if !fits && e > s {
                break;
            }
            if !fits && e == s && clusters[e].core_char_count > 1 {
                // 行頭のクラスタが単独で入りきらない場合のみ強制分割する。
                // 1 文字でも入りきらないときは分割しても縮まらないので、
                // そのまま 1 クラスタ載せて前進する（無限ループ防止）。
                let pieces = explode_cluster(font, full_text, &clusters[e], font_size, images);
                clusters.splice(e..e + 1, pieces);
                continue; // 分解後の先頭クラスタで再判定する
            }
            w_full += clusters[e].width_full;
            e += 1;
        }
        // 最低 1 クラスタは必ず載せる（上のループが 0 個で抜けることはないが保険）。
        if e == s {
            e = s + 1;
        }

        // ── 2. 行頭禁則（ぶら下げ）──────────────────────────
        // 次行の先頭が禁則文字なら、前行末尾へはみ出させる。
        let mut hung = 0usize;
        while e < clusters.len()
            && hung < MAX_HANGING_CHARS
            && clusters[e].single.is_some_and(is_kinsoku_head)
        {
            e += 1;
            hung += 1;
        }

        // ── 3. 行末禁則（追い出し）──────────────────────────
        // 行末が開き括弧なら次行へ送る。送ると行が空になる場合・
        // 次行が存在しない（＝最終行）場合は諦める。
        if e < clusters.len() && e - 1 > s && clusters[e - 1].single.is_some_and(is_kinsoku_tail) {
            e -= 1;
        }

        // ── 4. 行の確定（範囲は末尾空白を除く。幅は実測し直す）──
        let start = clusters[s].range.start;
        let end = clusters[e - 1].end_trimmed;
        out.push(WrappedLine {
            range: start..end,
            width: measure_range_width(font, full_text, start..end, font_size, images),
        });
        s = e;
    }
}

// ============================================================
//  単体テスト（GPU 不要。組み込みフォントで折り返し規則を検証する）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用フォント（組み込みフォント）。
    fn builtin() -> FontArc {
        FontArc::try_from_slice(super::super::DEFAULT_FONT_BYTES).expect("組み込みフォントを読める")
    }

    /// 行の文字列を取り出すヘルパ。
    fn texts<'a>(src: &'a str, lines: &[WrappedLine]) -> Vec<&'a str> {
        lines.iter().map(|l| &src[l.range.clone()]).collect()
    }

    /// max_width <= 0 は改行での分割と完全一致（枠なし従来経路）。
    #[test]
    fn no_wrap_matches_split_newline() {
        let f = builtin();
        let src = "abc\n\nデフ ghi";
        let lines = wrap_lines(&f, src, 24.0, 0.0);
        assert_eq!(texts(src, &lines), vec!["abc", "", "デフ ghi"]);
        for (l, expect) in lines.iter().zip(src.split('\n')) {
            assert!((l.width - measure_line_width(&f, expect, 24.0)).abs() < 1e-4);
        }
    }

    /// 日本語 20 文字は「10 文字ぶんの幅」でちょうど 2 行になる。
    #[test]
    fn japanese_wraps_by_character() {
        let f = builtin();
        let src = "あいうえおかきくけこさしすせそたちつてと";
        let font_size = 20.0;
        // 全角 10 文字ぶんの幅（誤差で 11 文字目が載らないよう僅かに余裕を持たせる）
        let ten = measure_line_width(&f, "あいうえおかきくけこ", font_size);
        let lines = wrap_lines(&f, src, font_size, ten + 0.01);
        assert_eq!(
            texts(src, &lines),
            vec!["あいうえおかきくけこ", "さしすせそたちつてと"]
        );
    }

    /// 英文は語の途中で折れない（空白は前行にぶら下がって行頭へ送られない）。
    #[test]
    fn english_wraps_at_word_boundary() {
        let f = builtin();
        let src = "hello wonderful world";
        let font_size = 20.0;
        let w = measure_line_width(&f, "hello wonderful", font_size) + 0.01;
        let lines = wrap_lines(&f, src, font_size, w);
        assert_eq!(texts(src, &lines), vec!["hello wonderful", "world"]);
    }

    /// 1 単語が枠より長い場合は文字単位で強制分割する。
    #[test]
    fn overlong_word_is_force_split() {
        let f = builtin();
        let src = "abcdefghij";
        let font_size = 20.0;
        let w = measure_line_width(&f, "abcde", font_size) + 0.01;
        let lines = wrap_lines(&f, src, font_size, w);
        assert_eq!(texts(src, &lines), vec!["abcde", "fghij"]);
    }

    /// 極小幅（1 文字も入らない）でも 1 行 1 文字ずつ必ず前進する（無限ループなし）。
    #[test]
    fn tiny_width_still_advances() {
        let f = builtin();
        let src = "あいう";
        let lines = wrap_lines(&f, src, 20.0, 0.001);
        assert_eq!(texts(src, &lines), vec!["あ", "い", "う"]);
    }

    /// 行頭禁則: 次行頭に来る句点は前行末尾へぶら下がる。
    #[test]
    fn kinsoku_head_hangs_to_previous_line() {
        let f = builtin();
        let src = "あいうえお。かきくけこ";
        let font_size = 20.0;
        // ちょうど 5 文字ぶん = 句点は次行頭に来るはずだが、禁則でぶら下がる。
        let w = measure_line_width(&f, "あいうえお", font_size) + 0.01;
        let lines = wrap_lines(&f, src, font_size, w);
        assert_eq!(texts(src, &lines)[0], "あいうえお。");
    }

    /// ぶら下げは MAX_HANGING_CHARS 文字で打ち切る。
    #[test]
    fn kinsoku_head_hanging_is_capped() {
        let f = builtin();
        // 句点を 3 つ並べる。ぶら下げは 2 文字までなので 3 つ目は次行へ。
        let src = "あいうえお。。。かきくけこ";
        let font_size = 20.0;
        let w = measure_line_width(&f, "あいうえお", font_size) + 0.01;
        let lines = wrap_lines(&f, src, font_size, w);
        assert_eq!(texts(src, &lines)[0], "あいうえお。。");
        assert!(texts(src, &lines)[1].starts_with('。'));
    }

    /// 行末禁則: 行末に来る開き括弧は次行頭へ追い出される。
    #[test]
    fn kinsoku_tail_is_pushed_to_next_line() {
        let f = builtin();
        let src = "あいうえ「かきくけこ";
        let font_size = 20.0;
        let w = measure_line_width(&f, "あいうえ「", font_size) + 0.01;
        let lines = wrap_lines(&f, src, font_size, w);
        assert_eq!(texts(src, &lines)[0], "あいうえ");
        assert!(texts(src, &lines)[1].starts_with('「'));
    }

    /// 追い出すと行が空になる場合は諦める（1 行に 1 クラスタは必ず載せる）。
    #[test]
    fn kinsoku_tail_gives_up_when_line_would_be_empty() {
        let f = builtin();
        let src = "「あいうえお";
        let font_size = 20.0;
        let lines = wrap_lines(&f, src, font_size, 0.001);
        assert_eq!(texts(src, &lines)[0], "「");
    }

    /// 行末の空白は幅に数えず（ぶら下げ）、次行の先頭へ送られない。
    #[test]
    fn trailing_space_hangs_and_is_trimmed() {
        let f = builtin();
        let src = "ab cd";
        let font_size = 20.0;
        // 「ab」ちょうどの幅。空白は幅に数えないので ab が 1 行目、cd が 2 行目。
        let w = measure_line_width(&f, "ab", font_size) + 0.01;
        let lines = wrap_lines(&f, src, font_size, w);
        assert_eq!(texts(src, &lines), vec!["ab", "cd"]);
    }

    /// 明示改行と折り返しは併用できる（段落ごとに独立して折る）。
    #[test]
    fn explicit_newline_splits_paragraphs() {
        let f = builtin();
        let src = "あいうえお\nかきくけこ";
        let font_size = 20.0;
        let w = measure_line_width(&f, "あいう", font_size) + 0.01;
        let lines = wrap_lines(&f, src, font_size, w);
        assert_eq!(texts(src, &lines), vec!["あいう", "えお", "かきく", "けこ"]);
    }

    /// CRLF（`\r\n`）・単独 CR（`\r`）・LF（`\n`）はすべて同じ改行として扱われ、
    /// 同じ行数・同じ行幅になる（本不具合の再現条件そのもの）。
    ///
    /// WPF の `TextBox` は Enter で CRLF を挿入するため、これを正規化しないまま
    /// 描画すると `\r` が未定義グリフ（tofu）として描かれてしまう。
    #[test]
    fn crlf_and_lone_cr_are_treated_as_newline() {
        let f = builtin();
        let font_size = 20.0;

        // 折り返しなし（max_width <= 0）経路。
        let lf = wrap_lines(&f, "a\nb", font_size, 0.0);
        let crlf = wrap_lines(&f, "a\r\nb", font_size, 0.0);
        let cr = wrap_lines(&f, "a\rb", font_size, 0.0);

        assert_eq!(lf.len(), 2, "LF は 2 行になる（前提）");
        assert_eq!(crlf.len(), lf.len(), "CRLF は 1 つの改行として扱われ、3 行に分裂しない");
        assert_eq!(cr.len(), lf.len(), "単独 CR も改行として扱われる");

        for i in 0..lf.len() {
            assert!(
                (crlf[i].width - lf[i].width).abs() < 1e-6,
                "CRLF 版の行幅に \r ぶんの幅が混入している（{} 行目: {} vs {}）",
                i, crlf[i].width, lf[i].width
            );
            assert!(
                (cr[i].width - lf[i].width).abs() < 1e-6,
                "単独 CR 版の行幅に \r ぶんの幅が混入している（{} 行目: {} vs {}）",
                i, cr[i].width, lf[i].width
            );
        }

        // wrap_lines が返す range は「正規化後」の文字列に対するバイト範囲になる
        // （resolve_layout 側も同じ正規化結果を layout.text として保持するため整合する）。
        // ここでは normalize_newlines を直接使って範囲の妥当性と \r 不在を確認する。
        let normalized_crlf = normalize_newlines("a\r\nb");
        let normalized_cr = normalize_newlines("a\rb");
        assert_eq!(normalized_crlf.as_ref(), "a\nb", "CRLF は 1 文字の \n に正規化される");
        assert_eq!(normalized_cr.as_ref(), "a\nb", "単独 CR も \n に正規化される");
        for l in &crlf {
            assert!(
                !normalized_crlf[l.range.clone()].contains('\r'),
                "折り返し結果の範囲に \r が残っている"
            );
        }
        for l in &cr {
            assert!(
                !normalized_cr[l.range.clone()].contains('\r'),
                "折り返し結果の範囲に \r が残っている"
            );
        }
    }

    /// 折り返し（max_width > 0）経路でも CRLF は 1 つの改行として扱われる
    /// （段落分割 = `split('\n')` の前で正規化されているため）。
    #[test]
    fn crlf_is_single_newline_when_wrapping_enabled() {
        let f = builtin();
        let font_size = 20.0;
        let w = measure_line_width(&f, "あいう", font_size) + 0.01;
        let lf = wrap_lines(&f, "あいうえお\nかきくけこ", font_size, w);
        let crlf = wrap_lines(&f, "あいうえお\r\nかきくけこ", font_size, w);
        assert_eq!(lf.len(), crlf.len(), "CRLF でも折り返し結果の行数が変わらない");
    }
}

// ============================================================
//  インライン画像を含む折り返しの単体テスト
//
//  実画像を用意せずに規則だけを検証するため、画像表は
//  `InlineImages::from_entries` で直接組み立てる。
// ============================================================

#[cfg(test)]
mod inline_image_tests {
    use super::*;
    use crate::engine::core::font::inline::doc::InlineImage;

    /// テスト用フォント（組み込みフォント）。
    fn builtin() -> FontArc {
        FontArc::try_from_slice(crate::engine::core::font::DEFAULT_FONT_BYTES)
            .expect("組み込みフォントを読める")
    }

    /// 行の文字列を取り出すヘルパ。
    fn texts<'a>(src: &'a str, lines: &[WrappedLine]) -> Vec<&'a str> {
        lines.iter().map(|l| &src[l.range.clone()]).collect()
    }

    /// 「幅 advance_em・高さ height_em の画像が 1 つ」の表を作る。
    fn one_image(offset: usize, advance_em: f32, height_em: f32) -> InlineImages {
        InlineImages::from_entries(vec![(
            offset,
            InlineImage {
                path: "assets://dummy.png".to_string(),
                advance_em,
                height_em,
            },
        )])
    }

    /// 画像の送り幅が行幅へ加算される（フォントの送り幅ではなく画像の幅が使われる）。
    #[test]
    fn image_advance_is_added_to_line_width() {
        let f = builtin();
        let font_size = 20.0;
        let text = format!("あ{IMAGE_PLACEHOLDER}");
        let offset = "あ".len();
        let images = one_image(offset, 2.0, 1.0);

        let with_img = wrap_lines_with_images(&f, &text, font_size, 0.0, &images);
        let plain = wrap_lines(&f, "あ", font_size, 0.0);
        assert_eq!(with_img.len(), 1);
        // 画像ぶん（2em = 40px）だけ広がる。代替文字自体の送り幅は使われない。
        let expected = plain[0].width + 2.0 * font_size;
        assert!(
            (with_img[0].width - expected).abs() < 1e-3,
            "行幅に画像の送り幅が入っていない（{} vs {}）",
            with_img[0].width,
            expected
        );
    }

    /// 画像は分割不可の 1 クラスタとして折り返しへ参加する。
    #[test]
    fn image_wraps_as_single_cluster() {
        let f = builtin();
        let font_size = 20.0;
        // 「あ」+ 画像(2em) + 「い」。幅は「あ」+ 画像 ちょうどぶん。
        let text = format!("あ{IMAGE_PLACEHOLDER}い");
        let img_off = "あ".len();
        let images = one_image(img_off, 2.0, 1.0);
        let a_w = measure_range_width(&f, "あ", 0.."あ".len(), font_size, InlineImages::empty());
        let max_w = a_w + 2.0 * font_size + 0.01;

        let lines = wrap_lines_with_images(&f, &text, font_size, max_w, &images);
        let got = texts(&text, &lines);
        assert_eq!(got.len(), 2, "画像の後ろで折り返る");
        assert!(got[0].ends_with(IMAGE_PLACEHOLDER), "1 行目は画像で終わる");
        assert_eq!(got[1], "い");
    }

    /// 枠より広い画像でも、1 行に必ず 1 クラスタは載る（無限ループしない）。
    #[test]
    fn oversized_image_still_advances() {
        let f = builtin();
        let font_size = 20.0;
        let text = format!("{IMAGE_PLACEHOLDER}あ");
        let images = one_image(0, 8.0, 1.0);
        let lines = wrap_lines_with_images(&f, &text, font_size, 1.0, &images);
        let got = texts(&text, &lines);
        assert_eq!(got.len(), 2);
        assert_eq!(got[0].chars().count(), 1, "画像だけで 1 行になる");
        assert_eq!(got[1], "あ");
    }

    /// 画像を含む段落でも明示改行はそのまま効く（範囲がズレない）。
    #[test]
    fn image_offsets_stay_valid_across_paragraphs() {
        let f = builtin();
        let font_size = 20.0;
        let text = format!("あ\nい{IMAGE_PLACEHOLDER}");
        let img_off = text.find(IMAGE_PLACEHOLDER).unwrap();
        let images = one_image(img_off, 3.0, 1.0);
        let lines = wrap_lines_with_images(&f, &text, font_size, 0.0, &images);
        assert_eq!(lines.len(), 2);
        // 2 行目にだけ画像ぶんの幅が乗る（2 行目の base オフセットで引けている証拠）。
        let i_w = measure_range_width(&f, "い", 0.."い".len(), font_size, InlineImages::empty());
        assert!((lines[1].width - (i_w + 3.0 * font_size)).abs() < 1e-3);
    }
}
