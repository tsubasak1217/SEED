// ============================================================
//  PlatformStorage.java — :seed_platform の置き場（端末保護ストレージ）のパスを 1 か所で決める（W1-3）
//
//  【なぜ端末保護ストレージか】
//  再起動の後、利用者が最初にロックを解除する前（LOCKED_BOOT_COMPLETED）でも、予約の控えを読んで張り直し
//  （BootReceiver）、発火を記録できる（AlarmReceiver）ようにするため（E-10・W1-0 の実機で確かめた置き場）。
//  資格情報で暗号化された普通の files（getFilesDir）は、ロックの解除まで読めない。
//
//  【置くもの】（すべて <端末保護ストレージの files>/seed_platform/ の下）
//    alarms.json  … 目覚ましの予約の控え（service/alarm/AlarmStore）
//    journal.json … イベントの記録（EventJournal。エンジンが取りに来るまでの未読）
//    ringing.json … 鳴動中の状態（鳴っている予約・待ち行列・force_volume の前の音量。service/alarm/ring/RingStateStore。W1-7。
//                   鳴っていない間は無い）
//    boot_rearm.json … 張り直しを済ませた起動の番号（Settings.Global.BOOT_COUNT。service/alarm/BootRearmGate。W1-7。
//                   同じ起動で重ねて届く BOOT_COMPLETED を見分ける）
//    sounds/      … 目覚ましの音源（メインプロセスのエンジンが「内容のハッシュ.拡張子」で書き出す。platform.paths で教える）
//  書き手は :seed_platform の 1 プロセスだけ（sounds/ だけはメインプロセスが書く。ファイルが別なのでぶつからない）。
// ============================================================

package com.seedengine.runtime.platform.service;

import android.content.Context;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;

import java.io.File;

/**
 * :seed_platform の置き場のパス（static のみ）。
 */
public final class PlatformStorage {

    private PlatformStorage() {
    }

    /** 予約の控えのファイルの名前。 */
    public static final String ALARMS_FILE_NAME = "alarms.json";

    /** イベントの記録のファイルの名前。 */
    public static final String JOURNAL_FILE_NAME = "journal.json";

    /** 鳴動中の状態の控えのファイルの名前（W1-7）。 */
    public static final String RINGING_FILE_NAME = "ringing.json";

    /** 張り直しを済ませた起動の番号のファイルの名前（W1-7）。 */
    public static final String BOOT_REARM_FILE_NAME = "boot_rearm.json";

    /** 端末保護ストレージの files（一度求めたら変わらないので持つ。null = まだ求めていない）。 */
    private static volatile File deviceProtectedFilesDir;

    /**
     * 端末保護ストレージの files の絶対パス（createDeviceProtectedStorageContext().getFilesDir()）。
     *
     * @param context どの Context でもよい
     * @return フォルダ（getFilesDir が作る）
     */
    public static File deviceProtectedFilesDir(Context context) {
        File cached = deviceProtectedFilesDir;
        if (cached == null) {
            // createDeviceProtectedStorageContext は API 24。minSdk 29 なので分岐は要らない
            cached = context.createDeviceProtectedStorageContext().getFilesDir();
            deviceProtectedFilesDir = cached;
        }
        return cached;
    }

    /**
     * :seed_platform のフォルダ（files/seed_platform。無ければ作る）。
     *
     * @param context どの Context でもよい
     * @return フォルダ
     */
    public static File platformDir(Context context) {
        return ensureDir(new File(deviceProtectedFilesDir(context), PlatformContract.STORAGE_DIR_NAME));
    }

    /**
     * 目覚ましの音源のフォルダ（files/seed_platform/sounds。無ければ作る）。
     *
     * @param context どの Context でもよい
     * @return フォルダ
     */
    public static File soundsDir(Context context) {
        return ensureDir(new File(platformDir(context), PlatformContract.SOUNDS_DIR_NAME));
    }

    /**
     * 予約の控えのファイル。
     *
     * @param context どの Context でもよい
     * @return ファイル（無いこともある）
     */
    public static File alarmsFile(Context context) {
        return new File(platformDir(context), ALARMS_FILE_NAME);
    }

    /**
     * イベントの記録のファイル。
     *
     * @param context どの Context でもよい
     * @return ファイル（無いこともある）
     */
    public static File journalFile(Context context) {
        return new File(platformDir(context), JOURNAL_FILE_NAME);
    }

    /**
     * 鳴動中の状態の控えのファイル（W1-7）。
     *
     * @param context どの Context でもよい
     * @return ファイル（鳴っていない間は無い）
     */
    public static File ringingFile(Context context) {
        return new File(platformDir(context), RINGING_FILE_NAME);
    }

    /**
     * 張り直しを済ませた起動の番号のファイル（W1-7）。
     *
     * @param context どの Context でもよい
     * @return ファイル（無いこともある）
     */
    public static File bootRearmFile(Context context) {
        return new File(platformDir(context), BOOT_REARM_FILE_NAME);
    }

    /**
     * フォルダが無ければ作る（作れなくても例外にしない。書くときの失敗で分かる）。
     *
     * @param dir フォルダ
     * @return dir
     */
    private static File ensureDir(File dir) {
        if (!dir.isDirectory() && !dir.mkdirs() && !dir.isDirectory()) {
            Log.w(PlatformContract.LOG_TAG, "フォルダを作れませんでした: " + dir);
        }
        return dir;
    }
}
