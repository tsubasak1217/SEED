package com.seedengine.platformspike;

import android.app.ForegroundServiceStartNotAllowedException;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.content.res.AssetFileDescriptor;
import android.media.AudioAttributes;
import android.media.MediaPlayer;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.IOException;

/**
 * 鳴動の前景サービス（:seed_platform・exported=false・directBootAware）。
 *
 * <ul>
 *   <li>startForeground（種類は extra で選ぶ。既定 mediaPlayback）→ 鳴動の通知（フルスクリーン通知 → RingEntry → RingActivity）</li>
 *   <li>MediaPlayer（USAGE_ALARM・ループ）。音声フォーカスは取らない＝失っても止まらない</li>
 *   <li>PARTIAL_WAKE_LOCK（安全弁＋余裕の時間切れ付き）</li>
 *   <li>安全弁（max_ring_s）で自動停止。最近のタスクから消されても止めない（onTaskRemoved は記録だけ）</li>
 * </ul>
 */
public final class RingService extends Service {
    private static final String WAKE_LOCK_TAG = "platformspike:ring";

    /** 同じプロセスの PlatformProvider から止めるための参照（UI スレッドでだけ触る）。 */
    private static volatile RingService instance;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private MediaPlayer player;
    private PowerManager.WakeLock wakeLock;
    private String ringingId;
    private long scheduledAt;
    private long ringStartWall;
    private String fgsType;
    private int heartbeatCount;

    private final Runnable heartbeat = new Runnable() {
        @Override
        public void run() {
            heartbeatCount++;
            SpikeLog.mark("ring_heartbeat", "id=" + ringingId + " n=" + heartbeatCount
                    + " playing=" + (player != null && player.isPlaying())
                    + " elapsed_ms=" + (System.currentTimeMillis() - ringStartWall));
            handler.postDelayed(this, SpikeContract.RING_HEARTBEAT_MS);
        }
    };

    private final Runnable safetyValve = () -> stopRinging("timeout");

    /** 背面（受信機）から前景サービスとして起こす。起こせなかった理由は目印に残す。 */
    static void startFromBackground(Context context, String origin, String id, long scheduledAt,
                                    String fgsType, int maxRingSeconds) {
        Intent intent = new Intent(context, RingService.class)
                .setAction(SpikeContract.ACTION_RING_START)
                .putExtra(SpikeContract.EXTRA_ID, id)
                .putExtra(SpikeContract.EXTRA_TRIGGER_AT, scheduledAt)
                .putExtra(SpikeContract.EXTRA_FGS_TYPE, fgsType)
                .putExtra(SpikeContract.EXTRA_MAX_RING_SECONDS, maxRingSeconds);
        try {
            context.startForegroundService(intent);
            SpikeLog.mark("fgs_start_requested", "origin=" + origin + " id=" + id);
        } catch (ForegroundServiceStartNotAllowedException e) {
            SpikeLog.mark("fgs_start_denied", "origin=" + origin + " where=startForegroundService error="
                    + SpikeLog.oneLine(e));
        } catch (RuntimeException e) {
            SpikeLog.mark("fgs_start_error", "origin=" + origin + " where=startForegroundService error="
                    + SpikeLog.oneLine(e));
        }
    }

    /** 同じプロセス（:seed_platform）から止める。鳴っていなければ false。 */
    static boolean stopFromSameProcess(String reason) {
        RingService service = instance;
        if (service == null) {
            return false;
        }
        new Handler(Looper.getMainLooper()).post(() -> service.stopRinging(reason));
        return true;
    }

    /** 鳴動の状態（JSON）。 */
    static String statusJson() {
        RingService service = instance;
        JSONObject json = new JSONObject();
        try {
            json.put("service_alive", service != null);
            if (service != null) {
                json.put("id", service.ringingId);
                json.put("scheduled_at", service.scheduledAt);
                json.put("ring_start_wall", service.ringStartWall);
                json.put("fgs_type", service.fgsType);
                json.put("playing", service.player != null && service.player.isPlaying());
            }
        } catch (JSONException e) {
            SpikeLog.w("状態を JSON にできませんでした", e);
        }
        return json.toString();
    }

    @Override
    public void onCreate() {
        super.onCreate();
        instance = this;
        SpikeLog.mark("service_create", DeviceState.describe(this));
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent != null ? intent.getAction() : null;
        if (SpikeContract.ACTION_RING_START.equals(action)) {
            startRinging(intent);
        } else if (SpikeContract.ACTION_RING_STOP.equals(action)) {
            stopRinging("intent");
        } else {
            SpikeLog.mark("service_unknown_start", "action=" + action);
            if (player == null) {
                stopSelf();
            }
        }
        // 殺されたら作り直さない（スパイクでは挙動を予測しやすくする。本番は要検討）。
        return START_NOT_STICKY;
    }

    private void startRinging(Intent intent) {
        String id = intent.getStringExtra(SpikeContract.EXTRA_ID);
        long scheduled = intent.getLongExtra(SpikeContract.EXTRA_TRIGGER_AT, -1);
        String type = intent.getStringExtra(SpikeContract.EXTRA_FGS_TYPE);
        if (type == null) {
            type = SpikeContract.FGS_TYPE_MEDIA_PLAYBACK;
        }
        int maxRing = intent.getIntExtra(SpikeContract.EXTRA_MAX_RING_SECONDS,
                SpikeContract.DEFAULT_MAX_RING_SECONDS);
        long startWall = System.currentTimeMillis();

        ensureChannel();
        Notification notification = buildNotification(id, scheduled, startWall);
        int typeFlag = fgsTypeFlag(type);
        // startForegroundService の後は必ず startForeground を呼ぶ（呼ばないとシステムがアプリを落とす）。
        try {
            startForeground(SpikeContract.NOTIFICATION_ID_RING, notification, typeFlag);
        } catch (ForegroundServiceStartNotAllowedException e) {
            SpikeLog.mark("fgs_start_failed", "type=" + type + " where=startForeground error=" + SpikeLog.oneLine(e));
            stopSelf();
            return;
        } catch (RuntimeException e) {
            // SecurityException（種類の権限・systemExempted の前提）や InvalidForegroundServiceTypeException など。
            SpikeLog.mark("fgs_start_failed", "type=" + type + " where=startForeground error=" + SpikeLog.oneLine(e));
            stopSelf();
            return;
        }
        SpikeLog.mark("fgs_started", "id=" + id + " type=" + type + " type_flag=" + typeFlag
                + " " + DeviceState.permissions(this));

        if (player != null) {
            // 既に鳴っている（重なり）。スパイクでは後から来た予約の情報だけ記録して鳴らし続ける。
            SpikeLog.mark("ring_overlap", "current=" + ringingId + " incoming=" + id);
            return;
        }
        ringingId = id;
        scheduledAt = scheduled;
        ringStartWall = startWall;
        fgsType = type;
        heartbeatCount = 0;
        acquireWakeLock(maxRing);
        if (!startAudio()) {
            // 音が出せなくても鳴動の通知・画面は続ける（本番は既定の音 → 端末の既定のアラーム音へ落とす）。
            SpikeLog.mark("ring_audio_failed", "id=" + id);
        }
        handler.postDelayed(safetyValve, maxRing * SpikeContract.MS_PER_SECOND);
        handler.postDelayed(heartbeat, SpikeContract.RING_HEARTBEAT_MS);
    }

    /** 音源（res/raw/quiet_tone.wav。音量を大きく下げた短いビープ）を USAGE_ALARM でループ再生する。 */
    private boolean startAudio() {
        AudioAttributes attributes = new AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_ALARM)
                .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                .build();
        MediaPlayer created = new MediaPlayer();
        try (AssetFileDescriptor fd = getResources().openRawResourceFd(R.raw.quiet_tone)) {
            created.setAudioAttributes(attributes);
            created.setDataSource(fd.getFileDescriptor(), fd.getStartOffset(), fd.getLength());
            created.setLooping(true);
            created.prepare();
            created.start();
        } catch (IOException | RuntimeException e) {
            SpikeLog.w("鳴動音を再生できませんでした", e);
            created.release();
            return false;
        }
        player = created;
        long now = System.currentTimeMillis();
        SpikeLog.mark("ring_start", "id=" + ringingId + " sched=" + scheduledAt
                + " since_sched_ms=" + (now - scheduledAt) + " usage=alarm fgs_type=" + fgsType
                + " " + DeviceState.describe(this));
        return true;
    }

    /** 止める（解除・安全弁・停止の命令）。音・WakeLock・通知・サービスをまとめて片付け、メインプロセスへ知らせる。 */
    void stopRinging(String reason) {
        handler.removeCallbacks(safetyValve);
        handler.removeCallbacks(heartbeat);
        long rangMs = ringStartWall > 0 ? System.currentTimeMillis() - ringStartWall : -1;
        if (player != null) {
            try {
                player.stop();
            } catch (IllegalStateException e) {
                SpikeLog.w("MediaPlayer.stop に失敗", e);
            }
            player.release();
            player = null;
        }
        releaseWakeLock();
        stopForeground(STOP_FOREGROUND_REMOVE);
        SpikeLog.mark("ring_stop", "reason=" + reason + " id=" + ringingId + " rang_ms=" + rangMs);
        // メインプロセス（鳴動画面）へ「止まった」を知らせる（自パッケージ宛て。受け手が居なければ何も起きない）。
        sendBroadcast(new Intent(SpikeContract.ACTION_RING_STOPPED)
                .setPackage(getPackageName())
                .putExtra(SpikeContract.EXTRA_ID, ringingId)
                .putExtra(SpikeContract.EXTRA_REASON, reason));
        ringingId = null;
        ringStartWall = 0;
        stopSelf();
    }

    @Override
    public void onTaskRemoved(Intent rootIntent) {
        // 最近のタスクから消された。止めない（アプリ仕様 P-7）。記録だけ。
        SpikeLog.mark("task_removed", "root=" + (rootIntent != null ? rootIntent.getComponent() : null)
                + " playing=" + (player != null && player.isPlaying()) + " id=" + ringingId);
        super.onTaskRemoved(rootIntent);
    }

    @Override
    public void onDestroy() {
        handler.removeCallbacks(safetyValve);
        handler.removeCallbacks(heartbeat);
        if (player != null) {
            player.release();
            player = null;
        }
        releaseWakeLock();
        instance = null;
        SpikeLog.mark("service_destroy", "");
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void acquireWakeLock(int maxRingSeconds) {
        PowerManager power = getSystemService(PowerManager.class);
        wakeLock = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, WAKE_LOCK_TAG);
        wakeLock.setReferenceCounted(false);
        wakeLock.acquire(maxRingSeconds * SpikeContract.MS_PER_SECOND + SpikeContract.WAKE_LOCK_MARGIN_MS);
    }

    private void releaseWakeLock() {
        if (wakeLock != null && wakeLock.isHeld()) {
            wakeLock.release();
        }
        wakeLock = null;
    }

    /** 鳴動の通知チャネル（重要度 HIGH・チャネルの音とバイブは無し＝音はこのサービスが鳴らす）。 */
    private void ensureChannel() {
        NotificationManager manager = getSystemService(NotificationManager.class);
        NotificationChannel channel = new NotificationChannel(SpikeContract.CHANNEL_RING,
                "鳴動（スパイク）", NotificationManager.IMPORTANCE_HIGH);
        channel.setSound(null, null);
        channel.enableVibration(false);
        channel.setLockscreenVisibility(Notification.VISIBILITY_PUBLIC);
        manager.createNotificationChannel(channel);
    }

    /** 鳴動の通知。フルスクリーン通知と本文のタップは、exported=false の別名 RingEntry を通って鳴動画面へ。 */
    private Notification buildNotification(String id, long scheduled, long startWall) {
        Intent ring = new Intent()
                .setComponent(new ComponentName(this, SpikeContract.RING_ENTRY_ALIAS))
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_NO_USER_ACTION)
                .putExtra(SpikeContract.EXTRA_ID, id)
                .putExtra(SpikeContract.EXTRA_TRIGGER_AT, scheduled)
                .putExtra(SpikeContract.EXTRA_RING_START_WALL, startWall);
        PendingIntent fullScreen = PendingIntent.getActivity(this, 0, ring,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, SpikeContract.CHANNEL_RING)
                .setSmallIcon(android.R.drawable.ic_lock_idle_alarm)
                .setContentTitle("PlatformSpike 鳴動中")
                .setContentText("id=" + id + "（止めるのは画面の「解除」だけ）")
                .setCategory(Notification.CATEGORY_ALARM)
                .setVisibility(Notification.VISIBILITY_PUBLIC)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setFullScreenIntent(fullScreen, true)
                .setContentIntent(fullScreen)
                .setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE)
                .build();
    }

    private static int fgsTypeFlag(String type) {
        switch (type) {
            case SpikeContract.FGS_TYPE_SYSTEM_EXEMPTED:
                return ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED;
            case SpikeContract.FGS_TYPE_SPECIAL_USE:
                return ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE;
            case SpikeContract.FGS_TYPE_MEDIA_PLAYBACK:
            default:
                return ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK;
        }
    }
}
