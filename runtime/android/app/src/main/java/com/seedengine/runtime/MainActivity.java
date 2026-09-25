// ============================================================
//  MainActivity.java — SEED ランタイムの Android エントリ Activity（段階0）
//
//  GameActivity（AGDK）を継承するだけの薄いクラス。描画・入力・ゲームループはすべて
//  ネイティブ側（libSEED.so の android_main → エンジン）で動き、ここでは
//    ・ネイティブライブラリの読み込み
//    ・環境変数 TMPDIR / HOME をアプリのフォルダへ向け、同梱 .NET の診断機能を止める（理由は setAppDirectoryEnvironment のコメント）
//    ・起動の Intent の「seed.」で始まる文字列の extra（起動するシーン等）を JSON にしてネイティブへ渡す
//      （デバッグ版の APK だけ。理由は forwardLaunchOptions のコメント。段階C-3）
//    ・全画面（システムバーを隠す）
//    ・安全領域と画面の回転をネイティブへ知らせる（中身は ScreenReporter。契機の受け口だけここ）
//    ・音量キーの対象をメディアの音量にし、音声フォーカスを前面で要求・前面を離れるときに手放す
//      （中身は AudioFocusController。契機の受け口だけここ）
//    ・Activity 破棄時のセーブ書き出し（JNI）とプロセス終了（理由は onDestroy のコメント）
//  だけを行う。画面の向きの固定はマニフェスト（ビルド時にプロジェクト設定から決まる）。全体像は docs/android.md。
// ============================================================

package com.seedengine.runtime;

import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.res.Configuration;
import android.media.AudioManager;
import android.os.Bundle;
import android.os.Process;
import android.system.ErrnoException;
import android.system.Os;
import android.util.Log;
import android.view.View;

import java.nio.charset.StandardCharsets;

import org.json.JSONException;
import org.json.JSONObject;

import androidx.core.view.WindowCompat;
import androidx.core.view.WindowInsetsCompat;
import androidx.core.view.WindowInsetsControllerCompat;

import com.google.androidgamesdk.GameActivity;

/**
 * SEED ランタイムの唯一の Activity。
 *
 * <p>ネイティブライブラリ名はマニフェストの {@code android.app.lib_name}（GameActivity が
 * onCreate で読む）と一致させてある。Cargo 側の出力名 libSEED.so（runtime/android/native の
 * [lib] name）とも同じ。</p>
 */
public class MainActivity extends GameActivity {

    /** ネイティブライブラリ名（libSEED.so）。Cargo の [lib] name とマニフェストの lib_name と一致させる。 */
    private static final String NATIVE_LIBRARY_NAME = "SEED";

    /** logcat のタグ（ネイティブ側と同じにして `adb logcat -s SEED` で一緒に見えるようにする）。 */
    private static final String LOG_TAG = "SEED";

    /** 一時ファイルの置き場を指す環境変数（.NET の Path.GetTempPath・Rust の std::env::temp_dir が読む）。 */
    private static final String TEMP_DIR_ENV = "TMPDIR";

    /** ユーザーのホームを指す環境変数（.NET のユーザーフォルダ系 API が読む）。 */
    private static final String HOME_DIR_ENV = "HOME";

    /**
     * 同梱 .NET（CoreCLR / Mono）の診断機能（デバッガ・プロファイラ・EventPipe の待ち受け）の有効／無効を決める環境変数。
     * 端末ではデバッガを付けないので止めておく（起動時の待ち受けスレッドと TMPDIR への FIFO・ソケットの作成を省く。
     * docs/android.md §11.2・§17）。CLR は起動時に getenv で読むので、ネイティブのスレッドが無いうちに設定する。
     */
    private static final String DOTNET_DIAGNOSTICS_ENV = "DOTNET_EnableDiagnostics";

    /** {@link #DOTNET_DIAGNOSTICS_ENV} に入れる値（0 = 無効）。 */
    private static final String DOTNET_DIAGNOSTICS_DISABLED = "0";

    /**
     * 起動オプションとしてネイティブへ渡す Intent の extra の名前の接頭辞（段階C-3）。
     * エディタ／SeedAndroid は {@code am start --es seed.scene <パス>} で渡す。接頭辞を外した名前（scene）が JSON のキーになる。
     * editor/src/Android/Common/AndroidRuntimeContract.cs の LaunchOptionExtraPrefix と一致させる。
     */
    private static final String LAUNCH_OPTION_EXTRA_PREFIX = "seed.";

    static {
        // GameActivity も onCreate で読み込むが、失敗を最も早い段階で明確に出すためここでも読む
        // （2 回目の loadLibrary は何もしない）。
        System.loadLibrary(NATIVE_LIBRARY_NAME);
        // 同梱 .NET の、JNI の初期化が要るネイティブライブラリ（暗号）を CLR の起動より前に読み込む（段階B）。
        DotnetJniLibraries.loadAvailable();
    }

    /**
     * 未書き出しのセーブデータを同期でディスクへ書き出す（libSEED.so の jni_exports.rs）。
     *
     * <p>プロセスを終える直前（onDestroy）の保険。通常はバックグラウンドへ回った時点
     * （ネイティブ側の suspended）で書き出し済みで、何も書かずに戻る。</p>
     */
    private static native void nativeFlushSaveData();

    /**
     * 起動オプション（UTF-8 の JSON。例 {"scene":"scenes/Main.scene"}）をネイティブへ預ける（libSEED.so の jni_exports.rs）。
     *
     * <p>android_main（super.onCreate が立てるスレッド）が起動引数を組み立てる前に呼ぶこと。
     * UTF-8 の byte[] で渡すのは、JNI の文字列（修正 UTF-8）と違って絵文字等も含めてそのまま読めるため。</p>
     */
    private static native void nativeSetLaunchOptions(byte[] optionsUtf8);

    /** 安全領域と画面の回転をネイティブへ知らせる係（UI スレッド専用）。 */
    private final ScreenReporter screenReporter = new ScreenReporter(this);

    /** 音声フォーカスの要求・放棄と変化の通知（UI スレッド専用。システムサービスを使うので onCreate で作る）。 */
    private AudioFocusController audioFocus;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        // super.onCreate がネイティブ側（android_main のスレッド）を起動するので、その前に行う。
        setAppDirectoryEnvironment();
        forwardLaunchOptions();
        super.onCreate(savedInstanceState);
        hideSystemBars();
        // 音量キーは常にメディアの音量（ゲームの音が属する STREAM_MUSIC）を上げ下げする。指定しないと
        // 何も鳴っていない瞬間の対象が端末の既定（端末によっては着信音量）になるため固定する。
        setVolumeControlStream(AudioManager.STREAM_MUSIC);
        audioFocus = new AudioFocusController(this);
        // 描画面（SurfaceView）は super.onCreate の中で作られる。以降、安全領域・回転の変化を知らせる。
        screenReporter.attach(mSurfaceView);
    }

    /** 前面に来た。音声フォーカスを要求する（得られるまでネイティブは音声を止めたまま）。 */
    @Override
    protected void onResume() {
        super.onResume();
        audioFocus.request();
    }

    /** 前面を離れる。音声フォーカスを手放す（ネイティブは音声を止める）。 */
    @Override
    protected void onPause() {
        super.onPause();
        audioFocus.abandon();
    }

    /**
     * WindowInsets（システムバー・切り欠き）が変わった。GameActivity の処理（IME 等）の後に、
     * レイアウトが確定してから安全領域をネイティブへ知らせる。
     */
    @Override
    public WindowInsetsCompat onApplyWindowInsets(View view, WindowInsetsCompat insets) {
        WindowInsetsCompat result = super.onApplyWindowInsets(view, insets);
        screenReporter.reportAfterLayout();
        return result;
    }

    /** 描画面のレイアウトが確定した（回転・リサイズの後に来る）。大きさ・安全領域・回転を知らせる。 */
    @Override
    public void onGlobalLayout() {
        super.onGlobalLayout();
        screenReporter.report();
    }

    /**
     * 構成が変わった（回転など。マニフェストの configChanges で Activity は作り直されない）。
     * レイアウトがまだ前の向きなら報告は onGlobalLayout に任せる（ScreenReporter.reportAfterRotation）。
     */
    @Override
    public void onConfigurationChanged(Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        screenReporter.reportAfterRotation();
    }

    /**
     * 環境変数 TMPDIR をアプリのキャッシュフォルダ、HOME をアプリのデータフォルダ（files）へ向ける。
     *
     * <p>Android のアプリプロセスではこれらが設定されていない（zygote から受け継ぐ環境に無い。API 35 の
     * エミュレータで /proc/&lt;pid&gt;/environ を見て確認）。段階B で載せる .NET は
     * Path.GetTempPath() が TMPDIR（無ければ Android に無い /tmp/）を、ユーザーフォルダ系の API が HOME を使い、
     * Rust の std::env::temp_dir() も TMPDIR（無ければアプリから書けない /data/local/tmp）を使うため、
     * 起動時に向けておく（docs/android.md §11.2・§14）。</p>
     *
     * <p>ネイティブ側で設定しないのは、環境変数の書き換えが他スレッドの getenv と競合するため
     * （Rust 2024 では std::env::set_var が unsafe）。ネイティブのスレッドが 1 本も無い
     * super.onCreate の前にここで済ませる。失敗しても起動は続ける（ログだけ残す）。</p>
     */
    private void setAppDirectoryEnvironment() {
        try {
            Os.setenv(TEMP_DIR_ENV, getCacheDir().getAbsolutePath(), true);
            Os.setenv(HOME_DIR_ENV, getFilesDir().getAbsolutePath(), true);
            // 同梱 .NET の診断機能を止める（段階B。理由は DOTNET_DIAGNOSTICS_ENV のコメント）。
            Os.setenv(DOTNET_DIAGNOSTICS_ENV, DOTNET_DIAGNOSTICS_DISABLED, true);
        } catch (ErrnoException e) {
            Log.w(LOG_TAG, "環境変数 " + TEMP_DIR_ENV + " / " + HOME_DIR_ENV + " / " + DOTNET_DIAGNOSTICS_ENV
                    + " を設定できませんでした: " + e);
        }
    }

    /**
     * 起動の Intent の「seed.」で始まる文字列の extra を JSON 1 つにまとめ、ネイティブへ渡す（段階C-3）。
     *
     * <p>例: {@code am start … --es seed.scene scenes/Second.scene} → {@code {"scene":"scenes/Second.scene"}}。
     * どのキーを使うか（今は scene＝起動するシーン）はネイティブ側（engine::platform::launch_options）が決め、
     * ここは名前を見ずにそのまま渡す（オプションを足すたびに Java を直さないため）。</p>
     *
     * <p>デバッグ版の APK（開発用。SeedAndroid・エディタが作るもの）だけで行う。配布版では他のアプリからの
     * Intent でゲームの途中のシーンへ飛べてしまわないよう、何も渡さない（常に開始シーン）。
     * 失敗しても起動は続ける（ログだけ残す）。</p>
     */
    private void forwardLaunchOptions() {
        if ((getApplicationInfo().flags & ApplicationInfo.FLAG_DEBUGGABLE) == 0) {
            return;
        }
        JSONObject options = new JSONObject();
        Intent intent = getIntent();
        Bundle extras = intent != null ? intent.getExtras() : null;
        if (extras != null) {
            for (String key : extras.keySet()) {
                if (!key.startsWith(LAUNCH_OPTION_EXTRA_PREFIX)) {
                    continue;
                }
                String value = extras.getString(key);
                if (value == null) {
                    Log.w(LOG_TAG, "起動オプション " + key + " は文字列ではないため渡しません（am start の --es で指定してください）");
                    continue;
                }
                try {
                    options.put(key.substring(LAUNCH_OPTION_EXTRA_PREFIX.length()), value);
                } catch (JSONException e) {
                    Log.w(LOG_TAG, "起動オプション " + key + " を JSON にできませんでした: " + e);
                }
            }
        }
        try {
            nativeSetLaunchOptions(options.toString().getBytes(StandardCharsets.UTF_8));
        } catch (UnsatisfiedLinkError e) {
            // 古い libSEED.so（関数が無い）でも起動は続ける（開始シーンで起動する）。
            Log.w(LOG_TAG, "nativeSetLaunchOptions を呼べませんでした: " + e);
        }
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        // 通知の引き下ろし等でシステムバーが出た後、フォーカスが戻ったら再び隠す。
        if (hasFocus) {
            hideSystemBars();
        }
    }

    /**
     * Activity の破棄時はプロセスごと終了する（段階0 の方針）。
     *
     * <p>理由は 2 つ。</p>
     * <ol>
     *   <li>GameActivity の onDestroy は、ネイティブ側の android_main が返るまで UI スレッドで待つ。
     *       ところが winit 0.30 は破棄通知（onDestroy）をアプリへ渡さないため、エンジンの
     *       イベントループは終わらず、そのまま super.onDestroy() を呼ぶと UI スレッドが永久に待って
     *       ANR になる。</li>
     *   <li>winit の EventLoop はプロセス内で 1 度しか作れないため、同じプロセスで Activity が
     *       作り直されてもエンジンを再起動できない。</li>
     * </ol>
     * <p>回転などの構成変更ではマニフェストの configChanges により Activity は作り直されないので、
     * ここへ来るのは「アプリを閉じる」ときだけになる。段階A 以降で正式な終了処理に置き換える。</p>
     *
     * <p>プロセスを終える前に、ネイティブのセーブの未書き出し分を同期で書き出す（nativeFlushSaveData）。
     * winit がアプリへ破棄を知らせないため、ここがエンジンの「終了時の保存」に当たる唯一の経路。
     * 通常はバックグラウンドへ回った時点（onStop のウィンドウ破棄 → suspended）で書き出し済みで、ここは保険。</p>
     */
    @Override
    protected void onDestroy() {
        Log.i(LOG_TAG, "MainActivity.onDestroy (isFinishing=" + isFinishing()
                + ", isChangingConfigurations=" + isChangingConfigurations()
                + ") → セーブを書き出してプロセスを終了します");
        // 表示の変化の購読をやめる（プロセスごと終えるので実害は無いが、万一戻った場合に備える）。
        screenReporter.detach();
        try {
            nativeFlushSaveData();
        } catch (UnsatisfiedLinkError e) {
            // 古い libSEED.so（関数が無い）でもプロセスは必ず終える。
            Log.w(LOG_TAG, "nativeFlushSaveData を呼べませんでした: " + e);
        }
        Process.killProcess(Process.myPid());
        // killProcess は通常戻らない。万一戻った場合に備えて本来の後始末へ進む。
        super.onDestroy();
    }

    /** ステータスバー・ナビゲーションバーを隠す（端からのスワイプで一時的に出せる）。 */
    private void hideSystemBars() {
        WindowInsetsControllerCompat controller =
                WindowCompat.getInsetsController(getWindow(), getWindow().getDecorView());
        controller.setSystemBarsBehavior(
                WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
        controller.hide(WindowInsetsCompat.Type.systemBars());
    }
}
