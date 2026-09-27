// ============================================================
//  MainProcessCommands.java — メインプロセスで答える命令の表（IPC に行かない命令。W1-4a）
//
//  SeedPlatform.invoke がまずここを引き、載っている命令はその場で答える（:seed_platform を起こさない・待たない）。
//  メインプロセスの持ち物（起動の Intent・Activity の窓）を扱う命令だけを置く:
//    platform.launch_reason       … この起動の理由（LaunchReasonCommand）
//    window.set_show_when_locked  … ロック画面の上に出す＋画面を点ける の切り替え（ShowWhenLockedCommand）
//  命令を足すときは、この表に 1 行と、Rust の wire.rs・デスクトップの模擬（desktop_sim の SIM_COMMANDS）に同じ名前を足す。
//  "platform" モジュールの他の命令（ping など）は :seed_platform の CorePlatformModule が答える（表は module と method の組で引く）。
// ============================================================

package com.seedengine.runtime.platform.local;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

import org.json.JSONException;

import java.util.Collections;
import java.util.HashMap;
import java.util.Map;

/**
 * メインプロセスで答える命令の表（static のみ）。
 */
public final class MainProcessCommands {

    private MainProcessCommands() {
    }

    /** "<module>.<method>" → 命令（作った後は変えない。どのスレッドから読んでもよい）。 */
    private static final Map<String, MainProcessCommand> TABLE = buildTable();

    /**
     * 表を作る（命令を足すときはここへ 1 行）。
     *
     * @return 変えられない表
     */
    private static Map<String, MainProcessCommand> buildTable() {
        Map<String, MainProcessCommand> table = new HashMap<>();
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_PLATFORM, PlatformContract.METHOD_LAUNCH_REASON),
                new LaunchReasonCommand());
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_WINDOW, PlatformContract.METHOD_WINDOW_SET_SHOW_WHEN_LOCKED),
                new ShowWhenLockedCommand());
        return Collections.unmodifiableMap(table);
    }

    /**
     * メインプロセスで答える命令なら答える（例外を投げない）。
     *
     * @param module   モジュールの名前
     * @param method   メソッドの名前
     * @param jsonUtf8 引数の JSON（UTF-8。null は {}）
     * @return 返答の JSON（UTF-8）。表に無い命令なら null（呼び出し側が :seed_platform へ送る）
     */
    public static byte[] tryHandle(String module, String method, byte[] jsonUtf8) {
        MainProcessCommand command = TABLE.get(PlatformContract.providerMethod(module, method));
        if (command == null) {
            return null;
        }
        Object request;
        try {
            request = PlatformJson.parseRequest(jsonUtf8);
        } catch (JSONException e) {
            return PlatformJson.errorReply(PlatformContract.ERROR_INVALID_JSON, PlatformJson.describe(e));
        }
        return command.handle(PlatformJson.asObject(request));
    }
}
