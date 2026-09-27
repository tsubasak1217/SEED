// ============================================================
//  RingSoundSource.java — 鳴動の音源の候補と、その読み込み（無音にしない落とし方。W1-4a）
//
//  上から順に試し、読めた最初のもので鳴らす（docs/app_platform_roadmap.md §2.2「鳴動の方針」の「音源が読めない」）:
//    ALARM_FILE      … 予約の sound_path（メインプロセスのエンジンが assets:// から端末保護ストレージの sounds/ へ書き出した
//                      ファイル・アプリが渡した端末のファイルの絶対パス）。空・読めなければ次へ
//    BUNDLED_DEFAULT … APK に同梱した既定の音（res/raw/seed_alarm_default.wav。runtime/android/tools/gen_alarm_default_tone.py
//                      で作る。APK の中なので再起動の後・ロック解除の前〈Direct Boot〉でも読める）
//    SYSTEM_ALARM    … 端末の既定のアラーム音（RingtoneManager.TYPE_ALARM。ロック解除の前は読めない見込み〈推論〉）
//  Flutter 版は読めない音源を無音で握りつぶしていた（例外を捨てる）。ここでは必ず次の候補へ進む。
// ============================================================

package com.seedengine.runtime.platform.service.alarm.ring;

import android.content.Context;
import android.content.res.AssetFileDescriptor;
import android.media.MediaPlayer;
import android.media.RingtoneManager;
import android.net.Uri;

import com.seedengine.runtime.R;
import com.seedengine.runtime.platform.service.alarm.AlarmEntry;

import java.io.File;
import java.io.IOException;

/**
 * 音源の候補（試す順に並べる）。
 */
enum RingSoundSource {

    /** 予約の音源のファイル。 */
    ALARM_FILE {
        @Override
        boolean applyTo(Context context, MediaPlayer player, AlarmEntry entry) throws IOException {
            if (entry.soundPath.isEmpty()) {
                return false;
            }
            File file = new File(entry.soundPath);
            if (!file.isFile() || !file.canRead()) {
                throw new IOException("音源のファイルを読めません: " + entry.soundPath);
            }
            player.setDataSource(entry.soundPath);
            return true;
        }
    },

    /** APK に同梱した既定の音。 */
    BUNDLED_DEFAULT {
        @Override
        boolean applyTo(Context context, MediaPlayer player, AlarmEntry entry) throws IOException {
            // res/raw の .wav は APK の中で圧縮されないので、ファイル記述子の一部として直接渡せる（W1-0 のスパイクと同じ形）
            try (AssetFileDescriptor descriptor = context.getResources().openRawResourceFd(R.raw.seed_alarm_default)) {
                if (descriptor == null) {
                    throw new IOException("既定の音（res/raw/seed_alarm_default）を開けません（圧縮されている？）");
                }
                player.setDataSource(descriptor.getFileDescriptor(), descriptor.getStartOffset(), descriptor.getLength());
            }
            return true;
        }
    },

    /** 端末の既定のアラーム音。 */
    SYSTEM_ALARM {
        @Override
        boolean applyTo(Context context, MediaPlayer player, AlarmEntry entry) throws IOException {
            Uri uri = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM);
            if (uri == null) {
                return false;
            }
            player.setDataSource(context, uri);
            return true;
        }
    };

    /**
     * この候補の音源を MediaPlayer に渡す（setDataSource まで。prepare は呼び出し側）。
     *
     * @param context :seed_platform の Context
     * @param player  まだ音源を持たない MediaPlayer
     * @param entry   鳴らす予約
     * @return 渡したら true。この候補が当てはまらない（予約に音源が無い等）なら false
     * @throws IOException 当てはまるが読めない
     */
    abstract boolean applyTo(Context context, MediaPlayer player, AlarmEntry entry) throws IOException;
}
