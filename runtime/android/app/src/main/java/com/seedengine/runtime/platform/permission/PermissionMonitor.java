// ============================================================
//  PermissionMonitor.java — 前面へ戻るたび（onResume）に権限の状態を前回と比べ、変わっていれば platform.permission_changed（W1-5）
//
//  【比べ方】v1 の 3 種（通知・正確なアラーム・フルスクリーン通知）のうち APK に機能が入っている種類だけを、onResume のたびに
//  PermissionStatusProbe で調べ、前回見た状態と違えば permission_changed { kind, status } を流す（比べるのは PermissionChangeTracker）。
//  前回が無い（その種類を初めて見る＝インストール後の最初の onResume）ときは覚えるだけ。前回は「前の onResume で見た状態」で、
//  スクリプトの Check は前回の状態を変えない（流れが決まるように、覚え直すのは onResume だけ）。確認の画面の結果で状態が変わった
//  ときも、画面を閉じた後の onResume で permission_changed が流れる（permission_result の後。どちらを受けてもよい）。
//  機能の無いゲームでは宣言の一覧（1 回だけ読んで持つ）を見るだけで、システムのサービスへは問い合わせない。
//
//  【前回の状態の置き場（M7）】メインプロセスの SharedPreferences（PermissionStatusMemory）。利用者が設定で通知をオフにすると
//  Android はアプリのプロセスを止めるが、置き場がディスクにあるので、起動し直した最初の onResume でも前回と比べて
//  granted → denied を流せる（以前はプロセスの中の表だけで、起動し直すと比べる相手が無く届かなかった）。
//  【スレッド】UI スレッドだけ。
// ============================================================

package com.seedengine.runtime.platform.permission;

import android.app.Activity;
import android.content.Context;

/**
 * 状態の見張り（static のみ。UI スレッドから呼ぶ）。
 */
final class PermissionMonitor {

    private PermissionMonitor() {
    }

    /** 前回と比べる係（最初の onResume で置き場を開いて作る。UI スレッドだけが触る）。 */
    private static PermissionChangeTracker tracker;

    /**
     * 前面へ戻った: 状態を調べて前回と比べる。
     *
     * @param activity MainActivity
     */
    static void onResume(Activity activity) {
        Context context = activity.getApplicationContext();
        PermissionChangeTracker changes = tracker(context);
        for (PermissionKind kind : PermissionKind.values()) {
            if (!kind.isDeclared(context)) {
                continue;
            }
            String status = PermissionStatusProbe.status(context, activity, kind);
            String previous = changes.observe(kind.wireName, status);
            if (previous != null) {
                PermissionEvents.changed(kind, previous, status);
            }
        }
    }

    /** 前回と比べる係（無ければ置き場を開いて作る）。 */
    private static PermissionChangeTracker tracker(Context context) {
        if (tracker == null) {
            tracker = new PermissionChangeTracker(new PermissionStatusMemory(context));
        }
        return tracker;
    }
}
