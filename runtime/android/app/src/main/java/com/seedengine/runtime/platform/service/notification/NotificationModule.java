// ============================================================
//  NotificationModule.java — 通知のモジュール "notification"（:seed_platform の命令。W1-5。機能 notifications）
//
//    notification.ensure_channel … チャネルを作る（あれば名前・説明だけ変わる）。引数 { channel_id, name, importance, description }。
//                                  返答 { channel_id }
//    notification.show           … 通知を出す（同じ id は置き換え）。引数 { id, channel_id, title, body, ongoing, category,
//                                  actions: [ { id, label } ×最大 3 ], payload_json }。返答 { id }。
//                                  通知が無効なら notifications_disabled、チャネルが無ければ channel_not_found
//    notification.cancel         … 通知を消す（無い id でも成功＝冪等）。引数 { id }。返答 { id }
//    notification.are_enabled    … アプリの通知が端末で有効か。返答 { enabled }
//
//  【ID】アプリの文字列の ID をそのまま NotificationManager の tag にし、int の ID は定数 NOTIFICATION_ID に固定する
//  （tag と ID の組で通知が決まるので、int の ID の表を持たずに済む。鳴動の通知〈tag なし・7201/7202〉とは tag の有無で分かれる）。
//  【どこから出すか】このプロセス（:seed_platform）から出すので、メインプロセス（エンジン）が居なくなっても通知は残る
//  （例: スヌーズの通知を出した後にアプリを閉じても、通知から開き直せる）。
//  【機能】APK に POST_NOTIFICATIONS の宣言が無い（project_settings.json の android.features に notifications も alarm も無い）ときは、
//  どの命令も feature_not_enabled で断る（宣言の調べ方は platform/DeclaredPermissions）。鳴動の通知（alarm/ring/RingNotification）は
//  このモジュールを通らないので、この判定と関係なく W1-4a のまま動く。
//  デスクトップの模擬（runtime/src/engine/platform/bridge/desktop_sim/notification_commands.rs）が同じ命令に同じ形で答える。
// ============================================================

package com.seedengine.runtime.platform.service.notification;

import android.Manifest;
import android.content.Context;
import android.os.Bundle;
import android.util.Log;

import com.seedengine.runtime.platform.DeclaredPermissions;
import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.PlatformModule;

import org.json.JSONObject;

/**
 * モジュール "notification"（状態は OS の NotificationManager が持ち、ここは持たない）。
 */
public final class NotificationModule implements PlatformModule {

    /**
     * アプリの通知の int の ID（tag = アプリの文字列の ID と組にする。全部の通知で同じ値）。鳴動の通知の ID（7201・7202）と
     * 見分けやすい値にした（dumpsys notification の id= の欄）。
     */
    static final int NOTIFICATION_ID = 7_300;

    @Override
    public String name() {
        return PlatformContract.MODULE_NOTIFICATION;
    }

    @Override
    public byte[] handle(Context context, String method, Object request, Bundle extras) {
        if (!DeclaredPermissions.isDeclared(context, Manifest.permission.POST_NOTIFICATIONS)) {
            return PlatformJson.errorReply(PlatformContract.ERROR_FEATURE_NOT_ENABLED,
                    "project_settings.json の android.features に \"notifications\" がありません（POST_NOTIFICATIONS の宣言が無い）");
        }
        JSONObject arguments = PlatformJson.asObject(request);
        switch (method) {
            case PlatformContract.METHOD_NOTIFICATION_ENSURE_CHANNEL:
                return ensureChannel(context, arguments);
            case PlatformContract.METHOD_NOTIFICATION_SHOW:
                return show(context, arguments);
            case PlatformContract.METHOD_NOTIFICATION_CANCEL:
                return cancel(context, arguments);
            case PlatformContract.METHOD_NOTIFICATION_ARE_ENABLED:
                return areEnabled(context);
            default:
                return PlatformJson.errorReply(PlatformContract.ERROR_UNKNOWN_METHOD);
        }
    }

    /** ensure_channel: 検査してチャネルを作る。 */
    private static byte[] ensureChannel(Context context, JSONObject arguments) {
        NotificationRequestReader.Result<NotificationChannelSpec> read = NotificationRequestReader.readChannel(arguments);
        if (!read.ok()) {
            return PlatformJson.errorReply(read.error, read.detail);
        }
        NotificationChannelSpec spec = read.value;
        NotificationChannels.ensure(context, spec);
        Log.i(PlatformContract.LOG_TAG, "通知チャネル " + spec.id + "（" + spec.name + "・重要度 " + spec.importance + "）を用意しました");
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_NOTIFICATION_CHANNEL_ID, spec.id);
        return PlatformJson.okReply(fields);
    }

    /** show: 検査し、出せるかを確かめてから出す。 */
    private static byte[] show(Context context, JSONObject arguments) {
        NotificationRequestReader.Result<NotificationContent> read = NotificationRequestReader.readShow(arguments);
        if (!read.ok()) {
            return PlatformJson.errorReply(read.error, read.detail);
        }
        NotificationContent content = read.value;
        NotificationChannels.Readiness readiness = NotificationChannels.readiness(context, content.channelId);
        if (!readiness.ok()) {
            Log.w(PlatformContract.LOG_TAG, "通知 " + content.id + " を出せません: " + readiness.error + "（" + readiness.detail + "）");
            return PlatformJson.errorReply(readiness.error, readiness.detail);
        }
        NotificationChannels.manager(context).notify(content.id, NOTIFICATION_ID, NotificationFactory.build(context, content));
        Log.i(PlatformContract.LOG_TAG, "通知 " + content.id + " を出しました（チャネル " + content.channelId
                + (content.ongoing ? "・常駐" : "") + "・操作 " + content.actions.size() + " 個）");
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_NOTIFICATION_ID, content.id);
        return PlatformJson.okReply(fields);
    }

    /** cancel: 消す（無い ID でも成功）。 */
    private static byte[] cancel(Context context, JSONObject arguments) {
        NotificationRequestReader.Result<String> read = NotificationRequestReader.readId(arguments);
        if (!read.ok()) {
            return PlatformJson.errorReply(read.error, read.detail);
        }
        NotificationChannels.manager(context).cancel(read.value, NOTIFICATION_ID);
        Log.i(PlatformContract.LOG_TAG, "通知 " + read.value + " を消しました");
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_NOTIFICATION_ID, read.value);
        return PlatformJson.okReply(fields);
    }

    /** are_enabled: アプリの通知が端末で有効か。 */
    private static byte[] areEnabled(Context context) {
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_NOTIFICATIONS_ENABLED, NotificationChannels.areEnabled(context));
        return PlatformJson.okReply(fields);
    }
}
