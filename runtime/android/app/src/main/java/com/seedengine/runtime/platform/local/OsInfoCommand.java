// ============================================================
//  OsInfoCommand.java — app.os_info（OS の種類と版。2026-10-01。W3-5 で見つかった不足）
//
//  引数なし。返答 { platform, os_version }:
//    platform   … 常に "android"（PlatformContract.APP_PLATFORM_ANDROID）
//    os_version … Build.VERSION.SDK_INT（API レベル。例 Android 13 = 33、14 = 34）
//  スクリプトの App.Platform・App.OsVersion の源（C# は最初に成功した値を持ち続ける。プロセスの中で変わらないため）。
//  権限の段の出し分け（通知の実行時の許可は 33 以上など）をスクリプトが版で決められるようにする。
//  Activity も Context も要らないので、登録の前でも答えられる。デスクトップの模擬はホストの OS と 0（環境変数で差し替え）
//  （runtime の desktop_sim/os_info.rs）。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.os.Build;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

import org.json.JSONObject;

/**
 * app.os_info。
 */
final class OsInfoCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_APP_PLATFORM, PlatformContract.APP_PLATFORM_ANDROID);
        PlatformJson.put(fields, PlatformContract.KEY_APP_OS_VERSION, Build.VERSION.SDK_INT);
        return PlatformJson.okReply(fields);
    }
}
