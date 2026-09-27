// ============================================================
//  SystemBarsVisibleCommand.java — window.set_system_bars_visible（システムバーを出すか隠すかの切り替え。W1-6）
//
//  引数 { on: 真偽 }。HostActivity の Activity（MainActivity）が window/SystemBarsHost なら、UI スレッドでその
//  setSystemBarsVisible(on) を呼ぶ（返答 { on }。引数と Activity の扱いは WindowToggleCommand）。中身は MainActivity の
//  SystemBarsController（起動時の既定＝プロジェクト設定 android.system_bars を、ここからの切り替えで上書きする。フォーカスが
//  戻ったときの隠し直しも今の状態に従う）。安全領域（スクリプトの Screen.SafeArea）は WindowInsets の変化で ScreenReporter が
//  知らせ直すので、バーの分が出入りする（docs/android.md §25.15）。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.window.SystemBarsHost;

/**
 * window.set_system_bars_visible。
 */
final class SystemBarsVisibleCommand extends WindowToggleCommand {

    @Override
    void apply(Activity activity, boolean on) {
        if (activity instanceof SystemBarsHost) {
            ((SystemBarsHost) activity).setSystemBarsVisible(on);
        } else {
            // HostActivity に登録されるのは MainActivity だけなので起きない見込み（起きたら取り違えの印としてログに残す）
            Log.w(PlatformContract.LOG_TAG, "システムバーを切り替えられる Activity ではありません: " + activity.getClass().getName());
        }
    }
}
