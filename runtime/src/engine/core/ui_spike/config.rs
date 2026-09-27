// ============================================================
//  ui_spike/config.rs — W2-0 スパイクの指定（文字列）を読む【純関数・単体テスト付き】
//
//  【書式】カンマ（またはセミコロン）区切りの項目の並び。空白は無視する。
//    idle=<N>        … 入力の無いフレームが N 回続いたら描画を止める（描かなくてよいときは描かない。X-2）
//    wake_ms=<M>     … 止めている間も M ミリ秒ごとに 1 フレームだけ描く（時計の表示のような定期の更新の模擬）。
//                      指定しなければ入力が来るまで眠る（ControlFlow::Wait）
//    ime             … 文字入力（IME）の試作を有効にする（PC: winit の Ime イベントのログ、Android: native の ime_probe）
//  例: "idle=30,wake_ms=1000"、"ime,idle=60"
//  （W2-0 の clip=<名前> は W2-1a で本番の CanvasClipComponent に置き換えて外した。今は知らない項目として警告になる）
//
//  【読めない項目】起動を止めない（スパイクのための指定で本番の振る舞いに関わらないため）。
//  読めた項目だけを採り、読めなかった項目は警告の文にして返す（呼び出し側がログへ出す）。
// ============================================================

/// 項目の区切り（カンマ）。
const ITEM_SEPARATOR: char = ',';

/// 項目の区切り（セミコロン。PowerShell などでカンマを避けたいとき用）。
const ITEM_SEPARATOR_ALT: char = ';';

/// キーと値の区切り。
const KEY_VALUE_SEPARATOR: char = '=';

/// 描画を止めるまでの無入力フレーム数のキー。
const KEY_IDLE: &str = "idle";

/// 止めている間に起こす間隔（ミリ秒）のキー。
const KEY_WAKE_MS: &str = "wake_ms";

/// 文字入力（IME）の試作の旗。
const FLAG_IME: &str = "ime";

/// 無入力フレーム数の下限（0 は「毎フレーム止める」になり操作できないため認めない）。
const MIN_IDLE_FRAMES: u32 = 1;

/// 起こす間隔の下限（ミリ秒。0 は「止めない」と同じなので認めない）。
const MIN_WAKE_MS: u32 = 1;

/// W2-0 スパイクの指定（既定はすべて無効＝従来どおりの振る舞い）。
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UiSpikeConfig {
    /// 入力の無いフレームがこの数だけ続いたら描画を止める（None = 止めない）。
    pub idle_after_frames: Option<u32>,
    /// 止めている間に起こす間隔（ミリ秒。None = 入力が来るまで起こさない）。
    pub idle_wake_ms: Option<u32>,
    /// 文字入力（IME）の試作を有効にするか。
    pub ime: bool,
}

impl UiSpikeConfig {
    /// どれか 1 つでも有効か（ログの要否の判断用）。
    pub fn any_enabled(&self) -> bool {
        self.idle_after_frames.is_some() || self.ime
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
                Some((key, value)) => {
                    let (key, value) = (key.trim(), value.trim());
                    match key {
                        KEY_IDLE => match parse_at_least(value, MIN_IDLE_FRAMES) {
                            Some(frames) => config.idle_after_frames = Some(frames),
                            None => warnings.push(format!(
                                "{KEY_IDLE} は {MIN_IDLE_FRAMES} 以上の整数にしてください（受け取った値: {value:?}）"
                            )),
                        },
                        KEY_WAKE_MS => match parse_at_least(value, MIN_WAKE_MS) {
                            Some(ms) => config.idle_wake_ms = Some(ms),
                            None => warnings.push(format!(
                                "{KEY_WAKE_MS} は {MIN_WAKE_MS} 以上の整数（ミリ秒）にしてください（受け取った値: {value:?}）"
                            )),
                        },
                        other => warnings.push(format!("知らない項目です: {other:?}")),
                    }
                }
                None if item == FLAG_IME => config.ime = true,
                None => warnings.push(format!("知らない項目です: {item:?}")),
            }
        }
        (config, warnings)
    }
}

/// 10 進の整数を読み、下限以上なら返す【純関数】。
fn parse_at_least(text: &str, min: u32) -> Option<u32> {
    text.parse::<u32>().ok().filter(|value| *value >= min)
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

    /// すべての項目を読める（区切りはカンマ・セミコロンのどちらでもよい）。
    #[test]
    fn parses_all_items() {
        let (config, warnings) =
            UiSpikeConfig::parse_lenient(" idle=30 ; wake_ms = 1000, ime ");
        assert!(warnings.is_empty(), "{warnings:?}");
        assert_eq!(config.idle_after_frames, Some(30));
        assert_eq!(config.idle_wake_ms, Some(1000));
        assert!(config.ime);
        assert!(config.any_enabled());
    }

    /// W2-0 の clip=<名前> は外した（本番の CanvasClipComponent へ置き換えた）ので、知らない項目の警告になる。
    #[test]
    fn removed_clip_item_is_a_warning() {
        let (config, warnings) = UiSpikeConfig::parse_lenient("clip=A,idle=5");
        assert_eq!(config.idle_after_frames, Some(5), "読めた項目は有効");
        assert_eq!(warnings.len(), 1, "{warnings:?}");
    }

    /// 読めない値・知らない項目は警告にし、読めた項目は活かす。
    #[test]
    fn bad_items_become_warnings() {
        let (config, warnings) = UiSpikeConfig::parse_lenient("idle=0,wake_ms=-5,clip=,bogus,foo=1,ime");
        assert_eq!(config.idle_after_frames, None, "0 は認めない");
        assert_eq!(config.idle_wake_ms, None, "負は認めない");
        assert!(config.ime, "読めた項目は有効");
        assert_eq!(warnings.len(), 5, "{warnings:?}");
    }
}
