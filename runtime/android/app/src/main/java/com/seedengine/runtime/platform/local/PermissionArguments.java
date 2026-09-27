// ============================================================
//  PermissionArguments.java — 権限の命令（permission.check / request / open_settings）の引数 { kind } を読み、機能の有無を確かめる（W1-5）
//
//  【規則】（Rust のデスクトップの模擬 runtime/src/engine/platform/bridge/permission/mod.rs の read_kind と同じ）
//    kind … 必須。post_notifications / exact_alarm / full_screen_intent / record_audio / send_sms のどれか（それ以外は invalid_argument）
//  v1 で扱う種類（record_audio・send_sms 以外）は、APK にその機能の権限の宣言が無ければ feature_not_enabled
//  （説明に足すべき android.features の名前。permission/PermissionKind）。v2 の予約の種類は機能を問わず通す（状態は not_applicable）。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.content.Context;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.permission.PermissionKind;

import org.json.JSONObject;

/**
 * 引数の読み取り（static のみ）。
 */
final class PermissionArguments {

    private PermissionArguments() {
    }

    /** 読み取りの結果（種類か、失敗の返答のどちらか一方）。 */
    static final class Parsed {
        /** 読めた種類（失敗なら null）。 */
        final PermissionKind kind;
        /** 失敗の返答（成功なら null）。 */
        final byte[] errorReply;

        private Parsed(PermissionKind kind, byte[] errorReply) {
            this.kind = kind;
            this.errorReply = errorReply;
        }
    }

    /**
     * 引数を読み、機能の有無を確かめる。
     *
     * @param arguments 引数
     * @param context   アプリの Context（宣言の一覧を読む）
     * @return 結果
     */
    static Parsed read(JSONObject arguments, Context context) {
        Object value = arguments.opt(PlatformContract.KEY_PERMISSION_KIND);
        if (!(value instanceof String)) {
            return failed(PlatformContract.ERROR_INVALID_ARGUMENT, PlatformContract.KEY_PERMISSION_KIND + " は文字列にしてください");
        }
        PermissionKind kind = PermissionKind.fromWire((String) value);
        if (kind == null) {
            return failed(PlatformContract.ERROR_INVALID_ARGUMENT,
                    PlatformContract.KEY_PERMISSION_KIND + " が約束の種類ではありません（" + value + "）");
        }
        if (kind.implemented && !kind.isDeclared(context)) {
            return failed(PlatformContract.ERROR_FEATURE_NOT_ENABLED,
                    "project_settings.json の android.features に \"" + kind.featureName + "\" がありません（" + kind.wireName + " の権限の宣言が無い）");
        }
        return new Parsed(kind, null);
    }

    /** 失敗の結果。 */
    private static Parsed failed(String reason, String detail) {
        return new Parsed(null, PlatformJson.errorReply(reason, detail));
    }
}
