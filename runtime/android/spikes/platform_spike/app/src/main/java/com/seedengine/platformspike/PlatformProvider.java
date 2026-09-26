package com.seedengine.platformspike;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.os.Process;
import android.os.SystemClock;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.List;

/**
 * 同期の命令と問い合わせの窓口（:seed_platform・exported=false。ロードマップ §2.2 の PlatformProvider の試作）。
 *
 * <p>メインプロセス（エンジン）は ContentResolver.call(method, extras) で呼ぶ。Binder の往復 1 回で、
 * バインドの手続き（非同期の onServiceConnected）が要らないので、JNI から同期で呼ぶ入口に向く。
 * call は Binder のスレッドで動くので、UI スレッドの部品（RingService）へは Handler で渡す。</p>
 */
public final class PlatformProvider extends ContentProvider {
    @Override
    public boolean onCreate() {
        SpikeLog.mark("provider_create", "");
        return true;
    }

    @Override
    public Bundle call(String method, String arg, Bundle extras) {
        Bundle out = new Bundle();
        out.putInt(SpikeContract.R_PID, Process.myPid());
        Context context = getContext();
        try {
            switch (method) {
                case SpikeContract.M_PING:
                    break;
                case SpikeContract.M_INVOKE:
                    // SeedPlatform.invoke(module, method, byte[] json) の往復を模す（中身はそのまま返す）。
                    out.putByteArray(SpikeContract.R_JSON,
                            extras != null ? extras.getByteArray(SpikeContract.EXTRA_JSON) : null);
                    break;
                case SpikeContract.M_ALARM_SCHEDULE:
                    schedule(context, extras, out);
                    break;
                case SpikeContract.M_ALARM_CANCEL: {
                    String id = extras != null ? extras.getString(SpikeContract.EXTRA_ID) : null;
                    if (id == null) {
                        id = SpikeContract.DEFAULT_ALARM_ID;
                    }
                    AlarmScheduler.cancel(context, id);
                    AlarmStore.remove(context, id);
                    break;
                }
                case SpikeContract.M_ALARM_CANCEL_ALL:
                    for (AlarmEntry entry : AlarmStore.load(context)) {
                        AlarmScheduler.cancel(context, entry.id);
                    }
                    AlarmStore.clear(context);
                    break;
                case SpikeContract.M_ALARM_REARM_ALL:
                    out.putString(SpikeContract.R_JSON, AlarmScheduler.rearmAll(context,
                            arg != null ? arg : "call"));
                    break;
                case SpikeContract.M_ALARM_LIST:
                    out.putString(SpikeContract.R_JSON, listJson(context));
                    break;
                case SpikeContract.M_RING_STOP:
                    out.putString(SpikeContract.R_JSON, "{\"was_ringing\":"
                            + RingService.stopFromSameProcess(arg != null ? arg : "ipc") + "}");
                    break;
                case SpikeContract.M_RING_STATUS:
                    out.putString(SpikeContract.R_JSON, RingService.statusJson());
                    break;
                case SpikeContract.M_PERM_STATUS:
                    out.putString(SpikeContract.R_JSON, DeviceState.permissions(context)
                            + " " + DeviceState.describe(context));
                    break;
                case SpikeContract.M_BENCH_BROADCAST_BACK:
                    // :seed_platform → メインプロセスの知らせ（自パッケージ宛ての放送）の遅延を測る。
                    context.sendBroadcast(new Intent(SpikeContract.ACTION_BENCH_PONG)
                            .setPackage(context.getPackageName())
                            .putExtra(SpikeContract.EXTRA_T_SEND_NS, SystemClock.elapsedRealtimeNanos()));
                    break;
                default:
                    out.putString(SpikeContract.R_ERROR, "unknown_method:" + method);
                    break;
            }
        } catch (RuntimeException e) {
            // 例外を投げずに {ok:false, error} で返す（ロードマップ §2.4 のエラーの流儀）。
            out.putString(SpikeContract.R_ERROR, SpikeLog.oneLine(e));
        }
        out.putBoolean(SpikeContract.R_OK, !out.containsKey(SpikeContract.R_ERROR));
        return out;
    }

    /** 予約: 控えを先に書き（落ちても次の張り直しで拾える）、それから setAlarmClock。 */
    private static void schedule(Context context, Bundle extras, Bundle out) {
        Bundle in = extras != null ? extras : new Bundle();
        String id = in.getString(SpikeContract.EXTRA_ID, SpikeContract.DEFAULT_ALARM_ID);
        long triggerAt = in.containsKey(SpikeContract.EXTRA_TRIGGER_AT)
                ? in.getLong(SpikeContract.EXTRA_TRIGGER_AT)
                : System.currentTimeMillis()
                + in.getInt(SpikeContract.EXTRA_SECONDS, SpikeContract.DEFAULT_SECONDS) * SpikeContract.MS_PER_SECOND;
        String fgsType = in.getString(SpikeContract.EXTRA_FGS_TYPE, SpikeContract.FGS_TYPE_MEDIA_PLAYBACK);
        int maxRing = in.getInt(SpikeContract.EXTRA_MAX_RING_SECONDS, SpikeContract.DEFAULT_MAX_RING_SECONDS);
        AlarmEntry entry = new AlarmEntry(id, triggerAt, fgsType, maxRing);
        AlarmStore.put(context, entry);
        String error = AlarmScheduler.schedule(context, entry);
        if (error != null) {
            AlarmStore.remove(context, id);
            out.putString(SpikeContract.R_ERROR, error);
            return;
        }
        out.putLong(SpikeContract.EXTRA_TRIGGER_AT, triggerAt);
    }

    private static String listJson(Context context) {
        JSONArray array = new JSONArray();
        List<AlarmEntry> entries = AlarmStore.load(context);
        try {
            for (AlarmEntry entry : entries) {
                JSONObject json = entry.toJson();
                json.put("armed", AlarmScheduler.isArmed(context, entry.id));
                array.put(json);
            }
        } catch (JSONException e) {
            SpikeLog.w("一覧を JSON にできませんでした", e);
        }
        return array.toString();
    }

    // ---- ContentProvider の表の操作は使わない ----
    @Override
    public Cursor query(Uri uri, String[] projection, String selection, String[] selectionArgs, String sortOrder) {
        return null;
    }

    @Override
    public String getType(Uri uri) {
        return null;
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        return null;
    }

    @Override
    public int delete(Uri uri, String selection, String[] selectionArgs) {
        return 0;
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        return 0;
    }
}
