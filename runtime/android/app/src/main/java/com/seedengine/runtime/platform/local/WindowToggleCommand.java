// ============================================================
//  WindowToggleCommand.java — 画面の切り替えの命令の共通の形（引数 { on }。メインプロセス。W1-6 で共通化）
//
//  window.set_show_when_locked（W1-4a）・window.set_keep_screen_on・window.set_system_bars_visible（W1-6）は、どれも
//    1. 引数 { on: 真偽 } を読む（真偽でなければ invalid_argument）
//    2. 操作する Activity（HostActivity に登録された MainActivity）を取る（無ければ no_activity）
//    3. 窓の操作は UI スレッドだけなので Activity.runOnUiThread へ投げてすぐ返る（呼び出し元はエンジンのスレッド）
//    4. 返答 { on }
//  の同じ形なので、ここにまとめ、命令ごとのクラスは 3 の中身（apply）だけを書く。切り替えが実際に効くのは UI スレッドで少し後。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

import org.json.JSONObject;

/**
 * 画面の切り替えの命令（{ on } を受けて UI スレッドで切り替える）。
 */
abstract class WindowToggleCommand implements MainProcessCommand {

    @Override
    public final byte[] handle(JSONObject arguments) {
        Object value = arguments.opt(PlatformContract.KEY_WINDOW_ON);
        if (!(value instanceof Boolean)) {
            return PlatformJson.errorReply(PlatformContract.ERROR_INVALID_ARGUMENT,
                    PlatformContract.KEY_WINDOW_ON + " は真偽にしてください");
        }
        boolean on = (Boolean) value;
        Activity activity = HostActivity.get();
        if (activity == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NO_ACTIVITY);
        }
        // Activity の窓の操作は UI スレッドだけ（呼び出し元はエンジンのスレッド）
        activity.runOnUiThread(() -> apply(activity, on));
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_WINDOW_ON, on);
        return PlatformJson.okReply(fields);
    }

    /**
     * 切り替える（UI スレッドで呼ばれる）。
     *
     * @param activity 操作する Activity（MainActivity）
     * @param on       入れるなら true
     */
    abstract void apply(Activity activity, boolean on);
}
