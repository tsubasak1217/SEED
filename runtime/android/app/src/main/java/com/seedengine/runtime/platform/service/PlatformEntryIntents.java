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
//
//  【通知の PendingIntent の同一性（W1-5）】アプリの通知は同時にいくつも出るので、用途ごとの要求コードだけでは足りない
//  （共有すると FLAG_UPDATE_CURRENT で別の通知の起動理由に書き換わり、古い通知を押すと新しい通知の ID で起動する）。
//  要求コードを通知の ID から作る（ハッシュ）形は、文字列から int への写像なので必ずどこかで衝突する（鳩の巣）。そこで
//  要求コードは用途（本文のタップ・操作）ごとの定数のまま、Intent.setIdentifier（API 29 = minSdk）に「用途・通知の ID・操作の ID」を
//  長さ付きで並べた文字列（identity）を入れる。identifier は Intent.filterEquals の比べる欄に入り（AOSP の Intent.java で確認）、
//  PendingIntentRecord.Key は要求コード＋filterEquals で同一性を決める（同 PendingIntentRecord.java で確認）ので、
//  identity が違えば別の PendingIntent になる。長さ付きの並べ方は区切り文字を含む ID でも曖昧にならない（単射）。
//  identifier は IntentFilter の照合には使われない（別名 PlatformEntry は intent-filter を持たないので、どのみち関係ない）。
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

    /** 要求コード: アプリの通知（W1-5）の本文のタップ（通知ごとの区別は identity。上の【通知の PendingIntent の同一性】）。 */
    public static final int REQUEST_NOTIFICATION_TAP = 5;

    /** 要求コード: アプリの通知（W1-5）の操作（ボタン）。通知・操作ごとの区別は identity。 */
    public static final int REQUEST_NOTIFICATION_ACTION = 6;

    /** 起動理由の数の欄が無いときの値。 */
    public static final long NO_TIME = 0L;

    /** 起動理由の文字列の欄が無いときの値。 */
    public static final String NO_TEXT = "";

    /** identity の部品の区切り（長さの後ろと部品の後ろに置く。長さで切り出すので、部品が同じ文字を含んでも曖昧にならない）。 */
    private static final char IDENTITY_SEPARATOR = ':';

    /** identifier を付けない（用途ごとの要求コードだけで区別する鳴動の PendingIntent）。 */
    private static final String NO_IDENTITY = null;

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
        return activity(context, requestCode, NO_IDENTITY, launch, noUserAction);
    }

    /**
     * PlatformEntry（→ MainActivity）を開く PendingIntent を、要求コードと identity の組で作る（既にあれば extras を書き換える。W1-5）。
     *
     * @param context      :seed_platform の Context
     * @param requestCode  用途ごとの要求コード（REQUEST_*）
     * @param identity     同じ用途の中での区別（identity で作る。null なら付けない＝要求コードだけで区別）
     * @param launch       起動理由（launchJson）
     * @param noUserAction 利用者の操作によらない起動か（フルスクリーン通知）
     * @return PendingIntent（FLAG_IMMUTABLE）
     */
    public static PendingIntent activity(Context context, int requestCode, String identity, JSONObject launch,
                                         boolean noUserAction) {
        int flags = Intent.FLAG_ACTIVITY_NEW_TASK;
        if (noUserAction) {
            flags |= Intent.FLAG_ACTIVITY_NO_USER_ACTION;
        }
        // 別名はクラスではないので、部品は名前の文字列で指す（:seed_platform で MainActivity のクラスに触ると libSEED.so を読み込む）
        Intent intent = new Intent()
                .setClassName(context.getPackageName(), PlatformContract.PLATFORM_ENTRY_ALIAS)
                .addFlags(flags)
                .putExtra(PlatformContract.EXTRA_LAUNCH, launch.toString());
        if (identity != null) {
            // PendingIntent の同一性の欄（filterEquals）に入る。IntentFilter の照合には使われない（API 29）
            intent.setIdentifier(identity);
        }
        return PendingIntent.getActivity(context, requestCode, intent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }

    /**
     * 部品を長さ付きで並べた identity を作る（例 ["tap", "a:b"] → "3:tap:3:a:b:"）。
     *
     * <p>各部品を「符号単位の長さ・区切り・部品・区切り」で並べる。読み手は長さの分だけ切り出せるので、部品が区切りの文字を
     * 含んでも、別の部品の並びと同じ文字列にならない（単射。要求コードを文字列から作るハッシュと違って衝突しない）。</p>
     *
     * @param parts 部品（null は空の部品として扱う）
     * @return identity
     */
    public static String identity(String... parts) {
        StringBuilder identity = new StringBuilder();
        for (String part : parts) {
            String text = part != null ? part : NO_TEXT;
            identity.append(text.length()).append(IDENTITY_SEPARATOR).append(text).append(IDENTITY_SEPARATOR);
        }
        return identity.toString();
    }
}
