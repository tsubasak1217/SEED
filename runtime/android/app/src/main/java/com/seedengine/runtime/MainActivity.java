// ============================================================
//  MainActivity.java — SEED ランタイムの Android エントリ Activity（段階0）
//
//  GameActivity（AGDK）を継承するだけの薄いクラス。描画・入力・ゲームループはすべて
//  ネイティブ側（libSEED.so の android_main → エンジン）で動き、ここでは
//    ・ネイティブライブラリの読み込み
//    ・全画面（システムバーを隠す）
//    ・Activity 破棄時のプロセス終了（理由は onDestroy のコメント）
//  だけを行う。全体像は docs/android.md。
// ============================================================

package com.seedengine.runtime;

import android.os.Bundle;
import android.os.Process;
import android.util.Log;

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

    static {
        // GameActivity も onCreate で読み込むが、失敗を最も早い段階で明確に出すためここでも読む
        // （2 回目の loadLibrary は何もしない）。
        System.loadLibrary(NATIVE_LIBRARY_NAME);
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        hideSystemBars();
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
     */
    @Override
    protected void onDestroy() {
        Log.i(LOG_TAG, "MainActivity.onDestroy (isFinishing=" + isFinishing()
                + ", isChangingConfigurations=" + isChangingConfigurations()
                + ") → プロセスを終了します");
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
