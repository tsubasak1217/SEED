// ============================================================
//  MainActivity.java — SEED ランタイムの Android エントリ Activity（段階0）
//
//  GameActivity（AGDK）を継承するだけの薄いクラス。描画・入力・ゲームループはすべて
//  ネイティブ側（libSEED.so の android_main → エンジン）で動き、ここでは
//    ・ネイティブライブラリの読み込み
//    ・環境変数 TMPDIR / HOME をアプリのフォルダへ向け、同梱 .NET の診断機能を止める（理由は setAppDirectoryEnvironment のコメント）
//    ・起動の Intent の「seed.」で始まる文字列の extra（起動するシーン等）を JSON にしてネイティブへ渡す
//      （デバッグ版の APK だけ。理由は forwardLaunchOptions のコメント。段階C-3）
//    ・システムバーの既定の出し方（隠す＝ゲーム向けの既定／出す＝アプリ向け。プロジェクト設定 android.system_bars。
//      中身は SystemBarsController。契機の受け口だけここ。W1-2）と、スクリプトからの実行中の切り替えの受け口
//      （platform/window/SystemBarsHost。SEED.Platform の Window.SetSystemBarsVisible。W1-6）
//    ・安全領域と画面の回転をネイティブへ知らせる（中身は ScreenReporter。契機の受け口だけここ）
//    ・音量キーの対象をメディアの音量にし、音声フォーカスを前面で要求・前面を離れるときに手放す
//      （中身は AudioFocusController。契機の受け口だけここ）
//    ・アプリのプラットフォーム機能（SEED.Platform）の JNI の入口の準備（中身は platform/SeedPlatform。W1-1。
//      :seed_platform のプロセスはここでは起動しない。最初の呼び出しまで遅らせる）
//    ・起動理由（中身は platform/LaunchReason。W1-4a）: onCreate（super.onCreate の前）と onNewIntent で Intent を見て、
//      信頼できる入口 PlatformEntry（exported=false の activity-alias）経由なら起動理由を読み、目覚ましの鳴動なら
//      ロック画面の上に出して画面を点ける。onNewIntent ではイベント platform.launch を流す
//    ・権限（中身は platform/permission/PermissionLifecycle。W1-5）: onResume で設定の画面から戻った要求に結果を返し、
//      権限の状態が前回と違えば platform.permission_changed を流す。onRequestPermissionsResult で実行時の確認の画面の結果を渡す
//    ・センサー（中身は platform/sensor/SensorFeeds。W1-8）: onPause でスクリプトが始めたセンサーの登録を外し（背面で電池を使わない）、
//      onResume で登録し直す
//    ・鳴動の音量の後始末（中身は platform/LeftoverVolumeNudge。W1-7）: onResume で、:seed_platform が戻せずに残した
//      force_volume の前の音量があれば、前面にいる今のうちに戻させる（Android 17 は背面からの音量の変更を無視する）
//    ・Activity 破棄時のセーブ書き出し（JNI）とプロセス終了（理由は onDestroy のコメント）
//    ・アプリ基盤 W2-0 のスパイク（文字入力の通知の観察。中身は spike/ImeSpikeLog。デバッグ版の APK を seed.ui_spike に ime で
//      起動したときだけ。既定では各受け口は super を呼ぶだけで従来どおり）
//    ・描画を止めている間（render_policy の on_demand。W2-10a）の起こし: 文字入力の受け口（stateChanged など）は winit が
//      WindowEvent にしないので、redraw/RedrawWaker でネイティブのイベントループを起こす（on_demand でなければ理由を積むだけ）
//    ・タッチの時刻の控え（中身は input/TouchTimeline。2026-09-29）: processMotionEvent で、GameActivity の glue へ渡す前に
//      MotionEvent の時刻と履歴をネイティブへ送る（winit はどちらも捨てるので。ジェスチャーの速度の推定に使う。docs/input_gestures.md §5）
//    ・予測型の戻る（中身は back/BackCallbackController。W2 の手直し P1-3・docs/android.md §25.18）: プロジェクト設定
//      android.predictive_back が true の APK で API 33 以上のときだけ、onCreate（super.onCreate の後）で戻るのコールバックを登録し、
//      スクリプトの App.SetBackCallbackEnabled（platform/app/BackCallbackHost の口）で出し入れする。無効なら何もしない（従来の戻るキー）
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
import android.view.MotionEvent;
import android.view.View;

import java.nio.charset.StandardCharsets;

import org.json.JSONException;
import org.json.JSONObject;

import androidx.core.graphics.Insets;
import androidx.core.view.WindowInsetsCompat;

import com.google.androidgamesdk.GameActivity;
import com.google.androidgamesdk.gametextinput.State;

import com.seedengine.runtime.back.BackCallbackController;
import com.seedengine.runtime.input.TouchTimeline;
import com.seedengine.runtime.platform.LaunchReason;
import com.seedengine.runtime.platform.LeftoverVolumeNudge;
import com.seedengine.runtime.platform.SeedPlatform;
import com.seedengine.runtime.platform.app.BackCallbackHost;
import com.seedengine.runtime.platform.app.NightMode;
import com.seedengine.runtime.platform.permission.PermissionLifecycle;
import com.seedengine.runtime.platform.sensor.SensorFeeds;
import com.seedengine.runtime.platform.window.SystemBarsHost;
import com.seedengine.runtime.redraw.RedrawWaker;
import com.seedengine.runtime.spike.ImeSpikeLog;

/**
 * SEED ランタイムの唯一の Activity。
 *
 * <p>ネイティブライブラリ名はマニフェストの {@code android.app.lib_name}（GameActivity が
 * onCreate で読む）と一致させてある。Cargo 側の出力名 libSEED.so（runtime/android/native の
 * [lib] name）とも同じ。</p>
 */
public class MainActivity extends GameActivity implements SystemBarsHost, BackCallbackHost {

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

    /** システムバーの既定の出し方（UI スレッド専用。リソースを読むので onCreate で作る。W1-2）。 */
    private SystemBarsController systemBars;

    /**
     * MotionEvent の時刻と履歴の控えをネイティブへ送る係（UI スレッド専用。アプリの情報を読むので onCreate で作る。
     * processMotionEvent から使う。2026-09-29）。
     */
    private TouchTimeline touchTimeline;

    /**
     * 予測型の戻る（UI スレッド専用。リソースと起動の Intent を読むので onCreate で作る。W2 の手直し P1-3）。
     * 予測型の戻るが無効な APK・端末でも作る（何も登録しない）。
     */
    private BackCallbackController backCallbacks;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        // タッチの時刻の控え: 描画面（super.onCreate が作る SurfaceView）が触れられるより前に用意する
        touchTimeline = new TouchTimeline(this);
        // super.onCreate がネイティブ側（android_main のスレッド）を起動するので、その前に行う。
        setAppDirectoryEnvironment();
        forwardLaunchOptions();
        // W2-0 のスパイク: seed.ui_spike に ime があるデバッグ版の起動だけ、文字入力の通知を logcat へ出す（既定で無効）
        ImeSpikeLog.configure(this);
        // SEED.Platform の JNI の入口を用意し、ネイティブへ SeedPlatform のクラスを渡す（エンジンが最初のフレームから
        // IsSupported を正しく読めるよう android_main より前に）。:seed_platform は呼ばない（プロセスの起動の約 120 ms を
        // ここで待たない。つなぐのはスクリプトが最初に呼んだとき・背面のスレッドで。platform/PlatformConnection）。
        SeedPlatform.init(this);
        // 端末の明暗の設定の起動時の値を覚える（W2-9。onConfigurationChanged で変わったときだけ platform.ui_mode_changed を流す）
        NightMode.remember(getResources().getConfiguration());
        // 起動理由（PlatformEntry 経由の目覚まし・通知の操作か、ランチャーか）。目覚ましの鳴動なら、最初のフレームより前に
        // ロック画面の上に出して画面を点ける（setShowWhenLocked・setTurnScreenOn。W1-4a）
        LaunchReason.onCreate(this);
        super.onCreate(savedInstanceState);
        // システムバーの既定（プロジェクト設定 android.system_bars。隠す＝従来のゲーム・出す＝アプリ）。
        systemBars = new SystemBarsController(this);
        systemBars.applyDefault();
        Log.i(LOG_TAG, "システムバー: " + (systemBars.isVisibleByDefault() ? "出したまま（system_bars=visible）" : "隠す（system_bars=hidden）"));
        // 音量キーは常にメディアの音量（ゲームの音が属する STREAM_MUSIC）を上げ下げする。指定しないと
        // 何も鳴っていない瞬間の対象が端末の既定（端末によっては着信音量）になるため固定する。
        setVolumeControlStream(AudioManager.STREAM_MUSIC);
        audioFocus = new AudioFocusController(this);
        // 描画面（SurfaceView）は super.onCreate の中で作られる。以降、安全領域・回転の変化を知らせる。
        screenReporter.attach(mSurfaceView);
        // 予測型の戻る（W2 の手直し P1-3）: 有効（android.predictive_back が true の APK・API 33 以上）なら、アプリが戻るを受ける状態で
        // 始める（自分のコールバックを登録。SEED.UI を使わないゲームも Escape で戻るを受けられる）。無効なら何もしない（従来の戻るキー）
        backCallbacks = new BackCallbackController(this);
        backCallbacks.attach();
    }

    /**
     * 起動済みの Activity に Intent が届いた（launchMode="singleTask" なので、目覚まし・通知の操作で開き直されたときもここ）。
     * getIntent が新しい Intent を返すように差し替え、起動理由を決め直してイベント platform.launch を流す（W1-4a）。
     */
    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        LaunchReason.onNewIntent(this, intent);
    }

    /**
     * 前面に来た。音声フォーカスを要求する（得られるまでネイティブは音声を止めたまま）。
     * 権限の設定の画面から戻ったときの結果と、権限の状態の変化の知らせもここ（W1-5。中身は PermissionLifecycle）。
     * スクリプトが始めていたセンサーを登録し直す（W1-8。中身は SensorFeeds）。
     * 戻せずに残した鳴動の音量があれば :seed_platform に戻させる（W1-7。中身は LeftoverVolumeNudge。無ければファイルを 1 つ見るだけ）。
     */
    @Override
    protected void onResume() {
        super.onResume();
        audioFocus.request();
        PermissionLifecycle.onResume(this);
        SensorFeeds.onHostResumed();
        LeftoverVolumeNudge.onHostResumed(this);
    }

    /**
     * 実行時の許可の確認の画面の結果（W1-5。SEED.Platform の Permissions.Request が Activity.requestPermissions で出したもの）。
     * super（androidx の ActivityResultRegistry へ配る）の後に PermissionLifecycle へ渡す（自分の要求コードのときだけ処理される）。
     * 非推奨の注意を抑えるのは、androidx の ComponentActivity がこの受け口を非推奨（Activity Result API を推す）にしているため。
     * Activity Result API は onCreate の間に登録が要り、エンジンのスレッドから好きな時に求める今の作りに合わないので、
     * プラットフォームの Activity.requestPermissions とこの受け口を使う。
     */
    @Override
    @SuppressWarnings("deprecation")
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        PermissionLifecycle.onRequestPermissionsResult(this, requestCode, permissions, grantResults);
    }

    /**
     * 前面を離れる。音声フォーカスを手放す（ネイティブは音声を止める）。
     * センサーの登録を外す（W1-8。背面で電池を使わない。start の状態は残り、onResume で登録し直す）。
     */
    @Override
    protected void onPause() {
        super.onPause();
        audioFocus.abandon();
        SensorFeeds.onHostPaused();
    }

    /**
     * WindowInsets（システムバー・切り欠き）が変わった。GameActivity の処理（IME 等）の後に、
     * レイアウトが確定してから安全領域をネイティブへ知らせる。
     */
    @Override
    public WindowInsetsCompat onApplyWindowInsets(View view, WindowInsetsCompat insets) {
        WindowInsetsCompat result = super.onApplyWindowInsets(view, insets);
        screenReporter.reportAfterLayout();
        // W2-0 のスパイク: IME の範囲（キーボードの高さ）の取り方の確かめ（既定では何もしない）
        ImeSpikeLog.onWindowInsets(insets);
        return result;
    }

    /**
     * 文字入力の状態が変わった（GameTextInput の InputConnection から。UI スレッド）。
     * super がネイティブへ渡す（onTextInputEventNative）。W2-0 のスパイクでは中身を logcat へ出す（既定では何もしない）。
     */
    @Override
    public void stateChanged(State newState, boolean dismissed) {
        super.stateChanged(newState, dismissed);
        ImeSpikeLog.onState(newState, dismissed);
        // 描画を止めている間（render_policy の on_demand）でも描き直すよう、ネイティブのイベントループを起こす（W2-10a）
        RedrawWaker.requestRedraw(RedrawWaker.REASON_TEXT_INPUT);
    }

    /**
     * 完了などのアクションが来た（UI スレッド）。super がネイティブへ渡す（onEditorActionNative）が、
     * winit はそれを読み捨てる（docs/app_platform_roadmap.md §3.8）。W2-0 のスパイクでは logcat へ出す（既定では何もしない）。
     */
    @Override
    public void onEditorAction(int action) {
        super.onEditorAction(action);
        ImeSpikeLog.onEditorAction(action);
        RedrawWaker.requestRedraw(RedrawWaker.REASON_TEXT_INPUT);
    }

    /** ソフトキーボードの表示が変わった（UI スレッド）。W2-0 のスパイクでは logcat へ出す（既定では何もしない）。 */
    @Override
    public void onSoftwareKeyboardVisibilityChanged(boolean visible) {
        super.onSoftwareKeyboardVisibilityChanged(visible);
        ImeSpikeLog.onKeyboardVisibility(visible);
        RedrawWaker.requestRedraw(RedrawWaker.REASON_TEXT_INPUT);
    }

    /** IME の占める範囲が変わった（UI スレッド）。W2-0 のスパイクでは logcat へ出す（既定では何もしない）。 */
    @Override
    public void onImeInsetsChanged(Insets insets) {
        super.onImeInsetsChanged(insets);
        ImeSpikeLog.onImeInsets(insets);
        RedrawWaker.requestRedraw(RedrawWaker.REASON_TEXT_INPUT);
    }

    /**
     * 描画面の指・マウスの MotionEvent（GameActivity の SurfaceView の OnTouchListener・OnGenericMotionListener・
     * captured pointer から呼ばれる。UI スレッド）。super が GameActivity の glue へ渡し、ネイティブの winit が Touch にする。
     *
     * <p>super を呼ぶ<b>前に</b>、同じ MotionEvent の時刻と履歴を TouchTimeline でネイティブへ送る（winit は時刻も履歴も捨てるので。
     * docs/input_gestures.md §5）。前に送るのは、ネイティブのスレッドが winit の Touch を作るより先に控えが箱にあるようにするため
     * （後に送ると、glue が起こしたネイティブのスレッドの方が先に Touch を処理して、控えが間に合わないことがある）。
     * 控えは例外を握りつぶすので、glue への受け渡し（タッチの本流）は必ず行う。</p>
     *
     * <p>glue が受け取らないとき（ネイティブが破棄された後。GameActivity の isNativeDestroyed は private で読めず、公開の
     * getGameActivityNativeHandle は 4.4.0 の実装〈javap で確かめた〉が生きている間 0 を返すので判定に使えない）は、控えだけが
     * 箱に残る。箱は上限つきで、後の Touch が一致すればその前の控えとして捨てられるので、害は無い。この Activity は破棄と同時に
     * プロセスを終える（onDestroy）ので、その状態はほぼ起きない。</p>
     */
    @Override
    protected boolean processMotionEvent(MotionEvent event) {
        if (touchTimeline != null) {
            touchTimeline.record(event);
        }
        return super.processMotionEvent(event);
    }

    /** 描画面のレイアウトが確定した（回転・リサイズの後に来る）。大きさ・安全領域・回転を知らせる。 */
    @Override
    public void onGlobalLayout() {
        super.onGlobalLayout();
        screenReporter.report();
    }

    /**
     * 構成が変わった（回転・明暗の設定など。マニフェストの configChanges で Activity は作り直されない）。
     * レイアウトがまだ前の向きなら報告は onGlobalLayout に任せる（ScreenReporter.reportAfterRotation）。
     * 夜の表示の bit が変わったら platform.ui_mode_changed を流す（W2-9。SEED.UI のテーマの「端末に従う」）。
     */
    @Override
    public void onConfigurationChanged(Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        screenReporter.reportAfterRotation();
        NightMode.onConfigurationChanged(newConfig);
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
        // 通知の引き下ろし等でシステムバーが出た後、フォーカスが戻ったら再び隠す（今の状態が「隠す」のときだけ。
        // 今の状態は起動時の既定か、スクリプトの Window.SetSystemBarsVisible で切り替えたもの。SystemBarsController）。
        if (hasFocus && systemBars != null) {
            systemBars.onFocusRegained();
        }
    }

    /**
     * システムバーを出す・隠すを実行中に切り替える（W1-6。SEED.Platform の window.set_system_bars_visible を受けた
     * platform/local/SystemBarsVisibleCommand が UI スレッドで呼ぶ）。以後のフォーカスが戻ったときの隠し直しもこの状態に従う。
     * 安全領域は WindowInsets の配り直し（onApplyWindowInsets → ScreenReporter）でネイティブへ知らせ直される。
     *
     * @param visible 出すなら true、隠すなら false
     */
    @Override
    public void setSystemBarsVisible(boolean visible) {
        if (systemBars == null) {
            // onCreate の途中（super.onCreate の前）に届くことは無い見込み（命令は UI スレッドへ投げられ、onCreate の後に動く）
            Log.w(LOG_TAG, "システムバーの切り替えが onCreate の前に届いたので無視しました");
            return;
        }
        systemBars.setVisible(visible);
        Log.i(LOG_TAG, "システムバー: " + (visible ? "出す" : "隠す") + "（Window.SetSystemBarsVisible）");
    }

    /**
     * 予測型の戻るが有効か（W2 の手直し P1-3。platform/local/BackCallbackCommand がエンジンのスレッドから呼ぶ。リソースを読むだけ）。
     *
     * @return APK の印 seed_predictive_back が true で API 33 以上なら true
     */
    @Override
    public boolean isPredictiveBackEnabled() {
        return BackCallbackController.isEnabledFor(this);
    }

    /**
     * アプリが戻るを受けるかを切り替える（W2 の手直し P1-3。SEED.Platform の app.set_back_callback を受けた
     * platform/local/BackCallbackCommand が UI スレッドで呼ぶ）。中身は back/BackCallbackController。
     *
     * @param on 受ける層があるなら true、無い（根）なら false
     */
    @Override
    public void setAppHandlesBack(boolean on) {
        if (backCallbacks == null) {
            // onCreate の途中に届くことは無い見込み（命令は UI スレッドへ投げられ、onCreate の後に動く）
            Log.w(LOG_TAG, "予測型の戻るの切り替えが onCreate の前に届いたので無視しました");
            return;
        }
        backCallbacks.setAppHandlesBack(on);
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
}
