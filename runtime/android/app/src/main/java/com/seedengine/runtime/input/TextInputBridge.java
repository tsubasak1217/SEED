// ============================================================
//  TextInputBridge.java — 文字入力（GameTextInput）の知らせをネイティブへ渡す窓口（W2-6a。E-06 の決定）
//
//  【なぜ Java 側で受けるか】（docs/app_platform_roadmap.md §3.8.1・docs/ui_text_input.md §6）
//  GameActivity の文字入力の知らせ（状態の変化 stateChanged・完了などのアクション onEditorAction）はネイティブの glue で
//  「フラグ 1 つ」にまとめられ、android-activity の入力の列の TextEvent / TextAction として 1 度だけ取り出せるが、
//  winit 0.30 はそれを「知らない入力」として読み捨てる（I-1）。アクション（完了・次へ）は後から取り戻せない。
//  そこで MainActivity が GameActivity の受け口を上書きし（super を先に呼ぶ）、ここからネイティブ（runtime/android/native の
//  text_input/jni_receivers.rs）へ渡す。ネイティブはエンジンの箱へ積み、描画を止めていてもイベントループを起こす。
//
//  【渡し方】今の JNI の流儀（§1.2）: 本文は UTF-8 の byte[]（修正 UTF-8 と違い絵文字もそのまま読める）、添字は数値。
//  添字は Java の String の添字＝UTF-16 の単位のまま渡す（エンジンが UTF-8 の境界へ直す。I-6）。
//  IME の高さは WindowInsets の ime().bottom（画面の下端からの画素）。前と同じ値なら送らない（システムバーの変化でも配り直されるため）。
//  【呼んでよいスレッド】UI スレッド（GameActivity の受け口）。入力された本文はログへ出さない。
// ============================================================

package com.seedengine.runtime.input;

import android.util.Log;

import java.nio.charset.StandardCharsets;

import androidx.core.graphics.Insets;
import androidx.core.view.WindowInsetsCompat;

import com.google.androidgamesdk.gametextinput.State;

/** 文字入力の知らせをネイティブへ渡す窓口（static のみ）。 */
public final class TextInputBridge {

    /** logcat のタグ（ネイティブのログは SEED タグの [SEED TEXT INPUT]）。 */
    private static final String LOG_TAG = "SEEDTextInput";

    /** IME の高さの「まだ送っていない」を表す値（どの実際の高さとも違う負の値）。 */
    private static final int HEIGHT_NOT_SENT = -1;

    /** 最後に送った IME の高さ（UI スレッドだけが読み書きする）。 */
    private static int lastImeHeight = HEIGHT_NOT_SENT;

    /** 古い libSEED.so（関数が無い）で一度ログを出したか。 */
    private static boolean linkFailureLogged;

    private TextInputBridge() {
    }

    /**
     * 入力の状態が変わった（GameActivity.stateChanged。IME が本文・選択・変換中の区間を変えるたび。
     * ネイティブが set_text_input_state で差し替えたときも同じ状態が返ってくる）。
     *
     * @param state 新しい状態（添字は UTF-16 の単位。変換中でなければ composingRegion は -1）
     */
    public static void onState(State state) {
        if (state == null) {
            return;
        }
        String text = state.text != null ? state.text : "";
        try {
            nativeOnTextState(text.getBytes(StandardCharsets.UTF_8), state.selectionStart, state.selectionEnd,
                    state.composingRegionStart, state.composingRegionEnd);
        } catch (UnsatisfiedLinkError e) {
            logLinkFailure(e);
        }
    }

    /**
     * 完了などのアクションが来た（GameActivity.onEditorAction。IME のアクションのボタンか、1 行の入力でのハードウェアの Enter）。
     *
     * @param action EditorInfo の IME_ACTION_*（6 = 完了・5 = 次へ）
     */
    public static void onEditorAction(int action) {
        try {
            nativeOnEditorAction(action);
        } catch (UnsatisfiedLinkError e) {
            logLinkFailure(e);
        }
    }

    /**
     * ソフトキーボードの表示が変わった（GameActivity.onSoftwareKeyboardVisibilityChanged）。
     *
     * @param visible 見えているか
     */
    public static void onKeyboardVisibility(boolean visible) {
        try {
            nativeOnKeyboardVisibility(visible);
        } catch (UnsatisfiedLinkError e) {
            logLinkFailure(e);
        }
    }

    /**
     * WindowInsets が配り直された（MainActivity.onApplyWindowInsets）。IME の範囲の下端からの高さが前と違えば送る。
     * GameTextInput は窓を setDecorFitsSystemWindows(false) にするので、キーボードが出ても描画面は縮まない（I-9）。
     * 入力欄をキーボードの上へずらすのはエンジン側（SEED.UI の入力欄）。
     *
     * @param insets 配り直された WindowInsets
     */
    public static void onWindowInsets(WindowInsetsCompat insets) {
        if (insets == null) {
            return;
        }
        Insets ime = insets.getInsets(WindowInsetsCompat.Type.ime());
        int height = Math.max(0, ime.bottom);
        if (height == lastImeHeight) {
            return;
        }
        lastImeHeight = height;
        try {
            nativeOnImeHeight(height);
        } catch (UnsatisfiedLinkError e) {
            logLinkFailure(e);
        }
    }

    /** 古い libSEED.so（関数が無い）では 1 度だけログを出して続ける（Activity は動かし続ける）。 */
    private static void logLinkFailure(UnsatisfiedLinkError e) {
        if (!linkFailureLogged) {
            linkFailureLogged = true;
            Log.w(LOG_TAG, "文字入力の知らせのネイティブ関数を呼べませんでした（古い libSEED.so）: " + e);
        }
    }

    /** 状態の写し（runtime/android/native の text_input/jni_receivers.rs）。 */
    private static native void nativeOnTextState(byte[] textUtf8, int selectionStart, int selectionEnd,
            int composingStart, int composingEnd);

    /** 完了などのアクション。 */
    private static native void nativeOnEditorAction(int action);

    /** キーボードの表示。 */
    private static native void nativeOnKeyboardVisibility(boolean visible);

    /** IME の高さ（画素）。 */
    private static native void nativeOnImeHeight(int bottomPx);
}
