// ============================================================
//  ui_spike/config.rs — W2-0 スパイクの指定（文字列）を読む【純関数・単体テスト付き】
//
//  【書式】カンマ（またはセミコロン）区切りの項目の並び。空白は無視する。
//    idle=<N>        … 入力の無いフレームが N 回続いたら描画を止める（描かなくてよいときは描かない。X-2）
//    wake_ms=<M>     … 止めている間も M ミリ秒ごとに 1 フレームだけ描く（時計の表示のような定期の更新の模擬）。
//                      指定しなければ入力が来るまで眠る（ControlFlow::Wait）
//    clip=<名前>     … その名前のキャンバスノード（最初の有効なスプライトの矩形）で子孫を切り抜く（何度でも書ける）
//    ime             … 文字入力（IME）の試作を有効にする（PC: winit の Ime イベントのログ、Android: native の ime_probe）
//  例: "idle=30,wake_ms=1000"、"clip=ClipBox"、"ime,idle=60"
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

/// 切り抜きの根にするキャンバスノードの名前のキー。
const KEY_CLIP: &str = "clip";

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
    /// 子孫を切り抜くキャンバスノードの名前（空 = 切り抜かない）。
    pub clip_actor_names: Vec<String>,
    /// 文字入力（IME）の試作を有効にするか。
    pub ime: bool,
}

impl UiSpikeConfig {
    /// どれか 1 つでも有効か（ログの要否の判断用）。
    pub fn any_enabled(&self) -> bool {
        self.idle_after_frames.is_some() || !self.clip_actor_names.is_empty() || self.ime
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
                        KEY_CLIP => {
                            if value.is_empty() {
                                warnings.push(format!("{KEY_CLIP} にノードの名前がありません"));
                            } else if !config.clip_actor_names.iter().any(|name| name == value) {
                                config.clip_actor_names.push(value.to_string());
                            }
                        }
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
            UiSpikeConfig::parse_lenient(" idle=30 ; wake_ms = 1000, clip=ClipBox,clip=Inner, ime ");
        assert!(warnings.is_empty(), "{warnings:?}");
        assert_eq!(config.idle_after_frames, Some(30));
        assert_eq!(config.idle_wake_ms, Some(1000));
        assert_eq!(config.clip_actor_names, vec!["ClipBox".to_string(), "Inner".to_string()]);
        assert!(config.ime);
        assert!(config.any_enabled());
    }

    /// 同じ名前の clip は 1 つにまとめる。
    #[test]
    fn duplicate_clip_names_are_merged() {
        let (config, _) = UiSpikeConfig::parse_lenient("clip=A,clip=A");
        assert_eq!(config.clip_actor_names, vec!["A".to_string()]);
    }

    /// 読めない値・知らない項目は警告にし、読めた項目は活かす。
    #[test]
    fn bad_items_become_warnings() {
        let (config, warnings) = UiSpikeConfig::parse_lenient("idle=0,wake_ms=-5,clip=,bogus,foo=1,ime");
        assert_eq!(config.idle_after_frames, None, "0 は認めない");
        assert_eq!(config.idle_wake_ms, None, "負は認めない");
        assert!(config.clip_actor_names.is_empty());
        assert!(config.ime, "読めた項目は有効");
        assert_eq!(warnings.len(), 5, "{warnings:?}");
    }
}
