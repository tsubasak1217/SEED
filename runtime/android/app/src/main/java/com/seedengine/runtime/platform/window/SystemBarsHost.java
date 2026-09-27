// ============================================================
//  SystemBarsHost.java — システムバーを実行中に切り替えられる Activity の約束（メインプロセス。W1-6）
//
//  スクリプトの Window.SetSystemBarsVisible（window.set_system_bars_visible）を受けた local/SystemBarsVisibleCommand が、
//  HostActivity に登録された Activity（MainActivity）へ UI スレッドで渡す。システムバーの出し方の中身
//  （既定の出し方・FLAG_FULLSCREEN の出し入れ・フォーカスが戻ったときの隠し直し）は com.seedengine.runtime.SystemBarsController
//  （MainActivity と同じパッケージの非公開クラス）にあり、platform パッケージからはこの約束だけを通して触る。
// ============================================================

package com.seedengine.runtime.platform.window;

/**
 * システムバーを実行中に切り替えられる Activity（MainActivity が実装する）。
 */
public interface SystemBarsHost {

    /**
     * システムバー（ステータスバー・ナビゲーションバー）を出すか隠すかを切り替え、以後の既定にする
     * （フォーカスが戻ったときの隠し直しもこの状態に従う）。UI スレッドで呼ぶこと。
     *
     * @param visible 出すなら true、隠すなら false
     */
    void setSystemBarsVisible(boolean visible);
}
