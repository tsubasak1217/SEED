// ============================================================
//  BackDispatcherApi33.java — 窓の OnBackInvokedDispatcher へのコールバックの登録と外し（API 33 以上。W2 の手直し P1-3）
//
//  Activity.getOnBackInvokedDispatcher() と OnBackInvokedDispatcher（どちらも API 33）を持つので、API 33 以上の端末でだけ作る
//  （BackCallbackController が「予測型の戻るが有効＝印が true かつ SDK_INT ≥ 33」のときだけ作る）。
//
//  【自分のコールバック】API 34 以上は BackAnimationCallbackApi34（進み具合つき）、33 は BackInvokedCallbackApi33（確定だけ）。
//  PRIORITY_DEFAULT（0）で登録する。同じ優先度では後から登録したものが上に来る（記憶による）ので、IME（ソフトキーボード）が
//  出ている間は IME のコールバックが先に戻るを受けてキーボードを閉じる見込み。
//
//  【アプリが受ける（on）】自分のコールバックを登録する（システムの背面行きのコールバックを登録していれば外す）。
//  【受ける層が無い＝根（off）】API ごとに分ける:
//    API 36 以上 … 自分のコールバックを外し、SystemOnBackInvokedCallbacks.moveTaskToBackCallback を登録する（BackSystemCallbacksApi36。
//                  どこから起動した根でも finish せずに背面へ回り、「ホームへ戻る」の予測アニメーションが出る見込み。記憶による）
//    API 33〜35 … 起動の Intent がランチャーの起動（ACTION_MAIN＋CATEGORY_LAUNCHER。launcherRoot）のときだけ外す（Android 12 以降、
//                  ホームから起動したランチャーの根の Activity はシステムの戻るで finish されず背面へ回る。記憶による）。
//                  それ以外の起動（PlatformEntry の別名・adb の am start -n〈SeedAndroid の run もこれ〉）は、システムの戻るが finish
//                  → MainActivity.onDestroy → プロセスの終了になるおそれがあるので、自分のコールバックを残す
//                  （確定 → 合成の KEYCODE_BACK → Escape → SEED.UI の BackDispatcher → App.MoveTaskToBack の従来の道で背面へ）
//  登録・外しは UI スレッドだけで行う（呼び手 BackCallbackController が UI スレッドから呼ぶ）。登録の有無は自分で覚え、
//  二重の登録・無いものの外しをしない。
// ============================================================

package com.seedengine.runtime.back;

import android.app.Activity;
import android.os.Build;
import android.window.OnBackInvokedCallback;
import android.window.OnBackInvokedDispatcher;

import androidx.annotation.RequiresApi;

/**
 * 窓の OnBackInvokedDispatcher へのコールバックの登録と外し（UI スレッド専用。API 33 以上の端末でだけ作る）。
 */
@RequiresApi(Build.VERSION_CODES.TIRAMISU)
final class BackDispatcherApi33 {

    /** 窓の戻るの配り口（Activity.getOnBackInvokedDispatcher。API 33）。 */
    private final OnBackInvokedDispatcher dispatcher;

    /** 自分のコールバック（手ぶりを BackGestureReporter へ渡す）。 */
    private final OnBackInvokedCallback appCallback;

    /** 根で登録するシステムの背面行きのコールバック（API 36 以上。未満は null）。 */
    private final OnBackInvokedCallback rootCallback;

    /** 起動の Intent がランチャーの起動か（API 33〜35 の根で自分のコールバックを外してよいか）。 */
    private final boolean launcherRoot;

    /** 自分のコールバックを登録しているか。 */
    private boolean appRegistered;

    /** システムの背面行きのコールバックを登録しているか。 */
    private boolean rootRegistered;

    /**
     * 作る（API 33 以上の端末でだけ呼ぶこと。まだ何も登録しない）。
     *
     * @param activity     コールバックを登録する Activity（MainActivity）
     * @param reporter     手ぶりの知らせる先
     * @param launcherRoot 起動の Intent がランチャーの起動か（BackCallbackController.isLauncherIntent）
     */
    BackDispatcherApi33(Activity activity, BackGestureReporter reporter, boolean launcherRoot) {
        this.dispatcher = activity.getOnBackInvokedDispatcher();
        // API 34 以上は進み具合つき、33 は確定だけ（作り方を static メソッドにして、33 の端末で API 34 の型を読み込まない）
        this.appCallback = Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE
                ? BackAnimationCallbackApi34.create(reporter)
                : new BackInvokedCallbackApi33(reporter);
        this.rootCallback = Build.VERSION.SDK_INT >= Build.VERSION_CODES.BAKLAVA
                ? BackSystemCallbacksApi36.moveTaskToBack(activity)
                : null;
        this.launcherRoot = launcherRoot;
    }

    /**
     * アプリが戻るを受ける: 自分のコールバックを登録する（システムの背面行きを登録していれば外す）。
     *
     * @return ログ用の説明
     */
    String handToApp() {
        unregisterRoot();
        registerApp();
        return "アプリが戻るを受ける（自分のコールバックを登録。確定は合成の KEYCODE_BACK → Escape）";
    }

    /**
     * 受ける層が無い（根）: API ごとに外し方を選ぶ（ファイル冒頭）。
     *
     * @return ログ用の説明
     */
    String handToSystem() {
        if (rootCallback != null) {
            unregisterApp();
            registerRoot();
            return "受ける層なし（根）→ 自分のコールバックを外し、システムの背面行き（moveTaskToBackCallback）を登録";
        }
        if (launcherRoot) {
            unregisterApp();
            return "受ける層なし（根）→ 自分のコールバックを外す（ランチャーから起動した根なので、システムの戻るが背面へ回す）";
        }
        return "受ける層なし（根）→ ランチャー以外から起動した根なので自分のコールバックを残す（Escape → App.MoveTaskToBack で背面へ）";
    }

    /** 自分のコールバックを登録する（済みなら何もしない）。 */
    private void registerApp() {
        if (!appRegistered) {
            dispatcher.registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_DEFAULT, appCallback);
            appRegistered = true;
        }
    }

    /** 自分のコールバックを外す（登録していなければ何もしない）。 */
    private void unregisterApp() {
        if (appRegistered) {
            dispatcher.unregisterOnBackInvokedCallback(appCallback);
            appRegistered = false;
        }
    }

    /** システムの背面行きのコールバックを登録する（API 36 未満・済みなら何もしない）。 */
    private void registerRoot() {
        if (rootCallback != null && !rootRegistered) {
            dispatcher.registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_DEFAULT, rootCallback);
            rootRegistered = true;
        }
    }

    /** システムの背面行きのコールバックを外す（登録していなければ何もしない）。 */
    private void unregisterRoot() {
        if (rootRegistered) {
            dispatcher.unregisterOnBackInvokedCallback(rootCallback);
            rootRegistered = false;
        }
    }
}
