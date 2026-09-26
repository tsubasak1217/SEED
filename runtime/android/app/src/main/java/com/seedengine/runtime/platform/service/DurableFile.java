// ============================================================
//  DurableFile.java — 電源断でも壊れない書き方（一時ファイル → fsync → rename → フォルダの fsync。W1-3）
//
//  目覚ましの予約の控え（alarms.json）とイベントの記録（journal.json）は、書いている途中で電源が落ちたり
//  プロセスが殺されたりしても「前の中身」か「新しい中身」のどちらかが必ず残らなければならない
//  （半端なファイルを読むと予約が全部消える。docs/app_platform_roadmap.md §2.7 と同じ考え）。そこで:
//    1. 同じフォルダの <名前>.tmp へ全部書く
//    2. FileDescriptor.sync()（fsync）で中身を記憶装置まで届ける
//    3. rename で本体へ置き換える（同じファイルシステムの中の rename(2) は原子的で、既存の宛先を置き換える）
//    4. フォルダを fsync して、rename（フォルダの項目の書き換え）そのものも届ける（失敗しても続ける）
//  呼び出し側が自分の lock の中で呼ぶこと（同じファイルへ同時に書かない前提。書き手は :seed_platform だけ）。
// ============================================================

package com.seedengine.runtime.platform.service;

import android.system.ErrnoException;
import android.system.Os;
import android.system.OsConstants;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;

import java.io.File;
import java.io.FileDescriptor;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.file.Files;

/**
 * 原子的な置き換えの書き込みと読み込み（static のみ）。
 */
public final class DurableFile {

    private DurableFile() {
    }

    /** 一時ファイルの接尾辞（本体と同じフォルダに置く＝同じファイルシステムなので rename が原子的）。 */
    private static final String TEMP_SUFFIX = ".tmp";

    /** フォルダを開くときの mode（O_CREAT を使わないので意味を持たない。0 を渡す）。 */
    private static final int OPEN_MODE_UNUSED = 0;

    /**
     * ファイルを全部読む（無ければ null）。
     *
     * <p>InputStream.readAllBytes は API 33 からなので使わない（minSdk 29。W1-0 のスパイクの AlarmStore は使っていて、
     * Android 10〜12 では NoSuchMethodError になるところだった）。Files.readAllBytes は API 26 から。</p>
     *
     * @param file 読むファイル
     * @return 中身。無ければ null
     * @throws IOException 読めない
     */
    public static byte[] readOrNull(File file) throws IOException {
        if (!file.isFile()) {
            return null;
        }
        return Files.readAllBytes(file.toPath());
    }

    /**
     * 中身を原子的に置き換える（一時ファイル → fsync → rename → フォルダの fsync）。
     *
     * @param file 本体のファイル
     * @param data 新しい中身
     * @throws IOException 一時ファイルへ書けない・fsync できない・rename できない（本体は前の中身のまま）
     */
    public static void writeAtomically(File file, byte[] data) throws IOException {
        File temp = new File(file.getPath() + TEMP_SUFFIX);
        try (FileOutputStream out = new FileOutputStream(temp)) {
            out.write(data);
            out.flush();
            // fsync: rename の前に中身を記憶装置へ（先に rename が届き中身が届かないと、空のファイルが残りうる）
            out.getFD().sync();
        }
        if (!temp.renameTo(file)) {
            // 一時ファイルは次の書き込みで上書きされるので消さなくても害は無いが、残さない
            if (!temp.delete()) {
                Log.w(PlatformContract.LOG_TAG, "一時ファイルを消せませんでした: " + temp);
            }
            throw new IOException("rename に失敗しました: " + temp + " -> " + file);
        }
        syncDirectory(file.getParentFile());
    }

    /**
     * フォルダを fsync する（rename の結果を記憶装置へ。できなくても続ける＝中身の置き換えそのものは済んでいる）。
     *
     * @param dir フォルダ（null なら何もしない）
     */
    private static void syncDirectory(File dir) {
        if (dir == null) {
            return;
        }
        FileDescriptor fd = null;
        try {
            fd = Os.open(dir.getPath(), OsConstants.O_RDONLY, OPEN_MODE_UNUSED);
            Os.fsync(fd);
        } catch (ErrnoException e) {
            Log.w(PlatformContract.LOG_TAG, "フォルダを fsync できませんでした（続けます）: " + dir + "（" + e.getMessage() + "）");
        } finally {
            if (fd != null) {
                try {
                    Os.close(fd);
                } catch (ErrnoException e) {
                    Log.w(PlatformContract.LOG_TAG, "フォルダを閉じられませんでした: " + dir + "（" + e.getMessage() + "）");
                }
            }
        }
    }
}
