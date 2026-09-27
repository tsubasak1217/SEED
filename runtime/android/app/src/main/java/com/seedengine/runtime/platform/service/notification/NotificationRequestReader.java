// ============================================================
//  NotificationRequestReader.java — notification.ensure_channel / show / cancel の引数の JSON を読み、検査する（:seed_platform。W1-5）
//
//  【規則】（Rust のデスクトップの模擬 runtime/src/engine/platform/bridge/notification/request.rs と同じ。変えるときは両方）
//    ensure_channel:
//      channel_id  … 必須。1〜MAX_NOTIFICATION_ID_LENGTH 文字（Unicode の符号位置）。RESERVED_NOTIFICATION_CHANNEL_PREFIX で
//                    始まる ID は不可（鳴動の通知チャネル seed_platform_alarm などプラットフォーム層のもの）
//      name        … 必須。1〜MAX_NOTIFICATION_TEXT_LENGTH 文字
//      importance  … 任意。"low" / "default" / "high"（無い・null は "default"。それ以外は誤り）
//      description … 任意の文字列（MAX_NOTIFICATION_TEXT_LENGTH 文字まで）
//    show:
//      id・channel_id … 必須（channel_id は上と同じ規則）
//      title / body   … 任意の文字列（MAX_NOTIFICATION_TEXT_LENGTH 文字まで）
//      ongoing        … 任意の真偽（既定 false）
//      category       … 任意の文字列（MAX_NOTIFICATION_ID_LENGTH 文字まで。知らない値は誤りにせず付けない。NotificationCategories）
//      actions        … 任意の配列（null は空）。MAX_NOTIFICATION_ACTIONS 個まで。各要素 { id: 1〜128 文字, label: 1〜4096 文字 }。
//                       同じ id の操作が 2 つあると押されたほうを起動理由で見分けられないので誤り
//      payload_json   … 任意の文字列（MAX_NOTIFICATION_PAYLOAD_LENGTH 文字まで。中身は検査しない）
//    cancel: id … 必須（show の id と同じ規則）
//  欄があるのに型が違う・長すぎる・数が多すぎるときは invalid_argument（detail に欄の名前）。
// ============================================================

package com.seedengine.runtime.platform.service.notification;

import com.seedengine.runtime.platform.PlatformContract;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * 引数の読み取り（static のみ）。
 */
final class NotificationRequestReader {

    private NotificationRequestReader() {
    }

    /** 空の文字列の欄の既定値。 */
    private static final String EMPTY = "";

    /** 空を許す文字列の欄の下限（0 文字）。 */
    private static final int ALLOW_EMPTY = 0;

    /** 空を許さない文字列の欄の下限（1 文字）。 */
    private static final int NON_EMPTY = 1;

    /** 重要度の語彙（importance に書ける値）。 */
    private static final List<String> IMPORTANCES = Arrays.asList(
            PlatformContract.NOTIFICATION_IMPORTANCE_LOW,
            PlatformContract.NOTIFICATION_IMPORTANCE_DEFAULT,
            PlatformContract.NOTIFICATION_IMPORTANCE_HIGH);

    /** 読み取りの結果（読めた値か、失敗の理由と説明のどちらか一方）。 */
    static final class Result<T> {
        /** 読めた値（失敗なら null）。 */
        final T value;
        /** 失敗の理由（PlatformContract.ERROR_*。成功なら null）。 */
        final String error;
        /** 失敗の説明（返答の detail）。 */
        final String detail;

        private Result(T value, String error, String detail) {
            this.value = value;
            this.error = error;
            this.detail = detail;
        }

        /** @return 読めたら true */
        boolean ok() {
            return error == null;
        }

        static <T> Result<T> of(T value) {
            return new Result<>(value, null, null);
        }

        static <T> Result<T> invalid(String detail) {
            return new Result<>(null, PlatformContract.ERROR_INVALID_ARGUMENT, detail);
        }
    }

    /** 欄の誤り（読み取りの途中で投げ、各 read が invalid_argument の結果にする）。 */
    private static final class FieldException extends Exception {
        /** 直列化の版（直列化はしないが、Exception の約束として持つ）。 */
        private static final long serialVersionUID = 1L;

        FieldException(String detail) {
            super(detail);
        }
    }

    /**
     * notification.ensure_channel の引数を読む。
     *
     * @param request 引数
     * @return 結果
     */
    static Result<NotificationChannelSpec> readChannel(JSONObject request) {
        try {
            String id = channelId(request);
            String name = text(request, PlatformContract.KEY_NOTIFICATION_CHANNEL_NAME, NON_EMPTY,
                    PlatformContract.MAX_NOTIFICATION_TEXT_LENGTH);
            String importance = importance(request);
            String description = text(request, PlatformContract.KEY_NOTIFICATION_DESCRIPTION, ALLOW_EMPTY,
                    PlatformContract.MAX_NOTIFICATION_TEXT_LENGTH);
            return Result.of(new NotificationChannelSpec(id, name, importance, description));
        } catch (FieldException e) {
            return Result.invalid(e.getMessage());
        }
    }

    /**
     * notification.show の引数を読む。
     *
     * @param request 引数
     * @return 結果
     */
    static Result<NotificationContent> readShow(JSONObject request) {
        try {
            String id = requiredId(request);
            String channelId = channelId(request);
            String title = text(request, PlatformContract.KEY_NOTIFICATION_TITLE, ALLOW_EMPTY,
                    PlatformContract.MAX_NOTIFICATION_TEXT_LENGTH);
            String body = text(request, PlatformContract.KEY_NOTIFICATION_BODY, ALLOW_EMPTY,
                    PlatformContract.MAX_NOTIFICATION_TEXT_LENGTH);
            boolean ongoing = optBoolean(request, PlatformContract.KEY_NOTIFICATION_ONGOING);
            String category = text(request, PlatformContract.KEY_NOTIFICATION_CATEGORY, ALLOW_EMPTY,
                    PlatformContract.MAX_NOTIFICATION_ID_LENGTH);
            List<NotificationActionItem> actions = actions(request);
            String payload = text(request, PlatformContract.KEY_NOTIFICATION_PAYLOAD_JSON, ALLOW_EMPTY,
                    PlatformContract.MAX_NOTIFICATION_PAYLOAD_LENGTH);
            return Result.of(new NotificationContent(id, channelId, title, body, ongoing, category, actions, payload));
        } catch (FieldException e) {
            return Result.invalid(e.getMessage());
        }
    }

    /**
     * notification.cancel の引数（ID だけ）を読む。
     *
     * @param request 引数
     * @return 結果（成功なら ID）
     */
    static Result<String> readId(JSONObject request) {
        try {
            return Result.of(requiredId(request));
        } catch (FieldException e) {
            return Result.invalid(e.getMessage());
        }
    }

    /** 必須の通知の ID。 */
    private static String requiredId(JSONObject request) throws FieldException {
        return text(request, PlatformContract.KEY_NOTIFICATION_ID, NON_EMPTY, PlatformContract.MAX_NOTIFICATION_ID_LENGTH);
    }

    /** 必須のチャネルの ID（予約の接頭辞で始まるものは不可）。 */
    private static String channelId(JSONObject request) throws FieldException {
        String id = text(request, PlatformContract.KEY_NOTIFICATION_CHANNEL_ID, NON_EMPTY, PlatformContract.MAX_NOTIFICATION_ID_LENGTH);
        if (id.startsWith(PlatformContract.RESERVED_NOTIFICATION_CHANNEL_PREFIX)) {
            throw new FieldException(PlatformContract.KEY_NOTIFICATION_CHANNEL_ID + " が "
                    + PlatformContract.RESERVED_NOTIFICATION_CHANNEL_PREFIX + " で始まるチャネルはプラットフォーム層が使うので使えません");
        }
        return id;
    }

    /** 任意の重要度（無い・null は default。語彙に無ければ誤り）。 */
    private static String importance(JSONObject request) throws FieldException {
        Object value = request.opt(PlatformContract.KEY_NOTIFICATION_IMPORTANCE);
        if (value == null || value == JSONObject.NULL) {
            return PlatformContract.NOTIFICATION_IMPORTANCE_DEFAULT;
        }
        if (!(value instanceof String) || !IMPORTANCES.contains(value)) {
            throw new FieldException(PlatformContract.KEY_NOTIFICATION_IMPORTANCE + " は " + String.join(" / ", IMPORTANCES)
                    + " のどれかにしてください");
        }
        return (String) value;
    }

    /** 操作の配列（null・無しは空。MAX_NOTIFICATION_ACTIONS 個まで。ID の重なりは誤り）。 */
    private static List<NotificationActionItem> actions(JSONObject request) throws FieldException {
        Object value = request.opt(PlatformContract.KEY_NOTIFICATION_ACTIONS);
        List<NotificationActionItem> actions = new ArrayList<>();
        if (value == null || value == JSONObject.NULL) {
            return actions;
        }
        if (!(value instanceof JSONArray)) {
            throw new FieldException(PlatformContract.KEY_NOTIFICATION_ACTIONS + " は配列にしてください");
        }
        JSONArray items = (JSONArray) value;
        if (items.length() > PlatformContract.MAX_NOTIFICATION_ACTIONS) {
            throw new FieldException(PlatformContract.KEY_NOTIFICATION_ACTIONS + " は " + PlatformContract.MAX_NOTIFICATION_ACTIONS
                    + " 個までにしてください（" + items.length() + " 個あります）");
        }
        for (int index = 0; index < items.length(); index++) {
            String where = PlatformContract.KEY_NOTIFICATION_ACTIONS + "[" + index + "]";
            Object item = items.opt(index);
            if (!(item instanceof JSONObject)) {
                throw new FieldException(where + " はオブジェクト（{ id, label }）にしてください");
            }
            JSONObject action = (JSONObject) item;
            String id;
            String label;
            try {
                id = text(action, PlatformContract.KEY_NOTIFICATION_ACTION_ID, NON_EMPTY, PlatformContract.MAX_NOTIFICATION_ID_LENGTH);
                label = text(action, PlatformContract.KEY_NOTIFICATION_ACTION_LABEL, NON_EMPTY,
                        PlatformContract.MAX_NOTIFICATION_TEXT_LENGTH);
            } catch (FieldException e) {
                throw new FieldException(where + " の " + e.getMessage());
            }
            for (NotificationActionItem existing : actions) {
                if (existing.id.equals(id)) {
                    throw new FieldException(where + " の " + PlatformContract.KEY_NOTIFICATION_ACTION_ID + " " + id + " が重なっています");
                }
            }
            actions.add(new NotificationActionItem(id, label));
        }
        return actions;
    }

    /** 任意の真偽（無い・null は false。型が違えば誤り）。 */
    private static boolean optBoolean(JSONObject request, String key) throws FieldException {
        Object value = request.opt(key);
        if (value == null || value == JSONObject.NULL) {
            return false;
        }
        if (!(value instanceof Boolean)) {
            throw new FieldException(key + " は真偽にしてください");
        }
        return (Boolean) value;
    }

    /** 文字列の欄（無い・null は空。長さは Unicode の符号位置で数えて minLength〜maxLength。型が違えば誤り）。 */
    private static String text(JSONObject request, String key, int minLength, int maxLength) throws FieldException {
        Object value = request.opt(key);
        String text;
        if (value == null || value == JSONObject.NULL) {
            text = EMPTY;
        } else if (value instanceof String) {
            text = (String) value;
        } else {
            throw new FieldException(key + " は文字列にしてください");
        }
        int length = text.codePointCount(0, text.length());
        if (length < minLength || length > maxLength) {
            throw new FieldException(minLength == ALLOW_EMPTY
                    ? key + " は " + maxLength + " 文字までにしてください"
                    : key + " は " + minLength + "〜" + maxLength + " 文字にしてください");
        }
        return text;
    }
}
