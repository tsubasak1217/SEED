// ============================================================
//  RingStateStore.java — 鳴動中の状態の控え（端末保護ストレージの seed_platform/ringing.json。:seed_platform。W1-7）
//
//  【なぜ要るか】鳴動の状態の正本 RingRegistry はプロセスのメモリにしかないので、:seed_platform がプロセスごと殺されると
//  （低メモリ・クラッシュ・kill -9）鳴動は黙って消え、force_volume で下げたアラームの音量も戻らなかった（W1-4b の T5・G-6）。
//  そこで鳴動中の状態をファイルへ控え、見張りの予約（RingWatchdog）の発火で読み戻して鳴らし直す（RingRecovery）。
//
//  【中身】{ "format_version": 1, "registry": RingSnapshot.toJson() | null, "volume": RingVolumeRecord.toJson() | null }
//    registry … RingRegistry の写し。変わるたびに書く（RingControl.persist。版の古い写しでは上書きしない）
//    volume   … force_volume で STREAM_ALARM を変える**前に**書く元の音量（AlarmStreamVolume）。戻したら消す
//  両方とも空になったらファイルを消す（鳴っていない間は何も残さない。DurableFile.delete でフォルダも fsync）。
//
//  【前のプロセスの残り（leftover）】プロセスで最初に触ったときに 1 回だけファイルを読み、以後はメモリの写しを正とする。
//  読んだ registry は「前のプロセスの残り」で、次のどれかで片付く（どれも同じ lock の中で、読んだ物と同じかを == で確かめる）:
//    ・見張りの発火（RingRecovery.onWatchdog）→ takeRegistryIfSame で取り出して鳴らし直す
//    ・プロセスの起動・再起動（RingRecovery.onStartup）で見張りが張られていない → 取り出して「終わった（error）」と記録する
//    ・このプロセスで新しい鳴動の写しを書いた（新しい目覚ましが先に鳴った）→ saveRegistry が「置き換えられた残りの予約」を返し、
//      呼び出し側が「終わった（error）」と記録する（同じ予約が新しい写しにもあれば〈= 鳴らし直した〉数えない）
//  どれか 1 つだけが取り出せるので、ring_stopped が 2 回記録されることは無い。
//
//  【書き方】DurableFile（一時ファイル → fsync → rename → フォルダの fsync）。このクラスの lock の中で書く（書き手はこのプロセスの
//  UI・Binder・音声・照合のスレッド）。書けなかったら警告だけ（鳴動は続ける。戻せなくなるだけ）。
//  【読み方】無ければ空。壊れていれば空として扱い警告する（鳴動も元の音量も戻せない）。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.content.Context;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.DurableFile;
import com.seedengine.runtime.platform.service.PlatformStorage;
import com.seedengine.runtime.platform.service.alarm.AlarmEntry;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.function.BooleanSupplier;

/**
 * 鳴動中の状態の控え（static のみ。プロセスで 1 つの lock）。
 */
final class RingStateStore {

    private RingStateStore() {
    }

    /** 控えのファイルの書式の版（形を変えたら上げる）。 */
    private static final int FILE_FORMAT_VERSION = 1;

    /** 欄: 書式の版。 */
    private static final String KEY_FORMAT_VERSION = "format_version";

    /** 欄: RingRegistry の写し。 */
    private static final String KEY_REGISTRY = "registry";

    /** 欄: 変える前の音量。 */
    private static final String KEY_VOLUME = "volume";

    /** 元の音量の控えが無いときの値。 */
    static final int NO_RECORDED_VOLUME = -1;

    /** 控えの中身（ある時点の両方の欄。どちらも null 可）。 */
    static final class Saved {
        /** RingRegistry の写し（空なら null）。 */
        final RingSnapshot registry;
        /** 変える前の音量（無ければ null）。 */
        final RingVolumeRecord volume;

        Saved(RingSnapshot registry, RingVolumeRecord volume) {
            this.registry = registry;
            this.volume = volume;
        }

        /** @return 両方とも空なら true */
        boolean isEmpty() {
            return registry == null && volume == null;
        }

        @Override
        public String toString() {
            return (registry != null ? registry.toString() : "鳴動なし") + "・" + (volume != null ? volume.toString() : "音量の控えなし");
        }
    }

    /** ファイルを読んだか（プロセスで 1 回だけ読む）。 */
    private static boolean loaded;

    /** 今の registry の欄（空なら null）。 */
    private static RingSnapshot registry;

    /** 今の volume の欄（無ければ null）。 */
    private static RingVolumeRecord volume;

    /** 最後に書いた RingRegistry の写しの版（このプロセスの中で比べる。ファイルから読んだ写しはどの版よりも古い）。 */
    private static long lastSavedVersion = RingSnapshot.LOADED_VERSION;

    /**
     * 今の控え（前のプロセスの残りを含む。最初の呼び出しでファイルを読む）。
     *
     * @param context どの Context でもよい
     * @return 控え
     */
    static synchronized Saved load(Context context) {
        ensureLoaded(context);
        return new Saved(registry, volume);
    }

    /**
     * RingRegistry の写しを書く（前に書いた写しより版が古ければ何もしない）。
     * 前のプロセスの残りの鳴動を置き換えたときは、その予約（新しい写しに同じ予約が無いもの）を返す（呼び出し側が
     * ring_stopped(error) を記録する）。
     *
     * @param context  どの Context でもよい
     * @param snapshot 写し
     * @return 置き換えた前のプロセスの残りの予約（無ければ空）
     */
    static synchronized List<AlarmEntry> saveRegistry(Context context, RingSnapshot snapshot) {
        ensureLoaded(context);
        if (snapshot.version <= lastSavedVersion) {
            return Collections.emptyList();
        }
        List<AlarmEntry> superseded = Collections.emptyList();
        if (registry != null && registry.version == RingSnapshot.LOADED_VERSION) {
            superseded = entriesMissingFrom(registry, snapshot);
        }
        lastSavedVersion = snapshot.version;
        registry = snapshot.isEmpty() ? null : snapshot;
        write(context);
        return superseded;
    }

    /**
     * registry の欄が expected と同じ物（==）なら空にして書き、true を返す（前のプロセスの残りを 1 か所だけが取り出すため）。
     *
     * @param context  どの Context でもよい
     * @param expected load で読んだ registry の欄
     * @return 取り出せたら true（既に別の者が取り出した・置き換えたなら false）
     */
    static synchronized boolean takeRegistryIfSame(Context context, RingSnapshot expected) {
        ensureLoaded(context);
        if (expected == null || registry != expected) {
            return false;
        }
        registry = null;
        write(context);
        return true;
    }

    /**
     * 変える前の音量の控え（無ければ NO_RECORDED_VOLUME）。鳴らし直すとき、今の音量（自分で下げた値）ではなくこの値を
     * 「元」とするため（AlarmStreamVolume.force）。
     *
     * @param context どの Context でもよい
     * @return 音量の段階
     */
    static synchronized int recordedOriginalVolume(Context context) {
        ensureLoaded(context);
        return volume != null ? volume.originalIndex : NO_RECORDED_VOLUME;
    }

    /**
     * 変える前の音量を書く（音量を変える前に呼ぶ）。
     *
     * @param context       どの Context でもよい
     * @param originalIndex 変える前の音量
     * @param forcedIndex   鳴動中に保つ音量
     */
    static synchronized void recordVolume(Context context, int originalIndex, int forcedIndex) {
        ensureLoaded(context);
        volume = new RingVolumeRecord(originalIndex, forcedIndex);
        write(context);
    }

    /**
     * 音量の控えを消す（元の音量へ戻した後に呼ぶ）。
     *
     * @param context どの Context でもよい
     */
    static synchronized void clearVolume(Context context) {
        ensureLoaded(context);
        if (volume == null) {
            return;
        }
        volume = null;
        write(context);
    }

    /**
     * volume の欄が expected と同じ物（==）なら、restore を呼び（元の音量へ戻す）、戻せたときだけ消す。
     * 戻す処理を lock の中で行うのは、戻す前に控えを消して殺されると元の値を失うため、と、同じ控えを 2 か所で戻さないため。
     * 戻せなかった（Android 17 の AudioHardening が背面のアプリの音量の変更を無視した等。W1-7 の実機）ときは控えを残し、
     * 前面に出たとき・次の鳴動で戻す（RingRecovery.retryLeftoverVolume・AlarmStreamVolume.force）。
     *
     * @param context  どの Context でもよい
     * @param expected load で読んだ volume の欄
     * @param restore  元の音量へ戻す処理（lock の中で呼ぶ。短く）。戻ったことを確かめられたら true を返す
     * @return 戻したら true
     */
    static synchronized boolean restoreVolumeIfSame(Context context, RingVolumeRecord expected, BooleanSupplier restore) {
        ensureLoaded(context);
        if (expected == null || volume != expected) {
            return false;
        }
        if (!restore.getAsBoolean()) {
            return false;
        }
        volume = null;
        write(context);
        return true;
    }

    /** 最初の呼び出しでファイルを読む（lock を持って呼ぶ）。 */
    private static void ensureLoaded(Context context) {
        if (loaded) {
            return;
        }
        loaded = true;
        byte[] bytes;
        try {
            bytes = DurableFile.readOrNull(PlatformStorage.ringingFile(context));
        } catch (IOException e) {
            Log.w(PlatformContract.LOG_TAG, "鳴動の状態の控えを読めませんでした（空として扱います）: " + PlatformJson.describe(e));
            return;
        }
        if (bytes == null) {
            return;
        }
        try {
            JSONObject root = new JSONObject(PlatformJson.text(bytes));
            int version = root.optInt(KEY_FORMAT_VERSION, 0);
            if (version != FILE_FORMAT_VERSION) {
                Log.w(PlatformContract.LOG_TAG, "鳴動の状態の控えの版が違うので読みません（" + version + "）");
                return;
            }
            JSONObject registryJson = root.optJSONObject(KEY_REGISTRY);
            if (registryJson != null) {
                RingSnapshot snapshot = RingSnapshot.fromJson(registryJson);
                registry = snapshot.isEmpty() ? null : snapshot;
            }
            JSONObject volumeJson = root.optJSONObject(KEY_VOLUME);
            if (volumeJson != null) {
                volume = RingVolumeRecord.fromJson(volumeJson);
            }
            Log.i(PlatformContract.LOG_TAG, "前のプロセスの鳴動の状態の控えを読みました（" + new Saved(registry, volume) + "）");
        } catch (JSONException e) {
            Log.w(PlatformContract.LOG_TAG, "鳴動の状態の控えが壊れていたので空として扱います: " + PlatformJson.describe(e));
            registry = null;
            volume = null;
        }
    }

    /** 今の両方の欄を書く。両方とも空ならファイルを消す（lock を持って呼ぶ。失敗は警告だけ）。 */
    private static void write(Context context) {
        if (registry == null && volume == null) {
            if (!DurableFile.delete(PlatformStorage.ringingFile(context))) {
                Log.w(PlatformContract.LOG_TAG, "鳴動の状態の控えを消せませんでした");
            }
            return;
        }
        JSONObject root = new JSONObject();
        PlatformJson.put(root, KEY_FORMAT_VERSION, FILE_FORMAT_VERSION);
        PlatformJson.put(root, KEY_REGISTRY, registry != null ? registry.toJson() : JSONObject.NULL);
        PlatformJson.put(root, KEY_VOLUME, volume != null ? volume.toJson() : JSONObject.NULL);
        try {
            DurableFile.writeAtomically(PlatformStorage.ringingFile(context), PlatformJson.utf8(root.toString()));
        } catch (IOException e) {
            Log.w(PlatformContract.LOG_TAG, "鳴動の状態の控えを書けませんでした（殺されたときに戻せません）: " + PlatformJson.describe(e));
        }
    }

    /** previous にあって next に同じ予約が無い予約（前のプロセスの残りが置き換えられた分）。 */
    private static List<AlarmEntry> entriesMissingFrom(RingSnapshot previous, RingSnapshot next) {
        List<AlarmEntry> kept = next.entries();
        List<AlarmEntry> missing = new ArrayList<>();
        for (AlarmEntry entry : previous.entries()) {
            boolean found = false;
            for (AlarmEntry candidate : kept) {
                if (candidate.isSameReservation(entry)) {
                    found = true;
                    break;
                }
            }
            if (!found) {
                missing.add(entry);
            }
        }
        return missing;
    }
}
