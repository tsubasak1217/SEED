// ============================================================
//  BackCallbackHost.java — 予測型の戻るのコールバックを出し入れできる Activity の口（メインプロセス。W2 の手直し P1-3）
//
//  local/BackCallbackCommand（app.set_back_callback）が、HostActivity に登録された Activity（MainActivity）がこれを実装していれば
//  使う。中身は MainActivity が持つ back/BackCallbackController（MainActivity は受け口だけ）。
//  window/SystemBarsHost（システムバーの切り替えの口）と同じ形。docs/android.md §25.18。
// ============================================================

package com.seedengine.runtime.platform.app;

/**
 * 予測型の戻るのコールバックを出し入れできる Activity。
 */
public interface BackCallbackHost {

    /**
     * 予測型の戻るが有効か（APK の印 R.bool.seed_predictive_back が true で、端末が API 33 以上）。
     * 無効なら {@link #setAppHandlesBack} を呼んでも何もしない（戻るは従来どおり KEYCODE_BACK → Escape）。
     * 命令を受けたエンジンのスレッドから呼ばれる（リソースを読むだけ）。
     *
     * @return 有効なら true
     */
    boolean isPredictiveBackEnabled();

    /**
     * アプリが戻るを受けるかを切り替える（UI スレッドで呼ぶこと）。
     *
     * @param on 受けるなら true（自分のコールバックを登録）、受ける層が無い〈根〉なら false（システムに任せて背面へ）
     */
    void setAppHandlesBack(boolean on);
}
