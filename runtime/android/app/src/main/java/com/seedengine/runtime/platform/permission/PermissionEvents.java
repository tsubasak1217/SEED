// ============================================================
//  PermissionEvents.java — 権限のイベントを作ってエンジンへ渡す（メインプロセス。W1-5）
//
//    platform.permission_result  { request_id, kind, status } … permission.request の結果（PermissionRequests）
//    platform.permission_changed { kind, status }             … 前面へ戻ったときに状態が前回と違った（PermissionMonitor）
//  どちらもメインプロセスの中で作る知らせ（seq 0。:seed_platform の記録を通らない）。SeedPlatform.emitLocalEvent が
//  nativeOnPlatformEvent でエンジンの箱へ積み、次のフレームでスクリプトの SEED.Events と PlatformEvents.OnEvent へ届く。
// ============================================================

package com.seedengine.runtime.platform.permission;

import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.SeedPlatform;

import org.json.JSONObject;

/**
 * 権限のイベント（static のみ。どのスレッドから呼んでもよい）。
 */
final class PermissionEvents {

    private PermissionEvents() {
    }

    /**
     * 要求の結果を流す。
     *
     * @param requestId 要求の ID（permission.request の返答と同じ）
     * @param kind      種類
     * @param status    状態（PlatformContract.PERMISSION_STATUS_*）
     */
    static void result(int requestId, PermissionKind kind, String status) {
        JSONObject data = new JSONObject();
        PlatformJson.put(data, PlatformContract.KEY_PERMISSION_REQUEST_ID, requestId);
        PlatformJson.put(data, PlatformContract.KEY_PERMISSION_KIND, kind.wireName);
        PlatformJson.put(data, PlatformContract.KEY_PERMISSION_STATUS, status);
        SeedPlatform.emitLocalEvent(PlatformContract.EVENT_PERMISSION_RESULT, data);
        Log.i(PlatformContract.LOG_TAG, "権限 " + kind.wireName + " の要求 " + requestId + " の結果: " + status);
    }

    /**
     * 状態の変化を流す。
     *
     * @param kind     種類
     * @param previous 前回の状態（ログ用）
     * @param status   今の状態
     */
    static void changed(PermissionKind kind, String previous, String status) {
        JSONObject data = new JSONObject();
        PlatformJson.put(data, PlatformContract.KEY_PERMISSION_KIND, kind.wireName);
        PlatformJson.put(data, PlatformContract.KEY_PERMISSION_STATUS, status);
        SeedPlatform.emitLocalEvent(PlatformContract.EVENT_PERMISSION_CHANGED, data);
        Log.i(PlatformContract.LOG_TAG, "権限 " + kind.wireName + " の状態が変わりました: " + previous + " → " + status);
    }
}
