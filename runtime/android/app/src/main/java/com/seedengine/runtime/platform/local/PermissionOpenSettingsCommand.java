// ============================================================
//  PermissionOpenSettingsCommand.java — permission.open_settings（権限の設定の画面を開く。メインプロセスで答える。W1-5）
//
//  引数 { kind }。返答 { kind }（すぐ返る。開くのは UI スレッドで少し後）。開く画面は permission/PermissionSettings
//  （通知はアプリの通知の設定、正確なアラーム・フルスクリーン通知は特別なアクセスの画面、開けなければアプリ情報）。
//  結果のイベントは無い（戻ったときに状態が変わっていれば platform.permission_changed。permission/PermissionMonitor）。
//  Activity が要る（無ければ no_activity）。デスクトップの模擬は受け付けてログだけ。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.permission.PermissionKind;
import com.seedengine.runtime.platform.permission.PermissionSettings;

import org.json.JSONObject;

/**
 * permission.open_settings。
 */
final class PermissionOpenSettingsCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        Activity activity = HostActivity.get();
        if (activity == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NO_ACTIVITY);
        }
        PermissionArguments.Parsed parsed = PermissionArguments.read(arguments, activity.getApplicationContext());
        if (parsed.errorReply != null) {
            return parsed.errorReply;
        }
        PermissionKind kind = parsed.kind;
        // 画面を開くのは UI スレッドだけ（呼び出し元はエンジンのスレッド）
        activity.runOnUiThread(() -> PermissionSettings.open(activity, kind));
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_PERMISSION_KIND, kind.wireName);
        return PlatformJson.okReply(fields);
    }
}
