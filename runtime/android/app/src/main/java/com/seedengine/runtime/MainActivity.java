// ============================================================
//  MainActivity.java — SEED ランタイムの Android エントリ Activity（段階0）
//
//  GameActivity（AGDK）を継承するだけの薄いクラス。描画・入力・ゲームループはすべて
//  ネイティブ側（libSEED.so の android_main → エンジン）で動き、ここでは
//    ・ネイティブライブラリの読み込み
//    ・環境変数 TMPDIR / HOME をアプリのフォルダへ向ける（理由は setAppDirectoryEnvironment のコメント）
//    ・全画面（システムバーを隠す）
//    ・安全領域と画面の回転をネイティブへ知らせる（中身は ScreenReporter。契機の受け口だけここ）
//    ・Activity 破棄時のセーブ書き出し（JNI）とプロセス終了（理由は onDestroy のコメント）
//  だけを行う。画面の向きの固定はマニフェスト（ビルド時にプロジェクト設定から決まる）。全体像は docs/android.md。
// ============================================================

package com.seedengine.runtime;

import android.content.res.Configuration;
import android.os.Bundle;
import android.os.Process;
import android.system.ErrnoException;
import android.system.Os;
import android.util.Log;
import android.view.View;

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

    static {
        // GameActivity も onCreate で読み込むが、失敗を最も早い段階で明確に出すためここでも読む
        // （2 回目の loadLibrary は何もしない）。
        System.loadLibrary(NATIVE_LIBRARY_NAME);
    }

    /**
     * 未書き出しのセーブデータを同期でディスクへ書き出す（libSEED.so の jni_exports.rs）。
     *
     * <p>プロセスを終える直前（onDestroy）の保険。通常はバックグラウンドへ回った時点
     * （ネイティブ側の suspended）で書き出し済みで、何も書かずに戻る。</p>
     */
    private static native void nativeFlushSaveData();

    /** 安全領域と画面の回転をネイティブへ知らせる係（UI スレッド専用）。 */
    private final ScreenReporter screenReporter = new ScreenReporter(this);

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        // super.onCreate がネイティブ側（android_main のスレッド）を起動するので、その前に行う。
        setAppDirectoryEnvironment();
        super.onCreate(savedInstanceState);
        hideSystemBars();
        // 描画面（SurfaceView）は super.onCreate の中で作られる。以降、安全領域・回転の変化を知らせる。
        screenReporter.attach(mSurfaceView);
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
        } catch (ErrnoException e) {
            Log.w(LOG_TAG, "環境変数 " + TEMP_DIR_ENV + " / " + HOME_DIR_ENV + " を設定できませんでした: " + e);
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
