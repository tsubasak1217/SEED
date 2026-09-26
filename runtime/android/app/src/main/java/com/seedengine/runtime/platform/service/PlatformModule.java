// ============================================================
//  PlatformModule.java — :seed_platform の命令のモジュール 1 つ分の約束（W1-1。W1-3 で alarm/ から実装するため public に）
//
//  PlatformProvider は ContentProvider の call の method（"<module>.<method>"）をモジュールの名前で振り分け、
//  モジュールがメソッドの名前で処理を選ぶ。機能を増やすときは、このインターフェースを実装したモジュールを
//  PlatformProvider のモジュール表に 1 行足す（W1-3 の alarm、W1-5 の notifications など）。JNI やエンジンは変えない。
// ============================================================

package com.seedengine.runtime.platform.service;

import android.content.Context;
import android.os.Bundle;

/**
 * 命令のモジュール（:seed_platform の Binder のスレッドから同時に呼ばれうる。状態を持つなら自分で同期する）。
 */
public interface PlatformModule {

    /**
     * モジュールの名前（PlatformContract.isValidName を満たす。call の method の "." より前）。
     *
     * @return 名前
     */
    String name();

    /**
     * メソッドを 1 つ処理する（例外で失敗を知らせてもよい。PlatformProvider が internal_error の返答にする）。
     *
     * @param context :seed_platform のプロセスの Context
     * @param method  メソッドの名前（規則は確かめ済み）
     * @param request 引数（PlatformJson.parseRequest の結果。多くは JSONObject）
     * @param extras  call の extras（Binder などを JSON 以外で受け取るメソッド用。null 可）
     * @return 返答の JSON（UTF-8。知らないメソッドは unknown_method の失敗の返答。null を返さない）
     */
    byte[] handle(Context context, String method, Object request, Bundle extras);
}
