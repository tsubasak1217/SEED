// ============================================================
//  AlarmRequestReader.java — alarm.schedule / alarm.cancel の引数の JSON を読み、検査・正規化する（W1-3）
//
//  【規則】（Rust のデスクトップの模擬 runtime/src/engine/platform/bridge/alarm/request.rs と同じ。変えるときは両方）
//    id                … 必須。1〜MAX_ALARM_ID_LENGTH 文字（Unicode の符号位置）の文字列
//    trigger_at_utc_ms … 必須。正の数（UTC の epoch ミリ秒。小数は切り捨て）。過去の時刻も受け付ける（setAlarmClock はすぐ配信する）
//    sound_path        … 任意。端末のファイルの絶対パス（/ で始まる）か空。それ以外は警告して空（既定の音）
//    sound_asset       … sound_path が空のときだけ見る。/ で始まれば sound_path に使い、assets:// などは読めないので警告して空
//                        （assets:// の書き出しはメインプロセスのエンジンの仕事。docs/android.md §25.11）
//    vibrate / keep_volume … 任意の真偽（無ければ既定値）
//    force_volume      … 任意の数。負は「触らない」（-1）にそろえ、1 を超えたら 1
//    fade_in_seconds   … 任意の数。負は 0
//    max_ring_minutes  … 任意の数（小数は切り捨て）。1 未満は 1
//    title / body      … 任意の文字列（MAX_ALARM_TEXT_LENGTH 文字まで）
//    payload_json      … 任意の文字列（MAX_ALARM_PAYLOAD_LENGTH 文字まで。中身は検査しない）
//  欄があるのに型が違う（数の欄に文字列など）ときは invalid_argument（detail に欄の名前）。無い・null は既定値。
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;

import org.json.JSONObject;

/**
 * 引数の読み取り（static のみ）。
 */
final class AlarmRequestReader {

    private AlarmRequestReader() {
    }

    /** 端末のファイルの絶対パスの先頭（Android のパス）。 */
    private static final String ABSOLUTE_PATH_PREFIX = "/";

    /** 空の文字列の欄の既定値。 */
    private static final String EMPTY = "";

    /** 音量の漸増の下限（秒）。 */
    private static final double MIN_FADE_IN_SECONDS = 0.0;

    /** 読み取りの結果（entry と error のどちらか一方）。 */
    static final class Result {
        /** 読めた予約（失敗なら null）。 */
        final AlarmEntry entry;
        /** 読めた ID（readId の結果。失敗なら null）。 */
        final String id;
        /** 失敗の理由（PlatformContract.ERROR_*。成功なら null）。 */
        final String error;
        /** 失敗の説明（ログ・返答の detail）。 */
        final String detail;

        private Result(AlarmEntry entry, String id, String error, String detail) {
            this.entry = entry;
            this.id = id;
            this.error = error;
            this.detail = detail;
        }

        /** @return 読めたら true */
        boolean ok() {
            return error == null;
        }

        static Result ofEntry(AlarmEntry entry) {
            return new Result(entry, entry.id, null, null);
        }

        static Result ofId(String id) {
            return new Result(null, id, null, null);
        }

        static Result invalid(String detail) {
            return new Result(null, null, PlatformContract.ERROR_INVALID_ARGUMENT, detail);
        }
    }

    /** 欄の型の誤り（読み取りの途中で投げ、read が invalid_argument の結果にする）。 */
    private static final class FieldException extends Exception {
        /** 直列化の版（直列化はしないが、Exception の約束として持つ）。 */
        private static final long serialVersionUID = 1L;

        FieldException(String detail) {
            super(detail);
        }
    }

    /**
     * alarm.schedule の引数を読む。
     *
     * @param request  引数（オブジェクトでなければ空として扱う＝ID が無いので失敗）
     * @param nowUtcMs 受け付けた時刻（控えの created_at_utc_ms）
     * @return 結果
     */
    static Result readSchedule(JSONObject request, long nowUtcMs) {
        try {
            String id = requiredId(request);
            long triggerAt = requiredTriggerAt(request);
            String soundPath = soundPath(request, id);
            boolean vibrate = optBoolean(request, PlatformContract.KEY_ALARM_VIBRATE, PlatformContract.DEFAULT_ALARM_VIBRATE);
            double forceVolume = normalizeVolume(
                    optNumber(request, PlatformContract.KEY_ALARM_FORCE_VOLUME, PlatformContract.ALARM_VOLUME_UNCHANGED));
            boolean keepVolume = optBoolean(request, PlatformContract.KEY_ALARM_KEEP_VOLUME, PlatformContract.DEFAULT_ALARM_KEEP_VOLUME);
            double fadeIn = Math.max(MIN_FADE_IN_SECONDS,
                    optNumber(request, PlatformContract.KEY_ALARM_FADE_IN_SECONDS, PlatformContract.DEFAULT_ALARM_FADE_IN_SECONDS));
            long maxRing = (long) optNumber(request, PlatformContract.KEY_ALARM_MAX_RING_MINUTES,
                    PlatformContract.DEFAULT_ALARM_MAX_RING_MINUTES);
            int maxRingMinutes = (int) Math.min(Integer.MAX_VALUE, Math.max(PlatformContract.MIN_ALARM_MAX_RING_MINUTES, maxRing));
            String title = optText(request, PlatformContract.KEY_ALARM_TITLE, PlatformContract.MAX_ALARM_TEXT_LENGTH);
            String body = optText(request, PlatformContract.KEY_ALARM_BODY, PlatformContract.MAX_ALARM_TEXT_LENGTH);
            String payload = optText(request, PlatformContract.KEY_ALARM_PAYLOAD_JSON, PlatformContract.MAX_ALARM_PAYLOAD_LENGTH);
            return Result.ofEntry(new AlarmEntry(id, triggerAt, soundPath, vibrate, forceVolume, keepVolume, fadeIn,
                    maxRingMinutes, title, body, payload, nowUtcMs));
        } catch (FieldException e) {
            return Result.invalid(e.getMessage());
        }
    }

    /**
     * alarm.cancel の引数（ID だけ）を読む。
     *
     * @param request 引数
     * @return 結果（成功なら id）
     */
    static Result readId(JSONObject request) {
        try {
            return Result.ofId(requiredId(request));
        } catch (FieldException e) {
            return Result.invalid(e.getMessage());
        }
    }

    /** 必須の ID（1〜MAX_ALARM_ID_LENGTH 文字の文字列）。 */
    private static String requiredId(JSONObject request) throws FieldException {
        Object value = request.opt(PlatformContract.KEY_ALARM_ID);
        if (!(value instanceof String)) {
            throw new FieldException(PlatformContract.KEY_ALARM_ID + " は文字列にしてください");
        }
        String id = (String) value;
        int length = id.codePointCount(0, id.length());
        if (length == 0 || length > PlatformContract.MAX_ALARM_ID_LENGTH) {
            throw new FieldException(PlatformContract.KEY_ALARM_ID + " は 1〜" + PlatformContract.MAX_ALARM_ID_LENGTH + " 文字にしてください");
        }
        return id;
    }

    /** 必須の予定時刻（正の数。小数は切り捨て）。 */
    private static long requiredTriggerAt(JSONObject request) throws FieldException {
        Object value = request.opt(PlatformContract.KEY_ALARM_TRIGGER_AT_UTC_MS);
        if (!(value instanceof Number)) {
            throw new FieldException(PlatformContract.KEY_ALARM_TRIGGER_AT_UTC_MS + " は数にしてください");
        }
        long triggerAt = ((Number) value).longValue();
        if (triggerAt <= 0) {
            throw new FieldException(PlatformContract.KEY_ALARM_TRIGGER_AT_UTC_MS + " は正の数（UTC の epoch ミリ秒）にしてください");
        }
        return triggerAt;
    }

    /** 音源の端末のファイルの絶対パス（読めない指定は警告して空＝既定の音）。 */
    private static String soundPath(JSONObject request, String id) throws FieldException {
        String path = optText(request, PlatformContract.KEY_ALARM_SOUND_PATH, Integer.MAX_VALUE);
        if (path.isEmpty()) {
            String asset = optText(request, PlatformContract.KEY_ALARM_SOUND_ASSET, Integer.MAX_VALUE);
            if (asset.startsWith(ABSOLUTE_PATH_PREFIX)) {
                return asset;
            }
            if (!asset.isEmpty()) {
                // assets://（pak の中身）は Java だけのこのプロセスからは読めない。書き出しはメインプロセスのエンジンの仕事
                Log.w(PlatformContract.LOG_TAG, "予約 " + id + " の音源 " + asset + " は書き出されていないので既定の音にします");
            }
            return EMPTY;
        }
        if (!path.startsWith(ABSOLUTE_PATH_PREFIX)) {
            Log.w(PlatformContract.LOG_TAG, "予約 " + id + " の sound_path が絶対パスでないので既定の音にします: " + path);
            return EMPTY;
        }
        return path;
    }

    /** 音量の正規化（負は「触らない」、上限を超えたら上限）。 */
    private static double normalizeVolume(double volume) {
        if (volume < 0) {
            return PlatformContract.ALARM_VOLUME_UNCHANGED;
        }
        return Math.min(PlatformContract.MAX_ALARM_FORCE_VOLUME, volume);
    }

    /** 任意の真偽（無い・null は既定値。型が違えば誤り）。 */
    private static boolean optBoolean(JSONObject request, String key, boolean defaultValue) throws FieldException {
        Object value = request.opt(key);
        if (value == null || value == JSONObject.NULL) {
            return defaultValue;
        }
        if (!(value instanceof Boolean)) {
            throw new FieldException(key + " は真偽にしてください");
        }
        return (Boolean) value;
    }

    /** 任意の数（無い・null は既定値。型が違う・有限でなければ誤り）。 */
    private static double optNumber(JSONObject request, String key, double defaultValue) throws FieldException {
        Object value = request.opt(key);
        if (value == null || value == JSONObject.NULL) {
            return defaultValue;
        }
        if (!(value instanceof Number)) {
            throw new FieldException(key + " は数にしてください");
        }
        double number = ((Number) value).doubleValue();
        if (Double.isNaN(number) || Double.isInfinite(number)) {
            throw new FieldException(key + " は有限の数にしてください");
        }
        return number;
    }

    /** 任意の文字列（無い・null は空。型が違う・長すぎれば誤り）。 */
    private static String optText(JSONObject request, String key, int maxLength) throws FieldException {
        Object value = request.opt(key);
        if (value == null || value == JSONObject.NULL) {
            return EMPTY;
        }
        if (!(value instanceof String)) {
            throw new FieldException(key + " は文字列にしてください");
        }
        String text = (String) value;
        if (text.codePointCount(0, text.length()) > maxLength) {
            throw new FieldException(key + " は " + maxLength + " 文字までにしてください");
        }
        return text;
    }
}
