// ============================================================
//  MoveTaskToBackCommand.java — app.move_task_to_back（閉じずに背面へ。W1-6）
//
//  引数なし。MainActivity の moveTaskToBack(true)（API 1。タスクの根でなくても背面へ回す）を UI スレッドで呼ぶ（すぐ返る。返答 {}）。
//  戻るの最上位で「閉じずに背面へ」に使う（docs/app_platform_roadmap.md §4 X-3）。閉じる（finish）と onDestroy でプロセスごと
//  終わり（MainActivity.onDestroy）、次の起動が冷える（.NET の起動・シーンの読み込みからやり直す）ため。背面へ回ったアプリは
//  従来どおり onStop → エンジンの suspended（セーブの書き出し・音の停止）になり、ランチャー・最近のタスクから戻ると onNewIntent
//  （起動理由 launcher）で前面に戻る。デスクトップの模擬は何もしない（ログだけ）。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

import org.json.JSONObject;

/**
 * app.move_task_to_back。
 */
final class MoveTaskToBackCommand implements MainProcessCommand {

    /** moveTaskToBack の引数: タスクの根の Activity でなくても背面へ回す。 */
    private static final boolean NON_ROOT = true;

    @Override
    public byte[] handle(JSONObject arguments) {
        Activity activity = HostActivity.get();
        if (activity == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NO_ACTIVITY);
        }
        // タスクの操作は UI スレッドで（呼び出し元はエンジンのスレッド）。結果はログだけ（返答は受け付けたことだけ）
        activity.runOnUiThread(() -> {
            boolean moved = activity.moveTaskToBack(NON_ROOT);
            Log.i(PlatformContract.LOG_TAG, "閉じずに背面へ: " + (moved ? "背面へ回しました" : "回せませんでした"));
        });
        return PlatformJson.okReply(new JSONObject());
    }
}
