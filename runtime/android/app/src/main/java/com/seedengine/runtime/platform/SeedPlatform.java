// ============================================================
//  SeedPlatform.java — アプリのプラットフォーム機能（SEED.Platform）の JNI の入口（メインプロセス。W1-1）
//
//  【JNI の面（増やさない。docs/app_platform_roadmap.md W1-P3）】
//    native → Java: invoke(String module, String method, byte[] json) → byte[] json   … 同期。例外を投げない
//    Java → native: nativeOnPlatformEvent(byte[] json)                                … イベントを 1 件エンジンの箱へ積む
//    Java → native: nativeRegisterPlatformBridge(Class)                               … 起動時に 1 回。このクラスを渡す
//  機能は :seed_platform のモジュール表（service/PlatformProvider）と C# の包みで増やし、ここの関数は増やさない。
//
//  【nativeRegisterPlatformBridge が要る理由】
//  ネイティブのスレッドから FindClass すると、システムのクラスローダーになり APK のクラスが見えない
//  （.NET の暗号の初期化で実際に abort した。docs/android.md §17.8）。そこで Java からこのクラスを渡し、
//  ネイティブ（runtime/android/native/src/platform_bridge/java_bridge.rs）が GlobalRef と invoke のメソッド ID を持つ。
//
//  【呼ばれ方】
//  MainActivity.onCreate（super.onCreate より前。android_main のスレッドが立つ前）に init を呼ぶ。init は
//  :seed_platform を呼ばない（プロセスの起動は最初の invoke まで遅らせる。描画のスレッドで待たない仕組みは PlatformConnection）。
//  R8 は使っていない（app/build.gradle.kts）ので、JNI から名前で呼ぶ invoke が消されることは無い。
//
//  【メインプロセスで答える命令（W1-4a）】
//  invoke はまず local/MainProcessCommands の表を引き、起動理由（platform.launch_reason）・画面の操作
//  （window.set_show_when_locked）はその場で答える（IPC に行かない）。それ以外を :seed_platform へ送る。
//  「この起動の理由」（LaunchReason が onCreate・onNewIntent で決めたもの）はここに預かる。
// ============================================================

package com.seedengine.runtime.platform;

import android.app.Activity;
import android.content.Context;
import android.util.Log;

import com.seedengine.runtime.platform.local.HostActivity;
import com.seedengine.runtime.platform.local.MainProcessCommands;

import org.json.JSONObject;

/**
 * JNI の入口（static のみ）。プロセスで 1 つの PlatformConnection を持つ。
 */
public final class SeedPlatform {

    private SeedPlatform() {
    }

    /** connection の作成を守る。 */
    private static final Object CONNECTION_LOCK = new Object();

    /** :seed_platform への接続（最初に要るときに作る。プロセスの間ずっと同じもの）。 */
    private static PlatformConnection connection;

    /** この起動の理由（最後に Activity へ届いた Intent の理由。既定はランチャー。W1-4a）。 */
    private static volatile LaunchInfo launchReason = LaunchInfo.LAUNCHER;

    /**
     * 起動時の準備（MainActivity.onCreate から。super.onCreate より前に呼ぶ）。
     *
     * <p>接続の入れ物を作り、画面の命令が操作する Activity を覚え、ネイティブへこのクラスを渡す。
     * :seed_platform には触らない（起動させない）。</p>
     *
     * @param activity 起動した Activity（接続にはアプリの Context だけを使う）
     */
    public static void init(Activity activity) {
        connection(activity.getApplicationContext());
        HostActivity.attach(activity);
        try {
            nativeRegisterPlatformBridge(SeedPlatform.class);
        } catch (UnsatisfiedLinkError e) {
            // 古い libSEED.so（関数が無い）でも起動は続ける（スクリプトからは IsSupported == false に見える）。
            Log.w(PlatformContract.LOG_TAG, "nativeRegisterPlatformBridge を呼べませんでした（SEED.Platform は使えません）: " + e);
        }
    }

    /**
     * プロセスで 1 つの接続を返す（無ければ作る。IPC はしない）。
     *
     * @param context どの Context でもよい（アプリの Context を使う）
     * @return 接続
     */
    static PlatformConnection connection(Context context) {
        synchronized (CONNECTION_LOCK) {
            if (connection == null) {
                connection = new PlatformConnection(context.getApplicationContext(), SeedPlatform::deliverEvent);
            }
            return connection;
        }
    }

    /**
     * ネイティブ（エンジンのスレッド）から呼ばれる同期の命令（JNI。名前と引数を変えないこと）。
     *
     * <p>例外を投げない（JNI の境界に例外を残さない）。つながっていなければ背面で接続を始めて
     * {"ok":false,"error":"connecting"} を返す（描画のスレッドでプロセスの起動を待たない）。</p>
     *
     * @param module   モジュールの名前
     * @param method   メソッドの名前
     * @param jsonUtf8 引数の JSON（UTF-8。null は {}）
     * @return 返答の JSON（UTF-8。null を返さない）
     */
    static byte[] invoke(String module, String method, byte[] jsonUtf8) {
        try {
            // メインプロセスで答える命令（起動理由・画面）は IPC に行かない
            byte[] local = MainProcessCommands.tryHandle(module, method, jsonUtf8);
            if (local != null) {
                return local;
            }
            PlatformConnection current;
            synchronized (CONNECTION_LOCK) {
                current = connection;
            }
            if (current == null) {
                return PlatformJson.errorReply(PlatformContract.ERROR_NOT_INITIALIZED);
            }
            return current.invoke(module, method, jsonUtf8, false);
        } catch (Throwable e) {
            // JNI の境界: どんな失敗も返答の形にする（ネイティブ側に例外を持ち越さない）
            Log.e(PlatformContract.LOG_TAG, module + "." + method + " の途中で例外", e);
            return PlatformJson.errorReply(PlatformContract.ERROR_INTERNAL, PlatformJson.describe(e));
        }
    }

    /**
     * この起動の理由を預ける（LaunchReason から。onCreate・onNewIntent）。
     *
     * @param info 起動理由
     */
    static void setLaunchReason(LaunchInfo info) {
        launchReason = info;
    }

    /**
     * この起動の理由（platform.launch_reason の返答。まだ決まっていなければランチャー）。
     *
     * @return 起動理由
     */
    public static LaunchInfo launchReason() {
        return launchReason;
    }

    /**
     * メインプロセスの中で作ったイベント（seq 0）をエンジンへ渡す（LaunchReason の platform.launch など）。
     *
     * @param name 名前（PlatformContract.EVENT_*）
     * @param data 中身
     */
    static void emitLocalEvent(String name, JSONObject data) {
        JSONObject event = PlatformJson.event(name, PlatformContract.LOCAL_EVENT_SEQ, System.currentTimeMillis(), data);
        deliverEvent(PlatformJson.utf8(event.toString()));
    }

    /**
     * 届いたイベントをエンジンへ渡す（PlatformConnection の背面のスレッド・onNewIntent の UI スレッドから。
     * ネイティブ側は箱へ積むだけなので、どのスレッドから呼んでもよい）。
     *
     * <p>エンジン（libSEED.so）が読み込まれていないプロセス（デバッグの受信機だけで起きたプロセス）では捨てる。</p>
     *
     * @param eventJsonUtf8 イベントの JSON（UTF-8）
     */
    static void deliverEvent(byte[] eventJsonUtf8) {
        try {
            nativeOnPlatformEvent(eventJsonUtf8);
        } catch (UnsatisfiedLinkError e) {
            Log.w(PlatformContract.LOG_TAG, "エンジンが読み込まれていないので、イベントを捨てました: "
                    + PlatformJson.text(eventJsonUtf8));
        }
    }

    /**
     * イベントを 1 件エンジンの箱へ積む（libSEED.so の platform_bridge/jni_exports.rs）。
     *
     * @param jsonUtf8 イベントの JSON（UTF-8。{"name","seq","time_ms","data"}）
     */
    private static native void nativeOnPlatformEvent(byte[] jsonUtf8);

    /**
     * このクラスをネイティブへ渡す（GlobalRef と invoke のメソッド ID を持たせる。libSEED.so の platform_bridge/jni_exports.rs）。
     *
     * @param cls このクラス（SeedPlatform.class）
     */
    private static native void nativeRegisterPlatformBridge(Class<?> cls);
}
