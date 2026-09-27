// ============================================================
//  PermissionKind.java — 権限の種類（wire の名前・扱うか・マニフェストの権限・機能の名前。メインプロセス。W1-5）
//
//  【機能の有無】種類ごとに「どの <uses-permission> があれば機能が入っているか」を持つ（platform/DeclaredPermissions で調べる）。
//  宣言が無い APK で状態を問い合わせても意味が無い（Android 13+ で宣言の無い権限を求めると、確認の画面を出さずに拒否が返る）ので、
//  権限の命令は feature_not_enabled で断り、どの機能を android.features に足せばよいかを説明に入れる。
//    post_notifications … POST_NOTIFICATIONS（機能 notifications。alarm も入れる）
//    exact_alarm        … USE_EXACT_ALARM か SCHEDULE_EXACT_ALARM（機能 alarm。SCHEDULE_EXACT_ALARM は maxSdkVersion 32 なので
//                          Android 13+ の端末では宣言の一覧から落ちる。どちらか 1 つあればよい）
//    full_screen_intent … USE_FULL_SCREEN_INTENT（機能 alarm）
//    record_audio / send_sms … v2 の予約（扱わない。状態は常に not_applicable）
//  Manifest.permission の定数はコンパイル時に文字列が埋め込まれるので、定数の API レベル（POST_NOTIFICATIONS は 33）より古い
//  端末でも読める（名前として比べるだけ）。
// ============================================================

package com.seedengine.runtime.platform.permission;

import android.Manifest;
import android.content.Context;

import com.seedengine.runtime.platform.DeclaredPermissions;
import com.seedengine.runtime.platform.PlatformContract;

/**
 * 権限の種類。
 */
public enum PermissionKind {

    /** 通知。 */
    POST_NOTIFICATIONS(PlatformContract.PERMISSION_KIND_POST_NOTIFICATIONS, true, Features.NOTIFICATIONS,
            Manifest.permission.POST_NOTIFICATIONS),

    /** 正確なアラーム。 */
    EXACT_ALARM(PlatformContract.PERMISSION_KIND_EXACT_ALARM, true, Features.ALARM,
            Manifest.permission.USE_EXACT_ALARM, Manifest.permission.SCHEDULE_EXACT_ALARM),

    /** フルスクリーン通知。 */
    FULL_SCREEN_INTENT(PlatformContract.PERMISSION_KIND_FULL_SCREEN_INTENT, true, Features.ALARM,
            Manifest.permission.USE_FULL_SCREEN_INTENT),

    /** 録音（v2 の予約）。 */
    RECORD_AUDIO(PlatformContract.PERMISSION_KIND_RECORD_AUDIO, false, Features.NONE),

    /** SMS の送信（v2 の予約）。 */
    SEND_SMS(PlatformContract.PERMISSION_KIND_SEND_SMS, false, Features.NONE);

    /** android.features の機能の名前（runtime/android/platform_features.json の name。案内の文言に使う）。 */
    private static final class Features {
        /** 通知。 */
        static final String NOTIFICATIONS = "notifications";
        /** 目覚まし。 */
        static final String ALARM = "alarm";
        /** 機能なし（v2 の予約）。 */
        static final String NONE = "";
    }

    /** wire の名前（PlatformContract.PERMISSION_KIND_*）。 */
    public final String wireName;

    /** この段階（v1）で扱う種類か（false は状態が常に not_applicable）。 */
    public final boolean implemented;

    /** 足すべき android.features の機能の名前（扱わない種類は空）。 */
    public final String featureName;

    /** どれか 1 つの宣言があれば機能が入っている、とみなす権限。 */
    private final String[] manifestPermissions;

    PermissionKind(String wireName, boolean implemented, String featureName, String... manifestPermissions) {
        this.wireName = wireName;
        this.implemented = implemented;
        this.featureName = featureName;
        this.manifestPermissions = manifestPermissions;
    }

    /**
     * wire の名前から引く。
     *
     * @param name wire の名前（null 可）
     * @return 種類（知らない名前なら null）
     */
    public static PermissionKind fromWire(String name) {
        for (PermissionKind kind : values()) {
            if (kind.wireName.equals(name)) {
                return kind;
            }
        }
        return null;
    }

    /**
     * APK にこの種類の機能が入っているか（扱う種類で、権限のどれかが宣言されている）。
     *
     * @param context どの Context でもよい
     * @return 入っていれば true（扱わない種類は false）
     */
    public boolean isDeclared(Context context) {
        if (!implemented) {
            return false;
        }
        for (String permission : manifestPermissions) {
            if (DeclaredPermissions.isDeclared(context, permission)) {
                return true;
            }
        }
        return false;
    }
}
