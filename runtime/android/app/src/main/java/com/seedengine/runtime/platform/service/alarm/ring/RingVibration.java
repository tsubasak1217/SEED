// ============================================================
//  RingVibration.java — 鳴動の振動（繰り返しのパターン。予約の vibrate が true のとき。:seed_platform。W1-4a）
//
//  端末の既定の振動子は platform/DeviceVibrator（Android 12〈API 31〉以降は VibratorManager.getDefaultVibrator、それより前は
//  getSystemService(Vibrator.class)。W1-6 でメインプロセスの触感と共通化）で取る。振動の種類はアラーム（API 33 以降は VibrationAttributes.USAGE_ALARM、
//  それより前は AudioAttributes の USAGE_ALARM。どちらもおやすみモード等でアラームとして扱われる）。
//  パターンは「振動 VIBRATE_ON_MS → 休み VIBRATE_OFF_MS」の繰り返し（止めるまで続ける）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.content.Context;
import android.media.AudioAttributes;
import android.os.Build;
import android.os.VibrationAttributes;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.util.Log;

import com.seedengine.runtime.platform.DeviceVibrator;
import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

/**
 * 鳴動の振動（UI スレッドから使う）。
 */
final class RingVibration {

    /** 振動を始めるまでの待ち（ミリ秒）。 */
    private static final long VIBRATE_DELAY_MS = 0L;

    /** 1 回の振動の長さ（ミリ秒）。 */
    private static final long VIBRATE_ON_MS = 800L;

    /** 振動の間の休み（ミリ秒）。 */
    private static final long VIBRATE_OFF_MS = 600L;

    /** 波形（待ち・振動・休み。createWaveform の「偶数番目が止め・奇数番目が振動」の並び）。 */
    private static final long[] PATTERN_MS = {VIBRATE_DELAY_MS, VIBRATE_ON_MS, VIBRATE_OFF_MS};

    /** 繰り返しの始まり（PATTERN_MS の「振動」から。待ちは最初の 1 回だけ）。 */
    private static final int REPEAT_FROM_INDEX = 1;

    /** 端末の既定の振動子（無ければ null）。 */
    private final Vibrator vibrator;

    /**
     * @param context どの Context でもよい
     */
    RingVibration(Context context) {
        this.vibrator = DeviceVibrator.defaultVibrator(context.getApplicationContext());
    }

    /**
     * 振動を始める（振動子が無ければ何もしない）。
     */
    void start() {
        if (vibrator == null || !vibrator.hasVibrator()) {
            Log.i(PlatformContract.LOG_TAG, "振動子が無いので鳴動の振動はしません");
            return;
        }
        VibrationEffect effect = VibrationEffect.createWaveform(PATTERN_MS, REPEAT_FROM_INDEX);
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                vibrator.vibrate(effect, VibrationAttributes.createForUsage(VibrationAttributes.USAGE_ALARM));
            } else {
                vibrateLegacy(effect);
            }
        } catch (RuntimeException e) {
            // 権限（VIBRATE）の無い APK など。振動が無くても音と通知は続ける
            Log.w(PlatformContract.LOG_TAG, "振動を始められませんでした: " + PlatformJson.describe(e));
        }
    }

    /**
     * 振動を止める（始めていなくてもよい）。
     */
    void stop() {
        if (vibrator != null) {
            vibrator.cancel();
        }
    }

    /**
     * Android 12L 以前の振動（AudioAttributes で種類を伝える形。API 33 で非推奨になったが、それより前の版では唯一の形）。
     *
     * @param effect 波形
     */
    @SuppressWarnings("deprecation")
    private void vibrateLegacy(VibrationEffect effect) {
        AudioAttributes attributes = new AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_ALARM)
                .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                .build();
        vibrator.vibrate(effect, attributes);
    }
}
