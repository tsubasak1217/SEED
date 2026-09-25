// ============================================================
//  jni_exports.rs — Java（MainActivity・ScreenReporter・AudioFocusController）から呼ばれるネイティブ関数（JNI）
//                   （セーブの書き出し・安全領域と回転・音声フォーカス・起動オプション）
//
//  【仕組み】
//  MainActivity は static 初期化子で System.loadLibrary("SEED") している。同じパッケージの Java クラスの
//  `native` メソッドは、JNI の命名規則 `Java_<パッケージ>_<クラス>_<メソッド>` の名前で libSEED.so から
//  書き出された C ABI の関数へ自動で結び付く（RegisterNatives も jni クレートも要らない）。
//  引数の JNIEnv* / jclass は使わないので生ポインタのまま受け取る（依存クレートを増やさない）。
//  jint は 32 ビット符号付き整数（i32）。
//
//  【nativeFlushSaveData（MainActivity）】
//  MainActivity.onDestroy がプロセスを終える（Process.killProcess）直前に UI スレッドから呼ぶ。
//  セーブの未書き出し分を同期でディスクへ書く保険。通常は背面へ回った時点（suspended。
//  エンジンの app/background_lifecycle.rs）で書き出し済みで、ここでは「変更なし」になる。
//  セーブのストアは Mutex で守られているので、UI スレッドから呼んでも android_main のスレッドと競合しない。
//  ログは標準エラーの転送スレッドを経由せず liblog へ直接書く（直後にプロセスが終わっても消えないように）。
//
//  【nativeOnScreenChanged（ScreenReporter）】
//  安全領域（描画面の各辺からの距離・物理ピクセル）・表示の回転・表示の自然な向きの大きさ・そのときの描画面の
//  大きさを、値が変わったときに UI スレッドから呼ぶ。エンジン側の報告の置き場（platform::screen::report。Mutex）へ
//  入れるだけで、スクリプトが読む値（SEED.Screen）へは次のフレームの公開で反映される（app/screen_publish.rs）。
//
//  【nativeOnAudioFocusChanged（AudioFocusController）】
//  音声フォーカスの要求・放棄の結果と、OS からの変化の通知（着信・他のアプリの再生・通知音）を、状態の番号で
//  UI スレッドから呼ぶ。エンジン側の置き場（platform::audio_focus。原子変数）へ入れるだけで、出力の一時停止・
//  再開・全体音量にはエンジンのイベントループの次の周回で反映される（app/audio_output_sync.rs）。
//  番号の対応は AudioFocus::from_code。
//
//  【nativeSetLaunchOptions（MainActivity。段階C-3）】
//  起動の Intent の「seed.」で始まる文字列の extra を JSON 1 つ（UTF-8 の byte[]）にして、onCreate の最初
//  （super.onCreate より前。android_main のスレッドが立つ前）に UI スレッドから呼ぶ。launch_options.rs に預け、
//  android_main（launch.rs）が起動するシーン等に使う。byte[] の中身は JNIEnv の関数で読む（jni_env.rs）。
// ============================================================

use std::ffi::c_void;

use seed_engine::engine::core::save;
use seed_engine::engine::platform::audio_focus::{self, AudioFocus};
use seed_engine::engine::platform::screen::report::{self, EdgeInsets, ScreenReport};

use crate::{jni_env, launch_options, logcat};

/// `MainActivity.nativeFlushSaveData()`（Java の `private static native void`）の実体。
///
/// # 引数
/// * `_env`   - JNIEnv*（使わない）
/// * `_class` - MainActivity の jclass（static メソッドなのでインスタンスではない。使わない）
#[unsafe(no_mangle)]
pub extern "system" fn Java_com_seedengine_runtime_MainActivity_nativeFlushSaveData(
    _env: *mut c_void,
    _class: *mut c_void,
) {
    // JNI の境界を panic で越えると Java 側ごと未定義の壊れ方をする（Rust では abort）ため、
    // ここで受け止めてログに残し、Java 側はそのままプロセスを終える。
    match std::panic::catch_unwind(save::flush_if_dirty) {
        Ok(outcome) => logcat::info(&format!("[SEED SAVE] onDestroy（プロセス終了前）: {}", outcome.describe())),
        Err(_) => logcat::error("[SEED SAVE] onDestroy（プロセス終了前）: セーブの書き出し中に panic しました"),
    }
}

/// `ScreenReporter.nativeOnScreenChanged(int ×9)`（Java の `private static native void`）の実体。
///
/// # 引数
/// * `_env` / `_class` - JNIEnv* と ScreenReporter の jclass（使わない）
/// * `frame_width` / `frame_height` - 報告したときの描画面（SurfaceView）の大きさ（物理ピクセル）
/// * `inset_left` / `inset_top` / `inset_right` / `inset_bottom` - 安全領域の、描画面の各辺からの距離（物理ピクセル）
/// * `rotation` - Display.getRotation（自然な向きからの 90 度単位の回転。0..3）
/// * `natural_width` / `natural_height` - 表示の自然な向き（回転 0）の大きさ（Display.Mode の physicalWidth / Height）
///
/// 負の値（OS の不具合でしか起きない）は 0 として扱う。
#[unsafe(no_mangle)]
#[allow(clippy::too_many_arguments)] // Java 側の引数の並びそのまま（JNI の関数形は変えられない）
pub extern "system" fn Java_com_seedengine_runtime_ScreenReporter_nativeOnScreenChanged(
    _env: *mut c_void,
    _class: *mut c_void,
    frame_width: i32,
    frame_height: i32,
    inset_left: i32,
    inset_top: i32,
    inset_right: i32,
    inset_bottom: i32,
    rotation: i32,
    natural_width: i32,
    natural_height: i32,
) {
    let screen_report = ScreenReport {
        frame_width: non_negative(frame_width),
        frame_height: non_negative(frame_height),
        insets: EdgeInsets {
            left: non_negative(inset_left),
            top: non_negative(inset_top),
            right: non_negative(inset_right),
            bottom: non_negative(inset_bottom),
        },
        rotation_quarter_turns: non_negative(rotation),
        natural_width: non_negative(natural_width),
        natural_height: non_negative(natural_height),
    };
    // Mutex への書き込みだけだが、JNI の境界を panic で越えないよう念のため受け止める。
    match std::panic::catch_unwind(|| report::submit(screen_report)) {
        Ok(true) => logcat::info(&format!(
            "[SEED SCREEN] 報告を受け取りました: frame={}x{} insets=({},{},{},{}) rotation={} natural={}x{}",
            screen_report.frame_width,
            screen_report.frame_height,
            screen_report.insets.left,
            screen_report.insets.top,
            screen_report.insets.right,
            screen_report.insets.bottom,
            screen_report.rotation_quarter_turns,
            screen_report.natural_width,
            screen_report.natural_height,
        )),
        // 同じ内容の報告の繰り返し（何も変わらない）。
        Ok(false) => {}
        Err(_) => logcat::error("[SEED SCREEN] 画面の報告の受け取り中に panic しました"),
    }
}

/// `AudioFocusController.nativeOnAudioFocusChanged(int)`（Java の `private static native void`）の実体。
///
/// # 引数
/// * `_env` / `_class` - JNIEnv* と AudioFocusController の jclass（使わない）
/// * `state` - 音声フォーカスの状態の番号（AudioFocusController.STATE_*。AudioFocus::from_code と同じ対応）
#[unsafe(no_mangle)]
pub extern "system" fn Java_com_seedengine_runtime_AudioFocusController_nativeOnAudioFocusChanged(
    _env: *mut c_void,
    _class: *mut c_void,
    state: i32,
) {
    let Some(focus) = AudioFocus::from_code(state) else {
        // Java とネイティブの版の食い違い。知らない状態で音を止めたり鳴らしたりせず、今の状態のまま続ける。
        logcat::warn(&format!("[SEED AUDIO] 知らない音声フォーカスの番号を受け取りました（無視します）: {state}"));
        return;
    };
    // 原子変数への書き込みだけだが、JNI の境界を panic で越えないよう念のため受け止める。
    match std::panic::catch_unwind(|| audio_focus::report(focus)) {
        Ok(true) => logcat::info(&format!(
            "[SEED AUDIO] 音声フォーカスの報告を受け取りました: {}（エンジンが次の周回で出力へ反映）",
            focus.describe()
        )),
        // 同じ状態の繰り返し（何も変わらない）。
        Ok(false) => {}
        Err(_) => logcat::error("[SEED AUDIO] 音声フォーカスの報告の受け取り中に panic しました"),
    }
}

/// `MainActivity.nativeSetLaunchOptions(byte[])`（Java の `private static native void`）の実体。
///
/// # 引数
/// * `env`          - JNIEnv*（byte[] の中身を読むのに使う）
/// * `_class`       - MainActivity の jclass（使わない）
/// * `options_utf8` - 起動オプションの JSON（UTF-8 の byte[]。null 可）
#[unsafe(no_mangle)]
pub extern "system" fn Java_com_seedengine_runtime_MainActivity_nativeSetLaunchOptions(
    env: *mut c_void,
    _class: *mut c_void,
    options_utf8: *mut c_void,
) {
    // SAFETY: env は JNI がこの呼び出しのスレッドへ渡した JNIEnv*、options_utf8 は同じ呼び出しの引数の byte[]（null 可）。
    // 読むのはこの呼び出しの間だけ（参照を持ち越さない）。
    let bytes = unsafe { jni_env::read_byte_array(env, options_utf8) };
    // JNI の境界を panic で越えないよう受け止める（ログだけ残し、起動は既定のまま続ける）。
    if std::panic::catch_unwind(move || launch_options::receive(bytes)).is_err() {
        logcat::error("[SEED LAUNCH] 起動オプションの受け取り中に panic しました（既定のまま起動します）");
    }
}

/// JNI の jint（符号付き）を 0 以上の値にする（負は 0）。
fn non_negative(value: i32) -> u32 {
    u32::try_from(value).unwrap_or(0)
}
