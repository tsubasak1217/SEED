// ============================================================
//  OpenAppSettingsCommand.java — app.open_app_settings（端末の「アプリ情報」の画面を開く。W1-6）
//
//  引数なし。返答 {}（すぐ返る。開くのは UI スレッドで少し後。開けなかったらログだけ）。中身は app/AppSettingsScreen
//  （Settings.ACTION_APPLICATION_DETAILS_SETTINGS・data = package:<アプリ ID>・Activity から開くので戻ると onResume）。
//  権限の種類ごとの画面は permission.open_settings（W1-5）。Activity が要る（無ければ no_activity）。デスクトップの模擬はログだけ。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.app.AppSettingsScreen;

import org.json.JSONObject;

/**
 * app.open_app_settings。
 */
final class OpenAppSettingsCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        Activity activity = HostActivity.get();
        if (activity == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NO_ACTIVITY);
        }
        // 画面を開くのは UI スレッドだけ（呼び出し元はエンジンのスレッド）
        activity.runOnUiThread(() -> AppSettingsScreen.open(activity));
        return PlatformJson.okReply(new JSONObject());
    }
}
