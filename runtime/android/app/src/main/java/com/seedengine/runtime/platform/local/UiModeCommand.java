// ============================================================
//  UiModeCommand.java — app.ui_mode（端末の明暗の設定。W2-9）
//
//  引数なし。MainActivity（無ければアプリの Context）の今の構成（Resources.getConfiguration）の夜の bit を返す
//  （返答 { night } = yes / no / unknown）。Activity の構成は onConfigurationChanged で新しい値になる（configChanges に uiMode）。
//  登録の前（Context が無い）は unknown。
//  変化はイベント platform.ui_mode_changed（platform/app/NightMode。MainActivity.onConfigurationChanged）で届く。
//  デスクトップの模擬は OS の「既定のアプリ モード」（runtime の desktop_sim/os_ui_mode.rs）。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;
import android.content.Context;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.app.NightMode;

import org.json.JSONObject;

/**
 * app.ui_mode。
 */
final class UiModeCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        Activity activity = HostActivity.get();
        Context context = activity != null ? activity : HostActivity.applicationContext();
        String night = context == null
                ? PlatformContract.APP_NIGHT_UNKNOWN
                : NightMode.of(context.getResources().getConfiguration());
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_APP_NIGHT, night);
        return PlatformJson.okReply(fields);
    }
}
