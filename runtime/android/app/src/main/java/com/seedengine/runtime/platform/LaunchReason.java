// ============================================================
//  LaunchReason.java — 起動の Intent から「この起動の理由」を決める（メインプロセス。MainActivity から。W1-4a）
//
//  【信用する起動（W1-P6。リリース版でも取れ、偽造できない）】
//  起動の Intent の部品名が activity-alias PlatformEntry（exported=false。main の AndroidManifest.xml）のときだけ、
//  extras の起動理由（PlatformContract.EXTRA_LAUNCH の JSON）を信用する。この別名を起動できるのは同じアプリ（:seed_platform が
//  作った PendingIntent）だけで、MainActivity（ランチャーのため exported）を他のアプリが同じ extras 付きで起動しても、部品名が
//  MainActivity なので信用しない（AC-6）。別名で起動すると Intent の部品名は別名のまま届く（W1-0 の実機で onCreate を確認。
//  onNewIntent も同じ仕組みで届く見込み〈推論。W1-4b の実機で確かめる〉）。
//  それ以外の起動は、action が MAIN（か無し）ならランチャー（launcher）、ほかは other。
//  最近のタスクからの開き直し（FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY）は、タスクの元の Intent（目覚ましで起きたタスクなら
//  PlatformEntry の鳴動の起動理由）がもう一度届くので、ランチャーとして扱う（止めた後の目覚ましで鳴動画面を出し直さない）。
//
//  【呼ばれ方】
//    onCreate（super.onCreate の前）… 理由を決めて SeedPlatform に預け（platform.launch_reason が返す）、alarm なら
//        setShowWhenLocked(true)・setTurnScreenOn(true)（ロック画面の上に出し、画面を点ける。ロックは解除しない）
//    onNewIntent（singleTask で起動済みのとき）… 同じく決めて預け、alarm なら上げ、イベント platform.launch を流す
//  alarm で上げた showWhenLocked は、アプリが鳴動を片付けた後に Window.SetShowWhenLocked(false) で下ろす
//  （下ろさないと、アプリを開いたまま電源ボタンを押してもロック画面が出ない。AC-5）。
// ============================================================

package com.seedengine.runtime.platform;

import android.app.Activity;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

import org.json.JSONException;
import org.json.JSONObject;

/**
 * 起動理由の判定（static のみ。UI スレッドから使う）。
 */
public final class LaunchReason {

    private LaunchReason() {
    }

    /**
     * 起動の Intent から理由を決める。
     *
     * @param context どの Context でもよい（パッケージ名を使う）
     * @param intent  起動の Intent（null 可）
     * @return 起動理由
     */
    public static LaunchInfo classify(Context context, Intent intent) {
        if (intent == null) {
            return LaunchInfo.LAUNCHER;
        }
        if ((intent.getFlags() & Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY) != 0) {
            // 最近のタスクからの開き直し: タスクの元の Intent（目覚ましで起きたタスクなら PlatformEntry と鳴動の起動理由）が
            // もう一度届くが、今鳴っているわけではないのでランチャーと同じに扱う
            return LaunchInfo.LAUNCHER;
        }
        ComponentName component = intent.getComponent();
        boolean viaPlatformEntry = component != null
                && context.getPackageName().equals(component.getPackageName())
                && PlatformContract.PLATFORM_ENTRY_ALIAS.equals(component.getClassName());
        if (viaPlatformEntry) {
            return fromPlatformEntry(intent);
        }
        String action = intent.getAction();
        return action == null || Intent.ACTION_MAIN.equals(action) ? LaunchInfo.LAUNCHER : LaunchInfo.OTHER;
    }

    /**
     * Activity の作成（MainActivity.onCreate の super.onCreate より前）で理由を決めて預け、alarm ならロック画面の上に出す。
     *
     * @param activity 作られている MainActivity
     */
    public static void onCreate(Activity activity) {
        LaunchInfo info = classify(activity, activity.getIntent());
        SeedPlatform.setLaunchReason(info);
        applyWindowFlags(activity, info);
        Log.i(PlatformContract.LOG_TAG, "起動理由: " + info + delaySuffix(info));
    }

    /**
     * 起動済みの Activity に Intent が届いた（singleTask の onNewIntent）: 理由を決めて預け、alarm なら上げ、
     * イベント platform.launch を流す。
     *
     * @param activity 起動済みの MainActivity
     * @param intent   届いた Intent
     */
    public static void onNewIntent(Activity activity, Intent intent) {
        LaunchInfo info = classify(activity, intent);
        SeedPlatform.setLaunchReason(info);
        applyWindowFlags(activity, info);
        SeedPlatform.emitLocalEvent(PlatformContract.EVENT_LAUNCH, info.toJson());
        Log.i(PlatformContract.LOG_TAG, "起動後に届いた Intent の理由: " + info + delaySuffix(info));
    }

    /**
     * PlatformEntry 経由の Intent の起動理由を読む（読めなければ other）。
     *
     * @param intent 起動の Intent
     * @return 起動理由
     */
    private static LaunchInfo fromPlatformEntry(Intent intent) {
        String text = intent.getStringExtra(PlatformContract.EXTRA_LAUNCH);
        if (text == null) {
            Log.w(PlatformContract.LOG_TAG, "PlatformEntry 経由の起動に起動理由がありません（other として扱います）");
            return LaunchInfo.OTHER;
        }
        try {
            LaunchInfo info = LaunchInfo.fromTrustedJson(new JSONObject(text));
            if (info == null) {
                Log.w(PlatformContract.LOG_TAG, "PlatformEntry 経由の起動理由の種類が約束に無いので other として扱います: " + text);
                return LaunchInfo.OTHER;
            }
            return info;
        } catch (JSONException e) {
            Log.w(PlatformContract.LOG_TAG, "PlatformEntry 経由の起動理由が読めないので other として扱います: " + PlatformJson.describe(e));
            return LaunchInfo.OTHER;
        }
    }

    /**
     * alarm ならロック画面の上に出し、画面を点ける（それ以外は触らない。下ろすのはアプリの Window.SetShowWhenLocked(false)）。
     *
     * @param activity MainActivity
     * @param info     起動理由
     */
    private static void applyWindowFlags(Activity activity, LaunchInfo info) {
        if (!info.isAlarm()) {
            return;
        }
        // API 27 からの Activity の API（minSdk 29 なので分岐は要らない）。マニフェストで静的に宣言しない（鳴っていないときに
        // ロック画面の上に出てしまう。アプリ仕様 §6.12 の教訓）
        activity.setShowWhenLocked(true);
        activity.setTurnScreenOn(true);
    }

    /** 予定時刻からの経過（ログ用。予定時刻が無ければ空）。 */
    private static String delaySuffix(LaunchInfo info) {
        if (info.scheduledAtUtcMs <= 0) {
            return "";
        }
        return "（予定から " + (System.currentTimeMillis() - info.scheduledAtUtcMs) + " ms）";
    }
}
