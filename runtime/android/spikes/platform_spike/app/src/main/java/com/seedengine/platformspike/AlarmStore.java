package com.seedengine.platformspike;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONException;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/**
 * 予約の控え（:seed_platform だけが読み書きする）。
 *
 * <p>置き場は<b>端末保護ストレージ</b>（createDeviceProtectedStorageContext）。再起動の後、最初のロック解除の前
 * （LOCKED_BOOT_COMPLETED）でも読めるようにするため（E-10 の材料）。書き込みは一時ファイル → fsync → rename の原子的な置き換え。</p>
 *
 * <p>同期は static synchronized（プロセス内だけ）。書き手を :seed_platform の 1 プロセスに限る設計なので足りる。</p>
 */
final class AlarmStore {
    private static final String DIR_NAME = "seed_platform";
    private static final String FILE_NAME = "alarms.json";
    private static final String TMP_SUFFIX = ".tmp";

    private AlarmStore() {}

    static synchronized List<AlarmEntry> load(Context context) {
        List<AlarmEntry> result = new ArrayList<>();
        File file = file(context);
        if (!file.exists()) {
            return result;
        }
        try (FileInputStream in = new FileInputStream(file)) {
            byte[] bytes = in.readAllBytes();
            JSONArray array = new JSONArray(new String(bytes, StandardCharsets.UTF_8));
            for (int i = 0; i < array.length(); i++) {
                result.add(AlarmEntry.fromJson(array.getJSONObject(i)));
            }
        } catch (IOException | JSONException e) {
            SpikeLog.w("予約の控えを読めませんでした（空として扱う）", e);
        }
        return result;
    }

    static synchronized void save(Context context, List<AlarmEntry> entries) {
        File file = file(context);
        File tmp = new File(file.getPath() + TMP_SUFFIX);
        try {
            JSONArray array = new JSONArray();
            for (AlarmEntry entry : entries) {
                array.put(entry.toJson());
            }
            try (FileOutputStream out = new FileOutputStream(tmp)) {
                out.write(array.toString().getBytes(StandardCharsets.UTF_8));
                out.getFD().sync();
            }
            if (!tmp.renameTo(file)) {
                throw new IOException("rename に失敗: " + tmp + " -> " + file);
            }
        } catch (IOException | JSONException e) {
            SpikeLog.w("予約の控えを書けませんでした", e);
        }
    }

    /** 同じ ID を置き換えて足す。 */
    static synchronized void put(Context context, AlarmEntry entry) {
        List<AlarmEntry> entries = load(context);
        entries.removeIf(existing -> existing.id.equals(entry.id));
        entries.add(entry);
        save(context, entries);
    }

    static synchronized void remove(Context context, String id) {
        List<AlarmEntry> entries = load(context);
        if (entries.removeIf(existing -> existing.id.equals(id))) {
            save(context, entries);
        }
    }

    static synchronized void clear(Context context) {
        save(context, new ArrayList<>());
    }

    private static File file(Context context) {
        Context deviceProtected = context.createDeviceProtectedStorageContext();
        File dir = new File(deviceProtected.getFilesDir(), DIR_NAME);
        if (!dir.isDirectory() && !dir.mkdirs()) {
            SpikeLog.i("控えのフォルダを作れませんでした: " + dir);
        }
        return new File(dir, FILE_NAME);
    }
}
