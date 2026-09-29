// ============================================================
//  BackKeyInjector.java — 戻るの確定を、従来の戻るキーと同じ入口（GameActivity の onKeyDown / onKeyUp）へ渡す（W2 の手直し P1-3）
//
//  【なぜ要るか】
//  予測型の戻るを有効にした APK（マニフェストの enableOnBackInvokedCallback="true"）では、Android 13 以降のシステムが
//  KEYCODE_BACK を窓の OnBackInvokedDispatcher のコールバックへ回し、アプリの onKeyDown / onKeyUp へは届けない。
//  スクリプトは戻るを従来どおり Escape（Input.GetKeyDown(KeyCode.Escape)・SEED.UI の BackDispatcher）で受けるので、
//  戻るが確定したら（onBackInvoked）KEYCODE_BACK の down と up を合成して GameActivity へ渡す。
//
//  【道】GameActivity.onKeyDown(int, KeyEvent)（games-activity 4.4.0 を javap で確かめた: isNativeDestroyed なら false、
//  onKeyDownNative が true なら true、そうでなければ AppCompatActivity.onKeyDown）→ onKeyDownNative → glue の onKey
//  （android-activity 0.6.1 の同梱の android_native_app_glue.c。default_key_filter は音量・カメラ・ズームのキーだけを捨て、
//  KEYCODE_BACK は通す）→ winit 0.30.13 の platform_impl/android/mod.rs の handle_input_event → KeyboardInput
//  （physical_key は keycodes.rs の to_physical_key で Unidentified(NativeKeyCode::Android(4))。key_code だけで決まり、
//  deviceId・source・flags は見ない）→ エンジンの input/key_remap.rs で Escape（docs/android.md §14.5）。
//
//  【合成する値】adb の `input keyevent KEYCODE_BACK` と同じにする（§14.7 の実機・エミュレータでこの道が Escape になると確かめ済み）:
//    downTime・eventTime = SystemClock.uptimeMillis()（KeyEvent の時計）、repeat 0（winit は repeat_count > 0 を繰り返しとみなす）、
//    metaState 0、deviceId = KeyCharacterMap.VIRTUAL_KEYBOARD（-1。winit は論理キーを引くために InputDevice.getDevice(deviceId) の
//    KeyCharacterMap を読む。0 だと引けずに警告が出る〈android-activity の input/sdk.rs〉。-1 は端末の仮想キーボード〈記憶による〉）、
//    scanCode 0、flags 0、source = InputDevice.SOURCE_KEYBOARD。up は down の action だけを変えたもの（KeyEvent.changeAction）。
//  直接 onKeyUp を呼ぶので、up に FLAG_TRACKING は付かず、万一ネイティブが受け取らなかった（破棄の後）ときも Activity.onKeyUp が
//  onBackPressed（＝finish → プロセスの終了）を呼ぶことは無い。
// ============================================================

package com.seedengine.runtime.back;

import android.app.Activity;
import android.os.SystemClock;
import android.util.Log;
import android.view.InputDevice;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;

/**
 * 合成の KEYCODE_BACK を GameActivity へ渡す係（UI スレッド専用。BackGestureReporter が確定のときに呼ぶ）。
 */
final class BackKeyInjector {

    /** 繰り返しの回数（押した最初の 1 回。adb の input keyevent と同じ 0）。 */
    private static final int REPEAT_NONE = 0;

    /** 修飾キーの状態（なし。adb の input keyevent と同じ 0）。 */
    private static final int META_NONE = 0;

    /** 走査コード（なし。adb の input keyevent と同じ 0）。 */
    private static final int SCAN_CODE_NONE = 0;

    /** KeyEvent の印（なし。adb の input keyevent と同じ 0）。 */
    private static final int FLAGS_NONE = 0;

    /** キーを渡す Activity（MainActivity。GameActivity の onKeyDown / onKeyUp が受ける）。 */
    private final Activity activity;

    /**
     * 作る。
     *
     * @param activity キーを渡す Activity（MainActivity）
     */
    BackKeyInjector(Activity activity) {
        this.activity = activity;
    }

    /**
     * KEYCODE_BACK の down と up を続けて渡す（エンジンは同じフレームに押して離したと見て、Input.GetKeyDown(Escape) が true になる）。
     *
     * @return down と up の両方をネイティブが受け取ったら true（受け取らなければログに残す）
     */
    boolean injectBack() {
        long now = SystemClock.uptimeMillis();
        KeyEvent down = new KeyEvent(now, now, KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_BACK, REPEAT_NONE, META_NONE,
                KeyCharacterMap.VIRTUAL_KEYBOARD, SCAN_CODE_NONE, FLAGS_NONE, InputDevice.SOURCE_KEYBOARD);
        KeyEvent up = KeyEvent.changeAction(down, KeyEvent.ACTION_UP);
        boolean downTaken = activity.onKeyDown(KeyEvent.KEYCODE_BACK, down);
        boolean upTaken = activity.onKeyUp(KeyEvent.KEYCODE_BACK, up);
        if (!downTaken || !upTaken) {
            // ネイティブが破棄された後（GameActivity の isNativeDestroyed）など。Escape は届かない
            Log.w(BackCallbackController.LOG_TAG, BackCallbackController.LOG_PREFIX
                    + "合成の KEYCODE_BACK をネイティブが受け取りませんでした（down=" + downTaken + " up=" + upTaken + "）");
        }
        return downTaken && upTaken;
    }
}
