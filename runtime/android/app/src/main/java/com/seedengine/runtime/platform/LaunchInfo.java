// ============================================================
//  LaunchInfo.java — 起動理由 1 つ（メインプロセス。W1-4a・W1-6 でディープリンクの uri を追加）
//
//  形は {kind, id, action_id, scheduled_at_utc_ms, fired_at_utc_ms, payload_json, uri}（:seed_platform の PlatformEntryIntents が
//  PlatformEntry 行きの Intent に載せるもの＋uri。Rust の wire::launch・C# の LaunchInfo と一致させる）。
//  kind は PlatformContract.LAUNCH_KIND_*。launcher・other・deep_link（W1-6。uri に Intent の data）はメインプロセスが自分で
//  決める種類で、それ以外（alarm・notification_tap・notification_action・alarm_clock_info）は PlatformEntry 経由の Intent からだけ
//  作る（LaunchReason）。deep_link の uri は他のアプリも送れる入力なので、アプリが検査してから使う。
// ============================================================

package com.seedengine.runtime.platform;

import org.json.JSONObject;

import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.Set;

/**
 * 起動理由（不変）。
 */
public final class LaunchInfo {

    /** 数の欄が無いときの値。 */
    private static final long NO_TIME = 0L;

    /** 文字列の欄が無いときの値。 */
    private static final String NO_TEXT = "";

    /** URI の scheme とその後ろを分ける文字（ログの要約に使う）。 */
    private static final char SCHEME_SEPARATOR = ':';

    /** URI に scheme が見当たらないときのログの表記。 */
    private static final String UNKNOWN_SCHEME = "?";

    /** PlatformEntry 経由の Intent だけが持てる種類（これ以外の kind が載っていたら other として扱う）。 */
    private static final Set<String> TRUSTED_KINDS = Collections.unmodifiableSet(new HashSet<>(Arrays.asList(
            PlatformContract.LAUNCH_KIND_ALARM,
            PlatformContract.LAUNCH_KIND_NOTIFICATION_TAP,
            PlatformContract.LAUNCH_KIND_NOTIFICATION_ACTION,
            PlatformContract.LAUNCH_KIND_ALARM_CLOCK_INFO)));

    /** ランチャー・普通の起動。 */
    static final LaunchInfo LAUNCHER = plain(PlatformContract.LAUNCH_KIND_LAUNCHER);

    /** それ以外の起動（ランチャー以外の action・読めなかった起動理由）。 */
    static final LaunchInfo OTHER = plain(PlatformContract.LAUNCH_KIND_OTHER);

    /** 種類（PlatformContract.LAUNCH_KIND_*）。 */
    public final String kind;
    /** 予約・通知の ID（無ければ空）。 */
    public final String id;
    /** 通知の操作の ID（notification_action のとき。無ければ空）。 */
    public final String actionId;
    /** 鳴るはずだった時刻（UTC の epoch ミリ秒。無ければ 0）。 */
    public final long scheduledAtUtcMs;
    /** 配信を受けた時刻（UTC の epoch ミリ秒。無ければ 0）。 */
    public final long firedAtUtcMs;
    /** アプリの任意の JSON（無ければ空）。 */
    public final String payloadJson;
    /** ディープリンクの URI（deep_link のとき。無ければ空。W1-6）。 */
    public final String uri;

    private LaunchInfo(String kind, String id, String actionId, long scheduledAtUtcMs, long firedAtUtcMs, String payloadJson,
            String uri) {
        this.kind = kind;
        this.id = id;
        this.actionId = actionId;
        this.scheduledAtUtcMs = scheduledAtUtcMs;
        this.firedAtUtcMs = firedAtUtcMs;
        this.payloadJson = payloadJson;
        this.uri = uri;
    }

    /** 種類だけの起動理由。 */
    private static LaunchInfo plain(String kind) {
        return new LaunchInfo(kind, NO_TEXT, NO_TEXT, NO_TIME, NO_TIME, NO_TEXT, NO_TEXT);
    }

    /**
     * ディープリンクの起動理由（W1-6。LaunchReason が ACTION_VIEW＋data の Intent から作る）。
     *
     * @param uri Intent の data（空でない・長さを確かめたもの）
     * @return 起動理由
     */
    static LaunchInfo deepLink(String uri) {
        return new LaunchInfo(PlatformContract.LAUNCH_KIND_DEEP_LINK, NO_TEXT, NO_TEXT, NO_TIME, NO_TIME, NO_TEXT, uri);
    }

    /**
     * PlatformEntry 経由の Intent の起動理由の JSON から作る（種類が約束に無ければ null）。
     *
     * @param json 起動理由（PlatformEntryIntents.launchJson の形）
     * @return 起動理由（読めなければ null）
     */
    static LaunchInfo fromTrustedJson(JSONObject json) {
        String kind = json.optString(PlatformContract.KEY_LAUNCH_KIND, NO_TEXT);
        if (!TRUSTED_KINDS.contains(kind)) {
            return null;
        }
        return new LaunchInfo(kind,
                json.optString(PlatformContract.KEY_ALARM_ID, NO_TEXT),
                json.optString(PlatformContract.KEY_LAUNCH_ACTION_ID, NO_TEXT),
                json.optLong(PlatformContract.KEY_ALARM_SCHEDULED_AT_UTC_MS, NO_TIME),
                json.optLong(PlatformContract.KEY_ALARM_FIRED_AT_UTC_MS, NO_TIME),
                json.optString(PlatformContract.KEY_ALARM_PAYLOAD_JSON, NO_TEXT),
                NO_TEXT);
    }

    /**
     * 目覚ましの鳴動による起動か（ロック画面の上に出し、画面を点ける）。
     *
     * @return alarm なら true
     */
    public boolean isAlarm() {
        return PlatformContract.LAUNCH_KIND_ALARM.equals(kind);
    }

    /**
     * ランチャー・最近のタスクからの普通の起動か（W1-6。開き直しでロック画面の上から下ろす忘れ対策に使う）。
     *
     * @return launcher なら true
     */
    public boolean isLauncher() {
        return PlatformContract.LAUNCH_KIND_LAUNCHER.equals(kind);
    }

    /**
     * JSON にする（launch_reason の返答の launch・platform.launch の data）。
     *
     * @return JSON のオブジェクト
     */
    public JSONObject toJson() {
        JSONObject json = new JSONObject();
        PlatformJson.put(json, PlatformContract.KEY_LAUNCH_KIND, kind);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_ID, id);
        PlatformJson.put(json, PlatformContract.KEY_LAUNCH_ACTION_ID, actionId);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_SCHEDULED_AT_UTC_MS, scheduledAtUtcMs);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_FIRED_AT_UTC_MS, firedAtUtcMs);
        PlatformJson.put(json, PlatformContract.KEY_ALARM_PAYLOAD_JSON, payloadJson);
        PlatformJson.put(json, PlatformContract.KEY_LAUNCH_URI, uri);
        return json;
    }

    /**
     * ログ向けの 1 行。ディープリンクの URI はトークン等を含みうるので、scheme と長さだけを出す。
     */
    @Override
    public String toString() {
        return kind + (id.isEmpty() ? "" : " " + id) + (actionId.isEmpty() ? "" : "/" + actionId) + describeUri();
    }

    /** ログ向けの URI の要約（無ければ空。例 " wakeorpay:…（21 文字）"）。 */
    private String describeUri() {
        if (uri.isEmpty()) {
            return "";
        }
        int separator = uri.indexOf(SCHEME_SEPARATOR);
        String scheme = separator > 0 ? uri.substring(0, separator) : UNKNOWN_SCHEME;
        return " " + scheme + SCHEME_SEPARATOR + "…（" + uri.codePointCount(0, uri.length()) + " 文字）";
    }
}
