// ============================================================
//  PermissionStatusProbe.java — 権限の種類ごとの「今の状態」を決める（メインプロセス。W1-5）
//
//  【判定表】（状態は PlatformContract.PERMISSION_STATUS_*。機能〈宣言〉の有無は呼び出し側が先に見る）
//    post_notifications  API 33+ … checkSelfPermission(POST_NOTIFICATIONS) が許可 → 通知が有効（areNotificationsEnabled）なら granted、
//                                   切られていれば needs_settings。不許可 → rationale（shouldShowRequestPermissionRationale）が true なら
//                                   denied、false で「はっきり拒否された覚え」（PermissionHistory）があれば denied_permanently、無ければ denied
//                        API 32 以下 … 実行時の許可が無いので areNotificationsEnabled() で granted か needs_settings
//    exact_alarm         API 33+ … USE_EXACT_ALARM が許可（インストール時に自動で許可・取り消せない）なら granted。無ければ
//                                   canScheduleExactAlarms() で granted か needs_settings
//                        API 31〜32 … canScheduleExactAlarms() で granted か needs_settings（SCHEDULE_EXACT_ALARM は特別なアクセス）
//                        API 30 以下 … not_applicable（正確なアラームに許可が要らない）
//    full_screen_intent  API 34+ … NotificationManager.canUseFullScreenIntent() で granted か needs_settings（特別なアクセス）
//                        API 33 以下 … granted（USE_FULL_SCREEN_INTENT はインストール時に許可される）
//    record_audio / send_sms … not_applicable（v2 の予約）
//  【確認の画面の結果】拒否のときは deniedByDialog: 求めた後の rationale が true なら denied（一度目の拒否。覚える）、false で求める前の
//  rationale が true なら denied_permanently（二度目の拒否。Android 11+ は二度の拒否で以後確認の画面を出さない。覚える）、どちらも false なら
//  覚えがあれば denied_permanently（もう画面が出なかった）、無ければ denied（画面の外側のタップで閉じた。覚えない）。
//  使った API の版は SDK の api-versions.xml で確かめた（checkSelfPermission 23・shouldShowRequestPermissionRationale 23・
//  areNotificationsEnabled 24・canScheduleExactAlarms 31・canUseFullScreenIntent 34）。
// ============================================================

package com.seedengine.runtime.platform.permission;

import android.Manifest;
import android.app.Activity;
import android.app.AlarmManager;
import android.app.NotificationManager;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;

import com.seedengine.runtime.platform.PlatformContract;

/**
 * 状態の判定（static のみ。どのスレッドから呼んでもよい。どれもシステムのサービスへの問い合わせ）。
 */
public final class PermissionStatusProbe {

    private PermissionStatusProbe() {
    }

    /**
     * 今の状態。
     *
     * @param context  どの Context でもよい（アプリの Context）
     * @param activity rationale を問い合わせる Activity（null なら rationale は false として扱う）
     * @param kind     種類（機能の有無は呼び出し側が確かめ済み）
     * @return 状態（PlatformContract.PERMISSION_STATUS_*）
     */
    public static String status(Context context, Activity activity, PermissionKind kind) {
        switch (kind) {
            case POST_NOTIFICATIONS:
                return postNotifications(context, activity);
            case EXACT_ALARM:
                return exactAlarm(context);
            case FULL_SCREEN_INTENT:
                return fullScreenIntent(context);
            default:
                return PlatformContract.PERMISSION_STATUS_NOT_APPLICABLE;
        }
    }

    /**
     * 実行時の確認の画面（requestPermissions）で求める種類か（それ以外は設定の画面を開く）。
     *
     * @param kind 種類
     * @return 確認の画面で求めるなら true（Android 13+ の post_notifications だけ）
     */
    static boolean usesRuntimeDialog(PermissionKind kind) {
        return kind == PermissionKind.POST_NOTIFICATIONS && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU;
    }

    /**
     * 確認の画面で拒否されたときの状態（onRequestPermissionsResult から。覚えの出し入れもする）。
     *
     * @param context         どの Context でもよい
     * @param kind            種類
     * @param rationaleBefore 求める直前の rationale
     * @param rationaleAfter  結果を受けた直後の rationale
     * @return denied か denied_permanently
     */
    static String deniedByDialog(Context context, PermissionKind kind, boolean rationaleBefore, boolean rationaleAfter) {
        if (rationaleAfter) {
            // 一度目の拒否（次に求めれば、理由を見せてからもう一度確認の画面が出る）
            PermissionHistory.rememberDenial(context, kind);
            return PlatformContract.PERMISSION_STATUS_DENIED;
        }
        if (rationaleBefore) {
            // 二度目の拒否（以後、求めても確認の画面は出ない）
            PermissionHistory.rememberDenial(context, kind);
            return PlatformContract.PERMISSION_STATUS_DENIED_PERMANENTLY;
        }
        // 前後とも false: 覚えがあれば画面が出ずに拒否された（永続）、無ければ画面の外側で閉じた（覚えない）
        return PermissionHistory.wasDenied(context, kind)
                ? PlatformContract.PERMISSION_STATUS_DENIED_PERMANENTLY
                : PlatformContract.PERMISSION_STATUS_DENIED;
    }

    /** 通知。 */
    private static String postNotifications(Context context, Activity activity) {
        boolean enabled = notificationManager(context).areNotificationsEnabled();
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
            // Android 12 以前は実行時の許可が無い（利用者が設定で切れるだけ）
            return enabled ? PlatformContract.PERMISSION_STATUS_GRANTED : PlatformContract.PERMISSION_STATUS_NEEDS_SETTINGS;
        }
        if (context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) {
            PermissionHistory.forgetDenial(context, PermissionKind.POST_NOTIFICATIONS);
            return enabled ? PlatformContract.PERMISSION_STATUS_GRANTED : PlatformContract.PERMISSION_STATUS_NEEDS_SETTINGS;
        }
        boolean rationale = activity != null && activity.shouldShowRequestPermissionRationale(Manifest.permission.POST_NOTIFICATIONS);
        if (rationale) {
            return PlatformContract.PERMISSION_STATUS_DENIED;
        }
        return PermissionHistory.wasDenied(context, PermissionKind.POST_NOTIFICATIONS)
                ? PlatformContract.PERMISSION_STATUS_DENIED_PERMANENTLY
                : PlatformContract.PERMISSION_STATUS_DENIED;
    }

    /** 正確なアラーム。 */
    private static String exactAlarm(Context context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) {
            return PlatformContract.PERMISSION_STATUS_NOT_APPLICABLE;
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU
                && context.checkSelfPermission(Manifest.permission.USE_EXACT_ALARM) == PackageManager.PERMISSION_GRANTED) {
            return PlatformContract.PERMISSION_STATUS_GRANTED;
        }
        AlarmManager alarms = context.getSystemService(AlarmManager.class);
        return alarms != null && alarms.canScheduleExactAlarms()
                ? PlatformContract.PERMISSION_STATUS_GRANTED
                : PlatformContract.PERMISSION_STATUS_NEEDS_SETTINGS;
    }

    /** フルスクリーン通知。 */
    private static String fullScreenIntent(Context context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            return PlatformContract.PERMISSION_STATUS_GRANTED;
        }
        return notificationManager(context).canUseFullScreenIntent()
                ? PlatformContract.PERMISSION_STATUS_GRANTED
                : PlatformContract.PERMISSION_STATUS_NEEDS_SETTINGS;
    }

    /** NotificationManager（システムのサービスなので、アプリのプロセスでは必ずある）。 */
    private static NotificationManager notificationManager(Context context) {
        return context.getSystemService(NotificationManager.class);
    }
}
