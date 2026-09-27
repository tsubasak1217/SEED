// ============================================================
//  PermissionSettings.java — 権限の種類ごとの設定の画面を開く（メインプロセス・UI スレッド。W1-5）
//
//  【開く画面】（Intent のアクションは api-versions.xml で版を確かめ、入力は AOSP の Settings.java の説明を読んで確かめた）
//    post_notifications … Settings.ACTION_APP_NOTIFICATION_SETTINGS（API 26。入力は extra EXTRA_APP_PACKAGE＝パッケージ名）
//    exact_alarm        … API 31+ は Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM（data に package:<アプリ ID>）。30 以下はアプリ情報
//    full_screen_intent … API 34+ は Settings.ACTION_MANAGE_APP_USE_FULL_SCREEN_INTENT（data に package:<アプリ ID>。必須）。
//                         33 以下は特別なアクセスの画面が無いのでアプリ情報
//    record_audio / send_sms（v2）… アプリ情報
//  開けない（ActivityNotFoundException。端末に画面が無い・メーカーが外した）ときはアプリ情報
//  （Settings.ACTION_APPLICATION_DETAILS_SETTINGS・package:<アプリ ID>。Intent と URI は W1-6 で app/AppSettingsScreen に共通化）を開く。
//  それも駄目なら false。
//  Activity から開く（FLAG_ACTIVITY_NEW_TASK なし）ので、利用者が戻ると MainActivity の onResume が来る（PermissionRequests・
//  PermissionMonitor がそこで状態を見直す）。
// ============================================================

package com.seedengine.runtime.platform.permission;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.provider.Settings;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.app.AppSettingsScreen;

/**
 * 設定の画面（static のみ。UI スレッドから呼ぶ）。
 */
public final class PermissionSettings {

    private PermissionSettings() {
    }

    /**
     * 種類の設定の画面を開く（開けなければアプリ情報）。UI スレッドで呼ぶこと。
     *
     * @param activity 開く元の Activity（MainActivity）
     * @param kind     種類
     * @return どちらかの画面を開けたら true
     */
    public static boolean open(Activity activity, PermissionKind kind) {
        Intent primary = intentFor(activity, kind);
        if (tryStart(activity, primary, kind)) {
            return true;
        }
        // 1 つ目が既にアプリ情報なら、もう試すものが無い
        if (Settings.ACTION_APPLICATION_DETAILS_SETTINGS.equals(primary.getAction())) {
            return false;
        }
        return tryStart(activity, AppSettingsScreen.intent(activity), kind);
    }

    /**
     * 種類の設定の画面の Intent。
     *
     * @param context どの Context でもよい（パッケージ名を使う）
     * @param kind    種類
     * @return Intent
     */
    static Intent intentFor(Context context, PermissionKind kind) {
        switch (kind) {
            case POST_NOTIFICATIONS:
                return new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                        .putExtra(Settings.EXTRA_APP_PACKAGE, context.getPackageName());
            case EXACT_ALARM:
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                    return new Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, AppSettingsScreen.packageUri(context));
                }
                break;
            case FULL_SCREEN_INTENT:
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                    return new Intent(Settings.ACTION_MANAGE_APP_USE_FULL_SCREEN_INTENT, AppSettingsScreen.packageUri(context));
                }
                break;
            default:
                break;
        }
        return AppSettingsScreen.intent(context);
    }

    /**
     * 画面を開いてみる。
     *
     * @return 開けたら true
     */
    private static boolean tryStart(Activity activity, Intent intent, PermissionKind kind) {
        try {
            activity.startActivity(intent);
            Log.i(PlatformContract.LOG_TAG, "権限 " + kind.wireName + " の設定の画面を開きました（" + intent.getAction() + "）");
            return true;
        } catch (ActivityNotFoundException | SecurityException e) {
            Log.w(PlatformContract.LOG_TAG, "権限 " + kind.wireName + " の設定の画面を開けませんでした（" + intent.getAction() + "）: "
                    + PlatformJson.describe(e));
            return false;
        }
    }
}
