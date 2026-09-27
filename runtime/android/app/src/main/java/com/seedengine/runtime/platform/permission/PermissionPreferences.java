// ============================================================
//  PermissionPreferences.java — 権限の覚えの置き場（メインプロセスの SharedPreferences。W1-5・W2-10a の M7）
//
//  置き場はメインプロセスの SharedPreferences（shared_prefs/seed_platform_permissions.xml。資格情報で保護された置き場。
//  MainActivity はロック解除の後にしか動かないので足りる）。:seed_platform からは使わない（SharedPreferences はプロセスを
//  またいで同期しない）。同じファイルに次の 2 つを種類ごとのキーで持つ（キーの接頭辞で分ける）:
//    denied.<種類>      … 利用者がはっきり拒否したことがあるか（PermissionHistory）
//    last_status.<種類> … 前の onResume で見た状態（PermissionStatusMemory。プロセスが止められても比べられるように。M7）
// ============================================================

package com.seedengine.runtime.platform.permission;

import android.content.Context;
import android.content.SharedPreferences;

/**
 * 権限の覚えの置き場を開く（static のみ。どのスレッドから呼んでもよい。SharedPreferences がスレッドをまたいで安全）。
 */
final class PermissionPreferences {

    private PermissionPreferences() {
    }

    /** SharedPreferences の名前（shared_prefs/seed_platform_permissions.xml）。 */
    static final String PREFERENCES_NAME = "seed_platform_permissions";

    /**
     * 置き場を開く（Android が中身をメモリに持つので、何度呼んでも同じものが返る）。
     *
     * @param context どの Context でもよい
     * @return 置き場
     */
    static SharedPreferences open(Context context) {
        return context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE);
    }
}
