package com.seedengine.platformspike;

import android.app.Activity;
import android.os.Bundle;
import android.widget.TextView;

/**
 * 普通の起動（ランチャー）。起動のたびに :seed_platform へ「全予約の張り直し」を命じ、状態を表示する。
 * 強制停止（予約が消える）の後、次の起動で戻るかを確かめるための代役（本番はエンジンの起動時に同じことをする）。
 */
public final class MainActivity extends Activity {
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        TextView view = new TextView(this);
        view.setText("PlatformSpike（W1-0）: 状態を取得中…");
        setContentView(view);
        SpikeLog.mark("main_activity_create", DeviceState.describe(this));
        new Thread(() -> {
            Bundle rearm = PlatformClient.call(getApplicationContext(), SpikeContract.M_ALARM_REARM_ALL,
                    "app_launch", null);
            Bundle perm = PlatformClient.call(getApplicationContext(), SpikeContract.M_PERM_STATUS, null);
            String text = "rearm: " + rearm.getString(SpikeContract.R_JSON) + " / " + rearm.getString(SpikeContract.R_ERROR)
                    + "\n\nperm: " + perm.getString(SpikeContract.R_JSON);
            SpikeLog.mark("main_activity_rearm", "ok=" + rearm.getBoolean(SpikeContract.R_OK)
                    + " json=" + rearm.getString(SpikeContract.R_JSON));
            runOnUiThread(() -> view.setText(text));
        }, "spike-main-rearm").start();
    }
}
