// ============================================================
//  ShowWhenLockedCommand.java — window.set_show_when_locked（ロック画面の上に出す＋画面を点ける の切り替え。W1-4a）
//
//  引数 { on: 真偽 }。MainActivity の setShowWhenLocked(on)・setTurnScreenOn(on)（中身は window/LockScreenPresence）を UI スレッドで
//  切り替える（runOnUiThread へ投げてすぐ返る。返答 { on }。引数と Activity の扱いは WindowToggleCommand）。目覚ましの鳴動で
//  起動したときは LaunchReason が上げているので、アプリは鳴動を片付けた後にこれで下ろす（下ろさないと、アプリを開いたまま
//  電源ボタンを押してもロック画面が出ない。AC-5）。下ろし忘れても、ランチャー・最近のタスクから開き直したときは LaunchReason が
//  下ろす（W1-6 の忘れ対策）。ロックは解除しない（requestDismissKeyguard は使わない。docs/app_platform_roadmap.md §2.2 の「キーガード」）。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.window.LockScreenPresence;

/**
 * window.set_show_when_locked。
 */
final class ShowWhenLockedCommand extends WindowToggleCommand {

    @Override
    void apply(Activity activity, boolean on) {
        LockScreenPresence.apply(activity, on);
        Log.i(PlatformContract.LOG_TAG, "ロック画面の上に出す・画面を点ける: " + on);
    }
}
