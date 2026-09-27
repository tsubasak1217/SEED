// ============================================================
//  PermissionRequests.java — permission.request の受け付けと、結果（platform.permission_result）の出し方（メインプロセス。W1-5）
//
//  【流れ】request（エンジンのスレッド）は要求の ID を払い出してすぐ返し、結果はイベントで届ける:
//    ① 今の状態が granted / not_applicable … 画面を出さずにすぐ permission_result（次のフレームでスクリプトへ届く）
//    ② 確認の画面の種類（Android 13+ の post_notifications で denied / denied_permanently）… UI スレッドで
//       Activity.requestPermissions → onRequestPermissionsResult（MainActivity → PermissionLifecycle）で状態を決めて permission_result。
//       永続の拒否なら OS は画面を出さずにすぐ拒否を返すので、そのまま denied_permanently が届く
//    ③ それ以外（needs_settings。正確なアラーム・フルスクリーン通知・通知が設定で切られている）… UI スレッドで設定の画面を開き
//       （PermissionSettings）、利用者が戻った onResume で状態を見て permission_result。開けなければすぐ今の状態を返す
//  同じ種類の要求が重なったら、出ている画面の結果を一緒に受け取る（確認の画面は一度に 1 つしか出せない。重ねて呼ぶと OS は空の結果で
//  取り消す〈AOSP の Activity.requestPermissions を読んで確かめた〉）。空の結果（中断）は今の状態を返す。
//
//  【要求コード】requestPermissions の要求コードは DIALOG_REQUEST_CODE の 1 つ（下位 16 bit に収める。androidx の
//  ActivityResultRegistry が払い出す番号は 0x10000 以上なので重ならない。MainActivity の onRequestPermissionsResult は super の後に
//  ここへ渡す）。要求の ID（スクリプトへ返す番号）とは別物。
//
//  【スレッド】request だけがエンジンのスレッドから呼ばれ、ID の払い出し（原子変数）と UI スレッドへの投げ込みだけをする。
//  待ちの表（dialogWaiting・settingsWaiting）は UI スレッドだけが触る（startDialog・startSettings・onRequestPermissionsResult・
//  onResume はどれも UI スレッド）ので、ロックは持たない。
// ============================================================

package com.seedengine.runtime.platform.permission;

import android.Manifest;
import android.app.Activity;
import android.content.Context;
import android.content.pm.PackageManager;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.EnumMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * 権限の要求（static のみ）。
 */
public final class PermissionRequests {

    private PermissionRequests() {
    }

    /** requestPermissions の要求コード（下位 16 bit。androidx の ActivityResultRegistry の番号〈0x10000 以上〉と重ならない）。 */
    static final int DIALOG_REQUEST_CODE = 0x5EED;

    /** 確認の画面で求める権限（Android 13+ の通知だけ）。 */
    private static final String[] DIALOG_PERMISSIONS = {Manifest.permission.POST_NOTIFICATIONS};

    /** 要求の結果に見つからない権限の位置。 */
    private static final int NOT_FOUND = -1;

    /** 次に払い出す要求の ID。 */
    private static final AtomicInteger NEXT_REQUEST_ID = new AtomicInteger(PlatformContract.FIRST_PERMISSION_REQUEST_ID);

    // ── 以下は UI スレッドだけが触る ──

    /** 確認の画面の結果を待っている要求の ID（重なった要求も一緒に待つ）。 */
    private static final List<Integer> dialogWaiting = new ArrayList<>();

    /** 確認の画面を出している途中か。 */
    private static boolean dialogInFlight;

    /** 確認の画面を出す直前の rationale（結果の判定に使う）。 */
    private static boolean rationaleBeforeDialog;

    /** 設定の画面からの戻りを待っている要求の ID（種類ごと）。 */
    private static final Map<PermissionKind, List<Integer>> settingsWaiting = new EnumMap<>(PermissionKind.class);

    /**
     * 求める（エンジンのスレッドから。permission.request）。
     *
     * @param activity MainActivity
     * @param kind     種類（機能の有無は確かめ済み）
     * @return 要求の ID（FIRST_PERMISSION_REQUEST_ID から増える）
     */
    public static int request(Activity activity, PermissionKind kind) {
        int requestId = NEXT_REQUEST_ID.getAndIncrement();
        Context context = activity.getApplicationContext();
        String status = PermissionStatusProbe.status(context, activity, kind);
        if (PlatformContract.PERMISSION_STATUS_GRANTED.equals(status) || PlatformContract.PERMISSION_STATUS_NOT_APPLICABLE.equals(status)) {
            // 求めるものが無い: 画面を出さずに結果だけ返す
            PermissionEvents.result(requestId, kind, status);
        } else if (PermissionStatusProbe.usesRuntimeDialog(kind) && !PlatformContract.PERMISSION_STATUS_NEEDS_SETTINGS.equals(status)) {
            activity.runOnUiThread(() -> startDialog(activity, kind, requestId));
        } else {
            activity.runOnUiThread(() -> startSettings(activity, kind, requestId));
        }
        Log.i(PlatformContract.LOG_TAG, "権限 " + kind.wireName + " を求めます（要求 " + requestId + "・今の状態 " + status + "）");
        return requestId;
    }

    /**
     * 確認の画面の結果（UI スレッド。MainActivity.onRequestPermissionsResult → PermissionLifecycle）。
     *
     * @param activity     MainActivity
     * @param requestCode  要求コード（DIALOG_REQUEST_CODE でなければ他人の要求なので何もしない）
     * @param permissions  求めた権限（中断なら空）
     * @param grantResults 結果（中断なら空）
     */
    static void onRequestPermissionsResult(Activity activity, int requestCode, String[] permissions, int[] grantResults) {
        if (requestCode != DIALOG_REQUEST_CODE || !dialogInFlight) {
            return;
        }
        List<Integer> waiting = new ArrayList<>(dialogWaiting);
        dialogWaiting.clear();
        dialogInFlight = false;
        String status = dialogStatus(activity, permissions, grantResults, rationaleBeforeDialog);
        for (int requestId : waiting) {
            PermissionEvents.result(requestId, PermissionKind.POST_NOTIFICATIONS, status);
        }
    }

    /**
     * 前面へ戻った（UI スレッド。PermissionLifecycle.onResume）: 設定の画面から戻るのを待っていた要求に、今の状態を返す。
     *
     * @param activity MainActivity
     */
    static void onResume(Activity activity) {
        for (PermissionKind kind : new ArrayList<>(settingsWaiting.keySet())) {
            resolveSettings(activity, kind);
        }
    }

    /** 確認の画面を出す（UI スレッド）。出している途中なら、その結果を一緒に待つ。 */
    private static void startDialog(Activity activity, PermissionKind kind, int requestId) {
        dialogWaiting.add(requestId);
        if (dialogInFlight) {
            return;
        }
        dialogInFlight = true;
        rationaleBeforeDialog = activity.shouldShowRequestPermissionRationale(Manifest.permission.POST_NOTIFICATIONS);
        try {
            // 結果は onRequestPermissionsResult（OS が一度に 1 つしか出せないときは、この中で空の結果がすぐ届く）
            activity.requestPermissions(DIALOG_PERMISSIONS, DIALOG_REQUEST_CODE);
        } catch (RuntimeException e) {
            // 出せなかった（Activity の状態など）: 待たずに今の状態を返す
            Log.w(PlatformContract.LOG_TAG, "権限 " + kind.wireName + " の確認の画面を出せませんでした: " + PlatformJson.describe(e));
            onRequestPermissionsResult(activity, DIALOG_REQUEST_CODE, new String[0], new int[0]);
        }
    }

    /** 設定の画面を開く（UI スレッド）。同じ種類で開いている途中なら、その戻りを一緒に待つ。開けなければすぐ今の状態を返す。 */
    private static void startSettings(Activity activity, PermissionKind kind, int requestId) {
        List<Integer> waiting = settingsWaiting.computeIfAbsent(kind, unused -> new ArrayList<>());
        boolean alreadyOpen = !waiting.isEmpty();
        waiting.add(requestId);
        if (alreadyOpen) {
            return;
        }
        if (!PermissionSettings.open(activity, kind)) {
            resolveSettings(activity, kind);
        }
    }

    /** 設定の画面を待っていた種類の要求に、今の状態を返す（UI スレッド）。 */
    private static void resolveSettings(Activity activity, PermissionKind kind) {
        List<Integer> waiting = settingsWaiting.remove(kind);
        if (waiting == null || waiting.isEmpty()) {
            return;
        }
        String status = PermissionStatusProbe.status(activity.getApplicationContext(), activity, kind);
        for (int requestId : waiting) {
            PermissionEvents.result(requestId, kind, status);
        }
    }

    /** 確認の画面の結果から状態を決める。 */
    private static String dialogStatus(Activity activity, String[] permissions, int[] grantResults, boolean rationaleBefore) {
        Context context = activity.getApplicationContext();
        int index = permissions != null ? Arrays.asList(permissions).indexOf(Manifest.permission.POST_NOTIFICATIONS) : NOT_FOUND;
        if (index == NOT_FOUND || grantResults == null || index >= grantResults.length) {
            // 中断（空の結果）: 今の状態をそのまま返す
            Log.i(PlatformContract.LOG_TAG, "通知の許可の確認の画面は中断されました（今の状態を返します）");
            return PermissionStatusProbe.status(context, activity, PermissionKind.POST_NOTIFICATIONS);
        }
        if (grantResults[index] == PackageManager.PERMISSION_GRANTED) {
            // 許可（通知が設定で切られていれば needs_settings。拒否の覚えもここで忘れる）
            return PermissionStatusProbe.status(context, activity, PermissionKind.POST_NOTIFICATIONS);
        }
        boolean rationaleAfter = activity.shouldShowRequestPermissionRationale(Manifest.permission.POST_NOTIFICATIONS);
        return PermissionStatusProbe.deniedByDialog(context, PermissionKind.POST_NOTIFICATIONS, rationaleBefore, rationaleAfter);
    }
}
