// ============================================================
//  AppSettingsScreen.java — 端末の「アプリ情報」の画面（メインプロセス・UI スレッド。W1-6 で共通化）
//
//  Settings.ACTION_APPLICATION_DETAILS_SETTINGS（API 9）に data = package:<アプリ ID> を付けて開く。
//  Activity から開く（FLAG_ACTIVITY_NEW_TASK なし）ので、利用者が戻ると MainActivity の onResume が来る（権限の状態の見直しと同じ流れ）。
//  使う所:
//    local/OpenAppSettingsCommand      … スクリプトの App.OpenAppSettings（app.open_app_settings）
//    permission/PermissionSettings     … 権限ごとの画面が無い・開けないときの受け皿と、package:<アプリ ID> の URI
// ============================================================

package com.seedengine.runtime.platform.app;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.provider.Settings;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

/**
 * アプリ情報の画面（static のみ）。
 */
public final class AppSettingsScreen {

    private AppSettingsScreen() {
    }

    /** アプリを指す URI の scheme（package:<アプリ ID>）。 */
    private static final String PACKAGE_SCHEME = "package";

    /** URI の fragment を付けない。 */
    private static final String NO_FRAGMENT = null;

    /**
     * アプリを指す URI（package:<アプリ ID>。設定の画面の Intent の data）。
     *
     * @param context どの Context でもよい（パッケージ名を使う）
     * @return URI
     */
    public static Uri packageUri(Context context) {
        return Uri.fromParts(PACKAGE_SCHEME, context.getPackageName(), NO_FRAGMENT);
    }

    /**
     * アプリ情報の画面の Intent。
     *
     * @param context どの Context でもよい（パッケージ名を使う）
     * @return Intent
     */
    public static Intent intent(Context context) {
        return new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, packageUri(context));
    }

    /**
     * アプリ情報の画面を開く（UI スレッドで呼ぶこと）。
     *
     * @param activity 開く元の Activity（MainActivity）
     * @return 開けたら true（端末に画面が無い・拒まれたら false。ログに残す）
     */
    public static boolean open(Activity activity) {
        try {
            activity.startActivity(intent(activity));
            Log.i(PlatformContract.LOG_TAG, "アプリ情報の画面を開きました");
            return true;
        } catch (ActivityNotFoundException | SecurityException e) {
            Log.w(PlatformContract.LOG_TAG, "アプリ情報の画面を開けませんでした: " + PlatformJson.describe(e));
            return false;
        }
    }
}
