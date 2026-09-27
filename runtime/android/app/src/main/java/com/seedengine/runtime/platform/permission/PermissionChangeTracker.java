// ============================================================
//  PermissionChangeTracker.java — 権限の状態を「前の onResume で見た状態」と比べる（純粋な Java。W1-5・M7 で置き場を永続化）
//
//  【比べ方】今の状態を見せるたびに、前回の状態（置き場 Store から読む）と比べる:
//    前回が無い（その種類を初めて見る）→ 覚えるだけ（変化とはしない）
//    前回と同じ                         → 何もしない（置き場へ書かない）
//    前回と違う                         → 覚え直し、前回の状態を返す（呼び出し側が platform.permission_changed を流す）
//
//  【M7 で永続化した理由】（docs/app_platform_roadmap.md §2.9.2 の W1-7 の手作業の確認 M7）
//  利用者が端末の設定で通知をオフにすると POST_NOTIFICATIONS が取り消され、Android はアプリの両プロセスを止める。
//  戻るとメインプロセスが起動し直すので、前回の状態をメモリにしか持たないと比べる相手が無く、granted → denied が届かなかった。
//  置き場を SharedPreferences（PermissionStatusMemory）にすれば、起動し直した最初の onResume でも前回と比べられる。
//
//  Android の API を使わない（Store の中身だけが Android）ので、JVM で検査できる
//  （runtime/android/tools/jvm_checks/ の PermissionChangeTrackerCheck）。
// ============================================================

package com.seedengine.runtime.platform.permission;

import java.util.Objects;

/**
 * 前回の状態と比べる係（スレッドを持たない。呼び出し側〈PermissionMonitor〉は UI スレッドだけから呼ぶ）。
 */
final class PermissionChangeTracker {

    /** 前回の状態の置き場（本番は SharedPreferences の PermissionStatusMemory、検査はメモリの表）。 */
    interface Store {

        /**
         * 前回の状態を読む。
         *
         * @param kindName 種類の wire の名前（PermissionKind.wireName）
         * @return 前回の状態（PlatformContract.PERMISSION_STATUS_*）。覚えが無ければ null
         */
        String load(String kindName);

        /**
         * 状態を覚える（前回と違うときだけ呼ばれる）。
         *
         * @param kindName 種類の wire の名前
         * @param status   今の状態
         */
        void save(String kindName, String status);
    }

    /** 前回の状態の置き場。 */
    private final Store store;

    /**
     * 係を作る。
     *
     * @param store 前回の状態の置き場
     */
    PermissionChangeTracker(Store store) {
        this.store = Objects.requireNonNull(store, "store");
    }

    /**
     * 今の状態を見せ、前回と比べる。
     *
     * @param kindName 種類の wire の名前
     * @param status   今の状態（null は扱わない）
     * @return 前回と違えば前回の状態（変化あり）。前回が無い・前回と同じなら null（変化なし）
     */
    String observe(String kindName, String status) {
        Objects.requireNonNull(kindName, "kindName");
        Objects.requireNonNull(status, "status");
        String previous = store.load(kindName);
        if (status.equals(previous)) {
            // 変わっていない（書かない）
            return null;
        }
        store.save(kindName, status);
        // 前回が無ければ（初めて見る種類）覚えるだけで変化とはしない
        return previous;
    }
}
