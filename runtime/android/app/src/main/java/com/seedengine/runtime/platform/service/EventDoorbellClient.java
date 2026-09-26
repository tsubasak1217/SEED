// ============================================================
//  EventDoorbellClient.java — エンジン（メインプロセス）へ「未読の記録がある」を知らせる呼び鈴（:seed_platform。W1-1）
//
//  メインプロセスの PlatformConnection が platform.register_callback の Bundle.putBinder で渡した Binder（EventDoorbell）を持ち、
//  記録を足したときに oneway で呼ぶ（相手の処理を待たない。:seed_platform の Binder のスレッドを止めない）。
//  エンジンが居なければ何もしない（メインプロセスを起こさない。未読は次の接続のときに取られる）。
//  メインプロセスが死んだら linkToDeath で登録を外す。受け手は 1 つ（メインプロセスは 1 つ）なので、新しい登録で置き換える。
// ============================================================

package com.seedengine.runtime.platform.service;

import android.os.DeadObjectException;
import android.os.IBinder;
import android.os.Parcel;
import android.os.RemoteException;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;

/**
 * 呼び鈴の相手（プロセスで 1 つ。Binder のスレッドから呼ばれるので同期する）。
 */
final class EventDoorbellClient {

    /** linkToDeath の flags（使わない。0 固定）。 */
    private static final int LINK_TO_DEATH_FLAGS = 0;

    /** プロセスで 1 つ。 */
    private static final EventDoorbellClient INSTANCE = new EventDoorbellClient();

    /** 呼び鈴の相手（メインプロセスの EventDoorbell の代理。登録が無ければ null）。 */
    private IBinder target;

    /** 相手の死の知らせの受け口（登録を外すときに unlinkToDeath する）。 */
    private IBinder.DeathRecipient deathRecipient;

    private EventDoorbellClient() {
    }

    /**
     * プロセスで 1 つの呼び鈴を返す。
     *
     * @return 呼び鈴
     */
    static EventDoorbellClient get() {
        return INSTANCE;
    }

    /**
     * 相手を登録する（前の登録は外す）。
     *
     * @param binder メインプロセスの EventDoorbell
     * @throws RemoteException 相手が既に死んでいる（linkToDeath の失敗）
     */
    synchronized void register(IBinder binder) throws RemoteException {
        clearLocked();
        IBinder.DeathRecipient recipient = () -> onTargetDied(binder);
        binder.linkToDeath(recipient, LINK_TO_DEATH_FLAGS);
        target = binder;
        deathRecipient = recipient;
    }

    /**
     * 呼び鈴を鳴らす（oneway。相手が居なければ何もしない）。
     *
     * @param latestSeq 記録の最新の通し番号
     * @return 鳴らせたら true
     */
    boolean ring(long latestSeq) {
        IBinder current;
        synchronized (this) {
            current = target;
        }
        if (current == null) {
            return false;
        }
        Parcel data = Parcel.obtain();
        try {
            data.writeLong(latestSeq);
            current.transact(PlatformContract.TRANSACTION_EVENTS_AVAILABLE, data, null, IBinder.FLAG_ONEWAY);
            return true;
        } catch (DeadObjectException e) {
            onTargetDied(current);
            return false;
        } catch (RemoteException e) {
            Log.w(PlatformContract.LOG_TAG, "記録の知らせを送れませんでした: " + PlatformJson.describe(e));
            return false;
        } finally {
            data.recycle();
        }
    }

    /**
     * 相手が死んだ（それが今の相手なら登録を外す）。
     *
     * @param binder 死んだ相手
     */
    private synchronized void onTargetDied(IBinder binder) {
        if (target == binder) {
            clearLocked();
            Log.i(PlatformContract.LOG_TAG, "エンジン（メインプロセス）が居なくなったので、記録の知らせの相手を外しました");
        }
    }

    /** 登録を外す（同期の中から）。 */
    private void clearLocked() {
        if (target != null && deathRecipient != null) {
            target.unlinkToDeath(deathRecipient, LINK_TO_DEATH_FLAGS);
        }
        target = null;
        deathRecipient = null;
    }
}
