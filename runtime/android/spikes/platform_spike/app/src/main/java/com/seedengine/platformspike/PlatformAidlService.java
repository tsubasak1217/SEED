package com.seedengine.platformspike;

import android.app.Service;
import android.content.Intent;
import android.os.IBinder;

/** IPC の比較用（:seed_platform）: AIDL の同期呼び出し。受けた byte[] をそのまま返す。 */
public final class PlatformAidlService extends Service {
    private final IPlatformSpike.Stub binder = new IPlatformSpike.Stub() {
        @Override
        public byte[] invoke(String method, byte[] json) {
            return json;
        }
    };

    @Override
    public IBinder onBind(Intent intent) {
        return binder;
    }
}
