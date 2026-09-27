// ============================================================
//  PermissionStatusMemory.java — 「前の onResume で見た権限の状態」の置き場（メインプロセスの SharedPreferences。M7）
//
//  PermissionChangeTracker.Store の Android の中身。置き場は PermissionPreferences（seed_platform_permissions.xml）の
//  last_status.<種類> のキー（拒否の覚え denied.<種類> と同じファイル）。
//
//  【書き込みは commit（同期）】前回の状態は「この後プロセスが止められても残っている」ことに意味がある
//  （利用者が設定で権限を取り消すと Android がプロセスを止める。M7）。apply は書き込みを背面へ回すので、止められる前に
//  ディスクへ届いたかを言い切れない（Activity の停止のときに Android が待つはずだが確かめていない）。書くのは状態が
//  変わったとき（最初の onResume と、権限が変わったとき）だけで、小さなファイル 1 つなので UI スレッドで同期に書いても短い。
// ============================================================

package com.seedengine.runtime.platform.permission;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;

/**
 * 前の onResume で見た状態の置き場（SharedPreferences。どのスレッドから呼んでもよい）。
 */
final class PermissionStatusMemory implements PermissionChangeTracker.Store {

    /** 「前回の状態」のキーの接頭辞（後ろに種類の wire の名前）。 */
    private static final String STATUS_KEY_PREFIX = "last_status.";

    /** 覚えが無いときの値。 */
    private static final String NO_STATUS = null;

    /** 置き場（アプリの Context から開いたもの）。 */
    private final SharedPreferences preferences;

    /**
     * 置き場を開く。
     *
     * @param context どの Context でもよい（アプリの Context で開く）
     */
    PermissionStatusMemory(Context context) {
        this.preferences = PermissionPreferences.open(context.getApplicationContext());
    }

    @Override
    public String load(String kindName) {
        return preferences.getString(key(kindName), NO_STATUS);
    }

    @Override
    public void save(String kindName, String status) {
        // 同期で書く（理由はファイル冒頭）。書けなくても権限の処理は止めない（次の onResume でまた書く）
        if (!preferences.edit().putString(key(kindName), status).commit()) {
            Log.w(PlatformContract.LOG_TAG, "権限 " + kindName + " の状態を覚えられませんでした（次に前面へ戻ったときに書き直します）");
        }
    }

    /** 種類のキー。 */
    private static String key(String kindName) {
        return STATUS_KEY_PREFIX + kindName;
    }
}
