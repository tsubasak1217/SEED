// ============================================================
//  ui_spike/ — アプリ基盤 W2-0 のスパイク（既定で無効の試作。結果と決定は docs/app_platform_roadmap.md §3.8）
//
//  【目的】
//  W2（UI 部品群）でいちばん不確かな 3 点を、本番の振る舞いを変えずに小さく試すための指定と判定を集める。
//    1. 描かなくてよいときは描かない（X-2・W2-10 の前提）… idle_redraw.rs（入力の無いフレームが続いたら次のフレームを要求しない）
//       App への組み込みは app/ui_spike_hooks.rs
//    2. 切り抜き（クリップ）の描画 … 名前で指定したキャンバスノードの矩形で子孫を scissor で切る
//       （仕組みは本番の候補として renderer/ui_clip.rs。ここは「どのノードを根にするか」の指定だけ）
//    3. 文字入力（IME）… PC は winit の Ime イベントのログ（app/ui_spike_hooks.rs）、
//       Android は runtime/android/native/src/ui_spike/（android-activity の API を winit の外から使う）
//
//  【有効にする方法】（どれも指定しなければ何もしない＝従来どおり）
//    PC      : SEED.exe --ui-spike=<指定>  か 環境変数 SEED_UI_SPIKE=<指定>
//    Android : am start … --es seed.ui_spike '<指定>'（起動オプションを渡すのはデバッグ版の APK だけ）
//  指定の書式は config.rs（例: "idle=30,wake_ms=1000"、"clip=ClipBox"、"ime"）。
//
//  【指定の受け渡し】起動時に 1 回だけ `install` し、以後は `config()` で読むだけ（実行中に変わらない）。
//  キャンバスの収集（app/canvas_collect.rs）は App を参照できない自由関数なので、プロセスで 1 つの置き場にした。
// ============================================================

pub mod config;
pub mod idle_redraw;

use std::sync::OnceLock;

pub use config::UiSpikeConfig;

/// PC の起動引数の接頭辞（`--ui-spike=<指定>`）。
pub const CLI_ARG_PREFIX: &str = "--ui-spike=";

/// PC の環境変数の名前（起動引数が無いときに読む）。
pub const ENV_VAR: &str = "SEED_UI_SPIKE";

/// ログの印。
pub const LOG_PREFIX: &str = "[SEED UI SPIKE]";

/// 起動時に決めた指定（install で 1 回だけ入る）。
static CONFIG: OnceLock<UiSpikeConfig> = OnceLock::new();

/// 指定の文字列を読んで置き場へ入れる（起動時に 1 回だけ。2 回目以降は無視して警告する）。
///
/// # 引数
/// * `spec` - 指定の文字列（None・空 = 指定なし＝従来どおり）
///
/// # 戻り値
/// 入れた指定（2 回目以降の呼び出しでは、最初に入った指定）。
pub fn install(spec: Option<&str>) -> &'static UiSpikeConfig {
    let (config, warnings) = UiSpikeConfig::parse_lenient(spec.unwrap_or_default());
    for warning in &warnings {
        eprintln!("{LOG_PREFIX}[WARN] 指定を読めませんでした（その項目は無視します）: {warning}");
    }
    if config.any_enabled() {
        eprintln!("{LOG_PREFIX} W2-0 の試作を有効にします: {config:?}");
    }
    if CONFIG.set(config).is_err() {
        eprintln!("{LOG_PREFIX}[WARN] 指定は既に入っています（2 回目の install は無視します）");
    }
    self::config()
}

/// 起動時に決めた指定（install 前に呼ばれたら既定＝すべて無効）。
pub fn config() -> &'static UiSpikeConfig {
    CONFIG.get_or_init(UiSpikeConfig::default)
}

/// PC の起動引数と環境変数から指定の文字列を選ぶ【純関数】（起動引数が優先）。
///
/// # 引数
/// * `args`    - 起動引数の並び
/// * `env_val` - 環境変数 `SEED_UI_SPIKE` の値（無ければ None）
pub fn spec_from_desktop(args: &[String], env_val: Option<String>) -> Option<String> {
    args.iter()
        .find_map(|arg| arg.strip_prefix(CLI_ARG_PREFIX).map(str::to_string))
        .or(env_val)
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 起動引数が環境変数より優先し、どちらも無ければ None。
    #[test]
    fn desktop_spec_prefers_cli_arg() {
        let args = vec!["SEED.exe".to_string(), "--ui-spike=idle=5".to_string()];
        assert_eq!(spec_from_desktop(&args, Some("clip=A".into())).as_deref(), Some("idle=5"));
        let args = vec!["SEED.exe".to_string()];
        assert_eq!(spec_from_desktop(&args, Some("clip=A".into())).as_deref(), Some("clip=A"));
        assert_eq!(spec_from_desktop(&args, None), None);
    }
}
