// ============================================================
//  platform/bridge/app/url_rules.rs — app.open_url で開いてよい URL かの判定（W1-6）
//
//  【規則】（Java の runtime/android/app/src/main/java/com/seedengine/runtime/platform/app/UrlPolicy.java と同じ。変えるときは両方）
//    1. 空でない・MAX_URL_LENGTH 文字（Unicode の符号位置）以下・制御文字（U+0000〜U+001F・U+007F）を含まない  … 外れたら invalid_argument
//    2. 先頭に scheme があり（最初の ':' より前）、形が RFC 3986 §3.1 の scheme（英字で始まり、英数字と + - . が続く）… 外れたら invalid_argument
//    3. scheme（小文字にそろえて比べる）が wire::app::DENIED_SCHEMES（file・content・javascript）なら scheme_not_allowed
//    4. それ以外（http・https・mailto・tel・アプリ独自の scheme）は通す（開けるアプリが無いかは開くときに分かる）
//  デスクトップの模擬は、通った URL のうち http / https / mailto だけを PC の既定のアプリで開く（desktop_sim/url_opener.rs）。
//  たとえば C:\Windows\x.exe は規則 2 では scheme "c" として通るが、PC では開かない（ファイルを実行させない）。
// ============================================================

use serde_json::Value;

use crate::engine::platform::bridge::wire::{alarm as alarm_names, app as names, MAX_URL_LENGTH};

/// scheme とその後ろを分ける文字。
const SCHEME_SEPARATOR: char = ':';

/// scheme の 2 文字目以降に使える記号（RFC 3986 §3.1: ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )）。
const SCHEME_SYMBOLS: [char; 3] = ['+', '-', '.'];

/// URL を断った理由（返答の error と detail）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum UrlRejection {
    /// 形が約束に合わない（invalid_argument）。中身は説明。
    Invalid(String),
    /// 断る scheme（scheme_not_allowed）。中身は説明。
    SchemeNotAllowed(String),
}

impl UrlRejection {
    /// 返答の error（理由の名前）。
    pub fn reason(&self) -> &'static str {
        match self {
            Self::Invalid(_) => alarm_names::ERROR_INVALID_ARGUMENT,
            Self::SchemeNotAllowed(_) => names::ERROR_SCHEME_NOT_ALLOWED,
        }
    }

    /// 返答の detail（説明）。
    pub fn detail(&self) -> &str {
        match self {
            Self::Invalid(detail) | Self::SchemeNotAllowed(detail) => detail,
        }
    }
}

/// open_url の引数 `{ url }` を読んで判定する。
///
/// # 戻り値
/// 開いてよければ Ok((URL, 小文字にそろえた scheme))。url が文字列でない・規則に合わなければ Err(理由)
pub fn read_open_url(request: &Value) -> Result<(String, String), UrlRejection> {
    let Some(url) = request.get(names::KEY_URL).and_then(Value::as_str) else {
        return Err(UrlRejection::Invalid(format!("{} は文字列にしてください", names::KEY_URL)));
    };
    let scheme = check_open_url(url)?;
    Ok((url.to_string(), scheme))
}

/// URL を判定する（規則はファイルの先頭）。
///
/// # 戻り値
/// 開いてよければ Ok(小文字にそろえた scheme)。だめなら Err(理由)
pub fn check_open_url(url: &str) -> Result<String, UrlRejection> {
    if url.is_empty() {
        return Err(UrlRejection::Invalid(format!("{} が空です", names::KEY_URL)));
    }
    if url.chars().count() > MAX_URL_LENGTH {
        return Err(UrlRejection::Invalid(format!("{} が長すぎます（{MAX_URL_LENGTH} 文字まで）", names::KEY_URL)));
    }
    // 制御文字: char::is_ascii_control は U+0000〜U+001F と U+007F（Java の UrlPolicy の「空白より小さいか DEL」と同じ範囲）
    if url.chars().any(|c| c.is_ascii_control()) {
        return Err(UrlRejection::Invalid(format!("{} に制御文字が入っています", names::KEY_URL)));
    }
    let Some(separator) = url.find(SCHEME_SEPARATOR).filter(|&index| index > 0) else {
        return Err(UrlRejection::Invalid(format!("{} に scheme がありません（例 https://…）", names::KEY_URL)));
    };
    let scheme = &url[..separator];
    if !is_scheme(scheme) {
        return Err(UrlRejection::Invalid(format!(
            "{} の scheme の形が正しくありません（英字で始まり、英数字と + - . が続く）",
            names::KEY_URL
        )));
    }
    let normalized = scheme.to_ascii_lowercase();
    if names::DENIED_SCHEMES.contains(&normalized.as_str()) {
        return Err(UrlRejection::SchemeNotAllowed(format!(
            "{normalized}: の URL は開けません（{} は断る）",
            names::DENIED_SCHEMES.join(" / ")
        )));
    }
    Ok(normalized)
}

/// RFC 3986 §3.1 の scheme の形か（英字で始まり、英数字と + - . が続く。空でないこと）。
fn is_scheme(scheme: &str) -> bool {
    let mut chars = scheme.chars();
    chars.next().is_some_and(|first| first.is_ascii_alphabetic())
        && chars.all(|c| c.is_ascii_alphanumeric() || SCHEME_SYMBOLS.contains(&c))
}

// ============================================================
//  ユニットテスト（Java の UrlPolicy を JVM で確かめたのと同じ入力）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// 通る URL: よくある scheme・独自の scheme・大文字（小文字にそろう）・記号入りの scheme・日本語入り。
    #[test]
    fn allowed_urls_return_lowercase_scheme() {
        let cases = [
            ("https://example.com", "https"),
            ("http://example.com/a?b=1&c=%20", "http"),
            ("mailto:someone@example.com", "mailto"),
            ("mailto:", "mailto"),
            ("tel:+81-90-0000-0000", "tel"),
            ("wakeorpay://alarm/x", "wakeorpay"),
            ("HTTPS://EXAMPLE.COM", "https"),
            ("my-app.v2+x:go", "my-app.v2+x"),
            ("https://例え.jp/パス", "https"),
            // Windows のドライブ文字は 1 文字の scheme として通る（PC の模擬は http / https / mailto 以外を開かない）
            (r"C:\Windows\notepad.exe", "c"),
        ];
        for (url, scheme) in cases {
            assert_eq!(check_open_url(url), Ok(scheme.to_string()), "{url}");
        }
    }

    /// 断る scheme: file / content / javascript（大文字でも）。理由は scheme_not_allowed。
    #[test]
    fn denied_schemes_are_rejected() {
        for url in ["file:///sdcard/x", "FILE:///x", "content://com.example/x", "javascript:alert(1)", "JavaScript:alert(1)"] {
            let rejection = check_open_url(url).unwrap_err();
            assert!(matches!(rejection, UrlRejection::SchemeNotAllowed(_)), "{url}");
            assert_eq!(rejection.reason(), names::ERROR_SCHEME_NOT_ALLOWED);
        }
    }

    /// 形の誤り: 空・scheme なし・先頭が ':'・数字で始まる・空白・制御文字（改行・タブ・DEL）。理由は invalid_argument。
    #[test]
    fn malformed_urls_are_invalid() {
        let delete = char::from(0x7f_u8);
        let cases = [
            String::new(),
            "example.com".to_string(),
            ":nothing".to_string(),
            "1http://x".to_string(),
            " https://x".to_string(),
            "ht tp://x".to_string(),
            "https://a\nb".to_string(),
            "https://a\tb".to_string(),
            format!("https://a{delete}b"),
        ];
        for url in cases {
            let rejection = check_open_url(&url).unwrap_err();
            assert!(matches!(rejection, UrlRejection::Invalid(_)), "{url:?}");
            assert_eq!(rejection.reason(), alarm_names::ERROR_INVALID_ARGUMENT);
        }
    }

    /// 長さは符号位置で数える（上限ちょうどは通り、1 文字超えると誤り。絵文字も 1 文字）。
    #[test]
    fn length_limit_counts_code_points() {
        let prefix = "https://x/";
        let fill = MAX_URL_LENGTH - prefix.chars().count();
        let emoji = char::from_u32(0x1F600).expect("絵文字の符号位置");
        assert!(check_open_url(&format!("{prefix}{}", "a".repeat(fill))).is_ok());
        assert!(matches!(check_open_url(&format!("{prefix}{}", "a".repeat(fill + 1))), Err(UrlRejection::Invalid(_))));
        assert!(check_open_url(&format!("{prefix}{}", emoji.to_string().repeat(fill))).is_ok());
        assert!(matches!(check_open_url(&format!("{prefix}{}", emoji.to_string().repeat(fill + 1))), Err(UrlRejection::Invalid(_))));
    }

    /// 引数: url が無い・文字列でないは invalid_argument（説明に欄の名前）。通れば URL と scheme。
    #[test]
    fn read_open_url_rules() {
        assert_eq!(
            read_open_url(&json!({ "url": "https://example.com" })),
            Ok(("https://example.com".to_string(), "https".to_string()))
        );
        for bad in [json!({}), json!({ "url": 7 }), json!({ "url": null })] {
            let rejection = read_open_url(&bad).unwrap_err();
            assert!(rejection.detail().contains(names::KEY_URL), "{bad}");
            assert_eq!(rejection.reason(), alarm_names::ERROR_INVALID_ARGUMENT);
        }
    }
}
