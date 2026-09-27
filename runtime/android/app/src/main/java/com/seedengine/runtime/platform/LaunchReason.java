// ============================================================
//  LaunchReason.java — 起動の Intent から「この起動の理由」を決める（メインプロセス。MainActivity から。W1-4a・W1-6 でディープリンクと忘れ対策）
//
//  【信用する起動（W1-P6。リリース版でも取れ、偽造できない）】
//  起動の Intent の部品名が activity-alias PlatformEntry（exported=false。main の AndroidManifest.xml）のときだけ、
//  extras の起動理由（PlatformContract.EXTRA_LAUNCH の JSON）を信用する。この別名を起動できるのは同じアプリ（:seed_platform が
//  作った PendingIntent）だけで、MainActivity（ランチャーのため exported）を他のアプリが同じ extras 付きで起動しても、部品名が
//  MainActivity なので信用しない（AC-6）。別名で起動すると Intent の部品名は別名のまま届く（W1-0 の実機で onCreate を確認。
//  onNewIntent も同じ仕組みで届く見込み〈推論。W1-4b の実機で確かめる〉）。
//  それ以外の起動は、action が MAIN（か無し）ならランチャー（launcher）、VIEW で data があればディープリンク（deep_link。W1-6。
//  uri に data。機能 deep_links の intent-filter に合った URL のほか、他のアプリが MainActivity を明示して送った VIEW も同じに見えるので、
//  中身はアプリが検査する。MAX_URL_LENGTH 文字を超える・data が無い VIEW は other）、ほかは other。
//  最近のタスクからの開き直し（FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY）は、タスクの元の Intent（目覚ましで起きたタスクなら
//  PlatformEntry の鳴動の起動理由、ディープリンクで起きたタスクならその URL）がもう一度届くので、ランチャーとして扱う
//  （止めた後の目覚ましで鳴動画面を出し直さない・同じディープリンクを二度処理させない）。
//
//  【呼ばれ方】
//    onCreate（super.onCreate の前）… 理由を決めて SeedPlatform に預け（platform.launch_reason が返す）、alarm なら
//        setShowWhenLocked(true)・setTurnScreenOn(true)（ロック画面の上に出し、画面を点ける。ロックは解除しない）
//    onNewIntent（singleTask で起動済みのとき）… 同じく決めて預け、alarm なら上げ、launcher なら下ろし（下の忘れ対策）、
//        イベント platform.launch を流す
//  alarm で上げた showWhenLocked は、アプリが鳴動を片付けた後に Window.SetShowWhenLocked(false) で下ろす
//  （下ろさないと、アプリを開いたまま電源ボタンを押してもロック画面が出ない。AC-5）。
//  【忘れ対策（W1-6）】アプリが下ろし忘れても、ランチャー・最近のタスクからの開き直し（onNewIntent で launcher）では下ろす
//  （目覚ましで上げたまま背面へ回ったアプリを次に普通に開いたとき、ロック画面の上に出る状態を持ち越さない）。利用者が自分で
//  開いた＝端末のロックは解除されているので、下ろしても鳴動画面が隠れることは無い。鳴動が止まった（ring_stopped）ときに
//  自動で下ろすことはしない（鳴動画面を出したまま解除の後の画面を続けたいアプリがあるため）。
// ============================================================

package com.seedengine.runtime.platform;

import android.app.Activity;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

import com.seedengine.runtime.platform.window.LockScreenPresence;

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
        if (Intent.ACTION_VIEW.equals(action)) {
            return fromDeepLink(intent);
        }
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
        // 作られたばかりの窓はロック画面の上に出ていないので、下ろす必要は無い（上げるのは alarm だけ）
        applyWindowFlags(activity, info, false);
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
        applyWindowFlags(activity, info, true);
        SeedPlatform.emitLocalEvent(PlatformContract.EVENT_LAUNCH, info.toJson());
        Log.i(PlatformContract.LOG_TAG, "起動後に届いた Intent の理由: " + info + delaySuffix(info));
    }

    /**
     * VIEW の Intent をディープリンクとして読む（W1-6。data が無い・長すぎるなら other）。
     *
     * @param intent 起動の Intent（action が VIEW）
     * @return 起動理由
     */
    private static LaunchInfo fromDeepLink(Intent intent) {
        String uri = intent.getDataString();
        if (uri == null || uri.isEmpty()) {
            Log.w(PlatformContract.LOG_TAG, "VIEW の起動に data がありません（other として扱います）");
            return LaunchInfo.OTHER;
        }
        int length = uri.codePointCount(0, uri.length());
        if (length > PlatformContract.MAX_URL_LENGTH) {
            Log.w(PlatformContract.LOG_TAG, "ディープリンクの URI が長すぎます（" + length + " 文字 > " + PlatformContract.MAX_URL_LENGTH
                    + "。other として扱います）");
            return LaunchInfo.OTHER;
        }
        return LaunchInfo.deepLink(uri);
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
     * alarm ならロック画面の上に出し、画面を点ける。起動済みの Activity へランチャー・最近のタスクから開き直したとき（reopened で
     * launcher）は下ろす（W1-6 の忘れ対策）。それ以外は触らない（下ろすのは基本はアプリの Window.SetShowWhenLocked(false)）。
     *
     * @param activity MainActivity
     * @param info     起動理由
     * @param reopened 起動済みの Activity に届いた Intent（onNewIntent）なら true
     */
    private static void applyWindowFlags(Activity activity, LaunchInfo info, boolean reopened) {
        if (info.isAlarm()) {
            // 中身は window/LockScreenPresence（setShowWhenLocked・setTurnScreenOn。マニフェストで静的に宣言しない理由もそこ）
            LockScreenPresence.apply(activity, true);
        } else if (reopened && info.isLauncher()) {
            LockScreenPresence.apply(activity, false);
            Log.i(PlatformContract.LOG_TAG, "ランチャー・最近のタスクからの開き直しなので、ロック画面の上に出す・画面を点けるを下ろしました（忘れ対策）");
        }
    }

    /** 予定時刻からの経過（ログ用。予定時刻が無ければ空）。 */
    private static String delaySuffix(LaunchInfo info) {
        if (info.scheduledAtUtcMs <= 0) {
            return "";
        }
        return "（予定から " + (System.currentTimeMillis() - info.scheduledAtUtcMs) + " ms）";
    }
}
