// ============================================================
//  PlatformJson.java — プラットフォーム機能の JSON の組み立て・読み取りの共通処理（W1-1）
//
//  返答（{"ok":…}）・イベント（{"name","seq","time_ms","data"}）・引数の読み取りを、両プロセスの部品が同じ形で作るための
//  小さな道具。値の受け渡しは UTF-8 の byte[]（JNI の修正 UTF-8 の文字列を避ける。docs/android.md §4・§25）。
//  JSON は Android 標準の org.json を使う（依存を増やさない）。
// ============================================================

package com.seedengine.runtime.platform;

import org.json.JSONException;
import org.json.JSONObject;
import org.json.JSONTokener;

import java.nio.charset.StandardCharsets;
import java.util.Iterator;

/**
 * プラットフォーム機能の JSON の道具（static のみ）。
 *
 * <p>org.json の put は検査例外（JSONException）を投げるが、ここで作る JSON はキーが固定の文字列・値が文字列／数値／真偽値／
 * JSON の値だけなので、実際には起きない（起きたら作り方の誤りなので IllegalArgumentException にする）。</p>
 */
public final class PlatformJson {

    private PlatformJson() {
    }

    /** 引数が空のときに使う JSON（空のオブジェクト）。 */
    private static final String EMPTY_OBJECT_TEXT = "{}";

    /**
     * 文字列を UTF-8 の byte[] にする。
     *
     * @param text 文字列
     * @return UTF-8 のバイト列
     */
    public static byte[] utf8(String text) {
        return text.getBytes(StandardCharsets.UTF_8);
    }

    /**
     * UTF-8 の byte[] を文字列にする（null は空文字）。
     *
     * @param utf8 UTF-8 のバイト列（null 可）
     * @return 文字列
     */
    public static String text(byte[] utf8) {
        return utf8 == null ? "" : new String(utf8, StandardCharsets.UTF_8);
    }

    /**
     * 空のオブジェクト（{}）の UTF-8。呼ぶたびに新しい配列を返す（受け取った側が書き換えても壊れないように）。
     *
     * @return {} の UTF-8
     */
    public static byte[] emptyObject() {
        return utf8(EMPTY_OBJECT_TEXT);
    }

    /**
     * オブジェクトへ 1 つ入れる（検査例外を持ち込まない put）。
     *
     * @param target 入れ先
     * @param key    キー
     * @param value  値（文字列・数値・真偽値・JSONObject・JSONArray・JSONObject.NULL）
     * @return target（続けて入れられるように）
     */
    public static JSONObject put(JSONObject target, String key, Object value) {
        try {
            return target.put(key, value);
        } catch (JSONException e) {
            // 数値の NaN・無限大か null のキーでだけ起きる。ここで作る JSON では起きない（作り方の誤り）。
            throw new IllegalArgumentException("JSON に入れられない値です: " + key, e);
        }
    }

    /**
     * 失敗の返答 {"ok":false,"error":理由} を作る。
     *
     * @param reason 理由の名前（PlatformContract.ERROR_*）
     * @return UTF-8 の JSON
     */
    public static byte[] errorReply(String reason) {
        return errorReply(reason, null);
    }

    /**
     * 失敗の返答 {"ok":false,"error":理由,"detail":説明} を作る。
     *
     * @param reason 理由の名前（PlatformContract.ERROR_*）
     * @param detail 詳しい説明（null・空なら入れない）
     * @return UTF-8 の JSON
     */
    public static byte[] errorReply(String reason, String detail) {
        JSONObject reply = new JSONObject();
        put(reply, PlatformContract.KEY_OK, false);
        put(reply, PlatformContract.KEY_ERROR, reason);
        if (detail != null && !detail.isEmpty()) {
            put(reply, PlatformContract.KEY_DETAIL, detail);
        }
        return utf8(reply.toString());
    }

    /**
     * 成功の返答を作る（先頭に "ok":true を入れ、fields の中身を続ける。fields の ok は無視する）。
     *
     * @param fields 返答の中身
     * @return UTF-8 の JSON
     */
    public static byte[] okReply(JSONObject fields) {
        JSONObject reply = new JSONObject();
        put(reply, PlatformContract.KEY_OK, true);
        Iterator<String> keys = fields.keys();
        while (keys.hasNext()) {
            String key = keys.next();
            if (!PlatformContract.KEY_OK.equals(key)) {
                put(reply, key, fields.opt(key));
            }
        }
        return utf8(reply.toString());
    }

    /**
     * 引数の JSON を読む（null・空は空のオブジェクト。オブジェクト以外の値〈配列・数値など〉もそのまま返す）。
     *
     * @param json UTF-8 の JSON（null 可）
     * @return 読んだ値（JSONObject・JSONArray・String・Number・Boolean・JSONObject.NULL）
     * @throws JSONException 読めない・値の後ろに余計な文字がある
     */
    public static Object parseRequest(byte[] json) throws JSONException {
        String source = text(json).trim();
        if (source.isEmpty()) {
            return new JSONObject();
        }
        JSONTokener tokener = new JSONTokener(source);
        Object value = tokener.nextValue();
        // 前後の空白は落としてあるので、値の後ろに何か残っていれば壊れた JSON（"{} x" など）
        if (tokener.more()) {
            throw new JSONException("値の後ろに余計な文字があります");
        }
        return value;
    }

    /**
     * 読んだ引数をオブジェクトとして扱う（オブジェクトでなければ空のオブジェクト）。
     *
     * @param request parseRequest の結果
     * @return オブジェクト
     */
    public static JSONObject asObject(Object request) {
        return request instanceof JSONObject ? (JSONObject) request : new JSONObject();
    }

    /**
     * イベントを作る（{"name","seq","time_ms","data"}）。
     *
     * @param name   名前（PlatformContract.EVENT_*。platform. で始める）
     * @param seq    通し番号（記録の番号か LOCAL_EVENT_SEQ）
     * @param timeMs 起きた時刻（UTC の epoch ミリ秒）
     * @param data   中身
     * @return イベントのオブジェクト
     */
    public static JSONObject event(String name, long seq, long timeMs, JSONObject data) {
        JSONObject event = new JSONObject();
        put(event, PlatformContract.KEY_NAME, name);
        put(event, PlatformContract.KEY_SEQ, seq);
        put(event, PlatformContract.KEY_TIME_MS, timeMs);
        put(event, PlatformContract.KEY_DATA, data);
        return event;
    }

    /**
     * 例外を 1 行の説明にする（返答の detail・ログ用）。
     *
     * @param error 例外
     * @return 「型名: 文言」
     */
    public static String describe(Throwable error) {
        return error.getClass().getSimpleName() + ": " + error.getMessage();
    }
}
