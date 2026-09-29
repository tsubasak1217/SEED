// ============================================================
//  text_input/jni_receivers.rs — Java（input/TextInputBridge）から文字入力の知らせを受ける JNI（W2-6a）
//
//  MainActivity の上書き（GameActivity の受け口。UI スレッド）→ TextInputBridge → ここ → エンジンの箱
//  （engine::core::text_input::inbox。積むとイベントループを起こす〈render_policy の on_demand で止めていても遅れない〉）。
//    nativeOnTextState(byte[] textUtf8, int selStart, int selEnd, int compStart, int compEnd)
//        … stateChanged の状態の丸ごとの写し。本文は UTF-8（絵文字も落ちない。§1.2 の流儀）、添字は Java の String の添字
//          ＝UTF-16 の単位のまま（I-6。エンジンが UTF-8 の境界へ直す）。変換中でなければ -1,-1
//    nativeOnEditorAction(int action)          … onEditorAction（EditorInfo.IME_ACTION_*。6 = 完了）
//    nativeOnKeyboardVisibility(boolean)       … onSoftwareKeyboardVisibilityChanged
//    nativeOnImeHeight(int bottomPx)           … onApplyWindowInsets の IME の範囲の下端からの高さ（画素。0 = 無い）
//  jni クレート 0.22 の型で受け、with_env で中の panic を受け止める（JNI の境界を panic で越えない。touch_timeline.rs と同じ流儀）。
//  入力された本文はログへ出さない（私物の端末の打鍵を logcat に残さない）。確かめ用に、知らせの種類と長さ・添字だけを
//  logcat（タグ SEED の [SEED TEXT INPUT]）へ出す（人の打鍵の速さなので量は少ない）。
// ============================================================

use std::sync::atomic::{AtomicBool, Ordering};

use jni::objects::{JByteArray, JClass};
use jni::sys::{jboolean, jint};
use jni::{EnvUnowned, Outcome};

use seed_engine::engine::core::text_input::{inbox, PlatformTextMessage, Utf16State, LOG_PREFIX};

use crate::logcat;

/// 受け取りの失敗を一度ログに出したか（打鍵のたびに出さない）。
static FAILURE_LOGGED: AtomicBool = AtomicBool::new(false);

/// Java の「無い」の添字（変換中の区間が無い）。
const JAVA_NO_INDEX: jint = -1;

/// `TextInputBridge.nativeOnTextState(byte[], int, int, int, int)`（Java の `private static native void`）の実体。
///
/// # 引数
/// * `unowned_env`       - JNIEnv（jni クレートの FFI 安全な包み）
/// * `_class`            - TextInputBridge のクラス（使わない）
/// * `text_utf8`         - 本文（UTF-8 の byte[]）
/// * `selection_start`   - 選択の起点（UTF-16）
/// * `selection_end`     - 選択の動く端（UTF-16）
/// * `composition_start` - 変換中の区間の始め（UTF-16。無ければ -1）
/// * `composition_end`   - 変換中の区間の終わり（UTF-16。無ければ -1）
#[unsafe(no_mangle)]
pub extern "system" fn Java_com_seedengine_runtime_input_TextInputBridge_nativeOnTextState<'caller>(
    mut unowned_env: EnvUnowned<'caller>,
    _class: JClass<'caller>,
    text_utf8: JByteArray<'caller>,
    selection_start: jint,
    selection_end: jint,
    composition_start: jint,
    composition_end: jint,
) {
    let outcome = unowned_env.with_env(|env| -> Result<Vec<u8>, jni::errors::Error> { env.convert_byte_array(&text_utf8) });
    let bytes = match outcome.into_outcome() {
        Outcome::Ok(bytes) => bytes,
        Outcome::Err(err) => return log_failure_once(&format!("本文の byte[] を読めませんでした（捨てます）: {err}")),
        Outcome::Panic(_) => return log_failure_once("本文の受け取りの途中で panic しました（捨てます）"),
    };
    let text = String::from_utf8_lossy(&bytes).into_owned();
    let composition = (composition_start != JAVA_NO_INDEX && composition_end != JAVA_NO_INDEX)
        .then(|| (non_negative(composition_start), non_negative(composition_end)));
    let state = Utf16State {
        text,
        selection_start: non_negative(selection_start),
        selection_end: non_negative(selection_end),
        composition,
    };
    // 確かめ用のログ（本文は出さず、長さと添字だけ。私物の端末の打鍵を残さない）
    logcat::info(&format!(
        "{LOG_PREFIX} IME の状態: 長さ {}（UTF-16）・選択 {}..{}・変換 {:?}",
        state.text.encode_utf16().count(),
        state.selection_start,
        state.selection_end,
        state.composition
    ));
    push(PlatformTextMessage::State(state));
}

/// `TextInputBridge.nativeOnEditorAction(int)` の実体。
#[unsafe(no_mangle)]
pub extern "system" fn Java_com_seedengine_runtime_input_TextInputBridge_nativeOnEditorAction<'caller>(
    _env: EnvUnowned<'caller>,
    _class: JClass<'caller>,
    action: jint,
) {
    logcat::info(&format!("{LOG_PREFIX} IME のアクション: {action}"));
    push(PlatformTextMessage::Action(action));
}

/// `TextInputBridge.nativeOnKeyboardVisibility(boolean)` の実体（jni-sys 0.4 の jboolean は bool）。
#[unsafe(no_mangle)]
pub extern "system" fn Java_com_seedengine_runtime_input_TextInputBridge_nativeOnKeyboardVisibility<'caller>(
    _env: EnvUnowned<'caller>,
    _class: JClass<'caller>,
    visible: jboolean,
) {
    logcat::info(&format!("{LOG_PREFIX} キーボードの表示: {visible}"));
    push(PlatformTextMessage::KeyboardVisible(visible));
}

/// `TextInputBridge.nativeOnImeHeight(int)` の実体。
#[unsafe(no_mangle)]
pub extern "system" fn Java_com_seedengine_runtime_input_TextInputBridge_nativeOnImeHeight<'caller>(
    _env: EnvUnowned<'caller>,
    _class: JClass<'caller>,
    bottom_px: jint,
) {
    logcat::info(&format!("{LOG_PREFIX} IME の高さ: {bottom_px} px"));
    push(PlatformTextMessage::KeyboardHeight(bottom_px.max(0)));
}

/// 箱へ積む（Mutex の出し入れと起こすだけ。念のため panic をここで止める）。
fn push(message: PlatformTextMessage) {
    if std::panic::catch_unwind(move || inbox::push(message)).is_err() {
        log_failure_once("知らせを積む途中で panic しました（捨てます）");
    }
}

/// 負の添字を 0 にする（IME の食い違い。エンジンがさらに本文の長さへ丸める）。
fn non_negative(value: jint) -> usize {
    usize::try_from(value).unwrap_or(0)
}

/// 受け取りの失敗を最初の 1 回だけログへ出す。
fn log_failure_once(message: &str) {
    if !FAILURE_LOGGED.swap(true, Ordering::Relaxed) {
        logcat::warn(&format!("{LOG_PREFIX} {message}（以後の同じ失敗は出しません）"));
    }
}
