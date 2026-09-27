// ============================================================
//  ui_spike/config.rs — W2-0 スパイクの指定（文字列）を読む【純関数・単体テスト付き】
//
//  【書式】カンマ（またはセミコロン）区切りの項目の並び。空白は無視する。
//    ime             … 文字入力（IME）の試作を有効にする（PC: winit の Ime イベントのログ、Android: native の ime_probe）
//  例: "ime"
//  （W2-0 の clip=<名前> は W2-1a で本番の CanvasClipComponent に、idle=<N>・wake_ms=<M> は W2-10a で本番の
//   render_policy〈project_settings.json・SEED.Redraw〉に置き換えて外した。今はどれも知らない項目として警告になる）
//
//  【読めない項目】起動を止めない（スパイクのための指定で本番の振る舞いに関わらないため）。
//  読めた項目だけを採り、読めなかった項目は警告の文にして返す（呼び出し側がログへ出す）。
// ============================================================

/// 項目の区切り（カンマ）。
const ITEM_SEPARATOR: char = ',';

/// 項目の区切り（セミコロン。PowerShell などでカンマを避けたいとき用）。
const ITEM_SEPARATOR_ALT: char = ';';

/// キーと値の区切り（今の項目は旗だけだが、外した idle=・clip= などを「知らない項目」と分かる形で警告するために使う）。
const KEY_VALUE_SEPARATOR: char = '=';

/// 文字入力（IME）の試作の旗。
const FLAG_IME: &str = "ime";

/// W2-0 スパイクの指定（既定はすべて無効＝従来どおりの振る舞い）。
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UiSpikeConfig {
    /// 文字入力（IME）の試作を有効にするか。
    pub ime: bool,
}

impl UiSpikeConfig {
    /// どれか 1 つでも有効か（ログの要否の判断用）。
    pub fn any_enabled(&self) -> bool {
        self.ime
    }

    /// 指定の文字列を読む【純関数】。
    ///
    /// # 引数
    /// * `spec` - 書式はファイル冒頭の説明のとおり（空文字は「指定なし」）
    ///
    /// # 戻り値
    /// (読めた指定, 読めなかった項目の警告の文)。警告があっても読めた項目は有効にする。
    pub fn parse_lenient(spec: &str) -> (Self, Vec<String>) {
        let mut config = Self::default();
        let mut warnings = Vec::new();
        for raw in spec.split([ITEM_SEPARATOR, ITEM_SEPARATOR_ALT]) {
            let item = raw.trim();
            if item.is_empty() {
                continue;
            }
            match item.split_once(KEY_VALUE_SEPARATOR) {
                Some((key, _)) => warnings.push(format!(
                    "知らない項目です: {:?}（idle=・wake_ms= は render_policy に、clip= は CanvasClip に置き換えました）",
                    key.trim()
                )),
                None if item == FLAG_IME => config.ime = true,
                None => warnings.push(format!("知らない項目です: {item:?}")),
            }
        }
        (config, warnings)
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 空・空白だけは「指定なし」（既定＝すべて無効）で警告も無い。
    #[test]
    fn empty_spec_is_default() {
        for spec in ["", "   ", ",,", " ; "] {
            let (config, warnings) = UiSpikeConfig::parse_lenient(spec);
            assert_eq!(config, UiSpikeConfig::default(), "spec={spec:?}");
            assert!(!config.any_enabled());
            assert!(warnings.is_empty(), "spec={spec:?}");
        }
    }

    /// ime を読める（区切りはカンマ・セミコロンのどちらでもよい）。
    #[test]
    fn parses_ime() {
        let (config, warnings) = UiSpikeConfig::parse_lenient(" ; ime ,");
        assert!(warnings.is_empty(), "{warnings:?}");
        assert!(config.ime);
        assert!(config.any_enabled());
    }

    /// 外した項目（clip= は W2-1a、idle=・wake_ms= は W2-10a）と知らない項目は警告にし、読めた項目は活かす。
    #[test]
    fn removed_and_unknown_items_are_warnings() {
        let (config, warnings) = UiSpikeConfig::parse_lenient("clip=A,idle=30,wake_ms=1000,bogus,ime");
        assert!(config.ime, "読めた項目は有効");
        assert_eq!(warnings.len(), 4, "{warnings:?}");
    }
}
