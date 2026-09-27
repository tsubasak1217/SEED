// ============================================================
//  KeepScreenOnCommand.java — window.set_keep_screen_on（画面を点けたままにする の切り替え。W1-6）
//
//  引数 { on: 真偽 }。MainActivity の窓の WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON（API 1）を UI スレッドで出し入れする
//  （返答 { on }。引数と Activity の扱いは WindowToggleCommand）。窓が見えている間だけ効き（背面へ回れば効かない）、
//  権限は要らない（WAKE_LOCK と違い、窓の属性）。利用者の電源ボタンでの消灯は止めない。
//  MainActivity は作り直されない（構成変更で作り直さず、破棄ではプロセスごと終わる）ので、切り替えはプロセスの間ずっと残る。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;
import android.util.Log;
import android.view.WindowManager;

import com.seedengine.runtime.platform.PlatformContract;

/**
 * window.set_keep_screen_on。
 */
final class KeepScreenOnCommand extends WindowToggleCommand {

    @Override
    void apply(Activity activity, boolean on) {
        if (on) {
            activity.getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        } else {
            activity.getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        }
        Log.i(PlatformContract.LOG_TAG, "画面を点けたままにする: " + on);
    }
}
