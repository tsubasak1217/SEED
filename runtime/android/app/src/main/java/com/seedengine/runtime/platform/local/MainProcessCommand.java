// ============================================================
//  MainProcessCommand.java — メインプロセスで答える命令 1 つの約束（IPC に行かない命令。W1-4a）
//
//  SeedPlatform.invoke は、まず MainProcessCommands の表を引き、ここに載っている命令（起動理由・画面の操作など、
//  メインプロセスの Activity・Intent の持ち物）はその場で答える。表に無い命令だけを :seed_platform へ送る。
// ============================================================

package com.seedengine.runtime.platform.local;

import org.json.JSONObject;

/**
 * メインプロセスで答える命令（呼び出し元のスレッド＝エンジンのスレッドで動く。UI の操作は Activity.runOnUiThread へ投げる）。
 */
interface MainProcessCommand {

    /**
     * 命令を 1 つ処理する（例外を投げない。失敗は {"ok":false,"error":…} の返答）。
     *
     * @param arguments 引数（オブジェクトでなければ空のオブジェクト）
     * @return 返答の JSON（UTF-8）
     */
    byte[] handle(JSONObject arguments);
}
