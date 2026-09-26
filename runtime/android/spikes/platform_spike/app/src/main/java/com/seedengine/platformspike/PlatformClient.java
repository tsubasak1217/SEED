package com.seedengine.platformspike;

import android.content.Context;
import android.os.Bundle;

/**
 * メインプロセス側から :seed_platform の PlatformProvider を呼ぶ薄い入口（SeedPlatform.invoke の代役）。
 * 例外を投げず、失敗は {ok:false, error} の Bundle で返す。
 */
final class PlatformClient {
    private PlatformClient() {}

    static Bundle call(Context context, String method, Bundle extras) {
        return call(context, method, null, extras);
    }

    static Bundle call(Context context, String method, String arg, Bundle extras) {
        try {
            Bundle result = context.getContentResolver().call(SpikeContract.PROVIDER_URI, method, arg, extras);
            return result != null ? result : error("null_result");
        } catch (RuntimeException e) {
            return error(SpikeLog.oneLine(e));
        }
    }

    private static Bundle error(String message) {
        Bundle bundle = new Bundle();
        bundle.putBoolean(SpikeContract.R_OK, false);
        bundle.putString(SpikeContract.R_ERROR, message);
        return bundle;
    }
}
