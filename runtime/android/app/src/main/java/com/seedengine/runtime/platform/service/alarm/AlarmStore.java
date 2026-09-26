// ============================================================
//  AlarmStore.java — 目覚ましの予約の控え（端末保護ストレージの seed_platform/alarms.json。W1-3）
//
//  【役割】予約の一覧をファイルへ読み書きするだけ（AlarmManager には触らない。組み合わせは AlarmBook）。
//  控えが正本で、AlarmManager の予約は「控えから張ったもの」。電源断・強制停止・権限の取り消しで AlarmManager の予約が
//  消えても、控えから張り直せる（BootReceiver）。発火したら控えから消す（一回限り。AlarmReceiver）。
//
//  【置き場】端末保護ストレージ（PlatformStorage）。再起動の後、最初のロック解除の前（LOCKED_BOOT_COMPLETED）でも読める。
//  【書き方】一時ファイル → fsync → rename → フォルダの fsync（DurableFile）。途中で落ちても前の中身か新しい中身のどちらか。
//  【読み方】無ければ空。壊れていれば空として扱い警告する（行ごとに壊れていればその行だけ捨てる）。
//  【形】{"format_version":1,"alarms":[AlarmEntry.toJson(), …]}
//  書き手は :seed_platform の 1 プロセスだけ。同時の読み書きは AlarmBook の lock とこのクラスの lock で直列にする。
// ============================================================

package com.seedengine.runtime.platform.service.alarm;

import android.content.Context;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.service.DurableFile;
import com.seedengine.runtime.platform.service.PlatformStorage;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;

/**
 * 予約の控えの読み書き（static のみ）。
 */
final class AlarmStore {

    private AlarmStore() {
    }

    /** 控えのファイルの書式の版（形を変えたら上げる）。 */
    private static final int FILE_FORMAT_VERSION = 1;

    /** 控えのファイルの欄: 書式の版。 */
    private static final String FILE_KEY_FORMAT_VERSION = "format_version";

    /**
     * 控えを読む（無ければ空。壊れていれば空として扱い警告する）。
     *
     * @param context どの Context でもよい
     * @return 予約の一覧（書き換えてよい新しいリスト）
     */
    static synchronized List<AlarmEntry> load(Context context) {
        List<AlarmEntry> entries = new ArrayList<>();
        byte[] bytes;
        try {
            bytes = DurableFile.readOrNull(PlatformStorage.alarmsFile(context));
        } catch (IOException e) {
            Log.w(PlatformContract.LOG_TAG, "予約の控えを読めませんでした（空として扱います）: " + PlatformJson.describe(e));
            return entries;
        }
        if (bytes == null) {
            return entries;
        }
        try {
            JSONObject root = new JSONObject(PlatformJson.text(bytes));
            int version = root.optInt(FILE_KEY_FORMAT_VERSION, 0);
            if (version != FILE_FORMAT_VERSION) {
                Log.w(PlatformContract.LOG_TAG, "予約の控えの版が違うので読みません（" + version + "）。空として扱います");
                return entries;
            }
            JSONArray rows = root.optJSONArray(PlatformContract.KEY_ALARMS);
            int count = rows != null ? rows.length() : 0;
            for (int i = 0; i < count; i++) {
                JSONObject row = rows.optJSONObject(i);
                try {
                    if (row == null) {
                        throw new JSONException("オブジェクトではありません");
                    }
                    entries.add(AlarmEntry.fromStoredJson(row));
                } catch (JSONException e) {
                    Log.w(PlatformContract.LOG_TAG, "予約の控えの " + i + " 行目が壊れていたので捨てます: " + PlatformJson.describe(e));
                }
            }
        } catch (JSONException e) {
            Log.w(PlatformContract.LOG_TAG, "予約の控えが壊れていたので空として扱います: " + PlatformJson.describe(e));
            entries.clear();
        }
        return entries;
    }

    /**
     * 控えを原子的に書き直す。
     *
     * @param context どの Context でもよい
     * @param entries 予約の一覧（この順で書く）
     * @return 書けたら true（失敗は警告を残して false。呼び出し側が予約を張らない・戻す）
     */
    static synchronized boolean save(Context context, List<AlarmEntry> entries) {
        JSONArray rows = new JSONArray();
        for (AlarmEntry entry : entries) {
            rows.put(entry.toJson());
        }
        JSONObject root = new JSONObject();
        PlatformJson.put(root, FILE_KEY_FORMAT_VERSION, FILE_FORMAT_VERSION);
        PlatformJson.put(root, PlatformContract.KEY_ALARMS, rows);
        try {
            DurableFile.writeAtomically(PlatformStorage.alarmsFile(context), PlatformJson.utf8(root.toString()));
            return true;
        } catch (IOException e) {
            Log.w(PlatformContract.LOG_TAG, "予約の控えを書けませんでした: " + PlatformJson.describe(e));
            return false;
        }
    }
}
