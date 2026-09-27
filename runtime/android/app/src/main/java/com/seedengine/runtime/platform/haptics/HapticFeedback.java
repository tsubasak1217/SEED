// ============================================================
//  HapticFeedback.java — 触感（スクリプトの Haptics.Tap / Vibrate。メインプロセス。W1-6）
//
//  【振動の中身】
//    tap     … VibrationEffect.createPredefined(EFFECT_CLICK)（API 29 = minSdk。端末が用意した「クリック」の触感）
//    vibrate … VibrationEffect.createOneShot(ms, DEFAULT_AMPLITUDE)（ms は呼び出し側で下限・上限にそろえたもの）
//  【振動の種類】（Android 13 = API 33 以降。利用者の「振動と触感」の設定がこの種類ごとに効く）
//    tap     … VibrationAttributes.USAGE_TOUCH（AOSP の説明「典型的な触感はタッチの触感。タップ・長押し・ドラッグ・スクロール」）
//    vibrate … VibrationAttributes.USAGE_MEDIA（同「音楽・動画・アニメーション・ゲームなど、タッチの触感ではない対話的なもの」）
//  Android 12L 以前（API 29〜32）は種類を付けずに vibrate(effect) で鳴らす（VibrationAttributes.createForUsage と
//  vibrate(effect, attributes) は API 33 から。api-versions.xml で確かめた）。
//  振動子は DeviceVibrator（W1-4a の RingVibration と共通）。Vibrator は Binder 越しのシステムサービスで、どのスレッドから
//  呼んでもよいので、エンジンのスレッドからそのまま呼ぶ（UI スレッドへは回さない）。権限 VIBRATE は main のマニフェストに常設。
// ============================================================

package com.seedengine.runtime.platform.haptics;

import android.content.Context;
import android.os.Build;
import android.os.VibrationAttributes;
import android.os.VibrationEffect;
import android.os.Vibrator;

import com.seedengine.runtime.platform.DeviceVibrator;

/**
 * 触感（static のみ）。
 */
public final class HapticFeedback {

    private HapticFeedback() {
    }

    /**
     * 軽いクリックの触感を鳴らす。
     *
     * @param context アプリの Context
     * @return 鳴らしたら true。振動子が無ければ false
     */
    public static boolean tap(Context context) {
        Vibrator vibrator = usableVibrator(context);
        if (vibrator == null) {
            return false;
        }
        play(vibrator, VibrationEffect.createPredefined(VibrationEffect.EFFECT_CLICK), VibrationAttributes.USAGE_TOUCH);
        return true;
    }

    /**
     * 決まった長さだけ振動する。
     *
     * @param context      アプリの Context
     * @param milliseconds 長さ（ミリ秒。呼び出し側で下限・上限にそろえておくこと）
     * @return 鳴らしたら true。振動子が無ければ false
     */
    public static boolean vibrate(Context context, long milliseconds) {
        Vibrator vibrator = usableVibrator(context);
        if (vibrator == null) {
            return false;
        }
        play(vibrator, VibrationEffect.createOneShot(milliseconds, VibrationEffect.DEFAULT_AMPLITUDE), VibrationAttributes.USAGE_MEDIA);
        return true;
    }

    /**
     * 振動できる振動子（無い・振動子の無い端末なら null）。
     *
     * @param context アプリの Context
     * @return 振動子
     */
    private static Vibrator usableVibrator(Context context) {
        Vibrator vibrator = DeviceVibrator.defaultVibrator(context);
        return vibrator != null && vibrator.hasVibrator() ? vibrator : null;
    }

    /**
     * 振動を鳴らす（API 33 以降は種類つき）。
     *
     * @param vibrator 振動子
     * @param effect   振動の中身
     * @param usage    振動の種類（VibrationAttributes.USAGE_*。API 33 以降だけ使う）
     */
    private static void play(Vibrator vibrator, VibrationEffect effect, int usage) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            vibrator.vibrate(effect, VibrationAttributes.createForUsage(usage));
        } else {
            vibrator.vibrate(effect);
        }
    }
}
