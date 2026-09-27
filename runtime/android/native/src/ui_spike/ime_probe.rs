// ============================================================
//  ui_spike/ime_probe.rs — 文字入力（GameActivity の GameTextInput）の試作（W2-0。既定で無効）
//
//  【何を確かめるか】（結果は docs/app_platform_roadmap.md §3.8）
//  winit が AndroidApp を EventLoop に握らせたまま、複製した AndroidApp から android-activity 0.6.1 の API
//  （set_ime_editor_info → show_soft_input / hide_soft_input、text_input_state、set_text_input_state）で
//  ソフトキーボードを出し、入力の状態を読み書きできるか。
//
//  【命令】アプリ専用の内部フォルダの files/ui_spike/ime_cmd に 1 行 1 命令で書く（試作のスレッドが周期的に読んで消す）:
//    adb shell "run-as <アプリ ID> sh -c 'mkdir -p files/ui_spike && echo show_text > files/ui_spike/ime_cmd'"
//      show_text        … 1 行の文字・完了ボタンで出す（TYPE_CLASS_TEXT・IME_ACTION_DONE・全画面の入力欄を出さない）
//      show_number      … 数字だけ・完了ボタンで出す（TYPE_CLASS_NUMBER）
//      show_multiline   … 複数行（改行キー）で出す（TYPE_TEXT_FLAG_MULTI_LINE・アクションなし）
//      hide             … 隠す（hide_soft_input）
//      set <文字列>     … 入力の状態を差し替える（set_text_input_state。選択＝カーソルは末尾）
//      clear            … 入力の状態を空にする
//      dump             … 今の状態を 1 度ログへ出す
//  【観察】入力の状態（text・selection・compose_region）は Java の UI スレッドで text_input_state() を読む
//  （AndroidApp::run_on_java_main_thread）。GameTextInput は状態の文字列を UI スレッドで固定長のバッファへ上書きし、
//  getState はロックの外でその文字列を読むので、別のスレッドで読むと途中の文字列を読みうる（gametextinput.cpp）。
//  UI スレッドで読めば書き手と同じスレッドなので取り合わない。周期ごとに読み、前と違えば logcat へ出す。
//
//  【注意】選択・変換中の区間の添字は Java の String の添字（UTF-16 の単位）のまま届く（GameTextInput は変換しない。
//  android-activity の TextSpan の説明は「バイト」ではないので、日本語では添字の単位の取り違えに注意する。§3.8）。
// ============================================================

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use winit::platform::android::activity::AndroidApp;
use winit::platform::android::activity::input::{
    ImeOptions, InputType, TextInputAction, TextInputState, TextSpan,
};

use crate::logcat;

/// 試作のスレッドの名前（logcat の tid 表示・デバッガでの識別用）。
const THREAD_NAME: &str = "seed-ime-spike";

/// 命令のファイルを置くフォルダ（アプリ専用の内部フォルダ files/ からの相対）。
const COMMAND_DIR: &str = "ui_spike";

/// 命令のファイルの名前。
const COMMAND_FILE: &str = "ime_cmd";

/// 命令のファイルと入力の状態を見る周期。
const POLL_INTERVAL: Duration = Duration::from_millis(100);

/// ログの印（Java 側は SEEDImeSpike のタグ）。
const LOG_PREFIX: &str = "[SEED IME SPIKE]";

/// 命令と引数の区切り。
const ARG_SEPARATOR: char = ' ';

/// ソフトキーボードの出し方（命令ごとの EditorInfo の組）。
struct KeyboardKind {
    /// ログに出す名前。
    label: &'static str,
    /// 入力の種類（EditorInfo.inputType）。
    input_type: InputType,
    /// 完了などのアクション（EditorInfo.actionId と imeOptions のアクション）。
    action: TextInputAction,
}

/// 試作のスレッドを始める（プロセスの終わりまで常駐する）。
///
/// # 引数
/// * `app` - 複製した AndroidApp（winit が握っているものと同じ Activity を指す）
pub fn spawn(app: AndroidApp) {
    let spawned = std::thread::Builder::new()
        .name(THREAD_NAME.to_string())
        .spawn(move || run(app));
    if let Err(err) = spawned {
        logcat::warn(&format!("{LOG_PREFIX} 試作のスレッドを起動できませんでした: {err}"));
    }
}

/// 試作のスレッドの本体（命令を読んで実行し、入力の状態の変化をログへ出す）。
fn run(app: AndroidApp) {
    let Some(command_path) = command_path(&app) else {
        logcat::warn(&format!("{LOG_PREFIX} アプリ専用の内部フォルダが取れないので命令を受けられません"));
        return;
    };
    logcat::info(&format!("{LOG_PREFIX} 命令のファイル: {}", command_path.display()));
    // 前回ログへ出した状態（同じ状態を繰り返し出さない）。UI スレッドの読み取りと共有する
    let last_logged: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));
    loop {
        std::thread::sleep(POLL_INTERVAL);
        for command in take_commands(&command_path) {
            execute(&app, &command, &last_logged);
        }
        request_state_log(&app, &last_logged, false);
    }
}

/// 命令のファイルのパス（フォルダが無ければ作る）。
fn command_path(app: &AndroidApp) -> Option<PathBuf> {
    let dir = app.internal_data_path()?.join(COMMAND_DIR);
    if let Err(err) = std::fs::create_dir_all(&dir) {
        logcat::warn(&format!("{LOG_PREFIX} 命令のフォルダを作れませんでした: {err}"));
    }
    Some(dir.join(COMMAND_FILE))
}

/// 命令のファイルがあれば読んで消し、空でない行を返す。
fn take_commands(path: &Path) -> Vec<String> {
    let Ok(text) = std::fs::read_to_string(path) else { return Vec::new() };
    if let Err(err) = std::fs::remove_file(path) {
        logcat::warn(&format!("{LOG_PREFIX} 命令のファイルを消せませんでした（同じ命令を繰り返すおそれ）: {err}"));
    }
    text.lines().map(str::trim).filter(|line| !line.is_empty()).map(str::to_string).collect()
}

/// 1 つの命令を実行する。
fn execute(app: &AndroidApp, command: &str, last_logged: &Arc<Mutex<Option<String>>>) {
    let (name, arg) = command.split_once(ARG_SEPARATOR).unwrap_or((command, ""));
    logcat::info(&format!("{LOG_PREFIX} 命令: {name}"));
    match name {
        "show_text" => show(app, &KeyboardKind {
            label: "1 行の文字（完了）",
            input_type: InputType::TYPE_CLASS_TEXT | InputType::TYPE_TEXT_VARIATION_NORMAL,
            action: TextInputAction::Done,
        }),
        "show_number" => show(app, &KeyboardKind {
            label: "数字だけ（完了）",
            input_type: InputType::TYPE_CLASS_NUMBER | InputType::TYPE_NUMBER_VARIATION_NORMAL,
            action: TextInputAction::Done,
        }),
        "show_multiline" => show(app, &KeyboardKind {
            label: "複数行（改行）",
            input_type: InputType::TYPE_CLASS_TEXT | InputType::TYPE_TEXT_FLAG_MULTI_LINE,
            action: TextInputAction::None,
        }),
        "hide" => {
            // 明示の操作で隠す（HIDE_IMPLICIT_ONLY を付けない）
            app.hide_soft_input(false);
        }
        "set" => set_state(app, arg),
        "clear" => set_state(app, ""),
        "dump" => request_state_log(app, last_logged, true),
        other => logcat::warn(&format!("{LOG_PREFIX} 知らない命令です: {other:?}")),
    }
}

/// EditorInfo を決めてからソフトキーボードを出す。
///
/// どちらも GameActivity の UI スレッドの作業の列（work pipe）に順に積まれるので、EditorInfo の差し替え
/// （setImeEditorInfoFields）→ 表示（GameTextInput_showIme → setSoftKeyboardActive(true) → restartInput）の順に効き、
/// 表示のときの restartInput で IME が新しい EditorInfo を読み直す（GameActivity 4.4.0 の javap で確かめた流れ）。
fn show(app: &AndroidApp, kind: &KeyboardKind) {
    // 全画面の入力欄（横画面で出る抽出 UI）を出さない。アクションは imeOptions の下位ビットにも入れる
    // （IME が表示するボタンは imeOptions のアクション、ハードウェアの Enter は actionId を使う）
    let mut options = ImeOptions::IME_FLAG_NO_FULLSCREEN | ImeOptions::IMG_FLAG_NO_EXTRACT_UI;
    options.set_action(kind.action);
    app.set_ime_editor_info(kind.input_type, kind.action, options);
    // 明示の操作として出す（SHOW_IMPLICIT を付けない）
    app.show_soft_input(false);
    logcat::info(&format!(
        "{LOG_PREFIX} ソフトキーボードを出します: {}（inputType=0x{:x}・action={:?}・imeOptions=0x{:x}）",
        kind.label,
        kind.input_type.bits(),
        kind.action,
        options.bits()
    ));
}

/// 入力の状態を差し替える（選択＝カーソルは末尾）。
///
/// 添字は Java の String の添字（UTF-16 の単位）として IME へ渡るので、UTF-16 の長さで数える。
fn set_state(app: &AndroidApp, text: &str) {
    let end = text.encode_utf16().count();
    app.set_text_input_state(TextInputState {
        text: text.to_string(),
        selection: TextSpan { start: end, end },
        compose_region: None,
    });
    logcat::info(&format!("{LOG_PREFIX} 入力の状態を差し替えました: {text:?}（カーソル {end}。UTF-16 の単位）"));
}

/// UI スレッドで入力の状態を読み、前と違えば（`force` なら必ず）ログへ出す。
fn request_state_log(app: &AndroidApp, last_logged: &Arc<Mutex<Option<String>>>, force: bool) {
    let reader = app.clone();
    let last_logged = Arc::clone(last_logged);
    app.run_on_java_main_thread(Box::new(move || {
        let description = describe(&reader.text_input_state());
        let Ok(mut last) = last_logged.lock() else { return };
        if force || last.as_deref() != Some(description.as_str()) {
            logcat::info(&format!("{LOG_PREFIX} native state: {description}"));
            *last = Some(description);
        }
    }));
}

/// 入力の状態をログ用の 1 行にする（添字の単位を確かめられるよう、UTF-8 のバイト数・UTF-16 の長さ・文字数も添える）。
fn describe(state: &TextInputState) -> String {
    format!(
        "text={:?}（UTF-8 {} バイト・UTF-16 {}・文字 {}） selection={}..{} compose={:?}",
        state.text,
        state.text.len(),
        state.text.encode_utf16().count(),
        state.text.chars().count(),
        state.selection.start,
        state.selection.end,
        state.compose_region.map(|span| (span.start, span.end)),
    )
}
