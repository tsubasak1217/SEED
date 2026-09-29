// ============================================================
//  BackCallbackController.java — 予測型の戻る（opt-in）の本体（MainActivity は受け口だけ。W2 の手直し P1-3・docs/android.md §25.18）
//
//  【有効になる条件】プロジェクト設定 android.predictive_back が true の APK（SeedAndroid がマニフェストの
//  android:enableOnBackInvokedCallback を "true" にし、res の印 R.bool.seed_predictive_back を true にする）で、端末が API 33 以上。
//  どちらかが欠ければ何もしない（何も登録しない＝従来の KEYCODE_BACK → GameActivity → glue → winit → Escape の道がそのまま。
//  predictive_back の無いプロジェクト〈WarashibeFishing など〉はマニフェストが "false" なので、システムの戻るも従来どおり）。
//
//  【流れ】
//    MainActivity.onCreate（super.onCreate の後）→ new + attach: 有効なら「アプリが戻るを受ける」状態で始める（自分のコールバックを
//      PRIORITY_DEFAULT で登録。SEED.UI を使わないゲームも今どおり Escape で戻るを受けられる）
//    スクリプトの App.SetBackCallbackEnabled(on) → app.set_back_callback → local/BackCallbackCommand → UI スレッドで
//      MainActivity.setAppHandlesBack → setAppHandlesBack（受ける層があるか。登録と外し方は API ごとに BackDispatcherApi33）
//    システムの戻るの手ぶり → 自分のコールバック（BackAnimationCallbackApi34 / BackInvokedCallbackApi33）→ BackGestureReporter
//      （platform.back_* のイベント・確定で合成の KEYCODE_BACK → BackKeyInjector → Escape）
//  【クラスの分け方】API 33 未満の端末でも読み込めるよう、android.window の型はこのクラスに持たず、API 33 以上の端末でだけ
//  BackDispatcherApi33 を作る（その中で 34 以上・36 以上の型を持つクラスを SDK_INT で分けて使う）。
//  【ログ】タグ SEED・行頭 [SEED BACK]。有効か無効かを起動時に 1 行、受ける層の切り替えを変わったときだけ 1 行、手ぶりの始まり・
//  取り消し・確定を 1 行ずつ（進み具合は出さない）。
//  MainActivity は破棄と同時にプロセスを終える（MainActivity.onDestroy）ので、コールバックを外す後始末はしない。
// ============================================================

package com.seedengine.runtime.back;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.util.Log;

import com.seedengine.runtime.R;

import java.util.Set;

/**
 * 予測型の戻るの本体（UI スレッド専用。MainActivity が onCreate で 1 つ作る）。
 */
public final class BackCallbackController {

    /** logcat のタグ（MainActivity・ネイティブと同じ。`adb logcat -s SEED` で一緒に見える）。 */
    static final String LOG_TAG = "SEED";

    /** ログの行頭（`adb logcat | grep "SEED BACK"` で探す）。 */
    static final String LOG_PREFIX = "[SEED BACK] ";

    /** ランチャーの起動の Intent の category の数（CATEGORY_LAUNCHER の 1 つだけ。システムの判定と同じかそれより厳しくする）。 */
    private static final int LAUNCHER_CATEGORY_COUNT = 1;

    /** 予測型の戻るが有効か（印が true かつ API 33 以上。作った後は変わらない）。 */
    private final boolean enabled;

    /** コールバックの登録と外し（有効なときだけ。無効なら null）。 */
    private final BackDispatcherApi33 dispatcher;

    /** 今「アプリが戻るを受ける」状態か（attach の前は false）。 */
    private boolean appHandlesBack;

    /**
     * attach 済みか（attach の前の切り替えは無視する。命令は UI スレッドへ投げられ onCreate の後に動くので、実際には来ない見込み）。
     */
    private boolean attached;

    /**
     * 作る（MainActivity.onCreate の super.onCreate の後。まだ何も登録しない）。
     *
     * @param activity MainActivity（コールバックを登録し、合成の KEYCODE_BACK を渡す先）
     */
    public BackCallbackController(Activity activity) {
        this.enabled = isEnabledFor(activity);
        // isEnabledFor は API 33 以上を含むが、lint（NewApi）が読めるよう SDK_INT の比べをここにも書く
        if (enabled && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            BackGestureReporter reporter = new BackGestureReporter(new BackKeyInjector(activity));
            // 起動の Intent（onCreate の時点の getIntent。singleTask の根なのでタスクの最初の Intent）でランチャーの起動かを決める
            this.dispatcher = new BackDispatcherApi33(activity, reporter, isLauncherIntent(activity.getIntent()));
        } else {
            this.dispatcher = null;
        }
    }

    /**
     * 予測型の戻るが有効か（APK の印 R.bool.seed_predictive_back が true で、端末が API 33 以上）。どのスレッドから呼んでもよい。
     *
     * @param context リソースを読む Context
     * @return 有効なら true
     */
    public static boolean isEnabledFor(Context context) {
        return Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU
                && context.getResources().getBoolean(R.bool.seed_predictive_back);
    }

    /**
     * 起動の Intent がランチャーの起動か（ACTION_MAIN で、category が CATEGORY_LAUNCHER の 1 つだけ、data と type が無い）。
     *
     * <p>システムが「根の Activity を戻るで finish せず背面へ回す」かの判定（ActivityRecord.isMainIntent とホームからの起動か。
     * 記憶による）と同じか、それより厳しくする（厳しい側に外れても、自分のコールバックを残して Escape → App.MoveTaskToBack で
     * 背面へ回るだけで困らない。緩い側に外れると finish → プロセスの終了になる）。</p>
     *
     * @param intent 起動の Intent（null 可）
     * @return ランチャーの起動なら true
     */
    static boolean isLauncherIntent(Intent intent) {
        if (intent == null) {
            return false;
        }
        Set<String> categories = intent.getCategories();
        return Intent.ACTION_MAIN.equals(intent.getAction())
                && categories != null
                && categories.size() == LAUNCHER_CATEGORY_COUNT
                && categories.contains(Intent.CATEGORY_LAUNCHER)
                && intent.getData() == null
                && intent.getType() == null;
    }

    /**
     * 予測型の戻るが有効か（作ったときに決まる）。
     *
     * @return 有効なら true
     */
    public boolean isEnabled() {
        return enabled;
    }

    /**
     * 始める（MainActivity.onCreate の最後に 1 回）。有効ならアプリが戻るを受ける状態（自分のコールバックを登録）にし、
     * どちらの道で戻るを受けるかをログに 1 行出す。
     */
    public void attach() {
        if (!enabled) {
            Log.i(LOG_TAG, LOG_PREFIX + "予測型の戻る: 使わない（" + disabledReason() + "）。戻るキーは従来どおり KEYCODE_BACK → Escape");
            return;
        }
        attached = true;
        Log.i(LOG_TAG, LOG_PREFIX + "予測型の戻る: 有効（API " + Build.VERSION.SDK_INT + "）");
        apply(true);
    }

    /**
     * アプリが戻るを受けるかを切り替える（UI スレッドで呼ぶこと。MainActivity.setAppHandlesBack から）。
     * 無効なとき・同じ状態への切り替えは何もしない。
     *
     * @param on 受ける層があるなら true、無い（根）なら false
     */
    public void setAppHandlesBack(boolean on) {
        if (!enabled || !attached || on == appHandlesBack) {
            return;
        }
        apply(on);
    }

    /** 状態を変え、登録・外しを行い、1 行ログに残す。 */
    private void apply(boolean on) {
        appHandlesBack = on;
        String what = on ? dispatcher.handToApp() : dispatcher.handToSystem();
        Log.i(LOG_TAG, LOG_PREFIX + what);
    }

    /** 無効な理由（ログ用）。 */
    private static String disabledReason() {
        return Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU
                ? "API " + Build.VERSION.SDK_INT + " は予測型の戻るの API〈33〉より前"
                : "プロジェクト設定 android.predictive_back が無い・false";
    }
}
