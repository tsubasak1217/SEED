// ============================================================
//  DeviceVibrator.java — 端末の既定の振動子を取る（両プロセス共通。W1-6 で RingVibration から共通化）
//
//  Android 12（API 31）以降は VibratorManager.getDefaultVibrator、それより前は getSystemService(Vibrator.class)。
//  使う所:
//    service/alarm/ring/RingVibration（:seed_platform）… 目覚ましの鳴動の繰り返しの振動（W1-4a）
//    haptics/HapticFeedback（メインプロセス）          … スクリプトの Haptics.Tap / Vibrate（W1-6）
//  振動には権限 VIBRATE が要る（normal 権限。W1-6 から main の AndroidManifest.xml に常設）。
//  ネイティブライブラリにも Activity にも依存しない（:seed_platform は libSEED.so を読み込まない）。
// ============================================================

package com.seedengine.runtime.platform;

import android.content.Context;
import android.os.Build;
import android.os.Vibrator;
import android.os.VibratorManager;

/**
 * 端末の既定の振動子（static のみ。どのスレッドから呼んでもよい）。
 */
public final class DeviceVibrator {

    private DeviceVibrator() {
    }

    /**
     * 端末の既定の振動子を取る。
     *
     * @param context どの Context でもよい（アプリの Context を渡すこと）
     * @return 振動子（取れなければ null。振動子の無い端末でも null でないことがあるので hasVibrator で確かめる）
     */
    public static Vibrator defaultVibrator(Context context) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            VibratorManager manager = context.getSystemService(VibratorManager.class);
            return manager != null ? manager.getDefaultVibrator() : null;
        }
        return context.getSystemService(Vibrator.class);
    }
}
