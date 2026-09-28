// ============================================================
//  NightMode.java — 端末の明暗の設定（夜の表示）の読み取りと、変化のイベント（メインプロセス。W2-9）
//
//  Configuration.uiMode の UI_MODE_NIGHT_MASK（API 8）を wire の語（yes / no / unknown）にする。
//  使う所:
//    local/UiModeCommand            … スクリプトの App.UiMode（app.ui_mode）
//    MainActivity.onConfigurationChanged … 夜の bit が前と変わったら platform.ui_mode_changed を流す
//      （マニフェストの configChanges に uiMode があるので Activity は作り直されず、ここが呼ばれる）
//  SEED.UI のテーマの「端末に従う」（UiBrightnessMode.System）がこの値とイベントで明暗を切り替える。
// ============================================================

package com.seedengine.runtime.platform.app;

import android.content.res.Configuration;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.SeedPlatform;

import org.json.JSONObject;

/**
 * 端末の明暗の設定（static のみ。値の覚えは UI スレッドの onCreate・onConfigurationChanged だけが書く）。
 */
public final class NightMode {

    private NightMode() {
    }

    /** 最後に見た値（wire の語。まだ見ていなければ null）。 */
    private static volatile String lastSeen;

    /**
     * 構成の夜の bit を wire の語にする。
     *
     * @param configuration 構成（null なら unknown）
     * @return yes / no / unknown
     */
    public static String of(Configuration configuration) {
        if (configuration == null) {
            return PlatformContract.APP_NIGHT_UNKNOWN;
        }
        switch (configuration.uiMode & Configuration.UI_MODE_NIGHT_MASK) {
            case Configuration.UI_MODE_NIGHT_YES:
                return PlatformContract.APP_NIGHT_YES;
            case Configuration.UI_MODE_NIGHT_NO:
                return PlatformContract.APP_NIGHT_NO;
            default:
                return PlatformContract.APP_NIGHT_UNKNOWN;
        }
    }

    /**
     * 起動時の値を覚える（MainActivity.onCreate から。最初の onConfigurationChanged で比べる相手）。
     *
     * @param configuration 今の構成
     */
    public static void remember(Configuration configuration) {
        lastSeen = of(configuration);
    }

    /**
     * 構成が変わった（MainActivity.onConfigurationChanged から。UI スレッド）。夜の bit が前と違えばイベントを流す。
     *
     * @param configuration 新しい構成
     */
    public static void onConfigurationChanged(Configuration configuration) {
        String now = of(configuration);
        if (now.equals(lastSeen)) {
            return;
        }
        String previous = lastSeen;
        lastSeen = now;
        JSONObject data = new JSONObject();
        PlatformJson.put(data, PlatformContract.KEY_APP_NIGHT, now);
        SeedPlatform.emitLocalEvent(PlatformContract.EVENT_UI_MODE_CHANGED, data);
        Log.i(PlatformContract.LOG_TAG, "端末の明暗の設定が変わりました: " + previous + " → " + now);
    }
}
