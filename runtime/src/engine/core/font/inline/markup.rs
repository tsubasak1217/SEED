// ============================================================
//  font/inline/markup.rs — 本文中のインライン画像記法パーサ（純関数）
//
//  【役割】
//  `TextComponent.content` に書かれた「文字の並びの中に画像を混ぜる記法」を、
//  トークン列（通常テキスト／アイコン／画像）へ分解する。
//  **アセットを一切読まない**（名前・パスの解決は `icon_set.rs` / `doc.rs` の責務）。
//  したがって GPU もファイルシステムも不要で、そのまま単体テストできる。
//
//  【記法（確定仕様）】
//    [icon:名前]              … アイコンセット（.icons）の名前で引く
//    [img:assets://path.png]  … 画像パスを直接指定する
//    [icon:名前 h=1.4]        … 高さ倍率（画像の高さ = フォントサイズ × h）
//    [img:assets://a.png h=0.8]
//    バックスラッシュ + [     … 「[」そのものを描くエスケープ
//
//  【不正な記法の扱い（本文が崩れないことを最優先する）】
//  - `[` で始まるが `icon:` / `img:` のどちらでもない → **そのまま通常文字**として描く
//  - `]` が見つからない（未閉じ）                     → **そのまま通常文字**として描く
//  - 単独の `]`                                       → **そのまま通常文字**として描く
//  - 名前・パスが空（`[icon:]`）                      → 「未解決の画像」トークン
//    （解決側が 1em の空白を確保して警告を 1 回出す。記法としては正しいため）
//
//  【なぜトークン化と解決を分けるか】
//  記法の文法（ここ）とアセット解決（`doc.rs`）は変更理由がまったく別である。
//  同居させるとパーサのテストにファイル I/O が必要になり、記法の回帰テストが書けなくなる。
// ============================================================

// ─── 記法の定数（マジックナンバー・マジックストリング禁止）──────

/// トークンの開始文字。
pub const TOKEN_OPEN: char = '[';

/// トークンの終了文字。
pub const TOKEN_CLOSE: char = ']';

/// エスケープ文字のコードポイント（U+005C = バックスラッシュ）。
pub const ESCAPE_CHAR_CODE: u8 = 0x5C;

/// エスケープ文字（バックスラッシュ）。直後が `[` のときだけ意味を持つ。
pub const ESCAPE_CHAR: char = ESCAPE_CHAR_CODE as char;

/// アイコンセット参照トークンの接頭辞。
pub const ICON_PREFIX: &str = "icon:";

/// 画像パス直接指定トークンの接頭辞。
pub const IMG_PREFIX: &str = "img:";

/// 高さ倍率オプションの区切り（本体と `h=` の間には必ず空白を置く）。
///
/// 空白を必須にしているのは、画像パスに `h=` を含む文字列が来ても
/// 誤ってオプションと解釈しないため。
pub const HEIGHT_OPTION_SEPARATOR: &str = " h=";

/// 高さ倍率の既定値（画像の高さ = フォントサイズ × これ）。
pub const DEFAULT_HEIGHT_SCALE: f32 = 1.0;

/// 高さ倍率の下限（0 以下・極小はレイアウトが破綻するため丸める）。
pub const MIN_HEIGHT_SCALE: f32 = 0.05;

/// 高さ倍率の上限（誤入力で 1 行が画面外まで伸びるのを防ぐ）。
pub const MAX_HEIGHT_SCALE: f32 = 16.0;

// ─── トークン ─────────────────────────────────────────────────

/// 本文を分解した 1 トークン。
#[derive(Clone, Debug, PartialEq)]
pub enum InlineToken {
    /// 通常の文字列（エスケープ解除済み。そのままグリフとして描く）。
    Text(String),
    /// アイコンセットの名前による画像参照。
    Icon {
        /// アイコン名（`.icons` の `icons` テーブルのキー）。
        name: String,
        /// 記法で明示された高さ倍率。`None` = アイコンセット／既定値に従う。
        height_scale: Option<f32>,
    },
    /// 画像パスの直接指定。
    Image {
        /// 画像の assets:// パス。
        path: String,
        /// 記法で明示された高さ倍率。`None` = 既定値。
        height_scale: Option<f32>,
    },
}

// ─── パース本体 ────────────────────────────────────────────────

/// 本文をトークン列へ分解する（純関数。アセットは読まない）。
///
/// 連続する通常文字は 1 つの `Text` トークンへまとめる
/// （後段が文字列を組み立てるときの再確保を減らすため）。
pub fn parse_markup(text: &str) -> Vec<InlineToken> {
    let mut out: Vec<InlineToken> = Vec::new();
    // 通常文字の貯め込みバッファ。トークンが出たとき／末尾で吐き出す。
    let mut buf = String::new();
    // バイトオフセットで走査する（部分文字列の切り出しに使うため）。
    let bytes_len = text.len();
    let mut i = 0usize;

    while i < bytes_len {
        let rest = &text[i..];
        let ch = rest.chars().next().expect("i は必ず文字境界を指す");

        // ── エスケープ: バックスラッシュ + `[` は「[」そのもの ──
        if ch == ESCAPE_CHAR {
            let after = &rest[ch.len_utf8()..];
            if let Some(next) = after.chars().next() {
                if next == TOKEN_OPEN {
                    buf.push(TOKEN_OPEN);
                    i += ch.len_utf8() + next.len_utf8();
                    continue;
                }
            }
            // `[` 以外が続くバックスラッシュは特別扱いしない（そのまま描く）。
            buf.push(ch);
            i += ch.len_utf8();
            continue;
        }

        // ── トークン開始 ──
        if ch == TOKEN_OPEN {
            match take_token_body(text, i) {
                Some((body, next_i)) => {
                    match classify_body(body) {
                        Some(token) => {
                            // 貯め込んだ通常文字を先に確定させてからトークンを積む。
                            flush_text(&mut buf, &mut out);
                            out.push(token);
                        }
                        // `[...]` ではあるが記法ではない → 角括弧ごと通常文字として描く。
                        None => buf.push_str(&text[i..next_i]),
                    }
                    i = next_i;
                    continue;
                }
                // 未閉じ `[` → 通常文字。
                None => {
                    buf.push(ch);
                    i += ch.len_utf8();
                    continue;
                }
            }
        }

        // ── それ以外（単独の `]` を含む）は通常文字 ──
        buf.push(ch);
        i += ch.len_utf8();
    }

    flush_text(&mut buf, &mut out);
    out
}

/// 貯め込んだ通常文字を `Text` トークンとして確定させる（空なら何もしない）。
fn flush_text(buf: &mut String, out: &mut Vec<InlineToken>) {
    if !buf.is_empty() {
        out.push(InlineToken::Text(std::mem::take(buf)));
    }
}

/// `open_at`（`[` の位置）から対応する `]` までの中身を取り出す。
///
/// 戻り値は「中身」と「`]` の次のバイトオフセット」。`]` が無ければ `None`。
/// 入れ子は考えない（画像パスに `]` は入らない前提。入れ子記法も無い）。
fn take_token_body(text: &str, open_at: usize) -> Option<(&str, usize)> {
    let body_start = open_at + TOKEN_OPEN.len_utf8();
    let rel_close = text[body_start..].find(TOKEN_CLOSE)?;
    let close_at = body_start + rel_close;
    Some((&text[body_start..close_at], close_at + TOKEN_CLOSE.len_utf8()))
}

/// `[...]` の中身を記法として解釈する。記法でなければ `None`。
fn classify_body(body: &str) -> Option<InlineToken> {
    if let Some(rest) = body.strip_prefix(ICON_PREFIX) {
        let (name, height_scale) = split_height_option(rest);
        return Some(InlineToken::Icon {
            name: name.to_string(),
            height_scale,
        });
    }
    if let Some(rest) = body.strip_prefix(IMG_PREFIX) {
        let (path, height_scale) = split_height_option(rest);
        return Some(InlineToken::Image {
            path: path.to_string(),
            height_scale,
        });
    }
    None
}

/// 本体（名前 or パス）と高さ倍率オプションを切り分ける。
///
/// 区切りは **最後の** ` h=` を使う（パス自体に空白を含めても壊れないように）。
/// 数値としてパースできない場合はオプションとみなさず、全体を本体として扱う。
/// 倍率は `MIN_HEIGHT_SCALE`..`MAX_HEIGHT_SCALE` へ丸める。
fn split_height_option(body: &str) -> (&str, Option<f32>) {
    if let Some(sep) = body.rfind(HEIGHT_OPTION_SEPARATOR) {
        let value_str = body[sep + HEIGHT_OPTION_SEPARATOR.len()..].trim();
        if let Ok(v) = value_str.parse::<f32>() {
            if v.is_finite() {
                return (
                    body[..sep].trim(),
                    Some(v.clamp(MIN_HEIGHT_SCALE, MAX_HEIGHT_SCALE)),
                );
            }
        }
    }
    (body.trim(), None)
}

// ============================================================
//  単体テスト（アセット不要の純関数）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 文字列トークンだけを連結して取り出すヘルパ。
    fn text_of(tokens: &[InlineToken]) -> String {
        tokens
            .iter()
            .filter_map(|t| match t {
                InlineToken::Text(s) => Some(s.as_str()),
                _ => None,
            })
            .collect()
    }

    /// 記法が無い本文はそのまま 1 つの Text トークンになる。
    #[test]
    fn plain_text_is_single_token() {
        let t = parse_markup("こんにちは world");
        assert_eq!(t, vec![InlineToken::Text("こんにちは world".into())]);
    }

    /// `[icon:名前]` はアイコントークンになり、前後の文字が保たれる。
    #[test]
    fn icon_token_is_parsed() {
        let t = parse_markup("押す[icon:key_w]で移動");
        assert_eq!(
            t,
            vec![
                InlineToken::Text("押す".into()),
                InlineToken::Icon {
                    name: "key_w".into(),
                    height_scale: None
                },
                InlineToken::Text("で移動".into()),
            ]
        );
    }

    /// `[img:パス]` は画像トークンになる（パスはそのまま保持）。
    #[test]
    fn img_token_is_parsed() {
        let t = parse_markup("[img:assets://ui/a.png]");
        assert_eq!(
            t,
            vec![InlineToken::Image {
                path: "assets://ui/a.png".into(),
                height_scale: None
            }]
        );
    }

    /// 高さ倍率 `h=` が読める（アイコン・画像の両方）。
    #[test]
    fn height_option_is_parsed() {
        let t = parse_markup("[icon:key_w h=1.4][img:assets://a.png h=0.8]");
        assert_eq!(
            t,
            vec![
                InlineToken::Icon {
                    name: "key_w".into(),
                    height_scale: Some(1.4)
                },
                InlineToken::Image {
                    path: "assets://a.png".into(),
                    height_scale: Some(0.8)
                },
            ]
        );
    }

    /// 高さ倍率は上下限へ丸められる（誤入力で行が壊れない）。
    #[test]
    fn height_option_is_clamped() {
        let t = parse_markup("[icon:a h=999][icon:b h=-3]");
        assert_eq!(
            t,
            vec![
                InlineToken::Icon {
                    name: "a".into(),
                    height_scale: Some(MAX_HEIGHT_SCALE)
                },
                InlineToken::Icon {
                    name: "b".into(),
                    height_scale: Some(MIN_HEIGHT_SCALE)
                },
            ]
        );
    }

    /// 数値でない `h=` はオプションとみなさず、本体の一部として残す。
    #[test]
    fn non_numeric_height_option_is_not_an_option() {
        let t = parse_markup("[img:assets://a b h=xyz.png]");
        assert_eq!(
            t,
            vec![InlineToken::Image {
                path: "assets://a b h=xyz.png".into(),
                height_scale: None
            }]
        );
    }

    /// 記法でない角括弧はそのまま通常文字として描く。
    #[test]
    fn unknown_bracket_is_plain_text() {
        let t = parse_markup("配列[0]と[note:x]");
        assert_eq!(text_of(&t), "配列[0]と[note:x]");
        assert_eq!(t.len(), 1, "画像トークンは 1 つも出ない");
    }

    /// 未閉じの `[` は通常文字として描く（以降が消えない）。
    #[test]
    fn unclosed_bracket_is_plain_text() {
        let t = parse_markup("残り[icon:key_w");
        assert_eq!(text_of(&t), "残り[icon:key_w");
        assert_eq!(t.len(), 1);
    }

    /// 単独の `]` も通常文字。
    #[test]
    fn lone_close_bracket_is_plain_text() {
        let t = parse_markup("a]b");
        assert_eq!(text_of(&t), "a]b");
    }

    /// エスケープ（バックスラッシュ + `[`）は「[」そのものになる。
    #[test]
    fn escaped_open_bracket_is_literal() {
        let t = parse_markup(r"\[icon:key_w]");
        assert_eq!(text_of(&t), "[icon:key_w]");
        assert_eq!(t.len(), 1, "エスケープしたのでトークンにならない");
    }

    /// `[` 以外が続くバックスラッシュはそのまま残る（Windows パス等を壊さない）。
    #[test]
    fn other_backslash_is_kept() {
        let t = parse_markup(r"C:\temp\a");
        assert_eq!(text_of(&t), r"C:\temp\a");
    }

    /// 空の名前でもトークンにはなる（解決側が「未解決」として扱う）。
    #[test]
    fn empty_name_is_still_a_token() {
        let t = parse_markup("[icon:]");
        assert_eq!(
            t,
            vec![InlineToken::Icon {
                name: String::new(),
                height_scale: None
            }]
        );
    }

    /// 名前の前後の空白は取り除く。
    #[test]
    fn name_is_trimmed() {
        let t = parse_markup("[icon: key_w ]");
        assert_eq!(
            t,
            vec![InlineToken::Icon {
                name: "key_w".into(),
                height_scale: None
            }]
        );
    }
}
