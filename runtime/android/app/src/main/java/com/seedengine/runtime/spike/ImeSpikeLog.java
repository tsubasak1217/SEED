// ============================================================
//  ImeSpikeLog.java — アプリ基盤 W2-0 のスパイク: 文字入力（GameTextInput）の Java 側の通知を logcat へ出す（既定で無効）
//
//  【なぜ Java 側で見るか】（docs/app_platform_roadmap.md §3.8）
//  GameActivity の文字入力の通知（状態の変化 stateChanged・完了などのアクション onEditorAction）はネイティブの
//  glue で「フラグ 1 つ」にまとめられ、android-activity の入力の列（input_events_iter）の TextEvent / TextAction として
//  1 度だけ取り出せる。ところが winit 0.30 はその列を自分で回し、TextEvent / TextAction を「知らない入力」として
//  読み捨てる。状態は text_input_state() で後から読めるが、アクション（完了・次へ）は取り戻せない。
//  そこで MainActivity が GameActivity の受け口を上書きし（super を必ず呼ぶ）、ここで中身を確かめる。
//  本番（W2-6）では同じ上書きから JNI でネイティブへ渡す想定。
//
//  【有効にする方法】デバッグ版の APK を am start … --es seed.ui_spike 'ime' で起動する。指定が無ければ何もしない
//  （各メソッドは最初に enabled を見て抜ける）。配布版（FLAG_DEBUGGABLE なし）では常に無効。
//  【注意】入力された文字列そのものを logcat へ出す（試作専用。本番の経路では出さない）。
// ============================================================

package com.seedengine.runtime.spike;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.util.Log;

import androidx.core.graphics.Insets;
import androidx.core.view.WindowInsetsCompat;

import com.google.androidgamesdk.gametextinput.State;

/** 文字入力の Java 側の通知を logcat（タグ SEEDImeSpike）へ出す試作（W2-0。既定で無効）。 */
public final class ImeSpikeLog {

    /** logcat のタグ（ネイティブ側の試作は SEED タグの [SEED IME SPIKE]）。 */
    private static final String LOG_TAG = "SEEDImeSpike";

    /** スパイクの指定を受け取る Intent の extra（ネイティブへ渡る seed.ui_spike と同じもの）。 */
    private static final String SPIKE_EXTRA = "seed.ui_spike";

    /** 文字入力の試作を有効にする項目（engine::core::ui_spike::config の ime と同じ綴り）。 */
    private static final String IME_ITEM = "ime";

    /** 指定の項目の区切り（カンマかセミコロン。ネイティブの書式と同じ）。 */
    private static final String ITEM_SEPARATOR_PATTERN = "[,;]";

    /** 有効か（onCreate で 1 度だけ決める。UI スレッドだけが読むが、念のため volatile）。 */
    private static volatile boolean enabled;

    private ImeSpikeLog() {
    }

    /**
     * 起動の Intent から有効かを決める（MainActivity.onCreate の super.onCreate より前に 1 度だけ呼ぶ）。
     *
     * @param activity 起動した Activity（デバッグ版かと Intent を見る）
     */
    public static void configure(Activity activity) {
        enabled = false;
        if ((activity.getApplicationInfo().flags & ApplicationInfo.FLAG_DEBUGGABLE) == 0) {
            return;
        }
        Intent intent = activity.getIntent();
        String spec = intent != null ? intent.getStringExtra(SPIKE_EXTRA) : null;
        if (spec == null) {
            return;
        }
        for (String item : spec.split(ITEM_SEPARATOR_PATTERN)) {
            if (IME_ITEM.equals(item.trim())) {
                enabled = true;
                Log.i(LOG_TAG, "文字入力の試作の通知を logcat へ出します（seed.ui_spike に ime）");
                return;
            }
        }
    }

    /**
     * 入力の状態が変わった（GameActivity.stateChanged。InputConnection がテキスト・選択・変換中の区間を変えるたび）。
     *
     * @param state     新しい状態（添字は Java の String の添字＝UTF-16 の単位）
     * @param dismissed IME が閉じたときの通知か
     */
    public static void onState(State state, boolean dismissed) {
        if (!enabled || state == null) {
            return;
        }
        String text = state.text != null ? state.text : "";
        Log.i(LOG_TAG, "stateChanged text=\"" + text + "\"（UTF-16 " + text.length()
                + "・符号位置 " + text.codePointCount(0, text.length()) + "） selection="
                + state.selectionStart + ".." + state.selectionEnd + " compose="
                + state.composingRegionStart + ".." + state.composingRegionEnd + " dismissed=" + dismissed);
    }

    /**
     * 完了などのアクションが来た（GameActivity.onEditorAction。ソフトキーボードのアクションのボタンか、
     * 1 行の入力でのハードウェアの Enter）。winit はこのアクションを読み捨てるので、本番もここから取る。
     *
     * @param action EditorInfo の IME_ACTION_*（6 = 完了・5 = 次へ）
     */
    public static void onEditorAction(int action) {
        if (!enabled) {
            return;
        }
        Log.i(LOG_TAG, "onEditorAction action=" + action);
    }

    /**
     * ソフトキーボードの表示が変わった（GameActivity が WindowInsets の IME の有無から推す）。
     *
     * @param visible 見えているか
     */
    public static void onKeyboardVisibility(boolean visible) {
        if (!enabled) {
            return;
        }
        Log.i(LOG_TAG, "onSoftwareKeyboardVisibilityChanged visible=" + visible);
    }

    /**
     * IME の占める範囲が変わった（GameTextInput の受け口。GameActivity 4.4.0 の既定の実装はログを出すだけ）。
     *
     * @param insets IME の範囲（px）
     */
    public static void onImeInsets(Insets insets) {
        if (!enabled || insets == null) {
            return;
        }
        Log.i(LOG_TAG, "onImeInsetsChanged bottom=" + insets.bottom + " (left=" + insets.left + " top=" + insets.top
                + " right=" + insets.right + ")");
    }

    /**
     * WindowInsets が配り直された（MainActivity.onApplyWindowInsets）。IME の範囲（下端からの高さ）と表示を出す。
     * 入力欄をキーボードの上へずらす（UC-5）のに使う値の取り方の確かめ。
     *
     * @param insets 配り直された WindowInsets
     */
    public static void onWindowInsets(WindowInsetsCompat insets) {
        if (!enabled || insets == null) {
            return;
        }
        Insets ime = insets.getInsets(WindowInsetsCompat.Type.ime());
        boolean visible = insets.isVisible(WindowInsetsCompat.Type.ime());
        Log.i(LOG_TAG, "WindowInsets ime.bottom=" + ime.bottom + " visible=" + visible);
    }
}
