// ============================================================
//  BackSystemCallbacksApi36.java — Android 16（API 36）のシステムの戻るのコールバック（W2 の手直し P1-3）
//
//  android.window.SystemOnBackInvokedCallbacks（API 36。ローカルの SDK の api-versions.xml で確かめた）の
//  moveTaskToBackCallback(Activity) を返すだけ。受ける層が無い（根）ときに、自分のコールバックの代わりに PRIORITY_DEFAULT で登録する
//  （BackDispatcherApi33）。戻るで Activity を finish せずタスクを背面へ回し（MainActivity の破棄はプロセスの終了なので避けたい）、
//  システムの「ホームへ戻る」の予測アニメーションが出る見込み（記憶による。登録の仕方〈registerOnBackInvokedCallback に
//  PRIORITY_DEFAULT で渡す〉も記憶による。実機で確かめる）。ランチャー以外（PlatformEntry の別名・adb の am start -n）から
//  起動した根でも背面へ回る。
//  このクラスは API 36 の型を使うので、API 36 以上の端末でだけ呼ぶ（BackDispatcherApi33 が SDK_INT で分ける）。
// ============================================================

package com.seedengine.runtime.back;

import android.app.Activity;
import android.os.Build;
import android.window.OnBackInvokedCallback;
import android.window.SystemOnBackInvokedCallbacks;

import androidx.annotation.RequiresApi;

/**
 * API 36 のシステムの戻るのコールバック（static のみ）。
 */
@RequiresApi(Build.VERSION_CODES.BAKLAVA)
final class BackSystemCallbacksApi36 {

    private BackSystemCallbacksApi36() {
    }

    /**
     * 戻るでタスクを背面へ回すシステムのコールバック（API 36 以上の端末でだけ呼ぶこと）。
     *
     * @param activity 背面へ回す Activity（MainActivity）
     * @return コールバック（登録・外しは呼び手が行う）
     */
    static OnBackInvokedCallback moveTaskToBack(Activity activity) {
        return SystemOnBackInvokedCallbacks.moveTaskToBackCallback(activity);
    }
}
