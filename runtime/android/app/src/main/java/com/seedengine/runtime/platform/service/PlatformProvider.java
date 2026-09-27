// ============================================================
//  PlatformProvider.java — :seed_platform プロセスの同期の命令の窓口（ContentProvider。W1-1）
//
//  【なぜ別プロセスか】鳴動（W1-4）はエンジン（Rust・C#）が落ちても・最近のタスクから消されても続けるため、
//  Java だけの別プロセス :seed_platform に置く（E-01。W1-0 の実機で、鳴動画面のタスクを消しても別プロセスの音は続いた）。
//  このプロバイダはその入口で、メインプロセスの PlatformConnection が ContentProviderClient.call で呼ぶ
//  （同期・Binder の往復 1 回。バインドの手続きが要らない。E-02）。
//
//  【呼ばれ方】call(method = "<module>.<method>", arg = 未使用, extras = { json: UTF-8 の byte[], callback: Binder（登録のときだけ） })
//  返答は Bundle { json: UTF-8 の byte[] }。中身は {"ok":true,…} / {"ok":false,"error":理由}。例外は投げない。
//  モジュールの表（下の buildModules）に行を足せば命令が増える。今の表: "platform"（CorePlatformModule）・
//  "alarm"（alarm/AlarmModule。W1-3。機能 alarm の無い APK では feature_not_enabled で断る）・
//  "notification"（notification/NotificationModule。W1-5。POST_NOTIFICATIONS の宣言の無い APK では feature_not_enabled）。
//  権限（"permission"）は Activity が要るのでここではなくメインプロセスが答える（platform/local/・platform/permission/）。
//
//  【マニフェスト】main の AndroidManifest.xml に exported=false・android:process=":seed_platform" で常設（W1-1）。
//  プロバイダのプロセスは最初の接続まで起動しないので、使わないゲームには影響しない。機能ごとの宣言の出し入れ（E-04）は W1-2。
//  このクラスは Java だけのプロセスで動く（libSEED.so を読み込まない。SeedPlatform・MainActivity を参照しないこと）。
//
//  【起動時の照合（W1-4a）】onCreate は :seed_platform のプロセスが起きた最初（受信機・サービスより前）に呼ばれるので、
//  ここで目覚ましの控えと AlarmManager の照合を背面のスレッドで始める（alarm/AlarmStartup。Android 10〜14 の強制停止の後の保険）。
//  【残した音量（W1-7）】call のたびに、鳴動の後始末で戻せずに残した force_volume の前の音量を戻してみる（AlarmStartup.onMainProcessCall。
//  Android 17 は背面のプロセスからの音量の変更を無視するので、アプリが前面に出て呼んできたときに戻る）。
// ============================================================

package com.seedengine.runtime.platform.service;

import android.app.Application;
import android.content.ContentProvider;
import android.content.ContentValues;
import android.database.Cursor;
import android.net.Uri;
import android.os.Binder;
import android.os.Bundle;
import android.os.Process;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.alarm.AlarmModule;
import com.seedengine.runtime.platform.service.alarm.AlarmStartup;
import com.seedengine.runtime.platform.service.notification.NotificationModule;

import org.json.JSONException;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * :seed_platform の命令の窓口（exported=false。同じアプリのプロセスからだけ呼べる）。
 */
public final class PlatformProvider extends ContentProvider {

    /** 表の操作（query 等）を使わないときの「変えた行の数」。 */
    private static final int NO_ROWS = 0;

    /** 区切りが見つからないときの位置（String.indexOf と同じ）。 */
    private static final int NOT_FOUND = -1;

    /** モジュールの名前 → モジュール（作った後は変えない。Binder のスレッドから同時に読む）。 */
    private final Map<String, PlatformModule> modules = buildModules();

    /**
     * モジュールの表を作る（機能を増やすときはここへ 1 行足す）。
     *
     * @return 変えられない表
     */
    private Map<String, PlatformModule> buildModules() {
        Map<String, PlatformModule> table = new LinkedHashMap<>();
        // version の返答に表の名前を出すため、表を後から読む形で渡す（表が出来上がった後に呼ばれる）
        register(table, new CorePlatformModule(() -> modules.keySet()));
        register(table, new AlarmModule());
        register(table, new NotificationModule());
        return Collections.unmodifiableMap(table);
    }

    /**
     * 表へ 1 つ足す（名前の重複は作り方の誤りなので止める）。
     *
     * @param table  表
     * @param module 足すモジュール
     */
    private static void register(Map<String, PlatformModule> table, PlatformModule module) {
        if (!PlatformContract.isValidName(module.name()) || table.containsKey(module.name())) {
            throw new IllegalStateException("モジュールの名前が不正か重複しています: " + module.name());
        }
        table.put(module.name(), module);
    }

    @Override
    public boolean onCreate() {
        Log.i(PlatformContract.LOG_TAG, "PlatformProvider を作りました（" + Application.getProcessName()
                + "・pid " + Process.myPid() + "・モジュール " + modules.keySet() + "）");
        // :seed_platform が起きた最初（受信機・サービスより前）。目覚ましの控えと AlarmManager を照合する（W1-4a。背面のスレッドで。
        // 配信でプロセスが起きたときの AlarmReceiver の startForegroundService を遅らせない。AlarmStartup）
        AlarmStartup.reconcileInBackground(getContext());
        return true;
    }

    @Override
    public Bundle call(String method, String arg, Bundle extras) {
        // 戻せずに残した鳴動の音量（force_volume の前の値）を、アプリが呼んできたこの機会に戻してみる（W1-7。背面の制限。AlarmStartup）
        AlarmStartup.onMainProcessCall(getContext());
        Bundle reply = new Bundle();
        reply.putByteArray(PlatformContract.BUNDLE_JSON, dispatch(method, extras));
        return reply;
    }

    /**
     * "<module>.<method>" をモジュールへ振り分ける（例外を投げない）。
     *
     * @param providerMethod call の method
     * @param extras         call の extras（null 可）
     * @return 返答の JSON（UTF-8）
     */
    private byte[] dispatch(String providerMethod, Bundle extras) {
        // exported=false なので同じ UID からしか届かないが、念のため確かめる（同じプロセスの中からの呼び出しも同じ UID）
        if (Binder.getCallingUid() != Process.myUid()) {
            return PlatformJson.errorReply(PlatformContract.ERROR_FORBIDDEN);
        }
        int separator = providerMethod != null ? providerMethod.indexOf(PlatformContract.METHOD_SEPARATOR) : NOT_FOUND;
        if (separator == NOT_FOUND) {
            return PlatformJson.errorReply(PlatformContract.ERROR_INVALID_NAME);
        }
        String moduleName = providerMethod.substring(0, separator);
        String methodName = providerMethod.substring(separator + PlatformContract.METHOD_SEPARATOR.length());
        if (!PlatformContract.isValidName(moduleName) || !PlatformContract.isValidName(methodName)) {
            return PlatformJson.errorReply(PlatformContract.ERROR_INVALID_NAME);
        }
        PlatformModule module = modules.get(moduleName);
        if (module == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_UNKNOWN_METHOD);
        }
        Object request;
        try {
            request = PlatformJson.parseRequest(extras != null ? extras.getByteArray(PlatformContract.BUNDLE_JSON) : null);
        } catch (JSONException e) {
            return PlatformJson.errorReply(PlatformContract.ERROR_INVALID_JSON, PlatformJson.describe(e));
        }
        try {
            return module.handle(getContext(), methodName, request, extras);
        } catch (RuntimeException e) {
            Log.e(PlatformContract.LOG_TAG, providerMethod + " の途中で例外", e);
            return PlatformJson.errorReply(PlatformContract.ERROR_INTERNAL, PlatformJson.describe(e));
        }
    }

    // ── ContentProvider の表の操作は使わない（call だけの窓口）──

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
        return NO_ROWS;
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        return NO_ROWS;
    }
}
