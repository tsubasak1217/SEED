// ============================================================
//  font/inline/slot_format.rs — スロット値を本文へ差し込む文字列にする
//
//  【役割】
//  数値スロットの書式（四捨五入・小数桁）と、差し込み文字列の長さ制限だけを持つ。
//  展開器（`doc.rs`）から切り離してあるのは、書式の規則がゲーム側の
//  「見た目の約束」であり、記法の解析ともレイアウトとも変更理由が違うため。
//
//  【四捨五入の規約】
//  half-away-from-zero（0.5 は絶対値が大きい側へ）。
//  Rust の `{:.n}` 書式は「偶数丸め（銀行家丸め）」なので、
//  **先に丸めてから**桁を固定する（2.5 が 2 になる事故を防ぐ）。
//
//  【特殊値】
//  NaN / 無限大 / 負のゼロは、そのまま書式化すると "NaN" "inf" "-0" と
//  出てゲーム画面として読めないため、明示的に置き換える。
// ============================================================

// ─── 定数（マジックナンバー・マジックストリング禁止）──────────

/// スロット 1 件が差し込める最大文字数（char 単位）。
///
/// バインド先の文字列が事故で巨大になっても、レイアウト計算と
/// アトラス登録がフレームを食い潰さないための安全弁。
/// 本文全体の上限（`MAX_TEXT_CHARS`）とは別に、1 件単位で先に切る。
pub const MAX_SLOT_INJECTED_CHARS: usize = 256;

/// 非数（NaN）の表示。
pub const NAN_TEXT: &str = "NaN";

/// 正の無限大の表示。
pub const INFINITY_TEXT: &str = "∞";

/// 負の無限大の表示。
pub const NEG_INFINITY_TEXT: &str = "-∞";

/// 四捨五入に使う基数（10 進数の桁送り）。
const DECIMAL_RADIX: f64 = 10.0;

/// 数値スロットの値を本文へ差し込む文字列にする。
///
/// - `decimals` = 0 なら整数（四捨五入）。
/// - half-away-from-zero で丸めてから桁を固定する。
/// - NaN / 無限大 / 負のゼロは読める表記へ置き換える。
///
/// 丸めは f64 で行うが、入力が f32 である以上「10 進で見たときの
/// ちょうど半分」は元から表現できないことがある（例: f32 の 0.35 は
/// 実際には 0.3499999... なので 0.3 へ丸まる）。これは仕様上の限界であり、
/// 表示専用の値には十分な精度である。
pub fn format_number(value: f32, decimals: u32) -> String {
    if value.is_nan() {
        return NAN_TEXT.to_string();
    }
    if value.is_infinite() {
        return if value.is_sign_positive() {
            INFINITY_TEXT.to_string()
        } else {
            NEG_INFINITY_TEXT.to_string()
        };
    }
    let factor = DECIMAL_RADIX.powi(decimals as i32);
    // f64::round は half-away-from-zero（Rust の定義）。
    let rounded = (value as f64 * factor).round() / factor;
    // 負のゼロを正のゼロへ寄せる（-0.0 == 0.0 なのでこの比較で拾える）。
    let rounded = if rounded == 0.0 { 0.0 } else { rounded };
    format!("{:.*}", decimals as usize, rounded)
}

/// 差し込み文字列を上限文字数へ切り詰める（char 境界で安全に切る）。
///
/// 上限内ならコピーせずそのまま借用を返す。
pub fn clamp_injected(text: &str) -> std::borrow::Cow<'_, str> {
    // char 数の計算は上限まで数えれば十分（巨大文字列を全走査しない）。
    let mut iter = text.char_indices();
    match iter.nth(MAX_SLOT_INJECTED_CHARS) {
        Some((byte, _)) => std::borrow::Cow::Owned(text[..byte].to_string()),
        None => std::borrow::Cow::Borrowed(text),
    }
}

// ============================================================
//  単体テスト（純関数）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 小数桁 0 は整数へ四捨五入される（0.5 は絶対値の大きい側へ）。
    #[test]
    fn zero_decimals_rounds_half_away_from_zero() {
        assert_eq!(format_number(2.5, 0), "3", "銀行家丸めなら 2 になるので誤り");
        assert_eq!(format_number(3.5, 0), "4");
        assert_eq!(format_number(-2.5, 0), "-3");
        assert_eq!(format_number(2.4, 0), "2");
        assert_eq!(format_number(-2.4, 0), "-2");
    }

    /// 小数桁を指定すると必ずその桁数で出る（0 埋めを含む）。
    #[test]
    fn decimals_are_fixed_width() {
        assert_eq!(format_number(1.0, 3), "1.000");
        assert_eq!(format_number(1.23456, 3), "1.235");
        assert_eq!(format_number(-1.23456, 3), "-1.235");
    }

    /// NaN と無限大は読める表記へ置き換わる。
    #[test]
    fn special_values_are_readable() {
        assert_eq!(format_number(f32::NAN, 0), NAN_TEXT);
        assert_eq!(format_number(f32::INFINITY, 2), INFINITY_TEXT);
        assert_eq!(format_number(f32::NEG_INFINITY, 2), NEG_INFINITY_TEXT);
    }

    /// 負のゼロは "0"（"-0" と出さない）。
    #[test]
    fn negative_zero_is_plain_zero() {
        assert_eq!(format_number(-0.0, 0), "0");
        assert_eq!(format_number(-0.0, 2), "0.00");
        // 丸めた結果が 0 になる負の値も同じ扱い。
        assert_eq!(format_number(-0.004, 2), "0.00");
    }

    /// 上限以内の文字列はコピーされずそのまま返る。
    #[test]
    fn short_text_is_borrowed() {
        let s = "所持金";
        assert!(matches!(clamp_injected(s), std::borrow::Cow::Borrowed(_)));
        assert_eq!(clamp_injected(s), s);
    }

    /// 上限を超えた文字列は上限ちょうどで切られる（マルチバイトでも壊れない）。
    #[test]
    fn long_text_is_truncated_at_char_boundary() {
        let s: String = std::iter::repeat_n('あ', MAX_SLOT_INJECTED_CHARS + 10).collect();
        let cut = clamp_injected(&s);
        assert_eq!(cut.chars().count(), MAX_SLOT_INJECTED_CHARS);
        assert!(cut.chars().all(|c| c == 'あ'), "文字が壊れていない");
    }

    /// ちょうど上限の長さは切られない。
    #[test]
    fn exact_limit_is_kept() {
        let s: String = std::iter::repeat_n('a', MAX_SLOT_INJECTED_CHARS).collect();
        assert_eq!(clamp_injected(&s).chars().count(), MAX_SLOT_INJECTED_CHARS);
    }
}
