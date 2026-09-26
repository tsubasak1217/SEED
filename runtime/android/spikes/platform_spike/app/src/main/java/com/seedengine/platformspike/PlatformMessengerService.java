package com.seedengine.platformspike;

import android.app.Service;
import android.content.Intent;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.Message;
import android.os.Messenger;
import android.os.RemoteException;

/** IPC の比較用（:seed_platform）: Messenger。PING を受けたら replyTo へ PONG を返すだけ。 */
public final class PlatformMessengerService extends Service {
    static final int MSG_PING = 1;
    static final int MSG_PONG = 2;

    private HandlerThread thread;
    private Messenger messenger;

    @Override
    public void onCreate() {
        super.onCreate();
        thread = new HandlerThread("spike-messenger");
        thread.start();
        messenger = new Messenger(new Handler(thread.getLooper(), message -> {
            if (message.what == MSG_PING && message.replyTo != null) {
                Message reply = Message.obtain(null, MSG_PONG);
                reply.arg1 = message.arg1;
                try {
                    message.replyTo.send(reply);
                } catch (RemoteException e) {
                    SpikeLog.w("PONG を返せませんでした", e);
                }
            }
            return true;
        }));
    }

    @Override
    public IBinder onBind(Intent intent) {
        return messenger.getBinder();
    }

    @Override
    public void onDestroy() {
        thread.quitSafely();
        super.onDestroy();
    }
}
