// ============================================================
//  LockScreenPresence.java — ロック画面の上に出す＋画面を点ける の切り替え（メインプロセス・UI スレッド。W1-6 で共通化）
//
//  Activity.setShowWhenLocked（ロック画面の上に出す）と setTurnScreenOn（出るときに画面を点ける）を必ず対で切り替える
//  （API 27 からの API。minSdk 29 なので版の分岐は要らない）。ロックは解除しない（requestDismissKeyguard は使わない。
//  docs/app_platform_roadmap.md §2.2 の「キーガード」）。マニフェストで静的に宣言しないのは、鳴っていないときにロック画面の上に
//  出てしまうため（アプリ仕様 §6.12 の教訓）。
//
//  使う所:
//    LaunchReason                      … 目覚ましの鳴動の起動（alarm）で上げ、ランチャー・最近のタスクからの開き直しで下ろす（忘れ対策）
//    local/ShowWhenLockedCommand       … スクリプトの Window.SetShowWhenLocked（window.set_show_when_locked）
// ============================================================

package com.seedengine.runtime.platform.window;

import android.app.Activity;

/**
 * ロック画面の上に出す＋画面を点ける の切り替え（static のみ。UI スレッドから呼ぶ）。
 */
public final class LockScreenPresence {

    private LockScreenPresence() {
    }

    /**
     * ロック画面の上に出す＋画面を点ける を切り替える（UI スレッドで呼ぶこと）。
     *
     * @param activity 対象の Activity（MainActivity）
     * @param on       上げるなら true、下ろすなら false
     */
    public static void apply(Activity activity, boolean on) {
        activity.setShowWhenLocked(on);
        activity.setTurnScreenOn(on);
    }
}
