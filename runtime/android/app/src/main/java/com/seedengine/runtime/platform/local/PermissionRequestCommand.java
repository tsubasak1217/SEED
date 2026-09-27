// ============================================================
//  PermissionRequestCommand.java — permission.request（権限を求める。メインプロセスで答える。W1-5）
//
//  引数 { kind }。返答 { kind, request_id }（すぐ返る）。結果は後からイベント platform.permission_result { request_id, kind, status }。
//  求め方（確認の画面・設定の画面・画面なしですぐ結果）は permission/PermissionRequests。Activity が要る（無ければ no_activity）。
//  デスクトップの模擬は画面を出さずにすぐ permission_result（granted。v2 の種類は not_applicable）。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.permission.PermissionRequests;

import org.json.JSONObject;

/**
 * permission.request。
 */
final class PermissionRequestCommand implements MainProcessCommand {

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
        int requestId = PermissionRequests.request(activity, parsed.kind);
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_PERMISSION_KIND, parsed.kind.wireName);
        PlatformJson.put(fields, PlatformContract.KEY_PERMISSION_REQUEST_ID, requestId);
        return PlatformJson.okReply(fields);
    }
}
