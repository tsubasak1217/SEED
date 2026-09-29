// ============================================================
//  MainProcessCommands.java — メインプロセスで答える命令の表（IPC に行かない命令。W1-4a・W1-5 で権限・W1-6 で画面・アプリ・触感・
//  W1-8 でセンサーを追加）
//
//  SeedPlatform.invoke がまずここを引き、載っている命令はその場で答える（:seed_platform を起こさない・待たない）。
//  メインプロセスの持ち物（起動の Intent・Activity の窓とタスク・実行時の許可の確認の画面・振動子・センサー）を扱う命令だけを置く:
//    platform.launch_reason          … この起動の理由（LaunchReasonCommand）
//    window.set_show_when_locked     … ロック画面の上に出す＋画面を点ける の切り替え（ShowWhenLockedCommand）
//    window.set_keep_screen_on       … 画面を点けたままにする の切り替え（KeepScreenOnCommand。W1-6）
//    window.set_system_bars_visible  … システムバーを出す・隠す の切り替え（SystemBarsVisibleCommand。W1-6）
//    app.move_task_to_back           … 閉じずに背面へ（MoveTaskToBackCommand。W1-6）
//    app.open_url                    … URL を端末のアプリで開く（OpenUrlCommand。W1-6）
//    app.open_app_settings           … 端末の「アプリ情報」の画面を開く（OpenAppSettingsCommand。W1-6）
//    app.ui_mode                     … 端末の明暗の設定（UiModeCommand。W2-9。変化は MainActivity から platform.ui_mode_changed）
//    app.set_back_callback           … 予測型の戻るの自分のコールバックの出し入れ（BackCallbackCommand。W2 の手直し P1-3。
//                                      手ぶりは MainActivity の back/ から platform.back_*）
//    haptics.tap / haptics.vibrate   … 触感（HapticsTapCommand・HapticsVibrateCommand。W1-6）
//    permission.check                … 権限の今の状態（PermissionCheckCommand。W1-5）
//    permission.request              … 権限を求める（PermissionRequestCommand。結果は platform.permission_result。W1-5）
//    permission.open_settings        … 権限の設定の画面を開く（PermissionOpenSettingsCommand。W1-5）
//    sensor.start / stop / read      … センサーの受け取り（SensorStartCommand・SensorStopCommand・SensorReadCommand。W1-8）
//  命令を足すときは、この表に 1 行と、Rust の wire.rs・デスクトップの模擬（desktop_sim の SIM_COMMANDS）に同じ名前を足す。
//  "platform" モジュールの他の命令（ping など）は :seed_platform の CorePlatformModule が答える（表は module と method の組で引く）。
//  【メインプロセスだけのモジュール】（LOCAL_MODULES。W1-8 の sensor から）表に無い命令も :seed_platform へ送らず unknown_method で
//  答える（模擬だけの sensor.sim_inject を実機で呼んでも :seed_platform を起こさず、connecting にもならない）。W1-6 までの
//  window・app・haptics・permission は従来どおり（表に無い命令は :seed_platform へ送られ、そこで unknown_method）。
// ============================================================

package com.seedengine.runtime.platform.local;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

import org.json.JSONException;

import java.util.Collections;
import java.util.HashMap;
import java.util.Map;
import java.util.Set;

/**
 * メインプロセスで答える命令の表（static のみ）。
 */
public final class MainProcessCommands {

    private MainProcessCommands() {
    }

    /** "<module>.<method>" → 命令（作った後は変えない。どのスレッドから読んでもよい）。 */
    private static final Map<String, MainProcessCommand> TABLE = buildTable();

    /** メインプロセスだけが持つモジュール（表に無い命令も :seed_platform へ送らず unknown_method。W1-8）。 */
    private static final Set<String> LOCAL_MODULES = Collections.singleton(PlatformContract.MODULE_SENSOR);

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
        // W1-6: 画面の切り替え・アプリ・触感
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_WINDOW, PlatformContract.METHOD_WINDOW_SET_KEEP_SCREEN_ON),
                new KeepScreenOnCommand());
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_WINDOW, PlatformContract.METHOD_WINDOW_SET_SYSTEM_BARS_VISIBLE),
                new SystemBarsVisibleCommand());
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_APP, PlatformContract.METHOD_APP_MOVE_TASK_TO_BACK),
                new MoveTaskToBackCommand());
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_APP, PlatformContract.METHOD_APP_OPEN_URL),
                new OpenUrlCommand());
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_APP, PlatformContract.METHOD_APP_OPEN_APP_SETTINGS),
                new OpenAppSettingsCommand());
        // W2-9: 端末の明暗の設定
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_APP, PlatformContract.METHOD_APP_UI_MODE),
                new UiModeCommand());
        // W2 の手直し P1-3: 予測型の戻るの自分のコールバックの出し入れ
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_APP, PlatformContract.METHOD_APP_SET_BACK_CALLBACK),
                new BackCallbackCommand());
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_HAPTICS, PlatformContract.METHOD_HAPTICS_TAP),
                new HapticsTapCommand());
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_HAPTICS, PlatformContract.METHOD_HAPTICS_VIBRATE),
                new HapticsVibrateCommand());
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_PERMISSION, PlatformContract.METHOD_PERMISSION_CHECK),
                new PermissionCheckCommand());
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_PERMISSION, PlatformContract.METHOD_PERMISSION_REQUEST),
                new PermissionRequestCommand());
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_PERMISSION, PlatformContract.METHOD_PERMISSION_OPEN_SETTINGS),
                new PermissionOpenSettingsCommand());
        // W1-8: センサー
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_SENSOR, PlatformContract.METHOD_SENSOR_START),
                new SensorStartCommand());
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_SENSOR, PlatformContract.METHOD_SENSOR_STOP),
                new SensorStopCommand());
        table.put(PlatformContract.providerMethod(PlatformContract.MODULE_SENSOR, PlatformContract.METHOD_SENSOR_READ),
                new SensorReadCommand());
        return Collections.unmodifiableMap(table);
    }

    /**
     * メインプロセスで答える命令なら答える（例外を投げない）。
     *
     * @param module   モジュールの名前
     * @param method   メソッドの名前
     * @param jsonUtf8 引数の JSON（UTF-8。null は {}）
     * @return 返答の JSON（UTF-8）。表に無い命令なら null（呼び出し側が :seed_platform へ送る）。ただしメインプロセスだけの
     *         モジュール（LOCAL_MODULES）の表に無い命令は unknown_method の返答
     */
    public static byte[] tryHandle(String module, String method, byte[] jsonUtf8) {
        MainProcessCommand command = TABLE.get(PlatformContract.providerMethod(module, method));
        if (command == null) {
            // メインプロセスだけのモジュールの知らない命令（模擬だけの sensor.sim_inject など）は :seed_platform を起こさずに断る
            return LOCAL_MODULES.contains(module) ? PlatformJson.errorReply(PlatformContract.ERROR_UNKNOWN_METHOD) : null;
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
