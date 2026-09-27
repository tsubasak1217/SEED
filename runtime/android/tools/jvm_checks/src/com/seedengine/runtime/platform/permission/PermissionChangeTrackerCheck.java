// ============================================================
//  PermissionChangeTrackerCheck.java — PermissionChangeTracker（権限の前回の状態との比較）を JVM で確かめる検査（M7）
//
//  Android の API を使わない純粋な部分だけを、端末なしで確かめる（JUnit は使わず、W1 の JVM の検査と同じ手書きの main）。
//  置き場（Store）はメモリの表で真似し、「プロセスが止められて起動し直す」は、同じ置き場で係を作り直して真似する。
//  実行は run_jvm_checks.sh（javac --release 17 → java）。失敗があれば終了コード 1。
//  package-private の係を触るので、同じパッケージに置く（Gradle のソースセットには入れない）。
// ============================================================

package com.seedengine.runtime.platform.permission;

import java.util.HashMap;
import java.util.Map;

/** 検査の本体（main だけ）。 */
public final class PermissionChangeTrackerCheck {

    /** 状態の名前（PlatformContract.PERMISSION_STATUS_* と同じ綴り。検査は Android のクラスを読まないので写す）。 */
    private static final String GRANTED = "granted";
    /** 状態の名前（拒否）。 */
    private static final String DENIED = "denied";
    /** 状態の名前（設定の画面が要る）。 */
    private static final String NEEDS_SETTINGS = "needs_settings";
    /** 種類の名前（PermissionKind.wireName と同じ綴り）。 */
    private static final String NOTIFICATIONS = "post_notifications";
    /** 種類の名前（正確なアラーム）。 */
    private static final String EXACT_ALARM = "exact_alarm";

    /** 失敗の数。 */
    private static int failures = 0;

    private PermissionChangeTrackerCheck() {
    }

    /** メモリの置き場（書き込みの回数も数える）。 */
    private static final class MemoryStore implements PermissionChangeTracker.Store {
        /** 覚えた状態。 */
        final Map<String, String> values = new HashMap<>();
        /** save が呼ばれた回数。 */
        int saves = 0;

        @Override
        public String load(String kindName) {
            return values.get(kindName);
        }

        @Override
        public void save(String kindName, String status) {
            values.put(kindName, status);
            saves++;
        }
    }

    /** 確かめる（ok なら "ok"、違えば "NG" を出して数える）。 */
    private static void expect(boolean ok, String what) {
        System.out.println((ok ? "ok   " : "NG   ") + what);
        if (!ok) {
            failures++;
        }
    }

    /** 2 つの文字列（null を含む）が等しいか。 */
    private static boolean same(String a, String b) {
        return a == null ? b == null : a.equals(b);
    }

    public static void main(String[] args) {
        // ① 初めて見る種類は覚えるだけ（変化なし）
        MemoryStore store = new MemoryStore();
        PermissionChangeTracker tracker = new PermissionChangeTracker(store);
        expect(tracker.observe(NOTIFICATIONS, GRANTED) == null, "初めて見る種類は変化なし（覚えるだけ）");
        expect(same(store.values.get(NOTIFICATIONS), GRANTED) && store.saves == 1, "初めて見た状態を置き場へ書いた");

        // ② 前回と同じなら何もしない（書かない）
        expect(tracker.observe(NOTIFICATIONS, GRANTED) == null, "前回と同じなら変化なし");
        expect(store.saves == 1, "前回と同じなら置き場へ書かない");

        // ③ 同じプロセスの中の変化（W1-7 の M8: denied → granted はプロセスが止められずに届いた）
        expect(same(tracker.observe(NOTIFICATIONS, DENIED), GRANTED), "変わったら前回の状態を返す（granted → denied）");
        expect(same(tracker.observe(NOTIFICATIONS, GRANTED), DENIED), "戻ったら前回の状態を返す（denied → granted）");

        // ④ M7: 通知をオフにされてプロセスが止められ、起動し直した最初の onResume（同じ置き場で係を作り直す）
        PermissionChangeTracker restarted = new PermissionChangeTracker(store);
        expect(same(restarted.observe(NOTIFICATIONS, DENIED), GRANTED), "起動し直しても前回（granted）と比べて変化を返す（M7）");
        expect(same(store.values.get(NOTIFICATIONS), DENIED), "起動し直した後の状態も覚えた");

        // ⑤ 起動し直しても変わっていなければ何も流さない
        PermissionChangeTracker restartedAgain = new PermissionChangeTracker(store);
        expect(restartedAgain.observe(NOTIFICATIONS, DENIED) == null, "起動し直しても変わっていなければ変化なし");

        // ⑥ 種類ごとに別に覚える
        expect(restartedAgain.observe(EXACT_ALARM, NEEDS_SETTINGS) == null, "別の種類は初めて見るので変化なし");
        expect(same(restartedAgain.observe(EXACT_ALARM, GRANTED), NEEDS_SETTINGS), "別の種類の変化は別に返す");
        expect(same(store.values.get(NOTIFICATIONS), DENIED), "別の種類の変化で通知の覚えは変わらない");

        // ⑦ 置き場の無い係・状態の無い呼び出しは作らせない（呼び出し側の誤り）
        boolean rejectedNullStore = false;
        try {
            new PermissionChangeTracker(null);
        } catch (NullPointerException e) {
            rejectedNullStore = true;
        }
        expect(rejectedNullStore, "置き場が null なら作らない");
        boolean rejectedNullStatus = false;
        try {
            tracker.observe(NOTIFICATIONS, null);
        } catch (NullPointerException e) {
            rejectedNullStatus = true;
        }
        expect(rejectedNullStatus, "状態が null なら断る");

        System.out.println(failures == 0 ? "ALL OK" : ("FAILURES " + failures));
        System.exit(failures == 0 ? 0 : 1);
    }
}
