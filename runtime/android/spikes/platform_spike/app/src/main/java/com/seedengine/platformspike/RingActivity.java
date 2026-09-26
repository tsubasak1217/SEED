package com.seedengine.platformspike;

import android.app.Activity;
import android.content.BroadcastReceiver;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Color;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewTreeObserver;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.window.OnBackInvokedDispatcher;

/**
 * 鳴動画面（メインプロセス。SEED の MainActivity＝GameActivity の代役）。
 *
 * <ul>
 *   <li>起動の Intent の component が exported=false の別名 RingEntry のとき（＝プラットフォーム層の PendingIntent から）だけ
 *       setShowWhenLocked / setTurnScreenOn を上げる（W1-P6 の試作。静的な宣言はしない＝アプリ仕様 §6.12 の教訓 2）</li>
 *   <li>経過秒数と大きな「解除」ボタン。解除は :seed_platform へ ContentResolver.call("ring.stop")</li>
 *   <li>戻るは無視する。鳴動が別の経路（安全弁・adb の STOP）で止まったら :seed_platform からの放送で閉じる</li>
 * </ul>
 * 計測の目印: ring_activity_create / ring_activity_first_frame（最初のフレームを提出）/ ring_activity_focus。
 */
public final class RingActivity extends Activity {
    private static final float ELAPSED_TEXT_SP = 40f;
    private static final float BUTTON_TEXT_SP = 48f;
    private static final int BUTTON_HEIGHT_DP = 200;
    private static final int PADDING_DP = 24;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private TextView elapsedView;
    private long ringStartWall;
    private String alarmId;
    private boolean trusted;
    private boolean focusLogged;
    private BroadcastReceiver stoppedReceiver;

    private final Runnable tick = new Runnable() {
        @Override
        public void run() {
            long seconds = (System.currentTimeMillis() - ringStartWall) / SpikeContract.MS_PER_SECOND;
            elapsedView.setText("鳴動から " + seconds + " 秒\nid=" + alarmId);
            handler.postDelayed(this, SpikeContract.UI_TICK_MS);
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Intent intent = getIntent();
        ComponentName component = intent.getComponent();
        trusted = component != null && SpikeContract.RING_ENTRY_ALIAS.equals(component.getClassName());
        if (trusted) {
            setShowWhenLocked(true);
            setTurnScreenOn(true);
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        }
        alarmId = intent.getStringExtra(SpikeContract.EXTRA_ID);
        ringStartWall = intent.getLongExtra(SpikeContract.EXTRA_RING_START_WALL, System.currentTimeMillis());
        SpikeLog.mark("ring_activity_create", "trusted=" + trusted + " component=" + component
                + " id=" + alarmId + " since_ring_ms=" + (System.currentTimeMillis() - ringStartWall)
                + " " + DeviceState.describe(this));
        setContentView(buildContent());
        watchFirstFrame();
        registerStoppedReceiver();
        // 戻るは無視する（アプリ仕様 §6.4）。targetSdk 36 の予測型の「戻る」でも効くように OnBackInvokedCallback で受ける。
        getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                OnBackInvokedDispatcher.PRIORITY_DEFAULT, () -> SpikeLog.mark("back_ignored", "id=" + alarmId));
    }

    @Override
    protected void onResume() {
        super.onResume();
        SpikeLog.mark("ring_activity_resume", "id=" + alarmId + " " + DeviceState.describe(this));
        handler.post(tick);
    }

    @Override
    protected void onPause() {
        handler.removeCallbacks(tick);
        super.onPause();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus && !focusLogged) {
            focusLogged = true;
            SpikeLog.mark("ring_activity_focus", "id=" + alarmId
                    + " since_ring_ms=" + (System.currentTimeMillis() - ringStartWall)
                    + " " + DeviceState.describe(this));
        }
    }

    @Override
    protected void onDestroy() {
        if (stoppedReceiver != null) {
            unregisterReceiver(stoppedReceiver);
        }
        handler.removeCallbacksAndMessages(null);
        SpikeLog.mark("ring_activity_destroy", "id=" + alarmId + " finishing=" + isFinishing());
        super.onDestroy();
    }

    /** 最初のフレームが提出された時刻を記録する（描画の直前にフレーム提出のコールバックを 1 回だけ登録）。 */
    private void watchFirstFrame() {
        View decor = getWindow().getDecorView();
        decor.getViewTreeObserver().addOnPreDrawListener(new ViewTreeObserver.OnPreDrawListener() {
            @Override
            public boolean onPreDraw() {
                decor.getViewTreeObserver().removeOnPreDrawListener(this);
                decor.getViewTreeObserver().registerFrameCommitCallback(() ->
                        SpikeLog.mark("ring_activity_first_frame", "id=" + alarmId
                                + " since_ring_ms=" + (System.currentTimeMillis() - ringStartWall)
                                + " " + DeviceState.describe(RingActivity.this)));
                return true;
            }
        });
    }

    /** :seed_platform が鳴動を止めた（安全弁・adb の STOP・解除）ら画面を閉じる。 */
    private void registerStoppedReceiver() {
        stoppedReceiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context context, Intent intent) {
                SpikeLog.mark("ring_activity_stopped_broadcast", "id=" + intent.getStringExtra(SpikeContract.EXTRA_ID)
                        + " reason=" + intent.getStringExtra(SpikeContract.EXTRA_REASON));
                finish();
            }
        };
        registerReceiver(stoppedReceiver, new IntentFilter(SpikeContract.ACTION_RING_STOPPED),
                Context.RECEIVER_NOT_EXPORTED);
    }

    private View buildContent() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setGravity(Gravity.CENTER);
        root.setBackgroundColor(Color.BLACK);
        int padding = dp(PADDING_DP);
        root.setPadding(padding, padding, padding, padding);

        elapsedView = new TextView(this);
        elapsedView.setTextColor(Color.WHITE);
        elapsedView.setTextSize(TypedValue.COMPLEX_UNIT_SP, ELAPSED_TEXT_SP);
        elapsedView.setGravity(Gravity.CENTER);
        root.addView(elapsedView, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        Button dismiss = new Button(this);
        dismiss.setText("解除");
        dismiss.setTextSize(TypedValue.COMPLEX_UNIT_SP, BUTTON_TEXT_SP);
        dismiss.setOnClickListener(view -> dismiss());
        LinearLayout.LayoutParams buttonParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(BUTTON_HEIGHT_DP));
        buttonParams.topMargin = padding;
        root.addView(dismiss, buttonParams);
        return root;
    }

    /** 解除: :seed_platform へ停止を命じて閉じる（Binder の往復は UI スレッドを塞がないよう別スレッドで）。 */
    private void dismiss() {
        SpikeLog.mark("dismiss_tap", "id=" + alarmId);
        Context app = getApplicationContext();
        new Thread(() -> {
            Bundle result = PlatformClient.call(app, SpikeContract.M_RING_STOP, null);
            SpikeLog.mark("dismiss_result", "ok=" + result.getBoolean(SpikeContract.R_OK)
                    + " error=" + result.getString(SpikeContract.R_ERROR));
            runOnUiThread(this::finish);
        }, "spike-dismiss").start();
    }

    private int dp(int value) {
        return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value,
                getResources().getDisplayMetrics()));
    }
}
