// ============================================================
//  RedrawWaker.java — Java から「描く理由」をネイティブへ知らせ、描画を止めている間のイベントループを起こす（W2-10a）
//
//  【なぜ要るか】（docs/redraw_policy.md §3）
//  プロジェクト設定の render_policy が on_demand のとき、エンジンは描く理由の無いフレームが続いたら描画を止めて眠る。
//  入力の多くは winit の WindowEvent として届いて起こしてくれるが、文字入力（IME の本文・完了などのアクション・
//  キーボードの表示と高さ）は GameActivity の glue がルーパーを起こしても winit が WindowEvent にしない（読み捨てる）。
//  そこで MainActivity の IME の受け口がここを呼び、ネイティブ（runtime/android/native の redraw_waker.rs）が
//  engine::core::redraw::wake::raise で理由を積んでイベントループを起こす。
//
//  【呼んでよいスレッド】どれでもよい（ネイティブは原子変数と EventLoopProxy の送信だけ）。ふつうは UI スレッド。
//  【on_demand でないとき】ネイティブは理由を積むだけで何も起きない（毎フレーム描いているので）。
// ============================================================

package com.seedengine.runtime.redraw;

import android.util.Log;

/**
 * 描く理由をネイティブへ知らせる窓口（static のみ）。
 *
 * <p>理由の番号はネイティブの engine::core::redraw::reason の EXTERNAL_REASON_* と一致させる。</p>
 */
public final class RedrawWaker {

    /** 理由の番号: 文字入力（IME の本文・アクション・キーボードの表示と高さ）。 */
    public static final int REASON_TEXT_INPUT = 0;

    /** 理由の番号: 画面の変化。 */
    public static final int REASON_SCREEN = 1;

    /** 理由の番号: その他のアプリの状態の変化。 */
    public static final int REASON_SYSTEM_EVENT = 2;

    /** logcat のタグ（ネイティブのログは SEED タグの [SEED REDRAW]）。 */
    private static final String LOG_TAG = "SEEDRedraw";

    private RedrawWaker() {
    }

    /**
     * 描く理由を知らせる（描画を止めていればイベントループを起こす）。
     *
     * @param reason 理由の番号（REASON_*）
     */
    public static void requestRedraw(int reason) {
        try {
            nativeRequestRedraw(reason);
        } catch (UnsatisfiedLinkError e) {
            // 古い libSEED.so（関数が無い）でも Activity は動かし続ける（描画を止めていれば次の入力まで遅れるだけ）。
            Log.w(LOG_TAG, "nativeRequestRedraw を呼べませんでした: " + e);
        }
    }

    /**
     * ネイティブの受け口（runtime/android/native/src/redraw_waker.rs）。
     *
     * @param reason 理由の番号（REASON_*）
     */
    private static native void nativeRequestRedraw(int reason);
}
