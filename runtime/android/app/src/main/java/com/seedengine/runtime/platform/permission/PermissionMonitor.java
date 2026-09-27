// ============================================================
//  PermissionMonitor.java — 前面へ戻るたび（onResume）に権限の状態を前回と比べ、変わっていれば platform.permission_changed（W1-5）
//
//  【比べ方】v1 の 3 種（通知・正確なアラーム・フルスクリーン通知）のうち APK に機能が入っている種類だけを、onResume のたびに
//  PermissionStatusProbe で調べ、前回見た状態（プロセスの中の表）と違えば permission_changed { kind, status } を流す。
//  前回が無い（プロセスの最初の onResume）ときは覚えるだけ。前回は「前の onResume で見た状態」で、スクリプトの Check は表を変えない
//  （流れが決まるように、表を書き換えるのは onResume だけ）。確認の画面の結果で状態が変わったときも、画面を閉じた後の onResume で
//  permission_changed が流れる（permission_result の後。どちらを受けてもよい）。
//  機能の無いゲームでは宣言の一覧（1 回だけ読んで持つ）を見るだけで、システムのサービスへは問い合わせない。
//  【スレッド】UI スレッドだけ（表はロックを持たない）。
// ============================================================

package com.seedengine.runtime.platform.permission;

import android.app.Activity;
import android.content.Context;

import java.util.EnumMap;
import java.util.Map;

/**
 * 状態の見張り（static のみ。UI スレッドから呼ぶ）。
 */
final class PermissionMonitor {

    private PermissionMonitor() {
    }

    /** 前の onResume で見た状態（種類 → PlatformContract.PERMISSION_STATUS_*。UI スレッドだけが触る）。 */
    private static final Map<PermissionKind, String> lastSeen = new EnumMap<>(PermissionKind.class);

    /**
     * 前面へ戻った: 状態を調べて前回と比べる。
     *
     * @param activity MainActivity
     */
    static void onResume(Activity activity) {
        Context context = activity.getApplicationContext();
        for (PermissionKind kind : PermissionKind.values()) {
            if (!kind.isDeclared(context)) {
                continue;
            }
            String status = PermissionStatusProbe.status(context, activity, kind);
            String previous = lastSeen.put(kind, status);
            if (previous != null && !previous.equals(status)) {
                PermissionEvents.changed(kind, previous, status);
            }
        }
    }
}
