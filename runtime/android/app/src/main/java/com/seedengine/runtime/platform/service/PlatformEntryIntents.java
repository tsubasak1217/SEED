// ============================================================
//  PlatformEntryIntents.java — :seed_platform から Activity を開く PendingIntent を、信頼できる入口 PlatformEntry 行きで作る（W1-4a）
//
//  【なぜ PlatformEntry か（W1-P6）】
//  MainActivity はランチャーのために exported なので、他のアプリも extras 付きで起動できる（起動理由を偽れる）。そこで
//  main の AndroidManifest.xml に exported=false の activity-alias PlatformEntry（targetActivity = MainActivity）を常設し、
//  :seed_platform が作る Activity 行きの PendingIntent は**すべて**この別名を通す。メインプロセスの LaunchReason は、起動の
//  Intent の部品名がこの別名のときだけ extras の起動理由（EXTRA_LAUNCH の JSON）を信用する。PendingIntent は作ったアプリの
//  身元で送られるので、exported=false の別名でも SystemUI（フルスクリーン通知・通知のタップ）から起動できる（W1-0 の実機で確認）。
//
//  【なぜ Activity を直接開くか】Android 12+ は通知（本文・ボタン）の PendingIntent から受信機・サービスを経由して Activity を
//  起動できない（通知のトランポリンの禁止）。停止などの判断はアプリのスクリプトが起動理由を見て StopRinging で行う。
//
//  【要求コード】PlatformEntry 行きの Intent は部品が同じで action も data も付けないので、PendingIntent の同一性
//  （要求コード＋Intent.filterEquals。extras は含まない）は**要求コードだけ**で決まる。用途ごとに下の REQUEST_* を使い分け、
//  FLAG_UPDATE_CURRENT で extras（起動理由）を最新に書き換える（鳴動は同時に 1 つなので、同じ用途の PendingIntent は共有してよい）。
//  このクラスは :seed_platform で使う（メインプロセスの読み手は platform/LaunchReason.java）。
// ============================================================

package com.seedengine.runtime.platform.service;

import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

import org.json.JSONObject;

/**
 * PlatformEntry 行きの PendingIntent と起動理由の JSON（static のみ）。
 */
public final class PlatformEntryIntents {

    private PlatformEntryIntents() {
    }

    /** 要求コード: ステータスバー・ロック画面の「次の目覚まし」の表示（AlarmClockInfo の showIntent。W1-3 の値を引き継ぐ）。 */
    public static final int REQUEST_ALARM_CLOCK_INFO = 1;

    /** 要求コード: 鳴動のフルスクリーン通知。 */
    public static final int REQUEST_RING_FULL_SCREEN = 2;

    /** 要求コード: 鳴動の通知の本文のタップ（端末の使用中はヘッドアップ通知になるので、ここから鳴動画面へ。W1-0 の F-2）。 */
    public static final int REQUEST_RING_CONTENT = 3;

    /** 要求コード: 鳴動の通知の「開く」の操作。 */
    public static final int REQUEST_RING_ACTION_OPEN = 4;

    /** 起動理由の数の欄が無いときの値。 */
    public static final long NO_TIME = 0L;

    /** 起動理由の文字列の欄が無いときの値。 */
    public static final String NO_TEXT = "";

    /**
     * 起動理由の JSON を作る（{kind, id, action_id, scheduled_at_utc_ms, fired_at_utc_ms, payload_json}。
     * メインプロセスの LaunchReason・Rust の wire::launch・C# の LaunchInfo と同じ欄）。
     *
     * @param kind             種類（PlatformContract.LAUNCH_KIND_*）
     * @param id               予約・通知の ID（無ければ空）
     * @param actionId         通知の操作の ID（notification_action のとき。無ければ空）
     * @param scheduledAtUtcMs 鳴るはずだった時刻（無ければ NO_TIME）
     * @param firedAtUtcMs     配信を受けた時刻（無ければ NO_TIME）
     * @param payloadJson      アプリの任意の JSON（無ければ空）
     * @return 起動理由
     */
    public static JSONObject launchJson(String kind, String id, String actionId, long scheduledAtUtcMs, long firedAtUtcMs,
                                        String payloadJson) {
        JSONObject launch = new JSONObject();
        PlatformJson.put(launch, PlatformContract.KEY_LAUNCH_KIND, kind);
        PlatformJson.put(launch, PlatformContract.KEY_ALARM_ID, id != null ? id : NO_TEXT);
        PlatformJson.put(launch, PlatformContract.KEY_LAUNCH_ACTION_ID, actionId != null ? actionId : NO_TEXT);
        PlatformJson.put(launch, PlatformContract.KEY_ALARM_SCHEDULED_AT_UTC_MS, scheduledAtUtcMs);
        PlatformJson.put(launch, PlatformContract.KEY_ALARM_FIRED_AT_UTC_MS, firedAtUtcMs);
        PlatformJson.put(launch, PlatformContract.KEY_ALARM_PAYLOAD_JSON, payloadJson != null ? payloadJson : NO_TEXT);
        return launch;
    }

    /**
     * PlatformEntry（→ MainActivity）を開く PendingIntent を作る（既にあれば extras を書き換える）。
     *
     * @param context      :seed_platform の Context
     * @param requestCode  用途ごとの要求コード（REQUEST_*）
     * @param launch       起動理由（launchJson）
     * @param noUserAction 利用者の操作によらない起動か（フルスクリーン通知。前面の Activity の onUserLeaveHint を呼ばせない）
     * @return PendingIntent（FLAG_IMMUTABLE。受け手に書き換えさせない）
     */
    public static PendingIntent activity(Context context, int requestCode, JSONObject launch, boolean noUserAction) {
        int flags = Intent.FLAG_ACTIVITY_NEW_TASK;
        if (noUserAction) {
            flags |= Intent.FLAG_ACTIVITY_NO_USER_ACTION;
        }
        // 別名はクラスではないので、部品は名前の文字列で指す（:seed_platform で MainActivity のクラスに触ると libSEED.so を読み込む）
        Intent intent = new Intent()
                .setClassName(context.getPackageName(), PlatformContract.PLATFORM_ENTRY_ALIAS)
                .addFlags(flags)
                .putExtra(PlatformContract.EXTRA_LAUNCH, launch.toString());
        return PendingIntent.getActivity(context, requestCode, intent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }
}
