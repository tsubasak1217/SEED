package com.seedengine.platformspike;

import android.app.KeyguardManager;
import android.app.NotificationManager;
import android.content.Context;
import android.os.Build;
import android.os.PowerManager;
import android.os.UserManager;

/** 端末の状態（画面・ロック・Doze・ロック解除前か）を 1 行にまとめる。目印のログに添えて証拠にする。 */
final class DeviceState {
    private DeviceState() {}

    static String describe(Context context) {
        PowerManager power = context.getSystemService(PowerManager.class);
        KeyguardManager keyguard = context.getSystemService(KeyguardManager.class);
        UserManager users = context.getSystemService(UserManager.class);
        String lightIdle = Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU
                ? String.valueOf(power.isDeviceLightIdleMode()) : "n/a";
        return "interactive=" + power.isInteractive()
                + " keyguard_locked=" + keyguard.isKeyguardLocked()
                + " device_locked=" + keyguard.isDeviceLocked()
                + " device_idle=" + power.isDeviceIdleMode()
                + " light_idle=" + lightIdle
                + " power_save=" + power.isPowerSaveMode()
                + " user_unlocked=" + users.isUserUnlocked();
    }

    /** 権限・特別なアクセスの状態（API 36 で何が要るかの確認）。 */
    static String permissions(Context context) {
        android.app.AlarmManager alarms = context.getSystemService(android.app.AlarmManager.class);
        NotificationManager notifications = context.getSystemService(NotificationManager.class);
        String fsi = Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE
                ? String.valueOf(notifications.canUseFullScreenIntent()) : "n/a";
        return "can_schedule_exact=" + alarms.canScheduleExactAlarms()
                + " can_use_full_screen_intent=" + fsi
                + " notifications_enabled=" + notifications.areNotificationsEnabled()
                + " sdk=" + Build.VERSION.SDK_INT;
    }
}
