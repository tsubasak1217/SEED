// ============================================================
//  PermissionLifecycle.java — MainActivity の onResume・onRequestPermissionsResult から権限の処理へつなぐ受け口（W1-5）
//
//  MainActivity は契機を渡すだけにする（中身はこのパッケージ）:
//    onResume                   → ① 設定の画面から戻るのを待っていた要求に結果を返す（PermissionRequests）
//                                 ② 状態を前回と比べて permission_changed（PermissionMonitor）
//    onRequestPermissionsResult → 確認の画面の結果（PermissionRequests。自分の要求コードのときだけ）
//  ここでの失敗（システムのサービスの想定外の例外）は Activity の寿命の処理を止めないよう、ログに残して飲み込む
//  （JNI の境界の SeedPlatform.invoke と同じ方針。起動・前面への復帰でアプリが落ちるのを防ぐ）。
// ============================================================

package com.seedengine.runtime.platform.permission;

import android.app.Activity;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;

/**
 * MainActivity からの受け口（static のみ。UI スレッドから呼ぶ）。
 */
public final class PermissionLifecycle {

    private PermissionLifecycle() {
    }

    /**
     * 前面に来た（MainActivity.onResume の super の後）。
     *
     * @param activity MainActivity
     */
    public static void onResume(Activity activity) {
        try {
            PermissionRequests.onResume(activity);
            PermissionMonitor.onResume(activity);
        } catch (RuntimeException e) {
            Log.e(PlatformContract.LOG_TAG, "前面への復帰での権限の確かめに失敗しました（Activity は続けます）", e);
        }
    }

    /**
     * 確認の画面の結果（MainActivity.onRequestPermissionsResult の super の後）。
     *
     * @param activity     MainActivity
     * @param requestCode  要求コード
     * @param permissions  求めた権限
     * @param grantResults 結果
     */
    public static void onRequestPermissionsResult(Activity activity, int requestCode, String[] permissions, int[] grantResults) {
        try {
            PermissionRequests.onRequestPermissionsResult(activity, requestCode, permissions, grantResults);
        } catch (RuntimeException e) {
            Log.e(PlatformContract.LOG_TAG, "権限の確認の画面の結果の処理に失敗しました（Activity は続けます）", e);
        }
    }
}
