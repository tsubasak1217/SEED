// ============================================================
//  PermissionHistory.java — 実行時の確認の画面で「拒否された」ことを覚える（メインプロセス。W1-5）
//
//  【なぜ要るか】Android は「二度と求められない（denied_permanently）」を直接は教えない。shouldShowRequestPermissionRationale は
//  「一度拒否された（次は理由を見せてから求めるとよい）」のときだけ true で、「一度も求めていない」と「二度拒否されて、もう確認の画面が
//  出ない」はどちらも false になる。そこで「利用者がはっきり拒否した」ことをここへ覚え、rationale が false でも覚えがあれば
//  denied_permanently とする（PermissionStatusProbe）。確認の画面を外側のタップで閉じた（どちらも選ばなかった）ときは覚えない
//  （次に求めればまた出るため）。許可を見たら忘れる（設定で許可 → 後で取り消された、を一度目の拒否として扱い直す）。
//
//  置き場はメインプロセスの SharedPreferences（seed_platform_permissions.xml。資格情報で保護された置き場。MainActivity はロック解除の後に
//  しか動かないので足りる）。:seed_platform からは使わない（SharedPreferences はプロセスをまたいで同期しない）。
// ============================================================

package com.seedengine.runtime.platform.permission;

import android.content.Context;
import android.content.SharedPreferences;

/**
 * 拒否の覚え（static のみ。どのスレッドから呼んでもよい。SharedPreferences がスレッドをまたいで安全）。
 */
final class PermissionHistory {

    private PermissionHistory() {
    }

    /** SharedPreferences の名前（shared_prefs/seed_platform_permissions.xml）。 */
    private static final String PREFERENCES_NAME = "seed_platform_permissions";

    /** 「拒否された」のキーの接頭辞（後ろに種類の wire の名前）。 */
    private static final String DENIED_KEY_PREFIX = "denied.";

    /** 覚えが無いときの値。 */
    private static final boolean NOT_DENIED = false;

    /**
     * 利用者がはっきり拒否したことがあるか。
     *
     * @param context どの Context でもよい
     * @param kind    種類
     * @return 覚えがあれば true
     */
    static boolean wasDenied(Context context, PermissionKind kind) {
        return preferences(context).getBoolean(key(kind), NOT_DENIED);
    }

    /**
     * 拒否を覚える。
     *
     * @param context どの Context でもよい
     * @param kind    種類
     */
    static void rememberDenial(Context context, PermissionKind kind) {
        preferences(context).edit().putBoolean(key(kind), true).apply();
    }

    /**
     * 拒否を忘れる（許可を見たとき。覚えが無ければ何も書かない）。
     *
     * @param context どの Context でもよい
     * @param kind    種類
     */
    static void forgetDenial(Context context, PermissionKind kind) {
        SharedPreferences preferences = preferences(context);
        if (preferences.getBoolean(key(kind), NOT_DENIED)) {
            preferences.edit().remove(key(kind)).apply();
        }
    }

    /** 置き場。 */
    private static SharedPreferences preferences(Context context) {
        return context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE);
    }

    /** 種類のキー。 */
    private static String key(PermissionKind kind) {
        return DENIED_KEY_PREFIX + kind.wireName;
    }
}
