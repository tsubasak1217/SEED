// ============================================================
//  EventDoorbell.java — :seed_platform からの「未読の記録がある」の知らせを受ける Binder（メインプロセス。W1-1）
//
//  PlatformConnection が接続のたびに platform.register_callback の Bundle.putBinder でこの Binder を :seed_platform へ渡し、
//  :seed_platform の EventDoorbellClient が記録を足したときに oneway で呼ぶ（相手を待たせない）。
//  知らせは「呼び鈴」だけで、中身（イベント）は受け手が platform.poll_events で取りに行く（記録が正本。取りこぼしても
//  次の呼び鈴か次の接続で取れる）。自パッケージ宛ての放送を使わないのは、受け手が別の放送を処理している間
//  5 秒待たされたため（W1-0 の F-3。docs/app_platform_roadmap.md §2.9.1）。
// ============================================================

package com.seedengine.runtime.platform;

import android.os.Binder;
import android.os.Parcel;
import android.os.Process;
import android.os.RemoteException;
import android.util.Log;

/**
 * 記録の知らせの受け口（Binder の実体。Binder のスレッドで呼ばれる）。
 *
 * <p>同じアプリ（同じ UID）の :seed_platform 以外からの呼び出しは受け付けない
 * （この Binder は自分のプロバイダにしか渡さないが、念のため）。</p>
 */
final class EventDoorbell extends Binder {

    /** 知らせを受けたときの処理（PlatformConnection）。 */
    interface Listener {
        /**
         * 未読の記録がある（Binder のスレッドから呼ばれる。重い処理をしないこと）。
         *
         * @param latestSeq :seed_platform の記録の最新の通し番号
         */
        void onEventsAvailable(long latestSeq);
    }

    /** 知らせの渡し先。 */
    private final Listener listener;

    /**
     * @param listener 知らせの渡し先
     */
    EventDoorbell(Listener listener) {
        this.listener = listener;
    }

    @Override
    protected boolean onTransact(int code, Parcel data, Parcel reply, int flags) throws RemoteException {
        if (code != PlatformContract.TRANSACTION_EVENTS_AVAILABLE) {
            // Binder の標準の呼び出し（インターフェースの問い合わせ等）は既定の処理へ
            return super.onTransact(code, data, reply, flags);
        }
        if (Binder.getCallingUid() != Process.myUid()) {
            Log.w(PlatformContract.LOG_TAG, "よそのアプリ（uid=" + Binder.getCallingUid() + "）からの記録の知らせを無視しました");
            return false;
        }
        listener.onEventsAvailable(data.readLong());
        return true;
    }
}
