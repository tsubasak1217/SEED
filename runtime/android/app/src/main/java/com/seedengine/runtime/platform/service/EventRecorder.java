// ============================================================
//  EventRecorder.java — :seed_platform で起きたことを「記録して、エンジンへ呼び鈴を鳴らす」（W1-3）
//
//  記録（EventJournal。端末保護ストレージへ書く）が正本で、呼び鈴（EventDoorbellClient）は「未読あり」の知らせ。
//  各モジュール・受信機（目覚ましの AlarmReceiver・BootReceiver、試験イベント）はこの 1 か所を通して記録する
//  （EventJournal と EventDoorbellClient はパッケージの中だけに閉じたまま、別パッケージの alarm/ から使えるようにする窓口）。
//  エンジンが居なければ呼び鈴は鳴らず、記録は次の接続（platform.register_callback）で取られる。
// ============================================================

package com.seedengine.runtime.platform.service;

import android.content.Context;

import org.json.JSONObject;

/**
 * 記録と呼び鈴の窓口（static のみ）。
 */
public final class EventRecorder {

    private EventRecorder() {
    }

    /** 記録した結果。 */
    public static final class Recorded {
        /** 付いた通し番号。 */
        public final long seq;
        /** 呼び鈴を鳴らせたか（エンジンが居なければ false）。 */
        public final boolean doorbellRang;

        Recorded(long seq, boolean doorbellRang) {
            this.seq = seq;
            this.doorbellRang = doorbellRang;
        }
    }

    /**
     * イベントを 1 件記録し（端末保護ストレージへ書く）、エンジンへ呼び鈴を鳴らす。
     *
     * @param context :seed_platform の Context
     * @param name    名前（PlatformContract.EVENT_*。platform. で始める）
     * @param data    中身
     * @return 付いた通し番号と、呼び鈴を鳴らせたか
     */
    public static Recorded record(Context context, String name, JSONObject data) {
        long seq = EventJournal.get(context).append(name, data);
        boolean rang = EventDoorbellClient.get().ring(seq);
        return new Recorded(seq, rang);
    }
}
