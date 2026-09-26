// ============================================================
//  PlatformContract.java — アプリのプラットフォーム機能（SEED.Platform）の約束の置き場（W1-1）
//
//  メインプロセス（SeedPlatform・PlatformConnection）と :seed_platform プロセス（service/ の PlatformProvider 等）の
//  両方が使う名前・キー・理由の名前を 1 か所に集める（マジックナンバー・文字列を散らさない）。
//  エンジン側（Rust）の対になる正典は runtime/src/engine/platform/bridge/wire.rs、C# 側は scripting/src/Api/Platform/。
//  値を変えるときは 3 か所を必ず揃える。全体像は docs/android.md §25。
// ============================================================

package com.seedengine.runtime.platform;

import android.content.Context;
import android.os.IBinder;

/**
 * プラットフォーム機能の約束（名前・キー・理由）の定数と、名前の規則の判定。
 *
 * <p>インスタンスは作らない。両方のプロセスから参照するので、ネイティブライブラリや Activity に依存するものを置かない
 * （:seed_platform は Java だけのプロセスで、libSEED.so を読み込まない）。</p>
 */
public final class PlatformContract {

    private PlatformContract() {
    }

    /** プロトコルの版（platform.version の返答）。Rust の wire::PROTOCOL_VERSION と一致させる。 */
    public static final int PROTOCOL_VERSION = 1;

    /** logcat のタグ（両プロセス共通。`adb logcat -s SEED SEEDPlatform` で一緒に見る）。 */
    public static final String LOG_TAG = "SEEDPlatform";

    // ── ContentProvider（:seed_platform の PlatformProvider）──

    /**
     * プロバイダの authority の接尾辞。authority は「applicationId＋この接尾辞」（マニフェストの
     * {@code ${applicationId}.seed_platform} と一致させる）。applicationId はプロジェクトごとに変わるので、実行時に作る。
     */
    public static final String AUTHORITY_SUFFIX = ".seed_platform";

    /** ContentProvider の call の method を「module＋区切り＋method」にするときの区切り。 */
    public static final String METHOD_SEPARATOR = ".";

    /** call の extras / 返答の Bundle: JSON（UTF-8 の byte[]）のキー。 */
    public static final String BUNDLE_JSON = "json";

    /** call の extras: イベントの知らせを受ける Binder（platform.register_callback）のキー。 */
    public static final String BUNDLE_CALLBACK = "callback";

    /** :seed_platform → メインプロセスの知らせ（Binder の oneway の呼び出し）: 未読の記録がある。Parcel は long（最新の seq）1 つ。 */
    public static final int TRANSACTION_EVENTS_AVAILABLE = IBinder.FIRST_CALL_TRANSACTION;

    // ── モジュールとメソッド（W1-1）──

    /** 基盤そのもののモジュール。 */
    public static final String MODULE_PLATFORM = "platform";
    /** 往復の計測（受け取った JSON をそのまま echo に入れて返す＋pid・起動からの ms）。 */
    public static final String METHOD_PING = "ping";
    /** プロトコルの版と使えるモジュール。 */
    public static final String METHOD_VERSION = "version";
    /** イベントの知らせを受ける Binder を登録する（メインプロセスの PlatformConnection が接続のたびに呼ぶ）。 */
    public static final String METHOD_REGISTER_CALLBACK = "register_callback";
    /** 試験イベントを 1 つ記録し、知らせる（デバッグ用）。 */
    public static final String METHOD_EMIT_TEST_EVENT = "emit_test_event";
    /** 未読の記録を取り出す（知らせを受けたメインプロセスが呼ぶ）。 */
    public static final String METHOD_POLL_EVENTS = "poll_events";

    // ── JSON のキー ──

    /** 返答: 成功したか。 */
    public static final String KEY_OK = "ok";
    /** 返答: 失敗の理由の名前（下の ERROR_*）。 */
    public static final String KEY_ERROR = "error";
    /** 返答: 失敗の詳しい説明（例外の型と文言。スクリプトの LastError には入らない。ログ用）。 */
    public static final String KEY_DETAIL = "detail";
    /** イベント: 名前。 */
    public static final String KEY_NAME = "name";
    /** イベント: 通し番号（記録の番号。メインプロセスで作る知らせは LOCAL_EVENT_SEQ）。 */
    public static final String KEY_SEQ = "seq";
    /** イベント: 起きた時刻（UTC の epoch ミリ秒）。 */
    public static final String KEY_TIME_MS = "time_ms";
    /** イベント: 中身。 */
    public static final String KEY_DATA = "data";
    /** poll_events の返答: イベントの配列。 */
    public static final String KEY_EVENTS = "events";
    /** poll_events の返答: まだ未読が残っているか。 */
    public static final String KEY_HAS_MORE = "has_more";
    /** 記録の最新の通し番号。 */
    public static final String KEY_LATEST_SEQ = "latest_seq";
    /** poll_events の引数: 1 回に取り出す最大の件数。 */
    public static final String KEY_MAX = "max";
    /** ping の返答: 受け取った JSON。 */
    public static final String KEY_ECHO = "echo";
    /** プロセスの pid。 */
    public static final String KEY_PID = "pid";
    /** ping の返答: プロセスの起動からの経過ミリ秒。 */
    public static final String KEY_UPTIME_MS = "uptime_ms";
    /** プロセスの名前（例 com.example:seed_platform）。 */
    public static final String KEY_PROCESS = "process";
    /** version の返答: プロトコルの版。 */
    public static final String KEY_PROTOCOL = "protocol";
    /** version の返答: 使えるモジュールの名前の配列。 */
    public static final String KEY_MODULES = "modules";
    /** version の返答: 端末の API レベル。 */
    public static final String KEY_SDK_INT = "sdk_int";
    /** 試験イベントの文言。 */
    public static final String KEY_MESSAGE = "message";
    /** 接続の知らせ: 接続にかかったミリ秒。 */
    public static final String KEY_CONNECT_MS = "connect_ms";
    /** 知らせ・返答: 記録の知らせ（Binder）を送れたか。 */
    public static final String KEY_DOORBELL = "doorbell";

    // ── イベントの名前（スクリプトの SEED.Events にもこの名前で流れる）──

    /** 名前の接頭辞。 */
    public static final String EVENT_PREFIX = "platform.";
    /** 試験イベント（platform.emit_test_event）。 */
    public static final String EVENT_TEST = "platform.test_event";
    /** :seed_platform へつながった（最初の呼び出しが背面で始めた接続が済んだ。以降の呼び出しは同期で通る）。 */
    public static final String EVENT_CONNECTED = "platform.connected";
    /** :seed_platform へつなげなかった（data.error に理由）。 */
    public static final String EVENT_CONNECT_FAILED = "platform.connect_failed";
    /** :seed_platform のプロセスが居なくなった（次の呼び出しでつなぎ直す）。 */
    public static final String EVENT_DISCONNECTED = "platform.disconnected";
    /** メインプロセスの中で作った知らせの seq（記録を通らないので番号を持たない）。Rust の wire::LOCAL_EVENT_SEQ と一致させる。 */
    public static final long LOCAL_EVENT_SEQ = 0;

    // ── 失敗の理由の名前（Rust の wire::ERROR_* と重なるものは一致させる。C# の Platform.LastError に入る）──

    /** 知らないモジュール・メソッド。 */
    public static final String ERROR_UNKNOWN_METHOD = "unknown_method";
    /** module / method の名前が規則（isValidName）に合わない。 */
    public static final String ERROR_INVALID_NAME = "invalid_name";
    /** 引数の JSON が読めない。 */
    public static final String ERROR_INVALID_JSON = "invalid_json";
    /** :seed_platform へつなぎ始めた・つないでいる途中（プロセスの起動を描画のスレッドで待たないため。platform.connected を待って呼び直す）。 */
    public static final String ERROR_CONNECTING = "connecting";
    /** プロバイダが見つからない（マニフェストに無い等）・つなげなかった。 */
    public static final String ERROR_PROVIDER_UNAVAILABLE = "provider_unavailable";
    /** :seed_platform のプロセスが居なくなった。 */
    public static final String ERROR_PROVIDER_DIED = "provider_died";
    /** Binder の呼び出しの失敗（プロセスの死以外）。 */
    public static final String ERROR_REMOTE = "remote_exception";
    /** 同期でつなぐのを待ちきれなかった（デバッグの受信機など、待ってよい呼び出し元だけ）。 */
    public static final String ERROR_CONNECT_TIMEOUT = "connect_timeout";
    /** 返答が空だった。 */
    public static final String ERROR_NO_REPLY = "no_reply";
    /** Java の中の想定外の例外。 */
    public static final String ERROR_INTERNAL = "internal_error";
    /** SeedPlatform.init が呼ばれていない。 */
    public static final String ERROR_NOT_INITIALIZED = "not_initialized";
    /** 同じアプリ以外からの呼び出し。 */
    public static final String ERROR_FORBIDDEN = "forbidden";
    /** register_callback に Binder が無い。 */
    public static final String ERROR_MISSING_CALLBACK = "missing_callback";

    /** module / method の名前の最大の長さ（文字）。Rust の wire::MAX_NAME_LEN と一致させる。 */
    public static final int MAX_NAME_LENGTH = 64;

    /**
     * プロバイダの authority（applicationId＋接尾辞）を作る。
     *
     * @param context どのプロセスの Context でもよい（パッケージ名 = applicationId）
     * @return authority
     */
    public static String authority(Context context) {
        return context.getPackageName() + AUTHORITY_SUFFIX;
    }

    /**
     * ContentProvider の call に渡す method（module＋区切り＋method）を作る。
     *
     * @param module モジュールの名前（isValidName を満たすこと）
     * @param method メソッドの名前（isValidName を満たすこと）
     * @return 例 "platform.ping"
     */
    public static String providerMethod(String module, String method) {
        return module + METHOD_SEPARATOR + method;
    }

    /**
     * module / method の名前が規則どおりか（1〜MAX_NAME_LENGTH 文字の小文字英数字と _）。
     * 区切りの . を含む名前を通すと module と method の境目が分からなくなるので、呼び出しの両端で確かめる。
     *
     * @param name 調べる名前（null 可）
     * @return 規則どおりなら true
     */
    public static boolean isValidName(String name) {
        if (name == null || name.isEmpty() || name.length() > MAX_NAME_LENGTH) {
            return false;
        }
        for (int i = 0; i < name.length(); i++) {
            char c = name.charAt(i);
            boolean allowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
            if (!allowed) {
                return false;
            }
        }
        return true;
    }
}
